"""Experimental compound Cap, on private geometry only; not a Unity pipeline stage.

Boundary JSON lists directed source cycles in lexicographically welded vertex
slots. Projection and floor margin are explicit experiment parameters. Existing
faces/positions remain the reference; only Cap interior vertices may be added.
"""
import argparse
from collections import Counter
import json
from pathlib import Path

import numpy as np

from analyze import read_mesh, scan
from intersections import exact_positions, orient
from topology import inspect
from branch import reconnect


def validate_boundaries(indices, loops):
    incidence = Counter(tuple(sorted(map(int, edge))) for t in indices for edge in zip(t, np.roll(t, -1)))
    expected = Counter((int(a), int(b)) for t in indices for a, b in zip(t, np.roll(t, -1))
                       if incidence[tuple(sorted((int(a), int(b))))] == 1)
    if any(n > 2 for n in incidence.values()):
        raise ValueError('Source already has non-manifold edges')
    supplied = Counter()
    for loop in loops:
        if len(loop) < 3 or len(set(loop)) != len(loop):
            raise ValueError('Boundary cycles must be simple and have at least three vertices')
        supplied.update(zip(loop, loop[1:]+loop[:1]))
    if not expected or supplied != expected:
        raise ValueError('Boundary cycles must cover every directed source boundary exactly once')


def validate_projection(projected, loops):
    """Reject crossed/overlapping constraints and T junctions before triangulation."""
    ids = sorted({v for loop in loops for v in loop})
    if len(np.unique(projected[ids], axis=0)) != len(ids):
        raise ValueError('Distinct boundary vertices coincide in projection')
    exact, _ = exact_positions(projected)
    edges = [(a, b) for loop in loops for a, b in zip(loop, loop[1:]+loop[:1])]
    for first, (a, b) in enumerate(edges):
        pa, pb = exact[a], exact[b]
        for c, d in edges[first+1:]:
            pc, pd = exact[c], exact[d]
            if any(max(pa[k], pb[k]) < min(pc[k], pd[k]) or
                   max(pc[k], pd[k]) < min(pa[k], pb[k]) for k in (0, 1)):
                continue
            signs = (orient(pa, pb, pc), orient(pa, pb, pd),
                     orient(pc, pd, pa), orient(pc, pd, pb))
            if signs[0]*signs[1] > 0 or signs[2]*signs[3] > 0:
                continue
            shared = {a, b} & {c, d}
            if not any(signs):
                axis = 0 if pa[0] != pb[0] else 1
                lower = max(min(pa[axis], pb[axis]), min(pc[axis], pd[axis]))
                upper = min(max(pa[axis], pb[axis]), max(pc[axis], pd[axis]))
                if lower == upper and shared:
                    continue
            elif shared:
                continue
            raise ValueError(f'Projection has crossed, overlapping or nonadjacent touching boundary edges {(a,b)} / {(c,d)}')
    areas = [sum(exact[a][0]*exact[b][1]-exact[a][1]*exact[b][0]
                 for a, b in zip(loop, loop[1:]+loop[:1])) for loop in loops]
    if any(area == 0 for area in areas):
        raise ValueError('Boundary collapses in projection')
    return areas


def triangulate(vertices, segments, holes):
    # Optional, pinned offline dependency. No vendored library or Unity/native ABI change.
    import triangle
    data = {'vertices': vertices, 'segments': segments}
    if len(holes):
        data['holes'] = np.asarray(holes)
    result = triangle.triangulate(data, 'pYCS0Q')
    if 'triangles' not in result or not np.array_equal(vertices, result['vertices']):
        raise ValueError('Triangulation changed/added constraint vertices or produced no faces')
    return result['triangles']


