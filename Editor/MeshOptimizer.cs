// MeshOptimizer.cs — C# wrapper for meshoptimizer native bridge
// Optimizes Unity Mesh: vertex dedup + cache + overdraw + fetch
// Supports all vertex channels: position, normal, tangent, color, uv0–uv7 (Vector2/3/4)
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

        // Unity supports TexCoord0–TexCoord7, each can be 2/3/4 floats.
        // Some channels may store color data, bone weights, etc.
        const int MAX_UV_CHANNELS = 8;

        struct ChannelLayout
        {
            public bool hasNormal;
            public bool hasTangent;
            public bool hasColor;
            public int[] uvDim;    // per-channel dimension (0 = absent, 2/3/4 = present)
            public int[] uvOffset; // byte offset for each UV channel

            // Byte offsets (computed from present channels)
            public int normalOffset;
            public int tangentOffset;
            public int colorOffset;
            public int totalStride;
        }

        /// <summary>
        /// Run meshoptimizer full pipeline on a Unity Mesh (in-place).
        /// Each submesh is optimized independently. All vertex channels are preserved.
        /// Supports up to 8 UV channels with dynamic dimensions (Vector2/3/4).
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

            // ── Detect channel layout ──
            if (!mesh.HasVertexAttribute(VertexAttribute.Position))
            {
                result.error = "Mesh has no position channel";
                return result;
            }

            var layout = BuildChannelLayout(mesh);

            // Build UV dim summary for log
            var uvDimStr = new System.Text.StringBuilder();
            for (int ch = 0; ch < MAX_UV_CHANNELS; ch++)
            {
                if (ch > 0) uvDimStr.Append(',');
                uvDimStr.Append(layout.uvDim[ch]);
            }

            Debug.Log($"[meshopt] Channel layout: stride={layout.totalStride}, " +
                      $"normal={layout.hasNormal}, tangent={layout.hasTangent}, color={layout.hasColor}, " +
                      $"uv dims=[{uvDimStr}]");

            // ── Read all channel data ──
            Vector3[] positions = mesh.vertices;
            Vector3[] normals   = layout.hasNormal  ? mesh.normals   : null;
            Vector4[] tangents  = layout.hasTangent ? mesh.tangents  : null;
            Color32[] colors    = layout.hasColor   ? mesh.colors32  : null;

            // Read UV channels as Vector4 (Unity fills extra components with 0)
            var uvData = new List<Vector4>[MAX_UV_CHANNELS];
            for (int ch = 0; ch < MAX_UV_CHANNELS; ch++)
            {
                if (layout.uvDim[ch] > 0)
                {
                    uvData[ch] = new List<Vector4>();
                    mesh.GetUVs(ch, uvData[ch]);
                }
            }

            // ── Process each submesh independently ──
            var allOutPositions = new List<Vector3>();
            var allOutNormals   = layout.hasNormal  ? new List<Vector3>() : null;
            var allOutTangents  = layout.hasTangent ? new List<Vector4>() : null;
            var allOutColors    = layout.hasColor   ? new List<Color32>() : null;
            var allOutUv        = new List<Vector4>[MAX_UV_CHANNELS];
            for (int ch = 0; ch < MAX_UV_CHANNELS; ch++)
            {
                if (layout.uvDim[ch] > 0)
                    allOutUv[ch] = new List<Vector4>();
            }

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

                // Pack vertices into interleaved byte buffer
                byte[] vertexBytes = PackVertices(
                    globalToLocal, layout,
                    positions, normals, tangents, colors, uvData);

                // Build local index buffer
                uint[] localIndices = new uint[subTris.Length];
                for (int i = 0; i < subTris.Length; i++)
                    localIndices[i] = (uint)globalToLocal[subTris[i]];

                // Allocate output buffers
                byte[] outVertexBytes = new byte[localVertCount * layout.totalStride];
                uint[] outIndices = new uint[localIndexCount];
                uint   outVertCount;

                // Call native meshoptimizer pipeline
                int err = MeshoptNative.meshoptOptimize(
                    vertexBytes, (uint)localVertCount, (uint)layout.totalStride,
                    localIndices, localIndexCount,
                    overdrawThreshold,
                    outVertexBytes, outIndices, out outVertCount);

                if (err != 0)
                {
                    result.error = $"meshoptOptimize error {err} on submesh {s}";
                    return result;
                }

                // Unpack output vertices and append to global lists
                UnpackVertices(
                    outVertexBytes, (int)outVertCount, layout,
                    allOutPositions, allOutNormals, allOutTangents, allOutColors, allOutUv);

                // Remap output indices to global space (offset by totalOutVerts)
                int[] finalTris = new int[localIndexCount];
                for (int i = 0; i < (int)localIndexCount; i++)
                    finalTris[i] = (int)outIndices[i] + totalOutVerts;

                submeshTriangles.Add(finalTris);
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

            // Write UV channels back with original dimensions
            for (int ch = 0; ch < MAX_UV_CHANNELS; ch++)
            {
                if (allOutUv[ch] == null) continue;

                int dim = layout.uvDim[ch];
                switch (dim)
                {
                    case 2:
                    {
                        var list2 = new List<Vector2>(allOutUv[ch].Count);
                        for (int i = 0; i < allOutUv[ch].Count; i++)
                        {
                            var v = allOutUv[ch][i];
                            list2.Add(new Vector2(v.x, v.y));
                        }
                        mesh.SetUVs(ch, list2);
                        break;
                    }
                    case 3:
                    {
                        var list3 = new List<Vector3>(allOutUv[ch].Count);
                        for (int i = 0; i < allOutUv[ch].Count; i++)
                        {
                            var v = allOutUv[ch][i];
                            list3.Add(new Vector3(v.x, v.y, v.z));
                        }
                        mesh.SetUVs(ch, list3);
                        break;
                    }
                    case 4:
                        mesh.SetUVs(ch, allOutUv[ch]);
                        break;
                }
            }

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

        // ── Layout computation ──

        static ChannelLayout BuildChannelLayout(Mesh mesh)
        {
            var layout = new ChannelLayout();
            layout.uvDim = new int[MAX_UV_CHANNELS];
            layout.uvOffset = new int[MAX_UV_CHANNELS];

            layout.hasNormal  = mesh.HasVertexAttribute(VertexAttribute.Normal);
            layout.hasTangent = mesh.HasVertexAttribute(VertexAttribute.Tangent);
            layout.hasColor   = mesh.HasVertexAttribute(VertexAttribute.Color);

            // Detect UV channel dimensions (2, 3, or 4 floats)
            for (int ch = 0; ch < MAX_UV_CHANNELS; ch++)
            {
                var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
                if (mesh.HasVertexAttribute(attr))
                    layout.uvDim[ch] = mesh.GetVertexAttributeDimension(attr);
            }

            // Position is always first: 12 bytes (float3)
            int offset = 12;

            // Normal: 12 bytes (float3)
            layout.normalOffset = offset;
            if (layout.hasNormal) offset += 12;

            // Tangent: 16 bytes (float4)
            layout.tangentOffset = offset;
            if (layout.hasTangent) offset += 16;

            // Color32: 4 bytes (RGBA)
            layout.colorOffset = offset;
            if (layout.hasColor) offset += 4;

            // UV channels: dim * 4 bytes each
            for (int ch = 0; ch < MAX_UV_CHANNELS; ch++)
            {
                layout.uvOffset[ch] = offset;
                if (layout.uvDim[ch] > 0)
                    offset += layout.uvDim[ch] * 4;  // sizeof(float) * dimension
            }

            layout.totalStride = offset;
            return layout;
        }

        // ── Packing ──

        static unsafe byte[] PackVertices(
            Dictionary<int, int> globalToLocal,
            in ChannelLayout layout,
            Vector3[] positions, Vector3[] normals, Vector4[] tangents,
            Color32[] colors, List<Vector4>[] uvData)
        {
            int localVertCount = globalToLocal.Count;
            byte[] bytes = new byte[localVertCount * layout.totalStride];

            fixed (byte* pBytes = bytes)
            {
                foreach (var kv in globalToLocal)
                {
                    int gi = kv.Key;
                    int li = kv.Value;
                    byte* dst = pBytes + li * layout.totalStride;

                    // Position (always at offset 0, 12 bytes)
                    WriteFloat3(dst, 0, positions[gi]);

                    // Normal
                    if (normals != null)
                        WriteFloat3(dst, layout.normalOffset, normals[gi]);

                    // Tangent
                    if (tangents != null)
                        WriteFloat4(dst, layout.tangentOffset, tangents[gi]);

                    // Color32
                    if (colors != null)
                    {
                        var c = colors[gi];
                        dst[layout.colorOffset + 0] = c.r;
                        dst[layout.colorOffset + 1] = c.g;
                        dst[layout.colorOffset + 2] = c.b;
                        dst[layout.colorOffset + 3] = c.a;
                    }

                    // UV channels (0–7, dynamic dimension)
                    for (int ch = 0; ch < MAX_UV_CHANNELS; ch++)
                    {
                        int dim = layout.uvDim[ch];
                        if (dim <= 0 || uvData[ch] == null || gi >= uvData[ch].Count)
                            continue;

                        Vector4 uv = uvData[ch][gi];
                        float* fp = (float*)(dst + layout.uvOffset[ch]);
                        fp[0] = uv.x;
                        if (dim >= 2) fp[1] = uv.y;
                        if (dim >= 3) fp[2] = uv.z;
                        if (dim >= 4) fp[3] = uv.w;
                    }
                }
            }

            return bytes;
        }

        // ── Unpacking ──

        static unsafe void UnpackVertices(
            byte[] bytes, int vertCount,
            in ChannelLayout layout,
            List<Vector3> outPositions,
            List<Vector3> outNormals,
            List<Vector4> outTangents,
            List<Color32> outColors,
            List<Vector4>[] outUv)
        {
            fixed (byte* pBytes = bytes)
            {
                for (int i = 0; i < vertCount; i++)
                {
                    byte* src = pBytes + i * layout.totalStride;

                    // Position
                    outPositions.Add(ReadFloat3(src, 0));

                    // Normal
                    if (outNormals != null)
                        outNormals.Add(ReadFloat3(src, layout.normalOffset));

                    // Tangent
                    if (outTangents != null)
                        outTangents.Add(ReadFloat4(src, layout.tangentOffset));

                    // Color32
                    if (outColors != null)
                    {
                        outColors.Add(new Color32(
                            src[layout.colorOffset + 0],
                            src[layout.colorOffset + 1],
                            src[layout.colorOffset + 2],
                            src[layout.colorOffset + 3]));
                    }

                    // UV channels
                    for (int ch = 0; ch < MAX_UV_CHANNELS; ch++)
                    {
                        int dim = layout.uvDim[ch];
                        if (dim <= 0 || outUv[ch] == null) continue;

                        float* fp = (float*)(src + layout.uvOffset[ch]);
                        var uv = new Vector4(
                            fp[0],
                            dim >= 2 ? fp[1] : 0f,
                            dim >= 3 ? fp[2] : 0f,
                            dim >= 4 ? fp[3] : 0f);
                        outUv[ch].Add(uv);
                    }
                }
            }
        }

        // ── Unsafe helpers ──

        static unsafe void WriteFloat3(byte* dst, int offset, Vector3 v)
        {
            float* p = (float*)(dst + offset);
            p[0] = v.x; p[1] = v.y; p[2] = v.z;
        }

        static unsafe void WriteFloat4(byte* dst, int offset, Vector4 v)
        {
            float* p = (float*)(dst + offset);
            p[0] = v.x; p[1] = v.y; p[2] = v.z; p[3] = v.w;
        }

        static unsafe Vector3 ReadFloat3(byte* src, int offset)
        {
            float* p = (float*)(src + offset);
            return new Vector3(p[0], p[1], p[2]);
        }

        static unsafe Vector4 ReadFloat4(byte* src, int offset)
        {
            float* p = (float*)(src + offset);
            return new Vector4(p[0], p[1], p[2], p[3]);
        }
    }
}
