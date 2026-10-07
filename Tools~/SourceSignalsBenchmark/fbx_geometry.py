"""Read binary FBX numeric geometry layers; no code or properties are executed."""
import struct,zlib,json
from pathlib import Path
import numpy as np

def geometry(path):
    b=Path(path).read_bytes();ver=struct.unpack_from('<I',b,23)[0];header='<QQQB' if ver>=7500 else '<IIIB';hs=struct.calcsize(header)
    def prop(o):
     t=chr(b[o]);o+=1
     scalar={'Y':'h','C':'?','I':'i','L':'q','F':'f','D':'d'}
     if t in scalar:
      fmt='<'+scalar[t];return struct.unpack_from(fmt,b,o)[0],o+struct.calcsize(fmt)
     if t in 'SR':
      n=struct.unpack_from('<I',b,o)[0];v=b[o+4:o+4+n];return v.decode('utf8','replace') if t=='S' else '<binary>',o+4+n
     if t in 'fdilbc':
      n,enc,length=struct.unpack_from('<III',b,o);v=b[o+12:o+12+length];v=zlib.decompress(v) if enc==1 else v
      return np.frombuffer(v,dtype={'f':'<f4','d':'<f8','i':'<i4','l':'<i8','b':'u1','c':'u1'}[t]).copy(),o+12+length
     raise ValueError((t,o))
    def node(o):
     end,np_,pl,nl=struct.unpack_from(header,b,o)
     if end==0:return None,o+hs
     o+=hs;name=b[o:o+nl].decode();o+=nl;p=[]
     for _ in range(np_):v,o=prop(o);p.append(v)
     ch=[]
     while o<end:
      v,o2=node(o)
      if v is None:break
      ch.append(v);o=o2
     return {'name':name,'p':p,'ch':ch},end
    nodes=[];o=27
    while o<len(b):
     n,o=node(o)
     if n is None:break
     nodes.append(n)
    def find(n,name):return next((c for c in n['ch'] if c['name']==name),None)
    objects=next(n for n in nodes if n['name']=='Objects')
    meshes=[g for g in objects["ch"] if g["name"]=="Geometry"]
    if len(meshes)!=1: raise ValueError("Expected exactly one FBX Geometry")
    return meshes[0], find
