"""Reproducible generated-Bridge and averaged co-normal growth benchmark."""
import argparse
import json
from pathlib import Path

import numpy as np

from analyze import scan
from boundaries import extract
from bridge import generate
from bridge_probe import torus_gap
from incremental_probe import compact_box
from surface_signature import signature


def subdivide_boundary(p, ix):
    h = extract(p, ix)['loops'][0]['halfedges'][0]
    a, b, c = map(int, np.roll(ix[h['face']], -h['corner']))
    m = len(p)
    points = np.concatenate((p, ((p[a] * 3 + p[b]) / 4)[None, :])).astype(p.dtype)
    faces = np.concatenate((np.delete(ix, h['face'], axis=0), [[a, m, c], [m, b, c]])).astype(ix.dtype)
    return points, faces


def blocked_gap():
    p, ix, *_ = torus_gap(8, 6)
    theta = np.pi / 8
    wall = np.array([[r * np.cos(theta), r * np.sin(theta), z]
                     for r, z in ((1.1, -.8), (2.9, -.8), (2.9, .8), (1.1, .8))], dtype='f4')
    count = len(p)
    p = np.concatenate((p, wall))
    ix = np.concatenate((ix, [[count, count + 1, count + 2], [count, count + 2, count + 3]])).astype('u4')
    return p, ix


def trial(name, p, ix, expected, rim_size=None):
    boundary = extract(p, ix)
    ids = [loop['id'] for loop in boundary['loops'] if rim_size is None or len(loop['halfedges']) == rim_size]
    result_p, result_ix, report = generate(p, ix, boundary['sourceHash'], ids)
    preserved = np.array_equal(p, result_p) and np.array_equal(ix, result_ix[:len(ix)])
    result = {'name': name, 'expectedAccepted': expected, 'generation': report, 'sourcePreserved': preserved}
    passed = preserved and report['accepted'] == expected and report['searchComplete']
    if report['accepted']:
        audit = scan(result_p, result_ix, len(ix))['summary']
        sig = signature(result_p, result_ix)
        result.update({'independentGeometry': audit, 'independentSignature': sig})
        passed = passed and audit['capGeometryAccepted'] and sig['boundaryLoops'] == 0 and sig['components'][0]['genus'] == 1
    result['probePassed'] = passed
    return result


def build_report():
    p, ix, *_ = torus_gap()
    cases = [trial('equal_torus_rims', p, ix, True)]
    p, ix, *_ = torus_gap(8, 6)
    p, ix = subdivide_boundary(p, ix)
    cases.append(trial('unequal_torus_rims', p, ix, True))
    cases.append(trial('box_requires_disks', *compact_box([0, 1]), False))
    cases.append(trial('occupied_torus_gap', *blocked_gap(), False, rim_size=6))
    return {'schemaVersion': 1, 'purpose': 'Explicit Bridge intent; generated candidates, not automatic closure classification',
            'cases': cases, 'probePassed': all(case['probePassed'] for case in cases)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = build_report()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, allow_nan=False), encoding='utf-8')
    print(json.dumps({'probePassed': report['probePassed'], 'cases': [
        {'name': case['name'], 'accepted': case['generation']['accepted'],
         'alternatives': case['generation'].get('auditedAlternativeCount'), 'work': case['generation']['work']}
        for case in report['cases']]}))
    return 0 if report['probePassed'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
