"""Compare saved geometry captures (positions/UV/indices/charts) from UV replay.
Usage: python Tools~/analyze_uv_packing.py SCRATCH_REPLAY OUTPUT_DIRECTORY
"""
import csv
import struct
import sys
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.collections import PolyCollection
from analyze_uv_merge_relax import geometry


def measure(path):
    data = path.read_bytes()
    nv, ni, nc = struct.unpack_from('<3i', data)
    p = np.frombuffer(data, '<f4', nv * 3, 12).reshape(-1, 3).astype(float)
    uv = np.frombuffer(data, '<f4', nv * 2, 12 + nv * 12).reshape(-1, 2).astype(float)
    ix = np.frombuffer(data, '<i4', ni, 12 + nv * 20).reshape(-1, 3)
    ch = np.frombuffer(data, '<i4', nv, 12 + nv * 20 + ni * 4)[ix[:, 0]]
    edges = uv[ix[:, 1]] - uv[ix[:, 0]]
    other = uv[ix[:, 2]] - uv[ix[:, 0]]
    area = np.abs(edges[:, 0] * other[:, 1] - edges[:, 1] * other[:, 0]) / 2
    surface = np.linalg.norm(np.cross(p[ix[:, 1]] - p[ix[:, 0]], p[ix[:, 2]] - p[ix[:, 0]]), axis=1) / 2
    cuv = np.bincount(ch, weights=area)
    c3d = np.bincount(ch, weights=surface)
    valid = c3d > 0
    density = np.sqrt(cuv[valid] / c3d[valid])
    average = np.average(density, weights=c3d[valid])
    cv = np.sqrt(np.average((density - average) ** 2, weights=c3d[valid])) / average
    _, stretch, _ = geometry(path)
    counts = np.bincount(ch)
    return uv[ix], ch, dict(file=path.name, faces=ni // 3, charts=nc,
        small=int(((counts > 0) & (counts <= 8)).sum()), filledArea=area.sum(),
        chartDensityCV=cv, meanStretch=np.average(stretch, weights=surface), worstStretch=stretch.max())


def main():
    source, output = map(Path, sys.argv[1:3])
    output.mkdir(parents=True, exist_ok=True)
    cases = [('1901 triangles; rounded screenshot settings (512 / padding 3)', ['old-plain', 'old-merge', 'final-1901-merge']),
             ('1997 triangles; saved chart settings (512 / padding 2)', ['exact-old-plain', 'exact-old-merge', 'final-1997-merge'])]
    rows = []
    fig, axes = plt.subplots(2, 3, figsize=(15, 10), layout='constrained')
    for row, (case, files) in enumerate(cases):
        for col, (name, title) in enumerate(zip(files, ['Previous baseline', 'Previous merge', 'Revised merge'])):
            triangles, charts, metrics = measure(source / (name + '.bin'))
            rows.append(dict(case=case, **metrics))
            ax = axes[row, col]
            colors = plt.get_cmap('tab20')(charts % 20)
            ax.add_collection(PolyCollection(triangles, facecolors=colors, edgecolors='#20262e', linewidths=.16))
            ax.set_xlim(0, 1); ax.set_ylim(0, 1); ax.set_aspect('equal')
            ax.set_title(f'{title}: {metrics["charts"]} charts\nFill {metrics["filledArea"]:.1%}; worst stretch {metrics["worstStretch"]:.2f}')
            ax.set_xlabel('U'); ax.set_ylabel('V' if col else case + '\nV')
            print(metrics)
    fig.suptitle('Packed triangle area and actual UV layouts; no overlap in these captures')
    fig.savefig(output / 'UV_PACKING_QUALITY.png', dpi=160)
    plt.close(fig)
    with (output / 'UV_PACKING_QUALITY.csv').open('w', newline='', encoding='utf-8') as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0])); writer.writeheader(); writer.writerows(rows)


if __name__ == '__main__':
    main()
