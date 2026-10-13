"""Render Cap components, intersection witnesses and voxel defect locations."""
import argparse
import json
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection, PolyCollection
import numpy as np

from analyze import cap_groups, read_mesh


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    parser.add_argument('--title',default='Remesh Cap: topology and geometric intersections')
    args=parser.parse_args()
    report=json.loads(args.report.read_text(encoding='utf-8'))
    p,ix=read_mesh(report['capped'])
    count=report['summary']['originalFaces']
    groups=cap_groups(ix,count)
    triangles=p[ix][:,:,[0,2]]
    fig,axes=plt.subplots(1,3,figsize=(16,7),layout='constrained')
    colors=plt.get_cmap('tab10')
    contours=[]
    for group in range(groups.max()+1):
        faces=ix[groups==group]
        edges,counts=np.unique(np.sort(faces[:,[0,1,1,2,2,0]].reshape(-1,2),axis=1),axis=0,return_counts=True)
        segments=p[edges[counts==1]][:,:,[0,2]]
        contours.append(segments)
        axes[0].add_collection(PolyCollection(triangles[groups==group],facecolors=[colors(group)],edgecolors='none',alpha=.18))
        axes[0].add_collection(LineCollection(segments,colors=[colors(group)],linewidths=1.2))
        center=p[np.unique(faces)][:,[0,2]].mean(axis=0)
        axes[0].text(*center,f'{group} ({len(faces)}f)',fontsize=8,color=colors(group),bbox={'facecolor':'white','alpha':.9,'edgecolor':'none','pad':1})
    for ax in axes[1:]:
        ax.add_collection(LineCollection(np.concatenate(contours),colors='#b4bbc4',linewidths=.7))
    fans=report.get('topology',{}).get('disconnectedFanPositions',[])
    if fans:
        locations=np.array(fans)[:,[0,2]]
        axes[0].scatter(locations[:,0],locations[:,1],marker='x',color='#d63235',s=60,zorder=5)
        axes[0].annotate(f'{len(fans)} disconnected vertex fan(s)',locations[0],xytext=(5,-25),
                         textcoords='offset points',color='#d63235',fontsize=9,
                         bbox={'facecolor':'white','edgecolor':'none','alpha':.9})
    palette={'source-source':'#267f4b','source-cap':'#d63235','cap-cap':'#aa31af'}
    for role,color in palette.items():
        proper=[pair for pair in report['pairs'] if pair['role']==role and pair['kind'] in ('crossing','coplanar_overlap')]
        segments=[]
        for pair in proper:
            q=np.array(pair['points'])[:,[0,2]]
            if len(q)>2:
                segments.extend(np.stack((q,np.roll(q,-1,axis=0)),axis=1))
            elif len(q)==2:
                segments.append(q)
        if segments:
            axes[1].add_collection(LineCollection(segments,colors=color,linewidths=2))
            axes[1].plot([],[],color=color,label=f'{role}: {len(proper)} pairs',linewidth=2)
    contacts=[q for pair in report['pairs'] if pair['role']!='source-source' and pair['kind'] in ('point_contact','segment_contact') for q in pair['points']]
    if contacts:
        q=np.array(contacts)[:,[0,2]]
        axes[1].scatter(q[:,0],q[:,1],s=9,color='#a66e14',label='Nonadjacent cap contacts',zorder=3)
    if report['voxels']:
        voxel=report['voxels'][0]
        for kind,color,label in [('non_manifold_edge','#2472bc','Non-manifold edge midpoints'),('duplicate_face','#e18521','Duplicate face group centroids')]:
            points=np.array([row['point'] for row in voxel['samples'] if row['kind']==kind])
            if len(points):
                axes[2].scatter(points[:,0],points[:,2],s=9,color=color,label=f'{label}: {len(points)}',alpha=.7)
        near=voxel['association'].get('non_manifold_edge',{}).get('capCrossingsWithin2Cells',0)
        note=f'{near}/{voxel["counts"]["nonManifoldEdges"]} edge midpoints within 2 voxel cells\nof new crossing/overlap geometry'
        if not report['summary']['newCrossingOrOverlapPairs']:
            note=f'No new Cap crossings/overlaps.\n{voxel["counts"]["nonManifoldEdges"]} non-manifold edges remain.'
        axes[2].text(.02,.02,note,transform=axes[2].transAxes,fontsize=9,bbox={'facecolor':'white','edgecolor':'#dde0e4','alpha':.95})
    voxel_title='Voxel defects'
    if report['voxels']:
        voxel_title=f"Voxel defects, resolution {voxel['resolution']} / {Path(voxel['file']).stem}"
    for ax,title in zip(axes,['Cap components and boundary contours','Exact intersection witnesses',voxel_title]):
        ax.set_title(title,fontsize=11,pad=12)
        ax.set_xlim(float(p[:,0].min())-.05,float(p[:,0].max())+.05)
        ax.set_ylim(float(p[:,2].min())-.05,float(p[:,2].max())+.05)
        ax.set_aspect('equal'); ax.set_xlabel('X (capture units)'); ax.set_ylabel('Z (capture units)')
        ax.grid(alpha=.15); ax.set_axisbelow(True)
    axes[1].legend(loc='upper left',fontsize=8,framealpha=.95)
    axes[2].legend(loc='upper left',fontsize=8,framealpha=.95)
    fig.suptitle(args.title,fontsize=15)
    args.output.parent.mkdir(parents=True,exist_ok=True)
    fig.savefig(args.output,dpi=160,facecolor='white')
    print(args.output.resolve())


if __name__=='__main__':
    main()
