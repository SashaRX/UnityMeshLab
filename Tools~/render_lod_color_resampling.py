"""Plot actual GPU captures and RGBA measurements before/after color resampling."""
import argparse
import json
from pathlib import Path

import matplotlib.pyplot as plt


def capture(report, variant, view):
    return next(c for c in report['captures'] if c['variant'] == variant and c['view'] == view)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('before', type=Path)
    parser.add_argument('after', type=Path)
    parser.add_argument('--fixture', default='tent-colored')
    parser.add_argument('--variant', default='unprotected-lod1')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    before = json.loads((args.before / (args.fixture+'-metrics.json')).read_text(encoding='utf-8'))
    after = json.loads((args.after / (args.fixture+'-metrics.json')).read_text(encoding='utf-8'))
    if before['sourceSha256'] != after['sourceSha256']:
        raise ValueError('Source FBX hashes must match.')
    fig, axes = plt.subplots(3, 3, figsize=(15, 12))
    for row, view in enumerate(('front', 'oblique')):
        b, a = capture(before, args.variant, view), capture(after, args.variant, view)
        if b['triangles'] != a['triangles'] or b['silhouetteMismatch'] != a['silhouetteMismatch']:
            raise ValueError('Counts and silhouettes must match for attribute-only comparison.')
        columns = ((args.after, 'source', 'Исходная модель'),
                   (args.before, args.variant, 'До запасного переноса'),
                   (args.after, args.variant, 'После запасного переноса'))
        for col, (directory, variant, title) in enumerate(columns):
            axes[row, col].imshow(plt.imread(directory / f'{args.fixture}-{variant}-{view}-color.png'))
            axes[row, col].axis('off')
            note = '' if col == 0 else f"\nGPU RGBA RMS: {(b if col == 1 else a)['rmsPixelRgbaError']:.5f}"
            axes[row, col].set_title(f'{title} · {view}{note}', fontsize=12)
    b, a = capture(before, args.variant, 'front'), capture(after, args.variant, 'front')
    for channel in b['surfaceColorRms']:
        if (a['surfaceColorRms'][channel] > b['surfaceColorRms'][channel]+1e-6 or
                a['surfaceColorMax'][channel] > b['surfaceColorMax'][channel]+1e-6):
            raise ValueError('Surface RGBA non-regression check failed.')
    worst_before = max(capture(before, args.variant, view)['rmsPixelRgbaError'] for view in ('front', 'oblique'))
    worst_after = max(capture(after, args.variant, view)['rmsPixelRgbaError'] for view in ('front', 'oblique'))
    for col, (key, title) in enumerate((('surfaceColorRms', 'RMS по поверхности'),
                                      ('surfaceColorMax', 'Максимум по поверхности'))):
        for offset, record, label in ((-.18, b, 'До'), (.18, a, 'После')):
            axes[2, col].bar([i+offset for i in range(4)], list(record[key].values()), .36, label=label)
        axes[2, col].set_xticks(range(4), ('R', 'G', 'B', 'A'))
        axes[2, col].set_title(title)
        axes[2, col].legend()
        axes[2, col].grid(axis='y', alpha=.2)
    axes[2, 2].axis('off')
    axes[2, 2].text(0, .9, f"{args.variant}: {a['triangles']} треугольников\n\n"
        'Перенос усредняет барицентрические\nсэмплы исходной поверхности по площади.\n\n'
        'RMS и максимум каждого канала\nне увеличиваются относительно базы.\n\n'
        f"Спереди: {b['rmsPixelRgbaError']:.5f} → {a['rmsPixelRgbaError']:.5f}\n"
        f'Худший ракурс: {worst_before:.5f} → {worst_after:.5f}\n\n'
        'Геометрия и силуэт не меняются.\nИсходные материалы не проверены.', va='top', fontsize=12)
    fig.suptitle(f'{args.fixture}: запасной перенос vertex color · {args.variant}', fontsize=17)
    fig.tight_layout(rect=(0, 0, 1, .96))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(args.output, dpi=150)
    plt.close(fig)


if __name__ == '__main__':
    main()
