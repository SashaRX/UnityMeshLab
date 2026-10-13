"""Source-fan-aware boundary extraction; no Cap generation or source mutation.

Halfedge IDs are face*3+corner and follow source face winding. Analysis welds
exact positions virtually, while retaining original vertex/corner references.
Extraction can succeed on singular vertices; that does not certify a solid or
authorize treating the resulting loops as independent simple Cap polygons.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path

import numpy as np

from analyze import read_mesh
from intersections import cross, exact_positions, subtract


class Links:
    def __init__(self, count):
        self.parent = list(range(count))

    def root(self, item):
        while self.parent[item] != item:
            self.parent[item] = self.parent[self.parent[item]]
            item = self.parent[item]
        return item

    def join(self, first, second):
        a, b = self.root(first), self.root(second)
        self.parent[max(a, b)] = min(a, b)


def next_corner(halfedge):
    return halfedge // 3 * 3 + (halfedge + 1) % 3


def snapshot_hash(positions, indices):
    digest = hashlib.sha256()
    for array in (positions, indices):
        dtype = array.dtype.newbyteorder('<')
        digest.update(f'{dtype.str}:{array.shape};'.encode('ascii'))
        digest.update(array.astype(dtype, copy=False).tobytes(order='C'))
    return digest.hexdigest()


def checked_arrays(positions, triangles):
    p, ix = np.asarray(positions), np.asarray(triangles)
    if p.ndim != 2 or p.shape[1] != 3 or p.dtype.kind != 'f' or p.dtype.itemsize not in (4, 8):
        raise ValueError('Positions must be an Nx3 float32/float64 array')
    if ix.ndim != 2 or ix.shape[1] != 3 or ix.dtype.kind not in 'iu':
        raise ValueError('Triangles must be an Mx3 integer array')
    if not np.isfinite(p).all() or (ix.size and (ix.min() < 0 or ix.max() >= len(p))):
        raise ValueError('Non-finite positions or invalid triangle indices')
    return p, ix


def extract(positions, triangles, max_halfedges=3_000_000, cancel=None):
    p, raw = checked_arrays(positions, triangles)
    if type(max_halfedges) is not int or max_halfedges < 0:
        raise ValueError('Halfedge budget must be a nonnegative integer')
    report = {'schemaVersion': 1, 'sourceHash': None,
              'sourceVertices': len(p), 'sourceFaces': len(raw),
              'extractionAccepted': False, 'loops': []}

    def refused(reason):
        report['refusal'] = reason
        return report

    def stopped():
        return cancel is not None and cancel()

    if raw.size > max_halfedges:
        return refused('halfedge_budget_exceeded')
    if stopped():
        return refused('cancelled')
    report['sourceHash'] = snapshot_hash(p, raw)
    points, slots = np.unique(p, axis=0, return_inverse=True)
    indices = slots[raw]
    exact = exact_positions(points)[0] if len(points) else []
    flat, raw_flat = indices.ravel(), raw.ravel()
    edges, face_keys = defaultdict(list), {}
    defects = {'duplicateFaces': [], 'degenerateFaces': [],
               'nonManifoldEdges': [], 'windingEdges': []}
    for face, triangle in enumerate(indices):
        if face % 256 == 0 and stopped():
            return refused('cancelled')
        a, b, c = (exact[int(v)] for v in triangle)
        if cross(subtract(b, a), subtract(c, a)) == (0, 0, 0):
            defects['degenerateFaces'].append(face)
        key = tuple(sorted(map(int, triangle)))
        if key in face_keys:
            defects['duplicateFaces'].append([face_keys[key], face])
        else:
            face_keys[key] = face
        for corner in range(3):
            h = face * 3 + corner
            edges[tuple(sorted((int(flat[h]), int(flat[next_corner(h)]))))].append(h)
    for key, incident in edges.items():
        if len(incident) > 2:
            defects['nonManifoldEdges'].append({'slots': list(key), 'halfedges': incident})
        elif len(incident) == 2 and flat[incident[0]] == flat[incident[1]]:
            defects['windingEdges'].append({'slots': list(key), 'halfedges': incident})
    report['geometricVertices'] = len(points)
    report['sourceDefects'] = defects
    if any(defects.values()):
        return refused('source_edge_or_face_defects')

    fans, components = Links(raw.size), Links(len(raw))
    twins, boundary = {}, set()
    for edge_number, incident in enumerate(edges.values()):
        if edge_number % 256 == 0 and stopped():
            return refused('cancelled')
        if len(incident) == 1:
            boundary.add(incident[0])
            continue
        h, g = incident
        twins[h], twins[g] = g, h
        fans.join(h, next_corner(g))
        fans.join(next_corner(h), g)
        components.join(h // 3, g // 3)
    fan_sets = defaultdict(set)
    for corner, vertex in enumerate(flat):
        fan_sets[int(vertex)].add(fans.root(corner))
    singular = {v: sorted(links) for v, links in fan_sets.items() if len(links) > 1}
    singular_slots = set(singular)
    report['singularVertices'] = [{'slot': v, 'position': points[v].tolist(), 'fans': singular[v]}
                                  for v in sorted(singular)]
    report['boundaryEdges'] = len(boundary)
    successors = {}
    # Rotate through the source face fan at the target endpoint. Each step
    # crosses an interior edge and remains in that fan; no projected ordering.
    work = 0
    for boundary_number, h in enumerate(sorted(boundary)):
        if boundary_number % 256 == 0 and stopped():
            return refused('cancelled')
        following = next_corner(h)
        visited = set()
        while following not in boundary:
            work += 1
            if work > max_halfedges:
                return refused('traversal_budget_exceeded')
            if work % 256 == 0 and stopped():
                return refused('cancelled')
            if following in visited or following not in twins:
                return refused('boundary_fan_does_not_reach_boundary')
            visited.add(following)
            following = next_corner(twins[following])
        if flat[next_corner(h)] != flat[following] or fans.root(next_corner(h)) != fans.root(following):
            return refused('boundary_fan_endpoint_mismatch')
        successors[h] = following
    if set(successors.values()) != boundary or len(set(successors.values())) != len(boundary):
        return refused('boundary_successor_is_not_a_permutation')
    remaining = set(boundary)
    loops = []
    for start in sorted(boundary):
        if start not in remaining:
            continue
        if stopped():
            return refused('cancelled')
        ordered = []
        h = start
        while h in remaining:
            if len(ordered) % 256 == 0 and stopped():
                return refused('cancelled')
            remaining.remove(h)
            ordered.append(h)
            h = successors[h]
        if h != start:
            return refused('boundary_walk_does_not_close')
        occurrences = []
        for h in ordered:
            end = next_corner(h)
            occurrences.append({'halfedge': h, 'face': h // 3, 'corner': h % 3,
                                'sourceVertices': [int(raw_flat[h]), int(raw_flat[end])],
                                'slots': [int(flat[h]), int(flat[end])],
                                'sourceFans': [fans.root(h), fans.root(end)],
                                'successor': successors[h]})
        loop_slots = [int(flat[h]) for h in ordered]
        loop_hash = hashlib.sha256(bytes.fromhex(report['sourceHash']) +
                                   np.asarray(ordered, dtype='<u8').tobytes()).hexdigest()
        junctions = sorted(set(loop_slots) & singular_slots)
        loops.append({'id': loop_hash, 'component': components.root(start // 3),
                      'slots': loop_slots, 'halfedges': occurrences,
                      'junctionSlots': junctions, 'requiresJunctionResolution': bool(junctions)})
    if stopped():
        return refused('cancelled')
    report.update({'extractionAccepted': True, 'loops': loops,
                   'boundaryHalfedges': sorted(boundary),
                   'orientation': 'source_face_winding; Cap must oppose selected edges'})
    return report


def select_domains(report, loop_ids=None, source_hash=None):
    if not report['extractionAccepted']:
        raise ValueError('Cannot select domains from a refused extraction')
    if source_hash is not None and source_hash != report['sourceHash']:
        raise ValueError('Boundary selection belongs to a different source snapshot')
    available = {loop['id']: loop for loop in report['loops']}
    ids = sorted(available) if loop_ids is None else list(loop_ids)
    if any(type(identity) is not str for identity in ids):
        raise ValueError('Boundary loop identities must be strings')
    if len(set(ids)) != len(ids) or any(identity not in available for identity in ids):
        raise ValueError('Duplicate or unknown boundary loop identity')
    selected = sorted(h['halfedge'] for identity in ids for h in available[identity]['halfedges'])
    return {'sourceHash': report['sourceHash'], 'loopIds': sorted(ids),
            'selectedHalfedges': selected,
            'unselectedHalfedges': sorted(set(report['boundaryHalfedges']) - set(selected)),
            'requiresJunctionResolution': any(available[identity]['requiresJunctionResolution'] for identity in ids)}


def validate_coverage(report, selection, supplied_halfedges):
    expected = select_domains(report, selection['loopIds'], selection['sourceHash'])
    supplied_halfedges = list(supplied_halfedges)
    if any(type(h) is not int for h in supplied_halfedges):
        raise ValueError('Boundary occurrences must be integer halfedge IDs')
    if expected != selection or Counter(supplied_halfedges) != Counter(expected['selectedHalfedges']):
        raise ValueError('Selected directed boundary occurrences must be covered exactly once')
    return expected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--max-halfedges', type=int, default=3_000_000)
    parser.add_argument('--select', action='append', help='Snapshot-bound loop ID; omitted selects all')
    args = parser.parse_args()
    report = extract(*read_mesh(args.source), max_halfedges=args.max_halfedges)
    if report['extractionAccepted']:
        report['selection'] = select_domains(report, args.select)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({'extractionAccepted': report['extractionAccepted'],
                      'boundaryEdges': report.get('boundaryEdges'), 'loops': len(report['loops']),
                      'singularVertices': len(report.get('singularVertices', [])),
                      'refusal': report.get('refusal')}, indent=2))
    return 0 if report['extractionAccepted'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
