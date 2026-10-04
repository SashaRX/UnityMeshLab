"""Distinguish inherited triangle slivers from UV-induced elongation.

Triangle aspect = sqrt(3)*longest_side_squared/(4*area), one for equilateral.
UV-induced elongated faces have aspect>10 and >1.5 times their 3D aspect.
This is a diagnostic, not a license to change artist topology or to cut a mesh
into tiny islands. Reports use 3D-area weights and actual source correspondence.
"""
import argparse
import csv
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import PolyCollection
import numpy as np

from seams import align, read_capture, topology


def aspect(triangle, area):
    side2 = ((triangle-np.roll(triangle, -1, axis=1))**2).sum(axis=2).max(axis=1)
    return np.sqrt(3)*side2/(4*np.maximum(area, 1e-30))


def measure(capture):
    p, uv, faces, charts, _ = capture
    triangle = p[faces]
    area3 = np.linalg.norm(np.cross(triangle[:,1]-triangle[:,0], triangle[:,2]-triangle[:,0]), axis=1)/2
    texture = uv[faces]
    a, b = texture[:,1]-texture[:,0], texture[:,2]-texture[:,0]
    area_uv = np.abs(a[:,0]*b[:,1]-a[:,1]*b[:,0])/2
    intrinsic, flattened = aspect(triangle, area3), aspect(texture, area_uv)
    inherited = intrinsic > 10
    introduced = (flattened > 10) & (flattened > 1.5*intrinsic)
    chart_area = np.bincount(charts[faces[:,0]], weights=area3)
    row = dict(largestChartAreaFraction=float(chart_area.max()/area3.sum()),
               intrinsicSliverFaces=int(inherited.sum()), uvSliverFaces=int((flattened > 10).sum()),
               uvIntroducedSliverFaces=int(introduced.sum()),
               uvIntroducedSliverSurfaceFraction=float(area3[introduced].sum()/area3.sum()),
               worstIntrinsicAspect=float(intrinsic.max()), worstUvAspect=float(flattened.max()))
    return texture, inherited, introduced, row


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--capture", action="append", required=True, metavar="NAME=FILE")
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    reference = topology(read_capture(args.reference))
    rows = []
    fig, axes = plt.subplots(1, len(args.capture), figsize=(5*len(args.capture), 5.5), layout="constrained", squeeze=False)
    for ax, case in zip(axes.flat, args.capture):
        name, file = case.split("=", 1)
        capture = read_capture(file)
        align(capture, reference)
        triangles, inherited, introduced, row = measure(capture)
        rows.append(dict(method=name, **row))
        ax.add_collection(PolyCollection(triangles, facecolors="#d2d8dd", edgecolors="#53616c", linewidths=.15))
        ax.add_collection(PolyCollection(triangles[inherited], facecolors="#509fc080", edgecolors="#266680", linewidths=.5))
        ax.add_collection(PolyCollection(triangles[introduced], facecolors="#e7464980", edgecolors="#b4161c", linewidths=.7))
        ax.set_xlim(0,1)
        ax.set_ylim(0,1)
        ax.set_aspect("equal")
        ax.set_title(f'{name}\nLargest chart {row["largestChartAreaFraction"]:.1%} surface\n'
                     f'{row["uvIntroducedSliverFaces"]} UV-induced slivers, {row["uvIntroducedSliverSurfaceFraction"]:.3%} surface', fontsize=10)
        ax.set_xlabel("U")
        ax.set_ylabel("V")
        print(rows[-1])
    fig.suptitle("Blue: intrinsic 3D slivers. Red: elongation introduced by UV (aspect >10 and >1.5x source)", fontsize=12)
    args.out.mkdir(parents=True, exist_ok=True)
    fig.savefig(args.out / "triangle-shapes.png", dpi=150)
    plt.close(fig)
    with (args.out / "triangle-shapes.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


if __name__ == "__main__":
    main()
