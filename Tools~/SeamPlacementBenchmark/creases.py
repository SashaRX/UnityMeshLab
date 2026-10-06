"""Geometry-only crease components, not smoothing-group or artist-label inputs.

Threshold proposals may contain dangling cuts. Region proposals retain only
boundaries separating connected face components; a UV solver can add more cuts.
"""
import argparse
import json
from pathlib import Path

import numpy as np

from relocate import split_components
from seams import read_capture, topology


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    p, faces, edges, pairs, _ = topology(read_capture(args.reference))
    cross = np.cross(p[faces[:, 1]] - p[faces[:, 0]], p[faces[:, 2]] - p[faces[:, 0]])
    normal = cross / np.linalg.norm(cross, axis=1)[:, None]
    angles = np.degrees(np.arccos(np.clip(np.einsum("ij,ij->i", normal[pairs[:, 0]], normal[pairs[:, 1]]), -1, 1)))
    cuts, report = {}, []
    args.out.mkdir(parents=True, exist_ok=True)
    for threshold in (20, 30, 40, 45, 50, 60, 75):
        selected = angles >= threshold
        cuts[f"crease-threshold-{threshold}"] = selected
        labels = split_components(np.zeros(len(faces), dtype=int), pairs[~selected])
        name = f"crease-regions-{threshold}"
        cuts[name] = labels[pairs[:, 0]] != labels[pairs[:, 1]]
        np.save(args.out / (name + "-labels.npy"), labels)
        report.append(dict(method=name, threshold=threshold, regions=int(labels.max() + 1)))
    np.savez(args.out / "crease-prototypes.npz", **cuts)
    (args.out / "creases.json").write_text(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
