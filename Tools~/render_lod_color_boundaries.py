"""Compare actual vertex-color LOD2 captures at a source-framed 64px footprint."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET

import matplotlib.pyplot as plt
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage
from render_lod_far_budget import digest
from render_lod_far_refinement import BACKGROUND, coverage, frame, compare_gpu
from render_lod_screen_acceptance import readback


def fields(directory, name, variant, view, source_mask):
    mask = coverage(directory, name, variant, view)
    cov = frame(mask[:, :, None], source_mask)[:, :, 0]
    raw = readback(directory/f'{name}-{variant}-{view}-color.rgba')
    rgba = frame(raw-BACKGROUND*(1-mask[:, :, None]), source_mask)/np.maximum(cov[:, :, None], 1e-20)
    return rgba, cov


def boundary_loss(source, source_cov, target, target_cov):
    # Same four-channel contrast/tolerance and one-pixel correspondence as the
    # C# guide, measured independently on box-filtered actual GPU readbacks.
    full = source_cov >= .99999
    interior = ndimage.binary_erosion(full, structure=np.ones((3, 3)))
    target_full = target_cov >= .99999
    pairs = []
    mask = np.zeros(interior.shape, dtype=bool)
    h, w = interior.shape
    for y, x in zip(*np.where(interior)):
        for dy, dx in ((0, 1), (1, 0)):
            yy, xx = y+dy, x+dx
            if yy >= h or xx >= w or not interior[yy, xx]:
                continue
            delta = source[yy, xx]-source[y, x]
            if np.max(abs(delta)) < .15:
                continue
            retained = False
            for sy in (-1, 0, 1):
                for sx in (-1, 0, 1):
                    y0, x0, y1, x1 = y+sy, x+sx, yy+sy, xx+sx
                    if min(y0, x0) < 0 or y1 >= h or x1 >= w:
                        continue
                    if not target_full[y0, x0] or not target_full[y1, x1]:
                        continue
                    retained |= (np.max(abs(target[y0, x0]-source[y, x])) <= .1 and
                                 np.max(abs(target[y1, x1]-source[yy, xx])) <= .1 and
                                 np.max(abs(target[y1, x1]-target[y0, x0]-delta)) <= .1)
            mask[y, x] = mask[yy, xx] = True
            pairs.append((y, x, bool(retained)))
    labels, _ = ndimage.label(mask, structure=np.ones((3, 3)))
    groups = {}
    for y, x, retained in pairs:
        groups.setdefault(int(labels[y, x]), []).append(retained)
    regions = [values for values in groups.values() if len(values) >= 4]
    worst = max((1-sum(values)/len(values) for values in regions), default=0)
    return {'sharpPairs': len(pairs), 'lostPairs': sum(not p[2] for p in pairs),
            'regions': len(regions), 'worstRegionLoss': worst,
            'boundaryLoss': sum(not p[2] for p in pairs)/max(1, len(pairs)),
            'evaluated': bool(regions), 'accepted': bool(regions) and worst <= .25}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--previous', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--paint-surface', type=Path)
    args = parser.parse_args()
    test = ET.parse(args.directory/'tests.xml').getroot()
    assert test.get('result') == 'Passed' and test.get('failed') == test.get('skipped') == '0'
    result = {'baselineCommit': '9c47d01', 'tests': {k: test.get(k) for k in ('total', 'passed', 'failed', 'skipped')},
              'footprint': '64px maximum source extent with 4px padding; actual Unity GPU diagnostic vertex RGBA.',
              'limits': 'No original materials or categorical paint labels. Sharp boundaries use all four linear RGBA channels, contrast .15, tolerance .1, one-pixel correspondence, minimum four pairs.',
              'models': []}
    canvas = Image.new('RGB', (1480, 1060), '#20242a'); draw = ImageDraw.Draw(canvas)
    draw.text((15, 12), 'Actual project vertex color at 64px, shown 3x. Source / before LOD2 / after LOD2, then the same red channel.', fill='white')
    draw.text((15, 32), 'RGB is linear and clamped only for display; measurements include Float32 RGBA. Original materials are not evaluated.', fill='white')
    for model_index, name in enumerate(('bench-colored', 'tent-colored')):
        new = json.loads((args.directory/f'{name}-metrics.json').read_text())
        old = json.loads((args.previous/f'{name}-metrics.json').read_text())
        assert new['sourceSha256'] == old['sourceSha256']
        after = next(c for c in new['captures'] if c['variant'] == 'far-lod2' and c['view'] == 'front')
        before = next(c for c in old['captures'] if c['variant'] == 'far-lod2' and c['view'] == 'front')
        assert after['budgetReached'] and after['triangles'] <= after['targetTriangles']
        fixed = {}
        for view in ('front', 'oblique'):
            for kind in ('coverage', 'shaded', 'wire'):
                filename = f'{name}-far-lod2-{view}-{kind}.rgba'
                fixed[filename] = digest(args.directory/filename)
                assert fixed[filename] == digest(args.previous/filename), f'Changed far geometry/shading: {filename}'
        fit = after['attributeFit']['visibleColorBoundaries']
        assert fit['accepted'] and fit['boundaryAfter'] < fit['boundaryBefore']
        assert all(a <= b+1e-6 for a, b in zip(fit['viewsAfter'], fit['viewsBefore']))
        controls = {}
        for c in new['captures']:
            if c['variant'] == 'far-lod2':
                continue
            for kind in ('color', 'coverage', 'shaded'):
                filename = f'{name}-{c["variant"]}-{c["view"]}-{kind}.rgba'
                controls[filename] = digest(args.directory/filename)
                assert controls[filename] == digest(args.previous/filename), f'Changed control: {filename}'
        row = {'fixture': name, 'sourceSha256': new['sourceSha256'], 'sourceTriangles': new['sourceTriangles'],
               'beforeTriangles': before['triangles'], 'afterTriangles': after['triangles'], 'targetTriangles': after['targetTriangles'],
               'before64': before['farComparison'], 'after64': after['farComparison'],
               'fit': {key: after['attributeFit'][key] for key in ('colorsAccepted', 'colorMethod', 'screenColorVerification',
                    'visibleColorSamples', 'visibleRgbaBefore', 'visibleRgbaAfter', 'visibleChannelsBefore', 'visibleChannelsAfter',
                    'visibleColorBoundaries', 'colorRmsBefore', 'colorRmsAfter', 'colorMaxBefore', 'colorMaxAfter', 'note')},
               'selection': after['reductionNote'], 'beforeMs': before['simplifyMs'], 'afterMs': after['simplifyMs'],
               'candidates': after['budgetCandidates'], 'unchangedControls': controls, 'unchangedFarGeometryShading': fixed,
               'gpu64': compare_gpu(args.directory, args.previous, name), 'boundaries64': []}
        for v, view in enumerate(('front', 'oblique')):
            source_mask = coverage(args.directory, name, 'source', view)
            source_rgba, source_cov = fields(args.directory, name, 'source', view, source_mask)
            boundary = {'view': view}
            for label, folder in (('before', args.previous), ('after', args.directory)):
                rgba, cov = fields(folder, name, 'far-lod2', view, source_mask)
                boundary[label] = boundary_loss(source_rgba, source_cov, rgba, cov)
                boundary[label]['rawSha256'] = digest(folder/f'{name}-far-lod2-{view}-color.rgba')
            row['boundaries64'].append(boundary)
            for col, (folder, variant, triangles) in enumerate(((args.directory, 'source', new['sourceTriangles']),
                    (args.previous, 'far-lod2', before['triangles']), (args.directory, 'far-lod2', after['triangles']))):
                rgba, cov = fields(folder, name, variant, view, source_mask)
                for channel in (False, True):
                    values = np.repeat(rgba[:, :, :1], 3, axis=2) if channel else rgba[:, :, :3]
                    values = values*cov[:, :, None]+.13*(1-cov[:, :, None])
                    pic = Image.fromarray(np.uint8(np.clip(values, 0, 1)*255)).transpose(Image.Transpose.FLIP_TOP_BOTTOM)
                    x = 15+(col+3*channel)*245; y = 65+(model_index*2+v)*245
                    draw.text((x, y), f'{name} | {view} | {triangles} tris'+(' | R' if channel else ''), fill='white')
                    canvas.paste(pic.resize((216, 216), Image.Resampling.NEAREST), (x, y+22))
        result['models'].append(row)
    canvas.save(str(args.output)+'_MODELS.png')
    fig, axes = plt.subplots(1, 4, figsize=(15, 4))
    for i, policy in enumerate(('before', 'after')):
        xs = np.arange(2)+(i-.5)*.3
        for ax, key, title in zip(axes[:3], ('colorLoss', 'rgbaRms', 'silhouetteMean'),
                                 ('Worst CPU paint-boundary loss', 'Worst CPU RGBA RMS', 'Mean CPU silhouette mismatch')):
            ax.bar(xs, [m[policy+'64'][key] for m in result['models']], width=.28, label=policy)
            ax.set_title(title); ax.set_xticks(np.arange(2), ['Colored bench', 'Tent']); ax.grid(axis='y', alpha=.2)
        axes[3].bar(xs, [m['fit']['visibleColorBoundaries']['boundaryBefore' if policy == 'before' else 'boundaryAfter'] for m in result['models']], width=.28)
    axes[3].set_title('Mean boundary loss within final pass'); axes[3].set_xticks(np.arange(2), ['Colored bench', 'Tent']); axes[3].grid(axis='y', alpha=.2)
    axes[0].legend(); fig.tight_layout(); fig.savefig(str(args.output)+'_ERRORS.png', dpi=130); plt.close(fig)
    if args.paint_surface:
        extra = args.paint_surface
        capture_tests = ET.parse(extra/'tests.xml').getroot()
        assert capture_tests.get('result') == 'Passed' and capture_tests.get('failed') == capture_tests.get('skipped') == '0'
        current = json.loads((extra/'tent-colored-metrics.json').read_text())
        selected = next(c for c in current['captures'] if c['variant'] == 'far-lod2' and c['view'] == 'front')
        tent = next(m for m in result['models'] if m['fixture'] == 'tent-colored')
        assert selected['triangles'] == tent['afterTriangles'] and selected['farComparison'] == tent['after64']
        for view in ('front', 'oblique'):
            for kind in ('color', 'coverage', 'shaded'):
                filename = f'tent-colored-far-lod2-{view}-{kind}.rgba'
                assert digest(extra/filename) == digest(args.directory/filename)
        source_mask = coverage(extra, 'tent-colored', 'source', 'paint-surface')
        ref, ref_cov = fields(extra, 'tent-colored', 'source', 'paint-surface', source_mask)
        target, cov = fields(extra, 'tent-colored', 'far-lod2', 'paint-surface', source_mask)
        result['additionalOppositeSide'] = {'testsPassed': capture_tests.get('passed'),
            'lineage': 'Fresh opposite-side Generate capture. All existing final fields and front/oblique raw captures match the complete run exactly. Previous iteration did not capture this side.',
            'sourceRawSha256': digest(extra/'tent-colored-source-paint-surface-color.rgba'),
            'afterRawSha256': digest(extra/'tent-colored-far-lod2-paint-surface-color.rgba'),
            'afterBoundary': boundary_loss(ref, ref_cov, target, cov)}
        picture = Image.new('RGB', (740, 310), '#20242a'); label = ImageDraw.Draw(picture)
        label.text((15, 12), 'Actual tent, opposite painted side at 64px, shown 3x. Source / generated LOD2 / red-channel error.', fill='white')
        for col, values in enumerate((ref*ref_cov[:, :, None]+.13*(1-ref_cov[:, :, None]),
                                      target*cov[:, :, None]+.13*(1-cov[:, :, None]))):
            pic = Image.fromarray(np.uint8(np.clip(values[:, :, :3], 0, 1)*255)).transpose(Image.Transpose.FLIP_TOP_BOTTOM)
            label.text((15+col*245, 52), 'LOD0: 560 triangles' if col == 0 else 'LOD2: 62 triangles', fill='white')
            picture.paste(pic.resize((216, 216), Image.Resampling.NEAREST), (15+col*245, 74))
        delta = np.clip(abs(target[:, :, 0]-ref[:, :, 0])*np.minimum(ref_cov, cov)*4, 0, 1)
        error = np.zeros((72, 72, 3)); error[:, :, 0] = delta
        picture.paste(Image.fromarray(np.uint8(error*255)).transpose(Image.Transpose.FLIP_TOP_BOTTOM).resize((216, 216), Image.Resampling.NEAREST), (505, 74))
        label.text((505, 52), 'Absolute R error, magnified 4x', fill='white')
        picture.save(str(args.output)+'_PAINT_SIDE.png')
    Path(str(args.output)+'.json').write_text(json.dumps(result, indent=2))
    print(json.dumps({'tests': result['tests'], 'models': [{k:m[k] for k in ('fixture', 'afterTriangles', 'after64', 'boundaries64')} for m in result['models']]}, indent=2))


if __name__ == '__main__':
    main()
