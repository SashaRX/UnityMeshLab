"""Audit and render the first rejected production Bridge candidate per capture."""
import argparse
import json
from pathlib import Path
import re

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from mpl_toolkits.mplot3d.art3d import Poly3DCollection
import numpy as np

from analyze import read_mesh
from compare_fill import audit


def plot(row, output):
    points, faces = read_mesh(row['rejectedPath'])
    report = audit(points, faces, row['sourceFaces'], pair_budget=2000000)
    contact = re.search(r'new face (\d+) contacts face (\d+)', row['rejectedReason'])
    if not contact:
        raise ValueError('Missing production contact witness')
    first, second = map(int, contact.groups())
    witness = points[faces[[first, second]]]
    colors = np.tile([.65, .68, .71, .35], (len(faces), 1))
    colors[row['sourceFaces']:] = [.96, .55, .15, .7]
    colors[first], colors[second] = [1, .05, .08, 1], [.05, .4, 1, 1]
    fig = plt.figure(figsize=(13, 5), constrained_layout=True)
    for panel, focus in enumerate((points[faces].reshape(-1, 3), witness.reshape(-1, 3)), 1):
        ax = fig.add_subplot(1, 2, panel, projection='3d')
        if panel == 1:
            triangles, shades = points[faces], colors
        else:
            triangles, shades = witness, colors[[first, second]]
        ax.add_collection3d(Poly3DCollection(triangles, facecolors=shades, edgecolors=(.1, .1, .1, .25), linewidths=.25))
        low, high = focus.min(axis=0), focus.max(axis=0)
        center, radius = (low + high) * .5, max(float((high - low).max()) * .6, 1e-6)
        ax.set_xlim(center[0] - radius, center[0] + radius)
        ax.set_ylim(center[1] - radius, center[1] + radius)
        ax.set_zlim(center[2] - radius, center[2] + radius)
        ax.set_box_aspect((1, 1, 1))
        if panel == 1:
            ax.view_init(elev=24, azim=-65)
        else:
            normal = np.cross(witness[0, 1] - witness[0, 0], witness[0, 2] - witness[0, 0])
            normal /= np.linalg.norm(normal)
            ax.view_init(elev=np.rad2deg(np.arcsin(np.clip(normal[2], -1, 1))), azim=np.rad2deg(np.arctan2(normal[1], normal[0])))
        ax.set_title('Rejected strip: orange' if panel == 1 else f'New face {first}: red / donor {second}: blue')
    fig.suptitle(row['caseName'] + ': diagnostic candidate, never applied')
    path = output / (row['caseName'] + '__rejected_bridge.png')
    fig.savefig(path, dpi=150)
    plt.close(fig)
    return dict(caseName=row['caseName'], productionReason=row['rejectedReason'], image=str(path.resolve()),
                witnessFaces=[first, second], audit=report)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    rows = json.loads(args.report.read_text(encoding='utf-8'))['results']
    results = [plot(row, args.output) for row in rows if row.get('rejectedPath')]
    (args.output / 'rejection-audit.json').write_text(json.dumps(results, indent=2, allow_nan=False), encoding='utf-8')
    for row in results:
        print(row['caseName'], row['witnessFaces'], 'improper contacts:', row['audit']['improperContacts'])


if __name__ == '__main__':
    main()
