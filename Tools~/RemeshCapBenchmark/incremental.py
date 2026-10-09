"""Transactional audit/re-analysis of supplied planar Cap proposals, offline only.

This is not the P3 missing-feature solver: callers supply added vertices/faces.
An accepted step is locally compatible, not a certified intended surface/solid.
"""
from collections import defaultdict
from dataclasses import dataclass

import numpy as np

from boundaries import checked_arrays, extract
from intersections import BoundsTree, exact_positions, triangle_intersection
from planes import Options as PlaneOptions, analyze, fit_plane


@dataclass(frozen=True)
class Limits:
    max_faces: int = 8192
    max_vertices: int = 16384
    max_pair_tests: int = 50_000

    def validate(self):
        if any(type(v) is not int or v < 1 for v in vars(self).values()):
            raise ValueError('Incremental limits must be positive integers')


class Refused(Exception):
    pass


def continuous_arc(boundary, source_hash, halfedges):
    """Validate an ordered, unbroken source-fan path in one fresh boundary cycle."""
    if not boundary['extractionAccepted'] or source_hash != boundary['sourceHash']:
        raise ValueError('Stale or refused boundary snapshot')
    if len(halfedges) < 2 or any(type(h) is not int for h in halfedges) or len(set(halfedges)) != len(halfedges):
        raise ValueError('Arc needs at least two distinct integer halfedges')
    lookup = {h['halfedge']: (loop, h) for loop in boundary['loops'] for h in loop['halfedges']}
    if any(h not in lookup for h in halfedges):
        raise ValueError('Arc contains a non-boundary occurrence')
    loop = lookup[halfedges[0]][0]
    if loop['requiresJunctionResolution']:
        raise ValueError('Arc requires junction resolution')
    arc = [lookup[h][1] for h in halfedges]
    if any(lookup[h][0]['id'] != loop['id'] for h in halfedges):
        raise ValueError('Arc crosses boundary cycles')
    for first, second in zip(arc, arc[1:]):
        if (first['successor'] != second['halfedge'] or first['slots'][1] != second['slots'][0]
                or first['sourceFans'][1] != second['sourceFans'][0]):
            raise ValueError('Arc is discontinuous or crosses source fans')
    return arc


def plane_summary(report):
    return {'analysisComplete': report['analysisComplete'],
            'sourceHash': report['sourceHash'],
            'loops': [{k: loop[k] for k in ('loopId', 'edgeCount', 'status', 'hypothesisCount') if k in loop}
                      for loop in report['loops']], 'refusal': report.get('refusal')}


def audit_new_pairs(points, faces, old_count, limits, cancel):
    """Exact new/old and new/new tests, including improper adjacent overlaps.

    Old/old geometry is unchanged and is deliberately not re-certified here.
    """
    triangles = points[faces].astype('d')
    tree = BoundsTree(triangles)
    exact, scale = exact_positions(points)
    exact_faces = [tuple(exact[int(v)] for v in face) for face in faces]
    tests, hits = 0, []
    for first in range(old_count, len(faces)):
        if cancel is not None and cancel():
            raise Refused('cancelled')
        triangle = triangles[first]
        for second in sorted(map(int, tree.query(triangle.min(axis=0), triangle.max(axis=0)))):
            if second >= first:
                continue
            if cancel is not None and cancel():
                raise Refused('cancelled')
            if tests >= limits.max_pair_tests:
                raise Refused('pair_test_budget_exceeded')
            tests += 1
            hit = triangle_intersection(exact_faces[first], exact_faces[second])
            if hit is not None:
                hits.append({'newFace': first, 'otherFace': second,
                             'role': 'new-old' if second < old_count else 'new-new',
                             'kind': hit.kind, 'points': [[float(v / scale) for v in p] for p in hit.points]})
    return {'pairTests': tests, 'hits': hits, 'geometryAccepted': not hits}


def attached_patch(points, faces, old_count, support):
    # Every added face must connect over added-face edges to the selected arc.
    _, slots = np.unique(points, axis=0, return_inverse=True)
    welded = slots[faces]
    edges = defaultdict(list)
    for face in range(old_count, len(faces)):
        triangle = welded[face]
        for a, b in zip(triangle, np.roll(triangle, -1)):
            edges[tuple(sorted((int(a), int(b))))].append(face)
    roots = set()
    for h in support:
        roots.update(edges[tuple(sorted(int(slots[v]) for v in h['sourceVertices']))])
    adjacent = defaultdict(set)
    for incident in edges.values():
        if len(incident) == 2:
            a, b = incident
            adjacent[a].add(b)
            adjacent[b].add(a)
    visited, pending = set(roots), list(roots)
    while pending:
        for neighbor in adjacent[pending.pop()] - visited:
            visited.add(neighbor)
            pending.append(neighbor)
    return len(visited) == len(faces) - old_count


