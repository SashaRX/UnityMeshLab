"""python compare.py --v1 DIR --v2 DIR --labels edges.json --out DIR"""
import argparse
import csv
import hashlib
import json
from pathlib import Path

import numpy as np

from seams import align, exact_scores, graph_stats, read_capture, sample_distances, topology


def compare(cuts, reference, hard):
    positions, _, edges, _, _ = reference
    lengths = np.linalg.norm(positions[edges[:, 1]] - positions[edges[:, 0]], axis=1)
    rows = []
    for name, predicted in cuts.items():
        for label in ("artist-v1", "artist-v2", "artist-shared"):
            expected = cuts[label]
            row = dict(method=name, reference=label, **exact_scores(predicted, expected, lengths))
            distances, weights = sample_distances(positions, edges, lengths, predicted, expected)
            reverse, reverse_weights = sample_distances(positions, edges, lengths, expected, predicted)
            diagonal = np.linalg.norm(np.ptp(positions, axis=0))
            for fraction in (0.0025, 0.01):
                suffix = str(fraction * 100).replace(".", "_")
                row["nearPrecision" + suffix] = float(weights[distances <= diagonal * fraction].sum() / weights.sum()) if weights.sum() else 0.0
                row["nearRecall" + suffix] = float(reverse_weights[reverse <= diagonal * fraction].sum() / reverse_weights.sum()) if reverse_weights.sum() else 0.0
            row["hardRecall"] = float(lengths[predicted & hard].sum() / lengths[hard].sum())
            row["hardEdgesMissed"] = int((hard & ~predicted).sum())
            row["cutLength"] = float(lengths[predicted].sum())
            row.update(graph_stats(edges, predicted))
            rows.append(row)
    return rows


def plot(cuts, reference, out, names):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from mpl_toolkits.mplot3d.art3d import Line3DCollection, Poly3DCollection
    positions, faces, edges, _, _ = reference
    # Common display rotation only; measured geometry and edge correspondence
    # stay in the capture coordinate system. The reference bust is X-long.
    vertical = int(np.ptp(positions, axis=0).argmax())
    horizontal = [i for i in range(3) if i != vertical]
    positions = positions[:, horizontal + [vertical]]
    fig = plt.figure(figsize=(4 * len(names), 8))
    extent = np.ptp(positions, axis=0).max() * 0.53
    center = (positions.max(0) + positions.min(0)) / 2
    for row, azimuth in enumerate((55, 235)):
        for col, name in enumerate(names):
            ax = fig.add_subplot(2, len(names), row * len(names) + col + 1, projection="3d")
            ax.add_collection3d(Poly3DCollection(positions[faces], facecolors="#dedede", edgecolors="none", alpha=0.65))
            ax.add_collection3d(Line3DCollection(positions[edges[cuts[name]]], colors="#a32648", linewidths=1.1))
            for axis, c in zip((ax.set_xlim, ax.set_ylim, ax.set_zlim), center):
                axis(c - extent, c + extent)
            ax.set_box_aspect((1, 1, 1))
            ax.view_init(elev=18, azim=azimuth, vertical_axis="z")
            ax.set_axis_off()
            if row == 0:
                ax.set_title(name)
    fig.suptitle("Seam placement on identical geometry — two opposite views", fontsize=15)
    fig.tight_layout()
    fig.savefig(out, dpi=160)
    plt.close(fig)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--v1", type=Path, required=True)
    parser.add_argument("--v2", type=Path, required=True)
    parser.add_argument("--labels", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--prototype", type=Path)
    parser.add_argument("--extra", action="append", default=[], metavar="NAME=CAPTURE")
    parser.add_argument("--plot", nargs="+", default=["artist-v1", "artist-v2", "xatlas", "merge"])
    args = parser.parse_args()
    reference = topology(read_capture(args.v1 / "reference.bin"))
    cuts = {"artist-v1": reference[4], "artist-v2": align(read_capture(args.v2 / "reference.bin"), reference)}
    cuts["artist-shared"] = cuts["artist-v1"] & cuts["artist-v2"]
    for name, file in (("xatlas", "auto-plain.bin"), ("merge", "auto-merge.bin")):
        cuts[name] = align(read_capture(args.v1 / file), reference)
    labels = json.loads(args.labels.read_text())
    if not np.array_equal(reference[2], [[r["a"], r["b"]] for r in labels]):
        raise ValueError("Label edge correspondence failed")
    for name, key in (("artist-v1", "seamV1"), ("artist-v2", "seamV2")):
        if not np.array_equal(cuts[name], [bool(r[key]) for r in labels]):
            raise ValueError("Reference labels differ from actual UV continuity")
    hard = np.array([bool(r["hard"]) for r in labels])
    cuts["crease-60"] = np.array([r["lowAngle"] >= 60 for r in labels])
    if args.prototype:
        prototypes = np.load(args.prototype, allow_pickle=False)
        for name in prototypes.files:
            if name in cuts or prototypes[name].shape != reference[4].shape or prototypes[name].dtype != bool:
                raise ValueError("Invalid proposal shape, type or duplicate name")
            cuts[name] = prototypes[name]
    for extra in args.extra:
        name, file = extra.split("=", 1)
        if name in cuts:
            raise ValueError("Duplicate method name")
        cuts[name] = align(read_capture(file), reference)
    args.out.mkdir(parents=True, exist_ok=True)
    rows = compare(cuts, reference, hard)
    with (args.out / "scores.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    inputs = [args.v1 / "reference.bin", args.v2 / "reference.bin", args.v1 / "auto-plain.bin", args.v1 / "auto-merge.bin", args.labels]
    if args.prototype:
        inputs.append(args.prototype)
    inputs.extend(Path(extra.split("=", 1)[1]) for extra in args.extra)
    provenance = {str(file): hashlib.sha256(file.read_bytes()).hexdigest() for file in inputs}
    (args.out / "provenance.json").write_text(json.dumps(provenance, indent=2))
    np.savez(args.out / "cuts.npz", **cuts)
    plot(cuts, reference, args.out / "placement.png", args.plot)
    for r in rows:
        if r["reference"] == "artist-v1":
            print(r)


if __name__ == "__main__":
    main()
