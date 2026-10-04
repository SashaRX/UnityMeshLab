"""Compare UV cuts in 3D on identical geometry, independently of island count."""
from collections import defaultdict
from pathlib import Path
import struct

import numpy as np


def read_capture(path):
    data = Path(path).read_bytes()
    if len(data) < 12:
        raise ValueError(f"Truncated geometry capture: {path}")
    nv, ni, nc = struct.unpack_from("<3i", data)
    if nv <= 0 or ni <= 0 or ni % 3 or len(data) != 12 + nv * 24 + ni * 4:
        raise ValueError(f"Invalid geometry capture: {path}")
    positions = np.frombuffer(data, "<f4", nv * 3, 12).reshape(-1, 3).astype(float)
    uv = np.frombuffer(data, "<f4", nv * 2, 12 + nv * 12).reshape(-1, 2).astype(float)
    faces = np.frombuffer(data, "<i4", ni, 12 + nv * 20).reshape(-1, 3)
    if not np.isfinite(positions).all() or not np.isfinite(uv).all():
        raise ValueError("Non-finite geometry")
    if faces.min() < 0 or faces.max() >= nv:
        raise ValueError("Invalid index")
    charts = np.frombuffer(data, "<i4", nv, 12 + nv * 20 + ni * 4)
    return positions, uv, faces, charts, nc


def topology(capture):
    positions, uv, faces, charts, _ = capture
    welded, slots = np.unique(positions, axis=0, return_inverse=True)
    triangles = slots[faces]
    if np.any(np.diff(np.sort(triangles, axis=1), axis=1) == 0):
        raise ValueError("Degenerate welded face")
    edges = defaultdict(list)
    for face, corners in enumerate(faces):
        for a, b in zip(corners, np.roll(corners, -1)):
            if slots[a] > slots[b]:
                a, b = b, a
            edges[(int(slots[a]), int(slots[b]))].append((face, uv[[a, b]]))
    keys = sorted(edges)
    if any(len(edges[k]) != 2 for k in keys):
        raise ValueError("Benchmark expects a closed manifold input")
    pairs = np.array([[v[0][0], v[1][0]] for v in (edges[k] for k in keys)])
    cut = np.array([np.max(np.abs(edges[k][0][1] - edges[k][1][1])) > 1e-7 for k in keys])
    return welded, triangles, np.array(keys), pairs, cut


def canonical_faces(faces):
    # Check connectivity as well as positions, even if output vertices/faces reorder.
    return sorted(map(tuple, np.sort(faces, axis=1)))


def align(capture, reference):
    positions, faces, edges, _, cut = topology(capture)
    rp, rf, re, _, _ = reference
    if not np.array_equal(positions, rp) or canonical_faces(faces) != canonical_faces(rf):
        raise ValueError("Cannot compare cuts: geometry/connectivity differ")
    if not np.array_equal(edges, re):
        raise ValueError("Edge correspondence failed")
    return cut


def exact_scores(predicted, expected, lengths):
    tp = lengths[predicted & expected].sum()
    predicted_length = lengths[predicted].sum()
    expected_length = lengths[expected].sum()
    precision = tp / predicted_length if predicted_length else 0.0
    recall = tp / expected_length if expected_length else 0.0
    return dict(precision=float(precision), recall=float(recall),
                f1=float(2 * precision * recall / (precision + recall)) if precision + recall else 0.0,
                missingLength=float(lengths[expected & ~predicted].sum()),
                extraLength=float(lengths[predicted & ~expected].sum()))


def sample_distances(positions, edges, lengths, predicted, expected):
    """Length-weighted segment quadrature, exact point-to-target-segment distance.

    Avoid the misleading nearest-midpoint measure on uneven triangulations.
    Samples include nine uniformly spaced interior points per edge; tolerance
    coverage is approximate, whereas each sampled distance is exact Euclidean.
    """
    if not predicted.any():
        return np.empty(0), np.empty(0)
    if not expected.any():
        return np.full(predicted.sum() * 9, np.inf), np.repeat(lengths[predicted] / 9, 9)
    a, b = positions[edges[predicted, 0]], positions[edges[predicted, 1]]
    t = (np.arange(9) + 0.5) / 9
    points = (a[:, None, :] * (1 - t[None, :, None]) + b[:, None, :] * t[None, :, None]).reshape(-1, 3)
    ta, tb = positions[edges[expected, 0]], positions[edges[expected, 1]]
    delta = tb - ta
    denominator = np.einsum("ij,ij->i", delta, delta)
    distances = []
    for start in range(0, len(points), 256):
        relative = points[start:start + 256, None, :] - ta[None, :, :]
        along = np.clip(np.einsum("qsi,si->qs", relative, delta) / denominator, 0, 1)
        residual = relative - along[:, :, None] * delta[None, :, :]
        distances.extend(np.sqrt(np.einsum("qsi,qsi->qs", residual, residual).min(axis=1)))
    return np.asarray(distances), np.repeat(lengths[predicted] / 9, 9)


def graph_stats(edges, cut):
    adjacency = defaultdict(set)
    for a, b in edges[cut]:
        adjacency[int(a)].add(int(b))
        adjacency[int(b)].add(int(a))
    seen = set()
    components = 0
    for vertex in adjacency:
        if vertex in seen:
            continue
        components += 1
        stack = [vertex]
        while stack:
            vertex = stack.pop()
            if vertex not in seen:
                seen.add(vertex)
                stack.extend(adjacency[vertex] - seen)
    return dict(cutEdges=int(cut.sum()), components=components,
                endpoints=sum(len(v) == 1 for v in adjacency.values()),
                junctions=sum(len(v) > 2 for v in adjacency.values()))
