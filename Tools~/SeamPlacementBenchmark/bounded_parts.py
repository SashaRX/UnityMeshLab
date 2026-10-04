"""Automatic semantic partitions bounded by surface area, with structural cuts.

Uses the official PartUV hierarchy from frozen PartField features; no artist UV
or SG labels. Area rather than face count prevents highly triangulated curved
regions from dominating the split. Existing geometric planar boundaries remain
mandatory. The result is a region proposal for a separate parameterizer.
"""
import argparse
import json
from pathlib import Path

import numpy as np

from relocate import split_components
from seams import read_capture, topology


def partition(tree, areas, fraction):
    count = len(areas)
    if not 0 < fraction <= 1:
        raise ValueError("Invalid area fraction")
    accumulated = {i: float(area) for i, area in enumerate(areas)}
    for key in sorted(tree):
        accumulated[key] = accumulated[tree[key]["left"]] + accumulated[tree[key]["right"]]
    labels = np.full(count, -1, dtype=int)
    selected = []
    stack = [max(tree)]
    while stack:
        node = stack.pop()
        if node in tree and accumulated[node] > areas.sum() * fraction:
            stack.extend([tree[node]["right"], tree[node]["left"]])
            continue
        selected.append(node)
        pending = [node]
        while pending:
            current = pending.pop()
            if current in tree:
                pending.extend([tree[current]["left"], tree[current]["right"]])
            else:
                labels[current] = len(selected)-1
    if (labels < 0).any():
        raise ValueError("Incomplete hierarchy")
    return labels


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--tree", type=Path, required=True)
    parser.add_argument("--structural", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    p, faces, edges, pairs, _ = topology(read_capture(args.reference))
    triangle = p[faces]
    area = np.linalg.norm(np.cross(triangle[:,1]-triangle[:,0], triangle[:,2]-triangle[:,0]), axis=1)/2
    tree = {int(k): v for k, v in json.loads(args.tree.read_text()).items()}
    structural = np.load(args.structural, allow_pickle=False)
    if structural.shape != (len(faces),):
        raise ValueError("Structural label mismatch")
    args.out.mkdir(parents=True, exist_ok=True)
    report, cuts = [], {}
    for fraction in (0.15, 0.2, 0.3):
        semantic = partition(tree, area, fraction)
        _, labels = np.unique(np.column_stack((semantic, structural)), axis=0, return_inverse=True)
        labels = split_components(labels, pairs)
        name = f"bounded-parts-{fraction:g}"
        np.save(args.out / (name + "-labels.npy"), labels)
        cuts[name] = labels[pairs[:,0]] != labels[pairs[:,1]]
        region_area = np.bincount(labels, weights=area)
        row = dict(method=name, maxAreaFraction=fraction, regions=int(labels.max()+1),
                   largestAreaFraction=float(region_area.max()/area.sum()),
                   leafExceptions=int((region_area > area.sum()*fraction).sum()))
        report.append(row)
        print(row, flush=True)
    np.savez(args.out / "bounded-prototypes.npz", **cuts)
    (args.out / "bounded-parts.json").write_text(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
