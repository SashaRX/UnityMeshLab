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
from render_lod_far_refinement import BACKGROUND


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('before', type=Path)
    parser.add_argument('after', type=Path)
    parser.add_argument('dataset', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--silhouette', action='store_true', help='Compare silhouette masks and choose the worst prior coverage view')
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
            samples={}
            for label, directory, variant in variants:
                target, b = fields(directory, name, variant, view, sm)
                samples[label]=(target,b)
                source_image=source*a[:,:,None]+BACKGROUND*(1-a[:,:,None])
                target_image=target*b[:,:,None]+BACKGROUND*(1-b[:,:,None])
                gpu.append({'view': view, 'variant': label, **errors(source, target, a, b),
                            'imageRgbaRms':float(np.sqrt(np.mean((source_image-target_image)**2)))})
            # Restored pixels change the conditional overlap set. Also compare
            # color on the same source/before/after pixels and the full image.
            common=np.minimum(a,np.minimum(samples['before'][1],samples['after'][1]))
            for label in ('before','after'):
                shared=errors(source,samples[label][0],common,common)
                row=next(r for r in gpu if r['view']==view and r['variant']==label)
                row['commonRgbaRms']=shared['rgbaRms']; row['commonRgbaChannelRms']=shared['rgbaChannelRms']
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
                'meanImageRgba': float(np.mean([r['imageRgbaRms'] for r in samples])),
                'worstImageRgba': max(r['imageRgbaRms'] for r in samples),
                'meanCoverage': float(np.mean([r['coverageError'] for r in samples])),
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
                       'retainedBuffers': {key:meshes[0][key]==meshes[1][key] for key in ('colors','uvs','slots','tangents')},
                       'farGenerateMs': [r['far-lod2']['simplifyMs'] for r in rows],
                       'nearTriangles': [r['far-lod1']['triangles'] for r in rows],
                       'cpu64': [r['far-lod2']['farComparison'] for r in rows],
                       'beforeUnderCurrentGuide': new.get('baselineFarGuide'),
                       'beforeAllSidesUnderCurrentGuide': new.get('baselineFarAllSidesGuide'),
                       'afterAllSidesUnderCurrentGuide': new.get('farAllSidesGuide'),
                       'attributeFit': rows[1]['far-lod2']['attributeFit'],
                       'selection': rows[1]['far-lod2']['reductionNote'],
                       'candidates': rows[1]['far-lod2']['budgetCandidates'],
                       'ownership': [ownership(m) for m in meshes],
                       'summary': summary, 'gpu64': gpu})
        if args.silhouette:
            assert summary['after']['meanCoverage'] <= summary['before']['meanCoverage']+1e-6, name
            assert summary['after']['worstCoverage'] <= summary['before']['worstCoverage']+1e-6, name
            assert summary['after']['worstRgba'] <= summary['before']['worstRgba']+1e-6, name
            assert summary['after']['meanImageRgba'] <= summary['before']['meanImageRgba']+1e-6, name
            assert summary['after']['worstImageRgba'] <= summary['before']['worstImageRgba']+1e-6, name
            for view in views:
                before,after=[next(r for r in gpu if r['variant']==v and r['view']==view) for v in ('before','after')]
                assert after['coverageError'] <= before['coverageError']+1/255+1e-6, (name,view,'coverage')
                assert after['imageRgbaRms'] <= before['imageRgbaRms']+1/255+1e-6, (name,view,'full image')
                assert after['commonRgbaRms'] <= before['commonRgbaRms']+1/255+1e-6, (name,view,'same-pixel RGBA')
                assert all(a <= b+1/255+1e-6 for a,b in zip(after['commonRgbaChannelRms'],before['commonRgbaChannelRms'])), (name,view,'same-pixel channel')
            for metric in ('sourceDistanceRms','sourceDistance','uvRms'):
                assert rows[1]['far-lod2'][metric] <= rows[0]['far-lod2'][metric]+1e-6, (name,metric)
    fig, axes = plt.subplots(len(models)*3, 4, figsize=(13, len(models)*8), squeeze=False)
    for i, model in enumerate(models):
        worst = max((r for r in model['gpu64'] if r['variant'] == 'before'),
                    key=lambda r: r['coverageError' if args.silhouette else 'rgbaRms'])['view']
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
    if args.silhouette:
        fig, axes = plt.subplots(len(models)*3, 4, figsize=(13, len(models)*8), squeeze=False)
        for i, model in enumerate(models):
            name=model['fixture']
            worst=max((r for r in model['gpu64'] if r['variant']=='before'),key=lambda r:r['coverageError'])['view']
            for j,view in enumerate(('front','oblique',worst)):
                sm=coverage(args.after,name,'source',view)
                _,source=fields(args.after,name,'source',view,sm)
                columns=[('LOD0',args.after,'source'),('Before',args.before,'far-lod2'),('After',args.after,'far-lod2')]
                for col,(label,directory,variant) in enumerate(columns):
                    _,mask=fields(directory,name,variant,view,sm)
                    axes[i*3+j,col].imshow(np.flipud(mask),cmap='gray',vmin=0,vmax=1,interpolation='nearest')
                    axes[i*3+j,col].set_title(label)
                _,mask=fields(args.after,name,'far-lod2',view,sm)
                error=np.stack((np.maximum(source-mask,0),np.minimum(source,mask)*.65,np.maximum(mask-source,0)),axis=-1)
                axes[i*3+j,3].imshow(np.flipud(error),interpolation='nearest'); axes[i*3+j,3].set_title('After versus source')
                axes[i*3+j,0].set_ylabel(name+'\n'+view)
                for axis in axes[i*3+j]: axis.set_xticks([]); axis.set_yticks([])
        fig.suptitle('Actual GPU silhouette at 64px: red = missing, blue = excess, green = overlap',y=.995)
        fig.tight_layout(rect=(0,.02,1,.975))
        fig.text(.5,.008,'Front, oblique and worst previous coverage view; source framing and 1/9 budget retained.',ha='center')
        fig.savefig(str(args.output)+'_SILHOUETTE.png',dpi=130); plt.close(fig)
        fig,axes=plt.subplots(1,3,figsize=(15,5))
        labels=[m['fixture'] for m in models]; x=np.arange(len(models))
        for axis,metric,title in zip(axes,('meanCoverage','worstCoverage','worstAlpha'),
                                    ('Mean silhouette mismatch','Worst silhouette mismatch','Worst alpha RMS')):
            for n,variant in enumerate(('authored','before','after')):
                values=[m['summary'].get(variant,{}).get(metric,np.nan) for m in models]
                bars=axis.bar(x+(n-1)*.25,values,.25,label=variant)
                for bar,value in zip(bars,values):
                    if np.isfinite(value): axis.text(bar.get_x()+bar.get_width()/2,value,f'{value:.4g}',ha='center',va='bottom',fontsize=8,rotation=90)
            axis.set_xticks(x,labels,rotation=15); axis.set_title(title+' · 14 GPU views'); axis.grid(axis='y',alpha=.2); axis.margins(y=.22)
        axes[0].legend(); fig.tight_layout(); fig.savefig(str(args.output)+'_COVERAGE.png',dpi=130); plt.close(fig)
        fig,axes=plt.subplots(len(models),3,figsize=(16,len(models)*3.2),squeeze=False)
        for i,model in enumerate(models):
            for axis,metric,title in zip(axes[i],('coverageError','commonRgbaRms','imageRgbaRms'),
                                        ('Silhouette mismatch','RGBA RMS on identical visible pixels','Full diagnostic RGBA image RMS')):
                for label in ('before','after'):
                    samples=[r for r in model['gpu64'] if r['variant']==label]
                    axis.plot(np.arange(len(views)),[r[metric] for r in samples],marker='.',label=label)
                axis.set_xticks(np.arange(len(views)),views,rotation=60,fontsize=7)
                axis.set_title(model['fixture']+' · '+title,fontsize=9); axis.grid(alpha=.2)
            axes[i,0].legend()
        fig.tight_layout(); fig.savefig(str(args.output)+'_VIEWS.png',dpi=130); plt.close(fig)
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
              'colorComparisonScope': 'Conditional overlap RMS is retained. Common RMS uses the identical source/before/after overlap set; full-image RMS includes lost or excess coverage on the fixed diagnostic background.',
              'gradientRegionScope': 'High-contrast pairs >=0.15; at least four pairs per region. No gradientViews means unmeasured regional ramps, not zero color error.'}
    Path(str(args.output)+'_RESULTS.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps([{k: m[k] for k in ('fixture', 'triangles', 'vertices', 'summary')} for m in models], indent=2))


if __name__ == '__main__':
    main()
