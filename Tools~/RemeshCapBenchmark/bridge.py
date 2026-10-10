"""Bounded annular Bridge for two explicitly selected snapshot-bound rims.

Offline geometry only. Enumerates cyclic seams and k-best monotone zippers;
source borders are never moved, split or resampled. Closure type is caller intent.
"""
import argparse
from dataclasses import asdict, dataclass, fields
import json
from pathlib import Path

import numpy as np

from analyze import read_mesh
from boundaries import checked_arrays, extract, select_domains
from surface_signature import signature
from collar_growth import evaluate as growth_evidence
from incremental import Limits, Refused, audit_new_pairs, plane_summary
from planes import Options as PlaneOptions, analyze


@dataclass(frozen=True)
class Options:
    max_loop_edges: int = 64
    max_phases: int = 64
    paths_per_phase: int = 2
    max_dp_states: int = 100_000
    max_candidates: int = 128
    max_pair_tests: int = 200_000
    min_area_ratio: float = 1e-12

    def validate(self):
        for key, value in vars(self).items():
            if key != 'min_area_ratio' and (type(value) is not int or value < 1):
                raise ValueError('Bridge budgets must be positive integers')
        if self.paths_per_phase > 8 or not np.isfinite(self.min_area_ratio) or not 0 < self.min_area_ratio < 1:
            raise ValueError('Invalid Bridge path count or triangle conditioning threshold')


class Work:
    def __init__(self, options, cancel):
        self.options, self.cancel = options, cancel
        self.dp_states, self.candidates, self.pair_tests = 0, 0, 0

    def check(self):
        if self.cancel is not None and self.cancel():
            raise Refused('cancelled')

    def step(self, field):
        self.check()
        if getattr(self, field) >= getattr(self.options, 'max_' + field):
            raise Refused(field + '_budget_exceeded')
        setattr(self, field, getattr(self, field) + 1)

    def summary(self):
        return {name: getattr(self, name) for name in ('dp_states', 'candidates', 'pair_tests')}


def selected_rims(boundary, source_hash, loop_ids):
    if not boundary['extractionAccepted']:
        raise Refused('source_boundary_' + boundary['refusal'])
    if len(loop_ids) != 2:
        raise ValueError('Bridge requires exactly two explicitly selected loops')
    selection = select_domains(boundary, loop_ids, source_hash)
    lookup = {loop['id']: loop for loop in boundary['loops']}
    loops = [lookup[identity] for identity in selection['loopIds']]
    if boundary['singularVertices']:
        raise Refused('source_requires_junction_resolution')
    if any(len(loop['slots']) < 3 or len(set(loop['slots'])) != len(loop['slots']) for loop in loops):
        raise Refused('non_simple_selected_rim')
    if set(loops[0]['slots']) & set(loops[1]['slots']):
        raise Refused('touching_selected_rims')
    return selection, loops


def triangle_cost(points, face, collar_normal, min_area_ratio):
    a, b, c = points[list(face)]
    edges = (b - a, c - b, a - c)
    normal = np.cross(b - a, c - a)
    area = float(np.linalg.norm(normal))
    if area <= min_area_ratio:
        return None
    shape = sum(float(edge @ edge) for edge in edges) / area
    # Explicit geometric score, not an acceptance confidence or shape certificate.
    continuation = 1 - float(np.clip((normal / area) @ collar_normal, -1, 1))
    return shape + 2 * continuation


def zipper_faces(a, b, moves):
    i, j, faces = 0, 0, []
    for move in moves:
        if move == 'A':
            faces.append((a[i % len(a)], b[j % len(b)], a[(i + 1) % len(a)]))
            i += 1
        else:
            faces.append((a[i % len(a)], b[j % len(b)], b[(j + 1) % len(b)]))
            j += 1
    return faces


