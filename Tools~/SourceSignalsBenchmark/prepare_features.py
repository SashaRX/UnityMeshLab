from pathlib import Path
import struct,itertools,json,sys
import numpy as np
from scipy.spatial import cKDTree
scratch=Path(sys.argv[1]);root=scratch/'source-signals';reference=Path(sys.argv[2])
b=(root/'source.bin').read_bytes();nv,ni=struct.unpack_from('<2i',b);p=np.frombuffer(b,'<f4',nv*3,8).reshape(-1,3).astype(float);n=np.frombuffer(b,'<f4',nv*3,8+nv*12).reshape(-1,3).astype(float);ix=np.frombuffer(b,'<i4',ni,8+nv*24).reshape(-1,3)
r=(reference/'reference.bin').read_bytes();rv,ri,rc=struct.unpack_from('<3i',r);rp=np.frombuffer(r,'<f4',rv*3,12).reshape(-1,3).astype(float);rix=np.frombuffer(r,'<i4',ri,12+rv*20).reshape(-1,3);rp=np.unique(rp,axis=0)
fits=[]
for perm in itertools.permutations(range(3)):
 for sign in itertools.product([-1,1],repeat=3):
  q=p[:,perm]*sign;d=cKDTree(q).query(rp)[0];fits.append((float(np.mean(d*d)),perm,sign,np.median(d),np.quantile(d,.95)))
fits.sort();print('BEST_AXIS_ALIGNMENTS',fits[:4]);_,perm,sign,_,_=fits[0];p=p[:,perm]*sign;n=n[:,perm]*sign
matrix=np.eye(3)[list(perm)]*np.array(sign)[:,None]
if np.linalg.det(matrix)<0:ix=ix[:,[0,2,1]]
with (root/'source-aligned.bin').open('wb') as w:
 w.write(struct.pack('<2i',nv,ni));w.write(p.astype('<f4').tobytes());w.write(n.astype('<f4').tobytes());w.write(ix.astype('<i4').tobytes())
(root/'alignment.json').write_text(json.dumps({'permutation':perm,'sign':sign,'rmsNearestVertex':fits[0][0]**.5,'medianNearestVertex':fits[0][3],'p95NearestVertex':fits[0][4]},indent=2))
# Source features computed in position-welded topology, not UV/normal index topology.
wp,slots=np.unique(p,axis=0,return_inverse=True);wi=slots[ix];facevec=np.cross(wp[wi[:,1]]-wp[wi[:,0]],wp[wi[:,2]]-wp[wi[:,0]]);twice=np.linalg.norm(facevec,axis=1);surface=np.zeros(len(wp));normal=np.zeros_like(wp);lap=np.zeros_like(wp)
for k in range(3):np.add.at(surface,wi[:,k],twice/6);np.add.at(normal,wi[:,k],facevec)
normal/=np.maximum(np.linalg.norm(normal,axis=1)[:,None],1e-30)
# cotangent Laplacian / barycentric area. Positive signed H is convex outward.
for k in range(3):
 a=wi[:,k];v=wi[:,(k+1)%3];z=wi[:,(k+2)%3];edge=wp[z]-wp[v];cot=np.sum((wp[v]-wp[a])*(wp[z]-wp[a]),axis=1)/np.maximum(twice,1e-30);np.add.at(lap,v,cot[:,None]*edge);np.add.at(lap,z,-cot[:,None]*edge)
h=-np.sum(lap*normal,axis=1)/(4*np.maximum(surface,1e-30));tree=cKDTree(wp);features=[h,np.abs(h)];names=['signedH','absH']
for radius in [.01,.03,.07]:
 neighbors=tree.query_ball_point(wp,radius);heights=[];spread=[]
 for i,near in enumerate(neighbors):
  near=np.asarray(near);weights=surface[near];den=max(weights.sum(),1e-30);mean=(wp[near]*weights[:,None]).sum(axis=0)/den;heights.append((mean-wp[i])@normal[i]/(radius*radius));nm=(normal[near]*weights[:,None]).sum(axis=0)/den;spread.append(1-np.clip(nm@normal[i],-1,1))
 features += [np.asarray(heights),np.asarray(spread)];names += [f'cavity{radius}',f'normalSpread{radius}']
data=np.asarray(features).T[slots]
with (root/'source-features.bin').open('wb') as w:w.write(struct.pack('<2i',nv,len(names)));w.write(data.astype('<f4').tobytes())
(root/'source-feature-names.json').write_text(json.dumps(names))
print('SOURCE_FEATURES',len(wp),'welded vertices',names,'H quantiles',np.quantile(h,[0,.01,.5,.99,1]))
