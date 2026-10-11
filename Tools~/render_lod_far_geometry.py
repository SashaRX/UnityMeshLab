"""Audit a fresh far-geometry run and plot its actual shared-bake comparison."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET

import matplotlib.pyplot as plt
import numpy as np
from PIL import Image, ImageDraw
from render_lod_far_budget import digest
from render_lod_far_refinement import coverage, frame
from render_lod_originals import display
from render_lod_screen_acceptance import readback
from render_lod_shared_lightmap import field


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--previous', required=True, type=Path)
    parser.add_argument('--previous-bake', required=True, type=Path)
    parser.add_argument('--originals', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    test = ET.parse(args.directory/'tests.xml').getroot()
    assert test.get('result') == 'Passed' and test.get('failed') == test.get('skipped') == '0'
    result = {'baselineCommit': '98901b9', 'tests': {k:test.get(k) for k in ('total','passed','failed','skipped','duration')},
              'preview': 'Fresh source-framed 64px captures; gray headlight and actual shared Bakery irradiance. Original PBR materials are not evaluated.',
              'models': [], 'bakedViews': []}
    for path in sorted(args.directory.glob('*-metrics.json')):
        new = json.loads(path.read_text()); old = json.loads((args.previous/path.name).read_text())
        name = new['fixture']; assert new['sourceSha256'] == old['sourceSha256']
        before = next(c for c in old['captures'] if c['variant'] == 'far-lod2' and c['view'] == 'front')
        after = next(c for c in new['captures'] if c['variant'] == 'far-lod2' and c['view'] == 'front')
        assert after['budgetReached'] and after['triangles'] <= after['targetTriangles']
        controls = {}
        for capture in new['captures']:
            if capture['variant'] == 'far-lod2': continue
            for kind in ('coverage','color','shaded'):
                filename = f'{name}-{capture["variant"]}-{capture["view"]}-{kind}.rgba'
                value = digest(args.directory/filename)
                assert value == digest(args.previous/filename), f'Changed control: {filename}'
                controls[filename] = value
        far_hashes = {f'{v}-{k}':digest(args.directory/f'{name}-far-lod2-{v}-{k}.rgba')
                      for v in ('front','oblique') for k in ('coverage','color','shaded')}
        far_unchanged = all(value == digest(args.previous/f'{name}-far-lod2-{key}.rgba')
                            for key,value in far_hashes.items())
        if name in ('bench-colored','tent-colored'):
            assert far_unchanged, f'Changed varying-color control: {name}'
        result['models'].append({'fixture':name, 'sourceSha256':new['sourceSha256'], 'sourceTriangles':new['sourceTriangles'],
                                 'beforeTriangles':before['triangles'], 'afterTriangles':after['triangles'],
                                 'targetTriangles':after['targetTriangles'], 'before64':before['farComparison'], 'after64':after['farComparison'],
                                 'beforeMs':before['simplifyMs'], 'afterMs':after['simplifyMs'], 'selection':after['reductionNote'],
                                 'candidates':after['budgetCandidates'], 'unchangedControlSha256':controls,
                                 'farRawSha256':far_hashes, 'farRawUnchanged':far_unchanged})
    assert len(result['models']) == 8
    baked = json.loads((args.directory/'fire-shield-shared-lightmap.json').read_text())
    prior_bake = json.loads((args.previous_bake/'fire-shield-shared-lightmap.json').read_text())
    assert baked['binding'] == prior_bake['binding']
    result['bakeBinding'] = baked['binding']; result['integratedTransfer'] = baked['integratedTransfer']
    assert baked['integratedTransfer']['accepted']
    canvas = Image.new('RGB',(1100,1150),'#20242a'); draw = ImageDraw.Draw(canvas)
    draw.text((15,12),'Actual FireShield: source / previous LOD2 / refined LOD2 / authored FBX LOD2',fill='white')
    draw.text((15,32),'64px maximum source extent, shown 3x. Headlight and shared Bakery irradiance; original PBR material is not evaluated.',fill='white')
    for v,view in enumerate(('front','oblique')):
        mask = coverage(args.directory,'fire-shield','source',view)
        authored_source = coverage(args.originals,'fire-shield','original-lod0',view)
        assert np.max(np.abs(mask-authored_source)) < .001, 'Authored comparison uses a different source framing'
        for mode in ('baked-color','baked-directional'):
            ref,cov,_ = field(args.directory,'source',view,mode,mask)
            filename = f'fire-shield-source-{view}-{mode}.rgba'
            assert digest(args.directory/filename) == digest(args.previous_bake/filename)
            row = {'view':view,'mode':mode,'sourceRawSha256':digest(args.directory/filename)}
            for label,folder in (('before',args.previous_bake),('after',args.directory)):
                values,target,_ = field(folder,'uv2-min16',view,mode,mask)
                weight = np.minimum(cov,target)
                rms = np.sqrt(np.sum((values[:,:,:3]-ref[:,:,:3])**2*weight[:,:,None])/(3*max(weight.sum(),1e-20)))
                row[label] = {'rgbRms':float(rms),'coverageError':float(abs(cov-target).sum()/np.maximum(cov,target).sum()),
                              'rawSha256':digest(folder/f'fire-shield-uv2-min16-{view}-{mode}.rgba')}
            row['improvementPercent'] = 100*(1-row['after']['rgbRms']/row['before']['rgbRms'])
            result['bakedViews'].append(row)
        columns = ((args.directory,'source',1850,'LOD0'),(args.previous_bake,'uv2-min16',prior_bake['targetTriangles'],'Before'),
                   (args.directory,'uv2-min16',baked['targetTriangles'],'After'),(args.originals,'original-lod2',303,'FBX LOD2'))
        for col,(folder,variant,count,label) in enumerate(columns):
            for m,mode in enumerate(('shaded','baked-directional')):
                y=65+v*535+m*265; x=15+col*270
                raw = readback(folder/f'fire-shield-{variant}-{view}-{mode}.rgba')
                draw.text((x,y),f'{label}: {count} tris | {view}',fill='white')
                draw.text((x,y+18),'Headlight' if m == 0 else 'Shared Bakery irradiance',fill='white')
                canvas.paste(display(frame(raw,mask)).resize((216,216),Image.Resampling.NEAREST),(x,y+38))
    canvas.save(str(args.output)+'_MODELS.png')
    fig,axes = plt.subplots(1,3,figsize=(13,6))
    y=np.arange(8)
    for i,label in enumerate(('before','after')):
        models=result['models']; offset=(i-.5)*.32
        axes[0].barh(y+offset,[100*m[label+'Triangles']/m['sourceTriangles'] for m in models],height=.3,label=label)
        axes[1].barh(y+offset,[100*m[label+'64']['silhouetteMean'] for m in models],height=.3,label=label)
        rows=result['bakedViews']; axes[2].bar(np.arange(4)+offset,[r[label]['rgbRms'] for r in rows],width=.3,label=label)
    axes[0].set_yticks(y,[m['fixture'] for m in result['models']]); axes[1].set_yticks(y,[])
    for axis in axes[:2]: axis.invert_yaxis(); axis.grid(axis='x',alpha=.2)
    axes[0].axvline(100/9,color='black',ls='--',lw=1); axes[0].set_title('LOD0 triangles retained, %'); axes[0].legend()
    axes[1].set_title('Mean silhouette mismatch at 64px, %')
    axes[2].set_title('Fire shield: actual baked RGB RMS'); axes[2].set_xticks(np.arange(4),[r['view']+'\n'+r['mode'].replace('baked-','') for r in result['bakedViews']],fontsize=8)
    axes[2].grid(axis='y',alpha=.2)
    fig.tight_layout(); fig.savefig(str(args.output)+'_ERRORS.png',dpi=130); plt.close(fig)
    Path(str(args.output)+'.json').write_text(json.dumps(result,indent=2))
    print(json.dumps({'tests':result['tests'],'models':[(m['fixture'],m['beforeTriangles'],m['afterTriangles']) for m in result['models']],
                      'bakedViews':result['bakedViews']},indent=2))


if __name__ == '__main__':
    main()
