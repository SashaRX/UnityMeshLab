"""Offline libigl 2.6.3 QSlim experiment on exact Unity-exported project meshes.

python -X utf8 Tools~/compare_lod_qslim.py OUTPUT --library _results~/qslim-python-2.6.3
Run Unity's -meshlabQslimExport first, then -meshlabQslimCompare after this script.
No distributed Unity native plugin is changed. Each native job runs in a child.
"""
import argparse
import hashlib
import importlib.metadata
import json
import math
from pathlib import Path
import subprocess
import sys
import time

PINNED_VERSION = "2.6.3"


def topology_stats(vertices, faces):
    import numpy as np
    edges = {}; incident = {}; adjacency = [set() for _ in faces]
    for i, face in enumerate(faces):
        for v in face:
            incident.setdefault(int(v), set()).add(i)
        for a, b in zip(face, np.roll(face, -1)):
            key = tuple(sorted((int(a), int(b))))
            edges.setdefault(key, []).append((i, 1 if a < b else -1))
    vertex_neighbors = {v: {} for v in incident}
    boundary = {}
    for (a, b), attached in edges.items():
        linked = {f for f, _ in attached}
        for f in linked:
            adjacency[f].update(linked-{f})
            for v in (a, b):
                vertex_neighbors[v].setdefault(f, set()).update(linked-{f})
        if len(attached) == 1:
            boundary.setdefault(a, set()).add(b); boundary.setdefault(b, set()).add(a)
    def components(nodes, graph):
        unseen = set(nodes); count = 0
        while unseen:
            stack = [unseen.pop()]; count += 1
            while stack:
                for n in graph(stack.pop()):
                    if n in unseen:
                        unseen.remove(n); stack.append(n)
        return count
    vertex_manifold = all(components(fs, lambda f: vertex_neighbors[v].get(f, set())) == 1 for v, fs in incident.items())
    boundary_loops = components(boundary, lambda v: boundary[v]) if all(len(n) == 2 for n in boundary.values()) else None
    zero_area = sum(len(set(f)) != 3 or np.linalg.norm(np.cross(vertices[f[1]]-vertices[f[0]], vertices[f[2]]-vertices[f[0]])) <= 1e-20 for f in faces)
    return dict(vertices=len(incident), faces=len(faces), edges=len(edges),
                components=components(range(len(faces)), lambda f: adjacency[f]),
                euler=len(incident)-len(edges)+len(faces), boundaryLoops=boundary_loops,
                edgeManifold=all(len(e) <= 2 for e in edges.values()), vertexManifold=vertex_manifold,
                windingConflicts=sum(len(e) == 2 and abs(sum(s for _, s in e)) == 2 for e in edges.values()),
                zeroAreaFaces=int(zero_area))


