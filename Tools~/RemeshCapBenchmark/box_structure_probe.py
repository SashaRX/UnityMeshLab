"""Architecture probe: known box reference, not an automatic Cap solver."""
import argparse
import json
from collections import Counter, defaultdict
from pathlib import Path

import numpy as np

from topology import inspect
from analyze import scan

POINTS = np.array([[-1,-1,-1], [1,-1,-1], [1,1,-1], [-1,1,-1],
                   [-1,-1,1], [1,-1,1], [1,1,1], [-1,1,1]], dtype='f4')
QUADS = [(0,3,2,1), (4,5,6,7), (0,4,7,3),
         (1,2,6,5), (0,1,5,4), (3,7,6,2)]


def triangulate(quads):
    return np.array([t for a,b,c,d in quads for t in ((a,b,c), (a,c,d))], dtype='u4')


def simple_loops(indices):
    directed = [(int(a),int(b)) for t in indices for a,b in zip(t, np.roll(t,-1))]
    counts = Counter(tuple(sorted(edge)) for edge in directed)
    remaining = {e for e in directed if counts[tuple(sorted(e))] == 1}
    outgoing, incoming = defaultdict(list), defaultdict(list)
    for a,b in remaining:
        outgoing[a].append(b)
        incoming[b].append(a)
    if any(len(outgoing[v]) != 1 or len(incoming[v]) != 1 for v in set(outgoing)|set(incoming)):
        raise ValueError('Only ordinary manifold box boundaries are supported by this probe')
    loops = []
    while remaining:
        start = min(remaining)[0]
        loop, current = [], start
        while True:
            loop.append(current)
            nxt = outgoing[current][0]
            remaining.remove((current,nxt))
            current = nxt
            if current == start:
                break
        loops.append(loop)
    return loops


def fit(points):
    q = np.asarray(points, dtype='d')
    lengths = np.linalg.norm(np.roll(q,-1,axis=0)-q, axis=1)
    weights = (lengths + np.roll(lengths,1)) / (2 * lengths.sum())
    center = (q * weights[:,None]).sum(axis=0)
    _, singular, basis = np.linalg.svd((q-center)*np.sqrt(weights[:,None]), full_matrices=False)
    residual = np.abs((q-center) @ basis[-1])
    return {'normal': basis[-1].tolist(), 'maxDistance': float(residual.max()),
            'rmsDistance': float(np.sqrt((weights*residual**2).sum())),
            'singularValues': singular.tolist()}


def probe(name, missing, points=POINTS):
    kept = triangulate([quad for i,quad in enumerate(QUADS) if i not in missing])
    used = np.unique(kept)
    mapping = {int(v): i for i,v in enumerate(used)}
    source_points = points[used].copy()
    source_indices = np.array([[mapping[int(v)] for v in t] for t in kept], dtype='u4')
    loops = simple_loops(source_indices)
    reference_points = list(source_points)
    for v in range(len(points)):
        if v not in mapping:
            mapping[v] = len(reference_points)
            reference_points.append(points[v])
    reference_caps = np.array([[mapping[int(v)] for v in t]
                              for t in triangulate([QUADS[i] for i in missing])], dtype='u4')
    candidate_points = np.array(reference_points, dtype='f4')
    candidate_indices = np.concatenate((source_indices, reference_caps))
    np.testing.assert_array_equal(source_points, candidate_points[:len(source_points)])
    np.testing.assert_array_equal(source_indices, candidate_indices[:len(source_indices)])
    topology = inspect(candidate_points, candidate_indices)
    geometry = scan(candidate_points, candidate_indices, len(source_indices))['summary']
    if not topology['closedManifold'] or not geometry['capGeometryAccepted']:
        raise RuntimeError('Known box reference failed the independent geometry/topology audit')
    return {'name': name, 'sourceTopology': inspect(source_points, source_indices),
            'loops': [{'edges': len(loop), 'wholePlane': fit(source_points[loop])} for loop in loops],
            'groundTruthPlanes': len(missing), 'groundTruthCapTriangles': len(reference_caps),
            'groundTruthNewVertices': len(candidate_points)-len(source_points),
            'groundTruthTopology': topology, 'groundTruthGeometry': geometry}


def build_report():
    return {
        'schemaVersion': 1,
        'purpose': 'Known-reference feasibility and whole-loop plane-fit probe; not automatic reconstruction',
        'boxSide': 2,
        'cases': [probe('one_missing_face', [1]),
                  probe('two_adjacent_missing_faces', [1,3]),
                  probe('two_opposite_missing_faces', [0,1]),
                  probe('three_adjacent_missing_faces', [1,3,5])]
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    report = build_report()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    for case in report['cases']:
        print(case['name'], 'loops', [v['edges'] for v in case['loops']],
              'max plane distances', [round(v['wholePlane']['maxDistance'],6) for v in case['loops']],
              'reference cap triangles', case['groundTruthCapTriangles'],
              'reference new vertices', case['groundTruthNewVertices'])


if __name__ == '__main__':
    main()
