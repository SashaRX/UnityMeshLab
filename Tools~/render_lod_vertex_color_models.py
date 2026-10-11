"""Audit actual project vertex-color meshes against their authored and generated LODs."""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np

from render_lod_far_refinement import BACKGROUND, coverage, frame
from render_lod_screen_acceptance import readback


def fields(directory, fixture, variant, view, source_mask, mode='color'):
    mask = coverage(directory, fixture, variant, view)
    cov = frame(mask[:, :, None], source_mask)[:, :, 0]
    rgba = readback(directory / f'{fixture}-{variant}-{view}-{mode}.rgba')
    rgba = frame(rgba - BACKGROUND*(1-mask[:, :, None]), source_mask)/np.maximum(cov[:, :, None], 1e-20)
    return rgba, cov


def errors(source, target, a, b):
    weight = np.minimum(a, b)
    channels = np.sqrt(np.sum((source-target)**2*weight[:, :, None], axis=(0, 1))/max(weight.sum(), 1e-20))
    return {'rgbaRms': float(np.sqrt(np.mean(channels**2))), 'rgbaChannelRms': channels.tolist(),
            'coverageError': float(np.abs(a-b).sum()/max(np.maximum(a, b).sum(), 1e-20))}


def display(rgba, cov):
    color = np.clip(rgba[:, :, :3], 0, 1).copy()
    color[cov <= 0] = BACKGROUND[:3]
    return np.flipud(np.where(color <= .0031308, color*12.92, 1.055*color**(1/2.4)-.055))


