// GroupedShellTransfer.cs — UV2 transfer via shell-level matching
//
// Algorithm: "Shell-First Matching with Interpolation Primary"
// Phase 1: Extract UV0 shells from source and target
// Phase 1b: Precompute per-source-shell similarity transform (UV0→UV2)
// Phase 2a: Match each target shell → best source shell by 3D centroid
// Phase 2b: Deduplicate — resolve same-source conflicts (one-to-one matching)
// Phase 3: Per-vertex UV0 interpolation (primary, bounded by source UV2).
//          Similarity transform is fallback only when interpolation has
//          strictly more inverted/zero-area triangles.
//
// Interpolation stays within the convex hull of source UV2 triangles,
// preventing extrapolation artifacts. Transform can extrapolate when
// target UV0 differs from source UV0 (always the case on LOD meshes).

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class GroupedShellTransfer
    {
        // ─── Similarity Transform (4 params: a, b, tx, ty) ───
        public struct SimilarityTransform
        {
            public float a, b, tx, ty;
            public bool mirrored;
            public float residual; // RMS fit error

            public Vector2 Apply(Vector2 uv0)
            {
                if (mirrored)
                    return new Vector2( a * uv0.x + b * uv0.y + tx,
                                        b * uv0.x - a * uv0.y + ty);
                else
                    return new Vector2( a * uv0.x - b * uv0.y + tx,
                                        b * uv0.x + a * uv0.y + ty);
            }

            public bool valid; // false if degenerate (zero-area shell)
        }

        // ─── Shell info for cross-LOD analysis (UI stats) ───
        public class SourceShellInfo
        {
            public int shellId;
            public Vector2 uv0BoundsMin, uv0BoundsMax;
            public Vector2 uv0Centroid;
            public Vector3 worldCentroid;
            public float signedAreaUv0;
            public int vertexCount;
            public int[] vertexIndices;
            public Vector3[] worldPositions;
            public Vector3[] normals;
            public Vector2[] shellUv0;
            public Vector2[] shellUv2;
            public List<int> faceIndices;
        }

        public class TransferResult
        {
            public Vector2[] uv2;
            public int shellsMatched;
            public int shellsUnmatched;
            public int shellsTransform;    // shells that used similarity transform
            public int shellsInterpolation; // shells that fell back to interpolation
            public int shellsMerged;       // merged shells (multi-source)
            public int consistencyCorrected; // verts where 3D check overrode UV0 in merged shells
            public int verticesTransferred;
            public int verticesTotal;

            // ─── Diagnostics ───
            public int[] vertexToSourceShell;
            public int[] targetShellToSourceShell;
            public int[] targetShellMethod;  // 0=interp, 1=xform, 2=merged
            public int[] faceToTargetShell;  // face index → target UV0 shell index
            public Vector3[] targetShellCentroids;
            public float[] targetShellMatchDistSqr;
            public int dedupConflicts;       // shells reassigned by dedup
        }

        // ═══════════════════════════════════════════════════════════
        //  Compute similarity transform from UV0→UV2 pairs
        //  Least-squares fit: UV2 = [a -b; b a] * UV0 + [tx; ty]
        //  For mirrored:      UV2 = [a  b; b -a] * UV0 + [tx; ty]
        // ═══════════════════════════════════════════════════════════

        public static SimilarityTransform ComputeSimilarityTransform(
            Vector2[] uv0, Vector2[] uv2, int[] vertexIndices, bool mirrored)
        {
            var result = new SimilarityTransform { mirrored = mirrored };

            // Collect valid pairs
            int n = 0;
            double mx0 = 0, my0 = 0, mx2 = 0, my2 = 0;
            for (int k = 0; k < vertexIndices.Length; k++)
            {
                int vi = vertexIndices[k];
                if (vi >= uv0.Length || vi >= uv2.Length) continue;
                mx0 += uv0[vi].x; my0 += uv0[vi].y;
                mx2 += uv2[vi].x; my2 += uv2[vi].y;
                n++;
            }

            if (n < 2) return result; // degenerate

            mx0 /= n; my0 /= n; mx2 /= n; my2 /= n;

            // Centered least-squares
            double Sxx = 0, Syy = 0; // sum of x0c^2 + y0c^2
            double Sab_a = 0, Sab_b = 0;

            for (int k = 0; k < vertexIndices.Length; k++)
            {
                int vi = vertexIndices[k];
                if (vi >= uv0.Length || vi >= uv2.Length) continue;

                double x0c = uv0[vi].x - mx0;
                double y0c = uv0[vi].y - my0;
                double x2c = uv2[vi].x - mx2;
                double y2c = uv2[vi].y - my2;

                Sxx += x0c * x0c + y0c * y0c;

                if (mirrored)
                {
                    // UV2 = [a b; b -a] * UV0_centered
                    Sab_a += x0c * x2c - y0c * y2c;
                    Sab_b += x0c * y2c + y0c * x2c;
                }
                else
                {
                    // UV2 = [a -b; b a] * UV0_centered
                    Sab_a += x0c * x2c + y0c * y2c;
                    Sab_b += x0c * y2c - y0c * x2c;
                }
            }

            if (Sxx < 1e-14) return result; // zero-area shell in UV0

            double a = Sab_a / Sxx;
            double b = Sab_b / Sxx;

            // Translation from centroids
            double tx, ty;
            if (mirrored)
            {
                tx = mx2 - a * mx0 - b * my0;
                ty = my2 - b * mx0 + a * my0;
            }
            else
            {
                tx = mx2 - a * mx0 + b * my0;
                ty = my2 - b * mx0 - a * my0;
            }

            result.a = (float)a;
            result.b = (float)b;
            result.tx = (float)tx;
            result.ty = (float)ty;
            result.valid = true;

            // Compute RMS residual
            double sumSqErr = 0;
            for (int k = 0; k < vertexIndices.Length; k++)
            {
                int vi = vertexIndices[k];
                if (vi >= uv0.Length || vi >= uv2.Length) continue;
                Vector2 predicted = result.Apply(uv0[vi]);
                float dx = predicted.x - uv2[vi].x;
                float dy = predicted.y - uv2[vi].y;
                sumSqErr += dx * dx + dy * dy;
            }
            result.residual = (float)System.Math.Sqrt(sumSqErr / n);

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  AnalyzeSource — extract UV0 shells for UI display
        // ═══════════════════════════════════════════════════════════

        public static SourceShellInfo[] AnalyzeSource(Mesh sourceMesh)
        {
            var uv0List = new List<Vector2>();
            var uv2List = new List<Vector2>();
            sourceMesh.GetUVs(0, uv0List);
            sourceMesh.GetUVs(1, uv2List);
            if (uv0List.Count == 0 || uv2List.Count == 0)
            {
                UvtLog.Error("[GroupedTransfer] Source mesh missing UV0 or UV2");
                return null;
            }
            var uv0 = uv0List.ToArray();
            var uv2 = uv2List.ToArray();
            var tris = sourceMesh.triangles;
            var verts = sourceMesh.vertices;
            var norms = sourceMesh.normals;
            bool hasN = norms != null && norms.Length == verts.Length;

            var shells = UvShellExtractor.Extract(uv0, tris);
            var infos = new SourceShellInfo[shells.Count];
            for (int si = 0; si < shells.Count; si++)
            {
                var sh = shells[si];
                var idx = new List<int>(); var pos = new List<Vector3>();
                var nrm = new List<Vector3>(); var u0s = new List<Vector2>();
                var u2s = new List<Vector2>();
                Vector2 u0Sum = Vector2.zero; Vector3 wSum = Vector3.zero; int n = 0;
                foreach (int vi in sh.vertexIndices)
                {
                    if (vi >= uv0.Length || vi >= uv2.Length || vi >= verts.Length) continue;
                    idx.Add(vi); pos.Add(verts[vi]);
                    nrm.Add(hasN ? norms[vi] : Vector3.up);
                    u0s.Add(uv0[vi]); u2s.Add(uv2[vi]);
                    u0Sum += uv0[vi]; wSum += verts[vi]; n++;
                }
                float sa = ComputeSignedArea(tris, uv0, sh.faceIndices);
                infos[si] = new SourceShellInfo
                {
                    shellId = sh.shellId,
                    uv0BoundsMin = sh.boundsMin, uv0BoundsMax = sh.boundsMax,
                    uv0Centroid = n > 0 ? u0Sum / n : Vector2.zero,
                    worldCentroid = n > 0 ? wSum / n : Vector3.zero,
                    signedAreaUv0 = sa, vertexCount = n,
                    vertexIndices = idx.ToArray(), worldPositions = pos.ToArray(),
                    normals = nrm.ToArray(), shellUv0 = u0s.ToArray(),
                    shellUv2 = u2s.ToArray(), faceIndices = sh.faceIndices
                };
            }

            return infos;
        }

        // ═══════════════════════════════════════════════════════════
        //  FindBestSourceShell — match a target shell to the best
        //  source shell by 3D vertex-to-surface distance.
        //  Optionally excludes already-claimed source shells (dedup).
        // ═══════════════════════════════════════════════════════════

        static void FindBestSourceShell(
            UvShell tShell,
            Vector3[] tVerts,
            List<UvShell> srcShells,
            Vector3[] srcCentroid3D,
            Vector3[] triPosA, Vector3[] triPosB, Vector3[] triPosC,
            Vector3 tCentroid,
            int maxRetries, float goodDistSq,
            HashSet<int> excludeSources,
            out int chosenSrc, out float chosenDistSq, out float chosenAvg3D)
        {
            chosenSrc = -1;
            chosenDistSq = float.MaxValue;
            chosenAvg3D = float.MaxValue;

            // Rank source shells by 3D centroid distance
            var ranked = new List<(int si, float distSq)>();
            for (int si = 0; si < srcShells.Count; si++)
            {
                if (excludeSources != null && excludeSources.Contains(si)) continue;
                float d = (tCentroid - srcCentroid3D[si]).sqrMagnitude;
                ranked.Add((si, d));
            }
            ranked.Sort((a, b) => a.distSq.CompareTo(b.distSq));

            int tries = Mathf.Min(maxRetries, ranked.Count);
            for (int attempt = 0; attempt < tries; attempt++)
            {
                int si = ranked[attempt].si;
                var srcFaces = srcShells[si].faceIndices;

                float totalDistSq = 0; int sampled = 0;
                foreach (int vi in tShell.vertexIndices)
                {
                    if (vi >= tVerts.Length) continue;
                    Vector3 tPos = tVerts[vi];
                    float bestDSq = float.MaxValue;
                    for (int fi = 0; fi < srcFaces.Count; fi++)
                    {
                        int f = srcFaces[fi];
                        float dSq = PointToTri3D(tPos, triPosA[f], triPosB[f], triPosC[f],
                            out _, out _, out _);
                        if (dSq < bestDSq) bestDSq = dSq;
                    }
                    totalDistSq += bestDSq;
                    sampled++;
                }

                float avgDist = sampled > 0 ? totalDistSq / sampled : float.MaxValue;
                if (avgDist < chosenAvg3D)
                {
                    chosenSrc = si;
                    chosenDistSq = ranked[attempt].distSq;
                    chosenAvg3D = avgDist;
                }
                if (chosenAvg3D < goodDistSq) break;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  DetectMergedShell — check if a target shell's UV0 coverage
        //  is too poor for the matched source shell (>30% verts bad).
        // ═══════════════════════════════════════════════════════════

        static bool DetectMergedShell(
            UvShell tShell, Vector2[] tUv0,
            List<int> srcFaces,
            Vector2[] triUv0A, Vector2[] triUv0B, Vector2[] triUv0C)
        {
            const float kUv0BadThreshold = 0.01f;
            int uv0BadCount = 0;
            foreach (int vi in tShell.vertexIndices)
            {
                if (vi >= tUv0.Length) continue;
                Vector2 tUv = tUv0[vi];
                float bestDSq = float.MaxValue;
                for (int fi = 0; fi < srcFaces.Count; fi++)
                {
                    int f = srcFaces[fi];
                    float dSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                        out _, out _, out _);
                    if (dSq < bestDSq) bestDSq = dSq;
                    if (bestDSq < 1e-8f) break;
                }
                if (bestDSq > kUv0BadThreshold) uv0BadCount++;
            }
            int sv = tShell.vertexIndices.Count;
            return sv > 0 && (float)uv0BadCount / sv > 0.3f;
        }

        // ═══════════════════════════════════════════════════════════
        //  Transfer: Shell-first matching, interpolation primary
        //
        //  Phase 1:  Extract UV0 shells from source & target
        //  Phase 1b: Precompute similarity transform per source shell
        //  Phase 2a: Match each target shell → source shell by 3D centroid
        //  Phase 2b: Deduplicate — one-to-one source assignment
        //  Phase 3:  Per-vertex UV0 interpolation (primary, bounded).
        //            Similarity transform only if it produces strictly
        //            fewer inverted/zero-area triangles.
        // ═══════════════════════════════════════════════════════════

        public static TransferResult Transfer(Mesh targetMesh, Mesh sourceMesh)
        {
            var result = new TransferResult();

            // Source data
            var srcVerts = sourceMesh.vertices;
            var srcTris = sourceMesh.triangles;
            var srcUv0List = new List<Vector2>(); sourceMesh.GetUVs(0, srcUv0List);
            var srcUv2List = new List<Vector2>(); sourceMesh.GetUVs(1, srcUv2List);
            var srcUv0 = srcUv0List.ToArray();
            var srcUv2 = srcUv2List.ToArray();

            if (srcUv0.Length == 0 || srcUv2.Length == 0)
            { UvtLog.Error("[GroupedTransfer] Source missing UV0/UV2"); return result; }

            // Target data
            var tVerts = targetMesh.vertices;
            var tNormals = targetMesh.normals;
            var tUv0List = new List<Vector2>(); targetMesh.GetUVs(0, tUv0List);
            var tUv0 = tUv0List.ToArray();
            int vertCount = targetMesh.vertexCount;

            if (tUv0.Length == 0)
            { UvtLog.Error("[GroupedTransfer] Target missing UV0"); return result; }

            result.uv2 = new Vector2[vertCount];
            result.verticesTotal = vertCount;
            result.vertexToSourceShell = new int[vertCount];
            for (int i = 0; i < vertCount; i++) result.vertexToSourceShell[i] = -1;

            // ── Pre-compute source triangle data (3D + UV0 + UV2) ──
            int srcTriCount = srcTris.Length / 3;
            var triPosA = new Vector3[srcTriCount];
            var triPosB = new Vector3[srcTriCount];
            var triPosC = new Vector3[srcTriCount];
            var triUv0A = new Vector2[srcTriCount];
            var triUv0B = new Vector2[srcTriCount];
            var triUv0C = new Vector2[srcTriCount];
            var triUv2A = new Vector2[srcTriCount];
            var triUv2B = new Vector2[srcTriCount];
            var triUv2C = new Vector2[srcTriCount];

            for (int f = 0; f < srcTriCount; f++)
            {
                int i0 = srcTris[f * 3], i1 = srcTris[f * 3 + 1], i2 = srcTris[f * 3 + 2];
                triPosA[f] = srcVerts[i0]; triPosB[f] = srcVerts[i1]; triPosC[f] = srcVerts[i2];
                triUv0A[f] = srcUv0[i0];  triUv0B[f] = srcUv0[i1];  triUv0C[f] = srcUv0[i2];
                triUv2A[f] = srcUv2[i0];  triUv2B[f] = srcUv2[i1];  triUv2C[f] = srcUv2[i2];
            }

            // Pre-compute source triangle normals
            var triNormal = new Vector3[srcTriCount];
            for (int f = 0; f < srcTriCount; f++)
            {
                triNormal[f] = Vector3.Cross(triPosB[f] - triPosA[f], triPosC[f] - triPosA[f]).normalized;
            }

            // ── Phase 1: Extract shells ──
            var srcShells = UvShellExtractor.Extract(srcUv0, srcTris);
            var tgtTris = targetMesh.triangles;
            var tgtShells = UvShellExtractor.Extract(tUv0, tgtTris);

            // Build face → source shell lookup
            var faceToSrcShell = new int[srcTriCount];
            for (int si = 0; si < srcShells.Count; si++)
                foreach (int f in srcShells[si].faceIndices)
                    faceToSrcShell[f] = si;

            // Build face → target shell lookup (for overlap diagnostics)
            int tgtTriCount = tgtTris.Length / 3;
            result.faceToTargetShell = new int[tgtTriCount];
            for (int i = 0; i < tgtTriCount; i++) result.faceToTargetShell[i] = -1;
            for (int tsi = 0; tsi < tgtShells.Count; tsi++)
                foreach (int f in tgtShells[tsi].faceIndices)
                    result.faceToTargetShell[f] = tsi;

            // ── Phase 1b: Precompute similarity transform per source shell ──
            var srcTransforms = new SimilarityTransform[srcShells.Count];
            for (int si = 0; si < srcShells.Count; si++)
            {
                var sh = srcShells[si];
                var idxArr = new int[sh.vertexIndices.Count];
                sh.vertexIndices.CopyTo(idxArr, 0);

                // Detect mirrored: compare signed areas of UV0 and UV2
                float saUv0 = ComputeSignedArea(srcTris, srcUv0, sh.faceIndices);
                float saUv2 = ComputeSignedArea(srcTris, srcUv2, sh.faceIndices);
                bool mirrored = saUv0 * saUv2 < 0f;

                srcTransforms[si] = ComputeSimilarityTransform(srcUv0, srcUv2, idxArr, mirrored);
            }

            // Compute 3D centroid + AABB for each source shell
            var srcCentroid3D = new Vector3[srcShells.Count];
            var srcAABBMin = new Vector3[srcShells.Count];
            var srcAABBMax = new Vector3[srcShells.Count];
            for (int si = 0; si < srcShells.Count; si++)
            {
                Vector3 sum = Vector3.zero; int n = 0;
                Vector3 bMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 bMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                foreach (int vi in srcShells[si].vertexIndices)
                {
                    if (vi < srcVerts.Length)
                    {
                        sum += srcVerts[vi]; n++;
                        bMin = Vector3.Min(bMin, srcVerts[vi]);
                        bMax = Vector3.Max(bMax, srcVerts[vi]);
                    }
                }
                srcCentroid3D[si] = n > 0 ? sum / n : Vector3.zero;
                srcAABBMin[si] = bMin;
                srcAABBMax[si] = bMax;
            }

            // ── Phase 2a: Match each target shell → best source shell ──
            result.targetShellToSourceShell = new int[tgtShells.Count];
            result.targetShellMethod = new int[tgtShells.Count]; // 0=interp, 1=xform, 2=merged
            result.targetShellCentroids = new Vector3[tgtShells.Count];
            result.targetShellMatchDistSqr = new float[tgtShells.Count];

            var tgtChosenAvg3D = new float[tgtShells.Count];
            var tgtIsMerged = new bool[tgtShells.Count];

            for (int i = 0; i < tgtShells.Count; i++)
            {
                result.targetShellToSourceShell[i] = -1;
                result.targetShellMethod[i] = -1;
                result.targetShellMatchDistSqr[i] = float.MaxValue;
                tgtChosenAvg3D[i] = float.MaxValue;
            }

            const float kGoodDistSq = 0.001f;
            const int kMaxRetries = 5;

            for (int tsi = 0; tsi < tgtShells.Count; tsi++)
            {
                var tShell = tgtShells[tsi];

                // Compute target shell 3D centroid
                Vector3 tCentroid = Vector3.zero; int tN = 0;
                foreach (int vi in tShell.vertexIndices)
                {
                    if (vi < tVerts.Length) { tCentroid += tVerts[vi]; tN++; }
                }
                if (tN > 0) tCentroid /= tN;
                result.targetShellCentroids[tsi] = tCentroid;

                // Find best source shell
                FindBestSourceShell(tShell, tVerts, srcShells, srcCentroid3D,
                    triPosA, triPosB, triPosC, tCentroid,
                    kMaxRetries, kGoodDistSq, null,
                    out int chosenSrc, out float chosenDistSq, out float chosenAvg3D);

                if (chosenSrc < 0) continue;

                result.targetShellToSourceShell[tsi] = chosenSrc;
                result.targetShellMatchDistSqr[tsi] = chosenDistSq;
                tgtChosenAvg3D[tsi] = chosenAvg3D;

                // Detect merged shell via UV0 coverage
                tgtIsMerged[tsi] = DetectMergedShell(tShell, tUv0,
                    srcShells[chosenSrc].faceIndices, triUv0A, triUv0B, triUv0C);
            }

            // ── Phase 2b: Deduplicate — resolve same-source conflicts ──
            // When multiple non-merged target shells claim the same source shell
            // (common with overlapping/tiling UV0), keep the best match and
            // reassign others to different source shells at the same 3D location.
            {
                // Build reverse map: source → list of non-merged target claimants
                var srcClaimants = new Dictionary<int, List<(int tsi, float avg3D)>>();
                for (int tsi = 0; tsi < tgtShells.Count; tsi++)
                {
                    int src = result.targetShellToSourceShell[tsi];
                    if (src < 0 || tgtIsMerged[tsi]) continue; // skip unmatched & merged
                    if (!srcClaimants.TryGetValue(src, out var list))
                    {
                        list = new List<(int, float)>();
                        srcClaimants[src] = list;
                    }
                    list.Add((tsi, tgtChosenAvg3D[tsi]));
                }

                // Identify conflicts and build claimed set
                var claimed = new HashSet<int>();
                var needsRematch = new List<int>();
                int dedupConflicts = 0;

                foreach (var kv in srcClaimants)
                {
                    claimed.Add(kv.Key); // source is claimed regardless
                    if (kv.Value.Count <= 1) continue;

                    // Multiple non-merged targets claim same source — keep best
                    kv.Value.Sort((a, b) => a.avg3D.CompareTo(b.avg3D));
                    for (int i = 1; i < kv.Value.Count; i++)
                    {
                        needsRematch.Add(kv.Value[i].tsi);
                        dedupConflicts++;
                    }
                }

                // Re-match evicted targets with exclusion of already-claimed sources
                if (needsRematch.Count > 0)
                {
                    // Iterative: each rematch claims a new source
                    for (int iteration = 0; iteration < 3 && needsRematch.Count > 0; iteration++)
                    {
                        var stillNeedsRematch = new List<int>();

                        foreach (int tsi in needsRematch)
                        {
                            var tShell = tgtShells[tsi];
                            int oldSrc = result.targetShellToSourceShell[tsi];
                            float oldDistSq = result.targetShellMatchDistSqr[tsi];
                            float oldAvg3D = tgtChosenAvg3D[tsi];

                            FindBestSourceShell(tShell, tVerts, srcShells, srcCentroid3D,
                                triPosA, triPosB, triPosC, result.targetShellCentroids[tsi],
                                kMaxRetries * 3, kGoodDistSq, claimed,
                                out int newSrc, out float newDistSq, out float newAvg3D);

                            if (newSrc >= 0)
                            {
                                // Re-check merged status with new source
                                bool newIsMerged = DetectMergedShell(tShell, tUv0,
                                    srcShells[newSrc].faceIndices, triUv0A, triUv0B, triUv0C);

                                // If reassignment would make a non-merged shell become merged,
                                // revert to original source — overlap is better than lost UV2.
                                if (newIsMerged && !tgtIsMerged[tsi] && oldSrc >= 0)
                                {
                                    UvtLog.Info($"[GroupedTransfer] Dedup: t{tsi} reverted to src{oldSrc} " +
                                        $"(new src{newSrc} would force merged)");
                                    // Keep old assignment, just re-claim old source
                                    claimed.Add(oldSrc);
                                }
                                else
                                {
                                    result.targetShellToSourceShell[tsi] = newSrc;
                                    result.targetShellMatchDistSqr[tsi] = newDistSq;
                                    tgtChosenAvg3D[tsi] = newAvg3D;
                                    claimed.Add(newSrc);
                                    tgtIsMerged[tsi] = newIsMerged;
                                }
                            }
                            else
                            {
                                stillNeedsRematch.Add(tsi);
                            }
                        }

                        needsRematch = stillNeedsRematch;
                    }

                    // Any remaining unmatched — force to merged mode
                    foreach (int tsi in needsRematch)
                    {
                        tgtIsMerged[tsi] = true;
                    }

                    result.dedupConflicts = dedupConflicts;
                    if (dedupConflicts > 0)
                        UvtLog.Info($"[GroupedTransfer] Dedup: {dedupConflicts} same-source conflicts, " +
                            $"{needsRematch.Count} forced merged");
                }
            }

            // ── Phase 3: Transfer UV2 using final source assignments ──
            // Verbose: dump per-shell matching for diagnostics
            for (int tsi = 0; tsi < tgtShells.Count; tsi++)
            {
                int src = result.targetShellToSourceShell[tsi];
                string method = tgtIsMerged[tsi] ? "merged" : "interp";
                int tFaces = tgtShells[tsi].faceIndices.Count;
                int tVtx = tgtShells[tsi].vertexIndices.Count;
                float avg3D = tgtChosenAvg3D[tsi];
                string srcInfo = src >= 0 ? $"src{src}({srcShells[src].faceIndices.Count}f)" : "none";
                UvtLog.Verbose($"[GroupedTransfer]   t{tsi}({tFaces}f,{tVtx}v) → {srcInfo} [{method}] avg3D={avg3D:F6}");
            }

            int transferred = 0;
            int shellsMatched = 0;
            int shellsTransform = 0, shellsInterpolation = 0, shellsMerged = 0;

            for (int tsi = 0; tsi < tgtShells.Count; tsi++)
            {
                var tShell = tgtShells[tsi];
                int chosenSrc = result.targetShellToSourceShell[tsi];

                // Skip unmatched non-merged shells
                if (chosenSrc < 0 && !tgtIsMerged[tsi]) continue;

                shellsMatched++;
                var srcFacesChosen = chosenSrc >= 0 ? srcShells[chosenSrc].faceIndices : null;

                Dictionary<int, Vector2> chosenUv2;

                if (tgtIsMerged[tsi])
                {
                    // ── Merged shell: try source-constrained first, fall back to all-source ──
                    // Tiling merged: constrained gives 0 issues → stays within one UV2 island.
                    // Genuine merged: constrained gives issues → fallback to all-source search.
                    const float kConsistencyThresh = 0.02f;
                    const float kUv0DistantThresh = 0.05f;
                    const float kBackfaceDot = 0.3f;

                    Dictionary<int, Vector2> bestMergedUv2 = null;
                    int bestMergedIssues = int.MaxValue;
                    int bestMergedConsistencyFixes = 0;
                    bool bestWasConstrained = false;

                    for (int pass = 0; pass < 2; pass++)
                    {
                        bool constrained;
                        if (pass == 0)
                        {
                            constrained = (srcFacesChosen != null);
                        }
                        else
                        {
                            if (srcFacesChosen == null) break; // pass 0 was already all-source
                            if (bestMergedIssues == 0) break;  // constrained was clean
                            constrained = false;               // try all-source
                        }

                        int searchCount = constrained ? srcFacesChosen.Count : srcTriCount;
                        var candidate = new Dictionary<int, Vector2>();
                        int localFixes = 0;

                        foreach (int vi in tShell.vertexIndices)
                        {
                            if (vi >= tUv0.Length || vi >= tVerts.Length) continue;
                            Vector2 tUv = tUv0[vi];
                            Vector3 tPos = tVerts[vi];
                            Vector3 tNrm = (tNormals != null && vi < tNormals.Length)
                                ? tNormals[vi] : Vector3.up;

                            // ── UV0 projection (primary) ──
                            float bestDSqUv0 = float.MaxValue;
                            int bestFUv0 = -1; float bestU_uv0 = 0, bestV_uv0 = 0, bestW_uv0 = 0;
                            for (int fi = 0; fi < searchCount; fi++)
                            {
                                int f = constrained ? srcFacesChosen[fi] : fi;
                                float dSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                                    out float u, out float v, out float w);
                                if (dSq < bestDSqUv0) { bestDSqUv0 = dSq; bestFUv0 = f; bestU_uv0 = u; bestV_uv0 = v; bestW_uv0 = w; }
                                if (bestDSqUv0 < 1e-8f) break;
                            }

                            // ── 3D projection (secondary, with backface filter) ──
                            float bestDSq3D = float.MaxValue;
                            int bestF3D = -1; float bestU_3d = 0, bestV_3d = 0, bestW_3d = 0;
                            for (int fi = 0; fi < searchCount; fi++)
                            {
                                int f = constrained ? srcFacesChosen[fi] : fi;
                                if (Vector3.Dot(triNormal[f], tNrm) < kBackfaceDot) continue;
                                float dSq = PointToTri3D(tPos, triPosA[f], triPosB[f], triPosC[f],
                                    out float u, out float v, out float w);
                                if (dSq < bestDSq3D) { bestDSq3D = dSq; bestF3D = f; bestU_3d = u; bestV_3d = v; bestW_3d = w; }
                            }

                            // ── Consistency decision ──
                            Vector2 uv2FromUv0 = (bestFUv0 >= 0)
                                ? triUv2A[bestFUv0] * bestU_uv0 + triUv2B[bestFUv0] * bestV_uv0 + triUv2C[bestFUv0] * bestW_uv0
                                : Vector2.zero;
                            Vector2 uv2From3D = (bestF3D >= 0)
                                ? triUv2A[bestF3D] * bestU_3d + triUv2B[bestF3D] * bestV_3d + triUv2C[bestF3D] * bestW_3d
                                : Vector2.zero;

                            if (bestFUv0 >= 0 && bestF3D >= 0)
                            {
                                float delta = (uv2FromUv0 - uv2From3D).magnitude;
                                if (bestDSqUv0 > kUv0DistantThresh && delta > kConsistencyThresh)
                                {
                                    candidate[vi] = uv2From3D;
                                    localFixes++;
                                }
                                else
                                {
                                    candidate[vi] = uv2FromUv0;
                                }
                            }
                            else if (bestFUv0 >= 0)
                            {
                                candidate[vi] = uv2FromUv0;
                            }
                            else if (bestF3D >= 0)
                            {
                                candidate[vi] = uv2From3D;
                                localFixes++;
                            }
                        }

                        int issues = CountShellIssues(tShell.faceIndices, tgtTris, tUv0, candidate);
                        // Prefer all-source on tie when both have issues:
                        // UV0 flipped winding causes false positives in CountShellIssues,
                        // so equal non-zero scores mean all-source (wider search) is safer.
                        bool better = (issues < bestMergedIssues) ||
                            (issues == bestMergedIssues && !constrained && bestWasConstrained && issues > 0);
                        if (better)
                        {
                            bestMergedIssues = issues;
                            bestMergedUv2 = candidate;
                            bestMergedConsistencyFixes = localFixes;
                            bestWasConstrained = constrained;
                        }
                    }

                    chosenUv2 = bestMergedUv2;
                    shellsMerged++;
                    result.targetShellMethod[tsi] = 2; // merged
                    result.consistencyCorrected += bestMergedConsistencyFixes;
                    UvtLog.Info($"[GroupedTransfer]   t{tsi} merged({tShell.faceIndices.Count}f): " +
                        $"{(bestWasConstrained ? "src-constrained" : "all-source")} ({bestMergedIssues} issues)");
                }
                else
                {
                    // ── Non-merged shell: try similarity transform first ──
                    var xf = srcTransforms[chosenSrc];

                    Dictionary<int, Vector2> uv2_transform = null;
                    int issuesTransform = int.MaxValue;

                    if (xf.valid)
                    {
                        uv2_transform = new Dictionary<int, Vector2>();
                        foreach (int vi in tShell.vertexIndices)
                        {
                            if (vi >= tUv0.Length) continue;
                            uv2_transform[vi] = xf.Apply(tUv0[vi]);
                        }
                        issuesTransform = CountShellIssues(tShell.faceIndices, tgtTris, tUv0, uv2_transform);
                    }

                    // Candidate B: per-vertex UV0 interpolation
                    var uv2_interp = new Dictionary<int, Vector2>();
                    foreach (int vi in tShell.vertexIndices)
                    {
                        if (vi >= tUv0.Length) continue;
                        Vector2 tUv = tUv0[vi];
                        float bestDSq = float.MaxValue;
                        int bestF = -1; float bestU = 0, bestV = 0, bestW = 0;
                        for (int fi = 0; fi < srcFacesChosen.Count; fi++)
                        {
                            int f = srcFacesChosen[fi];
                            float dSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                                out float u, out float v, out float w);
                            if (dSq < bestDSq) { bestDSq = dSq; bestF = f; bestU = u; bestV = v; bestW = w; }
                        }
                        if (bestF >= 0)
                            uv2_interp[vi] = triUv2A[bestF] * bestU + triUv2B[bestF] * bestV + triUv2C[bestF] * bestW;
                    }
                    int issuesInterp = CountShellIssues(tShell.faceIndices, tgtTris, tUv0, uv2_interp);

                    if (uv2_transform != null && issuesTransform < issuesInterp)
                    {
                        chosenUv2 = uv2_transform;
                        shellsTransform++;
                        result.targetShellMethod[tsi] = 1; // xform
                    }
                    else
                    {
                        chosenUv2 = uv2_interp;
                        shellsInterpolation++;
                        result.targetShellMethod[tsi] = 0; // interp
                    }
                }

                // Write chosen UV2
                int srcForLog = chosenSrc >= 0 ? chosenSrc : -1;
                foreach (var kv in chosenUv2)
                {
                    result.uv2[kv.Key] = kv.Value;
                    result.vertexToSourceShell[kv.Key] = srcForLog;
                    transferred++;
                }
            }

            result.verticesTransferred = transferred;
            result.shellsMatched = shellsMatched;
            result.shellsTransform = shellsTransform;
            result.shellsInterpolation = shellsInterpolation;
            result.shellsMerged = shellsMerged;

            UvtLog.Info($"[GroupedTransfer] '{targetMesh.name}': " +
                $"{tgtShells.Count} target → {shellsMatched} matched " +
                $"(xform:{shellsTransform} interp:{shellsInterpolation} merged:{shellsMerged}), " +
                $"{transferred}/{vertCount} verts" +
                (result.dedupConflicts > 0
                    ? $" (dedup:{result.dedupConflicts})"
                    : "") +
                (result.consistencyCorrected > 0
                    ? $" (consistency-corrected:{result.consistencyCorrected})"
                    : ""));

            // UV2 bounds check
            int oob = 0;
            Vector2 uvMin = Vector2.one * float.MaxValue, uvMax = Vector2.one * float.MinValue;
            for (int i = 0; i < result.uv2.Length; i++)
            {
                var uv = result.uv2[i];
                uvMin = Vector2.Min(uvMin, uv); uvMax = Vector2.Max(uvMax, uv);
                if (uv.x < -0.01f || uv.x > 1.01f || uv.y < -0.01f || uv.y > 1.01f) oob++;
            }
            if (oob > 0)
                UvtLog.Warn($"[GroupedTransfer] '{targetMesh.name}': {oob} verts outside 0-1! " +
                    $"UV2=[{uvMin.x:F3},{uvMin.y:F3}]-[{uvMax.x:F3},{uvMax.y:F3}]");

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  Count triangle issues (inverted + zero-area) for a UV2 candidate
        //  Used to pick between transform and interpolation per shell
        // ═══════════════════════════════════════════════════════════

        static int CountShellIssues(List<int> faceIndices, int[] tris, Vector2[] uv0,
            Dictionary<int, Vector2> candidateUv2)
        {
            int issues = 0;
            foreach (int f in faceIndices)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                if (!candidateUv2.TryGetValue(i0, out var a2) ||
                    !candidateUv2.TryGetValue(i1, out var b2) ||
                    !candidateUv2.TryGetValue(i2, out var c2))
                { issues++; continue; }

                float saUv2 = (b2.x - a2.x) * (c2.y - a2.y) - (c2.x - a2.x) * (b2.y - a2.y);
                if (Mathf.Abs(saUv2) < 1e-10f) { issues++; continue; }

                if (i0 < uv0.Length && i1 < uv0.Length && i2 < uv0.Length)
                {
                    var a0 = uv0[i0]; var b0 = uv0[i1]; var c0 = uv0[i2];
                    float saUv0 = (b0.x - a0.x) * (c0.y - a0.y) - (c0.x - a0.x) * (b0.y - a0.y);
                    if (saUv0 * saUv2 < 0f) issues++;
                }
            }
            return issues;
        }

        // ═══════════════════════════════════════════════════════════
        //  3D point-to-triangle distance (world space)
        // ═══════════════════════════════════════════════════════════

        static float PointToTri3D(Vector3 p, Vector3 a, Vector3 b, Vector3 c,
            out float u, out float v, out float w)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d00 = Vector3.Dot(ab, ab), d01 = Vector3.Dot(ab, ac);
            float d11 = Vector3.Dot(ac, ac), d20 = Vector3.Dot(ap, ab);
            float d21 = Vector3.Dot(ap, ac);
            float denom = d00 * d11 - d01 * d01;

            if (Mathf.Abs(denom) < 1e-12f)
            { u = 1f; v = 0f; w = 0f; return (p - a).sqrMagnitude; }

            float bV = (d11 * d20 - d01 * d21) / denom;
            float bW = (d00 * d21 - d01 * d20) / denom;
            float bU = 1f - bV - bW;

            if (bU >= 0f && bV >= 0f && bW >= 0f)
            {
                u = bU; v = bV; w = bW;
                Vector3 proj = a * u + b * v + c * w;
                return (p - proj).sqrMagnitude;
            }

            float best = float.MaxValue; u = 1; v = 0; w = 0;
            { float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(d00, 1e-12f));
              float d = (p - (a + ab * t)).sqrMagnitude;
              if (d < best) { best = d; u = 1f - t; v = t; w = 0f; } }
            { float t = Mathf.Clamp01(Vector3.Dot(p - a, ac) / Mathf.Max(d11, 1e-12f));
              float d = (p - (a + ac * t)).sqrMagnitude;
              if (d < best) { best = d; u = 1f - t; v = 0f; w = t; } }
            { Vector3 bc = c - b; float bcL = Vector3.Dot(bc, bc);
              float t = Mathf.Clamp01(Vector3.Dot(p - b, bc) / Mathf.Max(bcL, 1e-12f));
              float d = (p - (b + bc * t)).sqrMagnitude;
              if (d < best) { best = d; u = 0f; v = 1f - t; w = t; } }
            return best;
        }

        // ═══════════════════════════════════════════════════════════
        //  2D point-to-triangle distance (UV0 space)
        // ═══════════════════════════════════════════════════════════

        static float PointToTri2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c,
            out float u, out float v, out float w)
        {
            Vector2 ab = b - a, ac = c - a, ap = p - a;
            float d00 = Vector2.Dot(ab, ab), d01 = Vector2.Dot(ab, ac);
            float d11 = Vector2.Dot(ac, ac), d20 = Vector2.Dot(ap, ab);
            float d21 = Vector2.Dot(ap, ac);
            float denom = d00 * d11 - d01 * d01;

            if (Mathf.Abs(denom) < 1e-12f)
            { u = 1f; v = 0f; w = 0f; return (p - a).sqrMagnitude; }

            float bV = (d11 * d20 - d01 * d21) / denom;
            float bW = (d00 * d21 - d01 * d20) / denom;
            float bU = 1f - bV - bW;

            if (bU >= 0f && bV >= 0f && bW >= 0f)
            {
                u = bU; v = bV; w = bW;
                Vector2 proj = a * u + b * v + c * w;
                return (p - proj).sqrMagnitude;
            }

            float best = float.MaxValue; u = 1; v = 0; w = 0;
            { float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(d00, 1e-12f));
              float d = (p - (a + ab * t)).sqrMagnitude;
              if (d < best) { best = d; u = 1f - t; v = t; w = 0f; } }
            { float t = Mathf.Clamp01(Vector2.Dot(p - a, ac) / Mathf.Max(d11, 1e-12f));
              float d = (p - (a + ac * t)).sqrMagnitude;
              if (d < best) { best = d; u = 1f - t; v = 0f; w = t; } }
            { Vector2 bc = c - b; float bcL = Vector2.Dot(bc, bc);
              float t = Mathf.Clamp01(Vector2.Dot(p - b, bc) / Mathf.Max(bcL, 1e-12f));
              float d = (p - (b + bc * t)).sqrMagnitude;
              if (d < best) { best = d; u = 0f; v = 1f - t; w = t; } }
            return best;
        }

        static float SignedArea2D(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
        }

        static float ComputeSignedArea(int[] tris, Vector2[] uvs, List<int> faces)
        {
            double area = 0;
            foreach (int f in faces)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                var a = uvs[i0]; var b = uvs[i1]; var c = uvs[i2];
                area += (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            }
            return (float)(area * 0.5);
        }

        // Legacy overload
        public static TransferResult Transfer(Mesh targetMesh, SourceShellInfo[] sourceInfos)
        {
            return new TransferResult { uv2 = new Vector2[targetMesh.vertexCount], verticesTotal = targetMesh.vertexCount };
        }
    }
}
