"""Controlled reconnection of two geometric vertex fans through Cap faces only.

This is an offline candidate operation. It preserves the source face prefix and
all positions. Every changed triangle is checked in 3D, not in a projected chart.
Final whole-mesh geometry/topology acceptance belongs to the caller's audit.
"""
import itertools

import numpy as np

from intersections import BoundsTree, cross, exact_positions, subtract, triangle_intersection
from topology import inspect


def vertex_fans(indices, vertex):
    incident = np.flatnonzero(np.any(indices == vertex, axis=1))
    links = {int(f): set(map(int, indices[f]))-{vertex} for f in incident}
    remaining = set(links)
    groups = []
    while remaining:
        group = {min(remaining)}
        todo = list(group)
        while todo:
            face = todo.pop()
            neighbours = sorted(g for g in remaining-group if links[face] & links[g])
            group.update(neighbours)
            todo.extend(neighbours)
        remaining -= group
        groups.append(sorted(group))
    return groups


def connection(indices, first, second, vertex):
    """Replace two oriented Cap triangles with a four-triangle connection."""
    oriented = []
    for face in (first, second):
        triangle = list(map(int, indices[face]))
        start = triangle.index(vertex)
        oriented.append(triangle[start:]+triangle[:start])
    _, a, b = oriented[0]
    _, c, d = oriented[1]
    return np.array([[vertex, a, d], [vertex, c, b], [a, b, d], [c, d, b]], dtype='u4')


def patch_intersections(points, indices, patch, removed, exact, tree):
    triangles = [tuple(exact[int(v)] for v in face) for face in patch]
    for triangle in triangles:
        if cross(subtract(triangle[1], triangle[0]), subtract(triangle[2], triangle[0])) == (0, 0, 0):
            return [{'kind': 'degenerate_patch'}]
    hits = []
    for first, triangle in enumerate(triangles):
        q = points[patch[first]].astype('d')
        for face in tree.query(q.min(axis=0), q.max(axis=0)):
            face = int(face)
            if face in removed:
                continue
            other = tuple(exact[int(v)] for v in indices[face])
            hit = triangle_intersection(triangle, other)
            if hit:
                hits.append({'patchFace': first, 'otherFace': face, 'kind': hit.kind})
        for second in range(first):
            hit = triangle_intersection(triangle, triangles[second])
            if hit:
                hits.append({'patchFace': first, 'otherPatchFace': second, 'kind': hit.kind})
    return hits


def reconnect(points, indices, source_count, max_pairs=256):
    if not 0 <= source_count <= len(indices) or max_pairs < 1:
        raise ValueError('Invalid source prefix or connection budget')
    if len(np.unique(points, axis=0)) != len(points):
        raise ValueError('Connection requires geometric vertex slots, not attribute-split indices')
    candidate = indices.copy()
    baseline = inspect(points, candidate)
    report = {'changed': False, 'connections': [], 'attempts': [], 'budgetExhausted': False}
    keys = ('boundaryEdges', 'duplicateFaces', 'degenerateFaces', 'nonManifoldEdges', 'windingEdges')
    if any(baseline[k] for k in keys):
        report['refusal'] = 'Connection requires closed oriented edges and nondegenerate unique faces'
        return candidate, report
    exact, _ = exact_positions(points)
    for location in baseline['disconnectedFanPositions']:
        vertex = int(np.flatnonzero(np.all(points == location, axis=1))[0])
        current = inspect(points, candidate)
        fans = vertex_fans(candidate, vertex)
        if len(fans) == 1:
            continue
        if len(fans) != 2:
            report['attempts'].append({'vertex': vertex, 'refusal': 'Expected exactly two vertex fans'})
            continue
        choices = [[face for face in fan if face >= source_count] for fan in fans]
        if any(not faces for faces in choices):
            report['attempts'].append({'vertex': vertex, 'refusal': 'Both fans need an editable Cap face'})
            continue
        tree = BoundsTree(points[candidate].astype('d'))
        for first, second in itertools.product(*choices):
            if len(report['attempts']) >= max_pairs:
                report['budgetExhausted'] = True
                break
            patch = connection(candidate, first, second, vertex)
            changed = np.concatenate((np.delete(candidate, [first, second], axis=0), patch))
            after = inspect(points, changed)
            attempt = {'vertex': vertex, 'removedCapFaces': [first, second]}
            if any(after[k] for k in keys) or after['disconnectedVertexFans'] >= current['disconnectedVertexFans']:
                attempt['refusal'] = 'Connection does not improve geometric manifold topology'
            else:
                hits = patch_intersections(points, candidate, patch, {first, second}, exact, tree)
                if hits:
                    attempt['refusal'] = 'New patch intersects unchanged geometry or itself'
                    attempt['hits'] = hits
                else:
                    attempt['patch'] = patch.tolist()
                    report['connections'].append(dict(attempt))
                    candidate = changed
                    report['changed'] = True
            report['attempts'].append(attempt)
            if 'refusal' not in attempt:
                break
    if not np.array_equal(indices[:source_count], candidate[:source_count]):
        raise ValueError('Connection changed source faces')
    report['topology'] = inspect(points, candidate)
    return candidate, report
