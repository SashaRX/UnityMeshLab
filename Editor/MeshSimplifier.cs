// MeshSimplifier.cs — Mesh simplification via meshoptimizer with attribute preservation
// Reduces triangle count while preserving vertex attributes (normals, UVs).
// Uses meshopt_simplifyWithAttributes for attribute-aware simplification.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    public static partial class MeshSimplifier
    {
        public struct SimplifySettings
        {
            public float targetRatio;   // 0.5 = keep 50% of triangles
            public float targetError;   // max simplification error (0.01 typical)
            public float uv2Weight;     // importance of UV2 preservation (100 typical)
            public float normalWeight;  // importance of normal preservation (1 typical)
            public float colorWeight;   // importance of float RGBA preservation; 0 disables the cost
            public bool lockBorder;     // lock mesh boundary vertices
            public bool preserveHardEdges; // retain authored normal creases and their incident face belt
            internal bool nativeHardEdgeConstraints; // opt-in replacement of the incident face belt
            internal bool lockNativeCreaseChains; // validated retry when a protected seam collapse loses coverage
            internal bool allowAttributeSeamCollapse; // budget fallback; retains attribute channels
            public int uvChannel;       // which UV channel to protect (default 1 = UV2)

            public static SimplifySettings Default => new SimplifySettings
            {
                targetRatio = 0.5f,
                targetError = 0.01f,
                uv2Weight = 100f,
                normalWeight = 1f,
                colorWeight = 1f,
                lockBorder = true,
                uvChannel = 1
            };
        }

        public struct SimplifyResult
        {
            public bool ok;
            public string error;
            public Mesh simplifiedMesh;
            public bool draftUv;
            public int originalTriCount;
            public int simplifiedTriCount;
            public float resultError;
            internal LodHardEdges.Report hardEdges;
            internal int nativeRetries;
        }

        /// <summary>
        /// Simplify a mesh while preserving vertex attributes.
        /// Creates a new Mesh instance (does not modify the input).
        /// </summary>
        public static SimplifyResult Simplify(Mesh sourceMesh, SimplifySettings settings)
        {
            var result = SimplifyCore(sourceMesh, settings);
            if (settings.nativeHardEdgeConstraints && !settings.lockNativeCreaseChains && result.ok && result.hardEdges?.sourceFallback == true)
            {
                Object.DestroyImmediate(result.simplifiedMesh);
                settings.lockNativeCreaseChains = true;
                result = SimplifyCore(sourceMesh, settings);
                result.nativeRetries = 1;
                if (result.ok && result.hardEdges?.sourceFallback == true)
                {
                    Object.DestroyImmediate(result.simplifiedMesh);
                    settings.nativeHardEdgeConstraints = false;
                    result = SimplifyCore(sourceMesh, settings);
                    result.nativeRetries = 2;
                    if (result.hardEdges != null) result.hardEdges.beltFallback = true;
                }
            }
            return result;
        }

        static SimplifyResult SimplifyCore(Mesh sourceMesh, SimplifySettings settings)
            => new NativeSimplification(sourceMesh, settings).Run();

        // Holds buffers for one synchronous native call sequence; no shared state.
        sealed class NativeSimplification
        {
            const int MAX_UV = 8;
            readonly Mesh sourceMesh;
            readonly SimplifySettings settings;
            SimplifyResult result;
            int vertCount;
            int subCount;
            LodHardEdges hardEdges;
            bool constrained;
            byte[] vertexLocks;
            Vector3[] positions;
            Vector3[] normals;
            Vector4[] tangents;
            Color[] colors;
            bool hasNormal;
            bool hasTangent;
            bool hasColor;
            List<Vector4>[] uvData;
            int[] uvDim;
            int attrFloatCount;
            bool useNormalAttr;
            bool useUvAttr;
            bool useColorAttr;
            float[] weightArray;
            uint attrStride;
            uint options;
            int totalOrigTris;
            int totalSimplTris;
            List<Vector3> allOutPositions;
            List<Vector3> allOutNormals;
            List<Vector4> allOutTangents;
            List<Color> allOutColors;
            List<Vector4>[] allOutUv;
            List<int[]> submeshTriangles;
            int globalVertOffset;
            internal NativeSimplification(Mesh sourceMesh, SimplifySettings settings)
            { this.sourceMesh = sourceMesh; this.settings = settings; }
            internal SimplifyResult Run()
            {
                result = new SimplifyResult();

                if (sourceMesh == null)
                {
                    result.error = "Source mesh is null";
                    return result;
                }

                vertCount = sourceMesh.vertexCount;
                subCount = sourceMesh.subMeshCount;
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

                if (!Initialize()) return result;

                for (int s = 0; s < subCount; s++) if (!ProcessSubmesh(s)) return result;


                var outMesh = BuildMesh();
                result.ok = true;
                result.simplifiedMesh = outMesh;
                result.draftUv = MeshUvState.IsDraft(outMesh);
                result.originalTriCount = totalOrigTris;
                result.simplifiedTriCount = totalSimplTris;
                VerifyProtection(outMesh);
                UvtLog.Info($"[Simplify] Done: {totalOrigTris} → {result.simplifiedTriCount} tris " +
                          $"(ratio={settings.targetRatio:F2}, error={result.resultError:F6})");

                return result;
            }
            bool Initialize()
            {
                // ── Read all vertex data ──
                hardEdges = settings.preserveHardEdges ? new LodHardEdges(sourceMesh) : null;
                constrained = hardEdges != null && settings.nativeHardEdgeConstraints;
                if (!VerifyConstraintAbi()) return false;
                vertexLocks = constrained ? hardEdges.VertexLocks(settings.lockNativeCreaseChains) : null;
                positions = sourceMesh.vertices;
                normals = sourceMesh.normals;
                tangents = sourceMesh.tangents;
                colors = sourceMesh.colors;

                hasNormal = normals != null && normals.Length == vertCount;
                hasTangent = tangents != null && tangents.Length == vertCount;
                hasColor = colors != null && colors.Length == vertCount;

                uvData = new List<Vector4>[MAX_UV];
                uvDim = new int[MAX_UV];
                ReadUvChannels();
                ConfigureAttributes();
                // meshopt options
                options = 0;
                if (settings.lockBorder)
                    options |= MeshoptNative.SimplifyLockBorder;
                if (settings.allowAttributeSeamCollapse)
                    options |= MeshoptNative.SimplifyPermissive;

                // ── Process each submesh independently ──
                totalOrigTris = 0;
                totalSimplTris = 0;

                allOutPositions = new List<Vector3>();
                allOutNormals = hasNormal ? new List<Vector3>() : null;
                allOutTangents = hasTangent ? new List<Vector4>() : null;
                allOutColors = hasColor ? new List<Color>() : null;
                allOutUv = new List<Vector4>[MAX_UV];
                for (int ch = 0; ch < MAX_UV; ch++)
                    if (uvDim[ch] > 0) allOutUv[ch] = new List<Vector4>();

                submeshTriangles = new List<int[]>();
                globalVertOffset = 0;


                return true;
            }

            bool ProcessSubmesh(int s)
            {

                int[] subTris = LodMeshData.Triangles(sourceMesh, s);
                if (subTris.Length == 0)
                {
                    submeshTriangles.Add(new int[0]);
                    return true;
                }

                totalOrigTris += subTris.Length / 3;

                var globalToLocal = MapSubmesh(subTris, s, out var retainedIndices, out var reducibleIndices);
                int localVertCount = globalToLocal.Count;
                uint localIndexCount = (uint)reducibleIndices.Count;
                byte[] posBytes = PackNativePositions(globalToLocal, localVertCount);
                float[] attrArray = PackAttributes(globalToLocal, localVertCount);
                // Local index buffer
                uint[] localIndices = reducibleIndices.ToArray();

                // Output buffer
                uint[] outIndices = new uint[subTris.Length];
                uint outIndexCount = 0;
                float simplifyError = 0;
                uint patchOptions = options;
                float patchRatio = settings.targetRatio;
                if (retainedIndices.Count > 0)
                {
                    patchOptions |= MeshoptNative.SimplifyLockBorder;
                    int remainingTarget = Mathf.Max(1, Mathf.CeilToInt(subTris.Length / 3 * settings.targetRatio) - retainedIndices.Count / 3);
                    patchRatio = localIndexCount > 0 ? Mathf.Min(1, remainingTarget / (localIndexCount / 3f)) : 1;
                }

                int err = SimplifyPatch(globalToLocal, localVertCount, posBytes, localIndices, localIndexCount, attrArray, patchRatio, patchOptions, outIndices, out outIndexCount, out simplifyError);
                if (err != 0)
                {
                    result.error = $"meshoptSimplify error {err} on submesh {s}";
                    return false;
                }
                foreach (uint index in retainedIndices) outIndices[outIndexCount++] = index;

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

                EmitVertices(sortedUsed, localToGlobal);
                // Remap indices to global compact space
                int[] finalTris = new int[outIndexCount];
                for (int i = 0; i < (int)outIndexCount; i++)
                    finalTris[i] = localToCompact[(int)outIndices[i]] + globalVertOffset;

                submeshTriangles.Add(finalTris);
                globalVertOffset += localToCompact.Count;

                UvtLog.Verbose($"[Simplify] Submesh {s}: {localIndexCount / 3} → {outIndexCount / 3} tris, " +
                          $"verts {localVertCount} → {localToCompact.Count}, error={simplifyError:F6}");

                return true;
            }

            Mesh BuildMesh()
            {
                // ── Build output mesh ──
                var outMesh = new Mesh();
                outMesh.indexFormat = allOutPositions.Count > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : sourceMesh.indexFormat;
                outMesh.name = sourceMesh.name;

                outMesh.SetVertices(allOutPositions);
                if (allOutNormals != null) outMesh.SetNormals(allOutNormals);
                if (allOutTangents != null) outMesh.SetTangents(allOutTangents);
                if (allOutColors != null)
                {
                    if (sourceMesh.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8)
                        outMesh.SetColors(allOutColors.ConvertAll(c => (Color32)c));
                    else outMesh.SetColors(allOutColors);
                }

                SetOutputUv(outMesh);
                outMesh.subMeshCount = subCount;
                for (int s = 0; s < subCount; s++)
                    outMesh.SetTriangles(submeshTriangles[s], s);

                MeshGeometry.EnsureMeshChannels(outMesh);
                if (MeshUvState.IsDraft(sourceMesh)) MeshUvState.SetDraft(outMesh, true);
                outMesh.RecalculateBounds();
                outMesh.UploadMeshData(false);


                return outMesh;
            }

            void VerifyProtection(Mesh outMesh)
            {
                if (hardEdges != null)
                {
                    result.hardEdges = constrained ? hardEdges.MeasureNative(outMesh, settings.lockNativeCreaseChains) : hardEdges.Measure(outMesh);
                    if (!result.hardEdges.Valid)
                    {
                        Object.DestroyImmediate(outMesh);
                        result.simplifiedMesh = Object.Instantiate(sourceMesh);
                        MeshUvState.SetDraft(result.simplifiedMesh, result.draftUv);
                        result.simplifiedTriCount = totalOrigTris; result.resultError = 0;
                        result.hardEdges = constrained ? hardEdges.MeasureNative(result.simplifiedMesh, settings.lockNativeCreaseChains) : hardEdges.Measure(result.simplifiedMesh);
                        result.hardEdges.sourceFallback = true;
                        UvtLog.Warn("[Simplify] Protected face / edge / patch-interface check failed; retained source mesh.");
                    }
                }

            }

            void ReadUvChannels()
            {
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

            }

            void ConfigureAttributes()
            {
                // ── Determine attribute layout for simplification ──
                // Attributes passed to meshopt: normal (3), protected UV (2), float RGBA (4).
                attrFloatCount = 0;
                useNormalAttr = hasNormal && settings.normalWeight > 0;
                useUvAttr = uvDim[settings.uvChannel] >= 2 && settings.uv2Weight > 0;
                useColorAttr = hasColor && settings.colorWeight > 0;

                if (useNormalAttr) attrFloatCount += 3;
                if (useUvAttr) attrFloatCount += 2;
                if (useColorAttr) attrFloatCount += 4;

                // Build attribute weights array
                var weights = new List<float>();
                if (useNormalAttr) { weights.Add(settings.normalWeight); weights.Add(settings.normalWeight); weights.Add(settings.normalWeight); }
                if (useUvAttr) { weights.Add(settings.uv2Weight); weights.Add(settings.uv2Weight); }
                if (useColorAttr) for (int i = 0; i < 4; i++) weights.Add(settings.colorWeight);

                weightArray = weights.Count > 0 ? weights.ToArray() : null;
                attrStride = (uint)(attrFloatCount * 4); // bytes

            }

            byte[] PackNativePositions(Dictionary<int, int> globalToLocal, int localVertCount)
            {
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

                return posBytes;
            }

            float[] PackAttributes(Dictionary<int, int> globalToLocal, int localVertCount)
            {
                // Pack attribute buffer (normal + UV + RGBA as flat floats).
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
                        if (useColorAttr)
                        {
                            var color = colors[gi];
                            attrArray[baseIdx + off++] = color.r;
                            attrArray[baseIdx + off++] = color.g;
                            attrArray[baseIdx + off++] = color.b;
                            attrArray[baseIdx + off] = color.a;
                        }
                    }
                }

                return attrArray;
            }

            void EmitVertices(List<int> sortedUsed, int[] localToGlobal)
            {
                // Emit compacted vertices
                foreach (int li in sortedUsed)
                {
                    int gi = localToGlobal[li];
                    allOutPositions.Add(positions[gi]);
                    if (allOutNormals != null && hasNormal) allOutNormals.Add(normals[gi]);
                    if (allOutTangents != null && hasTangent) allOutTangents.Add(tangents[gi]);
                    if (allOutColors != null && hasColor) allOutColors.Add(colors[gi]);
                    EmitUv(gi);
                }

            }

            void SetOutputUv(Mesh outMesh)
            {
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

            }

            bool VerifyConstraintAbi()
            {
                if (constrained)
                {
                    try
                    {
                        if (MeshoptNative.meshoptConstraintVersion() != 1)
                        { result.error = "Unsupported native crease constraint ABI."; return false; }
                    }
                    catch (System.EntryPointNotFoundException)
                    { result.error = "Native crease constraints require the rebuilt xatlas-unity plugin."; return false; }
                }
                return true;
            }

            Dictionary<int, int> MapSubmesh(int[] subTris, int s, out List<uint> retainedIndices, out List<uint> reducibleIndices)
            {
                // Build local vertex mapping for this submesh
                var globalToLocal = new Dictionary<int, int>();
                for (int i = 0; i < subTris.Length; i++)
                {
                    if (!globalToLocal.ContainsKey(subTris[i]))
                        globalToLocal[subTris[i]] = globalToLocal.Count;
                }


                retainedIndices = new List<uint>();
                reducibleIndices = new List<uint>();
                for (int i = 0; i < subTris.Length; i += 3)
                {
                    var retained = constrained ? hardEdges.ambiguousFrozen : hardEdges?.frozen;
                    var destination = retained != null && retained[s][i / 3] ? retainedIndices : reducibleIndices;
                    for (int k = 0; k < 3; k++) destination.Add((uint)globalToLocal[subTris[i + k]]);
                }


                return globalToLocal;
            }

            int SimplifyPatch(Dictionary<int, int> globalToLocal, int localVertCount, byte[] posBytes, uint[] localIndices, uint localIndexCount,
                float[] attrArray, float patchRatio, uint patchOptions, uint[] outIndices, out uint outIndexCount, out float simplifyError)
            {
                const int POS_STRIDE = 12; outIndexCount = 0; simplifyError = 0;
                var localLocks = constrained ? new byte[localVertCount] : null;
                if (constrained) foreach (var pair in globalToLocal) localLocks[pair.Value] = vertexLocks[pair.Key];
                int err;
                if (localIndexCount == 0) err = 0;
                else if (constrained) err = MeshoptNative.meshoptSimplifyConstrained(
                    posBytes, (uint)localVertCount, (uint)POS_STRIDE,
                    localIndices, localIndexCount, attrArray, attrStride,
                    weightArray, (uint)(weightArray != null ? weightArray.Length : 0),
                    localLocks, (uint)localLocks.Length,
                    patchRatio, settings.targetError, patchOptions,
                    outIndices, out outIndexCount, out simplifyError);
                else err = MeshoptNative.meshoptSimplify(
                    posBytes, (uint)localVertCount, (uint)POS_STRIDE,
                    localIndices, localIndexCount,
                    attrArray, attrStride,
                    weightArray, (uint)(weightArray != null ? weightArray.Length : 0),
                    patchRatio, settings.targetError, patchOptions,
                    outIndices, out outIndexCount, out simplifyError);

                return err;
            }

            void EmitUv(int gi)
            {
                for (int ch = 0; ch < MAX_UV; ch++)
                {
                    if (allOutUv[ch] != null && uvData[ch] != null && gi < uvData[ch].Count)
                        allOutUv[ch].Add(uvData[ch][gi]);
                }
            }

        }
    }
}
