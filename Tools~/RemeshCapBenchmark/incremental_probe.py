"""Known-reference sequential Cap replay; no automatic feature reconstruction."""
import argparse
from itertools import permutations
import json
from pathlib import Path

import numpy as np

from analyze import scan
from boundaries import extract
from box_structure_probe import POINTS, QUADS, triangulate
from incremental import attempt, plane_summary
from planes import analyze
from topology import inspect


def compact_box(missing):
    faces = triangulate([q for i, q in enumerate(QUADS) if i not in missing])
    used = np.unique(faces)
    mapping = {int(v): i for i, v in enumerate(used)}
    return POINTS[used].copy(), np.array([[mapping[int(v)] for v in t] for t in faces], dtype='u4')


def reference_patch(p, face):
    points = list(p.copy())
    ids = []
    for v in QUADS[face]:
        matches = [i for i, point in enumerate(points) if np.array_equal(point, POINTS[v])]
        if not matches:
            matches = [len(points)]
            points.append(POINTS[v].copy())
        ids.append(matches[0])
    candidate = np.asarray(points, dtype=p.dtype)
    return candidate[len(p):].copy(), triangulate([ids]), candidate


def patch_support(p, ix, candidate, caps):
    cap_edges = {(tuple(candidate[a]), tuple(candidate[b])) for t in caps for a, b in zip(t, np.roll(t, -1))}
    boundary = extract(p, ix)
    selected = {h['halfedge'] for loop in boundary['loops'] for h in loop['halfedges']
                if tuple(reversed(tuple(tuple(p[v]) for v in h['sourceVertices']))) in cap_edges}
    for loop in boundary['loops']:
        edges = loop['halfedges']
        for i, h in enumerate(edges):
            if h['halfedge'] in selected and (len(selected) == len(edges) or edges[i - 1]['halfedge'] not in selected):
                ordered = edges[i:] + edges[:i]
                support = []
                for edge in ordered:
                    if edge['halfedge'] not in selected:
                        break
                    support.append(edge['halfedge'])
                if set(support) == selected:
                    return boundary, support
    raise ValueError('Known reference patch lacks a single continuous support arc')


def reference_attempt(p, ix, face, **kwargs):
    extra, caps, candidate = reference_patch(p, face)
    boundary, support = patch_support(p, ix, candidate, caps)
    return attempt(p, ix, boundary['sourceHash'], support, extra, caps, **kwargs)


def replay(order):
    p, ix = compact_box(order)
    original_p, original_ix = p.copy(), ix.copy()
    initial = plane_summary(analyze(p, ix))
    steps = []
    for face in order:
        p, ix, report = reference_attempt(p, ix, face)
        steps.append(dict(report, referenceFace=face))
        if not report['accepted']:
            break
    preserved = (np.array_equal(original_p, p[:len(original_p)]) and
                 np.array_equal(original_ix, ix[:len(original_ix)]))
    topology = inspect(p, ix)
    geometry = scan(p, ix, len(original_ix))['summary']
    complete = len(steps) == len(order) and all(step['accepted'] for step in steps)
    reference_passed = complete and preserved and topology['closedManifold'] and geometry['capGeometryAccepted']
    canonical = sorted(tuple(sorted(tuple(map(float, p[v])) for v in t)) for t in ix)
    return {'order': list(order), 'initialPlanes': initial, 'steps': steps,
            'referenceReplayPassed': reference_passed, 'sourcePreserved': preserved,
            'finalTopology': topology, 'finalGeometry': geometry}, canonical


def build_report():
    cases = []
    for missing in ([1, 3], [1, 3, 5]):
        trials, meshes = [], []
        for order in permutations(missing):
            trial, mesh = replay(order)
            trials.append(trial)
            meshes.append(mesh)
        cases.append({'missingReferenceFaces': missing, 'trials': trials,
                      'sameFinalReferenceGeometry': all(mesh == meshes[0] for mesh in meshes)})
    return {'schemaVersion': 1, 'purpose': 'Supplied reference Caps; transaction and re-analysis only',
            'cases': cases, 'referenceReplayPassed': all(
                case['sameFinalReferenceGeometry'] and all(t['referenceReplayPassed'] for t in case['trials'])
                for case in cases)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = build_report()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, allow_nan=False), encoding='utf-8')
    print(json.dumps({'referenceReplayPassed': report['referenceReplayPassed'],
                      'ordersTested': [len(case['trials']) for case in report['cases']],
                      'sameFinalReferenceGeometry': [case['sameFinalReferenceGeometry'] for case in report['cases']]}))
    return 0 if report['referenceReplayPassed'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
