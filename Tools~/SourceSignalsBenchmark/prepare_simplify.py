import json,struct,collections,sys
from pathlib import Path
import numpy as np
root=Path(sys.argv[1])/'source-signals'
def save(p,ix,path):
 with path.open('wb') as w:w.write(struct.pack('<2i',len(p),ix.size));w.write(p.astype('<f4').tobytes());w.write(ix.astype('<i4').tobytes())
r=(Path(sys.argv[2])/'reference.bin').read_bytes();nv,ni,_=struct.unpack_from('<3i',r);rp=np.frombuffer(r,'<f4',nv*3,12).reshape(-1,3);ri=np.frombuffer(r,'<i4',ni,12+nv*20).reshape(-1,3);save(rp,ri,root/'reference-target.bin')
space=json.loads((root/'source-space.json').read_text());center=np.array([space[k] for k in ['x','y','z']]);scale=space['w']
b=Path(sys.argv[3]).read_bytes();nv,ni=struct.unpack_from('<2i',b);p=np.frombuffer(b,'<f4',nv*3,8).reshape(-1,3).astype(float);ix=np.frombuffer(b,'<i4',ni,8+nv*12).reshape(-1,3);p=(p-center)/scale;save(p,ix,root/'simplify-target.bin')
wp,slots=np.unique(p,axis=0,return_inverse=True);wi=slots[ix];ed=collections.defaultdict(list);norm=np.cross(p[ix[:,1]]-p[ix[:,0]],p[ix[:,2]]-p[ix[:,0]]);norm/=np.maximum(np.linalg.norm(norm,axis=1)[:,None],1e-30);centers=p[ix].mean(axis=1)
for f,t in enumerate(wi):
 for a,b in zip(t,np.roll(t,-1)):ed[tuple(sorted((int(a),int(b))))].append(f)
result=Path(sys.argv[4]).read_bytes();v,i,_=struct.unpack_from('<3i',result);pos=np.frombuffer(result,'<f4',v*3,12).reshape(-1,3).astype(float);pos=(pos-center)/scale;uv=np.frombuffer(result,'<f4',v*2,12+v*12).reshape(-1,2);indices=np.frombuffer(result,'<i4',i,12+v*20).reshape(-1,3);_,slot=np.unique(pos,axis=0,return_inverse=True);seams=collections.defaultdict(list)
for t in indices:
 for a,b in zip(t,np.roll(t,-1)):
  if slot[a]>slot[b]:a,b=b,a
  seams[(int(slot[a]),int(slot[b]))].append(uv[[a,b]])
rows=[];queries=[]
for key,ff in sorted(ed.items()):
 if len(ff)!=2:continue
 a,b=key;f0,f1=ff;mid=(wp[a]+wp[b])*.5;v=seams[key];seam=len(v)==2 and np.max(np.abs(v[0]-v[1]))>1e-7
 row=dict(edge=len(rows),a=a,b=b,f0=f0,f1=f1,midX=mid[0],midY=mid[1],midZ=mid[2],length=float(np.linalg.norm(wp[b]-wp[a])),lowAngle=float(np.degrees(np.arccos(np.clip(norm[f0]@norm[f1],-1,1)))),seamCurrent=int(seam));rows.append(row)
 for t in [.2,.5,.8]:
  ep=wp[a]*(1-t)+wp[b]*t
  for side,f in enumerate(ff):
   q=ep*.95+centers[f]*.05;weights=np.full(3,.05/3);weights[np.flatnonzero(wi[f]==a)[0]]+=.95*(1-t);weights[np.flatnonzero(wi[f]==b)[0]]+=.95*t;queries.append((row['edge'],side,f,q,norm[f],weights))
with (root/'simplify-queries.bin').open('wb') as w:
 w.write(struct.pack('<i',len(queries)))
 for edge,side,f,q,n,wgt in queries:w.write(struct.pack('<3i9f',edge,side,f,*q,*n,*wgt))
(root/'simplify-edges.json').write_text(json.dumps(rows,indent=2))
print('SIMPLIFY',len(p),'vertices',len(ix),'faces',len(rows),'manifold edges',len(queries),'queries',sum(x['seamCurrent'] for x in rows),'current seam edges')
