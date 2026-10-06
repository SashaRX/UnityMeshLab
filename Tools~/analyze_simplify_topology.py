import collections,json,struct,sys
from pathlib import Path
import numpy as np

def read(path):
    b=Path(path).read_bytes();nv,ni=struct.unpack_from('<2i',b)
    return np.frombuffer(b,'<f4',nv*3,8).reshape(-1,3).astype(float),np.frombuffer(b,'<i4',ni,8+nv*12).reshape(-1,3)

def measure(p,ix):
    wp,slot=np.unique(p,axis=0,return_inverse=True);tri=slot[ix]
    cross=np.cross(p[ix[:,1]]-p[ix[:,0]],p[ix[:,2]]-p[ix[:,0]]);area=np.linalg.norm(cross,axis=1)*.5
    extent=np.ptp(p,axis=0).max();limit=extent*extent*np.finfo(np.float32).eps
    faces=collections.Counter(tuple(sorted(t)) for t in tri)
    edges=collections.defaultdict(list)
    for f,t in enumerate(tri):
        for a,b in zip(t,np.roll(t,-1)):edges[tuple(sorted((int(a),int(b))))].append((f,int(a<b)))
    border=[key for key,v in edges.items() if len(v)==1]
    nonmanifold=[key for key,v in edges.items() if len(v)>2]
    orient=[key for key,v in edges.items() if len(v)==2 and v[0][1]==v[1][1]]
    dup=[key for key,n in faces.items() if n>1]
    parent=np.arange(len(wp))
    def find(x):
        while x!=parent[x]:parent[x]=parent[parent[x]];x=parent[x]
        return x
    for a,b in edges:
        a=find(a);b=find(b);parent[b]=a
    components=collections.defaultdict(list)
    for f,t in enumerate(tri):components[find(t[0])].append(f)
    comp=[]
    for ff in components.values():
        tt=tri[ff]; vv=set(tt.ravel()); ee=set(tuple(sorted((int(a),int(b)))) for t in tt for a,b in zip(t,np.roll(t,-1)))
        comp.append(dict(faces=len(ff),vertices=len(vv),euler=len(vv)-len(ee)+len(ff),boundary=sum(len(edges[k])==1 for k in ee),duplicates=sum(faces[k]-1 for k in set(tuple(sorted(t)) for t in tt))))
    stats=dict(vertices=len(p),welded=len(wp),faces=len(ix),boundary=len(border),nonmanifold=len(nonmanifold),winding=len(orient),duplicates=sum(n-1 for n in faces.values()),zeroArea=int((area==0).sum()),nearZero=int((area<=limit).sum()),minAreaRelative=float(area.min()/extent**2),components=sorted(comp,key=lambda x:-x['faces']))
    return stats,dict(border=border,nonmanifold=nonmanifold,duplicates=dup)

if __name__=='__main__':
    for path in sys.argv[1:]:
        p,ix=read(path);stats,_=measure(p,ix);print(Path(path).name,json.dumps(stats))
