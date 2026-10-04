"""Experimental graph-cut relocation of chart boundaries, not a UV generator.

The energy is area-weighted normal-proxy error plus feature-weighted boundary
length. Alpha expansion solves binary moves on the entire face adjacency graph;
neither artist seam labels nor source classifiers trained on this asset are used.
Disk topology, flattening distortion and overlaps must be validated separately.
"""
import argparse
import json
from pathlib import Path

import numpy as np
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components, dijkstra, maximum_flow

from seams import canonical_faces, read_capture, topology


def binary_cut(cost0, cost1, pairs, pair_weights):
    count = len(cost0)
    shift = np.minimum(cost0, cost1)
    cost0, cost1 = cost0 - shift, cost1 - shift
    a, b = pairs.T
    source, sink = count, count + 1
    nodes = np.arange(count)
    rows = np.concatenate((np.full(count, source), nodes, a, b))
    cols = np.concatenate((nodes, np.full(count, sink), b, a))
    values = np.concatenate((cost1, cost0, pair_weights, pair_weights))
    scale = 1e8 / max(values.max(), 1e-12)
    capacities = np.rint(values * scale).astype(np.int64)
    graph = coo_matrix((capacities, (rows, cols)), shape=(count + 2, count + 2)).tocsr()
    flow = maximum_flow(graph, source, sink)
    residual = (graph - flow.flow).tocsr()
    seen = np.zeros(count + 2, dtype=bool)
    seen[source] = True
    stack = [source]
    while stack:
        vertex = stack.pop()
        start, end = residual.indptr[vertex:vertex + 2]
        for neighbor, capacity in zip(residual.indices[start:end], residual.data[start:end]):
            if capacity > 0 and not seen[neighbor]:
                seen[neighbor] = True
                stack.append(int(neighbor))
    return ~seen[:count]


def labeling_energy(labels, unary, pairs, weights):
    return float(unary[np.arange(len(labels)), labels].sum() + weights[labels[pairs[:, 0]] != labels[pairs[:, 1]]].sum())


def expansion(labels, unary, pairs, weights):
    """One deterministic alpha-expansion sweep for the metric Potts energy."""
    labels = labels.copy()
    energies = [labeling_energy(labels, unary, pairs, weights)]
    a, b = pairs.T
    for alpha in range(unary.shape[1]):
        if not np.any(labels == alpha):
            continue
        left, right = labels[a], labels[b]
        e00 = weights * (left != right)
        e01 = weights * (left != alpha)
        e10 = weights * (right != alpha)
        link = (e01 + e10 - e00) * 0.5
        if link.min(initial=0) < -1e-12:
            raise ValueError("Non-submodular move")
        d0 = unary[np.arange(len(labels)), labels].copy()
        d1 = unary[:, alpha].copy()
        np.add.at(d1, a, e10 - e00 - link)
        np.add.at(d1, b, e01 - e00 - link)
        switch = binary_cut(d0, d1, pairs, np.maximum(link, 0))
        candidate = labels.copy()
        candidate[switch] = alpha
        energy = labeling_energy(candidate, unary, pairs, weights)
        # Integer flow rounding may propose a tiny non-improvement. Accept only
        # a verified decrease in the original floating-point objective.
        if energy < energies[-1] - 1e-12:
            labels = candidate
            energies.append(energy)
    return labels, energies


def split_components(labels, pairs):
    count = len(labels)
    keep = labels[pairs[:, 0]] == labels[pairs[:, 1]]
    a, b = pairs[keep].T
    graph = coo_matrix((np.ones(len(a) * 2), (np.r_[a, b], np.r_[b, a])), shape=(count, count)).tocsr()
    _, components = connected_components(graph, directed=False)
    return components


def proxy_cost(normals, areas, labels, count):
    total = np.zeros((count, 3))
    np.add.at(total, labels, normals * areas[:, None])
    norm = np.linalg.norm(total, axis=1)
    valid = norm > 1e-12
    total[valid] /= norm[valid, None]
    cost = areas[:, None] * (2 - 2 * np.clip(normals @ total.T, -1, 1))
    # A vanished region has no valid proxy and cannot be resurrected arbitrarily.
    cost[:, ~valid] = areas[:, None] * 1e6
    return cost