def generate(raw_points, raw_indices, loops, shear, floor_margin, max_rounds=8, bridge_branches=False):
    if not np.isfinite(shear).all() or not np.isfinite(floor_margin) or floor_margin < 0 or max_rounds < 0:
        raise ValueError('Invalid projection, floor margin or refinement budget')
    points, remap = np.unique(raw_points, axis=0, return_inverse=True)
    source = remap[raw_indices].astype('u4')
    loops = [list(loop) for loop in loops]
    if any(type(v) is not int or not 0 <= v < len(points) for loop in loops for v in loop):
        raise ValueError('Boundary slots are invalid')
    validate_boundaries(source, loops)
    shear = np.asarray(shear, dtype=float)
    q = points[:, [0, 2]].astype('d')+points[:, 1, None].astype('d')*shear
    areas = validate_projection(q, loops)
    outer_sign = 1 if max(areas, key=abs) > 0 else -1
    ids = np.unique(np.concatenate(loops))
    slots = {int(v): k for k, v in enumerate(ids)}
    segments = np.array([(slots[a], slots[b]) for loop in loops
                         for a, b in zip(loop, loop[1:]+loop[:1])], dtype='i4')
    holes = []
    for loop, area in zip(loops, areas):
        if area*outer_sign < 0:
            ring = q[loop]
            edges = np.stack((np.arange(len(loop)), np.roll(np.arange(len(loop)), -1)), axis=1).astype('i4')
            triangles = ring[triangulate(ring, edges, [])]
            sizes = np.abs((triangles[:, 1, 0]-triangles[:, 0, 0])*(triangles[:, 2, 1]-triangles[:, 0, 1])-
                           (triangles[:, 1, 1]-triangles[:, 0, 1])*(triangles[:, 2, 0]-triangles[:, 0, 0]))
            holes.append(triangles[sizes.argmax()].mean(axis=0))
    initial = ids[triangulate(q[ids], segments, holes)]
    boundary = {tuple(sorted(edge)) for loop in loops for edge in zip(loop, loop[1:]+loop[:1])}
    internal = sorted({tuple(sorted(map(int, edge))) for t in initial for edge in zip(t, np.roll(t, -1))}-boundary)
    extra = q[initial].mean(axis=1)
    if internal:
        extra = np.concatenate((extra, q[np.array(internal)].mean(axis=1)))
    extra = np.unique(extra, axis=0)
    floor = float(points[:, 1].min())-floor_margin
    history = []
    for iteration in range(max_rounds+1):
        projected = np.concatenate((q[ids], extra))
        mapping = np.concatenate((ids, np.arange(len(points), len(points)+len(extra))))
        caps = mapping[triangulate(projected, segments, holes)].astype('u4')
        if outer_sign > 0:
            caps = caps[:, ::-1]
        interior = np.stack((extra[:, 0]-shear[0]*floor, np.full(len(extra), floor),
                             extra[:, 1]-shear[1]*floor), axis=1).astype('f4')
        candidate = np.concatenate((points, interior)).astype('f4')
        indices = np.concatenate((source, caps))
        audit = scan(candidate, indices, len(source))
        history.append(audit['summary'])
        if audit['summary']['capGeometryAccepted'] or iteration == max_rounds:
            break
        bad = sorted({pair['faceB'] for pair in audit['pairs'] if pair['role'] == 'source-cap'})
        if not bad:
            break
        actual = candidate[:, [0, 2]].astype('d')+candidate[:, 1, None].astype('d')*shear
        extra = np.unique(np.concatenate((extra, actual[indices[bad]].mean(axis=1))), axis=0)
    if not np.array_equal(raw_points[raw_indices], candidate[indices[:len(source)]]):
        raise ValueError('Experiment changed original face geometry')
    bridge_report = None
    if bridge_branches and audit['summary']['capGeometryAccepted']:
        indices, bridge_report = reconnect(candidate, indices, len(source))
        if bridge_report['changed']:
            audit = scan(candidate, indices, len(source))
    topology = inspect(candidate, indices)
    report = {'schemaVersion': 1, 'experimental': True, 'projectionShear': shear.tolist(),
              'floorMargin': floor_margin, 'floorHeight': floor, 'addedInteriorVertices': len(interior),
              'sourceFaces': len(source), 'sourceTopology': inspect(points, source), 'topology': topology,
              'geometry': audit['summary'], 'refinementHistory': history,
              'capAccepted': bool(topology['closedManifold'] and audit['summary']['capGeometryAccepted']),
              'solidCandidateAccepted': bool(topology['closedManifold'] and not audit['pairs'])}
    if bridge_report is not None:
        report['branchConnection'] = bridge_report
    return candidate, indices, report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--boundaries', required=True, type=Path)
    parser.add_argument('--shear', required=True, nargs=2, type=float)
    parser.add_argument('--floor-margin', required=True, type=float)
    parser.add_argument('--max-rounds', type=int, default=8)
    parser.add_argument('--bridge-branches', action='store_true')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    points, indices = read_mesh(args.source)
    loops = json.loads(args.boundaries.read_text(encoding='utf-8'))
    candidate, triangles, report = generate(points, indices, loops, args.shear, args.floor_margin, args.max_rounds, args.bridge_branches)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open('wb') as stream:
        np.array((len(candidate), triangles.size), '<u4').tofile(stream)
        candidate.astype('<f4').tofile(stream)
        triangles.astype('<u4').tofile(stream)
    report_path = args.output.with_suffix('.json')
    report_path.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2), flush=True)
    print('Diagnostic candidate:', args.output.resolve(), flush=True)
    return 0 if report['solidCandidateAccepted'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
