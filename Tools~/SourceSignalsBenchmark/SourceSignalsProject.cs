// Developer-only production cage/AO queries; copy into a scratch project Assets/Editor.
using System;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using SashaRX.UnityMeshLab;
public static class SourceSignalsProject {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 static readonly Assembly A=typeof(RemeshSettings).Assembly;
 static object Get(object g,string n)=>g.GetType().GetField(n,F).GetValue(g);
 static void Set(object g,string n,object v)=>g.GetType().GetField(n,F).SetValue(g,v);
 static Vector3 ReadV(BinaryReader r)=>new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
 struct Query {public int edge,side,face;public Vector3 position,normal,weights;}
 public static async void Run(){try{
  string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../source-signals"));
  await Task.Run(()=>{
   Vector3[] p,n;int[] ix;
   using(var r=new BinaryReader(File.OpenRead(Path.Combine(root,"source-aligned.bin")))){int nv=r.ReadInt32(),ni=r.ReadInt32();p=new Vector3[nv];n=new Vector3[nv];ix=new int[ni];for(int i=0;i<nv;i++)p[i]=ReadV(r);for(int i=0;i<nv;i++)n[i]=ReadV(r);for(int i=0;i<ni;i++)ix[i]=r.ReadInt32();}
   float[,] fields;int fieldCount;
   using(var r=new BinaryReader(File.OpenRead(Path.Combine(root,"source-features.bin")))){int nv=r.ReadInt32();fieldCount=r.ReadInt32();fields=new float[nv,fieldCount];for(int i=0;i<nv;i++)for(int k=0;k<fieldCount;k++)fields[i,k]=r.ReadSingle();}
   var bvh=new TriangleBvh(p,ix);var geometry=A.GetType("SashaRX.UnityMeshLab.MeshGeometry");
   var faceN=(Vector3[])geometry.GetMethod("FaceNormals",F).Invoke(null,new object[]{p,ix});
   int winding=(int)A.GetType("SashaRX.UnityMeshLab.RemeshBaker").GetMethod("ProbeWinding",F).Invoke(null,new object[]{bvh,p,faceN,null});
   if(winding<0)for(int i=0;i<faceN.Length;i++)faceN[i]=-faceN[i];
   Debug.Log("SOURCE_WINDING "+winding);
   var sourceType=A.GetType("SashaRX.UnityMeshLab.RemeshSource");var source=Activator.CreateInstance(sourceType,true);
   Set(source,"positions",p);Set(source,"normals",n);Set(source,"indices",ix);Set(source,"diagonal",1f);Set(source,"groundNormal",Vector3.up);Set(source,"faceMaterials",new int[ix.Length/3]);
   var surface=sourceType.GetNestedType("Surface",F);var surfaces=Array.CreateInstance(surface,1);surfaces.SetValue(Activator.CreateInstance(surface,true),0);Set(source,"materials",surfaces);
   var aoType=A.GetType("SashaRX.UnityMeshLab.SourceAoBaker");var ao=new object[3];var sample=aoType.GetMethod("Sample",F);float[] radius={.01f,.05f,.2f};
   for(int k=0;k<3;k++)ao[k]=Activator.CreateInstance(aoType,F,null,new object[]{source,bvh,faceN,null,new SourceAoSettings{samples=128,radius=radius[k],normalMap=false,groundPlane=false,bias=.0001f}},CultureInfo.InvariantCulture);
   foreach(string name in new[]{"reference","simplify"}){
    string queryPath=Path.Combine(root,name=="reference"?"queries.bin":"simplify-queries.bin");if(!File.Exists(queryPath))continue;
    var target=Activator.CreateInstance(A.GetType("SashaRX.UnityMeshLab.RemeshNative").GetNestedType("Geometry",F),true);
    using(var r=new BinaryReader(File.OpenRead(Path.Combine(root,name+"-target.bin")))){int nv=r.ReadInt32(),ni=r.ReadInt32();var tp=new Vector3[nv];var ti=new int[ni];for(int i=0;i<nv;i++)tp[i]=ReadV(r);for(int i=0;i<ni;i++)ti[i]=r.ReadInt32();Set(target,"positions",tp);Set(target,"indices",ti);}
    Set(target,"normals",geometry.GetMethod("AveragedNormals",F).Invoke(null,new object[]{Get(target,"positions"),Get(target,"indices"),CancellationToken.None}));
    var cage=A.GetType("SashaRX.UnityMeshLab.RemeshBaker").GetMethod("BuildCage",F).Invoke(null,new object[]{target,.02f,2f,bvh,null,CancellationToken.None});
    var direction=cage.GetType().GetMethod("Direction",F);var reachMethod=cage.GetType().GetMethod("Reach",F);
    Query[] queries;
    using(var r=new BinaryReader(File.OpenRead(queryPath))){queries=new Query[r.ReadInt32()];for(int i=0;i<queries.Length;i++)queries[i]=new Query{edge=r.ReadInt32(),side=r.ReadInt32(),face=r.ReadInt32(),position=ReadV(r),normal=ReadV(r),weights=ReadV(r)};}
    var output=new float[queries.Length][];int misses=0,fallbacks=0;
    Parallel.For(0,queries.Length,new ParallelOptions{MaxDegreeOfParallelism=Math.Max(1,Environment.ProcessorCount-1)},i=>{
     var q=queries[i];Vector3 d=(Vector3)direction.Invoke(cage,new object[]{q.face,q.weights});float reach=(float)reachMethod.Invoke(cage,new object[]{q.face,q.weights});
     var ray=winding!=0 ? bvh.RaycastFacingFiltered(q.position+d*reach,-d,reach*2,faceN) : bvh.Raycast(q.position+d*reach,-d,reach*2);int face=ray.triangleIndex;Vector3 weights=ray.barycentric;int mode=0;
     if(face<0){var near=winding!=0 ? bvh.FindNearestNormalFiltered(q.position,d,faceN,0f,reach) : bvh.FindNearest(q.position,reach);face=near.triangleIndex;weights=near.barycentric;mode=1;Interlocked.Increment(ref fallbacks);}
     var value=new float[9+fieldCount+3];value[0]=q.edge;value[1]=q.side;value[2]=q.face;value[3]=face;value[4]=mode;value[8]=reach;
     if(face<0){Interlocked.Increment(ref misses);value[5]=float.NaN;output[i]=value;return;}
     int a=ix[face*3],b=ix[face*3+1],c=ix[face*3+2];var point=p[a]*weights.x+p[b]*weights.y+p[c]*weights.z;var sn=(n[a]*weights.x+n[b]*weights.y+n[c]*weights.z).normalized;
     value[5]=(point-q.position).magnitude;value[6]=Vector3.Dot(sn,q.normal);value[7]=Vector3.Dot(sn,d);
     for(int k=0;k<fieldCount;k++)value[9+k]=fields[a,k]*weights.x+fields[b,k]*weights.y+fields[c,k]*weights.z;
     for(int k=0;k<3;k++)value[9+fieldCount+k]=1f-(float)sample.Invoke(ao[k],new object[]{face,weights,q.edge,CancellationToken.None});output[i]=value;
    });
    using(var w=new BinaryWriter(File.Create(Path.Combine(root,name+"-projected.bin")))){w.Write(output.Length);w.Write(output[0].Length);foreach(var row in output)foreach(float v in row)w.Write(v);}
    Debug.Log("SOURCE_PROJECT_COMPLETE "+name+" queries="+queries.Length+" fallback="+fallbacks+" misses="+misses);
   }
  });EditorApplication.Exit(0);
 }catch(Exception e){Debug.LogException(e);EditorApplication.Exit(1);}}
}
