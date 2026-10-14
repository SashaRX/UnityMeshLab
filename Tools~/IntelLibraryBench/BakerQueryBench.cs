using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using UnityEngine;
using SashaRX.UnityMeshLab;

// Private end-to-end bake verification. Actual model geometry and coarser
// ordinary-transfer UV layouts, controlled CPU texture maps, no FBX writes.
public static class BakerQueryBench
{
    [Serializable] public class Layout {public int lod;public Vector3[] positions;public Vector2[] uv;public int[] indices;}
    [Serializable] public class Measurement {public string model,controlSha256,candidateSha256;public int size,covered,misses,uniqueColors,bands,rayQueries,nearestQueries;public bool allMapBytesAndCountersExact;}
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Type Type(string name)=>typeof(TriangleBvh).Assembly.GetType("SashaRX.UnityMeshLab."+name,true);
    static object New(Type type)=>Activator.CreateInstance(type,true);
    static object Get(object value,string field)=>value.GetType().GetField(field,Flags).GetValue(value);
    static void Set(object value,string field,object data)=>value.GetType().GetField(field,Flags).SetValue(value,data);
    static object Call(Type type,string method,params object[] args)=>type.GetMethod(method,Flags).Invoke(null,args);
    public static List<Measurement> Run(string root,EmbreeQueryBench.Create create,EmbreeQueryBench.Destroy destroy,EmbreeQueryBench.Queries proposals,EmbreeQueryBench.CandidateQueries candidates)
    {
        var output=new List<Measurement>();
        foreach(string model in new[]{"Cafe_Table_A_01","Modern_Kitchen_A","kamaz_Off"})foreach(int size in new[]{64,128}){
            var files=Directory.GetFiles(Path.Combine(root,model,"32","forward"),"*.layout.json").OrderBy(p=>p).ToArray();
            var layouts=files.Select(p=>JsonUtility.FromJson<Layout>(File.ReadAllText(p))).ToArray();
            int last=layouts.Max(l=>l.lod);var source=Source(layouts.Where(l=>l.lod==0).ToArray());
            var target=Geometry(layouts.Where(l=>l.lod==last).ToArray());
            var settings=new RemeshSettings{textureResolution=size,padding=2,dilationRadius=0,bakeSamples=1,
                cageFit=false,sourceBackfaces=RemeshBackfaces.FromMaterials,gpuProjection=false,bakeSourceAO=false};
            var baker=Type("RemeshBaker");var token=CancellationToken.None;var tangents=Get(target,"tangents");
            object control=Call(baker,"Bake",source,target,tangents,settings,token,null,null);
            object ctx=Call(baker,"Prepare",source,target,tangents,settings,token,null,null);
            object band=Activator.CreateInstance(baker.GetNestedType("Band",Flags),Flags,null,new object[]{ctx,null},null);
            var tree=(TriangleBvh)Get(ctx,"bvh");var normals=(Vector3[])Get(ctx,"faceNormals");bool facing=(bool)Get(ctx,"facingFilter");
            var two=(bool[])Get(ctx,"twoSided");var positions=(Vector3[])Get(source,"positions");var indices=(int[])Get(source,"indices");
            var usedNormals=facing?normals:new Vector3[indices.Length/3];var mask=new byte[indices.Length/3];if(facing && two!=null)for(int i=0;i<mask.Length;++i)mask[i]=(byte)(two[i]?1:0);
            var adapter=new ManagedCandidateBvh(tree,usedNormals,facing?two:null);
            IntPtr state=create(positions,positions.Length,indices,indices.Length,usedNormals,mask);if(state==IntPtr.Zero)throw new InvalidOperationException("Bake scene creation failed");
            var measurement=new Measurement{model=model,size=size,covered=(int)Get(control,"covered"),misses=(int)Get(control,"misses"),uniqueColors=new HashSet<Color32>((Color32[])Get(control,"color")).Count};output.Add(measurement);
            try {
                if(measurement.covered<=0 || measurement.uniqueColors<=1)throw new InvalidOperationException("Bake fixture has no textured coverage "+model);
                int pixels=((int[])Get(ctx,"owners")).Length;
                for(int pixel=0;pixel<pixels;pixel=(int)Get(band,"nextPixel")){
                    Call(baker,"BuildRequests",ctx,band,pixel,token);Call(baker,"ResolveCpu",ctx,band,token);
                    ResolveBand(ctx,band,adapter,state,proposals,candidates,measurement);
                    Call(baker,"EvaluateBand",ctx,band,token);++measurement.bands;
                }
                object actual=Call(baker,"Finish",ctx,token);
                byte[] before=Bytes(control),after=Bytes(actual);measurement.allMapBytesAndCountersExact=before.SequenceEqual(after);
                using(var sha=SHA256.Create()){measurement.controlSha256=BitConverter.ToString(sha.ComputeHash(before)).Replace("-","");measurement.candidateSha256=BitConverter.ToString(sha.ComputeHash(after)).Replace("-","");}
                if(!measurement.allMapBytesAndCountersExact)throw new InvalidOperationException("Bake maps/counters differ for "+model);
                if(size==128 && measurement.bands<2)throw new InvalidOperationException("Multi-band fixture did not reuse query buffers "+model);
                UvtLog.Info($"Embree bake {model} {size}px: {measurement.bands} actual bands, {measurement.rayQueries} rays, {measurement.nearestQueries} nearest; all map bytes/counters exact.");
            } finally {destroy(state);}
        }
        return output;
    }
    static object Geometry(Layout[] layouts)
    {
        var positions=new List<Vector3>();var uv=new List<Vector2>();var indices=new List<int>();
        foreach(var layout in layouts){int first=positions.Count;positions.AddRange(layout.positions);uv.AddRange(layout.uv);indices.AddRange(layout.indices.Select(i=>i+first));}
        var mesh=new Mesh{indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
        try{mesh.vertices=positions.ToArray();mesh.triangles=indices.ToArray();mesh.uv=uv.ToArray();mesh.RecalculateNormals();mesh.RecalculateTangents();
            var result=New(Type("RemeshNative").GetNestedType("Geometry",Flags));Set(result,"positions",mesh.vertices);Set(result,"indices",mesh.triangles);Set(result,"uv",mesh.uv);Set(result,"normals",mesh.normals);Set(result,"tangents",mesh.tangents);return result;
        }finally{UnityEngine.Object.DestroyImmediate(mesh);}
    }
    static object Source(Layout[] layouts)
    {
        object geometry=Geometry(layouts);var source=New(Type("RemeshSource"));
        foreach(string field in new[]{"positions","indices","normals","tangents","uv"})Set(source,field,Get(geometry,field));
        var positions=(Vector3[])Get(source,"positions");var indices=(int[])Get(source,"indices");
        var bounds=new Bounds(positions[0],Vector3.zero);foreach(var p in positions)bounds.Encapsulate(p);Set(source,"diagonal",bounds.size.magnitude);
        Set(source,"colors",Enumerable.Repeat(Color.white,positions.Length).ToArray());Set(source,"faceMaterials",new int[indices.Length/3]);
        var sourceType=source.GetType();object surface=New(sourceType.GetNestedType("Surface",Flags));var mapType=sourceType.GetNestedType("Map",Flags);
        foreach(string field in new[]{"color","normal","metal","ao","emission"})Set(surface,field,New(mapType));
        Set(surface,"tint",Color.white);Set(surface,"emissionTint",Color.black);Set(surface,"normalScale",1f);Set(surface,"aoStrength",1f);
        object image=New(sourceType.GetNestedType("Image",Flags));var pixels=new Color32[16*16];
        for(int y=0;y<16;++y)for(int x=0;x<16;++x)pixels[y*16+x]=new Color32((byte)(x*17),(byte)(y*17),(byte)((x^y)*17),255);
        Set(image,"width",16);Set(image,"height",16);Set(image,"pixels",pixels);Set(image,"wrapU",TextureWrapMode.Repeat);Set(image,"wrapV",TextureWrapMode.Repeat);Set(Get(surface,"color"),"image",image);
        Array surfaces=Array.CreateInstance(surface.GetType(),1);surfaces.SetValue(surface,0);Set(source,"materials",surfaces);return source;
    }
    static EmbreeQueryBench.Result[] Batch(EmbreeQueryBench.Query[] queries,ManagedCandidateBvh adapter,IntPtr state,EmbreeQueryBench.Queries proposal,EmbreeQueryBench.CandidateQueries candidate)
    {
        const int capacity=256;var guesses=new EmbreeQueryBench.Result[queries.Length];var regions=new EmbreeQueryBench.Query[queries.Length];
        var ids=new int[checked(queries.Length*capacity)];var counts=new int[queries.Length];var results=new EmbreeQueryBench.Result[queries.Length];
        if(queries.Length==0)return results;
        if(proposal(state,queries,guesses,queries.Length,8)!=0)throw new InvalidOperationException("Bake proposal refused");
        for(int i=0;i<queries.Length;++i)regions[i]=adapter.Region(queries[i],guesses[i]);
        if(candidate(state,regions,ids,counts,queries.Length,capacity,8)!=0)throw new InvalidOperationException("Bake candidates refused");
        Parallel.For(0,queries.Length,new ParallelOptions{MaxDegreeOfParallelism=8},()=>new int[capacity*2],(i,loop,scratch)=>{results[i]=adapter.Resolve(queries[i],ids,i*capacity,counts[i],scratch);return scratch;},scratch=>{});return results;
    }
    static void ResolveBand(object ctx,object band,ManagedCandidateBvh adapter,IntPtr state,EmbreeQueryBench.Queries proposal,EmbreeQueryBench.CandidateQueries candidates,Measurement measurement)
    {
        int count=(int)Get(band,"count");bool facing=(bool)Get(ctx,"facingFilter"),proxy=(bool)Get(ctx,"proxy");
        var origins=(Vector4[])Get(band,"rayOrigin");var directions=(Vector4[])Get(band,"rayDir");var points=(Vector4[])Get(band,"point");var normals=(Vector4[])Get(band,"pointNormal");
        var ray=(Array)Get(band,"rayHit");var nearest=(Array)Get(band,"nearest");var queries=new EmbreeQueryBench.Query[count];
        for(int i=0;i<count;++i)queries[i]=new EmbreeQueryBench.Query{mode=proxy?(facing?1:0):2,origin=origins[i],direction=directions[i],reach=origins[i].w,target=directions[i].w};
        var hits=Batch(queries,adapter,state,proposal,candidates);measurement.rayQueries+=count;
        var missing=new List<int>();
        for(int i=0;i<count;++i){var expected=ray.GetValue(i);var actual=hits[i];
            if((int)Get(expected,"tri")!=actual.tri || !Bits((float)Get(expected,"t"),actual.distance) || !Bits((float)Get(expected,"u"),actual.bary.y) || !Bits((float)Get(expected,"v"),actual.bary.z))
                throw new InvalidOperationException("Actual bake ray band differs "+measurement.model+" #"+i);
            Set(expected,"tri",actual.tri);Set(expected,"t",actual.distance);Set(expected,"u",actual.bary.y);Set(expected,"v",actual.bary.z);ray.SetValue(expected,i);
            if(actual.tri<0)missing.Add(i);else if((int)Get(nearest.GetValue(i),"tri")!=-1)throw new InvalidOperationException("Unexpected nearest lookup after ray hit");
        }
        queries=new EmbreeQueryBench.Query[missing.Count];for(int j=0;j<missing.Count;++j){int i=missing[j];queries[j]=new EmbreeQueryBench.Query{mode=facing?4:3,origin=points[i],direction=normals[i],reach=points[i].w,dotMin=0};}
        hits=Batch(queries,adapter,state,proposal,candidates);measurement.nearestQueries+=missing.Count;
        for(int j=0;j<missing.Count;++j){int i=missing[j];var expected=nearest.GetValue(i);var actual=hits[j];
            if((int)Get(expected,"tri")!=actual.tri || !Bits((float)Get(expected,"distSq"),actual.distance) || !Exact((Vector3)Get(expected,"point"),actual.point) || !Exact((Vector3)Get(expected,"bary"),actual.bary))
                throw new InvalidOperationException("Actual bake nearest band differs "+measurement.model+" #"+i);
            Set(expected,"tri",actual.tri);Set(expected,"distSq",actual.distance);Set(expected,"point",actual.point);Set(expected,"bary",actual.bary);nearest.SetValue(expected,i);
        }
    }
    static bool Bits(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
    static bool Exact(Vector3 a,Vector3 b)=>Bits(a.x,b.x)&&Bits(a.y,b.y)&&Bits(a.z,b.z);
    static byte[] Bytes(object maps)
    {
        using var memory=new MemoryStream();using var writer=new BinaryWriter(memory);
        foreach(var field in maps.GetType().GetFields(Flags).Where(f=>!f.IsStatic).OrderBy(f=>f.Name)){
            writer.Write(field.Name);object value=field.GetValue(maps);
            if(value==null){writer.Write(-1);continue;}if(value is Color32[] bytes){writer.Write(bytes.Length);foreach(var c in bytes){writer.Write(c.r);writer.Write(c.g);writer.Write(c.b);writer.Write(c.a);}}
            else if(value is Color[] colors){writer.Write(colors.Length);foreach(var c in colors){writer.Write(c.r);writer.Write(c.g);writer.Write(c.b);writer.Write(c.a);}}
            else if(value is int i)writer.Write(i);else if(value is long l)writer.Write(l);else if(value is bool b)writer.Write(b);
            else if(value is float f)writer.Write(f);else if(value is double d)writer.Write(d);else throw new InvalidOperationException("Unknown bake field "+field.Name);
        }writer.Flush();return memory.ToArray();
    }
}
