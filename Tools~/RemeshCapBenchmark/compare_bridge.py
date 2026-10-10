"""Explicit-pair Bridge comparison: production export versus MeshLib stitching.

Prepare a private manifest, run GenerateBridgeCandidates in Unity, then audit.
Real capture pairs are geometric probes; authorial Bridge intent is unverified.
"""
import argparse
import json
from pathlib import Path
import time

import numpy as np

from analyze import read_mesh
from boundaries import extract
from bridge_generation_probe import subdivide_boundary
from bridge_probe import torus_gap
from compare_fill import audit, preserve_source, write_mesh
from surface_signature import signature


def prepare(output, captures):
    output.mkdir(parents=True, exist_ok=True)
    cases = []
    for name, major, minor, split in (
            ('Torus8', 12, 8, False), ('Torus64', 8, 64, False),
            ('Torus128', 8, 128, False), ('Unequal7', 8, 6, True),
            ('Unequal97', 8, 96, True)):
        p, ix, *_ = torus_gap(major, minor)
        if split:
            p, ix = subdivide_boundary(p, ix)
        path = output / (name + '__source.bin')
        write_mesh(path, p, ix)
        cases.append(dict(name=name, source=str(path.resolve()), selection='0,1', intent='torus'))
    for item in captures:
        name, path, first, second = item.split('|')
        cases.append(dict(name=name, source=str(Path(path).resolve()),
                          selection=f'{int(first)},{int(second)}', intent='unverified'))
    manifest = dict(output=str(output.resolve()), cases=cases)
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    return manifest


def edge_key(a, b):
    return tuple(sorted((tuple(map(float, a)), tuple(map(float, b)))))


def border(points, faces):
    loops = extract(points, faces)
    if not loops['extractionAccepted']:
        raise ValueError('Boundary extraction refused')
    edges = {edge_key(points[h['sourceVertices'][0]], points[h['sourceVertices'][1]])
             for loop in loops['loops'] for h in loop['halfedges']}
    return loops['loops'], edges


def stitch(points, faces, selected, metric):
    from meshlib import mrmeshpy as mr, mrmeshnumpy as mn
    p, slots = np.unique(points, axis=0, return_inverse=True)
    mesh = mn.meshFromFacesVerts(slots[faces].astype('i4'), p.astype('f4'))
    wanted = [{tuple(map(float, points[h['sourceVertices'][0]])) for h in loop['halfedges']}
              for loop in selected]
    chosen = []
    for hole in mesh.topology.findHoleRepresentiveEdges():
        # MeshLib hole representatives have no left face; traversal expects no right face.
        start = hole.sym()
        edge = start
        rim = set()
        for _ in range(len(p) + 1):
            rim.add(tuple(map(float, p[mesh.topology.org(edge).get()])))
            edge = mesh.topology.nextLeftBd(edge)
            if edge == start:
                break
        else:
            raise ValueError('MeshLib boundary traversal exceeded source vertex count')
        for number, expected in enumerate(wanted):
            if rim == expected:
                chosen.append((number, hole))
    if len(chosen) != 2 or {item[0] for item in chosen} != {0, 1}:
        raise ValueError('MeshLib did not preserve the selected boundary pair')
    params = mr.StitchHolesParams()
    params.metric = mr.getComplexStitchMetric(mesh) if metric == 'complex' else mr.getUniversalMetric(mesh)
    mr.stitchHoles(mesh, chosen[0][1], chosen[1][1], params)
    return preserve_source(points, faces, mn.getNumpyVerts(mesh), mn.getNumpyFaces(mesh.topology))


def verify(p, ix, result_p, result_ix, selected):
    if len(selected) != 2:
        raise ValueError('Bridge comparison requires exactly two selected loops')
    result_p, result_ix = preserve_source(p, ix, result_p, result_ix)
    report = audit(result_p, result_ix, len(ix), pair_budget=2000000)
    _, initial = border(p, ix)
    _, remaining = border(result_p, result_ix)
    removed = {edge_key(p[h['sourceVertices'][0]], p[h['sourceVertices'][1]])
               for loop in selected for h in loop['halfedges']}
    before = signature(p, ix)
    after = signature(result_p, result_ix)
    annulus = signature(result_p, result_ix[len(ix):])
    if not all(s['signatureAvailable'] for s in (before, after, annulus)):
        raise ValueError('Candidate has no valid orientable manifold signature')
    same = selected[0]['component'] == selected[1]['component']
    topology_valid = not any(report[key] for key in ('duplicateFaces', 'degenerateFaces',
        'nonManifoldEdges', 'windingEdges', 'disconnectedVertexFans'))
    report.update(boundaryPreserved=remaining == initial - removed,
                  before=before, after=after, patch=annulus,
                  componentCountPreserved=len(after['components']) == len(before['components']) - (not same),
                  eulerPreserved=before['eulerCharacteristic'] == after['eulerCharacteristic'])
    report['accepted'] = (topology_valid and report['auditComplete'] and report['improperContacts'] == 0
                          and report['boundaryPreserved'] and report['componentCountPreserved'] and report['eulerPreserved']
                          and len(annulus['components']) == 1 and annulus['components'][0]['eulerCharacteristic'] == 0
                          and annulus['boundaryLoops'] == 2)
    return report


def compare(manifest):
    output = Path(manifest['output'])
    production = json.loads((output / 'production-bridge.json').read_text(encoding='utf-8'))
    rows = []
    for case in manifest['cases']:
        p, ix = read_mesh(case['source'])
        loops, _ = border(p, ix)
        selected = [loops[int(number)] for number in case['selection'].split(',')]
        for method in ('ours_bridge', 'meshlib_complex', 'meshlib_universal'):
            started = time.perf_counter()
            row = dict(caseName=case['name'], method=method, intent=case['intent'])
            try:
                if method == 'ours_bridge':
                    result = next(r for r in production['results'] if r['caseName'] == case['name'])
                    row['production'] = result
                    if result['status'] != 'generated':
                        raise ValueError(result['reason'])
                    result_p, result_ix = read_mesh(result['path'])
                else:
                    result_p, result_ix = stitch(p, ix, selected, method.split('_')[1])
                    write_mesh(output / (case['name'] + '__' + method + '.bin'), result_p, result_ix)
                row['audit'] = verify(p, ix, result_p, result_ix, selected)
                row['status'] = 'accepted' if row['audit']['accepted'] else 'invalid'
            except (ValueError, RuntimeError) as error:
                row.update(status='refused', reason=str(error))
            row['seconds'] = time.perf_counter() - started
            rows.append(row)
            print(case['name'], method, row['status'], flush=True)
            (output / 'comparison.json').write_text(json.dumps(rows, indent=2, allow_nan=False), encoding='utf-8')
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--capture', action='append', default=[], help='NAME|SOURCE.bin|LOOP_A|LOOP_B')
    parser.add_argument('--audit', action='store_true')
    args = parser.parse_args()
    if args.audit:
        compare(json.loads((args.output / 'manifest.json').read_text(encoding='utf-8')))
    else:
        prepare(args.output, args.capture)


if __name__ == '__main__':
    main()
