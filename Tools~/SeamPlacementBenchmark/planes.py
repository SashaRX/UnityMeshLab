"""Unsupervised structural planar-region proposals, independent of face density.

Large connected patches must satisfy both a normal cone and a plane-distance
bound. Unclaimed curved surface is kept connected, not split into many tiny
normal clusters. Boundaries are proposals: a UV solver must add necessary cuts
and validate the final atlas. Artist labels never enter this algorithm.
"""
import argparse
import json
from pathlib import Path

import numpy as np

from relocate import split_components
from seams import read_capture, topology


def structural_regions(positions, faces, pairs, angle, distance, min_area_fraction):
    triangles = positions[faces]
    cross = np.cross(triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0])
    areas = np.linalg.norm(cross, axis=1) / 2
    normals = cross / (areas * 2)[:, None]
    centers = triangles.mean(axis=1)
    adjacency = [[] for _ in faces]
    for a, b in pairs:
        adjacency[a].append(int(b))
        adjacency[b].append(int(a))
    order = np.argsort(-areas, kind="stable")
    labels = np.full(len(faces), -1, dtype=int)
    cosine = np.cos(np.radians(angle))
    plane_bound = np.linalg.norm(np.ptp(positions, axis=0)) * distance
    patch = 0
    for seed in order:
        if labels[seed] >= 0:
            continue
        compatible = (normals @ normals[seed] >= cosine)
        compatible &= np.abs((triangles - centers[seed]) @ normals[seed]).max(axis=1) <= plane_bound
        compatible &= labels < 0
        seen = {int(seed)}
        stack = [int(seed)]
        while stack:
            face = stack.pop()
            for neighbor in adjacency[face]:
                if compatible[neighbor] and neighbor not in seen:
                    seen.add(neighbor)
                    stack.append(neighbor)
        members = sorted(seen)
        if areas[members].sum() >= areas.sum() * min_area_fraction:
            labels[members] = patch
            patch += 1
    labels[labels < 0] = patch
    return split_components(labels, pairs)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    p, faces, edges, pairs, _ = topology(read_capture(args.reference))
    args.out.mkdir(parents=True, exist_ok=True)
    cuts, rows = {}, []
    for angle in (5, 10, 20):
        for distance in (0.001, 0.003, 0.01):
            for min_area in (0.005, 0.02):
                name = f"planes-{angle}-{distance:g}-{min_area:g}"
                labels = structural_regions(p, faces, pairs, angle, distance, min_area)
                cuts[name] = labels[pairs[:, 0]] != labels[pairs[:, 1]]
                np.save(args.out / (name + "-labels.npy"), labels)
                rows.append(dict(method=name, normalConeDegrees=angle, maxPlaneDistanceFraction=distance,
                                 minAreaFraction=min_area, connectedRegions=int(labels.max() + 1)))
                print(rows[-1], flush=True)
    np.savez(args.out / "plane-prototypes.npz", **cuts)
    (args.out / "planes.json").write_text(json.dumps(rows, indent=2))


if __name__ == "__main__":
    main()
