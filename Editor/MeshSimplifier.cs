// MeshSimplifier.cs — Mesh simplification via meshoptimizer with attribute preservation
// Reduces triangle count while preserving vertex attributes (normals, UVs).
// Uses meshopt_simplifyWithAttributes (v0.22) for UV-aware simplification.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LightmapUvTool
{
    public static class MeshSimplifier
    {
        public struct SimplifySettings
        {
            public float targetRatio;   // 0.5 = keep 50% of triangles
            public float targetError;   // max simplification error (0.01 typical)
            public float uv2Weight;     // importance of UV2 preservation (100 typical)
            public float normalWeight;  // importance of normal preservation (1 typical)
            public bool lockBorder;     // lock mesh boundary vertices
            public int uvChannel;       // which UV channel to protect (default 1 = UV2)

            public static SimplifySettings Default => new SimplifySettings
            {
                targetRatio  = 0.5f,
                targetError  = 0.01f,
                uv2Weight    = 100f,
                normalWeight = 1f,
                lockBorder   = true,
                uvChannel    = 1
            };
        }

        public struct SimplifyResult
        {
            public bool ok;
            public string error;
            public Mesh simplifiedMesh;
            public int originalTriCount;
            public int simplifiedTriCount;
            public float resultError;
        }

        /// <summary>
        /// Simplify a mesh while preserving vertex attributes.
        /// Creates a new Mesh instance (does not modify the input).
        /// </summary>
        public static SimplifyResult Simplify(Mesh sourceMesh, SimplifySettings settings)
        {
            var result = new SimplifyResult();

            if (sourceMesh == null)
            {
                result.error = "Source mesh is null";
                return result;
            }

            int vertCount = sourceMesh.vertexCount;
            int subCount  = sourceMesh.subMeshCount;
            if (vertCount == 0 || subCount == 0)
            {
                result.error = "Mesh has no vertices or submeshes";
                return result;
            }

            if (!sourceMesh.HasVertexAttribute(VertexAttribute.Position))
            {
                result.error = "Mesh has no position channel";
                return result;
            }

            // ── Read all vertex data ──
            Vector3[] positions = sourceMesh.vertices;
            Vector3[] normals   = sourceMesh.normals;
            Vector4[] tangents  = sourceMesh.tangents;
            Color32[] colors    = sourceMesh.colors32;

            bool hasNormal  = normals  != null && normals.Length  == vertCount;
            bool hasTangent = tangents != null && tangents.Length == vertCount;
            bool hasColor   = colors   != null && colors.Length   == vertCount;

            const int MAX_UV = 8;
            var uvData = new List<Vector4>[MAX_UV];
            var uvDim  = new int[MAX_UV];
            for (int ch = 0; ch < MAX_UV; ch++)
            {
                var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
                if (sourceMesh.HasVertexAttribute(attr))
                {
                    uvDim[ch] = sourceMesh.GetVertexAttributeDimension(attr);
                    uvData[ch] = new List<Vector4>();
                    sourceMesh.GetUVs(ch, uvData[ch]);
                }
            }

            // ── Determine attribute layout for simplification ──
            // Attributes passed to meshopt: normal (3 floats) + protected UV (2 floats)
            int attrFloatCount = 0;
            bool useNormalAttr = hasNormal && settings.normalWeight > 0;
            bool useUvAttr = uvDim[settings.uvChannel] >= 2 && settings.uv2Weight > 0;

            if (useNormalAttr) attrFloatCount += 3;
            if (useUvAttr)     attrFloatCount += 2;

            // Build attribute weights array
            var weights = new List<float>();
            if (useNormalAttr) { weights.Add(settings.normalWeight); weights.Add(settings.normalWeight); weights.Add(settings.normalWeight); }
            if (useUvAttr)     { weights.Add(settings.uv2Weight);    weights.Add(settings.uv2Weight); }

            float[] weightArray = weights.Count > 0 ? weights.ToArray() : null;
            uint attrStride = (uint)(attrFloatCount * 4); // bytes

            // meshopt options
            uint options = 0;
            if (settings.lockBorder)
                options |= MeshoptNative.SimplifyLockBorder;

            // ── Process each submesh independently ──
            int totalOrigTris = 0;
            int totalSimplTris = 0;

            var allOutPositions = new List<Vector3>();
            var allOutNormals   = hasNormal  ? new List<Vector3>() : null;
            var allOutTangents  = hasTangent ? new List<Vector4>() : null;
            var allOutColors    = hasColor   ? new List<Color32>() : null;
            var allOutUv = new List<Vector4>[MAX_UV];
            for (int ch = 0; ch < MAX_UV; ch++)
                if (uvDim[ch] > 0) allOutUv[ch] = new List<Vector4>();

            var submeshTriangles = new List<int[]>();
            int globalVertOffset = 0;

            for (int s = 0; s < subCount; s++)
            {
                int[] subTris = sourceMesh.GetTriangles(s);
                if (subTris.Length == 0)
                {
                    submeshTriangles.Add(new int[0]);
                    continue;
                }

                totalOrigTris += subTris.Length / 3;

                // Build local vertex mapping for this submesh
                var globalToLocal = new Dictionary<int, int>();
                for (int i = 0; i < subTris.Length; i++)
                {
                    if (!globalToLocal.ContainsKey(subTris[i]))
                        globalToLocal[subTris[i]] = globalToLocal.Count;
                }

                int localVertCount = globalToLocal.Count;
                uint localIndexCount = (uint)subTris.Length;

                // Pack position buffer (interleaved, position first — 12 bytes per vertex)
                // meshopt_simplify only reads positions from this buffer
                const int POS_STRIDE = 12;
                byte[] posBytes = new byte[localVertCount * POS_STRIDE];
                unsafe
                {
                    fixed (byte* pBytes = posBytes)
                    {
                        foreach (var kv in globalToLocal)
                        {
                            float* dst = (float*)(pBytes + kv.Value * POS_STRIDE);
                            var p = positions[kv.Key];
                            dst[0] = p.x; dst[1] = p.y; dst[2] = p.z;
                        }
                    }
                }

                // Pack attribute buffer (normal + uv as flat floats)
                float[] attrArray = null;
                if (attrFloatCount > 0)
                {
                    attrArray = new float[localVertCount * attrFloatCount];
                    foreach (var kv in globalToLocal)
                    {
                        int gi = kv.Key;
                        int baseIdx = kv.Value * attrFloatCount;
                        int off = 0;
                        if (useNormalAttr)
                        {
                            var n = normals[gi];
                            attrArray[baseIdx + off++] = n.x;
                            attrArray[baseIdx + off++] = n.y;
                            attrArray[baseIdx + off++] = n.z;
                        }
                        if (useUvAttr)
                        {
                            var uv = uvData[settings.uvChannel][gi];
                            attrArray[baseIdx + off++] = uv.x;
                            attrArray[baseIdx + off++] = uv.y;
                        }
                    }
                }

                // Local index buffer
                uint[] localIndices = new uint[subTris.Length];
                for (int i = 0; i < subTris.Length; i++)
                    localIndices[i] = (uint)globalToLocal[subTris[i]];

                // Output buffer
                uint[] outIndices = new uint[localIndexCount];
                uint outIndexCount;
                float simplifyError;

                int err = MeshoptNative.meshoptSimplify(
                    posBytes, (uint)localVertCount, (uint)POS_STRIDE,
                    localIndices, localIndexCount,
                    attrArray, attrStride,
                    weightArray, (uint)(weightArray != null ? weightArray.Length : 0),
                    settings.targetRatio, settings.targetError, options,
                    outIndices, out outIndexCount, out simplifyError);

                if (err != 0)
                {
                    result.error = $"meshoptSimplify error {err} on submesh {s}";
                    return result;
                }

                result.resultError = Mathf.Max(result.resultError, simplifyError);
                totalSimplTris += (int)outIndexCount / 3;

                // Compact: find which local vertices are still referenced
                var usedLocal = new HashSet<int>();
                for (int i = 0; i < (int)outIndexCount; i++)
                    usedLocal.Add((int)outIndices[i]);

                // Build local→compact mapping
                var localToCompact = new Dictionary<int, int>();
                // Use sorted order for deterministic output
                var sortedUsed = new List<int>(usedLocal);
                sortedUsed.Sort();
                foreach (int li in sortedUsed)
                    localToCompact[li] = localToCompact.Count;

                // Build reverse mapping: local→global
                var localToGlobal = new int[localVertCount];
                foreach (var kv in globalToLocal)
                    localToGlobal[kv.Value] = kv.Key;

                // Emit compacted vertices
                foreach (int li in sortedUsed)
                {
                    int gi = localToGlobal[li];
                    allOutPositions.Add(positions[gi]);
                    if (allOutNormals  != null && hasNormal)  allOutNormals.Add(normals[gi]);
                    if (allOutTangents != null && hasTangent) allOutTangents.Add(tangents[gi]);
                    if (allOutColors   != null && hasColor)   allOutColors.Add(colors[gi]);
                    for (int ch = 0; ch < MAX_UV; ch++)
                    {
                        if (allOutUv[ch] != null && uvData[ch] != null && gi < uvData[ch].Count)
                            allOutUv[ch].Add(uvData[ch][gi]);
                    }
                }

                // Remap indices to global compact space
                int[] finalTris = new int[outIndexCount];
                for (int i = 0; i < (int)outIndexCount; i++)
                    finalTris[i] = localToCompact[(int)outIndices[i]] + globalVertOffset;

                submeshTriangles.Add(finalTris);
                globalVertOffset += localToCompact.Count;

                UvtLog.Verbose($"[Simplify] Submesh {s}: {localIndexCount / 3} → {outIndexCount / 3} tris, " +
                          $"verts {localVertCount} → {localToCompact.Count}, error={simplifyError:F6}");
            }

            // ── Build output mesh ──
            var outMesh = new Mesh();
            outMesh.indexFormat = allOutPositions.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : sourceMesh.indexFormat;
            outMesh.name = sourceMesh.name;

            outMesh.SetVertices(allOutPositions);
            if (allOutNormals  != null) outMesh.SetNormals(allOutNormals);
            if (allOutTangents != null) outMesh.SetTangents(allOutTangents);
            if (allOutColors   != null) outMesh.SetColors(allOutColors);

            for (int ch = 0; ch < MAX_UV; ch++)
            {
                if (allOutUv[ch] == null) continue;
                int dim = uvDim[ch];
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
                        outMesh.SetUVs(ch, list2);
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
                        outMesh.SetUVs(ch, list3);
                        break;
                    }
                    case 4:
                        outMesh.SetUVs(ch, allOutUv[ch]);
                        break;
                }
            }

            outMesh.subMeshCount = subCount;
            for (int s = 0; s < subCount; s++)
                outMesh.SetTriangles(submeshTriangles[s], s);

            outMesh.RecalculateBounds();
            outMesh.UploadMeshData(false);

            result.ok = true;
            result.simplifiedMesh = outMesh;
            result.originalTriCount = totalOrigTris;
            result.simplifiedTriCount = totalSimplTris;

            UvtLog.Info($"[Simplify] Done: {totalOrigTris} → {totalSimplTris} tris " +
                      $"(ratio={settings.targetRatio:F2}, error={result.resultError:F6})");

            return result;
        }
    }
}
