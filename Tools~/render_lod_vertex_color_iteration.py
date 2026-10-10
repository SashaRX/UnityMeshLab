"""Compare two real Generate runs, with authored LODs as separate controls."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np

from render_lod_vertex_color_models import coverage, display, errors, fields, ownership


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('before', type=Path)
    parser.add_argument('after', type=Path)
    parser.add_argument('dataset', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--supplement', type=Path, help='Completed rerun resolving every skipped test in the main run')
    args = parser.parse_args()
    test_root=ET.parse(args.after/'tests.xml').getroot()
    test=test_root.attrib
    assert int(test['failed']) == int(test['inconclusive']) == 0
    supplement=None
    passed={t.attrib['fullname'] for t in test_root.iter('test-case') if t.attrib['result']=='Passed'}
    if args.supplement:
        rerun=ET.parse(args.supplement).getroot(); supplement=rerun.attrib
        assert int(supplement['failed']) == int(supplement['skipped']) == int(supplement['inconclusive']) == 0
        resolved={t.attrib['fullname'] for t in rerun.iter('test-case') if t.attrib['result']=='Passed'}
        assert all(t.attrib['fullname'] in resolved for t in test_root.iter('test-case') if t.attrib['result']=='Skipped')
        passed |= resolved
    else:
        assert int(test['skipped']) == 0
    assert any(t.attrib.get('name')=='CopiedProjectModelsProduceActualGenerateCaptures' and t.attrib['result']=='Passed'
               for t in test_root.iter('test-case'))
    models = []
    for case in load(args.dataset)['cases']:
        name = case['name']
        old, new = [load(d/f'{name}-metrics.json') for d in (args.before, args.after)]
        assert old['sourceSha256'] == new['sourceSha256'] == case['sourceSha256']
        assert hashlib.sha256(Path(case['sourcePath']).read_bytes()).hexdigest() == case['sourceSha256']
        rows = [{r['variant']: r for r in data['captures'] if r['view'] == 'front'} for data in (old, new)]
        authored='authored-lod2' in rows[1]
        assert authored==('authored-lod2' in rows[0])
        assert rows[1]['far-lod2']['budgetReached']
        assert rows[1]['far-lod2']['triangles'] <= np.ceil(new['sourceTriangles']/9)
        views = ['front', 'oblique']+[f'field{i}' for i in range(12)]
        gpu = []
        for view in views:
            # Controls must be byte-identical, so comparison changes cannot come
            # from a different source import, camera, material or GPU readback.
            for control in (('source', 'authored-lod2') if authored else ('source',)):
                for mode in ('color', 'coverage'):
                    filename = f'{name}-{control}-{view}-{mode}.rgba'
                    assert (args.before/filename).read_bytes() == (args.after/filename).read_bytes(), filename
            sm = coverage(args.after, name, 'source', view)
            source, a = fields(args.after, name, 'source', view, sm)
            variants=[('before', args.before, 'far-lod2'), ('after', args.after, 'far-lod2')]
            if authored: variants.insert(0,('authored', args.after, 'authored-lod2'))
            for label, directory, variant in variants:
                target, b = fields(directory, name, variant, view, sm)
                gpu.append({'view': view, 'variant': label, **errors(source, target, a, b)})
        meshes = [load(d/f'{name}-far-lod2-mesh.json') for d in (args.before, args.after)]
        assert len(meshes[1]['positions']) <= len(meshes[0]['positions'])
        assert len(meshes[1]['slots']) == new['submeshes']
        assert meshes[0]['colorFormat'] == meshes[1]['colorFormat']
        assert [u['dimension'] for u in meshes[0]['uvs']] == [u['dimension'] for u in meshes[1]['uvs']]
        summary = {}
        for variant in ('authored', 'before', 'after'):
            samples = [r for r in gpu if r['variant'] == variant]
            if not samples: continue
            summary[variant] = {
                'meanRgba': float(np.mean([r['rgbaRms'] for r in samples])),
                'worstRgba': max(r['rgbaRms'] for r in samples),
                'worstCoverage': max(r['coverageError'] for r in samples),
                'worstAlpha': max(r['rgbaChannelRms'][3] for r in samples)}
        models.append({'fixture': name, 'sourceSha256': new['sourceSha256'],
                       'sourceTriangles': new['sourceTriangles'],
                       'triangles': {'authored': rows[1]['authored-lod2']['triangles'] if authored else None,
                                     'before': rows[0]['far-lod2']['triangles'],
                                     'after': rows[1]['far-lod2']['triangles']},
                       'vertices': [len(m['positions']) for m in meshes],
                       'farGenerateMs': [r['far-lod2']['simplifyMs'] for r in rows],
                       'nearTriangles': [r['far-lod1']['triangles'] for r in rows],
                       'cpu64': [r['far-lod2']['farComparison'] for r in rows],
                       'beforeUnderCurrentGuide': new.get('baselineFarGuide'),
                       'beforeAllSidesUnderCurrentGuide': new.get('baselineFarAllSidesGuide'),
                       'afterAllSidesUnderCurrentGuide': new.get('farAllSidesGuide'),
                       'attributeFit': rows[1]['far-lod2']['attributeFit'],
                       'ownership': [ownership(m) for m in meshes],
                       'summary': summary, 'gpu64': gpu})
    fig, axes = plt.subplots(len(models)*3, 4, figsize=(13, len(models)*8), squeeze=False)
    for i, model in enumerate(models):
        worst = max((r for r in model['gpu64'] if r['variant'] == 'before'), key=lambda r: r['rgbaRms'])['view']
        for j, view in enumerate(('front', 'oblique', worst)):
            sm = coverage(args.after, model['fixture'], 'source', view)
            variants = [('LOD0', args.after, 'source', model['sourceTriangles']),
                        ('Authored LOD2', args.after, 'authored-lod2', model['triangles']['authored']),
                        ('Before', args.before, 'far-lod2', model['triangles']['before']),
                        ('After', args.after, 'far-lod2', model['triangles']['after'])]
            for col, (label, directory, variant, triangles) in enumerate(variants):
                axis = axes[i*3+j, col]
                if variant=='authored-lod2' and triangles is None:
                    axis.text(.5,.5,'No authored LOD2 in this FBX',ha='center',va='center'); axis.axis('off'); continue
                color, cov = fields(directory, model['fixture'], variant, view, sm)
                axis.imshow(display(color, cov), interpolation='nearest')
                axis.set_xticks([]); axis.set_yticks([])
                axis.set_title(f'{label}: {triangles} triangles')
                if col == 0: axis.set_ylabel(f"{model['fixture']}\n{view}")
    fig.suptitle('Real project vertex RGB at 64px · source, authored LOD2, previous and current Generate', y=.995)
    fig.tight_layout(rect=(0, .02, 1, .975))
    fig.text(.5, .008, 'Diagnostic fields; original materials and baked lightmaps not evaluated. Authored budget differs.', ha='center')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(str(args.output)+'_MODELS.png', dpi=130); plt.close(fig)
    fig, axes = plt.subplots(len(models), 4, figsize=(13, len(models)*3), squeeze=False)
    for i, model in enumerate(models):
        view='field1'
        sm=coverage(args.after, model['fixture'], 'source', view)
        for col, (label, directory, variant) in enumerate((
                ('LOD0', args.after, 'source'), ('Authored LOD2', args.after, 'authored-lod2'),
                ('Before', args.before, 'far-lod2'), ('After', args.after, 'far-lod2'))):
            if variant=='authored-lod2' and model['triangles']['authored'] is None:
                axes[i,col].text(.5,.5,'No authored LOD2 in this FBX',ha='center',va='center'); axes[i,col].axis('off'); continue
            color, cov=fields(directory, model['fixture'], variant, view, sm, mode='wire')
            axis=axes[i, col]; axis.imshow(display(color, cov), interpolation='nearest')
            axis.set_xticks([]); axis.set_yticks([]); axis.set_title(label)
            if col==0: axis.set_ylabel(model['fixture'])
    fig.suptitle('Actual triangle layout · top view at 64px', y=.995)
    fig.tight_layout(rect=(0, 0, 1, .965)); fig.savefig(str(args.output)+'_WIRE.png', dpi=130); plt.close(fig)
    fig, axes = plt.subplots(1, 4, figsize=(18, 5))
    for axis, metric, title in zip(axes, ('meanRgba', 'worstRgba', 'worstCoverage', 'worstAlpha'),
                                  ('Mean RGBA RMS', 'Worst RGBA RMS', 'Worst coverage error', 'Worst alpha RMS')):
        for n, variant in enumerate(('authored', 'before', 'after')):
            values = [m['summary'].get(variant,{}).get(metric,np.nan) for m in models]
            bars = axis.bar(np.arange(len(models))+(n-1)*.25, values, .25, label=variant)
            for bar, value in zip(bars, values):
                if not np.isfinite(value): continue
                axis.text(bar.get_x()+bar.get_width()/2, value, f'{value:.4g}', ha='center', va='bottom', fontsize=8,rotation=90)
        axis.set_xticks(np.arange(len(models)), [m['fixture'] for m in models], rotation=15)
        axis.set_title(title+' · 14 GPU views'); axis.grid(axis='y', alpha=.2); axis.margins(y=.22)
    axes[0].legend(); fig.tight_layout(); fig.savefig(str(args.output)+'_ERRORS.png', dpi=130); plt.close(fig)
    if all(m['beforeAllSidesUnderCurrentGuide'] and m['afterAllSidesUnderCurrentGuide'] for m in models):
        fig, axes = plt.subplots(1, 3, figsize=(16, 5))
        labels=[m['fixture'] for m in models]; x=np.arange(len(models))
        guides=[m['afterAllSidesUnderCurrentGuide'] for m in models]
        axes[0].bar(x-.18,[g['sharpColorPairs'] for g in guides],.36,label='sharp pairs')
        axes[0].bar(x+.18,[g['gradientPairs'] for g in guides],.36,label='continuous ramp pairs')
        axes[0].set_title('Source high-contrast pairs · 12 CPU views'); axes[0].legend()
        for axis,metric,title in zip(axes[1:],('colorBoundaryMean','gradientWorstRegionRms'),
                                    ('Mean sharp-pair loss','Worst local gradient RGBA RMS')):
            for n,key in enumerate(('beforeAllSidesUnderCurrentGuide','afterAllSidesUnderCurrentGuide')):
                values=[m[key][metric] if metric!='gradientWorstRegionRms' or m[key]['gradientViews']>0 else np.nan for m in models]
                bars=axis.bar(x+(n-.5)*.36,values,.36,label=('before','after')[n])
                for bar,value in zip(bars,values):
                    if not np.isfinite(value): continue
                    axis.text(bar.get_x()+bar.get_width()/2,value,f'{value:.4g}',ha='center',va='bottom',fontsize=8)
            if metric=='gradientWorstRegionRms':
                if all(m['afterAllSidesUnderCurrentGuide']['gradientViews']==0 for m in models):
                    axis.text(.5,.5,'Regional ramp RMS not evaluated\nNo high-contrast region has >=4 pairs',
                              ha='center',transform=axis.transAxes,fontsize=10)
                    axis.set_yticks([])
                else:
                    for i,m in enumerate(models):
                        if m['afterAllSidesUnderCurrentGuide']['gradientViews']==0:
                            axis.text(i,.4,'Not evaluated',ha='center',transform=axis.get_xaxis_transform(),fontsize=8)
            axis.set_title(title+' · same source guide'); axis.legend()
        for axis in axes:
            axis.set_xticks(x,labels,rotation=15); axis.grid(axis='y',alpha=.2)
        fig.tight_layout(); fig.savefig(str(args.output)+'_GUIDE.png',dpi=130); plt.close(fig)
    report = {'tests': test, 'supplementaryTests': supplement, 'distinctPassedTests': len(passed),
              'before': str(args.before), 'after': str(args.after), 'models': models,
              'scope': 'Same FBX bytes, identical source/authored GPU controls, 14 views, source extent 64px.',
              'gradientRegionScope': 'High-contrast pairs >=0.15; at least four pairs per region. No gradientViews means unmeasured regional ramps, not zero color error.'}
    Path(str(args.output)+'_RESULTS.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps([{k: m[k] for k in ('fixture', 'triangles', 'vertices', 'summary')} for m in models], indent=2))


if __name__ == '__main__':
    main()
