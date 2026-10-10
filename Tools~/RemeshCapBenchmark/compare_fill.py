"""Compare real hole fillers on immutable private captures, then audit their output.

Optional dependencies are isolated benchmark tools, never Unity package dependencies.
The native replay manifest contains only closed, preserved, independently audited meshes.
An algorithm refusal is an experimental outcome; an incomplete audit never passes.
"""
import argparse
from collections import Counter
import hashlib
import importlib.metadata
import json
from pathlib import Path
import struct
import time

import numpy as np

from analyze import read_mesh
from boundaries import extract
from intersections import BoundsTree, exact_positions, triangle_intersection
from topology import inspect


def write_mesh(path, positions, faces):
    p = np.asarray(positions, dtype='<f4')
    ix = np.asarray(faces, dtype='<i4')
    Path(path).write_bytes(struct.pack('<ii', len(p), ix.size) + p.tobytes() + ix.tobytes())


def face_key(points):
    """Oriented cyclic key; reversing a donor face must fail preservation."""
    corners = tuple(tuple(float(x) for x in p) for p in points)
    return min(corners, corners[1:] + corners[:1], corners[2:] + corners[:2])


def preserve_source(source_p, source_ix, output_p, output_ix):
    """Reorder only; reject changed/dropped donor triangles before reconstructing Cap."""
    output_p = np.asarray(output_p, dtype=np.float32)
    output_ix = np.asarray(output_ix, dtype=np.int32)
    if not np.isfinite(output_p).all() or output_ix.min() < 0 or output_ix.max() >= len(output_p):
        raise ValueError('Library returned invalid positions/indices')
    required = Counter(face_key(t) for t in source_p[source_ix])
    cap = []
    for triangle in output_p[output_ix]:
        key = face_key(triangle)
        if required[key]:
            required[key] -= 1
        else:
            cap.append(triangle)
    missing = sum(required.values())
    if missing:
        raise ValueError(f'Library changed or dropped {missing} original oriented faces')
    p = [point.copy() for point in source_p]
    lookup = {tuple(map(float, point)): i for i, point in enumerate(p)}
    faces = source_ix.tolist()
    for triangle in cap:
        face = []
        for point in triangle:
            key = tuple(map(float, point))
            if key not in lookup:
                lookup[key] = len(p)
                p.append(point.copy())
            face.append(lookup[key])
        faces.append(face)
    return np.asarray(p, dtype=np.float32), np.asarray(faces, dtype=np.int32)


def components(faces):
    parent = list(range(len(faces)))
    def root(f):
        while parent[f] != f:
            parent[f] = parent[parent[f]]
            f = parent[f]
        return f
    edges = {}
    for f, face in enumerate(faces):
        for a, b in zip(face, np.roll(face, -1)):
            key = tuple(sorted((int(a), int(b))))
            if key in edges:
                parent[root(f)] = root(edges[key])
            else:
                edges[key] = f
    return np.array([root(f) for f in range(len(faces))])


def audit(positions, faces, old_count, pair_budget=200000):
    started = time.perf_counter()
    report = inspect(positions, faces)
    points, slots = np.unique(positions, axis=0, return_inverse=True)
    welded = slots[faces]
    labels = components(welded)
    triangles = positions[faces].astype(np.float64)
    tree = BoundsTree(triangles)
    exact, _ = exact_positions(positions)
    bad = []; tests = excluded = 0
    complete = True
    for first in range(old_count, len(faces)):
        tri = triangles[first]
        for second in tree.query(tri.min(axis=0), tri.max(axis=0)):
            second = int(second)
            if second >= first:
                continue
            if labels[first] != labels[second]:
                excluded += 1
                continue
            if tests >= pair_budget:
                complete = False
                break
            tests += 1
            contact = triangle_intersection(tuple(exact[int(v)] for v in faces[first]),
                                            tuple(exact[int(v)] for v in faces[second]))
            if contact is not None:
                bad.append({'newFace': first, 'otherFace': second, 'kind': contact.kind})
        if not complete:
            break
    cap = triangles[old_count:]
    if len(cap):
        edge2 = sum(np.sum((cap[:, a] - cap[:, b])**2, axis=1) for a, b in ((0,1),(1,2),(2,0)))
        twice = np.linalg.norm(np.cross(cap[:,1]-cap[:,0], cap[:,2]-cap[:,0]), axis=1)
        quality = np.divide(2*np.sqrt(3)*twice, edge2, out=np.zeros_like(twice), where=edge2 > 0)
        report.update(minTriangleQuality=float(quality.min()), meanTriangleQuality=float(quality.mean()))
    report.update(addedFaces=len(faces)-old_count, addedVertices=len(points),
                  exactTests=tests, excludedOtherElementPairs=excluded, improperContacts=len(bad),
                  contactSamples=bad[:8], auditComplete=complete, auditSeconds=time.perf_counter()-started)
    report['auditedClosed'] = report['closedManifold'] and not bad and complete
    return report


