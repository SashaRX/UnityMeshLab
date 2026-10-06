"""Render exact source-refinement replay buffers; no inferred geometry or UVs.

Requires NumPy and Matplotlib. Raw captures stay in the local replay directory.
"""
import argparse
import csv
import hashlib
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import PolyCollection
from mpl_toolkits.mplot3d.art3d import Poly3DCollection, Line3DCollection
import numpy as np
from analyze_uv_reference import overlaps


def mesh(path, uv=False):
    data = path.read_bytes()
    header = 12 if uv else 8
    count, index_count = np.frombuffer(data, dtype="<i4", count=2)
    stride = 24 if uv else 12
    if len(data) != header + count * stride + index_count * 4:
        raise ValueError(f"Unexpected capture length: {path}")
    if uv:
        vertices = np.frombuffer(data, dtype=[("p", "<f4", 3), ("uv", "<f4", 2), ("chart", "<i4")], count=count, offset=header)
        p, tex, charts = vertices["p"], vertices["uv"], vertices["chart"]
    else:
        p = np.frombuffer(data, dtype="<f4", count=count * 3, offset=header).reshape(-1, 3)
        tex, charts = None, None
    indices = np.frombuffer(data, dtype="<i4", count=index_count, offset=header + count * stride).reshape(-1, 3)
    if np.any(indices < 0) or np.any(indices >= count):
        raise ValueError(f"Invalid index: {path}")
    return p.astype(float), indices, tex, charts


def slivers(triangles):
    edges = np.roll(triangles, -1, axis=1) - triangles
    longest = (edges * edges).sum(axis=2).max(axis=1)
    cross = np.linalg.norm(np.cross(edges[:, 0], -edges[:, 2]), axis=1)
    return np.sqrt(3) * longest / (2 * cross) > 10


def geometry(directory, name, rows, out):
    source, source_ix, _, _ = mesh(directory / f"{name}-source.bin")
    center = (source.min(axis=0) + source.max(axis=0)) / 2
    extent = np.ptp(source, axis=0).max()
    fig = plt.figure(figsize=(12, 10))
    fig.subplots_adjust(left=.02, right=.98, bottom=.02, top=.83, hspace=.18, wspace=.03)
    variants = ["voxel", "voxel-refined", "simplify-baseline", "simplify-refined"]
    for i, variant in enumerate(variants):
        path = directory / f"{name}-{variant}.bin"
        p, ix, _, _ = mesh(path)
        triangles = (p[ix] - center) / extent
        bad = slivers(triangles)
        ax = fig.add_subplot(2, 2, i + 1, projection="3d")
        colors = np.full((len(ix), 4), [.65, .77, .87, 1.])
        colors[bad] = [.95, .4, .2, 1.]
        ax.add_collection3d(Poly3DCollection(triangles, facecolors=colors,
                                             edgecolors="#233749", linewidths=.18 if len(ix) > 5000 else .4))
        if name == "diagonal-box":
            edges = sorted({tuple(sorted((int(face[k]), int(face[(k + 1) % 3]))))
                            for face in source_ix for k in range(3)})
            ax.add_collection3d(Line3DCollection((source[np.array(edges)] - center) / extent,
                                                 colors="#1d872e", linewidths=.8))
        for setter in (ax.set_xlim, ax.set_ylim, ax.set_zlim):
            setter(-.55, .55)
        ax.set_box_aspect((1, 1, 1))
        ax.view_init(elev=20, azim=-65)
        ax.set_axis_off()
        row = rows[f"{name}-{variant}"]
        ax.set_title(f"{variant}: {row['faces']} faces\n"
                     f"quality {float(row['meanQuality']):.3f}; aspect > 10: {row['sliverFaces']}", fontsize=11)
    legend = "; green = source edges" if name == "diagonal-box" else ""
    fig.suptitle(f"{name} | red = thin triangles{legend}\n"
                 "Top: standalone pre-fit candidate. Bottom: actual guarded Simplify result.", fontsize=13, y=.97)
    fig.savefig(out / f"{name}-geometry.png", dpi=170)
    plt.close(fig)


def uvs(directory, out):
    fig, axes = plt.subplots(2, 2, figsize=(12, 12), constrained_layout=True)
    rows = []
    for ax, (name, variant) in zip(axes.flat, [(n, v) for n in ("bust", "diagonal-box") for v in ("baseline", "refined")]):
        path = directory / f"{name}-{variant}-uv.bin"
        p, ix, tex, charts = mesh(path, uv=True)
        triangles, face_charts = tex[ix].astype(float), charts[ix[:, 0]]
        a, b = triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0]
        cross = a[:, 0] * b[:, 1] - a[:, 1] * b[:, 0]
        pairs = overlaps(triangles, face_charts)
        row = dict(faces=len(ix), charts=len(np.unique(charts)), overlapPairs=len(pairs),
                   sameChartPairs=sum(pair["chartA"] == pair["chartB"] for pair in pairs),
                   zeroAreaFaces=int((np.abs(cross) <= 1e-14).sum()),
                   outOfBoundsVertices=int(((tex < -1e-7) | (tex > 1 + 1e-7)).any(axis=1).sum()),
                   captureSha256=hashlib.sha256(path.read_bytes()).hexdigest())
        for key in ("overlapPairs", "zeroAreaFaces", "outOfBoundsVertices"):
            if row[key] != 0:
                raise ValueError(f"UV verification failed: {path}: {key}={row[key]}")
        rows.append(dict(method=f"{name}-{variant}", **row))
        ax.add_collection(PolyCollection(tex[ix], facecolors=plt.get_cmap("tab20")(charts[ix[:, 0]] % 20),
                                         edgecolors="#26313d", linewidths=.25))
        ax.set(xlim=(0, 1), ylim=(0, 1), aspect="equal", xlabel="U", ylabel="V")
        ax.set_title(f"{name} / {variant}: {len(np.unique(charts))} charts", fontsize=12)
    fig.suptitle("Actual Unwrap outputs, merge disabled | complete overlap scans: 0 pairs", fontsize=13)
    fig.savefig(out / "uv-comparison.png", dpi=170)
    plt.close(fig)
    with (out / "uv-quality.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    rows = {r["method"]: r for r in csv.DictReader((args.directory / "metrics.csv").open(encoding="utf-8-sig"))}
    for name in ("bust", "diagonal-box"):
        geometry(args.directory, name, rows, args.out)
    uvs(args.directory, args.out)
    captures = sorted(args.directory.glob("*.bin"))
    (args.out / "capture-hashes.txt").write_text("\n".join(
        f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}" for p in captures) + "\n", encoding="utf-8")
    print(args.out)


if __name__ == "__main__":
    main()
