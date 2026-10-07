"""Compare reference/automatic UV captures and mark positive-area overlaps.

Usage: python Tools~/analyze_uv_reference.py FIRST_CAPTURE_DIR SECOND_CAPTURE_DIR OUTPUT_DIR
Each input directory contains reference.bin, auto-plain.bin and auto-merge.bin,
in the geometry capture format used by UvMergeRelaxBenchmark.
"""
import csv
import sys
from pathlib import Path

import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.collections import PolyCollection
from analyze_uv_packing import measure


def cross(a, b):
    return a[0] * b[1] - a[1] * b[0]


def intersection(a, b):
    polygon = list(a)
    sign = 1 if cross(b[1] - b[0], b[2] - b[0]) > 0 else -1
    for start, end in zip(b, np.roll(b, -1, axis=0)):
        if not polygon:
            break
        clipped = []
        previous = polygon[-1]
        previous_side = sign * cross(end - start, previous - start)
        for current in polygon:
            side = sign * cross(end - start, current - start)
            if (side >= 0) != (previous_side >= 0):
                clipped.append(previous + (current - previous) * previous_side / (previous_side - side))
            if side >= 0:
                clipped.append(current)
            previous, previous_side = current, side
        polygon = clipped
    return abs(sum(cross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0])
                   for i in range(1, len(polygon) - 1))) / 2


def overlaps(triangles, charts):
    minimum = triangles.min(axis=1)
    maximum = triangles.max(axis=1)
    edge = triangles[:, 1] - triangles[:, 0]
    other = triangles[:, 2] - triangles[:, 0]
    area = np.abs(edge[:, 0] * other[:, 1] - edge[:, 1] * other[:, 0]) / 2
    pairs = []
    for a in range(len(triangles)):
        candidates = np.flatnonzero((minimum[:, 0] < maximum[a, 0]) & (maximum[:, 0] > minimum[a, 0]) &
                                    (minimum[:, 1] < maximum[a, 1]) & (maximum[:, 1] > minimum[a, 1]))
        for b in candidates[candidates > a]:
            shared = intersection(triangles[a], triangles[b])
            if shared > max(1e-16, min(area[a], area[b]) * 1e-8):
                pairs.append(dict(faceA=a, faceB=b, chartA=charts[a], chartB=charts[b], area=shared))
    return pairs


def draw(ax, triangles, charts, neutral=False):
    ax.add_collection(PolyCollection(triangles, facecolors='#b7c7d5' if neutral else plt.get_cmap('tab20')(charts % 20),
                                    edgecolors='#26313d', linewidths=.2))
    ax.set_xlim(0, 1)
    ax.set_ylim(0, 1)
    ax.set_aspect('equal')
    ax.set_xlabel('U')
    ax.set_ylabel('V')


def main():
    first, second, output = map(Path, sys.argv[1:4])
    output.mkdir(parents=True, exist_ok=True)
    rows = []
    fig, axes = plt.subplots(2, 3, figsize=(15, 10.5), layout='constrained')
    for row, root in enumerate((first, second)):
        for column, (name, title) in enumerate((('reference', 'Artist reference'), ('auto-plain', 'Automatic unwrap'),
                                              ('auto-merge', 'Automatic unwrap + merge'))):
            triangles, charts, metrics = measure(root / (name + '.bin'))
            pairs = overlaps(triangles, charts)
            metrics.update(version=row + 1, mode=name, overlapPairs=len(pairs),
                           sameChartPairs=sum(p['chartA'] == p['chartB'] for p in pairs),
                           pairAreaSum=sum(p['area'] for p in pairs))
            rows.append(metrics)
            draw(axes[row, column], triangles, charts)
            axes[row, column].set_title(f'V{row + 1}: {title}\n{metrics["charts"]} islands / fill {metrics["filledArea"]:.1%} / {len(pairs)} overlaps')
            if pairs:
                with (output / f'UVBEST_V{row + 1}_OVERLAPS.csv').open('w', newline='', encoding='utf8') as stream:
                    writer = csv.DictWriter(stream, fieldnames=list(pairs[0]))
                    writer.writeheader()
                    writer.writerows(pairs)
                faces = sorted({p[k] for p in pairs for k in ('faceA', 'faceB')})
                for ax in (axes[row, column],):
                    ax.add_collection(PolyCollection(triangles[faces], facecolors='#ff303080', edgecolors='#e00000', linewidths=.9))
                detail, detail_axes = plt.subplots(1, 2, figsize=(12, 6), layout='constrained')
                for ax in detail_axes:
                    draw(ax, triangles, charts, neutral=True)
                    ax.add_collection(PolyCollection(triangles[faces], facecolors='#ff303080', edgecolors='#e00000', linewidths=1))
                points = triangles[faces].reshape(-1, 2)
                low, high = points.min(axis=0), points.max(axis=0)
                pad = max(high - low) * .12
                detail_axes[1].set_xlim(low[0] - pad, high[0] + pad)
                detail_axes[1].set_ylim(low[1] - pad, high[1] + pad)
                for face in faces:
                    center = triangles[face].mean(axis=0)
                    detail_axes[1].text(*center, str(face), fontsize=7)
                detail.suptitle(f'Reference V{row + 1}: {len(pairs)} intersecting triangle pairs, red = involved faces\nFace numbers are zero-based capture indices; positive-area intersections, not seam contact')
                detail.savefig(output / f'UVBEST_V{row + 1}_OVERLAPS.png', dpi=170)
                plt.close(detail)
            print(metrics)
    fig.suptitle('1856 triangles per reference; automatic unwrapping uses the corrected source geometry')
    fig.savefig(output / 'UVBEST_REFERENCE_COMPARISON.png', dpi=150)
    plt.close(fig)
    with (output / 'UVBEST_REFERENCE_COMPARISON.csv').open('w', newline='', encoding='utf8') as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


if __name__ == '__main__':
    main()
