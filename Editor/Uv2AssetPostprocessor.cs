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

        /// <summary>
        /// Paths to bypass during the next reimport. When ApplyUv2ToFbx needs the
        /// raw FBX mesh (before any postprocessor modifications), it adds the FBX
        /// path here and reimports. Both OnPreprocessModel and OnPostprocessModel
        /// skip processing for bypassed paths, yielding the untouched FBX mesh.
        /// </summary>
        internal static readonly HashSet<string> bypassPaths = new HashSet<string>();

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

            // Bypass: ApplyUv2ToFbx needs the raw FBX mesh without any modifications.
            if (bypassPaths.Contains(assetPath))
                return;

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

            // Disable Unity's post-import mesh processing that can modify our reconstructed mesh:
            // - weldVertices: merges vertices at same position, destroying UV seams we need
            // - meshCompression: quantizes positions, causing vertex shifts
            // - optimizeMeshPolygons/Vertices: reorders data (usually harmless but disable to be safe)
            if (modelImporter.weldVertices)
            {
                modelImporter.weldVertices = false;
                UvtLog.Info($"[UV2 Preprocess] Disabled weldVertices on '{assetPath}' (postprocessor manages mesh topology)");
            }
            if (modelImporter.meshCompression != ModelImporterMeshCompression.Off)
            {
                UvtLog.Info($"[UV2 Preprocess] Disabled meshCompression ({modelImporter.meshCompression}) on '{assetPath}'");
                modelImporter.meshCompression = ModelImporterMeshCompression.Off;
            }
            if (modelImporter.optimizeMeshPolygons)
            {
                modelImporter.optimizeMeshPolygons = false;
                UvtLog.Info($"[UV2 Preprocess] Disabled optimizeMeshPolygons on '{assetPath}'");
            }
            if (modelImporter.optimizeMeshVertices)
            {
                modelImporter.optimizeMeshVertices = false;
                UvtLog.Info($"[UV2 Preprocess] Disabled optimizeMeshVertices on '{assetPath}'");
            }
        }

        void OnPostprocessModel(GameObject root)
        {
            string modelPath = assetPath;

            // Bypass: ApplyUv2ToFbx needs the raw FBX mesh without any modifications.
            if (bypassPaths.Remove(modelPath))
            {
                UvtLog.Verbose($"[UV2 Postprocess] Bypassed '{modelPath}' (raw FBX mesh requested)");
                return;
            }

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
                    // Only mark stale if vertex/triangle counts actually differ.
                    // Hash-only mismatches (same counts) are typically caused by
                    // FBX importer floating-point non-determinism between reimports,
                    // not real geometry changes. The original remap is still valid
                    // in this case; marking stale would trigger RebuildRemapFromPositions
                    // which can't cover all opt vertices (orphans + dedup collisions),
                    // causing hundreds of unfilled vertices → mesh stretching to origin.
                    bool countsChanged = entry.sourceFingerprint.vertexCount != currentFp.vertexCount
                                      || entry.sourceFingerprint.triangleCount != currentFp.triangleCount;
                    if (countsChanged)
                    {
                        UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': FBX geometry changed since sidecar was created " +
                                    $"(verts: {entry.sourceFingerprint.vertexCount}→{currentFp.vertexCount}, " +
                                    $"tris: {entry.sourceFingerprint.triangleCount}→{currentFp.triangleCount}). " +
                                    "Sidecar may be stale.");
                        stats.stale = true;
                    }
                    else
                    {
                        UvtLog.Verbose($"[UV2 Postprocess] '{mesh.name}': fingerprint hash mismatch but " +
                                       $"vertex/triangle counts match ({currentFp.vertexCount} verts, " +
                                       $"{currentFp.triangleCount} tris) — trusting original remap " +
                                       "(likely FBX importer FP non-determinism).");
                    }
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
                    bool ok = ReplayOptimization(mesh, entry, stats.stale);
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
        static bool ReplayOptimization(Mesh mesh, MeshUv2Entry entry, bool stale = false)
        {
            int rawCount = mesh.vertexCount;
            int optCount = entry.optimizedVertexCount;
            int[] remap = entry.vertexRemap;

            if (optCount <= 0 || entry.optimizedTriangles == null || entry.submeshTriangleCounts == null)
                return false;

            // ── Stale fingerprint: rebuild remap from stored raw positions ──
            if (stale && entry.vertPositions != null && entry.vertPositions.Length > 0)
            {
                remap = RebuildRemapFromPositions(mesh, entry);
                if (remap == null)
                {
                    UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': remap rebuild failed — replay aborted.");
                    return false;
                }
            }

            // ── Hard validation ──
            // Remap length must match reimported vertex count
            if (remap.Length != rawCount)
            {
                UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': remap.Length ({remap.Length}) != " +
                            $"mesh.vertexCount ({rawCount}) — replay aborted.");
                return false;
            }

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
            // If ground-truth vertex data is available, use it DIRECTLY for positions/normals/tangents.
            // This completely bypasses the remap for geometry, eliminating all remap-related vertex errors.
            // The remap is only used for UV channels and colors (which aren't stored in ground truth).
            bool hasGroundTruth = entry.optimizedPositions != null && entry.optimizedPositions.Length == optCount;

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

            if (hasGroundTruth)
            {
                // Positions/normals/tangents from ground truth — these don't change during
                // weld (meshopt dedup is byte-exact, EdgeWeld only removes vertices at same pos).
                System.Array.Copy(entry.optimizedPositions, optPos, optCount);
                if (optNormals != null && entry.optimizedNormals != null && entry.optimizedNormals.Length == optCount)
                    System.Array.Copy(entry.optimizedNormals, optNormals, optCount);
                if (optTangents != null && entry.optimizedTangents != null && entry.optimizedTangents.Length == optCount)
                    System.Array.Copy(entry.optimizedTangents, optTangents, optCount);

                // UV channels, colors — from remap (UV0 is modified by weld, must come from raw FBX)
                for (int i = 0; i < rawCount; i++)
                {
                    int dst = remap[i];
                    if (dst < 0) continue;
                    if (optColors != null) optColors[dst] = rawColors[i];
                    for (int ch = 0; ch < 8; ch++)
                    {
                        if (ch == 1 || optUvs[ch] == null) continue;
                        optUvs[ch][dst] = rawUvs[ch][i];
                    }
                }

                // Orphan UV0 fill (orphan positions/normals/tangents already in ground truth)
                if (entry.orphanIndices != null && entry.orphanIndices.Length > 0)
                {
                    for (int k = 0; k < entry.orphanIndices.Length; k++)
                    {
                        int dst = entry.orphanIndices[k];
                        if (dst < 0 || dst >= optCount) continue;
                        if (optUvs[0] != null && entry.orphanUv0 != null && k < entry.orphanUv0.Length)
                            optUvs[0][dst] = entry.orphanUv0[k];
                    }
                }

                UvtLog.Info($"[UV2 Postprocess] '{mesh.name}': ground-truth geometry + remap UV ({optCount} verts)");
            }
            else
            {
                // Legacy path: all data from remap
                for (int i = 0; i < rawCount; i++)
                {
                    int dst = remap[i];
                    if (dst < 0) continue;

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

                // ── Detect and fix unfilled vertices (would cause 3D stretching to origin) ──
                // Unfilled vertices happen when BuildVertexRemap couldn't match some raw FBX
                // vertices to optimized vertices (e.g. UvEdgeWeld averaged positions).
                // Safety net: use stored vertPositions/vertUv0 from sidecar to fill them.
                {
                    // Build set of indices actually referenced by triangles
                    var referenced = new HashSet<int>();
                    for (int i = 0; i < entry.optimizedTriangles.Length; i++)
                        referenced.Add(entry.optimizedTriangles[i]);

                    // Find unfilled referenced vertices
                    var unfilledIndices = new List<int>();
                    foreach (int idx in referenced)
                    {
                        if (idx < optCount && optPos[idx] == Vector3.zero)
                        {
                            bool allZero = true;
                            if (optNormals != null && optNormals[idx].sqrMagnitude > 0) allZero = false;
                            if (allZero && optUvs[0] != null && optUvs[0][idx].sqrMagnitude > 0) allZero = false;
                            if (allZero) unfilledIndices.Add(idx);
                        }
                    }

                    if (unfilledIndices.Count > 0 &&
                        entry.vertPositions != null && entry.vertPositions.Length > 0)
                    {
                        int fixed_ = 0;
                        int[] origRemap = entry.vertexRemap;

                        var optToRaw = new Dictionary<int, int>();
                        if (origRemap != null)
                        {
                            for (int i = 0; i < origRemap.Length; i++)
                            {
                                int dst = origRemap[i];
                                if (dst >= 0 && !optToRaw.ContainsKey(dst))
                                    optToRaw[dst] = i;
                            }
                        }

                        foreach (int idx in unfilledIndices)
                        {
                            if (optToRaw.TryGetValue(idx, out int rawIdx) && rawIdx < rawCount)
                            {
                                optPos[idx] = rawPos[rawIdx];
                                if (optNormals != null && rawNormals != null && rawIdx < rawNormals.Length)
                                    optNormals[idx] = rawNormals[rawIdx];
                                if (optTangents != null && rawTangents != null && rawIdx < rawTangents.Length)
                                    optTangents[idx] = rawTangents[rawIdx];
                                if (optColors != null && rawColors != null && rawIdx < rawColors.Length)
                                    optColors[idx] = rawColors[rawIdx];
                                for (int ch = 0; ch < 8; ch++)
                                {
                                    if (ch == 1 || optUvs[ch] == null || rawUvs[ch] == null) continue;
                                    if (rawIdx < rawUvs[ch].Count)
                                        optUvs[ch][idx] = rawUvs[ch][rawIdx];
                                }
                                fixed_++;
                                continue;
                            }

                            if (idx < entry.vertPositions.Length)
                            {
                                Vector3 targetPos = entry.vertPositions[idx];
                                float bestDist = float.MaxValue;
                                int bestRaw = -1;
                                for (int r = 0; r < rawCount; r++)
                                {
                                    float d = Vector3.SqrMagnitude(rawPos[r] - targetPos);
                                    if (d < bestDist) { bestDist = d; bestRaw = r; }
                                }
                                if (bestRaw >= 0 && bestDist < 1e-1f)
                                {
                                    optPos[idx] = rawPos[bestRaw];
                                    if (optNormals != null && rawNormals != null && bestRaw < rawNormals.Length)
                                        optNormals[idx] = rawNormals[bestRaw];
                                    if (optTangents != null && rawTangents != null && bestRaw < rawTangents.Length)
                                        optTangents[idx] = rawTangents[bestRaw];
                                    if (optColors != null && rawColors != null && bestRaw < rawColors.Length)
                                        optColors[idx] = rawColors[bestRaw];
                                    for (int ch = 0; ch < 8; ch++)
                                    {
                                        if (ch == 1 || optUvs[ch] == null || rawUvs[ch] == null) continue;
                                        if (bestRaw < rawUvs[ch].Count)
                                            optUvs[ch][idx] = rawUvs[ch][bestRaw];
                                    }
                                    fixed_++;
                                }
                            }
                        }

                        if (fixed_ > 0)
                            UvtLog.Info($"[UV2 Postprocess] '{mesh.name}': fixed {fixed_}/{unfilledIndices.Count} " +
                                        "unfilled vertices from raw FBX data (remap gap safety net)");
                        int remaining = unfilledIndices.Count - fixed_;
                        if (remaining > 0)
                            UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': {remaining} referenced vertices still unfilled " +
                                        "(may cause 3D stretching)");
                    }
                    else if (unfilledIndices.Count > 0)
                    {
                        UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': {unfilledIndices.Count} referenced vertices have zero " +
                                    "position+normal+UV0 (likely unfilled — may cause 3D stretching)");
                    }
                }
            } // end legacy path

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

            // ── DIAGNOSTIC: verify reconstruction quality ──
            {
                var finalPos = mesh.vertices;
                int zeroCount = 0;
                float maxPosDev = 0f;
                int maxPosDevIdx = -1;
                for (int i = 0; i < finalPos.Length && i < optCount; i++)
                {
                    if (finalPos[i] == Vector3.zero) zeroCount++;
                    if (hasGroundTruth)
                    {
                        float dev = Vector3.Distance(finalPos[i], entry.optimizedPositions[i]);
                        if (dev > maxPosDev) { maxPosDev = dev; maxPosDevIdx = i; }
                    }
                }

                // Check UV0 — how many vertices have zero UV0?
                var finalUv0 = new List<Vector2>();
                mesh.GetUVs(0, finalUv0);
                int zeroUv0 = 0;
                for (int i = 0; i < finalUv0.Count; i++)
                    if (finalUv0[i] == Vector2.zero) zeroUv0++;

                // Check triangles — verify they match stored data
                var finalTris = mesh.triangles;
                int triMismatch = 0;
                int triOutOfRange = 0;
                for (int i = 0; i < finalTris.Length; i++)
                {
                    if (finalTris[i] < 0 || finalTris[i] >= mesh.vertexCount) triOutOfRange++;
                    if (i < entry.optimizedTriangles.Length && finalTris[i] != entry.optimizedTriangles[i]) triMismatch++;
                }
                int triCountDiff = finalTris.Length - entry.optimizedTriangles.Length;

                var sb = new System.Text.StringBuilder();
                sb.Append($"[UV2 Postprocess] DIAG '{mesh.name}': {rawCount}→{optCount} verts (mesh.vertexCount={mesh.vertexCount}), ");
                sb.Append($"groundTruth={hasGroundTruth}, ");
                sb.Append($"zeroPos={zeroCount}, zeroUv0={zeroUv0}/{finalUv0.Count}");
                if (hasGroundTruth)
                    sb.Append($", maxPosDev={maxPosDev:E3} @idx{maxPosDevIdx}");
                sb.Append($", tris={finalTris.Length}(expected={entry.optimizedTriangles.Length}, diff={triCountDiff}, mismatch={triMismatch}, OOB={triOutOfRange})");

                // Sample a few positions for inspection
                if (optCount > 0)
                {
                    int mid = optCount / 2;
                    int last = optCount - 1;
                    sb.Append($"\n  pos[0]={optPos[0]:F4}");
                    if (hasGroundTruth) sb.Append($" gt={entry.optimizedPositions[0]:F4}");
                    sb.Append($" raw[remap→0]?");
                    sb.Append($"\n  pos[{mid}]={optPos[mid]:F4}");
                    if (hasGroundTruth) sb.Append($" gt={entry.optimizedPositions[mid]:F4}");
                    sb.Append($"\n  pos[{last}]={optPos[last]:F4}");
                    if (hasGroundTruth) sb.Append($" gt={entry.optimizedPositions[last]:F4}");
                }
                UvtLog.Info(sb.ToString());
            }

            UvtLog.Verbose($"[UV2 Postprocess] '{mesh.name}': replay {rawCount}→{optCount} verts " +
                          $"({entry.optimizedTriangles.Length / 3} tris, {subCount} submeshes)");
            return true;
        }

        /// <summary>
        /// Rebuild the vertex remap table when the fingerprint is stale (vertex order changed).
        /// Uses stored raw FBX positions + UV0 to build an oldRaw→newRaw index mapping,
        /// then composes: newRaw[i] → oldRaw[j] → opt[originalRemap[j]].
        /// </summary>
        static int[] RebuildRemapFromPositions(Mesh mesh, MeshUv2Entry entry)
        {
            var storedPos = entry.vertPositions;
            var storedUv0 = entry.vertUv0;
            int storedCount = storedPos.Length;
            int[] origRemap = entry.vertexRemap;
            int optCount = entry.optimizedVertexCount;

            if (origRemap.Length != storedCount)
            {
                UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': stored positions ({storedCount}) != " +
                            $"original remap ({origRemap.Length}) — cannot rebuild remap.");
                return null;
            }

            var newPos = mesh.vertices;
            int newCount = newPos.Length;

            var newUv0 = new List<Vector2>();
            mesh.GetUVs(0, newUv0);
            bool hasUv0 = storedUv0 != null && storedUv0.Length == storedCount && newUv0.Count == newCount;

            // Build quantized position lookup for stored (old) raw positions
            var posLookup = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < storedCount; i++)
            {
                var key = QuantizePosForRemap(storedPos[i]);
                if (!posLookup.TryGetValue(key, out var list))
                {
                    list = new List<int>(2);
                    posLookup[key] = list;
                }
                list.Add(i);
            }

            // Map newRaw[i] → oldRaw[j] by position + UV0 matching.
            // Track coverage at OPTIMIZED index level (not stored raw index level).
            // Multiple raw vertices legitimately map to the same opt slot (meshopt
            // merges duplicates). Tracking at raw level would penalize these duplicates,
            // pushing them to wrong stored vertices with different origRemap values.
            // Tracking at opt level ensures each opt slot gets covered while allowing
            // legitimate duplicate mappings.
            var newRemap = new int[newCount];
            for (int i = 0; i < newCount; i++) newRemap[i] = -1;
            var coveredOpt = new bool[optCount > 0 ? optCount : 1];
            int matched = 0;

            for (int i = 0; i < newCount; i++)
            {
                var key = QuantizePosForRemap(newPos[i]);
                if (!posLookup.TryGetValue(key, out var candidates)) continue;

                int bestOld = PickBestCandidate(candidates, i, origRemap, coveredOpt,
                    hasUv0 ? newUv0 : null, storedUv0);

                if (bestOld >= 0)
                {
                    newRemap[i] = origRemap[bestOld];
                    if (origRemap[bestOld] >= 0)
                    {
                        coveredOpt[origRemap[bestOld]] = true;
                        matched++;
                    }
                }
            }

            // Pass 2: nearest-neighbor fallback for bucket boundary misses.
            // Allow reuse of already-used stored vertices here — these are rare
            // boundary cases where the quantization rounded differently, and the
            // nearest stored vertex (same position) should have the same origRemap.
            if (matched < newCount)
            {
                for (int i = 0; i < newCount; i++)
                {
                    if (newRemap[i] >= 0) continue;
                    float bestDist = float.MaxValue;
                    int bestOld = -1;
                    for (int j = 0; j < storedCount; j++)
                    {
                        float d = Vector3.SqrMagnitude(newPos[i] - storedPos[j]);
                        if (d < bestDist) { bestDist = d; bestOld = j; }
                    }
                    if (bestOld >= 0 && bestDist < 1e-4f)
                    {
                        newRemap[i] = origRemap[bestOld];
                        if (origRemap[bestOld] >= 0)
                        {
                            coveredOpt[origRemap[bestOld]] = true;
                            matched++;
                        }
                    }
                }
            }

            if (matched < newCount)
                UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': remap rebuild matched {matched}/{newCount} " +
                            $"({newCount - matched} unmapped)");

            // Coverage diagnostic: count how many referenced opt indices are still uncovered
            int uncoveredCount = 0;
            if (entry.optimizedTriangles != null && optCount > 0)
            {
                var referenced = new HashSet<int>();
                for (int i = 0; i < entry.optimizedTriangles.Length; i++)
                    referenced.Add(entry.optimizedTriangles[i]);

                var orphanSet = new HashSet<int>();
                if (entry.orphanIndices != null)
                    for (int i = 0; i < entry.orphanIndices.Length; i++)
                        orphanSet.Add(entry.orphanIndices[i]);

                foreach (int idx in referenced)
                    if (idx < optCount && !coveredOpt[idx] && !orphanSet.Contains(idx))
                        uncoveredCount++;
            }

            UvtLog.Info($"[UV2 Postprocess] '{mesh.name}': rebuilt remap from stored positions " +
                        $"({matched}/{newCount} matched, {uncoveredCount} uncovered opt indices, stale fingerprint)");
            return newRemap;
        }

        /// <summary>
        /// Pick the best stored vertex candidate for a new raw vertex.
        /// UV0 match is the PRIMARY signal (seam splits have very different UV0).
        /// Coverage (uncovered opt slot) is a SECONDARY tiebreaker among UV0-close candidates.
        /// This prevents duplicates (same UV0) from being pushed to wrong seam splits
        /// just because their opt slot is already covered.
        /// </summary>
        static int PickBestCandidate(List<int> candidates, int newIdx, int[] origRemap, bool[] coveredOpt,
                                      List<Vector2> newUv0, Vector2[] storedUv0)
        {
            if (candidates.Count == 1)
            {
                // Single candidate: always use it. Multiple raw verts can safely
                // map to the same stored vert (dedup — same position, same origRemap).
                return candidates[0];
            }

            bool hasUv = newUv0 != null && storedUv0 != null;

            // Pass 1: find best UV0 distance among valid (non -1) candidates
            float bestUv0Dist = float.MaxValue;
            for (int k = 0; k < candidates.Count; k++)
            {
                int ci = candidates[k];
                if (origRemap[ci] < 0) continue;
                float d = hasUv ? Vector2.SqrMagnitude(newUv0[newIdx] - storedUv0[ci]) : 0f;
                if (d < bestUv0Dist) bestUv0Dist = d;
            }

            // UV0 threshold: candidates within this range of the best are considered
            // "UV0-equivalent" (same seam side). Seam splits differ by >> 0.01 in UV space.
            // Use relative threshold so it works regardless of UV0 scale.
            float uvThreshold = hasUv ? bestUv0Dist + 1e-6f : float.MaxValue;

            // Pass 2: among UV0-close candidates, prefer uncovered opt slots
            int bestOld = -1;
            float bestScore = float.MaxValue;
            int bestPriority = int.MaxValue;
            // Priority: 0=UV0 close + uncovered, 1=UV0 close + covered,
            //           2=UV0 far + uncovered, 3=UV0 far + covered, 4=invalid(-1)

            for (int k = 0; k < candidates.Count; k++)
            {
                int ci = candidates[k];
                int opt = origRemap[ci];

                float uvDist = hasUv ? Vector2.SqrMagnitude(newUv0[newIdx] - storedUv0[ci]) : 0f;
                bool uvClose = uvDist <= uvThreshold;

                int priority;
                if (opt < 0)
                    priority = 4;
                else if (uvClose && !coveredOpt[opt])
                    priority = 0; // best: good UV0 match + uncovered
                else if (uvClose)
                    priority = 1; // good UV0 match + already covered (duplicate)
                else if (!coveredOpt[opt])
                    priority = 2; // bad UV0 match + uncovered (wrong seam side)
                else
                    priority = 3; // bad UV0 match + covered

                if (priority > bestPriority) continue;
                if (priority < bestPriority || uvDist < bestScore)
                {
                    bestPriority = priority;
                    bestScore = uvDist;
                    bestOld = ci;
                }
            }

            return bestOld;
        }

        static (int, int, int) QuantizePosForRemap(Vector3 pos)
        {
            return (
                Mathf.RoundToInt(pos.x * 10000f),
                Mathf.RoundToInt(pos.y * 10000f),
                Mathf.RoundToInt(pos.z * 10000f)
            );
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
