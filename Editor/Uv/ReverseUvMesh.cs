using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Split only UV2 conflicts; copy every original vertex stream byte.
    /// Triangle order, material submeshes and all non-UV2 corner attributes survive.</summary>
    internal static class ReverseUvMesh
    {
        internal static Mesh Copy(Mesh source, int[] triangles, Vector2[] cornerUv)
        {
            var remap = new Dictionary<(int, Vector2), int>();
            var vertices = new List<int>(); var uv = new List<Vector2>();
            var indices = new int[triangles.Length];
            for (int i = 0; i < indices.Length; ++i)
            {
                var key = (triangles[i], cornerUv[i]);
                if (!remap.TryGetValue(key, out int vertex))
                {
                    vertex = vertices.Count; remap.Add(key, vertex);
                    vertices.Add(triangles[i]); uv.Add(cornerUv[i]);
                }
                indices[i] = vertex;
            }
            var output = new Mesh { name = source.name + "_reverse", hideFlags = HideFlags.HideAndDontSave,
                indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                using (var data = Mesh.AcquireReadOnlyMeshData(source))
                {
                    output.SetVertexBufferParams(vertices.Count, source.GetVertexAttributes());
                    for (int stream = 0; stream < source.vertexBufferCount; ++stream)
                    {
                        int stride = source.GetVertexBufferStride(stream);
                        var from = data[0].GetVertexData<byte>(stream).ToArray();
                        var bytes = new byte[checked(vertices.Count * stride)];
                        for (int i = 0; i < vertices.Count; ++i)
                            Buffer.BlockCopy(from, vertices[i] * stride, bytes, i * stride, stride);
                        output.SetVertexBufferData(bytes, 0, 0, bytes.Length, stream);
                    }
                }
                output.subMeshCount = source.subMeshCount;
                int offset = 0;
                for (int sub = 0; sub < source.subMeshCount; ++sub)
                {
                    if (source.GetTopology(sub) != MeshTopology.Triangles)
                        throw new InvalidOperationException("Reverse UV supports triangle submeshes only.");
                    int count = source.GetTriangles(sub).Length;
                    var part = new int[count]; Array.Copy(indices, offset, part, 0, count);
                    output.SetTriangles(part, sub, false); offset += count;
                }
                output.bindposes = source.bindposes;
                for (int shape = 0; shape < source.blendShapeCount; ++shape)
                    for (int frame = 0; frame < source.GetBlendShapeFrameCount(shape); ++frame)
                    {
                        var p = new Vector3[source.vertexCount]; var n = new Vector3[p.Length]; var t = new Vector3[p.Length];
                        source.GetBlendShapeFrameVertices(shape, frame, p, n, t);
                        var dp = new Vector3[vertices.Count]; var dn = new Vector3[dp.Length]; var dt = new Vector3[dp.Length];
                        for (int i = 0; i < dp.Length; ++i) { dp[i] = p[vertices[i]]; dn[i] = n[vertices[i]]; dt[i] = t[vertices[i]]; }
                        output.AddBlendShapeFrame(source.GetBlendShapeName(shape), source.GetBlendShapeFrameWeight(shape, frame), dp, dn, dt);
                    }
                output.SetUVs(1, uv);
                CopySkinWeights(source, output, vertices);
                output.bounds = source.bounds;
                MeshUvState.SetDraft(output, MeshUvState.IsDraft(source));
                return output;
            }
            catch { UnityEngine.Object.DestroyImmediate(output); throw; }
        }

        static void CopySkinWeights(Mesh source, Mesh output, List<int> vertices)
        {
            using var counts = source.GetBonesPerVertex();
            using var weights = source.GetAllBoneWeights();
            if (counts.Length == 0 || weights.Length == 0) return;
            var offsets = new int[counts.Length];
            int total = 0, offset = 0;
            for (int i = 0; i < counts.Length; ++i) { offsets[i] = offset; offset += counts[i]; }
            foreach (int vertex in vertices) total = checked(total + counts[vertex]);
            var mappedCounts = new NativeArray<byte>(vertices.Count, Allocator.Temp);
            var mappedWeights = new NativeArray<BoneWeight1>(total, Allocator.Temp);
            try
            {
                offset = 0;
                for (int i = 0; i < vertices.Count; ++i)
                {
                    int v = vertices[i]; mappedCounts[i] = counts[v];
                    for (int k = 0; k < counts[v]; ++k) mappedWeights[offset++] = weights[offsets[v] + k];
                }
                output.SetBoneWeights(mappedCounts, mappedWeights);
            }
            finally { mappedCounts.Dispose(); mappedWeights.Dispose(); }
        }
    }
}
