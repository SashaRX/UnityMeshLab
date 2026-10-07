"""Collect UV merge/relax phase CSVs and plot final per-triangle stretch.
Usage: python Tools~/analyze_uv_merge_relax.py SCRATCH_PROJECT Documentation~
Requires numpy and matplotlib for the standalone UV heatmap.
"""
import csv
import statistics
import struct
import sys
from collections import defaultdict
from pathlib import Path

import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import PolyCollection


def geometry(path):
    data = path.read_bytes()
    vertices, index_count, charts = struct.unpack_from("<3i", data)
    offset = 12
    positions = np.frombuffer(data, "<f4", vertices * 3, offset).reshape(-1, 3).astype(float)
    offset += vertices * 12
    uv = np.frombuffer(data, "<f4", vertices * 2, offset).reshape(-1, 2).astype(float)
    offset += vertices * 8
    indices = np.frombuffer(data, "<i4", index_count, offset).reshape(-1, 3)
    e1 = positions[indices[:, 1]] - positions[indices[:, 0]]
    e2 = positions[indices[:, 2]] - positions[indices[:, 0]]
    u1 = uv[indices[:, 1]] - uv[indices[:, 0]]
    u2 = uv[indices[:, 2]] - uv[indices[:, 0]]
    length = np.linalg.norm(e1, axis=1)
    twice_area = np.linalg.norm(np.cross(e1, e2), axis=1)
    tx = (e1 * e2).sum(axis=1) / length
    ty = twice_area / length
    j0 = u1 / length[:, None]
    j1 = (u2 - j0 * tx[:, None]) / ty[:, None]
    trace = (j0 * j0).sum(axis=1) + (j1 * j1).sum(axis=1)
    det = j0[:, 0] * j1[:, 1] - j0[:, 1] * j1[:, 0]
    stretch = (trace + np.sqrt(np.maximum(0, trace * trace - 4 * det * det))) / (2 * np.abs(det))
    return uv[indices], stretch, charts


def main():
    root, output = map(Path, sys.argv[1:3])
    output.mkdir(parents=True, exist_ok=True)
    sources = list(root.glob("merge-relax-conformal-*/relax.csv"))
    sources += list(root.glob("merge-relax-arap-*/relax.csv"))
    sources += list(root.glob("merge-relax-production-*/production.csv"))
    rows = []
    for path in sorted(sources):
        with path.open(encoding="utf-8-sig") as stream:
            for row in csv.DictReader(stream):
                row = {"run": path.parent.name, "solver": "arap" if "arap" in path.parent.name else "conformal", **row}
                rows.append(row)
    fields = list(dict.fromkeys(key for row in rows for key in row))
    with (output / "UV_MERGE_RELAX_BENCHMARK.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)
    groups = defaultdict(list)
    for row in rows:
        groups[row["run"], row["case"], row["profile"], row.get("iterations", "production")].append(row)
    for (run, case, profile, iterations), samples in groups.items():
        if case == "bust":
            row = samples[0]
            timing = "unwrapMs" if "unwrapMs" in row else "relaxMs"
            print(run, profile, iterations, "n=" + str(len(samples)), "charts=" + row["charts"],
                  "small=" + row["small"], f'mean={float(row["mean"]):.5f}', f'worst={float(row["worst"]):.5f}',
                  timing + "=" + str(round(statistics.median(float(r[timing]) for r in samples), 2)))
    before = root / "merge-relax-conformal-all-512/bust-recommended-relax0.bin"
    after = root / "merge-relax-production-512/bust-recommended.bin"
    datasets = [geometry(before), geometry(after)]
    fig, axes = plt.subplots(1, 2, figsize=(12, 6), layout="constrained")
    for ax, (triangles, stretch, charts), title in zip(axes, datasets, ["Merge before relax", "Merge + conformal relax"]):
        collection = PolyCollection(triangles, array=stretch, cmap="turbo", edgecolors="#192129", linewidths=.13)
        collection.set_clim(1, 4)
        ax.add_collection(collection)
        ax.set_xlim(0, 1); ax.set_ylim(0, 1); ax.set_aspect("equal")
        ax.set_title(f"{title}\n{charts} charts / worst {stretch.max():.3f} / {int((stretch > 2).sum())} faces above 2")
        ax.set_xlabel("U"); ax.set_ylabel("V")
    fig.colorbar(collection, ax=axes, label="Conformal stretch (1 = ideal)", shrink=.8)
    fig.suptitle("Bust, fixed 1997 triangles, recommended charting, 512 / padding 2; zero overlaps", fontsize=12)
    fig.savefig(output / "UV_MERGE_RELAX_BENCHMARK.png", dpi=180)
    plt.close(fig)
    print("measured rows", len(rows))


if __name__ == "__main__":
    main()
