import struct,json,itertools,collections,sys
from pathlib import Path
import numpy as np
from scipy.spatial import cKDTree
root=Path(sys.argv[1])/'source-signals'
def capture(path):
 b=path.read_bytes();nv,ni,nc=struct.unpack_from('<3i',b);p=np.frombuffer(b,'<f4',nv*3,12).reshape(-1,3).astype(float);uv=np.frombuffer(b,'<f4',nv*2,12+nv*12).reshape(-1,2).astype(float);ix=np.frombuffer(b,'<i4',ni,12+nv*20).reshape(-1,3);return p,uv,ix
refs=[capture(Path(path)/'reference.bin') for path in sys.argv[2:4]]
p,uv,ix=refs[0];wp,slots=np.unique(p,axis=0,return_inverse=True);wi=slots[ix];ed=collections.defaultdict(list)
fn=np.cross(p[ix[:,1]]-p[ix[:,0]],p[ix[:,2]]-p[ix[:,0]]);fn/=np.linalg.norm(fn,axis=1)[:,None];centers=p[ix].mean(axis=1)
for f,t in enumerate(wi):
 for a,b in zip(t,np.roll(t,-1)):ed[tuple(sorted((int(a),int(b))))].append(f)
# Read smoothing masks from original V1 FBX, independently of UV or normal splits.
from fbx_geometry import geometry
g,find=geometry(sys.argv[4]);rawp=find(g,'Vertices')['p'][0].reshape(-1,3);rawix=find(g,'PolygonVertexIndex')['p'][0].copy();ends=rawix<0;rawix[ends]=-rawix[ends]-1;sg=find(find(g,'LayerElementSmoothing'),'Smoothing')['p'][0];faces=[];mask=[];start=0
for f,end in enumerate(np.flatnonzero(ends)):
 for k in range(start+1,end):faces.append(rawix[[start,k,k+1]]);mask.append(sg[f])
 start=end+1
rawp=(rawp-(rawp.max(axis=0)+rawp.min(axis=0))*.5)/np.linalg.norm(rawp.max(axis=0)-rawp.min(axis=0));fits=[]
for perm in itertools.permutations(range(3)):
 for signs in itertools.product([-1,1],repeat=3):
  transformed=rawp[:,perm]*signs;dist=cKDTree(transformed).query(wp)[0];fits.append((np.mean(dist**2),perm,signs))
fits.sort();_,perm,signs=fits[0];rawp=rawp[:,perm]*signs;rawcenter=rawp[np.asarray(faces)].mean(axis=1);md,nearest=cKDTree(rawcenter).query(centers);masks=np.asarray(mask)[nearest];print('SG_ALIGNMENT',fits[0], 'max centroid mismatch',md.max())
seams=[]
for pp,uu,ii in refs:
 # Match geometric edge endpoints; V2 mesh index order can differ after UV edits.
 _,ss=np.unique(pp,axis=0,return_inverse=True);euv=collections.defaultdict(list)
 for f,t in enumerate(ii):
  for a,b in zip(t,np.roll(t,-1)):
   if ss[a]>ss[b]:a,b=b,a
   euv[(int(ss[a]),int(ss[b]))].append(uu[[a,b]])
 seams.append({key:len(v)==2 and np.max(np.abs(v[0]-v[1]))>=1e-7 for key,v in euv.items()})
rows=[];queries=[]
for key,ff in sorted(ed.items()):
 if len(ff)!=2:continue
 a,b=key;f0,f1=ff;mid=(wp[a]+wp[b])*.5;direction=(wp[b]-wp[a]);length=np.linalg.norm(direction)
 row=dict(edge=len(rows),a=a,b=b,f0=f0,f1=f1,midX=mid[0],midY=mid[1],midZ=mid[2],length=length,lowAngle=float(np.degrees(np.arccos(np.clip(fn[f0]@fn[f1],-1,1)))),hard=int((masks[f0]&masks[f1])==0),seamV1=int(seams[0][key]),seamV2=int(seams[1][key]))
 rows.append(row)
 for t in [.2,.5,.8]:
  edgepoint=wp[a]*(1-t)+wp[b]*t
  for side,f in enumerate(ff):
   # Slightly inside each side, avoiding ambiguous exact-edge nearest hits.
   query=edgepoint*.95+centers[f]*.05;weights=np.full(3,.05/3)
   weights[np.flatnonzero(wi[f]==a)[0]]+=.95*(1-t);weights[np.flatnonzero(wi[f]==b)[0]]+=.95*t
   queries.append((row['edge'],side,f,query,fn[f],weights))
with (root/'queries.bin').open('wb') as w:
 w.write(struct.pack('<i',len(queries)))
 for edge,side,f,q,n,wgt in queries:w.write(struct.pack('<3i9f',edge,side,f,*q,*n,*wgt))
(root/'edges.json').write_text(json.dumps(rows,indent=2))
print('EDGES',len(rows),'hard',sum(x['hard'] for x in rows),'V1seams',sum(x['seamV1'] for x in rows),'V2seams',sum(x['seamV2'] for x in rows),'queries',len(queries))