def relocate(positions, faces, pairs, edges, initial, strength, feature_angle, iterations, spatial_weight=0):
    triangles = positions[faces]
    cross = np.cross(triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0])
    double_area = np.linalg.norm(cross, axis=1)
    if (double_area <= 0).any():
        raise ValueError("Degenerate input")
    normals = cross / double_area[:, None]
    areas = double_area * 0.5
    lengths = np.linalg.norm(positions[edges[:, 0]] - positions[edges[:, 1]], axis=1)
    angle = np.arccos(np.clip(np.einsum("ij,ij->i", normals[pairs[:, 0]], normals[pairs[:, 1]]), -1, 1))
    feature = np.exp(-(angle / np.radians(feature_angle)) ** 2)
    weights = strength * lengths * (0.05 + 0.95 * feature)
    labels = initial.copy()
    count = labels.max() + 1
    spatial = np.zeros((len(faces), count))
    seeds = []
    if spatial_weight > 0:
        centers = triangles.mean(axis=1)
        for label in range(count):
            members = np.flatnonzero(initial == label)
            centroid = np.average(centers[members], axis=0, weights=areas[members])
            seeds.append(int(members[np.linalg.norm(centers[members] - centroid, axis=1).argmin()]))
        distance = np.linalg.norm(centers[pairs[:, 0]] - centers[pairs[:, 1]], axis=1)
        a, b = pairs.T
        graph = coo_matrix((np.r_[distance, distance], (np.r_[a, b], np.r_[b, a])), shape=(len(faces), len(faces))).tocsr()
        spatial = areas[:, None] * spatial_weight * dijkstra(graph, directed=False, indices=seeds).T ** 2
    history = []
    for _ in range(iterations):
        unary = proxy_cost(normals, areas, labels, count) + spatial
        for label, seed in enumerate(seeds):
            unary[seed] = 1e3
            unary[seed, label] = 0
        moved, energy = expansion(labels, unary, pairs, weights)
        history.append(dict(before=energy[0], after=energy[-1], moves=len(energy) - 1,
                            changedFaces=int((moved != labels).sum())))
        if np.array_equal(moved, labels):
            break
        labels = moved
    labels = split_components(labels, pairs)
    return labels, history


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--initial", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--iterations", type=int, default=8)
    args = parser.parse_args()
    reference = topology(read_capture(args.reference))
    p, faces, edges, pairs, _ = reference
    capture = read_capture(args.initial)
    ap, af, _, _, _ = topology(capture)
    if not np.array_equal(p, ap) or canonical_faces(faces) != canonical_faces(af):
        raise ValueError("Different geometry")
    lookup = {tuple(sorted(f)): int(capture[3][capture[2][i, 0]]) for i, f in enumerate(af)}
    initial = np.array([lookup[tuple(sorted(f))] for f in faces])
    if any(len(set(capture[3][corners])) != 1 for corners in capture[2]):
        raise ValueError("Input chart crosses a face")
    _, initial = np.unique(initial, return_inverse=True)
    args.out.mkdir(parents=True, exist_ok=True)
    cuts, report = {}, []
    for spatial_weight, strength, feature_angle in (
        (s, w, a) for s in (0, 25, 100)
        for w in (0.001, 0.003, 0.01, 0.03) for a in (20, 45)
    ):
        name = f"relocate-{strength:g}-{feature_angle}-spatial{spatial_weight}"
        labels, history = relocate(p, faces, pairs, edges, initial, strength, feature_angle, args.iterations, spatial_weight)
        cuts[name] = labels[pairs[:, 0]] != labels[pairs[:, 1]]
        np.save(args.out / (name + "-labels.npy"), labels)
        row = dict(method=name, strength=strength, featureAngle=feature_angle, spatialWeight=spatial_weight, history=history,
                   connectedRegions=int(labels.max() + 1), cutEdges=int(cuts[name].sum()))
        report.append(row)
        print(name, row["connectedRegions"], row["cutEdges"], history[-1], flush=True)
    np.savez(args.out / "prototypes.npz", **cuts)
    (args.out / "relocation.json").write_text(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
