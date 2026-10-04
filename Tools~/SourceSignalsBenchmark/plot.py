"""Usage: python plot.py SOURCE_SIGNAL_DIRECTORY REPORT_DIRECTORY"""
import csv
import struct
import sys
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

source, output = map(Path, sys.argv[1:3])
output.mkdir(parents=True, exist_ok=True)
main = list(csv.DictReader((source / 'signal-cv.csv').open()))
soft = list(csv.DictReader((source / 'signal-soft-seams.csv').open()))
families = ['low_geometry', 'low_plus_AO', 'low_plus_curvature', 'low_plus_source']
labels = ['Low-poly target angle\n+ edge length', '+ source AO', '+ source\ncurvature/cavity', '+ all source /\nprojection signals']
colors = ['#5470a4', '#d79845', '#4d9979', '#b46077']
plt.rcParams.update({'font.size': 9})
fig, axes = plt.subplots(2, 3, figsize=(16, 9), layout='constrained')


def chart(ax, data, label, title):
    values = [float(next(r for r in data if r['label'] == label and r['family'] == f)['rocAuc']) for f in families]
    bars = ax.barh(labels, values, color=colors)
    ax.bar_label(bars, labels=[f'{v:.3f}' for v in values], padding=3)
    ax.invert_yaxis()
    ax.set_xlim(0, 1.05)
    ax.axvline(.5, color='#999999', linestyle=':', linewidth=1)
    ax.set_xlabel('ROC AUC (0.5 = chance ranking)')
    ax.set_title(title)


chart(axes[0, 0], main, 'hard', 'Smoothing boundaries: 163 / 2784 edges')
chart(axes[0, 1], main, 'seamV1', 'All UV seams V1: 204 / 2784 edges')
chart(axes[0, 2], main, 'seamV2', 'All UV seams V2: 270 / 2784 edges')
chart(axes[1, 0], soft, 'seamV1', 'UV seams on smooth edges V1: 43 / 2621')
chart(axes[1, 1], soft, 'seamV2', 'UV seams on smooth edges V2: 115 / 2621')
for case, color in [('reference', colors[0]), ('simplify', colors[3])]:
    data = (source / (case + '-projected.bin')).read_bytes()
    rows, columns = struct.unpack_from('<2i', data)
    values = np.frombuffer(data, '<f4', rows * columns, 8).reshape(rows, columns)[:, 5]
    axes[1, 2].hist(values, bins=np.geomspace(1e-6, .2, 50), weights=np.full(len(values), 1 / len(values)),
                    histtype='step', linewidth=1.5, color=color, label=case)
axes[1, 2].set_xscale('log')
axes[1, 2].axvline(.02, color='#555555', linestyle='--', label='2% source diagonal')
axes[1, 2].legend()
axes[1, 2].set_title('Cage projection distance\nSmall median; some far hits')
axes[1, 2].set_xlabel('Distance / source bounds diagonal')
axes[1, 2].set_ylabel('Fraction of samples per bin')
fig.suptitle('Source geometry signals vs authored boundaries\n5 spatial folds / 88 blocks / one bust; geometric AO, source normal maps excluded\nAUC measures ranking, not percent accuracy')
fig.savefig(output / 'UV_SOURCE_SIGNALS.png', dpi=150)
plt.close(fig)
