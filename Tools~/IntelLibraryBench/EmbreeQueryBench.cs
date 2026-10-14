using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using SashaRX.UnityMeshLab;

// Copy only into a closed isolated test project's Assets/Editor. This driver
// reads fixtures and exercises queries; it never assigns or saves a model mesh.
public static class EmbreeQueryBench
{
    [Serializable,StructLayout(LayoutKind.Sequential)] public struct Query {public Vector3 origin,direction;public float reach,target,dotMin;public int mode;}
    [Serializable,StructLayout(LayoutKind.Sequential)] public struct Result {public int tri;public float distance;public Vector3 bary,point;}
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr Create([In] Vector3[] vertices,int vertexCount,[In] int[] indices,int indexCount,[In] Vector3[] normals,[In] byte[] either);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void Destroy(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int Queries(IntPtr handle,[In] Query[] queries,[Out] Result[] results,int count,int threads);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AttachTree(IntPtr handle,[In] TriangleBvh.GPUNode[] nodes,int nodeCount,[In] int[] order,int faceCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CompatibleQueries(IntPtr handle,[In] Query[] queries,[Out] Result[] results,int count,int threads,int guided);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int CandidateQueries(IntPtr handle,[In] Query[] queries,[Out] int[] candidates,[Out] int[] counts,int count,int capacity,int threads);
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryExW(string path,IntPtr file,uint flags);
    [DllImport("kernel32",CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr module);
    [Serializable] public class Difference {public int index;public Query query;public Result control,actual;}
    [Serializable] public class Measurement {public string model,mode;public int triangles,queries,threads,hitMismatch,centerHitMismatch,boundaryHitMismatch,faceMismatch,parameterMismatch,uvMismatch,exactResults,candidateFallbacks,candidateMaximum;public long candidateTotal;public double[] managedMs,nativeMs;public float maxParameterError,maxUvError;public List<Difference> examples=new List<Difference>();}
    [Serializable] public class Model {public string name;public int vertices,triangles;public double managedBuildMs,nativeBuildMs,treeUploadMs;public List<Measurement> measurements=new List<Measurement>();}
    [Serializable] public class Report {public string library,backend;public List<Model> models=new List<Model>();public List<BakerQueryBench.Measurement> bakes=new List<BakerQueryBench.Measurement>();}
    static AttachTree attachTree;
    static CandidateQueries candidateQueries;
    static T Function<T>(IntPtr module,string name) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(GetProcAddress(module,name));
    public static void Run()
    {
        if(!Application.isBatchMode)throw new InvalidOperationException("Private batch project required.");
        string library=Path.GetFullPath(Environment.GetEnvironmentVariable("MESHLAB_EMBREE_BENCH_LIBRARY"));
        string output=Path.GetFullPath(Environment.GetEnvironmentVariable("MESHLAB_EMBREE_BENCH_OUTPUT"));
        IntPtr module=LoadLibraryExW(library,IntPtr.Zero,8);if(module==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
        try {
            var create=Function<Create>(module,"meshLabEmbreeCreate");var destroy=Function<Destroy>(module,"meshLabEmbreeDestroy");var queries=Function<Queries>(module,"meshLabEmbreeQueries");
            string backend=Environment.GetEnvironmentVariable("MESHLAB_EMBREE_BENCH_BACKEND")??"raw";
            if(backend=="candidates")candidateQueries=Function<CandidateQueries>(module,"meshLabEmbreeCandidates");
            else if(backend!="raw"){
                if(backend!="native" && backend!="guided")throw new InvalidOperationException("Unknown query backend "+backend);
                attachTree=Function<AttachTree>(module,"meshLabEmbreeLegacyTree");var compatible=Function<CompatibleQueries>(module,"meshLabCompatibleQueries");
                queries=(handle,q,r,count,threads)=>compatible(handle,q,r,count,threads,backend=="guided"?1:0);
            }
            var report=new Report {library=library,backend=backend};
            Analytic(report,create,destroy,queries);
            Adversarial(report,create,destroy,queries);
            foreach(string path in new[]{"Assets/JunctionFixtures/Cafe_Table_A_01.fbx","Assets/TransferBenchmarkInputs/Modern_Kitchen_A.fbx","Assets/KamazFixtures/kamaz_Off.fbx"})
                ModelFixture(path,report,create,destroy,queries);
            string bakeLayouts=Environment.GetEnvironmentVariable("MESHLAB_EMBREE_BAKE_LAYOUTS");
            if(!string.IsNullOrEmpty(bakeLayouts)){
                if(candidateQueries==null)throw new InvalidOperationException("Bake verification requires the candidate backend.");
                report.bakes=BakerQueryBench.Run(bakeLayouts,create,destroy,queries,candidateQueries);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output));File.WriteAllText(output,JsonUtility.ToJson(report,true));
            if(backend!="raw" && report.models.SelectMany(m=>m.measurements).Any(m=>m.exactResults!=m.queries))
                throw new InvalidOperationException("Compatible query backend changed exact result bits; audit: "+output);
            UvtLog.Info("Embree query experiment complete: "+output);
        }finally {attachTree=null;candidateQueries=null;FreeLibrary(module);}
    }
    static void Analytic(Report report,Create create,Destroy destroy,Queries queries)
    {
        // Parallel layers verify target-relative selection, filtering and misses;
        // same-distance opposing faces expose topology/UV tie sensitivity.
        var vertices=new List<Vector3>();var indices=new List<int>();var uv=new List<Vector2>();
        foreach(float z in new[]{0f,.01f,.03f}){
            int start=vertices.Count;vertices.AddRange(new[]{new Vector3(-1,-1,z),new Vector3(1,-1,z),new Vector3(1,1,z),new Vector3(-1,1,z)});
            uv.AddRange(new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up});indices.AddRange(new[]{start,start+1,start+2,start,start+2,start+3});
        }
        var points=new List<Vector3>();var directions=new List<Vector3>();
        for(int i=0;i<1024;++i){points.Add(new Vector3((i%32-15.3f)/18,(i/32-15.7f)/18,.01f));directions.Add(i%2==0?Vector3.forward:Vector3.back);}
        Compare("analytic-layers",vertices.ToArray(),indices.ToArray(),uv.ToArray(),points.ToArray(),directions.ToArray(),report,create,destroy,queries);
    }
    static void ModelFixture(string path,Report report,Create create,Destroy destroy,Queries queries)
    {
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);if(!prefab)throw new InvalidOperationException("Missing fixture "+path);
        var vertices=new List<Vector3>();var indices=new List<int>();var uv=new List<Vector2>();var points=new List<Vector3>();var directions=new List<Vector3>();
        var inverse=prefab.transform.worldToLocalMatrix;
        foreach(var filter in prefab.GetComponentsInChildren<MeshFilter>(true)){
            var mesh=filter.sharedMesh;if(!mesh)continue;
            var match=System.Text.RegularExpressions.Regex.Match(mesh.name,@"_LOD(\d+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if(!match.Success)continue;int lod=int.Parse(match.Groups[1].Value);
            var frame=inverse*filter.transform.localToWorldMatrix;var p=mesh.vertices.Select(frame.MultiplyPoint3x4).ToArray();var t=mesh.triangles;
            if(lod==0){int start=vertices.Count;vertices.AddRange(p);indices.AddRange(t.Select(i=>i+start));var channel=mesh.uv;uv.AddRange(channel.Length==p.Length?channel:new Vector2[p.Length]);}
            else {
                int step=Math.Max(1,t.Length/3/8192);
                for(int face=0;face<t.Length/3;face+=step){Vector3 a=p[t[face*3]],b=p[t[face*3+1]],c=p[t[face*3+2]];Vector3 n=Vector3.Cross(b-a,c-a).normalized;
                    if(n.sqrMagnitude<.5f)continue;points.Add((a+b+c)/3);directions.Add(n);
                    // Edge/vertex probes deliberately include seam and thin-part ties.
                    points.Add(a);directions.Add(n);
                }
            }
        }
        Compare(Path.GetFileNameWithoutExtension(path),vertices.ToArray(),indices.ToArray(),uv.ToArray(),points.ToArray(),directions.ToArray(),report,create,destroy,queries);
    }
    static void Adversarial(Report report,Create create,Destroy destroy,Queries queries)
    {
        foreach(var setup in new[]{(scale:.0001f,offset:Vector3.zero,label:"tiny"),(scale:1f,offset:Vector3.zero,label:"unit"),
            (scale:1000f,offset:Vector3.zero,label:"large"),(scale:1f,offset:new Vector3(100000,100000,100000),label:"offset")}){
            var v=new List<Vector3>();var t=new List<int>();var uv=new List<Vector2>();
            foreach(float z in new[]{0f,.01f,.03f,.03f}){int first=v.Count;
                v.AddRange(new[]{new Vector3(-1,-1,z),new Vector3(1,-1,z),new Vector3(1,1,z),new Vector3(-1,1,z)}.Select(p=>setup.offset+setup.scale*p));
                t.AddRange(new[]{first,first+1,first+2,first,first+2,first+3});uv.AddRange(new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up}.Select(p=>p+new Vector2(z*10,z*20)));}
            int start=v.Count;v.AddRange(new[]{setup.offset,setup.offset+setup.scale*Vector3.right,setup.offset+setup.scale*Vector3.right*.5f});
            uv.AddRange(new[]{Vector2.zero,Vector2.right,Vector2.one});t.AddRange(new[]{start,start+1,start+2,start,start,start});
            var points=new Vector3[256];var normals=new Vector3[points.Length];
            for(int i=0;i<points.Length;++i){Vector3 p=i%4==0?new Vector3(-1,-1,.01f):i%4==1?new Vector3(0,0,.03f):new Vector3((i%16-7.2f)/9,(i/16-7.6f)/9,.01f);
                points[i]=setup.offset+setup.scale*p;normals[i]=i%2==0?Vector3.forward:Vector3.back;}
            Compare("analytic-"+setup.label,v.ToArray(),t.ToArray(),uv.ToArray(),points,normals,report,create,destroy,queries,setup.scale,true);
        }
        var positions=new Vector3[1024*3];var indices=new int[positions.Length];var overlaps=new Vector2[positions.Length];
        for(int i=0;i<1024;++i){positions[i*3]=new Vector3(-1,-1,0);positions[i*3+1]=new Vector3(1,-1,0);positions[i*3+2]=new Vector3(0,1,0);
            for(int c=0;c<3;++c){indices[i*3+c]=i*3+c;overlaps[i*3+c]=new Vector2(i/1024f,c*.2f);}}
        var probes=new Vector3[128];var directions=new Vector3[128];for(int i=0;i<probes.Length;++i){probes[i]=new Vector3(0,0,.01f);directions[i]=i%2==0?Vector3.forward:Vector3.back;}
        Compare("analytic-capacity-overflow",positions,indices,overlaps,probes,directions,report,create,destroy,queries);
    }
    static void Compare(string name,Vector3[] vertices,int[] indices,Vector2[] uv,Vector3[] points,Vector3[] normals,Report report,Create create,Destroy destroy,Queries nativeQueries,float scale=1,bool varied=false)
    {
        if(vertices.Length==0 || points.Length==0)throw new InvalidOperationException("Empty query fixture "+name);
        var faceNormals=new Vector3[indices.Length/3];var either=new byte[faceNormals.Length];var eitherBool=new bool[faceNormals.Length];
        for(int i=0;i<faceNormals.Length;++i){faceNormals[i]=Vector3.Cross(vertices[indices[i*3+1]]-vertices[indices[i*3]],vertices[indices[i*3+2]]-vertices[indices[i*3]]).normalized;eitherBool[i]=i%17==0;either[i]=(byte)(eitherBool[i]?1:0);}
        var model=new Model{name=name,vertices=vertices.Length,triangles=faceNormals.Length};report.models.Add(model);
        var timer=Stopwatch.StartNew();var managed=new TriangleBvh(vertices,indices);model.managedBuildMs=timer.Elapsed.TotalMilliseconds;
        timer.Restart();IntPtr state=create(vertices,vertices.Length,indices,indices.Length,faceNormals,either);model.nativeBuildMs=timer.Elapsed.TotalMilliseconds;
        if(state==IntPtr.Zero)throw new InvalidOperationException("Embree scene creation failed "+name);
        try {
            var candidateTree=candidateQueries==null?null:new ManagedCandidateBvh(managed,faceNormals,eitherBool);
            if(attachTree!=null){
                if(Marshal.SizeOf<TriangleBvh.GPUNode>()!=40)throw new InvalidOperationException("Unexpected BVH node ABI");
                managed.GetGPUData(out var nodes,out var order,out _,out _);timer.Restart();
                if(attachTree(state,nodes,nodes.Length,order,order.Length)!=0)throw new InvalidOperationException("Legacy tree upload refused "+name);
                model.treeUploadMs=timer.Elapsed.TotalMilliseconds;
            }
            foreach(int mode in new[]{0,1,2,3,4})foreach(int threads in new[]{1,8}){
                var query=new Query[points.Length];var control=new Result[query.Length];var actual=new Result[query.Length];
                for(int i=0;i<query.Length;++i){query[i]=new Query{mode=mode,origin=mode<3?points[i]+.025f*scale*normals[i]:points[i]+.007f*scale*normals[i],direction=mode<3?-normals[i]:normals[i],reach=.1f*scale,target=.025f*scale,dotMin=.25f};
                    if(varied){if(i%7==0)query[i].reach=0;else if(i%7==1)query[i].reach=-.03f*scale;else if(i%7==2)query[i].reach=float.PositiveInfinity;
                        if(i%4==0)query[i].target=0;else if(i%4==1)query[i].target=-scale;else if(i%4==2)query[i].target=2*query[i].reach;}}
                var measurement=new Measurement{model=name,mode=new[]{"ray","ray-facing","ray-target","nearest","nearest-normal"}[mode],triangles=faceNormals.Length,queries=query.Length,threads=threads,managedMs=new double[3],nativeMs=new double[3]};model.measurements.Add(measurement);
                const int candidateCapacity=256;var candidateBuffer=candidateTree==null?null:new int[checked(query.Length*candidateCapacity)];var candidateCounts=candidateTree==null?null:new int[query.Length];
                var proposals=candidateTree==null?null:new Result[query.Length];var regions=candidateTree==null?null:new Query[query.Length];
                int NativeRun(){
                    if(candidateTree==null)return nativeQueries(state,query,actual,query.Length,threads);
                    int status=nativeQueries(state,query,proposals,query.Length,threads);if(status!=0)return status;
                    if(threads==1)for(int i=0;i<query.Length;++i)regions[i]=candidateTree.Region(query[i],proposals[i]);
                    else Parallel.For(0,query.Length,new ParallelOptions{MaxDegreeOfParallelism=threads},i=>regions[i]=candidateTree.Region(query[i],proposals[i]));
                    status=candidateQueries(state,regions,candidateBuffer,candidateCounts,query.Length,candidateCapacity,threads);if(status!=0)return status;
                    if(threads==1){var scratch=new int[candidateCapacity*2];for(int i=0;i<query.Length;++i)actual[i]=candidateTree.Resolve(query[i],candidateBuffer,i*candidateCapacity,candidateCounts[i],scratch);}
                    else Parallel.For(0,query.Length,new ParallelOptions{MaxDegreeOfParallelism=threads},()=>new int[candidateCapacity*2],(i,loop,scratch)=>{
                        actual[i]=candidateTree.Resolve(query[i],candidateBuffer,i*candidateCapacity,candidateCounts[i],scratch);return scratch;},scratch=>{});
                    return 0;
                }
                Action<int> resolve=i=>{var q=query[i];
                    if(mode<3){var hit=mode==0?managed.Raycast(q.origin,q.direction,q.reach):mode==1?managed.RaycastFacingFiltered(q.origin,q.direction,q.reach,faceNormals,eitherBool):managed.RaycastClosestToTarget(q.origin,q.direction,q.reach,q.target,faceNormals,eitherBool);
                        control[i]=new Result{tri=hit.triangleIndex,distance=hit.t,bary=hit.barycentric,point=hit.triangleIndex>=0?q.origin+hit.t*q.direction:Vector3.zero};}
                    else {var hit=mode==3?managed.FindNearest(q.origin,q.reach):managed.FindNearestNormalFiltered(q.origin,q.direction,faceNormals,q.dotMin,q.reach,eitherBool);control[i]=new Result{tri=hit.triangleIndex,distance=hit.distSq,bary=hit.barycentric,point=hit.point};}};
                Action managedRun=()=>{if(threads==1)for(int i=0;i<query.Length;++i)resolve(i);else Parallel.For(0,query.Length,new ParallelOptions{MaxDegreeOfParallelism=threads},resolve);};
                managedRun();if(NativeRun()!=0)throw new InvalidOperationException("Native query refused");
                for(int repeat=0;repeat<3;++repeat){
                    if(repeat%2==0){timer.Restart();managedRun();measurement.managedMs[repeat]=timer.Elapsed.TotalMilliseconds;timer.Restart();if(NativeRun()!=0)throw new InvalidOperationException("Native query refused");measurement.nativeMs[repeat]=timer.Elapsed.TotalMilliseconds;}
                    else {timer.Restart();if(NativeRun()!=0)throw new InvalidOperationException("Native query refused");measurement.nativeMs[repeat]=timer.Elapsed.TotalMilliseconds;timer.Restart();managedRun();measurement.managedMs[repeat]=timer.Elapsed.TotalMilliseconds;}
                }
                if(candidateCounts!=null)foreach(int count in candidateCounts){if(count<0)++measurement.candidateFallbacks;else{measurement.candidateTotal+=count;measurement.candidateMaximum=Math.Max(measurement.candidateMaximum,count);}}
                for(int i=0;i<query.Length;++i){var a=control[i];var b=actual[i];
                    if((a.tri<0)!=(b.tri<0)){++measurement.hitMismatch;if(i%2==0)++measurement.centerHitMismatch;else ++measurement.boundaryHitMismatch;
                        if(measurement.examples.Count<8)measurement.examples.Add(new Difference{index=i,query=query[i],control=a,actual=b});continue;}
                    bool exact=a.tri==b.tri && Bits(a.distance,b.distance) && Exact(a.bary,b.bary) && Exact(a.point,b.point);
                    if(exact)++measurement.exactResults;
                    if(a.tri<0)continue;
                    if(a.tri!=b.tri)++measurement.faceMismatch;
                    float error=Mathf.Abs(a.distance-b.distance);measurement.maxParameterError=Mathf.Max(measurement.maxParameterError,error);if(error>1e-5f)++measurement.parameterMismatch;
                    float uvError=Vector2.Distance(Uv(a,indices,uv),Uv(b,indices,uv));measurement.maxUvError=Mathf.Max(measurement.maxUvError,uvError);if(uvError>1e-5f)++measurement.uvMismatch;
                    if((error>1e-5f || uvError>1e-5f) && measurement.examples.Count<8)measurement.examples.Add(new Difference{index=i,query=query[i],control=a,actual=b});
                    if(!exact && measurement.examples.Count<8)measurement.examples.Add(new Difference{index=i,query=query[i],control=a,actual=b});
                }
                UvtLog.Info($"Embree {name} {measurement.mode} x{threads}: hit differences {measurement.hitMismatch}, face differences {measurement.faceMismatch}, UV differences {measurement.uvMismatch}");
            }
        }finally {destroy(state);}
    }
    static bool Bits(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
    static bool Exact(Vector3 a,Vector3 b)=>Bits(a.x,b.x)&&Bits(a.y,b.y)&&Bits(a.z,b.z);
    static Vector2 Uv(Result hit,int[] indices,Vector2[] uv)=>hit.bary.x*uv[indices[hit.tri*3]]+hit.bary.y*uv[indices[hit.tri*3+1]]+hit.bary.z*uv[indices[hit.tri*3+2]];
}
