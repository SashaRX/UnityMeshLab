// Uv2AssetPostprocessor.cs — Injects UV2 into imported meshes from sidecar data.
// Triggers on every FBX/model import. If a "_uv2data.asset" sidecar exists next
// to the model file, reads it and applies stored UV2 arrays to matching meshes.
// This mirrors Unity's built-in "Generate Lightmap UVs" approach: the FBX stays
// untouched on disk; UV2 lives only in the imported mesh inside Library/.

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    public class Uv2AssetPostprocessor : AssetPostprocessor
    {
        // Run well after Bakery and other postprocessors (default order = 0).
        public override int GetPostprocessOrder() => 10000;

        struct ApplyStats
        {
            public int fbxVerts;              // vertex count from fresh FBX import
            public int finalVerts;            // vertex count after meshopt + weld
            public bool remapped;             // true if position remap was used
            public bool replayUsed;           // true if deterministic replay was used
            public bool legacyUsed;           // true if legacy path was used
            public int unmatchedVerts;        // vertices with zero UV2 after remap
            public int nearestFallbackCount;  // nearest-unused fallback count
            public int nearestAnyReuseCount;  // nearest-ANY reuse fallback count
            public bool stale;                // fingerprint mismatch detected
        }

        void OnPreprocessModel()
        {
            var modelImporter = assetImporter as ModelImporter;
            if (modelImporter == null) return;

            string sidecarPath = Uv2DataAsset.GetSidecarPath(assetPath);
            if (!System.IO.File.Exists(sidecarPath)) return;

            // Disable Unity's built-in lightmap UV generation — we provide our own UV2.
            // Unity's generator may split vertices along UV seams, changing vertex count
            // and making our stored remap table invalid.
            if (modelImporter.generateSecondaryUV)
            {
                modelImporter.generateSecondaryUV = false;
                UvtLog.Info($"[UV2 Preprocess] Disabled generateSecondaryUV on '{assetPath}' (sidecar provides UV2)");
            }
        }

        void OnPostprocessModel(GameObject root)
        {
            string modelPath = assetPath;
            string sidecarPath = Uv2DataAsset.GetSidecarPath(modelPath);

            // Load sidecar without triggering import loop
            var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
            if (data == null || data.entries.Count == 0) return;

            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int applied = 0;
            int totalFbxVerts = 0, totalFinalVerts = 0;
            int remapCount = 0, replayCount = 0, legacyCount = 0;
            int staleCount = 0, totalFallback = 0;

            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                if (ApplyEntryToMesh(data, mesh, out var s))
                {
                    applied++;
                    totalFbxVerts += s.fbxVerts;
                    totalFinalVerts += s.finalVerts;
                    if (s.remapped) remapCount++;
                    if (s.replayUsed) replayCount++;
                    if (s.legacyUsed) legacyCount++;
                    if (s.stale) staleCount++;
                    totalFallback += s.nearestFallbackCount + s.nearestAnyReuseCount;
                    UvtLog.Verbose($"[UV2 Postprocess] mesh '{mesh.name}': {s.fbxVerts}→{s.finalVerts} verts, " +
                                   $"remapped={s.remapped}, replay={s.replayUsed}, legacy={s.legacyUsed}");
                }
            }

            foreach (var smr in skinned)
            {
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                if (ApplyEntryToMesh(data, mesh, out var s))
                {
                    applied++;
                    totalFbxVerts += s.fbxVerts;
                    totalFinalVerts += s.finalVerts;
                    if (s.remapped) remapCount++;
                    if (s.replayUsed) replayCount++;
                    if (s.legacyUsed) legacyCount++;
                    if (s.stale) staleCount++;
                    totalFallback += s.nearestFallbackCount + s.nearestAnyReuseCount;
                }
            }

            if (applied > 0)
            {
                int saved = totalFbxVerts - totalFinalVerts;
                float pct = totalFbxVerts > 0 ? saved * 100f / totalFbxVerts : 0f;
                UvtLog.Info($"[UV2 Postprocess] '{modelPath}': {applied} mesh(es), " +
                            $"{totalFbxVerts}→{totalFinalVerts} verts (−{saved}, −{pct:F1}%) | " +
                            $"replay={replayCount} legacy={legacyCount} remap={remapCount} " +
                            $"stale={staleCount} fallback={totalFallback}");
            }
        }

        /// <summary>Apply weld (if flagged) + UV2 to a single mesh. Returns true on success.</summary>
        static bool ApplyEntryToMesh(Uv2DataAsset data, Mesh mesh, out ApplyStats stats)
        {
            stats = default;

            // Use robust lookup: try exact name, then fingerprint fallback
            var currentFp = MeshFingerprint.Compute(mesh);
            var entry = data.FindRobust(mesh.name, currentFp);
            if (entry == null || entry.uv2 == null) return false;

            stats.fbxVerts = mesh.vertexCount;

            // ── Schema version check ──
            if (entry.schemaVersion > Uv2DataAsset.CurrentSchemaVersion)
                UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': sidecar schema v{entry.schemaVersion} > " +
                            $"current v{Uv2DataAsset.CurrentSchemaVersion}. Consider updating the tool.");

            // ── Fingerprint validation ──
            if (entry.sourceFingerprint != null)
            {
                if (!entry.sourceFingerprint.Matches(currentFp))
                {
                    UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': FBX geometry changed since sidecar was created " +
                                $"(verts: {entry.sourceFingerprint.vertexCount}→{currentFp.vertexCount}, " +
                                $"tris: {entry.sourceFingerprint.triangleCount}→{currentFp.triangleCount}). " +
                                "Sidecar may be stale.");
                    stats.stale = true;
                }
            }

            bool symmetryStep = ResolveSymmetrySplitStep(entry);

            // ── Deterministic replay path (variant B) ──
            // If sidecar has a stored vertexRemap, replay the optimization
            // by table lookup instead of re-running MeshOptimizer + UvEdgeWeld.
            // This guarantees byte-identical results across Unity's verification passes.
            if (entry.vertexRemap != null && entry.vertexRemap.Length > 0)
            {
                if (entry.vertexRemap.Length != mesh.vertexCount)
                {
                    UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': remap length mismatch " +
                                $"(remap={entry.vertexRemap.Length}, mesh={mesh.vertexCount}) — falling back to legacy path.");
                }
                else
                {
                    bool ok = ReplayOptimization(mesh, entry);
                    if (ok)
                    {
                        stats.finalVerts = mesh.vertexCount;
                        stats.remapped = false;
                        stats.replayUsed = true;
                        // UV2 count now matches — assign directly
                        mesh.SetUVs(1, entry.uv2);
                        UvtLog.Verbose($"[UV2 Postprocess] '{mesh.name}': replay path includes " +
                                       $"meshopt={entry.stepMeshopt}, edgeWeld={entry.stepEdgeWeld}, symmetrySplit={symmetryStep}");
                        return true;
                    }
                    UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': replay failed — falling back to legacy path.");
                }
            }

            // ── Legacy path (no replay data in sidecar) ──
            stats.legacyUsed = true;

            // Warn if mesh was modified but has no replay data
            if (entry.welded || entry.edgeWelded || symmetryStep)
            {
                UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': mesh was modified " +
                            $"(welded={entry.welded}, edgeWelded={entry.edgeWelded}, symmetrySplit={symmetryStep}) but no replay data — " +
                            "using non-deterministic legacy path.");
            }

            if (entry.welded)
            {
                MeshOptimizer.Optimize(mesh);
            }

            if (entry.edgeWelded)
            {
                var welded = Uv0Analyzer.UvEdgeWeld(mesh);
                if (welded != null && welded != mesh)
                {
                    mesh.Clear();
                    mesh.vertices = welded.vertices;
                    mesh.normals = welded.normals;
                    mesh.tangents = welded.tangents;
                    mesh.colors = welded.colors;
                    for (int ch = 0; ch < 8; ch++)
                    {
                        var uvs = new List<Vector2>();
                        welded.GetUVs(ch, uvs);
                        if (uvs.Count > 0) mesh.SetUVs(ch, uvs);
                    }
                    mesh.boneWeights = welded.boneWeights;
                    mesh.bindposes = welded.bindposes;
                    mesh.subMeshCount = welded.subMeshCount;
                    for (int s = 0; s < welded.subMeshCount; s++)
                        mesh.SetTriangles(welded.GetTriangles(s), s);
                    mesh.RecalculateBounds();
                }
            }

            if (entry.uv2.Length != mesh.vertexCount)
            {
                if (entry.vertPositions == null || entry.vertPositions.Length == 0)
                {
                    UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': vertex count mismatch " +
                                    $"(mesh={mesh.vertexCount}, uv2={entry.uv2.Length}), no position data — Skipped.");
                    return false;
                }
                UvtLog.Info($"[UV2 Postprocess] '{mesh.name}': vertex count mismatch " +
                                $"(mesh={mesh.vertexCount}, uv2={entry.uv2.Length}) — will remap by position.");
            }

            stats.finalVerts = mesh.vertexCount;

            bool didRemap;
            int nearestFallback, nearestAnyReuse, unmatched;
            var uv2 = RemapUv2IfNeeded(entry, mesh, out didRemap, out nearestFallback, out nearestAnyReuse, out unmatched);
            stats.remapped = didRemap;
            stats.nearestFallbackCount = nearestFallback;
            stats.nearestAnyReuseCount = nearestAnyReuse;
            stats.unmatchedVerts = unmatched;
            mesh.SetUVs(1, uv2);
            return true;
        }

        static bool ResolveSymmetrySplitStep(MeshUv2Entry entry)
        {
            if (entry == null) return false;
            if (entry.stepSymmetrySplit) return true;

            // Backward-compatible fallback for old sidecars without explicit symmetry flag:
            // schema v0/v1 entries may still have replay data that implies topology changes.
            if (entry.schemaVersion <= 1 &&
                entry.hasReplayData &&
                entry.vertexRemap != null && entry.vertexRemap.Length > 0 &&
                !entry.stepMeshopt && !entry.stepEdgeWeld)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Deterministic mesh rebuild from stored remap table.
        /// Takes raw FBX mesh, applies stored vertex remap + triangle indices
        /// to produce the exact same optimized mesh every time.
        /// No MeshOptimizer or UvEdgeWeld calls — pure table-driven permutation.
        ///
        /// Vertex order correctness: ApplyUv2ToFbx pre-disables generateSecondaryUV
        /// before building the remap, so the reimported mesh seen here has the same
        /// vertex order as e.fbxMesh had when the remap was computed.
        /// </summary>
        static bool ReplayOptimization(Mesh mesh, MeshUv2Entry entry)
        {
            int rawCount = mesh.vertexCount;
            int optCount = entry.optimizedVertexCount;
            int[] remap = entry.vertexRemap;

            if (optCount <= 0 || entry.optimizedTriangles == null || entry.submeshTriangleCounts == null)
                return false;

            // ── Hard validation ──
            // Check UV2 length matches optimized vertex count
            if (entry.uv2.Length != optCount)
            {
                UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': uv2.Length ({entry.uv2.Length}) != " +
                            $"optimizedVertexCount ({optCount}) — replay aborted.");
                return false;
            }

            // Check sum of submesh tri counts matches optimizedTriangles length
            int totalTriIndices = 0;
            foreach (int c in entry.submeshTriangleCounts) totalTriIndices += c;
            if (totalTriIndices != entry.optimizedTriangles.Length)
            {
                UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': sum(submeshTriCounts)={totalTriIndices} != " +
                            $"optimizedTriangles.Length={entry.optimizedTriangles.Length} — replay aborted.");
                return false;
            }

            // Validate remap bounds and check for negative indices in triangles
            for (int i = 0; i < rawCount; i++)
            {
                if (remap[i] < -1) // -1 is valid (removed vertex), anything less is corruption
                {
                    UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': remap[{i}]={remap[i]} is invalid — replay aborted.");
                    return false;
                }
                if (remap[i] >= optCount)
                {
                    UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': remap[{i}]={remap[i]} >= optCount={optCount} — replay aborted.");
                    return false;
                }
            }

            // Validate triangle indices: no negative, no out-of-range
            int maxIdx = -1;
            for (int i = 0; i < entry.optimizedTriangles.Length; i++)
            {
                int idx = entry.optimizedTriangles[i];
                if (idx < 0)
                {
                    UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': negative triangle index at [{i}] — replay aborted.");
                    return false;
                }
                if (idx > maxIdx) maxIdx = idx;
            }
            if (maxIdx >= optCount)
            {
                UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': max triangle index {maxIdx} >= " +
                            $"optimizedVertexCount {optCount} — replay aborted.");
                return false;
            }

            // ── Read all channels from raw FBX mesh ──
            var rawPos     = mesh.vertices;
            var rawNormals = mesh.normals;
            var rawTangents = mesh.tangents;
            var rawColors  = mesh.colors32;
            var rawBoneW   = mesh.boneWeights;
            var rawBindP   = mesh.bindposes;

            // Read UV channels (0–7, skip channel 1 which is UV2 — we'll write that from sidecar)
            var rawUvs = new List<Vector2>[8];
            var rawUvDim = new int[8];
            for (int ch = 0; ch < 8; ch++)
            {
                if (ch == 1) continue; // UV2 comes from sidecar
                var attr = (UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + ch);
                if (!mesh.HasVertexAttribute(attr)) continue;
                rawUvDim[ch] = mesh.GetVertexAttributeDimension(attr);
                rawUvs[ch] = new List<Vector2>();
                mesh.GetUVs(ch, rawUvs[ch]);
            }

            // ── Build optimized arrays via remap ──
            var optPos = new Vector3[optCount];
            var optNormals = rawNormals != null && rawNormals.Length == rawCount ? new Vector3[optCount] : null;
            var optTangents = rawTangents != null && rawTangents.Length == rawCount ? new Vector4[optCount] : null;
            var optColors = rawColors != null && rawColors.Length == rawCount ? new Color32[optCount] : null;

            var optUvs = new List<Vector2>[8];
            for (int ch = 0; ch < 8; ch++)
            {
                if (ch == 1) continue;
                if (rawUvs[ch] != null && rawUvs[ch].Count == rawCount)
                {
                    optUvs[ch] = new List<Vector2>(new Vector2[optCount]);
                }
            }

            for (int i = 0; i < rawCount; i++)
            {
                int dst = remap[i];
                if (dst < 0) continue; // vertex was removed (shouldn't happen with valid remap)

                optPos[dst] = rawPos[i];
                if (optNormals != null) optNormals[dst] = rawNormals[i];
                if (optTangents != null) optTangents[dst] = rawTangents[i];
                if (optColors != null) optColors[dst] = rawColors[i];

                for (int ch = 0; ch < 8; ch++)
                {
                    if (ch == 1 || optUvs[ch] == null) continue;
                    optUvs[ch][dst] = rawUvs[ch][i];
                }
            }

            // ── Fill orphan vertices (SymmetrySplit boundary verts, etc.) ──
            if (entry.orphanIndices != null && entry.orphanIndices.Length > 0)
            {
                for (int k = 0; k < entry.orphanIndices.Length; k++)
                {
                    int dst = entry.orphanIndices[k];
                    if (dst < 0 || dst >= optCount) continue;
                    if (entry.orphanPositions != null && k < entry.orphanPositions.Length)
                        optPos[dst] = entry.orphanPositions[k];
                    if (optNormals != null && entry.orphanNormals != null && k < entry.orphanNormals.Length)
                        optNormals[dst] = entry.orphanNormals[k];
                    if (optTangents != null && entry.orphanTangents != null && k < entry.orphanTangents.Length)
                        optTangents[dst] = entry.orphanTangents[k];
                    if (optUvs[0] != null && entry.orphanUv0 != null && k < entry.orphanUv0.Length)
                        optUvs[0][dst] = entry.orphanUv0[k];
                }
                UvtLog.Verbose($"[UV2 Postprocess] '{mesh.name}': filled {entry.orphanIndices.Length} orphan vertices from sidecar");
            }

            // ── Rebuild mesh ──
            // IMPORTANT: Do NOT use mesh.Clear() here. During OnPostprocessModel, Unity
            // allows mesh modifications regardless of isReadable. However, Clear() resets
            // the mesh's internal import context, causing subsequent Set* calls to check
            // isReadable and fail when it's false. Instead, we strip triangles first
            // (so indices don't reference beyond the new vertex count), then overwrite
            // all vertex data in-place.
            mesh.subMeshCount = 1;
            mesh.triangles = new int[0];

            mesh.SetVertices(optPos);
            if (optNormals != null) mesh.SetNormals(optNormals);
            if (optTangents != null) mesh.SetTangents(optTangents);
            if (optColors != null) mesh.SetColors(optColors);

            for (int ch = 0; ch < 8; ch++)
            {
                if (ch == 1 || optUvs[ch] == null) continue;
                mesh.SetUVs(ch, optUvs[ch]);
            }

            // Restore bone data
            if (rawBoneW != null && rawBoneW.Length > 0)
            {
                var optBoneW = new BoneWeight[optCount];
                for (int i = 0; i < rawCount; i++)
                {
                    int dst = remap[i];
                    if (dst >= 0) optBoneW[dst] = rawBoneW[i];
                }
                mesh.boneWeights = optBoneW;
            }
            if (rawBindP != null && rawBindP.Length > 0)
                mesh.bindposes = rawBindP;

            // ── Set submesh triangles ──
            int subCount = entry.submeshTriangleCounts.Length;
            mesh.subMeshCount = subCount;
            int offset = 0;
            for (int s = 0; s < subCount; s++)
            {
                int len = entry.submeshTriangleCounts[s];
                var subTris = new int[len];
                System.Array.Copy(entry.optimizedTriangles, offset, subTris, 0, len);
                mesh.SetTriangles(subTris, s);
                offset += len;
            }

            mesh.RecalculateBounds();
            // NOTE: Do NOT call mesh.UploadMeshData() here — during OnPostprocessModel,
            // Unity manages GPU upload internally. Calling it here can cause the
            // verification pass to detect inconsistent state ("inconsistent result" error).

            UvtLog.Verbose($"[UV2 Postprocess] '{mesh.name}': replay {rawCount}→{optCount} verts " +
                          $"({entry.optimizedTriangles.Length / 3} tris, {subCount} submeshes)");
            return true;
        }

        /// <summary>
        /// Returns the UV2 array, remapped to match the mesh's vertex order if needed.
        /// MeshOptimizer and UvEdgeWeld may reorder vertices differently between the editor
        /// workflow and the postprocessor (different starting vertex count/order from FBX reimport).
        /// This remaps UV2 by matching vertex positions so the correct UV2 reaches each vertex.
        /// </summary>
        static Vector2[] RemapUv2IfNeeded(MeshUv2Entry entry, Mesh mesh, out bool didRemap,
                                           out int nearestFallbackCount, out int nearestAnyReuseCount,
                                           out int unmatchedCount)
        {
            didRemap = false;
            nearestFallbackCount = 0;
            nearestAnyReuseCount = 0;
            unmatchedCount = 0;

            // No position data stored — backward compat, use UV2 as-is
            if (entry.vertPositions == null || entry.vertPositions.Length != entry.uv2.Length)
                return entry.uv2;

            var meshPos = mesh.vertices;
            int count = meshPos.Length;

            // Always remap: meshopt may reorder vertices differently between
            // editor workflow and postprocessor (e.g. 616→547→425 vs 616→587→425),
            // causing vertex order to diverge even when final counts match.

            // Vertex order differs — remap UV2 by matching positions
            var meshUv0 = new List<Vector2>();
            mesh.GetUVs(0, meshUv0);
            bool hasUv0 = entry.vertUv0 != null &&
                          entry.vertUv0.Length == entry.uv2.Length &&
                          meshUv0.Count == count;

            // Build position → candidate sidecar indices lookup
            var posLookup = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < entry.vertPositions.Length; i++)
            {
                var key = QuantizePos(entry.vertPositions[i]);
                if (!posLookup.TryGetValue(key, out var list))
                {
                    list = new List<int>(2);
                    posLookup[key] = list;
                }
                list.Add(i);
            }

            var result = new Vector2[count];
            var used = new bool[entry.uv2.Length];
            var meshMatched = new bool[count];
            int matched = 0;

            for (int i = 0; i < count; i++)
            {
                var key = QuantizePos(meshPos[i]);
                if (!posLookup.TryGetValue(key, out var candidates))
                {
                    // No match at this position — leave UV2 as zero
                    continue;
                }

                if (candidates.Count == 1)
                {
                    result[i] = entry.uv2[candidates[0]];
                    used[candidates[0]] = true;
                    meshMatched[i] = true;
                    matched++;
                }
                else if (hasUv0)
                {
                    // Multiple vertices at same position — disambiguate by closest UV0
                    float bestDist = float.MaxValue;
                    int bestIdx = -1;
                    foreach (int ci in candidates)
                    {
                        if (used[ci]) continue;
                        float d = Vector2.SqrMagnitude(meshUv0[i] - entry.vertUv0[ci]);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestIdx = ci;
                        }
                    }
                    if (bestIdx >= 0)
                    {
                        result[i] = entry.uv2[bestIdx];
                        used[bestIdx] = true;
                        meshMatched[i] = true;
                        matched++;
                    }
                }
                else
                {
                    // No UV0 data — pick first unused candidate
                    foreach (int ci in candidates)
                    {
                        if (!used[ci])
                        {
                            result[i] = entry.uv2[ci];
                            used[ci] = true;
                            meshMatched[i] = true;
                            matched++;
                            break;
                        }
                    }
                }
            }

            // Fallback pass: for vertices unmatched by quantized hash (bucket boundary
            // float rounding), find nearest unused sidecar vertex by 3D distance.
            if (matched < count)
            {
                // Collect unmatched mesh vertex indices
                var unmatchedMesh = new List<int>(count - matched);
                for (int i = 0; i < count; i++)
                    if (!meshMatched[i])
                        unmatchedMesh.Add(i);

                // Collect unused sidecar indices
                var unusedSidecar = new List<int>(entry.uv2.Length - matched);
                for (int i = 0; i < used.Length; i++)
                    if (!used[i]) unusedSidecar.Add(i);

                int fallbackMatched = 0;
                foreach (int mi in unmatchedMesh)
                {
                    float bestDist = float.MaxValue;
                    int bestIdx = -1;
                    int bestListIdx = -1;

                    // First try: nearest UNUSED sidecar vertex within tight tolerance (1mm)
                    for (int j = 0; j < unusedSidecar.Count; j++)
                    {
                        int si = unusedSidecar[j];
                        float d = Vector3.SqrMagnitude(meshPos[mi] - entry.vertPositions[si]);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestIdx = si;
                            bestListIdx = j;
                        }
                    }

                    if (bestIdx >= 0 && bestDist < 1e-4f)
                    {
                        result[mi] = entry.uv2[bestIdx];
                        used[bestIdx] = true;
                        unusedSidecar.RemoveAt(bestListIdx);
                        matched++;
                        fallbackMatched++;
                        nearestFallbackCount++;
                        continue;
                    }

                    // Second try: nearest ANY sidecar vertex (allow reuse).
                    // This handles the case where mesh has more vertices than sidecar
                    // (e.g. meshopt produces slightly different count on reimport):
                    // duplicate/nearby positions inherit UV2 from the closest existing point.
                    bestDist = float.MaxValue;
                    bestIdx = -1;
                    for (int si = 0; si < entry.vertPositions.Length; si++)
                    {
                        float d = Vector3.SqrMagnitude(meshPos[mi] - entry.vertPositions[si]);
                        if (d < bestDist) { bestDist = d; bestIdx = si; }
                    }
                    if (bestIdx >= 0)
                    {
                        result[mi] = entry.uv2[bestIdx];
                        matched++;
                        fallbackMatched++;
                        nearestAnyReuseCount++;
                    }
                }

                if (nearestFallbackCount > 0)
                    UvtLog.Info($"[UV2 Postprocess] '{mesh.name}': {nearestFallbackCount} vertices matched by nearest-unused fallback");

                if (nearestAnyReuseCount > 0)
                {
                    string severity = nearestAnyReuseCount > 5 ? "Sidecar may be invalid." : "Acceptable.";
                    UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': {nearestAnyReuseCount} vertices used nearest-ANY fallback (reuse). {severity}");
                }
            }

            didRemap = true;
            unmatchedCount = count - matched;

            if (matched < count)
                UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': position remap {matched}/{count} " +
                                 $"(unmatched vertices will have zero UV2)");

            return result;
        }

        static (int, int, int) QuantizePos(Vector3 pos)
        {
            return (
                Mathf.RoundToInt(pos.x * 10000f),
                Mathf.RoundToInt(pos.y * 10000f),
                Mathf.RoundToInt(pos.z * 10000f)
            );
        }
    }
}