def ownership(mesh):
    positions = [tuple(p[k] for k in 'xyz') for p in mesh['positions']]
    indices = [v for slot in mesh['slots'] for v in slot['indices']]
    uses = Counter(indices)
    edges = defaultdict(list)
    for offset in range(0, len(indices), 3):
        face = indices[offset:offset+3]
        for corner in range(3):
            side = face[corner:]+face[:corner]
            edges[tuple(sorted((positions[side[0]], positions[side[1]])))].append(side)
    interior = [sides for sides in edges.values() if len(sides) == 2]
    isolated = sum(len(set(a+b)) == 6 and all(uses[v] == 1 for v in a+b) for a, b in interior)
    return {'sharedRenderCorners': sum(n > 1 for n in uses.values()), 'geometricInteriorEdges': len(interior),
            'isolatedSixCornerPairs': isolated}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('dataset', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    test = ET.parse(args.directory/'tests.xml').getroot().attrib
    assert int(test['failed']) == int(test['skipped']) == 0 and int(test['total']) == int(test['passed'])
    cases = json.loads(args.dataset.read_text(encoding='utf-8-sig'))['cases']
    models = []
    for case in cases:
        data = json.loads((args.directory/f"{case['name']}-metrics.json").read_text(encoding='utf-8'))
        assert data['sourceSha256'] == case['sourceSha256']
        assert hashlib.sha256(Path(case['sourcePath']).read_bytes()).hexdigest() == case['sourceSha256']
        assert data['varyingVertexColors'], 'Constant white is not a vertex-color transfer fixture'
        rows = {r['variant']: r for r in data['captures'] if r['view'] == 'front'}
        assert 'authored-lod2' in rows and rows['far-lod2']['budgetReached']
        assert rows['far-lod2']['triangles'] <= np.ceil(data['sourceTriangles']/9)
        source_mesh = json.loads((args.directory/f"{case['name']}-source-mesh.json").read_text())
        far_mesh = json.loads((args.directory/f"{case['name']}-far-lod2-mesh.json").read_text())
        assert len(source_mesh['slots']) == len(far_mesh['slots']) == data['submeshes']
        views = ['front', 'oblique'] + [f'field{i}' for i in range(12)]
        gpu = []
        for view in views:
            sm = coverage(args.directory, case['name'], 'source', view)
            source, a = fields(args.directory, case['name'], 'source', view, sm)
            for variant in ('authored-lod2', 'far-lod2'):
                target, b = fields(args.directory, case['name'], variant, view, sm)
                gpu.append({'view': view, 'variant': variant, **errors(source, target, a, b)})
        models.append({'fixture': case['name'], 'mesh': data['meshName'], 'sourcePath': data['sourcePath'],
            'sourceSha256': data['sourceSha256'], 'submeshes': data['submeshes'], 'colorMin': data['colorMin'],
            'colorMax': data['colorMax'], 'triangles': {v: r['triangles'] for v, r in rows.items()},
            'sourceVertices': len(source_mesh['positions']), 'farVertices': len(far_mesh['positions']),
            'colorStorageRetained': far_mesh['colorFormat'] == source_mesh['colorFormat'],
            'uvDimensionsRetained': [u['dimension'] for u in source_mesh['uvs']] == [u['dimension'] for u in far_mesh['uvs']],
            'cpu64': {v: r['farComparison'] for v, r in rows.items()}, 'gpu64': gpu,
            'attributeFit': rows['far-lod2']['attributeFit'],
            'farCornerOwnership': ownership(far_mesh),
            'nearBudgetReached': rows['far-lod1']['budgetReached'], 'farGenerateMs': rows['far-lod2']['simplifyMs']})
        assert models[-1]['colorStorageRetained'] and models[-1]['uvDimensionsRetained']

    fig, axes = plt.subplots(len(models)*3, 3, figsize=(12, 4.2*len(models)*3), squeeze=False)
    for index, model in enumerate(models):
        worst = max((r for r in model['gpu64'] if r['variant'] == 'far-lod2' and r['view'].startswith('field')),
                    key=lambda r: r['rgbaRms'])['view']
        for j, view in enumerate(('front', 'oblique', worst)):
            sm = coverage(args.directory, model['fixture'], 'source', view)
            for col, variant in enumerate(('source', 'authored-lod2', 'far-lod2')):
                color, cov = fields(args.directory, model['fixture'], variant, view, sm)
                axis = axes[index*3+j, col]
                axis.imshow(display(color, cov), interpolation='nearest')
                axis.set_xticks([]); axis.set_yticks([])
                axis.set_title(f"{variant}: {model['triangles'][variant]} triangles")
                if col == 0: axis.set_ylabel(f"{model['fixture']}\n{view}")
    fig.suptitle('Actual project benches · vertex RGB\n64px maximum source extent; worst generated color view included', y=.995)
    fig.tight_layout(rect=(0, .02, 1, .97))
    fig.text(.5, .008, 'Diagnostic fields: project materials, complete prefab and baked lightmaps are not evaluated.', ha='center')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(str(args.output)+'_MODELS.png', dpi=130); plt.close(fig)

    fig, axes = plt.subplots(1, 4, figsize=(18, 5))
    metrics = ('triangles', 'rgbaRms', 'coverageError', 'alpha')
    for axis, metric in zip(axes, metrics):
        for n, variant in enumerate(('authored-lod2', 'far-lod2')):
            values = []
            for model in models:
                if metric == 'triangles': value = model['triangles'][variant]
                else:
                    samples = [r for r in model['gpu64'] if r['variant'] == variant]
                    value = max(r['rgbaChannelRms'][3] if metric == 'alpha' else r[metric] for r in samples)
                values.append(value)
            x = np.arange(len(models))+(n-.5)*.35
            bars = axis.bar(x, values, .35, label=variant)
            for bar, value in zip(bars, values): axis.text(bar.get_x()+bar.get_width()/2, value, f'{value:.4g}', ha='center', va='bottom')
        axis.set_xticks(np.arange(len(models)), [m['fixture'] for m in models], rotation=15)
        axis.set_title({'triangles': 'Triangle count (different budgets)', 'rgbaRms': 'Worst GPU RGBA RMS · 14 views',
                        'coverageError': 'Worst GPU coverage error · 14 views', 'alpha': 'Worst GPU alpha RMS · 14 views'}[metric])
        axis.grid(axis='y', alpha=.2)
    axes[0].legend(); fig.tight_layout(); fig.savefig(str(args.output)+'_ERRORS.png', dpi=140); plt.close(fig)
    report = {'models': models, 'tests': {k: test[k] for k in ('total','passed','failed','skipped','duration')},
        'raw': str(args.directory), 'dataset': str(args.dataset), 'unity': data['unity'], 'gpu': data['gpu'],
        'limits': 'Different authored/generated triangle budgets. Diagnostic vertex RGBA only: no original materials, full assembly, baked lightmap or LOD transition test. Passing tests record fidelity; they do not certify color acceptance. CPU and GPU raster guides are distinct.'}
    Path(str(args.output)+'_RESULTS.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({'tests': report['tests'], 'models': [{k: m[k] for k in ('fixture','triangles','farVertices','nearBudgetReached')} for m in models]}, indent=2))


if __name__ == '__main__':
    main()