def zipper_paths(points, a, b, normals_a, normals_b, work):
    """Restricted seam profile: start on A, end on B, no repeated cross-link.

    This is a bounded candidate family, not exhaustive annulus triangulation.
    B traverses against source winding; each triangle opposes its source edge.
    """
    m, n = len(a), len(b)
    states = {(0, 0): [(0.0, '')]}
    for i in range(m + 1):
        for j in range(n + 1):
            work.step('dp_states')
            if i == 0 and j == 0:
                continue
            if (i == 0 or (i == m and j == 0) or (j == n and i < m)):
                continue
            paths = []
            if i:
                face = (a[(i - 1) % m], b[j % n], a[i % m])
                cost = triangle_cost(points, face, normals_a[(i - 1) % m], work.options.min_area_ratio)
                if cost is not None:
                    paths.extend((value + cost, moves + 'A') for value, moves in states.get((i - 1, j), []))
            if j:
                face = (a[i % m], b[(j - 1) % n], b[j % n])
                cost = triangle_cost(points, face, normals_b[(j - 1) % n], work.options.min_area_ratio)
                if cost is not None:
                    paths.extend((value + cost, moves + 'B') for value, moves in states.get((i, j - 1), []))
            if paths:
                states[(i, j)] = sorted(paths)[:work.options.paths_per_phase]
    return states.get((m, n), [])


def audit_annulus(p, source, caps, boundary, selection, loops, limits, work, plane_options):
    candidate = np.concatenate((source, caps))
    after = extract(p, candidate, cancel=work.cancel)
    if not after['extractionAccepted']:
        return {'accepted': False, 'refusal': 'candidate_topology_' + after['refusal']}
    if after['singularVertices']:
        return {'accepted': False, 'refusal': 'candidate_disconnected_fans'}
    remaining = set(after['boundaryHalfedges'])
    if remaining != set(selection['unselectedHalfedges']):
        return {'accepted': False, 'refusal': 'selected_coverage_or_unselected_boundary_changed'}
    patch = signature(p, caps)
    if (not patch['signatureAvailable'] or patch['componentCount'] != 1 or
            patch['boundaryLoops'] != 2 or patch['eulerCharacteristic'] != 0 or patch['components'][0]['genus'] != 0):
        return {'accepted': False, 'refusal': 'patch_is_not_an_annulus', 'patchSignature': patch}
    before_sig, after_sig = signature(p, source), signature(p, candidate)
    merged = int(loops[0]['component'] != loops[1]['component'])
    if (not after_sig['signatureAvailable'] or
            after_sig['eulerCharacteristic'] != before_sig['eulerCharacteristic'] or
            after_sig['componentCount'] != before_sig['componentCount'] - merged or
            after_sig['boundaryLoops'] != before_sig['boundaryLoops'] - 2):
        return {'accepted': False, 'refusal': 'assembled_topology_intent_mismatch'}
    work.check()
    available = work.options.max_pair_tests - work.pair_tests
    if available <= 0:
        raise Refused('pair_tests_budget_exceeded')
    try:
        geometry = audit_new_pairs(p, candidate, len(source),
                                   Limits(limits.max_faces, limits.max_vertices, available), work.cancel)
    except Refused as error:
        if str(error) == 'pair_test_budget_exceeded':
            work.pair_tests = work.options.max_pair_tests
        raise
    work.pair_tests += geometry['pairTests']
    result = {'accepted': geometry['geometryAccepted'], 'geometry': geometry,
              'patchSignature': patch, 'resultSignature': after_sig}
    if not result['accepted']:
        result['refusal'] = 'new_intersections_or_contacts'
        return result
    # Report fresh hypotheses; stale selections never survive the operation.
    fresh = analyze(p, candidate, plane_options, cancel=work.cancel)
    if not fresh['analysisComplete']:
        raise Refused('reanalysis_' + fresh['refusal'])
    result['remainingPlanes'] = plane_summary(fresh)
    result['resultHash'] = after['sourceHash']
    return result


