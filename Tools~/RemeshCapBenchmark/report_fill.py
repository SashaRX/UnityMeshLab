"""Render audited Cap comparisons and a compact native-replay matrix."""
import argparse
from collections import Counter
import json
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from mpl_toolkits.mplot3d.art3d import Line3DCollection, Poly3DCollection
import numpy as np

from analyze import NearestTriangles, read_mesh
from boundaries import extract


def samples(triangles):
    weights = [(a/4,b/4,1-(a+b)/4) for a in range(5) for b in range(5-a)]
    return np.concatenate([np.einsum('k,fkj->fj', w, triangles) for w in weights])


def sampled_distance(a, b):
    """A sampled shape difference, deliberately not an exact Hausdorff certificate."""
    def one_way(first, second):
        nearest = NearestTriangles(second)
        return max(float(np.sqrt(nearest.distances(p).min())) for p in samples(first))
    return max(one_way(a,b),one_way(b,a))


def render_case(case, results, source, output, methods, zoom=False):
    p, ix = read_mesh(source)
    loops = extract(p,ix)['loops']
    segments = [p[e['sourceVertices']] for loop in loops for e in loop['halfedges']]
    lo, hi = p.min(axis=0), p.max(axis=0)
    if zoom:
        def warp(loop):
            q = p[[e['sourceVertices'][0] for e in loop['halfedges']]].astype(float)
            _,_,v = np.linalg.svd(q-q.mean(axis=0))
            return abs((q-q.mean(axis=0))@v[-1]).max()
        loop = max(loops,key=warp)
        rim = p[[e['sourceVertices'][0] for e in loop['halfedges']]]
        lo,hi = rim.min(axis=0),rim.max(axis=0)
        extent = (hi-lo).max(); lo -= extent*.1; hi += extent*.1
        segments = [p[e['sourceVertices']] for e in loop['halfedges']]
    fig = plt.figure(figsize=(17,5.2),layout='constrained')
    for index,method in enumerate(methods):
        row = next(r for r in results if r['caseName']==case and r['method']==method)
        q, faces = read_mesh(row['path']); triangles=q[faces]
        mask = np.all(triangles.max(axis=1)>=lo,axis=1)&np.all(triangles.min(axis=1)<=hi,axis=1)
        old = np.arange(len(faces))<len(ix)
        # Captures use Unity's Y-up coordinates; matplotlib's vertical axis is Z.
        triangles = triangles[:,:,[0,2,1]]
        view_lo,view_hi = lo[[0,2,1]],hi[[0,2,1]]
        bad = {s['newFace'] for s in row.get('contactSamples',[])}
        ax = fig.add_subplot(1,len(methods),index+1,projection='3d')
        ax.add_collection3d(Poly3DCollection(triangles[mask&old],facecolor='#bbc3cc',edgecolor='#67717e',linewidth=.2,alpha=.22))
        selected = np.flatnonzero(mask&~old)
        ax.add_collection3d(Poly3DCollection(triangles[selected],facecolor=['#e24c4b' if f in bad else '#428fba' for f in selected],
                                             edgecolor='#174158',linewidth=.65,alpha=.85))
        ax.add_collection3d(Line3DCollection(np.asarray(segments)[:,:,[0,2,1]],colors='#f1a52b',linewidths=2))
        center=(view_lo+view_hi)/2; extent=(view_hi-view_lo).max()*.55
        ax.set_xlim(center[0]-extent,center[0]+extent)
        ax.set_ylim(center[1]-extent,center[1]+extent)
        ax.set_zlim(center[2]-extent,center[2]+extent)
        ax.set_box_aspect((1,1,1))
        ax.view_init(elev=-23 if not zoom else 28,azim=-65)
        ax.set_axis_off()
        label={'ours_planar_001':'Planar 0.01','ours_surface':'Surface',
               'ours_planes_then_surface':'Planes then Surface','meshlib_min_area':'Minimum area',
               'meshlib_universal':'Universal','centroid_fan':'Centroid fan'}.get(method,method)
        ax.set_title(label+'\n'+
                     f"open {row['boundaryEdges']}; bad contacts {row['improperContacts']}",fontsize=11)
    fig.suptitle(case+' — orange: original rim; blue: Cap; red: rejected contact samples',fontsize=14)
    path=output/(case+'-comparison.png'); fig.savefig(path,dpi=150); plt.close(fig)
    return path


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest',required=True,type=Path)
    parser.add_argument('--native-report',type=Path,action='append',default=[],
                        help='Additional disjoint native replay report; do not include repeated attempts.')
    parser.add_argument('--resolution-report',type=Path)
    args=parser.parse_args()
    manifest=json.loads(args.manifest.read_text(encoding='utf-8-sig'))
    output=Path(manifest['output'])
    comparison=json.loads((output/'comparison.json').read_text())
    rows=comparison['results']
    native=json.loads((output/'native.json').read_text())['results'] if (output/'native.json').exists() else []
    for path in args.native_report:
        native.extend(json.loads(path.read_text())['results'])
    identities=[(r['caseName'],r['method'],r['solve'],r.get('voxelResolution',64)) for r in native]
    if len(set(identities)) != len(identities):
        raise ValueError('Duplicate native attempts in combined report')
    methods=list(dict.fromkeys(r['method'] for r in rows))
    count=len(manifest['cases'])
    lines=['# Private Cap comparison','', 'Versions: '+json.dumps(comparison['versions']), '',
           f'{count} captured meshes. Original oriented donor faces are immutable. Exact new-face contacts are checked only within the affected connected element; existing source/source contacts are not recertified. Closed topology and contacts alone do not certify shape or component volume.', '',
           '| Method | Audited closed meshes | Native Solve off/on passes |', '|---|---:|---:|']
    for method in methods:
        closed=sum(r['auditedClosed'] for r in rows if r['method']==method and 'auditedClosed' in r)
        attempts=[r for r in native if r['method']==method]
        lines.append(f"| {method} | {closed}/{count} | {sum(r['status']=='passed' for r in attempts)}/{len(attempts)} |")
    lines += ['', '## Per-mesh outcome', '', '| Mesh | '+' | '.join(methods)+' |', '|---|'+'---|'*len(methods)]
    for case in manifest['cases']:
        statuses=[]
        for method in methods:
            row=next(r for r in rows if r['caseName']==case['name'] and r['method']==method)
            attempts=[r for r in native if r['caseName']==case['name'] and r['method']==method]
            if row['status']=='audited_closed':
                statuses.append('native '+str(sum(r['status']=='passed' for r in attempts))+'/'+str(len(attempts)))
            else:
                statuses.append('source changed/error' if row['status']=='error' else
                                f"open {row['boundaryEdges']}, contact {row['improperContacts']}")
        lines.append('| '+case['name']+' | '+' | '.join(statuses)+' |')
    shapes=[]
    for case in manifest['cases']:
        if case['name'] not in ('FairStall_Steps','Univer_SmallPorch_1'):
            continue
        reference=next(r for r in rows if r['caseName']==case['name'] and r['method']=='ours_planar_001')
        p,ix=read_mesh(reference['path']); old=reference['sourceFaces']; ref=p[ix[old:]]
        for row in rows:
            if row['caseName']!=case['name'] or row['status']!='audited_closed':
                continue
            q,faces=read_mesh(row['path'])
            distance=sampled_distance(ref,q[faces[old:]])
            shapes.append({'caseName':case['name'],'method':row['method'],
                           'sampledDifferenceFromPlanar':distance,'fractionOfSourceSpan':float(distance/np.ptp(p,axis=0).max())})
    (output/'shape.json').write_text(json.dumps(shapes,indent=2))
    lines+=['','## Shape differences','', 'Bidirectional distances between barycentric samples (15 per triangle) and the other patch triangles. Reference is our two-plane Cap, not independent ground truth. Units are source capture units. This is not a Hausdorff bound.', '',
            '| Mesh | Method | Sampled difference | Source span % |','|---|---|---:|---:|']
    lines += [f"| {r['caseName']} | {r['method']} | {r['sampledDifferenceFromPlanar']:.6g} | {r['fractionOfSourceSpan']*100:.3f} |" for r in shapes]
    lines+=['','## Native failures','']
    lines += [f"- {r['caseName']}/{r['method']}/solve={r['solve']}: {r['status']} {r['reason']}" for r in native if r['status']!='passed']
    if args.resolution_report:
        resolution=json.loads(args.resolution_report.read_text())['results']
        lines+=['','## Resolution replay','','| Mesh | Method | Resolution | Solve off/on passes |','|---|---|---:|---:|']
        keys=dict.fromkeys((r['caseName'],r['method'],r['voxelResolution']) for r in resolution)
        for case,method,res in keys:
            attempts=[r for r in resolution if (r['caseName'],r['method'],r['voxelResolution'])==(case,method,res)]
            lines.append(f"| {case} | {method} | {res} | {sum(r['status']=='passed' for r in attempts)}/{len(attempts)} |")
    for name, selected, zoom in [
        ('Univer_Column_C',['ours_surface','meshlib_min_area','meshlib_universal','centroid_fan'],True),
        ('FairStall_Steps',['ours_planar_001','ours_surface','meshlib_min_area','ours_planes_then_surface'],False),
        ('SandbagRoundedcorner',['ours_planar_001','ours_surface','meshlib_min_area','centroid_fan'],False)]:
        source=next(c['source'] for c in manifest['cases'] if c['name']==name)
        path=render_case(name,rows,source,output,selected,zoom)
        lines+=['',f'![{name}]({path.name})']
    (output/'report.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print(output/'report.md')
    print(Counter(r['status'] for r in native))


if __name__=='__main__':
    main()