def fill(method, p, ix):
    points, slots = np.unique(p, axis=0, return_inverse=True)
    welded = slots[ix].astype(np.int32)
    if method.startswith('meshlab_'):
        import pymeshlab as ml
        ms = ml.MeshSet()
        ms.add_mesh(ml.Mesh(points.astype(np.float64), welded))
        ms.meshing_close_holes(maxholesize=512, selected=False, newfaceselected=True,
                              selfintersection=True, refinehole=method == 'meshlab_refine',
                              refineholeedgelen=ml.PercentageValue(3))
        mesh = ms.current_mesh()
        return mesh.vertex_matrix(), mesh.face_matrix()
    if method.startswith('meshlib_'):
        from meshlib import mrmeshpy as mr, mrmeshnumpy as mn
        mesh = mn.meshFromFacesVerts(welded, points)
        holes = list(mesh.topology.findHoleRepresentiveEdges())
        for edge in holes:
            if method == 'meshlib_refine':
                settings = mr.FillHoleNicelySettings()
                settings.triangulateParams.metric = mr.getUniversalMetric(mesh)
                settings.triangulateOnly = False
                settings.smoothCurvature = True
                boundary = extract(p, ix)
                lengths = [np.linalg.norm(p[e['sourceVertices'][0]]-p[e['sourceVertices'][1]])
                           for loop in boundary['loops'] for e in loop['halfedges']]
                settings.subdivideSettings.maxEdgeLen = float(np.median(lengths))*2
                settings.subdivideSettings.maxEdgeSplits = 1000
                mr.fillHoleNicely(mesh, edge, settings)
            else:
                params = mr.FillHoleParams()
                metric = {'meshlib_universal': mr.getUniversalMetric,
                          'meshlib_min_angle': mr.getMinTriAngleMetric,
                          'meshlib_min_area': mr.getMinAreaMetric}[method]
                params.metric = metric(mesh)
                mr.fillHole(mesh, edge, params)
        return mn.getNumpyVerts(mesh), mn.getNumpyFaces(mesh.topology)
    if method == 'centroid_fan':
        output = p.tolist(); faces = ix.tolist()
        for loop in extract(p, ix)['loops']:
            rim = [e['sourceVertices'][0] for e in loop['halfedges']]
            center = len(output); output.append(p[rim].mean(axis=0).tolist())
            faces.extend([a, center, b] for a,b in zip(rim, rim[1:]+rim[:1]))
        return output, faces
    raise ValueError('Unknown method: ' + method)


METHODS = ('meshlab_basic', 'meshlab_refine', 'meshlib_universal', 'meshlib_min_angle',
           'meshlib_min_area', 'meshlib_refine', 'centroid_fan', 'planes_then_meshlib_min_area')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--methods', nargs='+', default=list(METHODS))
    parser.add_argument('--pair-budget', type=int, default=200000)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding='utf-8-sig'))
    output = Path(manifest['output']); output.mkdir(parents=True, exist_ok=True)
    versions = {name: importlib.metadata.version(name) for name in ('numpy', 'pymeshlab', 'meshlib')}
    report = {'versions': versions, 'results': []}
    production_path = output / 'production.json'
    production = json.loads(production_path.read_text())['results'] if production_path.exists() else []
    native = []
    for case in manifest['cases']:
        p, ix = read_mesh(case['source']); old_count = len(ix)
        boundary = extract(p, ix)
        print(case['name'], 'rims', [len(loop['halfedges']) for loop in boundary['loops']], flush=True)
        methods = [(row['method'], row) for row in production if row['caseName'] == case['name']]
        methods.extend((method, None) for method in args.methods)
        for method, row in methods:
            result = {'caseName': case['name'], 'method': method, 'sourceFaces': old_count,
                      'sourceHash': hashlib.sha256(Path(case['source']).read_bytes()).hexdigest(),
                      'sourceLoops': len(boundary['loops']), 'rimEdges': [len(loop['halfedges']) for loop in boundary['loops']]}
            started = time.perf_counter()
            try:
                if row is not None:
                    result['production'] = row
                    if row['status'] != 'generated':
                        raise ValueError(row['reason'])
                    out_p, out_ix = read_mesh(row['path'])
                elif method == 'planes_then_meshlib_min_area':
                    baseline = next(r for r in production if r['caseName'] == case['name'] and r['method'] == 'ours_planar_001')
                    if baseline['status'] != 'generated':
                        raise ValueError('Planar baseline was refused: '+baseline['reason'])
                    base_p, base_ix = read_mesh(baseline['path'])
                    out_p, out_ix = fill('meshlib_min_area',base_p.copy(),base_ix.copy())
                    # The fallback also preserves every already accepted planar patch.
                    out_p, out_ix = preserve_source(base_p,base_ix,out_p,out_ix)
                else:
                    out_p, out_ix = fill(method, p.copy(), ix.copy())
                result['fillSeconds'] = time.perf_counter()-started
                out_p, out_ix = preserve_source(p, ix, out_p, out_ix)
                result['sourcePreserved'] = True
                result.update(audit(out_p, out_ix, old_count, args.pair_budget))
                result['addedVertices'] = len(out_p)-len(p)
                result['status'] = 'audited_closed' if result['auditedClosed'] else 'rejected'
                result['path'] = str((output / (case['name']+'__'+method+'__audited.bin')).resolve())
                write_mesh(result['path'], out_p, out_ix)
                if result['auditedClosed']:
                    native.append({key: result[key] for key in ('caseName', 'method', 'path')})
            except Exception as error:
                result.update(status='error', reason=type(error).__name__+': '+str(error))
            result['totalSeconds'] = time.perf_counter()-started
            report['results'].append(result)
            (output / 'comparison.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
            print(method, result['status'], '+', result.get('addedFaces',0), 'open', result.get('boundaryEdges'),
                  'contact', result.get('improperContacts'), result.get('reason','')[:160], flush=True)
    native_manifest = dict(manifest, candidates=native)
    (output / 'native-manifest.json').write_text(json.dumps(native_manifest, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