def generate(positions, triangles, source_hash, loop_ids, options=Options(), limits=Limits(),
             plane_options=PlaneOptions(), cancel=None):
    """Return unchanged arrays on refusal; choose a scored audited annulus otherwise.

    Explicit Bridge intent permits adding a handle or joining components. Neither
    intended shape nor unchanged source/source geometry is certified by this gate.
    """
    p, source = checked_arrays(positions, triangles)
    options.validate()
    limits.validate()
    plane_options.validate()
    report = {'schemaVersion': 1, 'operation': 'explicit_annular_bridge', 'sourceHash': source_hash,
              'options': asdict(options), 'limits': asdict(limits), 'planeOptions': asdict(plane_options),
              'accepted': False, 'searchComplete': False, 'candidates': [],
              'intendedShapeCertified': False, 'solidCertified': False}
    work = Work(options, cancel)
    try:
        work.check()
        if len(source) > limits.max_faces or len(p) > limits.max_vertices:
            raise Refused('mesh_budget_exceeded')
        boundary = extract(p, source, cancel=cancel)
        selection, loops = selected_rims(boundary, source_hash, loop_ids)
        report['selection'] = selection
        m, n = [len(loop['halfedges']) for loop in loops]
        if max(m, n) > options.max_loop_edges or n > options.max_phases:
            raise Refused('rim_or_phase_budget_exceeded')
        if len(source) + m + n > limits.max_faces:
            raise Refused('mesh_budget_exceeded')
        report['growthEvidence'] = growth_evidence(p, source, source_hash, selection['loopIds'], cancel=cancel)
        if report['growthEvidence'].get('refusal') == 'cancelled':
            raise Refused('cancelled')
        work.check()
        a = [h['sourceVertices'][0] for h in loops[0]['halfedges']]
        b_forward = [h['sourceVertices'][0] for h in loops[1]['halfedges']]
        used = p[a + b_forward].astype('d')
        local = used - used[0]
        scale = float(np.linalg.norm(local.max(axis=0) - local.min(axis=0)))
        if not np.isfinite(scale) or scale == 0:
            raise Refused('unsupported_coordinate_range')
        normalized = (p.astype('d') - used[0]) / scale
        collars = []
        for loop in loops:
            normals = []
            for h in loop['halfedges']:
                triangle = normalized[source[h['face']]]
                normal = np.cross(triangle[1] - triangle[0], triangle[2] - triangle[0])
                length = float(np.linalg.norm(normal))
                if length == 0 or not np.isfinite(length):
                    raise Refused('ill_conditioned_source_collar')
                normals.append(normal / length)
            collars.append(normals)
        seen, accepted = set(), []
        for phase in range(n):
            work.check()
            b = [b_forward[(phase - j) % n] for j in range(n)]
            normals_b = [collars[1][(phase - j - 1) % n] for j in range(n)]
            for cost, moves in zipper_paths(normalized, a, b, collars[0], normals_b, work):
                faces = zipper_faces(a, b, moves)
                key = tuple(sorted(tuple(sorted(face)) for face in faces))
                if key in seen:
                    continue
                seen.add(key)
                work.step('candidates')
                caps = np.asarray(faces, dtype=source.dtype)
                audit = audit_annulus(p, source, caps, boundary, selection, loops, limits, work, plane_options)
                report['candidates'].append(dict(audit, phase=phase, moves=moves, score=float(cost)))
                if audit['accepted']:
                    accepted.append((cost, key, caps, len(report['candidates']) - 1))
        work.check()
        report['searchComplete'] = True
        if not accepted:
            raise Refused('no_audited_annulus_in_candidate_family')
        cost, _, caps, index = min(accepted, key=lambda item: (item[0], item[1]))
        report.update({'accepted': True, 'auditedAlternativeCount': len(accepted),
                       'selectedCandidate': index, 'selectedScore': float(cost),
                       'addedFaces': len(caps), 'addedVertices': 0, 'selectedAddedFaces': caps.tolist()})
        result = np.concatenate((source, caps))
        report['work'] = work.summary()
        return p, result, report
    except Refused as error:
        report['refusal'] = str(error)
        report['work'] = work.summary()
        return p, source, report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--source-hash', required=True)
    parser.add_argument('--loop', action='append', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--mesh-output', type=Path)
    for option in fields(Options):
        parser.add_argument('--' + option.name.replace('_', '-'), type=option.type, default=option.default)
    args = parser.parse_args()
    options = Options(**{option.name: getattr(args, option.name) for option in fields(Options)})
    if args.mesh_output is not None and args.mesh_output.exists():
        raise ValueError('Mesh output already exists; use a fresh candidate path')
    p, ix, report = generate(*read_mesh(args.source), args.source_hash, args.loop, options)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, allow_nan=False), encoding='utf-8')
    if report['accepted'] and args.mesh_output is not None:
        args.mesh_output.parent.mkdir(parents=True, exist_ok=True)
        with args.mesh_output.open('xb') as stream:
            np.savez_compressed(stream, positions=p, indices=ix)
    print(json.dumps({k: report[k] for k in ('accepted', 'searchComplete', 'work', 'auditedAlternativeCount', 'refusal') if k in report}))
    return 0 if report['accepted'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