def summarize_existing(directory):
    import numpy as np
    summary = []
    for source_path in sorted(directory.glob('*-source.json')):
        source = json.loads(source_path.read_text(encoding='utf-8'))
        output = json.loads(source_path.with_name(source_path.name.replace('-source.json', '-qslim.json')).read_text(encoding='utf-8'))
        vertices = np.array([[v[k] for k in 'xyz'] for v in source['positions']])
        points = np.array(source['controlPoints'])
        original = []; raw_original = []
        for slot in source['submeshes']:
            faces = np.array(slot['triangles']).reshape(-1, 3)
            used = np.unique(faces); labels = np.unique(points[used]); lookup = {int(p): i for i, p in enumerate(labels)}
            v = np.array([vertices[used[points[used] == p]].mean(axis=0) for p in labels])
            f = np.array([[lookup[int(points[i])] for i in face] for face in faces])
            raw_original.append(topology_stats(v, f))
            if output.get('removeDegenerateFaces'):
                keep = [len(set(face)) == 3 and np.linalg.norm(np.cross(v[face[1]]-v[face[0]], v[face[2]]-v[face[0]])) >= source['scale']**2*1e-12 for face in f]
                f = f[keep]
            original.append(topology_stats(v, f))
        levels = []
        for level in output['levels']:
            if not level['valid']:
                levels.append(dict(level=level['level'], valid=False, error=level['error'])); continue
            stats = []
            for slot in level['submeshes']:
                v = np.array([[p[k] for k in 'xyz'] for p in slot['positions']])
                f = np.array(slot['triangles'], dtype=int).reshape(-1, 3)
                stats.append(topology_stats(v, f))
            levels.append(dict(level=level['level'], valid=True, submeshes=stats))
        summary.append(dict(fixture=source['fixture'], rawSource=raw_original, source=original, levels=levels))
    (directory/'qslim-topology.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print('Saved independent QSlim connectivity diagnostics', flush=True)


def allocations(counts, budget):
    total = sum(counts)
    exact = [budget*c/total for c in counts]
    values = [min(c, max(1, math.floor(e))) if c else 0 for c, e in zip(counts, exact)]
    while sum(values) < budget:
        eligible = [i for i, c in enumerate(counts) if values[i] < c]
        if not eligible:
            break
        i = max(eligible, key=lambda i: exact[i]-values[i])
        values[i] += 1
    return values


def worker(source_path, output_path, library, blocked, clean_degenerate):
    sys.path.insert(0, str(library.resolve()))
    import igl
    import numpy as np
    version = importlib.metadata.version('libigl')
    if version != PINNED_VERSION:
        raise RuntimeError(f"Expected libigl {PINNED_VERSION}, got {version}")
    source = json.loads(source_path.read_text(encoding='utf-8'))
    vertices = np.array([[v[k] for k in 'xyz'] for v in source['positions']], dtype=np.float64)
    points = np.array(source['controlPoints'], dtype=np.int64)
    meshes = []
    preparation = []
    for submesh in source['submeshes']:
        original_faces = np.array(submesh['triangles'], dtype=np.int32).reshape(-1, 3)
        used = np.unique(original_faces)
        unique_points = np.unique(points[used])
        mapping = {int(cp): i for i, cp in enumerate(unique_points)}
        positions = np.array([vertices[used[points[used] == cp]].mean(axis=0) for cp in unique_points])
        faces = np.array([[mapping[int(points[v])] for v in face] for face in original_faces], dtype=np.int32)
        delta = max((float(np.linalg.norm(vertices[v]-positions[mapping[int(points[v])]])) for v in used), default=0)
        degenerate = np.array([len(set(face)) != 3 or np.linalg.norm(np.cross(positions[face[1]]-positions[face[0]], positions[face[2]]-positions[face[0]])) < source['scale']**2*1e-12 for face in faces], dtype=bool)
        original_degenerates = int(sum(degenerate))
        birth_map = np.arange(len(faces))
        if clean_degenerate and original_degenerates:
            faces = faces[~degenerate]; birth_map = birth_map[~degenerate]
            used_geo = np.unique(faces); positions = positions[used_geo]
            faces = np.searchsorted(used_geo, faces).astype(np.int32)
        degenerates = 0 if clean_degenerate else original_degenerates
        edge_counts = {}; oriented = {}
        for face in faces:
            for a, b in zip(face, np.roll(face, -1)):
                key = tuple(sorted((int(a), int(b))))
                edge_counts[key] = edge_counts.get(key, 0)+1
                oriented[key] = oriented.get(key, 0)+(1 if a < b else -1)
        winding_conflicts = sum(edge_counts[e] == 2 and abs(oriented[e]) == 2 for e in edge_counts)
        edge_manifold = bool(igl.is_edge_manifold(faces)[0]) if len(faces) else True
        vertex_manifold = bool(np.all(igl.is_vertex_manifold(faces))) if len(faces) else True
        preparation.append(dict(vertices=len(positions), faces=len(faces), weldMaximumDistance=delta,
                                degenerates=int(degenerates), windingConflicts=winding_conflicts,
                                removedDegenerateFaces=original_degenerates if clean_degenerate else 0,
                                edgeManifold=edge_manifold, vertexManifold=vertex_manifold))
        meshes.append((positions, faces, birth_map))
    levels = []
    for level, target in enumerate(source['matchedTargets'], start=1):
        budgets = allocations([len(f) for _, f, _ in meshes], target)
        outputs = []; issues = []; elapsed = 0
        for slot, ((v, f, birth_map), budget, prep) in enumerate(zip(meshes, budgets, preparation)):
            if not len(f):
                outputs.append(dict(positions=[], triangles=[], birthFaces=[])); continue
            if not prep['edgeManifold'] or not prep['vertexManifold'] or prep['windingConflicts'] or prep['degenerates'] or prep['weldMaximumDistance'] > source['scale']*1e-5:
                issues.append(f"slot {slot}: QSlim input refused: {prep}")
                outputs.append(dict(positions=[], triangles=[], birthFaces=[])); continue
            started = time.perf_counter()
            u, g, birth, _ = igl.qslim(v, f, int(budget), bool(blocked))
            elapsed += (time.perf_counter()-started)*1000
            if not len(g) or not np.all(np.isfinite(u)):
                issues.append(f"slot {slot}: empty/invalid native result (binding does not return native success flag)")
            outputs.append(dict(positions=[dict(zip('xyz', map(float, p))) for p in u],
                                triangles=g.flatten().tolist(), birthFaces=birth_map[birth].tolist()))
        count = sum(len(s['triangles'])//3 for s in outputs)
        valid = not issues
        levels.append(dict(level=level, target=target, actual=count, valid=valid, budgetReached=valid and count <= target,
                           nativeMs=elapsed, error='; '.join(issues), submeshes=outputs))
    binaries = sorted(library.glob('**/*.pyd'))
    output = dict(fixture=source['fixture'], sourceSha256=source['sourceSha256'],
                  exportSha256=hashlib.sha256(source_path.read_bytes()).hexdigest(),
                  library=version, python=sys.version, numpy=np.__version__,
                  nativeBinaries={str(b.relative_to(library)):hashlib.sha256(b.read_bytes()).hexdigest() for b in binaries},
                  blockIntersections=bool(blocked), removeDegenerateFaces=bool(clean_degenerate), dimensions=3, preparation=preparation, levels=levels)
    output_path.write_text(json.dumps(output, indent=2), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--library', type=Path)
    parser.add_argument('--block-intersections', action='store_true')
    parser.add_argument('--worker', type=Path)
    parser.add_argument('--remove-degenerate-faces', action='store_true')
    parser.add_argument('--summarize-existing', action='store_true')
    args = parser.parse_args()
    directory = args.directory.resolve()
    if args.summarize_existing:
        summarize_existing(directory)
        return
    if args.library is None:
        parser.error('--library is required to run native QSlim jobs')
    if args.worker:
        worker(args.worker, directory/(args.worker.name.replace('-source.json', '-qslim.json')), args.library, args.block_intersections,args.remove_degenerate_faces)
        return
    failures = []
    for path in sorted(directory.glob('*-source.json')):
        command = [sys.executable, '-X', 'utf8', str(Path(__file__).resolve()), str(directory), '--library', str(args.library.resolve()), '--worker', str(path)]
        if args.block_intersections:
            command.append('--block-intersections')
        if args.remove_degenerate_faces:
            command.append('--remove-degenerate-faces')
        try:
            job = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', timeout=180)
            if job.returncode:
                raise RuntimeError(f"native child exit {job.returncode}: {job.stderr[-2000:]}")
            data = json.loads((directory/path.name.replace('-source.json', '-qslim.json')).read_text(encoding='utf-8'))
            print(path.stem, [(l['target'], l['actual'], l['valid']) for l in data['levels']], flush=True)
        except (RuntimeError, subprocess.TimeoutExpired) as error:
            failures.append(dict(fixture=path.stem, error=str(error)))
            print(path.stem, str(error), flush=True)
    (directory/'qslim-job-failures.json').write_text(json.dumps(failures, indent=2), encoding='utf-8')
    if failures:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