def attempt(positions, triangles, source_hash, support_halfedges, added_positions, added_faces,
            plane_options=PlaneOptions(), limits=Limits(), cancel=None):
    """Return new arrays only after every gate and fresh plane analysis passes.

    Rejection returns the original arrays unchanged. Proposals append only and
    use the current snapshot's vertex numbering. Previous accepted Caps belong
    to the protected old prefix on the next attempt.
    """
    p, ix = checked_arrays(positions, triangles)
    extra, caps = np.asarray(added_positions), np.asarray(added_faces)
    plane_options.validate()
    limits.validate()
    if extra.ndim != 2 or extra.shape[1] != 3 or extra.dtype != p.dtype or caps.dtype != ix.dtype:
        raise ValueError('Proposal arrays must retain source position/index dtypes')
    if not len(caps):
        raise ValueError('Proposal needs added faces')
    report = {'schemaVersion': 1, 'accepted': False, 'sourceHash': source_hash,
              'supportHalfedges': list(support_halfedges), 'solidCertified': False,
              'intendedShapeCertified': False}

    def check_cancel():
        if cancel is not None and cancel():
            raise Refused('cancelled')

    try:
        check_cancel()
        if len(ix) + len(caps) > limits.max_faces or len(p) + len(extra) > limits.max_vertices:
            raise Refused('mesh_budget_exceeded')
        before = extract(p, ix, cancel=cancel)
        if not before['extractionAccepted']:
            raise Refused('source_boundary_' + before['refusal'])
        try:
            support = continuous_arc(before, source_hash, support_halfedges)
        except ValueError as error:
            raise Refused(str(error)) from error
        candidate_p = np.concatenate((p, extra))
        candidate_ix = np.concatenate((ix, caps))
        checked_arrays(candidate_p, candidate_ix)
        if set(range(len(p), len(candidate_p))) - set(map(int, caps.ravel())):
            raise Refused('unused_added_vertices')

        vertices = [h['sourceVertices'][0] for h in support] + [support[-1]['sourceVertices'][1]]
        closed = support[-1]['successor'] == support[0]['halfedge']
        q = p[vertices[:-1] if closed else vertices].astype('d')
        plane = fit_plane(q, closed, plane_options.rank_ratio)
        if not plane['rankSupported']:
            raise Refused('underdetermined_patch_plane')
        diagonal = float(np.linalg.norm(q.max(axis=0) - q.min(axis=0)))
        tolerance = max(plane_options.absolute_tolerance, plane_options.relative_tolerance * diagonal)
        if not np.isfinite(tolerance):
            raise ValueError('Patch tolerance exceeds float64 analysis')
        tolerance += float(diagonal * np.finfo('d').eps * 64)
        patch_points = candidate_p[np.unique(caps)].astype('d')
        residual = np.abs((patch_points - plane['origin']) @ np.asarray(plane['normal']))
        report['patchPlane'] = dict(plane, tolerance=tolerance, maxPatchDistance=float(residual.max()))
        if plane['maxDistance'] > tolerance or np.any(residual > tolerance):
            raise Refused('patch_not_on_support_plane')

        after = extract(candidate_p, candidate_ix, cancel=cancel)
        if not after['extractionAccepted']:
            raise Refused('candidate_topology_' + after['refusal'])
        old_boundary = {h['halfedge'] for loop in before['loops'] for h in loop['halfedges']}
        preserved = {h['halfedge'] for loop in after['loops'] for h in loop['halfedges'] if h['face'] < len(ix)}
        if preserved != old_boundary - set(support_halfedges):
            raise Refused('selected_arc_coverage_or_unselected_boundary_changed')
        old_fans = {tuple(v['position']): len(v['fans']) for v in before['singularVertices']}
        if any(len(v['fans']) > old_fans.get(tuple(v['position']), 1) for v in after['singularVertices']):
            raise Refused('new_disconnected_source_fans')
        if not attached_patch(candidate_p, candidate_ix, len(ix), support):
            raise Refused('detached_added_faces')
        check_cancel()
        geometry = audit_new_pairs(candidate_p, candidate_ix, len(ix), limits, cancel)
        report['geometry'] = geometry
        if not geometry['geometryAccepted']:
            raise Refused('new_intersections_or_contacts')

        # Do not carry snapshot-bound loop IDs into the changed geometry.
        # Preserve untouched boundary occurrences, but re-extract all hypotheses.
        remaining = analyze(candidate_p, candidate_ix, plane_options, cancel=cancel)
        report['remainingPlanes'] = plane_summary(remaining)
        if not remaining['analysisComplete']:
            raise Refused('reanalysis_' + remaining['refusal'])
        check_cancel()
        report.update({'accepted': True, 'resultHash': after['sourceHash'],
                       'addedVertices': len(extra), 'addedFaces': len(caps),
                       'boundaryEdgesBefore': before['boundaryEdges'], 'boundaryEdgesAfter': after['boundaryEdges']})
        return candidate_p, candidate_ix, report
    except Refused as error:
        report['refusal'] = str(error)
        return p, ix, report
