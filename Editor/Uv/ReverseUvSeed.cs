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
        /// <summary>One seed atlas in world metric, independent of UV0 fragmentation
        /// and source texture aspect. Outputs remain detached until the chain commits.</summary>
        internal static Mesh[] Prepare(ReverseUvTransfer.Input[] inputs, int resolution, int padding,
            float texelsPerUnit, CancellationToken token, out int atlasSize)
        {
            if (resolution < 16 || resolution > 8192 || padding < 0 || padding >= resolution / 2
                || float.IsNaN(texelsPerUnit) || float.IsInfinity(texelsPerUnit) || texelsPerUnit < 0)
                throw new ArgumentException("Invalid reverse seed size, padding or density.");
            var corners = new List<Vector3>(); var originalIndices = new List<int[]>();
            double area = 0;
            foreach (var input in inputs)
            {
                token.ThrowIfCancellationRequested();
                var positions = input.mesh.vertices; var triangles = input.mesh.triangles;
                originalIndices.Add(triangles);
                foreach (int index in triangles) corners.Add(input.toWorld.MultiplyPoint3x4(positions[index]));
            }
            if (corners.Count == 0 || corners.Count > 150000)
                throw new InvalidOperationException("Reverse seed preparation supports 1–50000 faces in the combined coarsest LOD.");
            for (int t=0;t<corners.Count;t+=3)
            {
                if(!MeshGeometry.HasArea(corners[t],corners[t+1],corners[t+2])) throw new InvalidOperationException($"Reverse seed face {t / 3} has a degenerate geometric triangle.");
                var frame=ReverseUvTransfer.TriangleFrame(corners[t],corners[t+1],corners[t+2]);
                area+=frame.length*frame.y*.5;
            }
            var unique = new Dictionary<Vector3,int>(); var vertices = new List<Vector3>(); var indices = new int[corners.Count];
            for(int i=0;i<indices.Length;++i)
            {
                if(!unique.TryGetValue(corners[i],out int vertex)) { vertex=vertices.Count; vertices.Add(corners[i]); unique.Add(corners[i],vertex); }
                indices[i]=vertex;
            }
            var temporary = new Mesh { indexFormat=UnityEngine.Rendering.IndexFormat.UInt32 };
            var output = new Mesh[inputs.Length];
            try
            {
                temporary.SetVertices(vertices); temporary.triangles=indices;
                UnwrapParam.SetDefaults(out var settings); settings.packMargin=.02f;
                var uv=Unwrapping.GeneratePerTriangleUV(temporary,settings);
                if(uv.Length!=corners.Count) throw new InvalidOperationException("Reverse seed unwrap failed.");
                float density=texelsPerUnit>0?texelsPerUnit:(float)(resolution*Math.Sqrt(.6/area));
                var options=new ReverseUvTransfer.Options {seedResolution=resolution,padding=padding};
                var pixels=ReverseUvNewCharts.Prepare(corners.ToArray(),uv,density,options,token,out _);
                var min=pixels[0]; var max=min;
                foreach(var p in pixels) { min=Vector2.Min(min,p); max=Vector2.Max(max,p); }
                float span=Math.Max(max.x-min.x,max.y-min.y);
                atlasSize=texelsPerUnit>0?Mathf.NextPowerOfTwo(Mathf.CeilToInt(span+padding*2)):resolution;
                if(atlasSize>8192) throw new InvalidOperationException("Reverse seed atlas exceeds 8192 pixels.");
                float scale=texelsPerUnit>0?1:(resolution-padding*2)/span;
                int offset=0;
                for(int node=0;node<inputs.Length;++node)
                {
                    token.ThrowIfCancellationRequested();
                    var normalized=new Vector2[originalIndices[node].Length];
                    for(int i=0;i<normalized.Length;++i) normalized[i]=((pixels[offset+i]-min)*scale+Vector2.one*padding)/atlasSize;
                    offset+=normalized.Length;
                    output[node]=ReverseUvMesh.Copy(inputs[node].mesh,originalIndices[node],normalized);
                }
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
    }
}
