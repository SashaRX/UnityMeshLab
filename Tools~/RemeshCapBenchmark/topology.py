"""Offline geometric connectivity; acceptance stays separate from intersections."""
from collections import Counter, defaultdict

import numpy as np

from intersections import cross, exact_positions, subtract


def inspect(positions, triangles):
    points, slots = np.unique(positions, axis=0, return_inverse=True)
    indices = slots[triangles]
    exact, _ = exact_positions(points)
    faces = Counter(tuple(sorted(map(int, t))) for t in indices)
    edges = defaultdict(list)
    balance = Counter()
    for face, triangle in enumerate(indices):
        for a, b in zip(triangle, np.roll(triangle, -1)):
            a, b = int(a), int(b)
            key = tuple(sorted((a, b)))
            edges[key].append(face)
            balance[key] += 1 if a < b else -1
    parent = list(range(indices.size))

    def root(corner):
        while parent[corner] != corner:
            parent[corner] = parent[parent[corner]]
            corner = parent[corner]
        return corner

    for (a, b), adjacent in edges.items():
        if len(adjacent) != 2:
            continue
        first, second = adjacent
        for vertex in (a, b):
            x = first*3 + int(np.flatnonzero(indices[first] == vertex)[0])
            y = second*3 + int(np.flatnonzero(indices[second] == vertex)[0])
            parent[root(x)] = root(y)
    fans = defaultdict(set)
    for corner, vertex in enumerate(indices.ravel()):
        fans[int(vertex)].add(root(corner))
    bad_vertices = [v for v, links in fans.items() if len(links) > 1]
    degenerate = 0
    for triangle in indices:
        a, b, c = (exact[int(v)] for v in triangle)
        degenerate += cross(subtract(b, a), subtract(c, a)) == (0, 0, 0)
    report = {
        'boundaryEdges': sum(len(v) == 1 for v in edges.values()),
        'duplicateFaces': sum(v-1 for v in faces.values()),
        'degenerateFaces': degenerate,
        'nonManifoldEdges': sum(len(v) > 2 for v in edges.values()),
        'windingEdges': sum(len(v) == 2 and balance[k] != 0 for k, v in edges.items()),
        'disconnectedVertexFans': len(bad_vertices),
        'disconnectedFanPositions': points[bad_vertices].tolist(),
    }
    report['closedManifold'] = not any(report[k] for k in (
        'boundaryEdges', 'duplicateFaces', 'degenerateFaces', 'nonManifoldEdges',
        'windingEdges', 'disconnectedVertexFans'))
    return report
