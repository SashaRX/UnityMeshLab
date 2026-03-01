// MeshOptimizer.cs — C# wrapper for meshoptimizer native bridge
// Optimizes Unity Mesh: vertex dedup + cache + overdraw + fetch
// Place in Assets/Editor/

using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace LightmapUvTool
{
    public static class MeshOptimizer
    {
        public struct OptimizeResult
        {
            public bool   ok;
            public string error;
            public int    originalVertexCount;
            public int    optimizedVertexCount;
            public int    submeshCount;
        }

        // 76 bytes, position MUST be first (meshopt_optimizeOverdraw reads float3 at offset 0)
        [StructLayout(LayoutKind.Sequential)]
        struct InterleavedVertex
        {
            public Vector3 position;  // 12 bytes — offset 0
            public Vector3 normal;    // 12 bytes — offset 12
            public Vector4 tangent;   // 16 bytes — offset 24
            public Color32 color;     //  4 bytes — offset 40
            public Vector2 uv0;       //  8 bytes — offset 44
            public Vector2 uv1;       //  8 bytes — offset 52
            public Vector2 uv2;       //  8 bytes — offset 60
            public Vector2 uv3;       //  8 bytes — offset 68
            // total: 76 bytes
        }

        const int VERTEX_STRIDE = 76;

        /// <summary>
        /// Run meshoptimizer full pipeline on a Unity Mesh (in-place).
        /// Each submesh is optimized independently. All vertex channels are preserved.
        /// Does NOT use meshopt simplify — LOD generation is handled elsewhere.
        /// </summary>
        /// <param name="mesh">Mesh to optimize in-place.</param>
        /// <param name="overdrawThreshold">Overdraw optimization threshold (1.05 typical).</param>
        public static OptimizeResult Optimize(Mesh mesh, float overdrawThreshold = 1.05f)
        {
            var result = new OptimizeResult();

            if (mesh == null)
            {
                result.error = "Mesh is null";
                return result;
            }

            int vertCount = mesh.vertexCount;
            int subCount  = mesh.subMeshCount;
            if (vertCount == 0 || subCount == 0)
            {
                result.error = "Mesh has no vertices or submeshes";
                return result;
            }

            result.originalVertexCount = vertCount;
            result.submeshCount = subCount;

            // ── Read all channels (only those present) ──
            bool hasPos     = mesh.HasVertexAttribute(VertexAttribute.Position);
            bool hasNormal  = mesh.HasVertexAttribute(VertexAttribute.Normal);
            bool hasTangent = mesh.HasVertexAttribute(VertexAttribute.Tangent);
            bool hasColor   = mesh.HasVertexAttribute(VertexAttribute.Color);
            bool hasUv0     = mesh.HasVertexAttribute(VertexAttribute.TexCoord0);
            bool hasUv1     = mesh.HasVertexAttribute(VertexAttribute.TexCoord1);
            bool hasUv2     = mesh.HasVertexAttribute(VertexAttribute.TexCoord2);
            bool hasUv3     = mesh.HasVertexAttribute(VertexAttribute.TexCoord3);

            if (!hasPos)
            {
                result.error = "Mesh has no position channel";
                return result;
            }

            Vector3[] positions = mesh.vertices;
            Vector3[] normals   = hasNormal  ? mesh.normals  : null;
            Vector4[] tangents  = hasTangent ? mesh.tangents  : null;
            Color32[] colors    = hasColor   ? mesh.colors32  : null;

            var uv0List = new List<Vector2>(); if (hasUv0) mesh.GetUVs(0, uv0List);
            var uv1List = new List<Vector2>(); if (hasUv1) mesh.GetUVs(1, uv1List);
            var uv2List = new List<Vector2>(); if (hasUv2) mesh.GetUVs(2, uv2List);
            var uv3List = new List<Vector2>(); if (hasUv3) mesh.GetUVs(3, uv3List);

            // ── Process each submesh independently ──
            var allOutPositions = new List<Vector3>();
            var allOutNormals   = hasNormal  ? new List<Vector3>() : null;
            var allOutTangents  = hasTangent ? new List<Vector4>() : null;
            var allOutColors    = hasColor   ? new List<Color32>() : null;
            var allOutUv0       = hasUv0     ? new List<Vector2>() : null;
            var allOutUv1       = hasUv1     ? new List<Vector2>() : null;
            var allOutUv2       = hasUv2     ? new List<Vector2>() : null;
            var allOutUv3       = hasUv3     ? new List<Vector2>() : null;

            var submeshTriangles = new List<int[]>();
            int totalOutVerts = 0;

            for (int s = 0; s < subCount; s++)
            {
                int[] subTris = mesh.GetTriangles(s);
                if (subTris.Length == 0)
                {
                    submeshTriangles.Add(new int[0]);
                    continue;
                }

                // Build global→local vertex mapping for this submesh
                var globalToLocal = new Dictionary<int, int>();
                for (int i = 0; i < subTris.Length; i++)
                {
                    int gi = subTris[i];
                    if (!globalToLocal.ContainsKey(gi))
                        globalToLocal[gi] = globalToLocal.Count;
                }

                int localVertCount = globalToLocal.Count;
                uint localIndexCount = (uint)subTris.Length;

                // Build local interleaved vertex buffer
                var localVerts = new InterleavedVertex[localVertCount];
                foreach (var kv in globalToLocal)
                {
                    int gi = kv.Key;
                    int li = kv.Value;
                    var v = new InterleavedVertex();
                    v.position = positions[gi];
                    if (normals   != null) v.normal  = normals[gi];
                    if (tangents  != null) v.tangent = tangents[gi];
                    if (colors    != null) v.color   = colors[gi];
                    if (uv0List.Count > gi) v.uv0 = uv0List[gi];
                    if (uv1List.Count > gi) v.uv1 = uv1List[gi];
                    if (uv2List.Count > gi) v.uv2 = uv2List[gi];
                    if (uv3List.Count > gi) v.uv3 = uv3List[gi];
                    localVerts[li] = v;
                }

                // Build local index buffer
                uint[] localIndices = new uint[subTris.Length];
                for (int i = 0; i < subTris.Length; i++)
                    localIndices[i] = (uint)globalToLocal[subTris[i]];

                // Marshal interleaved vertices to byte[]
                byte[] vertexBytes = VerticesToBytes(localVerts, localVertCount);

                // Allocate output buffers
                byte[] outVertexBytes = new byte[localVertCount * VERTEX_STRIDE];
                uint[] outIndices = new uint[localIndexCount];
                uint   outVertCount;

                // Call native meshoptimizer pipeline
                int err = MeshoptNative.meshoptOptimize(
                    vertexBytes, (uint)localVertCount, (uint)VERTEX_STRIDE,
                    localIndices, localIndexCount,
                    overdrawThreshold,
                    outVertexBytes, outIndices, out outVertCount);

                if (err != 0)
                {
                    result.error = $"meshoptOptimize error {err} on submesh {s}";
                    return result;
                }

                // Unmarshal output vertices
                var outVerts = BytesToVertices(outVertexBytes, (int)outVertCount);

                // Remap output indices to global space (offset by totalOutVerts)
                int[] finalTris = new int[localIndexCount];
                for (int i = 0; i < (int)localIndexCount; i++)
                    finalTris[i] = (int)outIndices[i] + totalOutVerts;

                submeshTriangles.Add(finalTris);

                // Append vertices to global lists
                for (int i = 0; i < (int)outVertCount; i++)
                {
                    allOutPositions.Add(outVerts[i].position);
                    if (allOutNormals  != null) allOutNormals.Add(outVerts[i].normal);
                    if (allOutTangents != null) allOutTangents.Add(outVerts[i].tangent);
                    if (allOutColors   != null) allOutColors.Add(outVerts[i].color);
                    if (allOutUv0      != null) allOutUv0.Add(outVerts[i].uv0);
                    if (allOutUv1      != null) allOutUv1.Add(outVerts[i].uv1);
                    if (allOutUv2      != null) allOutUv2.Add(outVerts[i].uv2);
                    if (allOutUv3      != null) allOutUv3.Add(outVerts[i].uv3);
                }

                totalOutVerts += (int)outVertCount;

                Debug.Log($"[meshopt] Submesh {s}: {localVertCount} → {outVertCount} verts, " +
                          $"{localIndexCount} indices");
            }

            result.optimizedVertexCount = totalOutVerts;

            // ── Write back to mesh ──
            mesh.Clear();

            mesh.SetVertices(allOutPositions);
            if (allOutNormals  != null) mesh.SetNormals(allOutNormals);
            if (allOutTangents != null) mesh.SetTangents(allOutTangents);
            if (allOutColors   != null) mesh.SetColors(allOutColors);
            if (allOutUv0      != null) mesh.SetUVs(0, allOutUv0);
            if (allOutUv1      != null) mesh.SetUVs(1, allOutUv1);
            if (allOutUv2      != null) mesh.SetUVs(2, allOutUv2);
            if (allOutUv3      != null) mesh.SetUVs(3, allOutUv3);

            mesh.subMeshCount = subCount;
            for (int s = 0; s < subCount; s++)
                mesh.SetTriangles(submeshTriangles[s], s);

            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);

            result.ok = true;

            Debug.Log($"[meshopt] Done: {result.originalVertexCount} → {result.optimizedVertexCount} verts " +
                      $"({subCount} submeshes)");

            return result;
        }

        // ── Marshal helpers ──

        static unsafe byte[] VerticesToBytes(InterleavedVertex[] verts, int count)
        {
            byte[] bytes = new byte[count * VERTEX_STRIDE];
            fixed (InterleavedVertex* src = verts)
            fixed (byte* dst = bytes)
            {
                System.Buffer.MemoryCopy(src, dst, bytes.Length, count * VERTEX_STRIDE);
            }
            return bytes;
        }

        static unsafe InterleavedVertex[] BytesToVertices(byte[] bytes, int count)
        {
            var verts = new InterleavedVertex[count];
            fixed (byte* src = bytes)
            fixed (InterleavedVertex* dst = verts)
            {
                System.Buffer.MemoryCopy(src, dst, count * VERTEX_STRIDE, count * VERTEX_STRIDE);
            }
            return verts;
        }
    }
}
