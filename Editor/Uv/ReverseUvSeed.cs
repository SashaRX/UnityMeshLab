using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class ReverseUvSeed
    {
        /// <summary>Preserve authored charts, repair their metric and pack one seed
        /// atlas. Global Unity charting is opt-in. Outputs remain detached.</summary>
        internal static Mesh[] Prepare(ReverseUvTransfer.Input[] inputs, int resolution, int padding,
            float texelsPerUnit, CancellationToken token, out int atlasSize, bool cutNarrowJunctions = false,
            bool rotateCharts = true, bool rotateChartsToAxis = false, bool compact = true,
            Action<float> preparedDensity = null, Action<int,int[],Vector3[]> preparedTopology = null,
            bool rebuildWithUnity = false)
        {
            if (resolution < 16 || resolution > 8192 || padding < 0 || padding >= resolution / 2
                || float.IsNaN(texelsPerUnit) || float.IsInfinity(texelsPerUnit) || texelsPerUnit < 0)
                throw new ArgumentException("Invalid reverse seed size, padding or density.");
            var corners = new List<Vector3>(); var owners = new List<int>(); var originalIndices = new List<int[]>();
            var localCorners=new List<Vector3>();
            var origin = inputs[0].toWorld.GetColumn(3);
            double area = 0;
            for (int node=0;node<inputs.Length;++node)
            {
                var input=inputs[node];
                token.ThrowIfCancellationRequested();
                var positions = ReverseUvTransfer.RelativePositions(input, origin); var triangles = input.mesh.triangles;
                var local=input.mesh.vertices;
                originalIndices.Add(triangles);
                foreach (int index in triangles) { corners.Add(positions[index]);localCorners.Add(local[index]);owners.Add(node); }
            }
            if (corners.Count == 0 || corners.Count > 150000)
                throw new InvalidOperationException("Reverse seed preparation supports 1–50000 faces in the combined coarsest LOD.");
            for (int t=0;t<corners.Count;t+=3)
            {
                if(!MeshGeometry.HasArea(corners[t],corners[t+1],corners[t+2])) throw new InvalidOperationException($"Reverse seed face {t / 3} has a degenerate geometric triangle.");
                var frame=ReverseUvTransfer.TriangleFrame(corners[t],corners[t+1],corners[t+2]);
                area+=frame.length*frame.y*.5;
            }
            var unique = new Dictionary<(int,Vector3),int>(); var vertices = new List<Vector3>(); var indices = new int[corners.Count];
            for(int i=0;i<indices.Length;++i)
            {
                var key=(owners[i],corners[i]);
                if(!unique.TryGetValue(key,out int vertex)) { vertex=vertices.Count; vertices.Add(corners[i]); unique.Add(key,vertex); }
                indices[i]=vertex;
            }
            var temporary = new Mesh { indexFormat=UnityEngine.Rendering.IndexFormat.UInt32 };
            var output = new Mesh[inputs.Length];
            try
            {
                temporary.SetVertices(vertices); temporary.triangles=indices;
                int[] authoredRegions = null;
                Vector2[] uv;
                if (rebuildWithUnity)
                {
                    UnwrapParam.SetDefaults(out var settings); settings.packMargin=.02f;
                    uv=Unwrapping.GeneratePerTriangleUV(temporary,settings);
                }
                else uv=AuthoredCharts(inputs,corners,owners,originalIndices,token,out authoredRegions);
                if(uv.Length!=corners.Count) throw new InvalidOperationException("Reverse seed unwrap failed.");
                float density=texelsPerUnit>0?texelsPerUnit:(float)(resolution*Math.Sqrt(.6/area));
                var options=new ReverseUvTransfer.Options {seedResolution=resolution,padding=padding,cutNarrowJunctions=cutNarrowJunctions,
                    rotateCharts=rotateCharts,rotateChartsToAxis=rotateChartsToAxis};
                UvStripSubdivision.Result subdivision=null;
                if(cutNarrowJunctions && compact)
                {
                    var uvVertices=new List<Vector2>();var uvPositions=new List<Vector3>();var uvIndices=new int[uv.Length];
                    var uvUnique=new Dictionary<(int,Vector3,Vector2),int>();
                    for(int i=0;i<uv.Length;++i)
                    {
                        var key=(owners[i],corners[i],uv[i]);
                        if(!uvUnique.TryGetValue(key,out int v)) {v=uvVertices.Count;uvVertices.Add(uv[i]);uvPositions.Add(corners[i]);uvUnique.Add(key,v);}
                        uvIndices[i]=v;
                    }
                    subdivision=UvStripSubdivision.Build(uvVertices.ToArray(),uvIndices,token,uvPositions.ToArray(),
                        options.maxAtlasSize*1.1920928955078125e-7*8/density);
                    if(subdivision.cuts==0) subdivision=null;
                }
                var world=subdivision==null?corners.ToArray():subdivision.sourceFaces.SelectMany((f,i)=>Enumerable.Range(0,3)
                    .Select(k=>{
                        var b=subdivision.barycentrics[i*3+k];
                        return corners[f*3]*b.x+corners[f*3+1]*b.y+corners[f*3+2]*b.z;
                    })).ToArray();
                int[] sourceFaces=subdivision?.sourceFaces??Enumerable.Range(0,corners.Count/3).ToArray();
                var metricFrames=sourceFaces.Select((face,i)=>{
                    var local=new Vector3[3];
                    for(int k=0;k<3;++k)
                    {
                        var b=subdivision==null?(k==0?Vector3.right:k==1?Vector3.up:Vector3.forward):subdivision.barycentrics[i*3+k];
                        local[k]=localCorners[face*3]*b.x+localCorners[face*3+1]*b.y+localCorners[face*3+2]*b.z;
                    }
                    return TransferUvQuality.WorldFrame(local[0],local[1],local[2],inputs[owners[face*3]].toWorld);
                }).ToArray();
                var pixels=ReverseUvSeedPacking.Prepare(world,subdivision?.uv??uv,density,texelsPerUnit>0,options,token,out atlasSize,compact,
                    subdivision?.regions??authoredRegions,metricFrames);
                int finalSize=atlasSize;
                preparedDensity?.Invoke(options.seedDensity);
                int offset=0;
                var maps=new int[inputs.Length][];var baryMaps=new Vector3[inputs.Length][];
                for(int node=0;node<inputs.Length;++node)
                {
                    token.ThrowIfCancellationRequested();
                    if(subdivision==null)
                    {
                        var normalized=new Vector2[originalIndices[node].Length];
                        for(int i=0;i<normalized.Length;++i) normalized[i]=pixels[offset+i]/atlasSize;
                        output[node]=ReverseUvMesh.Copy(inputs[node].mesh,originalIndices[node],normalized);
                    }
                    else
                    {
                        int owner=node;
                        var selected=Enumerable.Range(0,subdivision.sourceFaces.Length).Where(f=>owners[subdivision.sourceFaces[f]*3]==owner).ToArray();
                        maps[node]=selected.Select(f=>subdivision.sourceFaces[f]-offset/3).ToArray();
                        baryMaps[node]=selected.SelectMany(f=>Enumerable.Range(0,3).Select(k=>subdivision.barycentrics[f*3+k])).ToArray();
                        var normalized=selected.SelectMany(f=>Enumerable.Range(0,3).Select(k=>pixels[f*3+k]/finalSize)).ToArray();
                        output[node]=ReverseUvRefinedMesh.Copy(inputs[node].mesh,maps[node],baryMaps[node],normalized);
                    }
                    offset+=originalIndices[node].Length;
                }
                if(subdivision!=null)
                    for(int node=0;node<inputs.Length;++node) preparedTopology?.Invoke(node,maps[node],baryMaps[node]);
                return output;
            }
            catch
            {
                foreach (var mesh in output)
                    if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
            finally { UnityEngine.Object.DestroyImmediate(temporary); }
        }

        // Chart-only topology: join matching 3D/UV edges, including false seams,
        // without welding the output mesh's normals, tangents or material slots.
        // Independent renderers and point contacts keep separate chart domains.
        static Vector2[] AuthoredCharts(ReverseUvTransfer.Input[] inputs, List<Vector3> corners,
            List<int> owners, List<int[]> originalIndices, CancellationToken token, out int[] regions)
        {
            var textureUv=inputs.Select(input=>input.mesh.uv).ToArray();
            var uv=new Vector2[corners.Count];
            int offset=0;
            for(int node=0;node<inputs.Length;++node)
                foreach(int vertex in originalIndices[node])
                {
                    var p=textureUv[node].Length==inputs[node].mesh.vertexCount?textureUv[node][vertex]:Vector2.zero;
                    uv[offset++]=float.IsNaN(p.x)||float.IsNaN(p.y)||float.IsInfinity(p.x)||float.IsInfinity(p.y)?Vector2.zero:p;
                }
            return RepairAuthoredCharts(corners.ToArray(),uv,owners.ToArray(),token,out regions);
        }

        internal static Vector2[] RepairAuthoredCharts(Vector3[] corners, Vector2[] uv, int[] owners,
            CancellationToken token, out int[] regions, bool joinPlanar=false)
        {
            if(joinPlanar) JoinPlanarCorners(corners,uv,owners,token);
            regions=AuthoredRegions(corners,uv,owners,token);
            var unique=new Dictionary<(int,Vector3,Vector2),int>();
            var positions=new List<Vector3>();var coordinates=new List<Vector2>();var indices=new int[uv.Length];
            for(int i=0;i<uv.Length;++i)
            {
                var key=(regions[i/3],corners[i],uv[i]);
                if(!unique.TryGetValue(key,out int v)) {v=coordinates.Count;unique.Add(key,v);coordinates.Add(uv[i]);positions.Add(corners[i]);}
                indices[i]=v;
            }
            var authored=coordinates.ToArray();
            var shells=UvShellExtractor.Extract(authored,indices);
            XatlasRepack.NormalizeShellWinding(authored,indices,shells);
            var flat=authored.SelectMany(p=>new[] {p.x,p.y}).ToArray();
            XatlasRepack.ReparameterizeStretchedShells(RepackOptions.Default,shells,indices,positions.ToArray(),flat,token);
            for(int i=0;i<uv.Length;++i) uv[i]=new Vector2(flat[indices[i]*2],flat[indices[i]*2+1]);
            UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Preserved source UV charts: {shells.Count}; local ARAP repair; Unity Unwrapping off.");
            return uv;
        }

        internal static int[] AuthoredRegions(Vector3[] corners,Vector2[] uv,int[] owners,CancellationToken token)
        {
            var connected=new DisjointSet(corners.Length/3);
            var edges=new Dictionary<(int,(Vector3,Vector2),(Vector3,Vector2)),int>();
            for(int f=0;f<corners.Length/3;++f)
            {
                token.ThrowIfCancellationRequested();
                for(int k=0;k<3;++k)
                {
                    int a=f*3+k,b=f*3+(k+1)%3;
                    var x=(corners[a],uv[a]);var y=(corners[b],uv[b]);
                    if(edges.TryGetValue((owners[a],x,y),out int other)||edges.TryGetValue((owners[a],y,x),out other)) connected.Union(f,other);
                    else edges.Add((owners[a],x,y),f);
                }
            }
            return Enumerable.Range(0,corners.Length/3).Select(connected.Find).ToArray();
        }

        // New lightmap-only patches can cross texture seams. Join geometry
        // edges inside one renderer only when the complete patch is planar;
        // curved patches retain authored parameterization and local ARAP repair.
        static void JoinPlanarCorners(Vector3[] corners,Vector2[] uv,int[] owners,CancellationToken token)
        {
            var union=new DisjointSet(corners.Length/3);
            var edges=new Dictionary<(int,(Vector3,Vector3)),List<int>>();
            var normals=new Vector3[corners.Length/3];
            for(int f=0;f<normals.Length;++f)
            {
                token.ThrowIfCancellationRequested();int t=f*3;
                normals[f]=MeshGeometry.UnitDirection(Vector3.Cross(corners[t+1]-corners[t],corners[t+2]-corners[t]));
                for(int k=0;k<3;++k)
                {
                    var key=(owners[t],ReverseUvCorrespondenceGraph.Key(corners[t+k],corners[t+(k+1)%3]));
                    if(!edges.TryGetValue(key,out var uses)) {uses=new List<int>();edges.Add(key,uses);}
                    uses.Add(f);
                }
            }
            foreach(var uses in edges.Values)
                if(uses.Count==2 && Vector3.Dot(normals[uses[0]],normals[uses[1]])>=.9999f) union.Union(uses[0],uses[1]);
            foreach(var group in Enumerable.Range(0,normals.Length).GroupBy(union.Find))
            {
                token.ThrowIfCancellationRequested();var faces=group.ToArray();
                if(faces.Length<2) continue;
                var origin=corners[faces[0]*3];var normal=normals[faces[0]];
                var all=faces.SelectMany(f=>Enumerable.Range(f*3,3)).ToArray();
                double span=all.Max(i=>(corners[i]-origin).magnitude);
                if(span<=0 || all.Any(i=>Math.Abs(Vector3.Dot(corners[i]-origin,normal))>span*.001)) continue;
                var tangent=MeshGeometry.UnitDirection(corners[faces[0]*3+1]-origin);
                var bitangent=Vector3.Cross(normal,tangent);
                foreach(int i in all)
                {
                    double x=(double)corners[i].x-origin.x,y=(double)corners[i].y-origin.y,z=(double)corners[i].z-origin.z;
                    uv[i]=new Vector2((float)(x*tangent.x+y*tangent.y+z*tangent.z),
                        (float)(x*bitangent.x+y*bitangent.y+z*bitangent.z));
                }
            }
        }
    }
}
