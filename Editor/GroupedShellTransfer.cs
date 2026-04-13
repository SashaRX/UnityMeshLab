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

        /// <summary>
        /// Per-shell quality classification assigned after transfer.
        /// </summary>
        public enum ShellStatus
        {
            /// <summary>Transfer clean — 0 issues.</summary>
            Accepted = 0,
            /// <summary>Minor issues (&le;30% faces) — usable but imperfect.</summary>
            Degraded = 1,
            /// <summary>Severe issues (&gt;30% faces) — written but likely broken.</summary>
            Poor = 2,
            /// <summary>Rejected (&gt;50% face issues for merged) — UV2 not written.</summary>
            Rejected = 3,
            /// <summary>No source shell matched.</summary>
            Unmatched = 4
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
            public int fragmentsMerged;      // target shell fragments merged pre-matching
            public ShellStatus[] targetShellStatus; // per-shell quality classification
            public int[] targetShellIssues;  // per-shell issue count (inverted/degenerate faces)
            public int shellsRejected;       // shells where UV2 was not written (too many issues)

            // ─── Cross-LOD overlap hints ───
            // Populated for merged shells to propagate source selection to subsequent LODs.
            public List<OverlapSourceHint> overlapHints;
        }

        /// <summary>
        /// Records which source shell was chosen for a merged target at a given 3D position.
        /// Used to propagate consistent source selection across LOD levels.
        /// </summary>
        public struct OverlapSourceHint
        {
            public Vector3 centroid3D;
            public int sourceShellIndex;
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

        const int kMaxSampleVerts = 32;

        static void FindBestSourceShell(
            UvShell tShell,
            Vector3[] tVerts,
            List<UvShell> srcShells,
            Vector3[] srcCentroid3D,
            Vector3[] triPosA, Vector3[] triPosB, Vector3[] triPosC,
            TriangleBvh[] shellBvh3D, int[][] shellBvh3DFaceMap,
            Vector3 tCentroid,
            int maxRetries, float goodDistSq,
            HashSet<int> excludeSources,
            out int chosenSrc, out float chosenDistSq, out float chosenAvg3D,
            Vector3 tgtNormal = default, Vector3[] srcAvgNormal = null,
            float meshDiag = 0f)
        {
            chosenSrc = -1;
            chosenDistSq = float.MaxValue;
            chosenAvg3D = float.MaxValue;

            bool useNormal = srcAvgNormal != null && tgtNormal.sqrMagnitude > 0.5f;

            // Rank source shells by 3D centroid distance
            var ranked = new List<(int si, float distSq)>();
            for (int si = 0; si < srcShells.Count; si++)
            {
                if (excludeSources != null && excludeSources.Contains(si)) continue;
                float d = (tCentroid - srcCentroid3D[si]).sqrMagnitude;
                ranked.Add((si, d));
            }
            ranked.Sort((a, b) => a.distSq.CompareTo(b.distSq));

            // Subsample target vertices for large shells
            var vertList = new List<int>(tShell.vertexIndices);
            int step = Mathf.Max(1, vertList.Count / kMaxSampleVerts);

            float bestScore = float.MaxValue;
            float bestNormalDot = useNormal ? -2f : 1f; // when not using normals, assume good
            const float kMinDotForEarlyExit = 0.3f;
            int tries = Mathf.Min(maxRetries, ranked.Count);
            for (int attempt = 0; attempt < tries; attempt++)
            {
                int si = ranked[attempt].si;
                var srcFaces = srcShells[si].faceIndices;
                bool hasBvh = shellBvh3D != null && si < shellBvh3D.Length && shellBvh3D[si] != null;

                float totalDistSq = 0; int sampled = 0;
                for (int vi_idx = 0; vi_idx < vertList.Count; vi_idx += step)
                {
                    int vi = vertList[vi_idx];
                    if (vi >= tVerts.Length) continue;
                    Vector3 tPos = tVerts[vi];

                    float bestDSq;
                    if (hasBvh)
                    {
                        var hit = shellBvh3D[si].FindNearest(tPos);
                        bestDSq = hit.distSq;
                    }
                    else
                    {
                        bestDSq = float.MaxValue;
                        for (int fi = 0; fi < srcFaces.Count; fi++)
                        {
                            int f = srcFaces[fi];
                            float dSq = PointToTri3D(tPos, triPosA[f], triPosB[f], triPosC[f],
                                out _, out _, out _);
                            if (dSq < bestDSq) bestDSq = dSq;
                        }
                    }
                    totalDistSq += bestDSq;
                    sampled++;
                }

                float avgDist = sampled > 0 ? totalDistSq / sampled : float.MaxValue;

                // Composite score: surface distance × normal factor.
                // Multiplicative factor disambiguates equidistant surfaces
                // (thin belts/straps) without overwhelming clearly-closer matches
                // (small detail shells on kiosks etc.).
                float score = avgDist;
                float candidateDot = 1f;
                if (useNormal && si < srcAvgNormal.Length)
                {
                    candidateDot = Vector3.Dot(tgtNormal, srcAvgNormal[si]);
                    // Factor: 1.0 when aligned (dot=1), 3.0 when opposite (dot=-1).
                    score *= 1f + (1f - candidateDot);
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestNormalDot = candidateDot;
                    chosenSrc = si;
                    chosenDistSq = ranked[attempt].distSq;
                    chosenAvg3D = avgDist;
                }
                // Early exit requires BOTH good distance AND good normal alignment.
                // With multiplicative penalty, even wrong-side shells have low scores
                // when distance is small (thin belts), so we must also check that the
                // best candidate has reasonable normal agreement before exiting.
                if (bestScore < goodDistSq && bestNormalDot >= kMinDotForEarlyExit) break;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  DetectMergedShell — check if a target shell's UV0 coverage
        //  is too poor for the matched source shell (>30% verts bad).
        // ═══════════════════════════════════════════════════════════

        static bool DetectMergedShell(
            UvShell tShell, Vector2[] tUv0,
            List<int> srcFaces,
            Vector2[] triUv0A, Vector2[] triUv0B, Vector2[] triUv0C,
            TriangleBvh2D shellBvh, float uv0BadThreshold)
        {
            int uv0BadCount = 0;
            foreach (int vi in tShell.vertexIndices)
            {
                if (vi >= tUv0.Length) continue;
                Vector2 tUv = tUv0[vi];

                float bestDSq;
                if (shellBvh != null)
                {
                    var hit = shellBvh.FindNearest(tUv);
                    bestDSq = hit.faceIndex >= 0 ? hit.distSq : float.MaxValue;
                }
                else
                {
                    bestDSq = float.MaxValue;
                    for (int fi = 0; fi < srcFaces.Count; fi++)
                    {
                        int f = srcFaces[fi];
                        float dSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                            out _, out _, out _);
                        if (dSq < bestDSq) bestDSq = dSq;
                        if (bestDSq < 1e-8f) break;
                    }
                }
                if (bestDSq > uv0BadThreshold) uv0BadCount++;
            }
            int sv = tShell.vertexIndices.Count;
            return sv > 0 && (float)uv0BadCount / sv > 0.3f;
        }

        // ═══════════════════════════════════════════════════════════
        //  UV0 coverage fraction — same logic as DetectMergedShell
        //  but returns a continuous [0,1] value instead of bool.
        // ═══════════════════════════════════════════════════════════

        static float ComputeUv0CoverageFraction(
            UvShell tShell, Vector2[] tUv0,
            List<int> srcFaces,
            Vector2[] triUv0A, Vector2[] triUv0B, Vector2[] triUv0C,
            TriangleBvh2D shellBvh, float uv0BadThreshold)
        {
            int goodCount = 0;
            int total = 0;
            foreach (int vi in tShell.vertexIndices)
            {
                if (vi >= tUv0.Length) continue;
                total++;
                Vector2 tUv = tUv0[vi];

                float bestDSq;
                if (shellBvh != null)
                {
                    var hit = shellBvh.FindNearest(tUv);
                    bestDSq = hit.faceIndex >= 0 ? hit.distSq : float.MaxValue;
                }
                else
                {
                    bestDSq = float.MaxValue;
                    for (int fi = 0; fi < srcFaces.Count; fi++)
                    {
                        int f = srcFaces[fi];
                        float dSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                            out _, out _, out _);
                        if (dSq < bestDSq) bestDSq = dSq;
                        if (bestDSq < 1e-8f) break;
                    }
                }
                if (bestDSq <= uv0BadThreshold) goodCount++;
            }
            return total > 0 ? (float)goodCount / total : 0f;
        }

        // ═══════════════════════════════════════════════════════════
        //  RescoreMergedShells — for shells marked as merged, try all
        //  source shells with a multi-criteria score and un-merge if
        //  a sufficiently good source is found.
        // ═══════════════════════════════════════════════════════════

        static void RescoreMergedShells(
            List<UvShell> tgtShells,
            List<UvShell> srcShells,
            Vector3[] tVerts,
            Vector2[] tUv0,
            int[] tgtTris,
            Vector2[] triUv0A, Vector2[] triUv0B, Vector2[] triUv0C,
            TriangleBvh2D[] shellUv0Bvh,
            Vector3[] srcCentroid3D,
            Vector3[] srcAvgNormal,
            float[] srcUv0Area,
            Vector3[] tgtAvgNormal,
            float[] tgtUv0Area,
            float meshDiagonal,
            float uv0BadThreshold,
            bool[] tgtIsMerged,
            int[] targetShellToSourceShell,
            float[] targetShellMatchDistSqr,
            float[] tgtChosenAvg3D,
            Vector3[] targetShellCentroids,
            bool[] tgtIsFragmentMerged = null)
        {
            const float wArea     = 0.20f;
            const float wNormal   = 0.30f;
            const float wDist     = 0.15f;
            const float wCoverage = 0.35f;
            const float kCoverageAcceptThreshold = 0.70f;

            // Normalize distance by a fraction of the diagonal so that
            // nearby vs. far sources produce meaningfully different scores.
            // Using full diagSq makes all distances look nearly identical.
            float distNormSq = meshDiagonal * meshDiagonal * 0.04f; // (20% of diagonal)²
            if (distNormSq < 1e-12f) distNormSq = 1f;

            int rescued = 0;

            for (int tsi = 0; tsi < tgtShells.Count; tsi++)
            {
                if (!tgtIsMerged[tsi]) continue;

                // Skip fragment-merged shells: they combine tiling copies with
                // identical UV0, so any single source would produce duplicate UV2.
                // Keep them in merged mode so Phase 3 per-face voting assigns
                // each fragment to a different source → unique UV2 regions.
                if (tgtIsFragmentMerged != null && tgtIsFragmentMerged[tsi])
                {
                    UvtLog.Info($"[GroupedTransfer] Rescore: t{tsi} kept merged " +
                        "(fragment-merged, per-face voting needed)");
                    continue;
                }

                var tShell = tgtShells[tsi];
                Vector3 tCentroid = targetShellCentroids[tsi];
                Vector3 tNrm = tgtAvgNormal[tsi];
                float tArea = tgtUv0Area[tsi];

                int bestSrc = -1;
                float bestScore = -1f;
                float bestCoverage = 0f;
                float bestDistSq = float.MaxValue;

                for (int si = 0; si < srcShells.Count; si++)
                {
                    // 1. UV0 area ratio: min/max, 1.0 = same footprint
                    float sArea = srcUv0Area[si];
                    float areaScore = (tArea > 1e-10f && sArea > 1e-10f)
                        ? Mathf.Min(tArea, sArea) / Mathf.Max(tArea, sArea) : 0f;

                    // 2. Normal agreement: remap dot [-1,1] → [0,1]
                    float normalScore;
                    if (tNrm.sqrMagnitude > 0.5f && srcAvgNormal[si].sqrMagnitude > 0.5f)
                        normalScore = (Vector3.Dot(tNrm, srcAvgNormal[si]) + 1f) * 0.5f;
                    else
                        normalScore = 0.5f;

                    // 3. 3D centroid distance (normalized by mesh diagonal)
                    float centDistSq = (tCentroid - srcCentroid3D[si]).sqrMagnitude;
                    float distScore = 1f - Mathf.Clamp01(centDistSq / distNormSq);

                    // 4. UV0 coverage fraction
                    float coverage = ComputeUv0CoverageFraction(
                        tShell, tUv0,
                        srcShells[si].faceIndices,
                        triUv0A, triUv0B, triUv0C,
                        shellUv0Bvh[si], uv0BadThreshold);

                    float score = wArea * areaScore
                                + wNormal * normalScore
                                + wDist * distScore
                                + wCoverage * coverage;

                    // When multiple sources have full coverage (>=threshold),
                    // prefer the closest in 3D to avoid scattering UV2 shells
                    // to distant parts of the atlas on lower LODs.
                    bool wins = false;
                    if (score > bestScore)
                    {
                        wins = true;
                    }
                    else if (Mathf.Abs(score - bestScore) < 0.01f
                        && coverage >= kCoverageAcceptThreshold
                        && bestCoverage >= kCoverageAcceptThreshold
                        && centDistSq < bestDistSq * 0.5f)
                    {
                        // Tie-break: both have good coverage but this one is
                        // significantly closer (< half the distance squared).
                        wins = true;
                    }

                    if (wins)
                    {
                        bestScore = score;
                        bestSrc = si;
                        bestCoverage = coverage;
                        bestDistSq = centDistSq;
                    }
                }

                if (bestSrc >= 0 && bestCoverage >= kCoverageAcceptThreshold)
                {
                    int oldSrc = targetShellToSourceShell[tsi];
                    tgtIsMerged[tsi] = false;
                    targetShellToSourceShell[tsi] = bestSrc;
                    targetShellMatchDistSqr[tsi] = bestDistSq;
                    tgtChosenAvg3D[tsi] = bestDistSq;
                    rescued++;

                    UvtLog.Info($"[GroupedTransfer] Rescore: t{tsi} rescued from merged " +
                        $"(src{oldSrc}→src{bestSrc}, score={bestScore:F3}, " +
                        $"coverage={bestCoverage:F3})");
                }
            }

            if (rescued > 0)
                UvtLog.Info($"[GroupedTransfer] Rescore: {rescued} shells rescued from merged");
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

        public static TransferResult Transfer(Mesh targetMesh, Mesh sourceMesh,
            List<OverlapSourceHint> previousLodHints = null)
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

            // ── Phase 1a: Merge fragment shells ──
            // LOD simplification can split a source UV0 shell into fragments.
            // Detect target shells whose UV0 bbox is fully contained within the
            // same source shell and merge them into virtual shells to prevent
            // same-source conflicts in Phase 2b dedup.
            int fragMergeCount;
            bool[] tgtIsFragmentMerged;
            int[] tgtFragmentMergeSource;
            tgtShells = MergeFragmentShells(
                tgtShells, tUv0, tgtTris, tVerts,
                srcShells, srcUv0, srcTris, srcVerts,
                out fragMergeCount, out tgtIsFragmentMerged,
                out tgtFragmentMergeSource);
            if (fragMergeCount > 0)
                result.fragmentsMerged = fragMergeCount;

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

            // ── Spatial partitioning for overlapping UV0 shells ──
            var srcPartitions = SpatialPartitioner.PartitionShells(
                srcShells, srcUv0, srcTris, srcVerts);

            // Pre-detect ribbon shells in source (for Approach C)
            var srcIsRibbon = new bool[srcShells.Count];
            var srcRibbonAxis = new Vector3[srcShells.Count];
            var srcRibbonAxis2 = new Vector3[srcShells.Count];
            var srcRibbonCentroid = new Vector3[srcShells.Count];
            for (int si = 0; si < srcShells.Count; si++)
            {
                if (srcPartitions[si].hasOverlap)
                {
                    srcIsRibbon[si] = StripParameterization.IsRibbon(
                        srcShells[si], srcVerts, srcTris,
                        out srcRibbonAxis[si], out srcRibbonAxis2[si], out srcRibbonCentroid[si]);
                }
            }

            // Pre-build per-partition BVHs for source shells with overlap
            // partitionBvh[si][pi] = BVH containing only faces of partition pi
            var partitionBvh = new TriangleBvh2D[srcShells.Count][];
            for (int si = 0; si < srcShells.Count; si++)
            {
                var pr = srcPartitions[si];
                if (pr.hasOverlap && pr.partitionCount > 1)
                {
                    partitionBvh[si] = new TriangleBvh2D[pr.partitionCount];
                    for (int pi = 0; pi < pr.partitionCount; pi++)
                    {
                        var faces = SpatialPartitioner.GetPartitionFaces(srcShells[si], pr, pi);
                        if (faces.Length > 0)
                            partitionBvh[si][pi] = new TriangleBvh2D(triUv0A, triUv0B, triUv0C, faces);
                    }
                }
            }

            // Per-partition similarity transforms for overlapping shells
            // Each partition has a unique UV0→UV2 mapping, so we can compute
            // a clean transform that preserves aspect ratios (unlike per-vertex interp).
            var partitionXform = new SimilarityTransform[srcShells.Count][];
            for (int si = 0; si < srcShells.Count; si++)
            {
                var pr = srcPartitions[si];
                if (pr.hasOverlap && pr.partitionCount > 1)
                {
                    partitionXform[si] = new SimilarityTransform[pr.partitionCount];
                    for (int pi = 0; pi < pr.partitionCount; pi++)
                    {
                        var partFaces = SpatialPartitioner.GetPartitionFaces(srcShells[si], pr, pi);
                        if (partFaces.Length == 0) continue;
                        var partVertSet = new HashSet<int>();
                        foreach (int f in partFaces)
                        {
                            partVertSet.Add(srcTris[f * 3]);
                            partVertSet.Add(srcTris[f * 3 + 1]);
                            partVertSet.Add(srcTris[f * 3 + 2]);
                        }
                        var partVertArr = new int[partVertSet.Count];
                        partVertSet.CopyTo(partVertArr);

                        var partFaceList = new List<int>(partFaces);
                        float saUv0p = ComputeSignedArea(srcTris, srcUv0, partFaceList);
                        float saUv2p = ComputeSignedArea(srcTris, srcUv2, partFaceList);
                        bool mirroredP = saUv0p * saUv2p < 0f;

                        partitionXform[si][pi] = ComputeSimilarityTransform(
                            srcUv0, srcUv2, partVertArr, mirroredP);
                    }
                }
            }

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

            // Compute UV2 bounding box for each source shell (for xform OOB detection)
            var srcUv2Min = new Vector2[srcShells.Count];
            var srcUv2Max = new Vector2[srcShells.Count];
            for (int si = 0; si < srcShells.Count; si++)
            {
                Vector2 bMin2 = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 bMax2 = new Vector2(float.MinValue, float.MinValue);
                foreach (int vi in srcShells[si].vertexIndices)
                {
                    if (vi < srcUv2.Length)
                    {
                        bMin2 = Vector2.Min(bMin2, srcUv2[vi]);
                        bMax2 = Vector2.Max(bMax2, srcUv2[vi]);
                    }
                }
                srcUv2Min[si] = bMin2;
                srcUv2Max[si] = bMax2;
            }

            // Precompute per-source-shell average face normal + total UV0 area
            var srcAvgNormal = new Vector3[srcShells.Count];
            var srcUv0Area = new float[srcShells.Count];
            for (int si = 0; si < srcShells.Count; si++)
            {
                Vector3 nSum = Vector3.zero;
                double areaSum = 0;
                foreach (int f in srcShells[si].faceIndices)
                {
                    nSum += triNormal[f];
                    float cross = (triUv0B[f].x - triUv0A[f].x) * (triUv0C[f].y - triUv0A[f].y)
                                - (triUv0C[f].x - triUv0A[f].x) * (triUv0B[f].y - triUv0A[f].y);
                    areaSum += Mathf.Abs(cross) * 0.5f;
                }
                srcAvgNormal[si] = nSum.sqrMagnitude > 1e-8f ? nSum.normalized : Vector3.up;
                srcUv0Area[si] = (float)areaSum;
            }

            // ── Mesh-scale metrics for adaptive thresholds ──
            Vector3 srcBoundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 srcBoundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < srcVerts.Length; i++)
            {
                srcBoundsMin = Vector3.Min(srcBoundsMin, srcVerts[i]);
                srcBoundsMax = Vector3.Max(srcBoundsMax, srcVerts[i]);
            }
            float meshDiagonal = (srcBoundsMax - srcBoundsMin).magnitude;

            // Average UV0 triangle edge length
            double totalEdgeLen = 0; int edgeCount = 0;
            for (int f = 0; f < srcTriCount; f++)
            {
                totalEdgeLen += (triUv0B[f] - triUv0A[f]).magnitude;
                totalEdgeLen += (triUv0C[f] - triUv0B[f]).magnitude;
                totalEdgeLen += (triUv0A[f] - triUv0C[f]).magnitude;
                edgeCount += 3;
            }
            float avgUv0Edge = edgeCount > 0 ? (float)(totalEdgeLen / edgeCount) : 0.01f;

            // ── Build BVH structures for accelerated lookups ──
            const int kMinFacesForShellBvh = 16;
            const float kNormalDotMin = 0.0f; // reject backfaces in UV0 lookups

            // Global UV0 2D BVH (for merged all-source transfer)
            var globalUv0Bvh = new TriangleBvh2D(triUv0A, triUv0B, triUv0C);

            // Per-source-shell UV0 2D BVH
            var shellUv0Bvh = new TriangleBvh2D[srcShells.Count];
            for (int si = 0; si < srcShells.Count; si++)
                if (srcShells[si].faceIndices.Count > kMinFacesForShellBvh)
                    shellUv0Bvh[si] = new TriangleBvh2D(triUv0A, triUv0B, triUv0C,
                                                          srcShells[si].faceIndices.ToArray());

            // Per-source-shell 3D BVH (for FindBestSourceShell)
            var shellBvh3D = new TriangleBvh[srcShells.Count];
            var shellBvh3DFaceMap = new int[srcShells.Count][];
            for (int si = 0; si < srcShells.Count; si++)
            {
                var faces = srcShells[si].faceIndices;
                if (faces.Count > kMinFacesForShellBvh || srcPartitions[si].hasOverlap)
                {
                    shellBvh3DFaceMap[si] = faces.ToArray();
                    int fn = faces.Count;
                    var sv = new Vector3[fn * 3];
                    var st = new int[fn * 3];
                    for (int i = 0; i < fn; i++)
                    {
                        int gf = faces[i];
                        sv[i * 3]     = triPosA[gf];
                        sv[i * 3 + 1] = triPosB[gf];
                        sv[i * 3 + 2] = triPosC[gf];
                        st[i * 3]     = i * 3;
                        st[i * 3 + 1] = i * 3 + 1;
                        st[i * 3 + 2] = i * 3 + 2;
                    }
                    shellBvh3D[si] = new TriangleBvh(sv, st);
                }
            }

            // Per-shell local face normals for 3D BVH normal-filtered queries
            // Only built for shells with UV0 overlap (used by direct 3D projection path)
            var shellBvh3DFaceNormals = new Vector3[srcShells.Count][];
            for (int si = 0; si < srcShells.Count; si++)
            {
                if (shellBvh3D[si] != null && srcPartitions[si].hasOverlap)
                {
                    var faces = srcShells[si].faceIndices;
                    var localNormals = new Vector3[faces.Count];
                    for (int i = 0; i < faces.Count; i++)
                        localNormals[i] = triNormal[faces[i]];
                    shellBvh3DFaceNormals[si] = localNormals;
                }
            }

            float kRayMaxDist = Mathf.Max(meshDiagonal * 0.1f, 0.01f);

            // Adaptive thresholds
            float kGoodDistSq = meshDiagonal > 0f
                ? 0.001f * meshDiagonal * meshDiagonal
                : 0.001f;
            float kUv0BadThreshold = Mathf.Max(avgUv0Edge * avgUv0Edge, 0.001f);

            // Adaptive kMaxRetries based on overlap group size
            var overlapGroups = UvShellExtractor.FindOverlapGroups(srcShells);
            int maxOverlapGroupSize = 0;
            foreach (var group in overlapGroups)
                maxOverlapGroupSize = Mathf.Max(maxOverlapGroupSize, group.Count);
            int kMaxRetries = Mathf.Clamp(maxOverlapGroupSize + 2, 5, srcShells.Count);

            // Build overlap group membership: srcShell → list of all group members
            var srcShellOverlapMembers = new List<int>[srcShells.Count];
            foreach (var group in overlapGroups)
                foreach (int si in group)
                    srcShellOverlapMembers[si] = group;

            UvtLog.Verbose($"[GroupedTransfer] Adaptive: meshDiag={meshDiagonal:F4}, " +
                $"avgUv0Edge={avgUv0Edge:F6}, goodDistSq={kGoodDistSq:F6}, " +
                $"uv0BadThresh={kUv0BadThreshold:F6}, maxRetries={kMaxRetries}, " +
                $"overlapGroups={overlapGroups.Count}(maxSize={maxOverlapGroupSize})");

            // ── Phase 2a: Match each target shell → best source shell ──
            result.targetShellToSourceShell = new int[tgtShells.Count];
            result.targetShellMethod = new int[tgtShells.Count]; // 0=interp, 1=xform, 2=merged
            result.targetShellCentroids = new Vector3[tgtShells.Count];
            result.targetShellMatchDistSqr = new float[tgtShells.Count];
            result.targetShellStatus = new ShellStatus[tgtShells.Count];
            result.targetShellIssues = new int[tgtShells.Count];

            var tgtChosenAvg3D = new float[tgtShells.Count];
            var tgtIsMerged = new bool[tgtShells.Count];
            var tgtForce3DFallback = new bool[tgtShells.Count];

            // Precompute per-target-shell average face normal + total UV0 area
            var tgtAvgNormal = new Vector3[tgtShells.Count];
            var tgtUv0Area = new float[tgtShells.Count];
            for (int tsi = 0; tsi < tgtShells.Count; tsi++)
            {
                Vector3 nSum = Vector3.zero;
                double areaSum = 0;
                foreach (int f in tgtShells[tsi].faceIndices)
                {
                    int i0 = tgtTris[f * 3], i1 = tgtTris[f * 3 + 1], i2 = tgtTris[f * 3 + 2];
                    if (i0 >= tVerts.Length || i1 >= tVerts.Length || i2 >= tVerts.Length) continue;
                    nSum += Vector3.Cross(tVerts[i1] - tVerts[i0], tVerts[i2] - tVerts[i0]).normalized;
                    if (i0 < tUv0.Length && i1 < tUv0.Length && i2 < tUv0.Length)
                    {
                        float cross = (tUv0[i1].x - tUv0[i0].x) * (tUv0[i2].y - tUv0[i0].y)
                                    - (tUv0[i2].x - tUv0[i0].x) * (tUv0[i1].y - tUv0[i0].y);
                        areaSum += Mathf.Abs(cross) * 0.5f;
                    }
                }
                tgtAvgNormal[tsi] = nSum.sqrMagnitude > 1e-8f ? nSum.normalized : Vector3.up;
                tgtUv0Area[tsi] = (float)areaSum;
            }

            for (int i = 0; i < tgtShells.Count; i++)
            {
                result.targetShellToSourceShell[i] = -1;
                result.targetShellMethod[i] = -1;
                result.targetShellMatchDistSqr[i] = float.MaxValue;
                tgtChosenAvg3D[i] = float.MaxValue;
            }

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

                // Fragment-merged shells: force to their merge source instead of
                // searching by 3D centroid. The merge was identified by UV0 bbox
                // containment — using a different source breaks the merge logic.
                int chosenSrc;
                float chosenDistSq;
                float chosenAvg3D;
                if (tgtIsFragmentMerged != null && tgtIsFragmentMerged[tsi]
                    && tgtFragmentMergeSource != null && tgtFragmentMergeSource[tsi] >= 0)
                {
                    chosenSrc = tgtFragmentMergeSource[tsi];
                    chosenDistSq = (tCentroid - srcCentroid3D[chosenSrc]).sqrMagnitude;
                    chosenAvg3D = chosenDistSq; // approximate
                    UvtLog.Info($"[GroupedTransfer] Phase2a: t{tsi} forced to merge source src{chosenSrc}");
                }
                else
                {
                    // Find best source shell (with BVH acceleration + subsampling)
                    FindBestSourceShell(tShell, tVerts, srcShells, srcCentroid3D,
                        triPosA, triPosB, triPosC,
                        shellBvh3D, shellBvh3DFaceMap,
                        tCentroid, kMaxRetries, kGoodDistSq, null,
                        out chosenSrc, out chosenDistSq, out chosenAvg3D,
                        tgtAvgNormal[tsi], srcAvgNormal, meshDiagonal);
                }

                if (chosenSrc < 0) continue;

                result.targetShellToSourceShell[tsi] = chosenSrc;
                result.targetShellMatchDistSqr[tsi] = chosenDistSq;
                tgtChosenAvg3D[tsi] = chosenAvg3D;

                // Fragment-merged shells must stay merged: they combine tiling
                // copies with identical UV0, so single-source interp would produce
                // duplicate UV2 for each fragment part.
                if (tgtIsFragmentMerged != null && tgtIsFragmentMerged[tsi])
                {
                    tgtIsMerged[tsi] = true;
                }
                else
                {
                    // Detect merged shell via UV0 coverage (BVH + adaptive threshold)
                    tgtIsMerged[tsi] = DetectMergedShell(tShell, tUv0,
                        srcShells[chosenSrc].faceIndices, triUv0A, triUv0B, triUv0C,
                        shellUv0Bvh[chosenSrc], kUv0BadThreshold);
                }
            }

            // ── Phase 2a-cov: 3D coverage check (diagnostic only) ──
            // Coverage upgrade disabled: upgrading shells to merged changes their
            // transfer mode from interp to 3D voting, which produces different UV2
            // positions and causes visible UV2 jumps between LODs.
            // Keeping the computation for diagnostics only.
            {
                var srcNormals = sourceMesh.normals;
                float coverageMaxDist = Mathf.Max(meshDiagonal * 0.05f, 0.01f);
                float coverageMinDot = 0.5f; // ~60°

                var cov3D = CoverageSplitSolver.ComputeShellCoverage3D(
                    tgtShells, tVerts, tNormals, tgtTris,
                    srcShells, srcVerts, srcNormals, srcTris,
                    result.targetShellToSourceShell,
                    coverageMaxDist, coverageMinDot);

                // Log poor coverage shells for diagnostics, but do NOT upgrade to merged.
                for (int tsi = 0; tsi < cov3D.Length; tsi++)
                {
                    if (!tgtIsMerged[tsi] && cov3D[tsi] < 0.7f && cov3D[tsi] > 0f)
                        UvtLog.Verbose($"[CoverageSplit] Shell t{tsi}: 3D coverage " +
                            $"{cov3D[tsi]:P0} < 70% (not upgrading to merged)");
                }
            }

            // ── Phase 2a+: Rescore merged shells with multi-criteria matching ──
            RescoreMergedShells(
                tgtShells, srcShells,
                tVerts, tUv0, tgtTris,
                triUv0A, triUv0B, triUv0C,
                shellUv0Bvh, srcCentroid3D,
                srcAvgNormal, srcUv0Area,
                tgtAvgNormal, tgtUv0Area,
                meshDiagonal, kUv0BadThreshold,
                tgtIsMerged,
                result.targetShellToSourceShell,
                result.targetShellMatchDistSqr,
                tgtChosenAvg3D,
                result.targetShellCentroids,
                tgtIsFragmentMerged);

            // ── Phase 2b: Deduplicate — resolve same-source conflicts ──
            // When multiple non-merged target shells claim the same source shell
            // (common with overlapping/tiling UV0), keep the best match and
            // reassign others to different source shells at the same 3D location.
            // Hoisted: used by Phase 3 overlap guard for merged shells
            var claimed = new HashSet<int>();
            {
                // Pre-claim sources used by fragment-merged shells — these are
                // locked to their merge source and must not be taken by others.
                for (int tsi = 0; tsi < tgtShells.Count; tsi++)
                {
                    if (tgtIsFragmentMerged != null && tsi < tgtIsFragmentMerged.Length
                        && tgtIsFragmentMerged[tsi])
                    {
                        int src = result.targetShellToSourceShell[tsi];
                        if (src >= 0) claimed.Add(src);
                    }
                }

                // Build reverse map: source → list of non-merged target claimants.
                // Merged shells are excluded from dedup — they use 3D voting and
                // don't need a specific source. Including them would cause merged
                // shells to claim sources and evict non-merged shells, changing
                // their source assignments and causing UV2 position jumps between LODs.
                var srcClaimants = new Dictionary<int, List<(int tsi, float avg3D)>>();
                for (int tsi = 0; tsi < tgtShells.Count; tsi++)
                {
                    int src = result.targetShellToSourceShell[tsi];
                    if (src < 0 || tgtIsMerged[tsi]) continue; // skip unmatched & merged
                    // Fragment-merged shells must keep their original source —
                    // they were identified by UV0 bbox containment in that specific
                    // source. Reassigning breaks the merge and produces garbage UV2.
                    if (tgtIsFragmentMerged != null && tsi < tgtIsFragmentMerged.Length
                        && tgtIsFragmentMerged[tsi]) continue;
                    if (!srcClaimants.TryGetValue(src, out var list))
                    {
                        list = new List<(int, float)>();
                        srcClaimants[src] = list;
                    }
                    list.Add((tsi, tgtChosenAvg3D[tsi]));
                }

                // Identify conflicts and build claimed set
                var needsRematch = new List<int>();
                int dedupConflicts = 0;

                // Sort keys for deterministic iteration order
                var sortedSrcKeys = new List<int>(srcClaimants.Keys);
                sortedSrcKeys.Sort();
                foreach (int srcKey in sortedSrcKeys)
                {
                    var claimants = srcClaimants[srcKey];
                    bool preClaimed = claimed.Contains(srcKey); // pre-claimed by fragment-merged
                    claimed.Add(srcKey); // source is claimed regardless

                    if (preClaimed)
                    {
                        // Source already locked by fragment-merged shell — evict ALL
                        // non-merged claimants (they must find a different source)
                        foreach (var (tsi, _) in claimants)
                        {
                            needsRematch.Add(tsi);
                            dedupConflicts++;
                        }
                        continue;
                    }

                    if (claimants.Count <= 1) continue;

                    // Multiple targets claim same source. Check if they are
                    // non-overlapping UV0 fragments (LOD polygon deletion splits
                    // a shell into pieces that cover different UV0 sub-regions).
                    // If their UV0 bboxes don't overlap, each can independently
                    // use standard UV0→UV2 interpolation from the same source —
                    // no conflict, no eviction needed.
                    bool hasUv0Overlap = false;
                    for (int i = 0; i < claimants.Count && !hasUv0Overlap; i++)
                    {
                        var shellA = tgtShells[claimants[i].tsi];
                        for (int j = i + 1; j < claimants.Count && !hasUv0Overlap; j++)
                        {
                            var shellB = tgtShells[claimants[j].tsi];
                            if (shellA.boundsMin.x < shellB.boundsMax.x &&
                                shellA.boundsMax.x > shellB.boundsMin.x &&
                                shellA.boundsMin.y < shellB.boundsMax.y &&
                                shellA.boundsMax.y > shellB.boundsMin.y)
                            {
                                hasUv0Overlap = true;
                            }
                        }
                    }

                    if (!hasUv0Overlap)
                    {
                        // Non-overlapping UV0 fragments sharing a source — allow all
                        // to stay. Each fragment covers a distinct UV0 sub-region,
                        // so standard interpolation produces correct UV2 per fragment.
                        UvtLog.Info($"[GroupedTransfer] Dedup: src{srcKey} has " +
                            $"{claimants.Count} non-overlapping UV0 fragments — allowing shared source");
                        continue;
                    }

                    // Truly overlapping UV0 (tiling/symmetric) — evict as before.
                    // Non-merged shells get priority (they need the specific UV0→UV2
                    // mapping); merged shells use 3D voting and work with any source.
                    claimants.Sort((a, b) =>
                    {
                        bool aM = tgtIsMerged[a.tsi];
                        bool bM = tgtIsMerged[b.tsi];
                        if (aM != bM) return aM ? 1 : -1; // non-merged first
                        return a.avg3D.CompareTo(b.avg3D);
                    });
                    for (int i = 1; i < claimants.Count; i++)
                    {
                        needsRematch.Add(claimants[i].tsi);
                        dedupConflicts++;
                    }
                }

                // Re-match evicted targets with exclusion of already-claimed sources
                if (needsRematch.Count > 0)
                {
                    // Adaptive iteration count: log2(conflicts), min 3, max 20
                    int dedupMaxIter = Mathf.Clamp(
                        Mathf.CeilToInt(Mathf.Log(needsRematch.Count + 1, 2)), 3, 20);
                    for (int iteration = 0; iteration < dedupMaxIter && needsRematch.Count > 0; iteration++)
                    {
                        var stillNeedsRematch = new List<int>();

                        foreach (int tsi in needsRematch)
                        {
                            var tShell = tgtShells[tsi];
                            int oldSrc = result.targetShellToSourceShell[tsi];
                            float oldDistSq = result.targetShellMatchDistSqr[tsi];
                            float oldAvg3D = tgtChosenAvg3D[tsi];

                            FindBestSourceShell(tShell, tVerts, srcShells, srcCentroid3D,
                                triPosA, triPosB, triPosC,
                                shellBvh3D, shellBvh3DFaceMap,
                                result.targetShellCentroids[tsi],
                                kMaxRetries * 3, kGoodDistSq, claimed,
                                out int newSrc, out float newDistSq, out float newAvg3D,
                                tgtAvgNormal[tsi], srcAvgNormal, meshDiagonal);

                            if (newSrc >= 0)
                            {
                                // Re-check merged status with new source (BVH + adaptive threshold)
                                bool newIsMerged = DetectMergedShell(tShell, tUv0,
                                    srcShells[newSrc].faceIndices, triUv0A, triUv0B, triUv0C,
                                    shellUv0Bvh[newSrc], kUv0BadThreshold);

                                bool wasMerged = tgtIsMerged[tsi];

                                // If reassignment would make a non-merged shell become merged,
                                // check if the new source is in the same overlap group:
                                // if so, accept the reassignment — each overlap group member
                                // has its own UV2 region, so merged transfer will work correctly.
                                // Only fall back to merged+3D for sources outside overlap groups.
                                bool newSrcInOverlapGroup = oldSrc >= 0
                                    && srcShellOverlapMembers[oldSrc] != null
                                    && srcShellOverlapMembers[newSrc] != null
                                    && srcShellOverlapMembers[oldSrc] == srcShellOverlapMembers[newSrc];

                                if (newIsMerged && !wasMerged && oldSrc >= 0 && !newSrcInOverlapGroup)
                                {
                                    // Use the new (unclaimed) source instead of the
                                    // conflicting old one — merged+3D works from any source.
                                    UvtLog.Info($"[GroupedTransfer] Dedup: t{tsi} → merged+3D " +
                                        $"(src{oldSrc}→src{newSrc}, forced merged)");
                                    result.targetShellToSourceShell[tsi] = newSrc;
                                    result.targetShellMatchDistSqr[tsi] = newDistSq;
                                    tgtChosenAvg3D[tsi] = newAvg3D;
                                    claimed.Add(newSrc);
                                    tgtIsMerged[tsi] = true;
                                    tgtForce3DFallback[tsi] = true;
                                }
                                else if (newIsMerged && !wasMerged && oldSrc >= 0 && newSrcInOverlapGroup)
                                {
                                    // Accept reassignment within overlap group — different
                                    // UV2 region, partition-aware transfer handles it.
                                    UvtLog.Info($"[GroupedTransfer] Dedup: t{tsi} " +
                                        $"reassigned src{oldSrc}→src{newSrc} " +
                                        $"(overlap group, accepted merged)");
                                    result.targetShellToSourceShell[tsi] = newSrc;
                                    result.targetShellMatchDistSqr[tsi] = newDistSq;
                                    tgtChosenAvg3D[tsi] = newAvg3D;
                                    claimed.Add(newSrc);
                                    tgtIsMerged[tsi] = true;
                                }
                                else
                                {
                                    bool isFragMergedLog = tgtIsFragmentMerged != null
                                        && tsi < tgtIsFragmentMerged.Length
                                        && tgtIsFragmentMerged[tsi];
                                    if (wasMerged)
                                        UvtLog.Info($"[GroupedTransfer] Dedup: merged t{tsi} " +
                                            $"reassigned src{oldSrc}→src{newSrc} " +
                                            $"(merged={isFragMergedLog || newIsMerged}" +
                                            (isFragMergedLog ? ", fragMerged" : "") + ")");
                                    result.targetShellToSourceShell[tsi] = newSrc;
                                    result.targetShellMatchDistSqr[tsi] = newDistSq;
                                    tgtChosenAvg3D[tsi] = newAvg3D;
                                    claimed.Add(newSrc);
                                    // Preserve merged status for fragment-merged shells:
                                    // they need per-face voting regardless of UV0 coverage
                                    bool isFragMerged = tgtIsFragmentMerged != null
                                        && tsi < tgtIsFragmentMerged.Length
                                        && tgtIsFragmentMerged[tsi];
                                    tgtIsMerged[tsi] = isFragMerged || newIsMerged;
                                }
                            }
                            else
                            {
                                stillNeedsRematch.Add(tsi);
                            }
                        }

                        needsRematch = stillNeedsRematch;
                    }

                    // Any remaining unmatched — try spreading across overlap group
                    // before falling back to forced merged on original (duplicate) source.
                    int spreadCount = 0;
                    if (needsRematch.Count > 0)
                    {
                        // Count how many targets currently use each source
                        var srcUsage = new Dictionary<int, int>();
                        for (int tsi2 = 0; tsi2 < tgtShells.Count; tsi2++)
                        {
                            int s = result.targetShellToSourceShell[tsi2];
                            if (s >= 0)
                            {
                                srcUsage.TryGetValue(s, out int cnt);
                                srcUsage[s] = cnt + 1;
                            }
                        }

                        var stillUnresolved = new List<int>();
                        foreach (int tsi in needsRematch)
                        {
                            int curSrc = result.targetShellToSourceShell[tsi];
                            if (curSrc < 0 || srcShellOverlapMembers[curSrc] == null)
                            {
                                stillUnresolved.Add(tsi);
                                continue;
                            }

                            // Find overlap group member with minimum usage
                            var group = srcShellOverlapMembers[curSrc];
                            int bestAlt = -1;
                            int bestUsage = int.MaxValue;
                            float bestDist = float.MaxValue;
                            Vector3 tCent = result.targetShellCentroids[tsi];
                            foreach (int gsi in group)
                            {
                                int usage = 0;
                                srcUsage.TryGetValue(gsi, out usage);
                                float dist = (tCent - srcCentroid3D[gsi]).sqrMagnitude;
                                // Prefer lower usage, then closer distance
                                if (usage < bestUsage || (usage == bestUsage && dist < bestDist))
                                {
                                    bestAlt = gsi;
                                    bestUsage = usage;
                                    bestDist = dist;
                                }
                            }

                            if (bestAlt >= 0 && bestAlt != curSrc)
                            {
                                UvtLog.Info($"[GroupedTransfer] Dedup spread: t{tsi} " +
                                    $"src{curSrc}→src{bestAlt} (usage {bestUsage}, overlap group)");
                                // Update usage counts
                                srcUsage.TryGetValue(curSrc, out int oldCnt);
                                srcUsage[curSrc] = Mathf.Max(0, oldCnt - 1);
                                srcUsage.TryGetValue(bestAlt, out int newCnt);
                                srcUsage[bestAlt] = newCnt + 1;

                                result.targetShellToSourceShell[tsi] = bestAlt;
                                result.targetShellMatchDistSqr[tsi] = bestDist;
                                claimed.Add(bestAlt);
                                tgtIsMerged[tsi] = true;
                                spreadCount++;
                            }
                            else
                            {
                                stillUnresolved.Add(tsi);
                            }
                        }
                        needsRematch = stillUnresolved;
                    }

                    // Any truly remaining unmatched — try to find an unclaimed
                    // source to avoid same-source duplicates, then force merged.
                    foreach (int tsi in needsRematch)
                    {
                        Vector3 tCent = result.targetShellCentroids[tsi];
                        int bestUnclaimed = -1;
                        float bestDist = float.MaxValue;
                        for (int s = 0; s < srcShells.Count; s++)
                        {
                            if (claimed.Contains(s)) continue;
                            float d = (tCent - srcCentroid3D[s]).sqrMagnitude;
                            if (d < bestDist) { bestUnclaimed = s; bestDist = d; }
                        }
                        if (bestUnclaimed >= 0)
                        {
                            int oldSrc2 = result.targetShellToSourceShell[tsi];
                            UvtLog.Info($"[GroupedTransfer] Dedup forced-merged: t{tsi} " +
                                $"src{oldSrc2}→src{bestUnclaimed} (unclaimed)");
                            result.targetShellToSourceShell[tsi] = bestUnclaimed;
                            result.targetShellMatchDistSqr[tsi] = bestDist;
                            claimed.Add(bestUnclaimed);
                        }
                        tgtIsMerged[tsi] = true;
                    }

                    result.dedupConflicts = dedupConflicts;
                    if (dedupConflicts > 0)
                        UvtLog.Info($"[GroupedTransfer] Dedup: {dedupConflicts} same-source conflicts, " +
                            $"{spreadCount} spread, {needsRematch.Count} forced merged");
                }
            }

            // Post-dedup diagnostic: check for remaining same-source duplicates.
            // Distinguish allowed shared-source fragments (non-overlapping UV0)
            // from true duplicates (overlapping UV0 — tiling/symmetric).
            // Only true duplicates go into duplicateSources for Phase 3 constraint.
            //
            // Also build fragment-restricted face lists and BVHs for shared sources
            // with non-overlapping UV0 fragments. This prevents cross-fragment
            // interpolation where a target vertex's UV0 is geometrically closer to
            // the wrong fragment's faces, causing UV2 to jump to the wrong region.
            var fragmentRestrictedFaces = new Dictionary<int, List<int>>();
            var fragmentRestrictedBvh = new Dictionary<int, TriangleBvh2D>();
            var duplicateSources = new HashSet<int>();
            {
                var srcToTargets = new Dictionary<int, List<int>>();
                for (int tsi = 0; tsi < tgtShells.Count; tsi++)
                {
                    int src = result.targetShellToSourceShell[tsi];
                    if (src < 0) continue;
                    if (!srcToTargets.TryGetValue(src, out var list))
                    {
                        list = new List<int>();
                        srcToTargets[src] = list;
                    }
                    list.Add(tsi);
                }
                foreach (var kv in srcToTargets)
                {
                    if (kv.Value.Count <= 1) continue;

                    // Check if these targets have overlapping UV0 bboxes
                    bool hasOverlap = false;
                    for (int i = 0; i < kv.Value.Count && !hasOverlap; i++)
                    {
                        var sA = tgtShells[kv.Value[i]];
                        for (int j = i + 1; j < kv.Value.Count && !hasOverlap; j++)
                        {
                            var sB = tgtShells[kv.Value[j]];
                            if (sA.boundsMin.x < sB.boundsMax.x &&
                                sA.boundsMax.x > sB.boundsMin.x &&
                                sA.boundsMin.y < sB.boundsMax.y &&
                                sA.boundsMax.y > sB.boundsMin.y)
                            {
                                hasOverlap = true;
                            }
                        }
                    }

                    var labels = new List<string>();
                    foreach (int tsi in kv.Value)
                        labels.Add($"t{tsi}(merged={tgtIsMerged[tsi]})");

                    if (hasOverlap)
                    {
                        // True overlap — constrain Phase 3
                        duplicateSources.Add(kv.Key);
                        UvtLog.Warn($"[GroupedTransfer] POST-DEDUP DUPLICATE: src{kv.Key} " +
                            $"claimed by {string.Join(", ", labels)}");
                    }
                    else
                    {
                        // Non-overlapping UV0 fragments sharing a source — expected
                        UvtLog.Info($"[GroupedTransfer] Shared source (fragments): src{kv.Key} " +
                            $"used by {string.Join(", ", labels)} — non-overlapping UV0, OK");

                        // Build restricted face lists/BVHs so each target only searches
                        // within its own UV0 sub-region of the shared source.
                        // Without this, BVH FindNearest may return a triangle from the
                        // wrong fragment, causing UV2 to jump to the wrong region.
                        var allFaces = srcShells[kv.Key].faceIndices;
                        foreach (int tsi2 in kv.Value)
                        {
                            var ts = tgtShells[tsi2];
                            Vector2 sz = ts.boundsMax - ts.boundsMin;
                            Vector2 pad = new Vector2(
                                Mathf.Max(sz.x * 0.15f, 0.002f),
                                Mathf.Max(sz.y * 0.15f, 0.002f));
                            Vector2 aMin = ts.boundsMin - pad;
                            Vector2 aMax = ts.boundsMax + pad;

                            var restricted = new List<int>();
                            foreach (int f in allFaces)
                            {
                                Vector2 fa = triUv0A[f], fb = triUv0B[f], fc = triUv0C[f];
                                Vector2 fMin = Vector2.Min(fa, Vector2.Min(fb, fc));
                                Vector2 fMax = Vector2.Max(fa, Vector2.Max(fb, fc));
                                if (fMin.x <= aMax.x && fMax.x >= aMin.x &&
                                    fMin.y <= aMax.y && fMax.y >= aMin.y)
                                    restricted.Add(f);
                            }

                            if (restricted.Count > 0 && restricted.Count < allFaces.Count)
                            {
                                fragmentRestrictedFaces[tsi2] = restricted;
                                if (restricted.Count >= kMinFacesForShellBvh)
                                    fragmentRestrictedBvh[tsi2] = new TriangleBvh2D(
                                        triUv0A, triUv0B, triUv0C, restricted.ToArray());
                            }
                        }
                    }
                }
            }

            // Log all target→source assignments for diagnostics
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[GroupedTransfer] Assignments:");
                for (int tsi = 0; tsi < tgtShells.Count; tsi++)
                {
                    int src = result.targetShellToSourceShell[tsi];
                    sb.Append($" t{tsi}→src{src}");
                    if (tgtIsMerged[tsi]) sb.Append("(M)");
                    if (tgtForce3DFallback[tsi]) sb.Append("(3D)");
                }
                UvtLog.Info(sb.ToString());
            }

            // Log overlap group assignment summary
            if (overlapGroups.Count > 0)
            {
                foreach (var group in overlapGroups)
                {
                    // Count how many unique sources in this group are claimed by targets
                    var groupSrcUsed = new HashSet<int>();
                    int groupTargets = 0;
                    for (int tsi = 0; tsi < tgtShells.Count; tsi++)
                    {
                        int src = result.targetShellToSourceShell[tsi];
                        if (src >= 0 && group.Contains(src))
                        {
                            groupSrcUsed.Add(src);
                            groupTargets++;
                        }
                    }
                    if (groupTargets > 0)
                        UvtLog.Info($"[GroupedTransfer] Overlap group [{string.Join(",", group)}]: " +
                            $"{groupTargets} targets → {groupSrcUsed.Count}/{group.Count} unique sources");
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

            // Track UV2 AABBs of placed force3D shells to prevent mutual overlap
            var force3DUsedRegions = new List<(Vector2 min, Vector2 max)>();

            // Track which source shells are already claimed by overlap group targets.
            // Prevents multiple targets from mapping to the same source, which causes
            // UV2 overlap (same-src) artifacts. Includes both interp and merged claims.
            var overlapClaimedSources = new HashSet<int>();


            for (int tsi = 0; tsi < tgtShells.Count; tsi++)
            {
                var tShell = tgtShells[tsi];
                int chosenSrc = result.targetShellToSourceShell[tsi];

                // Skip unmatched non-merged shells
                if (chosenSrc < 0 && !tgtIsMerged[tsi]) continue;

                shellsMatched++;
                var srcFacesChosen = chosenSrc >= 0 ? srcShells[chosenSrc].faceIndices : null;

                // Use fragment-restricted faces when this target shares a source
                // with non-overlapping UV0 fragments (prevents cross-fragment UV2 bleed)
                if (fragmentRestrictedFaces.TryGetValue(tsi, out var rFaces))
                    srcFacesChosen = rFaces;

                Dictionary<int, Vector2> chosenUv2;

                // ── Unified overlap transfer for overlapping UV0 shells ──
                // Only for MERGED shells — non-merged shells use standard interp/xform
                // which stays within source UV2 convex hull and works better for normal geometry.
                bool isFragMergedShell = tgtIsFragmentMerged != null
                    && tsi < tgtIsFragmentMerged.Length
                    && tgtIsFragmentMerged[tsi];
                if (tgtIsMerged[tsi] && chosenSrc >= 0 && srcShellOverlapMembers[chosenSrc] != null)
                {
                    // Fragment-merged shells OR duplicate-source shells: restrict to
                    // ONLY their merge source. Fragment-merged need partition-aware
                    // candidate generation but must not try other group members.
                    // Duplicate-source shells share their source with a non-merged
                    // sibling — trying other group members risks mixing UV2 regions
                    // and creating cross-shell stretching artifacts.
                    bool isDuplicateSrc = duplicateSources.Contains(chosenSrc);
                    var groupMembers = (isFragMergedShell || isDuplicateSrc)
                        ? new List<int> { chosenSrc }
                        : srcShellOverlapMembers[chosenSrc];

                    Dictionary<int, Vector2> bestOverlapUv2 = null;
                    int bestOverlapIssues = int.MaxValue;
                    int bestOverlapCoverage = -1;
                    int bestOverlapSrc = chosenSrc;
                    string bestOverlapMethod = "";
                    float bestOverlapCentroidDistSq = float.MaxValue;

                    // Target 3D centroid for distance-based source selection
                    Vector3 tgtCentroid = result.targetShellCentroids[tsi];

                    // Compute target 3D area for area-ratio comparison
                    float tgt3DArea = 0f;
                    foreach (int tfi in tShell.faceIndices)
                    {
                        int ti0 = tgtTris[tfi * 3], ti1 = tgtTris[tfi * 3 + 1], ti2 = tgtTris[tfi * 3 + 2];
                        if (ti0 >= tVerts.Length || ti1 >= tVerts.Length || ti2 >= tVerts.Length) continue;
                        tgt3DArea += Vector3.Cross(tVerts[ti1] - tVerts[ti0], tVerts[ti2] - tVerts[ti0]).magnitude;
                    }
                    tgt3DArea *= 0.5f;

                    // Precompute source 3D area + centroid distance for each group member
                    var srcGroupArea = new float[srcShells.Count];
                    var srcGroupDistSq = new float[srcShells.Count];
                    foreach (int si in groupMembers)
                    {
                        float area = 0f;
                        foreach (int sfi in srcShells[si].faceIndices)
                        {
                            int s0 = srcTris[sfi * 3], s1 = srcTris[sfi * 3 + 1], s2 = srcTris[sfi * 3 + 2];
                            if (s0 >= srcVerts.Length || s1 >= srcVerts.Length || s2 >= srcVerts.Length) continue;
                            area += Vector3.Cross(srcVerts[s1] - srcVerts[s0], srcVerts[s2] - srcVerts[s0]).magnitude;
                        }
                        srcGroupArea[si] = area * 0.5f;
                        srcGroupDistSq[si] = (srcCentroid3D[si] - tgtCentroid).sqrMagnitude;
                    }

                    // ── Pre-pass: 3D face-proximity voting with normal alignment ──
                    // For each target face centroid, find the nearest source face centroid
                    // (across all overlap group members) with aligned normal. Vote for that
                    // source. The source with the most votes is the physically correct one.
                    // This is deterministic across LODs because source faces are fixed.
                    // Also record per-face source for composite UV2 building.
                    var srcVoteCount = new int[srcShells.Count];
                    int totalVotes = 0;
                    var perFaceVoteSrc = new Dictionary<int, int>(); // faceIndex → sourceIndex

                    foreach (int tfi in tShell.faceIndices)
                    {
                        int ti0 = tgtTris[tfi * 3], ti1 = tgtTris[tfi * 3 + 1], ti2 = tgtTris[tfi * 3 + 2];
                        if (ti0 >= tVerts.Length || ti1 >= tVerts.Length || ti2 >= tVerts.Length) continue;

                        Vector3 tFC = (tVerts[ti0] + tVerts[ti1] + tVerts[ti2]) / 3f;
                        Vector3 tFN = Vector3.Cross(tVerts[ti1] - tVerts[ti0], tVerts[ti2] - tVerts[ti0]);
                        if (tFN.sqrMagnitude < 1e-12f) continue;
                        tFN.Normalize();

                        float bestDistSq = float.MaxValue;
                        int bestSrc = -1;

                        foreach (int si in groupMembers)
                        {
                            foreach (int sfi in srcShells[si].faceIndices)
                            {
                                int s0 = srcTris[sfi * 3], s1 = srcTris[sfi * 3 + 1], s2 = srcTris[sfi * 3 + 2];
                                if (s0 >= srcVerts.Length || s1 >= srcVerts.Length || s2 >= srcVerts.Length) continue;

                                Vector3 sFC = (srcVerts[s0] + srcVerts[s1] + srcVerts[s2]) / 3f;
                                float dSq = (tFC - sFC).sqrMagnitude;
                                if (dSq >= bestDistSq) continue;

                                Vector3 sFN = Vector3.Cross(srcVerts[s1] - srcVerts[s0], srcVerts[s2] - srcVerts[s0]);
                                if (sFN.sqrMagnitude < 1e-12f) continue;
                                sFN.Normalize();

                                // Skip backfacing: only match faces pointing in a similar direction
                                if (Vector3.Dot(tFN, sFN) < 0.3f) continue;

                                bestDistSq = dSq;
                                bestSrc = si;
                            }
                        }

                        if (bestSrc >= 0)
                        {
                            srcVoteCount[bestSrc]++;
                            totalVotes++;
                            perFaceVoteSrc[tfi] = bestSrc;
                        }
                    }

                    // Find vote winner — deterministic tie-breaking by 3D centroid
                    // distance ensures stable results across LODs even when vote
                    // counts are identical (common for thin belts/straps).
                    int voteSrc = -1;
                    int voteCount = 0;
                    float voteWinnerDistSq = float.MaxValue;
                    foreach (int si in groupMembers)
                    {
                        int votes = srcVoteCount[si];
                        if (votes < voteCount) continue;
                        float dSq = (srcCentroid3D[si] - tgtCentroid).sqrMagnitude;
                        if (votes > voteCount || dSq < voteWinnerDistSq)
                        {
                            voteCount = votes;
                            voteSrc = si;
                            voteWinnerDistSq = dSq;
                        }
                    }

                    // Check for cross-LOD hint: find nearest previous-LOD merged shell
                    // and prefer its source for consistent cross-LOD appearance.
                    int hintSrc = -1;
                    if (previousLodHints != null && previousLodHints.Count > 0)
                    {
                        float bestHintDistSq = float.MaxValue;
                        foreach (var hint in previousLodHints)
                        {
                            if (!groupMembers.Contains(hint.sourceShellIndex)) continue;
                            float d = (tgtCentroid - hint.centroid3D).sqrMagnitude;
                            if (d < bestHintDistSq)
                            {
                                bestHintDistSq = d;
                                hintSrc = hint.sourceShellIndex;
                            }
                        }
                    }

                    // Collect valid UV2 candidates from all group members.
                    // Each source produces UV2 for the target vertices via its own UV0→UV2 mapping.
                    var validCandidates = new Dictionary<int, OverlapCandidate>();

                    foreach (int si in groupMembers)
                    {
                        var allCandidates = GenerateOverlapCandidates(
                            tShell, si,
                            tVerts, tNormals, tUv0,
                            srcShells, srcVerts, srcUv0, srcUv2, srcTris,
                            triUv0A, triUv0B, triUv0C,
                            triUv2A, triUv2B, triUv2C, triNormal,
                            shellBvh3D[si], shellBvh3DFaceMap[si], shellBvh3DFaceNormals[si],
                            partitionBvh[si],
                            srcPartitions[si],
                            partitionXform[si],
                            srcTransforms[si],
                            srcIsRibbon[si], srcRibbonAxis[si], srcRibbonAxis2[si], srcRibbonCentroid[si],
                            srcUv2Min, srcUv2Max, groupMembers,
                            kRayMaxDist);

                        var best = SelectBestCandidate(allCandidates, tShell.faceIndices, tgtTris, tUv0);
                        if (best.HasValue)
                        {
                            validCandidates[si] = best.Value;

                            // Track best overall source (for fallback and logging)
                            var b = best.Value;
                            int votes = srcVoteCount[si];
                            float centroidDistSq = srcGroupDistSq[si];

                            bool betterIssues = b.issues < bestOverlapIssues;
                            bool sameIssues = b.issues == bestOverlapIssues;
                            bool isUnclaimed = !overlapClaimedSources.Contains(si);
                            bool bestIsUnclaimed = bestOverlapSrc < 0 || !overlapClaimedSources.Contains(bestOverlapSrc);
                            bool isHintMatch = (si == hintSrc);
                            bool bestIsHintMatch = (bestOverlapSrc == hintSrc);
                            bool isVoteWinner = (si == voteSrc && voteCount > 0);
                            bool bestIsVoteWinner = (bestOverlapSrc == voteSrc && voteCount > 0);
                            bool closerCentroid = centroidDistSq < bestOverlapCentroidDistSq - 1e-8f;

                            // Strong vote: >50% of votes → prioritize over unclaimed/hint
                            bool strongVote = voteCount > 0 && voteCount * 2 > totalVotes;

                            bool wins = betterIssues;
                            if (!wins && sameIssues)
                            {
                                if (strongVote)
                                {
                                    // Strong vote winner takes priority over unclaimed/hint
                                    wins = isVoteWinner && !bestIsVoteWinner;
                                    if (!wins)
                                    {
                                        wins = isVoteWinner == bestIsVoteWinner
                                            && isUnclaimed && !bestIsUnclaimed;
                                        if (!wins)
                                            wins = isVoteWinner == bestIsVoteWinner
                                                && isUnclaimed == bestIsUnclaimed
                                                && closerCentroid;
                                    }
                                }
                                else
                                {
                                    // Weak/no vote: original priority order
                                    wins = isUnclaimed && !bestIsUnclaimed;
                                    if (!wins)
                                        wins = isUnclaimed == bestIsUnclaimed
                                            && isHintMatch && !bestIsHintMatch;
                                    if (!wins)
                                        wins = isUnclaimed == bestIsUnclaimed
                                            && isHintMatch == bestIsHintMatch
                                            && isVoteWinner && !bestIsVoteWinner;
                                    if (!wins)
                                        wins = isUnclaimed == bestIsUnclaimed
                                            && isHintMatch == bestIsHintMatch
                                            && isVoteWinner == bestIsVoteWinner
                                            && votes > srcVoteCount[bestOverlapSrc];
                                    if (!wins)
                                        wins = isUnclaimed == bestIsUnclaimed
                                            && isHintMatch == bestIsHintMatch
                                            && isVoteWinner == bestIsVoteWinner
                                            && votes == srcVoteCount[bestOverlapSrc]
                                            && closerCentroid;
                                }
                            }

                            if (wins)
                            {
                                bestOverlapUv2 = b.uv2;
                                bestOverlapIssues = b.issues;
                                bestOverlapCoverage = b.coverage;
                                bestOverlapSrc = si;
                                bestOverlapMethod = b.method;
                                bestOverlapCentroidDistSq = centroidDistSq;
                            }
                        }
                    }

                    if (bestOverlapUv2 != null && bestOverlapUv2.Count > 0)
                    {
                        // Fragment-merged shells: each source covers only its fragment
                        // (~1/N of faces), so global issue count is always high.
                        // Per-face composite handles this — don't reject the entire path.
                        bool tooManyIssues = !isFragMergedShell
                            && bestOverlapIssues > tShell.faceIndices.Count / 2;

                        // ── Build composite UV2 from per-face voting ──
                        // On lower LODs, one target shell can span multiple source shells
                        // (e.g., belt strap: LOD0 has 13 source shells, LOD2 has 4 target shells).
                        // Instead of picking one source for all vertices, each face gets UV2
                        // from the source that's geometrically closest (per-face vote).
                        // Shared vertices at source boundaries may get reassigned — lightmap
                        // padding handles the resulting micro-seams.
                        var compositeUv2 = new Dictionary<int, Vector2>();
                        var compositeVertSrc = new Dictionary<int, int>(); // vertex → source that set it
                        var compositeUsedSources = new HashSet<int>();
                        int compositeFaces = 0;

                        // Compute max allowed UV2 displacement for shared vertex overwrites.
                        // Vertices shared between faces from different sources should not jump
                        // across UV2 regions — that creates triangular protrusions.
                        Vector2 bsMinC = srcUv2Min[bestOverlapSrc];
                        Vector2 bsMaxC = srcUv2Max[bestOverlapSrc];
                        float bsDiag = (bsMaxC - bsMinC).magnitude;
                        float maxVertJump = bsDiag * 0.5f;

                        foreach (int tfi in tShell.faceIndices)
                        {
                            int ti0 = tgtTris[tfi * 3], ti1 = tgtTris[tfi * 3 + 1], ti2 = tgtTris[tfi * 3 + 2];
                            if (ti0 >= tVerts.Length || ti1 >= tVerts.Length || ti2 >= tVerts.Length) continue;

                            // Try voted source first, then best overall
                            int faceSrc = perFaceVoteSrc.ContainsKey(tfi) ? perFaceVoteSrc[tfi] : -1;
                            bool assigned = false;

                            // Candidate sources in priority order: voted source, then best overall
                            int[] tryOrder = (faceSrc >= 0 && faceSrc != bestOverlapSrc)
                                ? new[] { faceSrc, bestOverlapSrc }
                                : new[] { bestOverlapSrc };

                            foreach (int src in tryOrder)
                            {
                                if (src < 0 || !validCandidates.ContainsKey(src)) continue;
                                // Skip high-issue sources unless fragment-merged (each
                                // source covers only its fragment → high global issues expected)
                                if (!isFragMergedShell
                                    && validCandidates[src].issues > tShell.faceIndices.Count / 2)
                                    continue;
                                var srcUv2Map = validCandidates[src].uv2;
                                if (srcUv2Map.ContainsKey(ti0) && srcUv2Map.ContainsKey(ti1) && srcUv2Map.ContainsKey(ti2))
                                {
                                    // Write vertex UV2, but protect against cross-region jumps:
                                    // if a vertex was already set by a different source and the
                                    // new UV2 is far away, keep the old value to prevent protrusions.
                                    int[] vis = { ti0, ti1, ti2 };
                                    foreach (int vi in vis)
                                    {
                                        if (compositeVertSrc.TryGetValue(vi, out int prevSrc) && prevSrc != src)
                                        {
                                            float jump = (compositeUv2[vi] - srcUv2Map[vi]).magnitude;
                                            if (jump > maxVertJump)
                                                continue; // keep old value
                                        }
                                        compositeUv2[vi] = srcUv2Map[vi];
                                        compositeVertSrc[vi] = src;
                                    }
                                    compositeUsedSources.Add(src);
                                    compositeFaces++;
                                    assigned = true;
                                    break;
                                }
                            }

                            // Last resort: use best overall even if it doesn't have all 3 verts
                            if (!assigned && bestOverlapUv2 != null)
                            {
                                if (bestOverlapUv2.ContainsKey(ti0) && !compositeUv2.ContainsKey(ti0))
                                    { compositeUv2[ti0] = bestOverlapUv2[ti0]; compositeVertSrc[ti0] = bestOverlapSrc; }
                                if (bestOverlapUv2.ContainsKey(ti1) && !compositeUv2.ContainsKey(ti1))
                                    { compositeUv2[ti1] = bestOverlapUv2[ti1]; compositeVertSrc[ti1] = bestOverlapSrc; }
                                if (bestOverlapUv2.ContainsKey(ti2) && !compositeUv2.ContainsKey(ti2))
                                    { compositeUv2[ti2] = bestOverlapUv2[ti2]; compositeVertSrc[ti2] = bestOverlapSrc; }
                                compositeUsedSources.Add(bestOverlapSrc);
                            }
                        }

                        // Fill any remaining gaps from best overall
                        foreach (var kv in bestOverlapUv2)
                            if (!compositeUv2.ContainsKey(kv.Key))
                                compositeUv2[kv.Key] = kv.Value;

                        // ── Per-face outlier rejection ──
                        // When per-face voting assigns faces to different overlap group
                        // sources, some faces may land in a completely different UV2 region
                        // (e.g., front side of belt mapped to back side's UV2). Detect and
                        // replace these outlier faces with the best source's UV2.
                        int outlierFacesFixed = 0;
                        if (compositeUsedSources.Count > 1 && bestOverlapUv2 != null)
                        {
                            Vector2 bsMin = srcUv2Min[bestOverlapSrc];
                            Vector2 bsMax = srcUv2Max[bestOverlapSrc];
                            Vector2 bsSize = bsMax - bsMin;
                            // Pad AABB by 50% of its dimensions to allow some flexibility
                            float padX = bsSize.x * 0.5f;
                            float padY = bsSize.y * 0.5f;
                            Vector2 allowMin = bsMin - new Vector2(padX, padY);
                            Vector2 allowMax = bsMax + new Vector2(padX, padY);

                            foreach (int tfi in tShell.faceIndices)
                            {
                                int ti0 = tgtTris[tfi * 3], ti1 = tgtTris[tfi * 3 + 1], ti2 = tgtTris[tfi * 3 + 2];
                                if (!compositeUv2.ContainsKey(ti0) || !compositeUv2.ContainsKey(ti1) || !compositeUv2.ContainsKey(ti2))
                                    continue;
                                Vector2 uv0 = compositeUv2[ti0], uv1 = compositeUv2[ti1], uv2f = compositeUv2[ti2];
                                Vector2 centroid = (uv0 + uv1 + uv2f) / 3f;

                                if (centroid.x < allowMin.x || centroid.x > allowMax.x ||
                                    centroid.y < allowMin.y || centroid.y > allowMax.y)
                                {
                                    // Face UV2 centroid is outside the allowed region — replace
                                    if (bestOverlapUv2.ContainsKey(ti0)) compositeUv2[ti0] = bestOverlapUv2[ti0];
                                    if (bestOverlapUv2.ContainsKey(ti1)) compositeUv2[ti1] = bestOverlapUv2[ti1];
                                    if (bestOverlapUv2.ContainsKey(ti2)) compositeUv2[ti2] = bestOverlapUv2[ti2];
                                    outlierFacesFixed++;
                                }
                            }
                            if (outlierFacesFixed > 0)
                                UvtLog.Info($"[GroupedTransfer]   t{tsi}: {outlierFacesFixed} outlier faces " +
                                    $"replaced with best source UV2 (src{bestOverlapSrc})");
                        }

                        // ── Spatial coherence check for composite UV2 ──
                        // If the composite UV2 AABB is much larger than the best single
                        // source's UV2 region, the composite is placing faces in different
                        // UV2 regions — this breaks lightmap continuity. Reject and
                        // fall through to single-source transfer.
                        bool compositeSpatiallyBroken = false;
                        if (compositeUsedSources.Count > 1 && compositeUv2.Count > 0)
                        {
                            Vector2 compMin = new Vector2(float.MaxValue, float.MaxValue);
                            Vector2 compMax = new Vector2(float.MinValue, float.MinValue);
                            foreach (var kv in compositeUv2)
                            {
                                compMin = Vector2.Min(compMin, kv.Value);
                                compMax = Vector2.Max(compMax, kv.Value);
                            }
                            float compArea = (compMax.x - compMin.x) * (compMax.y - compMin.y);

                            // Best source's UV2 AABB area
                            Vector2 bsMin2 = srcUv2Min[bestOverlapSrc];
                            Vector2 bsMax2 = srcUv2Max[bestOverlapSrc];
                            float bestSrcUv2Area = (bsMax2.x - bsMin2.x) * (bsMax2.y - bsMin2.y);

                            // If composite AABB > 2× best source UV2 AABB, it's spanning
                            // multiple UV2 regions → reject
                            if (bestSrcUv2Area > 1e-8f && compArea > bestSrcUv2Area * 2.0f)
                            {
                                compositeSpatiallyBroken = true;
                                UvtLog.Info($"[GroupedTransfer]   t{tsi}: composite spatially broken " +
                                    $"(compArea={compArea:F6} > 2×srcArea={bestSrcUv2Area:F6}), " +
                                    $"falling back to single-source");

                                // Replace composite with best single-source UV2
                                compositeUv2 = bestOverlapUv2;
                                compositeUsedSources.Clear();
                                compositeUsedSources.Add(bestOverlapSrc);
                            }
                        }

                        // Determine primary source (most faces contributed) for hint/log
                        int primarySrc = bestOverlapSrc;

                        float bestSrcDist = Mathf.Sqrt(bestOverlapCentroidDistSq);
                        float bestAreaR = (tgt3DArea > 1e-8f && srcGroupArea[bestOverlapSrc] > 1e-8f)
                            ? Mathf.Min(srcGroupArea[bestOverlapSrc], tgt3DArea) / Mathf.Max(srcGroupArea[bestOverlapSrc], tgt3DArea)
                            : 0f;
                        string compositeInfo = compositeUsedSources.Count > 1
                            ? $", composite={compositeUsedSources.Count}src/{compositeFaces}f"
                            : "";
                        string fragInfo = isFragMergedShell ? ", fragMerged" : "";
                        string coherenceInfo = compositeSpatiallyBroken ? ", spatial-fix" : "";
                        // ── Per-vertex outlier correction ──
                        // After composite UV2 is built, check each vertex against the
                        // best source's UV2 AABB. Vertices far outside are re-projected
                        // via 3D BVH to prevent protrusions / spikes in the UV layout.
                        int vertexOutliersFixed = 0;
                        if (compositeUv2.Count > 0 && bestOverlapSrc >= 0)
                        {
                            Vector2 sMin = srcUv2Min[bestOverlapSrc];
                            Vector2 sMax = srcUv2Max[bestOverlapSrc];
                            Vector2 sSize = sMax - sMin;
                            // Allow 30% margin beyond source UV2 AABB
                            float marginX = sSize.x * 0.3f;
                            float marginY = sSize.y * 0.3f;
                            Vector2 allowMin = sMin - new Vector2(marginX, marginY);
                            Vector2 allowMax = sMax + new Vector2(marginX, marginY);

                            // Also compute median UV2 to detect outliers relative to shell center
                            var allUvX = new List<float>(compositeUv2.Count);
                            var allUvY = new List<float>(compositeUv2.Count);
                            foreach (var kv in compositeUv2)
                            {
                                allUvX.Add(kv.Value.x);
                                allUvY.Add(kv.Value.y);
                            }
                            allUvX.Sort(); allUvY.Sort();
                            float medX = allUvX[allUvX.Count / 2];
                            float medY = allUvY[allUvY.Count / 2];
                            // Max distance from median: 2× source diagonal
                            float srcDiag = sSize.magnitude;
                            float maxMedianDist = srcDiag * 2.0f;

                            var outlierVerts = new List<int>();
                            foreach (var kv in compositeUv2)
                            {
                                Vector2 uv = kv.Value;
                                bool outsideBounds = uv.x < allowMin.x || uv.x > allowMax.x ||
                                                     uv.y < allowMin.y || uv.y > allowMax.y;
                                bool farFromMedian = srcDiag > 1e-6f &&
                                    new Vector2(uv.x - medX, uv.y - medY).magnitude > maxMedianDist;
                                if (outsideBounds || farFromMedian)
                                    outlierVerts.Add(kv.Key);
                            }

                            if (outlierVerts.Count > 0 && outlierVerts.Count < compositeUv2.Count)
                            {
                                // Re-project outliers via 3D BVH with normal filtering
                                var bvh3D = shellBvh3D[bestOverlapSrc];
                                var fMap3D = shellBvh3DFaceMap[bestOverlapSrc];
                                var fNorm3D = shellBvh3DFaceNormals[bestOverlapSrc];

                                foreach (int vi in outlierVerts)
                                {
                                    if (vi >= tVerts.Length) continue;
                                    Vector3 tPos = tVerts[vi];
                                    Vector3 tNrm = (tNormals != null && vi < tNormals.Length)
                                        ? tNormals[vi] : Vector3.up;

                                    // Try ray + normal-filtered nearest
                                    int hitFace = -1;
                                    Vector3 hitBary = Vector3.zero;

                                    if (bvh3D != null && tNrm.sqrMagnitude > 0.5f)
                                    {
                                        var rayHit = bvh3D.RaycastBidirectional(
                                            tPos, tNrm.normalized, kRayMaxDist);
                                        if (rayHit.triangleIndex >= 0)
                                        {
                                            // Back-face culling: reject hits on opposite-facing triangles
                                            int gfRay = (rayHit.triangleIndex < fMap3D.Length)
                                                ? fMap3D[rayHit.triangleIndex] : -1;
                                            bool facing = gfRay >= 0 && gfRay < triNormal.Length
                                                && Vector3.Dot(triNormal[gfRay], tNrm) > 0f;
                                            if (facing)
                                            {
                                                hitFace = rayHit.triangleIndex;
                                                hitBary = rayHit.barycentric;
                                            }
                                        }
                                    }
                                    if (hitFace < 0 && bvh3D != null && fNorm3D != null)
                                    {
                                        var nearest = bvh3D.FindNearestNormalFiltered(
                                            tPos, tNrm, fNorm3D, 0.3f);
                                        if (nearest.triangleIndex >= 0)
                                        {
                                            hitFace = nearest.triangleIndex;
                                            hitBary = nearest.barycentric;
                                        }
                                    }
                                    // Last resort: pure nearest
                                    if (hitFace < 0 && bvh3D != null)
                                    {
                                        var nearest = bvh3D.FindNearest(tPos);
                                        if (nearest.triangleIndex >= 0)
                                        {
                                            hitFace = nearest.triangleIndex;
                                            hitBary = nearest.barycentric;
                                        }
                                    }

                                    if (hitFace >= 0 && hitFace < fMap3D.Length)
                                    {
                                        int gf = fMap3D[hitFace];
                                        Vector2 reprojected = triUv2A[gf] * hitBary.x
                                                            + triUv2B[gf] * hitBary.y
                                                            + triUv2C[gf] * hitBary.z;
                                        // Only accept if reprojected UV2 is within bounds
                                        if (reprojected.x >= allowMin.x && reprojected.x <= allowMax.x &&
                                            reprojected.y >= allowMin.y && reprojected.y <= allowMax.y)
                                        {
                                            compositeUv2[vi] = reprojected;
                                            vertexOutliersFixed++;
                                        }
                                    }
                                }
                            }
                        }

                        // Compute composite UV2 AABB for diagnostics
                        Vector2 compUv2Min = new Vector2(float.MaxValue, float.MaxValue);
                        Vector2 compUv2Max = new Vector2(float.MinValue, float.MinValue);
                        foreach (var kv in compositeUv2)
                        {
                            compUv2Min = Vector2.Min(compUv2Min, kv.Value);
                            compUv2Max = Vector2.Max(compUv2Max, kv.Value);
                        }
                        Vector2 srcBbMin = srcUv2Min[bestOverlapSrc];
                        Vector2 srcBbMax = srcUv2Max[bestOverlapSrc];
                        string aabbInfo = compositeUv2.Count > 0
                            ? $", uv2bb=[{compUv2Min.x:F3},{compUv2Min.y:F3}]-[{compUv2Max.x:F3},{compUv2Max.y:F3}]" +
                              $" src=[{srcBbMin.x:F3},{srcBbMin.y:F3}]-[{srcBbMax.x:F3},{srcBbMax.y:F3}]"
                            : "";
                        string outlierInfo = vertexOutliersFixed > 0
                            ? $", outliersFix={vertexOutliersFixed}"
                            : "";
                        UvtLog.Info($"[GroupedTransfer]   t{tsi}: overlap unified " +
                            $"(best src{bestOverlapSrc}, {bestOverlapCoverage} cov, " +
                            $"{bestOverlapIssues} issues, " +
                            $"vote=src{voteSrc}({voteCount}/{totalVotes}), " +
                            $"dist3D={bestSrcDist:F4}, areaR={bestAreaR:F2}, " +
                            $"method={bestOverlapMethod}, " +
                            $"tried {groupMembers.Count} shells" +
                            (hintSrc >= 0 ? $", hint=src{hintSrc}" : "") +
                            compositeInfo + fragInfo + coherenceInfo + outlierInfo + aabbInfo +
                            (tooManyIssues ? " → fall-through" : "") + ")");

                        // Recount issues after outlier correction
                        if (vertexOutliersFixed > 0)
                            bestOverlapIssues = CountShellIssues(
                                tShell.faceIndices, tgtTris, tUv0, compositeUv2);

                        if (!tooManyIssues)
                        {
                            foreach (var kv in compositeUv2)
                            {
                                result.uv2[kv.Key] = kv.Value;
                                result.vertexToSourceShell[kv.Key] = primarySrc;
                                transferred++;
                            }
                            result.targetShellToSourceShell[tsi] = primarySrc;
                            result.targetShellIssues[tsi] = bestOverlapIssues;
                            shellsMerged++;
                            result.targetShellMethod[tsi] = 2;
                            foreach (int src in compositeUsedSources)
                                overlapClaimedSources.Add(src);

                            continue;
                        }
                        // Fall through: update chosenSrc to best source from overlap analysis
                        chosenSrc = bestOverlapSrc;
                        srcFacesChosen = srcShells[chosenSrc].faceIndices;
                    }
                }

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

                    // force3D: try all-source first (to find UV2 space in a different
                    // source region), fall back to constrained if overlap guard rejects.
                    // Normal merged: constrained first, all-source fallback (unchanged).
                    // Exception: duplicate-source shells must stay constrained even in
                    // force3D mode — all-source would match the sibling target's faces.
                    bool force3D = tgtForce3DFallback[tsi];
                    bool isDupSrc = chosenSrc >= 0 && duplicateSources.Contains(chosenSrc);
                    if (isDupSrc) force3D = false; // force constrained-first for dup sources

                    for (int pass = 0; pass < 2; pass++)
                    {
                        bool constrained;
                        if (force3D)
                        {
                            // pass 0 = all-source, pass 1 = constrained fallback
                            if (pass == 0)
                            {
                                constrained = false;           // all-source first
                            }
                            else
                            {
                                if (bestMergedIssues == 0) break;  // all-source was clean
                                constrained = (srcFacesChosen != null);
                                if (!constrained) break;       // no constrained faces
                            }
                        }
                        else
                        {
                            // Normal merged: pass 0 = constrained, pass 1 = all-source
                            if (pass == 0)
                            {
                                constrained = (srcFacesChosen != null);
                            }
                            else
                            {
                                if (srcFacesChosen == null) break; // pass 0 was already all-source
                                if (bestMergedIssues == 0) break;  // constrained was clean
                                // Small/degenerate shells: stay constrained to avoid
                                // wandering into wrong source's UV2 region
                                if (tShell.faceIndices.Count <= 4) break;
                                // Duplicate-source shells share their source with a
                                // sibling target — all-source would find faces from
                                // other shells and drag vertices to wrong UV2 regions.
                                if (chosenSrc >= 0 && duplicateSources.Contains(chosenSrc)) break;
                                constrained = false;               // all-source
                            }
                        }

                        var candidate = new Dictionary<int, Vector2>();
                        int localFixes = 0;

                        // Select BVH for UV0 projection
                        // When source shell has UV0 overlap with partitions,
                        // use PER-VERTEX partition BVH based on each vertex's
                        // normal direction (front→front partition, back→back).
                        TriangleBvh2D defaultUv0Bvh = null;
                        bool hasPartitions = false;
                        SpatialPartitioner.ShellPartitionResult srcPR = null;
                        if (constrained && chosenSrc >= 0)
                        {
                            srcPR = srcPartitions[chosenSrc];
                            hasPartitions = srcPR.hasOverlap && srcPR.partitionCount > 1
                                && partitionBvh[chosenSrc] != null;
                            defaultUv0Bvh = shellUv0Bvh[chosenSrc];
                        }
                        else if (!constrained)
                            defaultUv0Bvh = globalUv0Bvh;

                        // Default face list for 3D search (no partition)
                        List<int> defaultSearchFaces = srcFacesChosen;
                        int defaultSearchCount = constrained && defaultSearchFaces != null
                            ? defaultSearchFaces.Count : srcTriCount;

                        // Pre-compute partition face lists for per-vertex 3D search
                        List<int>[] partFaceLists = null;
                        if (hasPartitions && constrained)
                        {
                            partFaceLists = new List<int>[srcPR.partitionCount];
                            for (int pi = 0; pi < srcPR.partitionCount; pi++)
                            {
                                var faces = SpatialPartitioner.GetPartitionFaces(
                                    srcShells[chosenSrc], srcPR, pi);
                                partFaceLists[pi] = new List<int>(faces);
                            }
                        }

                        foreach (int vi in tShell.vertexIndices)
                        {
                            if (vi >= tUv0.Length || vi >= tVerts.Length) continue;
                            Vector2 tUv = tUv0[vi];
                            Vector3 tPos = tVerts[vi];
                            Vector3 tNrm = (tNormals != null && vi < tNormals.Length)
                                ? tNormals[vi] : Vector3.up;

                            // Per-vertex partition selection based on vertex normal
                            TriangleBvh2D vertexBvh = defaultUv0Bvh;
                            int vertexPid = -1;
                            if (hasPartitions)
                            {
                                vertexPid = SpatialPartitioner.MatchPartition(srcPR, tPos, tNrm);
                                if (vertexPid >= 0 && partitionBvh[chosenSrc][vertexPid] != null)
                                    vertexBvh = partitionBvh[chosenSrc][vertexPid];
                            }

                            // ── UV0 projection (primary, with BVH + normal filtering) ──
                            float bestDSqUv0;
                            int bestFUv0; float bestU_uv0, bestV_uv0, bestW_uv0;

                            if (vertexBvh != null)
                            {
                                var hitUv0 = tNrm.sqrMagnitude > 0.5f
                                    ? vertexBvh.FindNearestNormalFiltered(tUv, tNrm, triNormal, kBackfaceDot)
                                    : vertexBvh.FindNearest(tUv);
                                bestFUv0 = hitUv0.faceIndex;
                                bestU_uv0 = hitUv0.u; bestV_uv0 = hitUv0.v; bestW_uv0 = hitUv0.w;
                                bestDSqUv0 = hitUv0.distSq;
                            }
                            else
                            {
                                bestDSqUv0 = float.MaxValue;
                                bestFUv0 = -1; bestU_uv0 = 0; bestV_uv0 = 0; bestW_uv0 = 0;
                                var searchFaces = (partFaceLists != null && vertexPid >= 0)
                                    ? partFaceLists[vertexPid] : defaultSearchFaces;
                                int searchCount = constrained && searchFaces != null
                                    ? searchFaces.Count : srcTriCount;
                                for (int fi = 0; fi < searchCount; fi++)
                                {
                                    int f = constrained ? searchFaces[fi] : fi;
                                    float dSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                                        out float u, out float v, out float w);
                                    if (dSq < bestDSqUv0)
                                    { bestDSqUv0 = dSq; bestFUv0 = f; bestU_uv0 = u; bestV_uv0 = v; bestW_uv0 = w; }
                                    if (bestDSqUv0 < 1e-8f) break;
                                }
                            }

                            // ── 3D projection — also partition-constrained ──
                            var search3DFaces = (partFaceLists != null && vertexPid >= 0)
                                ? partFaceLists[vertexPid] : defaultSearchFaces;
                            int search3DCount = constrained && search3DFaces != null
                                ? search3DFaces.Count : srcTriCount;

                            float bestDSq3D = float.MaxValue;
                            int bestF3D = -1; float bestU_3d = 0, bestV_3d = 0, bestW_3d = 0;
                            for (int fi = 0; fi < search3DCount; fi++)
                            {
                                int f = constrained ? search3DFaces[fi] : fi;
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

                            if (force3D && bestF3D >= 0)
                            {
                                // 3D-primary: always prefer 3D projection to avoid
                                // same-source UV2 overlap with dedup siblings.
                                candidate[vi] = uv2From3D;
                                if (bestFUv0 >= 0)
                                {
                                    float delta = (uv2FromUv0 - uv2From3D).magnitude;
                                    if (delta > kConsistencyThresh) localFixes++;
                                }
                            }
                            else if (bestFUv0 >= 0 && bestF3D >= 0)
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

                        // Guard: reject candidate if its UV2 AABB overlaps with a claimed
                        // source shell's UV2 region. For force3D, also check the assigned
                        // source (occupied by interp winner) and force3D sibling regions.
                        if (!constrained && candidate.Count > 0 && issues < int.MaxValue)
                        {
                            Vector2 candMin = new Vector2(float.MaxValue, float.MaxValue);
                            Vector2 candMax = new Vector2(float.MinValue, float.MinValue);
                            foreach (var kv in candidate)
                            {
                                candMin = Vector2.Min(candMin, kv.Value);
                                candMax = Vector2.Max(candMax, kv.Value);
                            }
                            for (int si = 0; si < srcShells.Count; si++)
                            {
                                if (si == chosenSrc && !force3D) continue; // force3D: check own source too
                                if (!claimed.Contains(si)) continue;
                                if (candMin.x < srcUv2Max[si].x && candMax.x > srcUv2Min[si].x &&
                                    candMin.y < srcUv2Max[si].y && candMax.y > srcUv2Min[si].y)
                                {
                                    issues = int.MaxValue;
                                    break;
                                }
                            }
                            // Check against UV2 regions used by previously placed force3D siblings
                            if (force3D && issues < int.MaxValue)
                            {
                                foreach (var region in force3DUsedRegions)
                                {
                                    if (candMin.x < region.max.x && candMax.x > region.min.x &&
                                        candMin.y < region.max.y && candMax.y > region.min.y)
                                    {
                                        issues = int.MaxValue;
                                        break;
                                    }
                                }
                            }
                        }

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

                    // ── Pass 2: Unified overlap fallback for broken force3D/merged ──
                    // Uses GenerateOverlapCandidates which tries all strategies:
                    // ray+partition, partition-UV0-interp, partition-xform, strip-param, full-xform
                    if (force3D && bestMergedIssues > tShell.faceIndices.Count / 2
                        && chosenSrc >= 0 && srcFacesChosen != null)
                    {
                        var overlapCandidates = GenerateOverlapCandidates(
                            tShell, chosenSrc,
                            tVerts, tNormals, tUv0,
                            srcShells, srcVerts, srcUv0, srcUv2, srcTris,
                            triUv0A, triUv0B, triUv0C,
                            triUv2A, triUv2B, triUv2C, triNormal,
                            shellBvh3D[chosenSrc], shellBvh3DFaceMap[chosenSrc],
                            shellBvh3DFaceNormals[chosenSrc],
                            partitionBvh[chosenSrc],
                            srcPartitions[chosenSrc],
                            partitionXform[chosenSrc],
                            srcTransforms[chosenSrc],
                            srcIsRibbon[chosenSrc], srcRibbonAxis[chosenSrc],
                            srcRibbonAxis2[chosenSrc], srcRibbonCentroid[chosenSrc],
                            srcUv2Min, srcUv2Max, null,
                            kRayMaxDist);

                        var bestOverlap = SelectBestCandidate(
                            overlapCandidates, tShell.faceIndices, tgtTris, tUv0);

                        if (bestOverlap.HasValue && bestOverlap.Value.issues < bestMergedIssues)
                        {
                            bestMergedIssues = bestOverlap.Value.issues;
                            bestMergedUv2 = bestOverlap.Value.uv2;
                            bestMergedConsistencyFixes = 0;
                            bestWasConstrained = true;

                            UvtLog.Info($"[GroupedTransfer]   t{tsi}: overlap fallback " +
                                $"({bestOverlap.Value.method}, {bestOverlap.Value.issues} issues)");
                        }

                        // Also try inverted normal filter as last resort
                        // (catches cases where normals oppose source)
                        if (bestMergedIssues > tShell.faceIndices.Count / 2)
                        {
                            var candidateUv0 = new Dictionary<int, Vector2>();
                            TriangleBvh2D uv0Bvh = shellUv0Bvh[chosenSrc];

                            foreach (int vi in tShell.vertexIndices)
                            {
                                if (vi >= tUv0.Length) continue;
                                Vector2 tUv = tUv0[vi];
                                Vector3 tNrm = (tNormals != null && vi < tNormals.Length)
                                    ? tNormals[vi] : Vector3.up;

                                int bestF = -1;
                                float bestU = 0, bestV = 0, bestW = 0;

                                if (uv0Bvh != null)
                                {
                                    var hit = tNrm.sqrMagnitude > 0.5f
                                        ? uv0Bvh.FindNearestNormalFiltered(tUv, -tNrm, triNormal, kBackfaceDot)
                                        : uv0Bvh.FindNearest(tUv);
                                    bestF = hit.faceIndex;
                                    bestU = hit.u; bestV = hit.v; bestW = hit.w;
                                }
                                else
                                {
                                    float bestDSq = float.MaxValue;
                                    for (int fi = 0; fi < srcFacesChosen.Count; fi++)
                                    {
                                        int f = srcFacesChosen[fi];
                                        float dSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                                            out float u, out float v, out float w);
                                        if (dSq < bestDSq)
                                        { bestDSq = dSq; bestF = f; bestU = u; bestV = v; bestW = w; }
                                        if (bestDSq < 1e-8f) break;
                                    }
                                }

                                if (bestF >= 0)
                                    candidateUv0[vi] = triUv2A[bestF] * bestU
                                                     + triUv2B[bestF] * bestV
                                                     + triUv2C[bestF] * bestW;
                            }

                            int issuesUv0 = CountShellIssues(tShell.faceIndices, tgtTris, tUv0, candidateUv0);
                            if (issuesUv0 < bestMergedIssues)
                            {
                                bestMergedIssues = issuesUv0;
                                bestMergedUv2 = candidateUv0;
                                bestMergedConsistencyFixes = 0;
                                bestWasConstrained = true;

                                UvtLog.Info($"[GroupedTransfer]   t{tsi}: inverted-normal fallback " +
                                    $"({issuesUv0} issues)");
                            }
                        }
                    }

                    // Record force3D shell's UV2 AABB so later force3D siblings avoid it
                    if (force3D && bestMergedUv2 != null && bestMergedUv2.Count > 0)
                    {
                        Vector2 fMin = new Vector2(float.MaxValue, float.MaxValue);
                        Vector2 fMax = new Vector2(float.MinValue, float.MinValue);
                        foreach (var kv in bestMergedUv2)
                        {
                            fMin = Vector2.Min(fMin, kv.Value);
                            fMax = Vector2.Max(fMax, kv.Value);
                        }
                        force3DUsedRegions.Add((fMin, fMax));
                    }

                    // ── Coherence fix: detect outlier vertices outside matched source's UV2 AABB ──
                    // Some vertices (especially in degenerate/mixed shells) escape the matched
                    // source's UV2 region, creating long diagonal lines in UV2 space.
                    // If the majority of vertices are inside the correct region, re-project
                    // the outliers using the matched source's constrained UV0 lookup.
                    if (bestMergedUv2 != null && bestMergedUv2.Count > 1 && chosenSrc >= 0)
                    {
                        const float kUv2Margin = 0.005f;
                        Vector2 sMin = srcUv2Min[chosenSrc];
                        Vector2 sMax = srcUv2Max[chosenSrc];

                        int insideCount = 0;
                        var outsideVerts = new List<int>();
                        foreach (var kv in bestMergedUv2)
                        {
                            Vector2 uv2 = kv.Value;
                            if (uv2.x >= sMin.x - kUv2Margin && uv2.x <= sMax.x + kUv2Margin &&
                                uv2.y >= sMin.y - kUv2Margin && uv2.y <= sMax.y + kUv2Margin)
                                insideCount++;
                            else
                                outsideVerts.Add(kv.Key);
                        }

                        if (outsideVerts.Count > 0 && insideCount > outsideVerts.Count)
                        {
                            var cBvh = fragmentRestrictedBvh.TryGetValue(tsi, out var crBvh)
                                ? crBvh : shellUv0Bvh[chosenSrc];
                            var cFaces = fragmentRestrictedFaces.TryGetValue(tsi, out var crFaces)
                                ? crFaces : srcShells[chosenSrc].faceIndices;
                            int coherenceFixed = 0;

                            foreach (int vi in outsideVerts)
                            {
                                if (vi >= tUv0.Length) continue;
                                Vector2 tUv = tUv0[vi];
                                int bestF = -1;
                                float bestU = 0, bestV = 0, bestW = 0;

                                if (cBvh != null)
                                {
                                    Vector3 tNrm = (tNormals != null && vi < tNormals.Length)
                                        ? tNormals[vi] : Vector3.zero;
                                    var hit = tNrm.sqrMagnitude > 0.5f
                                        ? cBvh.FindNearestNormalFiltered(tUv, tNrm, triNormal, 0.0f)
                                        : cBvh.FindNearest(tUv);
                                    bestF = hit.faceIndex;
                                    bestU = hit.u; bestV = hit.v; bestW = hit.w;
                                }
                                else
                                {
                                    float bestDSq = float.MaxValue;
                                    for (int fi = 0; fi < cFaces.Count; fi++)
                                    {
                                        int f = cFaces[fi];
                                        float dSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                                            out float u, out float v, out float w);
                                        if (dSq < bestDSq)
                                        { bestDSq = dSq; bestF = f; bestU = u; bestV = v; bestW = w; }
                                        if (bestDSq < 1e-8f) break;
                                    }
                                }

                                if (bestF >= 0)
                                {
                                    bestMergedUv2[vi] = triUv2A[bestF] * bestU
                                                      + triUv2B[bestF] * bestV
                                                      + triUv2C[bestF] * bestW;
                                    coherenceFixed++;
                                }
                            }

                            if (coherenceFixed > 0)
                                UvtLog.Info($"[GroupedTransfer]   t{tsi}: coherence fix " +
                                    $"({coherenceFixed}/{outsideVerts.Count} outlier verts " +
                                    $"re-projected to src{chosenSrc})");
                        }
                    }

                    int faceCount = tShell.faceIndices.Count;
                    result.targetShellIssues[tsi] = bestMergedIssues;
                    result.targetShellMethod[tsi] = 2; // merged

                    // Reject gate: if >50% faces have issues, don't write garbage UV2.
                    if (bestMergedIssues > faceCount / 2)
                    {
                        if (force3D && bestMergedUv2 != null && bestMergedUv2.Count > 0)
                        {
                            // Force3D shells: accept degraded UV2 rather than leaving holes.
                            // These were forced to merged+3D by dedup — degenerate UV2 in the
                            // correct region is better than (0,0) which creates lightmap seams.
                            chosenUv2 = bestMergedUv2;
                            result.targetShellStatus[tsi] = ShellStatus.Poor;
                            shellsMerged++;
                            result.consistencyCorrected += bestMergedConsistencyFixes;
                            UvtLog.Warn($"[GroupedTransfer]   t{tsi} force3D-accepted({faceCount}f): " +
                                $"{bestMergedIssues} issues (>{faceCount / 2} threshold) — UV2 written as poor");

                            // ── Edge-based expansion for degenerate UV2 ──
                            // When 3D projection collapses all vertices to a line/point,
                            // expand the degenerate axis using the shell's 3D edge proportions.
                            if (chosenUv2.Count >= 3 && chosenSrc >= 0)
                            {
                                Vector2 dgMin = new Vector2(float.MaxValue, float.MaxValue);
                                Vector2 dgMax = new Vector2(float.MinValue, float.MinValue);
                                foreach (var kv in chosenUv2)
                                {
                                    dgMin = Vector2.Min(dgMin, kv.Value);
                                    dgMax = Vector2.Max(dgMax, kv.Value);
                                }
                                float dgW = dgMax.x - dgMin.x, dgH = dgMax.y - dgMin.y;
                                float dgLong = Mathf.Max(dgW, dgH);
                                float dgShort = Mathf.Min(dgW, dgH);

                                if (dgShort < dgLong * 0.01f && dgLong > 1e-6f)
                                {
                                    bool xIsLong = dgW >= dgH;

                                    // Find vertex pair spanning the non-degenerate axis
                                    int dgA = -1, dgB = -1;
                                    float dgSpan = 0;
                                    var dgKeys = new List<int>(chosenUv2.Keys);
                                    for (int i = 0; i < dgKeys.Count; i++)
                                    for (int j = i + 1; j < dgKeys.Count; j++)
                                    {
                                        float s = xIsLong
                                            ? Mathf.Abs(chosenUv2[dgKeys[i]].x - chosenUv2[dgKeys[j]].x)
                                            : Mathf.Abs(chosenUv2[dgKeys[i]].y - chosenUv2[dgKeys[j]].y);
                                        if (s > dgSpan) { dgSpan = s; dgA = dgKeys[i]; dgB = dgKeys[j]; }
                                    }

                                    if (dgA >= 0 && dgB >= 0 && dgA < tVerts.Length && dgB < tVerts.Length)
                                    {
                                        float width3D = (tVerts[dgB] - tVerts[dgA]).magnitude;
                                        if (width3D > 1e-8f)
                                        {
                                            Vector3 widthDir = (tVerts[dgB] - tVerts[dgA]) / width3D;
                                            Vector3 shellNrm = tgtAvgNormal[tsi];
                                            Vector3 heightDir = Vector3.Cross(shellNrm, widthDir).normalized;
                                            if (heightDir.sqrMagnitude < 0.5f)
                                                heightDir = Vector3.Cross(Vector3.up, widthDir).normalized;

                                            // Project vertices onto height direction
                                            Vector3 c3D = Vector3.zero; int cn3 = 0;
                                            foreach (int vi in tShell.vertexIndices)
                                                if (vi < tVerts.Length) { c3D += tVerts[vi]; cn3++; }
                                            if (cn3 > 0) c3D /= cn3;

                                            float hMin = float.MaxValue, hMax = float.MinValue;
                                            var hProj = new Dictionary<int, float>();
                                            foreach (int vi in tShell.vertexIndices)
                                            {
                                                if (vi >= tVerts.Length || !chosenUv2.ContainsKey(vi)) continue;
                                                float h = Vector3.Dot(tVerts[vi] - c3D, heightDir);
                                                hProj[vi] = h;
                                                hMin = Mathf.Min(hMin, h); hMax = Mathf.Max(hMax, h);
                                            }

                                            float height3D = hMax - hMin;
                                            if (height3D > 1e-8f)
                                            {
                                                // UV2 scale from non-degenerate axis
                                                float uvScale = dgLong / width3D;
                                                float targetShort = height3D * uvScale;

                                                // Clamp to source UV2 shortest edge
                                                Vector2 sMin2 = srcUv2Min[chosenSrc];
                                                Vector2 sMax2 = srcUv2Max[chosenSrc];
                                                float srcShort = Mathf.Min(
                                                    sMax2.x - sMin2.x, sMax2.y - sMin2.y);
                                                targetShort = Mathf.Min(targetShort, srcShort);

                                                float uvCenter = xIsLong
                                                    ? (dgMin.y + dgMax.y) * 0.5f
                                                    : (dgMin.x + dgMax.x) * 0.5f;
                                                float hCenter = (hMin + hMax) * 0.5f;
                                                float hScale = targetShort / height3D;

                                                foreach (int vi in tShell.vertexIndices)
                                                {
                                                    if (!chosenUv2.ContainsKey(vi) ||
                                                        !hProj.ContainsKey(vi)) continue;
                                                    Vector2 uv = chosenUv2[vi];
                                                    float off = (hProj[vi] - hCenter) * hScale;
                                                    if (xIsLong) uv.y = uvCenter + off;
                                                    else         uv.x = uvCenter + off;
                                                    uv.x = Mathf.Clamp(uv.x, sMin2.x, sMax2.x);
                                                    uv.y = Mathf.Clamp(uv.y, sMin2.y, sMax2.y);
                                                    chosenUv2[vi] = uv;
                                                }

                                                UvtLog.Info($"[GroupedTransfer]   t{tsi}: " +
                                                    $"edge-based UV2 expansion " +
                                                    $"(height3D={height3D:F4}, " +
                                                    $"uvExpand={targetShort:F6})");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            chosenUv2 = null; // prevent write
                            result.targetShellStatus[tsi] = ShellStatus.Rejected;
                            result.shellsRejected++;
                            shellsMerged++;
                            result.consistencyCorrected += bestMergedConsistencyFixes;
                            UvtLog.Warn($"[GroupedTransfer]   t{tsi} REJECTED({faceCount}f): " +
                                $"{bestMergedIssues} issues (>{faceCount / 2} threshold) — UV2 not written");
                        }
                    }
                    else
                    {
                        chosenUv2 = bestMergedUv2;
                        shellsMerged++;
                        result.consistencyCorrected += bestMergedConsistencyFixes;
                        string mergedLabel = force3D ? "3D-primary"
                            : (bestWasConstrained ? "src-constrained" : "all-source");
                        UvtLog.Info($"[GroupedTransfer]   t{tsi} merged({faceCount}f): " +
                            $"{mergedLabel} ({bestMergedIssues} issues)");
                    }
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

                        // Penalize xform if it extrapolates beyond source shell's UV2 bounds.
                        // Extrapolation is the primary cause of cross-source UV2 overlaps,
                        // since interp stays within source UV2 convex hull by construction.
                        const float kOobMargin = 0.005f;
                        Vector2 srcBMin2 = srcUv2Min[chosenSrc];
                        Vector2 srcBMax2 = srcUv2Max[chosenSrc];
                        Vector2 xfBMin = new Vector2(float.MaxValue, float.MaxValue);
                        Vector2 xfBMax = new Vector2(float.MinValue, float.MinValue);
                        int xfOob = 0;
                        foreach (var kv in uv2_transform)
                        {
                            Vector2 uv = kv.Value;
                            xfBMin = Vector2.Min(xfBMin, uv);
                            xfBMax = Vector2.Max(xfBMax, uv);
                            if (uv.x < srcBMin2.x - kOobMargin || uv.x > srcBMax2.x + kOobMargin ||
                                uv.y < srcBMin2.y - kOobMargin || uv.y > srcBMax2.y + kOobMargin)
                                xfOob++;
                        }
                        if (xfOob > 0)
                        {
                            // Check if extrapolated region crosses into another source
                            // shell's UV2 AABB — if so, force interp to prevent overlap.
                            bool crossesOther = false;
                            for (int si = 0; si < srcShells.Count; si++)
                            {
                                if (si == chosenSrc) continue;
                                if (xfBMin.x < srcUv2Max[si].x && xfBMax.x > srcUv2Min[si].x &&
                                    xfBMin.y < srcUv2Max[si].y && xfBMax.y > srcUv2Min[si].y)
                                {
                                    crossesOther = true;
                                    issuesTransform = int.MaxValue;
                                    break;
                                }
                            }
                            if (!crossesOther)
                            {
                                // Doesn't cross another shell, but still OOB — clamp to source AABB
                                Vector2 cMin = srcBMin2 - new Vector2(kOobMargin, kOobMargin);
                                Vector2 cMax = srcBMax2 + new Vector2(kOobMargin, kOobMargin);
                                var oobKeys = new List<int>();
                                foreach (var kv in uv2_transform)
                                {
                                    Vector2 uv = kv.Value;
                                    if (uv.x < cMin.x || uv.x > cMax.x || uv.y < cMin.y || uv.y > cMax.y)
                                        oobKeys.Add(kv.Key);
                                }
                                foreach (int vi in oobKeys)
                                {
                                    Vector2 uv = uv2_transform[vi];
                                    uv.x = Mathf.Clamp(uv.x, cMin.x, cMax.x);
                                    uv.y = Mathf.Clamp(uv.y, cMin.y, cMax.y);
                                    uv2_transform[vi] = uv;
                                }
                                issuesTransform += xfOob;
                            }
                        }
                    }

                    // Candidate B: per-vertex UV0 interpolation (BVH + normal filtering for thin details)
                    var uv2_interp = new Dictionary<int, Vector2>();
                    // Use fragment-restricted BVH when available (shared source with
                    // non-overlapping UV0 fragments) to prevent cross-fragment UV2 bleed
                    var srcBvh = fragmentRestrictedBvh.TryGetValue(tsi, out var rBvh)
                        ? rBvh : shellUv0Bvh[chosenSrc]; // may be null for small shells
                    foreach (int vi in tShell.vertexIndices)
                    {
                        if (vi >= tUv0.Length) continue;
                        Vector2 tUv = tUv0[vi];
                        int bestF; float bestU, bestV, bestW;

                        if (srcBvh != null)
                        {
                            // BVH lookup with normal filtering for thin wall disambiguation
                            Vector3 tNrm = (tNormals != null && vi < tNormals.Length)
                                ? tNormals[vi] : Vector3.zero;
                            TriangleBvh2D.HitResult2D hit;
                            if (tNrm.sqrMagnitude > 0.5f)
                                hit = srcBvh.FindNearestNormalFiltered(tUv, tNrm, triNormal, kNormalDotMin);
                            else
                                hit = srcBvh.FindNearest(tUv);
                            bestF = hit.faceIndex; bestU = hit.u; bestV = hit.v; bestW = hit.w;
                        }
                        else
                        {
                            // Linear scan for small shells (with inline normal check)
                            float bestDSq = float.MaxValue;
                            float bestDSqFiltered = float.MaxValue;
                            bestF = -1; bestU = 0; bestV = 0; bestW = 0;
                            int bestFFiltered = -1;
                            float bestUF = 0, bestVF = 0, bestWF = 0;
                            Vector3 tNrm = (tNormals != null && vi < tNormals.Length)
                                ? tNormals[vi] : Vector3.zero;
                            bool hasNrm = tNrm.sqrMagnitude > 0.5f;
                            for (int fi = 0; fi < srcFacesChosen.Count; fi++)
                            {
                                int f = srcFacesChosen[fi];
                                float dSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                                    out float u, out float v, out float w);
                                if (dSq < bestDSq)
                                { bestDSq = dSq; bestF = f; bestU = u; bestV = v; bestW = w; }
                                if (hasNrm && Vector3.Dot(triNormal[f], tNrm) >= kNormalDotMin && dSq < bestDSqFiltered)
                                { bestDSqFiltered = dSq; bestFFiltered = f; bestUF = u; bestVF = v; bestWF = w; }
                            }
                            // Prefer normal-filtered result
                            if (bestFFiltered >= 0)
                            { bestF = bestFFiltered; bestU = bestUF; bestV = bestVF; bestW = bestWF; }
                        }

                        if (bestF >= 0)
                            uv2_interp[vi] = triUv2A[bestF] * bestU
                                           + triUv2B[bestF] * bestV
                                           + triUv2C[bestF] * bestW;
                    }

                    int issuesInterp = CountShellIssues(tShell.faceIndices, tgtTris, tUv0, uv2_interp);

                    if (uv2_transform != null && issuesTransform < issuesInterp)
                    {
                        chosenUv2 = uv2_transform;
                        shellsTransform++;
                        result.targetShellMethod[tsi] = 1; // xform
                        result.targetShellIssues[tsi] = issuesTransform;
                    }
                    else
                    {
                        chosenUv2 = uv2_interp;
                        shellsInterpolation++;
                        result.targetShellMethod[tsi] = 0; // interp
                        result.targetShellIssues[tsi] = issuesInterp;
                    }
                }

                // Write chosen UV2
                int srcForLog = chosenSrc >= 0 ? chosenSrc : -1;
                if (chosenUv2 != null)
                {
                    foreach (var kv in chosenUv2)
                    {
                        result.uv2[kv.Key] = kv.Value;
                        result.vertexToSourceShell[kv.Key] = srcForLog;
                        transferred++;
                    }

                    // Claim source for overlap group dedup — prevents other
                    // targets from using the same source and creating UV2 overlap.
                    if (chosenSrc >= 0 && srcShellOverlapMembers[chosenSrc] != null)
                        overlapClaimedSources.Add(chosenSrc);
                }
            }

            // ── Post-transfer UV2 AABB safety net for merged shells ──
            // Catch any vertex whose UV2 escaped the matched source's UV2 region.
            // This can happen through multiple code paths (overlap composite, standard
            // merged all-source fallback, 3D projection, etc.). Re-project outlier
            // vertices using the source's UV0 BVH to bring them back into the correct region.
            {
                int totalOutlierVerts = 0;
                for (int tsi = 0; tsi < tgtShells.Count; tsi++)
                {
                    if (result.targetShellMethod[tsi] != 2) continue; // only merged shells
                    int src = result.targetShellToSourceShell[tsi];
                    if (src < 0) continue;

                    Vector2 sMin = srcUv2Min[src];
                    Vector2 sMax = srcUv2Max[src];
                    Vector2 sSize = sMax - sMin;
                    float padX = sSize.x * 0.2f;
                    float padY = sSize.y * 0.2f;
                    Vector2 allowMin = sMin - new Vector2(padX, padY);
                    Vector2 allowMax = sMax + new Vector2(padX, padY);

                    var tShell = tgtShells[tsi];
                    int shellOutliers = 0;

                    foreach (int vi in tShell.vertexIndices)
                    {
                        if (vi >= result.uv2.Length) continue;
                        Vector2 uv = result.uv2[vi];
                        if (uv.x >= allowMin.x && uv.x <= allowMax.x &&
                            uv.y >= allowMin.y && uv.y <= allowMax.y)
                            continue; // within bounds

                        // Outlier: re-project from source UV0 BVH
                        if (vi < tUv0.Length && shellUv0Bvh[src] != null)
                        {
                            Vector3 tNrm = (tNormals != null && vi < tNormals.Length)
                                ? tNormals[vi] : Vector3.up;
                            TriangleBvh2D.HitResult2D hit;
                            if (tNrm.sqrMagnitude > 0.5f)
                                hit = shellUv0Bvh[src].FindNearestNormalFiltered(
                                    tUv0[vi], tNrm, triNormal, 0.0f);
                            else
                                hit = shellUv0Bvh[src].FindNearest(tUv0[vi]);

                            if (hit.faceIndex >= 0)
                            {
                                int f = hit.faceIndex;
                                result.uv2[vi] = triUv2A[f] * hit.u + triUv2B[f] * hit.v + triUv2C[f] * hit.w;
                                shellOutliers++;
                            }
                        }
                    }

                    if (shellOutliers > 0)
                    {
                        UvtLog.Info($"[GroupedTransfer] Post-fix: t{tsi} — {shellOutliers} outlier " +
                            $"verts re-projected to src{src} UV2 AABB");
                        totalOutlierVerts += shellOutliers;
                    }
                }
                if (totalOutlierVerts > 0)
                    UvtLog.Info($"[GroupedTransfer] Post-fix total: {totalOutlierVerts} outlier verts corrected");
            }

            // ── Classify all shells ──
            int statusAccepted = 0, statusDegraded = 0, statusPoor = 0, statusRejected = 0, statusUnmatched = 0;
            for (int tsi = 0; tsi < tgtShells.Count; tsi++)
            {
                // Already classified as Rejected in the merge gate above
                if (result.targetShellStatus[tsi] == ShellStatus.Rejected)
                {
                    statusRejected++;
                    continue;
                }

                int src = result.targetShellToSourceShell[tsi];
                if (src < 0)
                {
                    result.targetShellStatus[tsi] = ShellStatus.Unmatched;
                    statusUnmatched++;
                    continue;
                }

                int issues = result.targetShellIssues[tsi];
                int faceCount = tgtShells[tsi].faceIndices.Count;
                float ratio = faceCount > 0 ? (float)issues / faceCount : 0f;

                if (issues == 0)
                {
                    result.targetShellStatus[tsi] = ShellStatus.Accepted;
                    statusAccepted++;
                }
                else if (ratio <= 0.3f)
                {
                    result.targetShellStatus[tsi] = ShellStatus.Degraded;
                    statusDegraded++;
                }
                else
                {
                    result.targetShellStatus[tsi] = ShellStatus.Poor;
                    statusPoor++;
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
            // Shell quality summary
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"[GroupedTransfer] Quality: {statusAccepted} accepted");
                if (statusDegraded > 0) sb.Append($", {statusDegraded} degraded");
                if (statusPoor > 0) sb.Append($", {statusPoor} poor");
                if (statusRejected > 0) sb.Append($", {statusRejected} rejected");
                if (statusUnmatched > 0) sb.Append($", {statusUnmatched} unmatched");
                if (statusRejected > 0 || statusPoor > 0)
                    UvtLog.Warn(sb.ToString());
                else
                    UvtLog.Info(sb.ToString());
            }

            // Per-shell UV2 fingerprint: hash of UV2 values for cross-branch comparison.
            // Logs centroid + hash so users can diff logs between branches to find
            // which specific shells produce different UV2.
            {
                var fpSb = new System.Text.StringBuilder();
                fpSb.Append($"[GroupedTransfer] UV2 fingerprint '{targetMesh.name}':");
                for (int tsi2 = 0; tsi2 < tgtShells.Count; tsi2++)
                {
                    var shell = tgtShells[tsi2];
                    float sumX = 0, sumY = 0;
                    int cnt = 0;
                    uint hash = 2166136261u;
                    foreach (int vi in shell.vertexIndices)
                    {
                        if (vi >= result.uv2.Length) continue;
                        var uv = result.uv2[vi];
                        sumX += uv.x; sumY += uv.y; cnt++;
                        // Quantize to 5 decimal places for stable hash
                        int qx = Mathf.RoundToInt(uv.x * 100000f);
                        int qy = Mathf.RoundToInt(uv.y * 100000f);
                        unchecked
                        {
                            hash = (hash ^ (uint)qx) * 16777619u;
                            hash = (hash ^ (uint)qy) * 16777619u;
                        }
                    }
                    if (cnt > 0)
                        fpSb.Append($" t{tsi2}={hash:X8}({sumX / cnt:F4},{sumY / cnt:F4})");
                }
                UvtLog.Info(fpSb.ToString());
            }

            // ── Shell topology consistency: detect & fix displaced vertices ──
            EnforceShellTopologyOnUv2(result.uv2, tVerts, tgtTris, tgtShells);

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

            // Populate cross-LOD overlap hints for subsequent LODs.
            // Records (3D centroid, source shell) for each merged shell so the next
            // LOD can match the same physical belt/strap to the same source.
            var hints = new List<OverlapSourceHint>();
            for (int tsi = 0; tsi < tgtShells.Count; tsi++)
            {
                if (result.targetShellMethod[tsi] == 2 && result.targetShellToSourceShell[tsi] >= 0)
                {
                    hints.Add(new OverlapSourceHint
                    {
                        centroid3D = result.targetShellCentroids[tsi],
                        sourceShellIndex = result.targetShellToSourceShell[tsi]
                    });
                }
            }
            result.overlapHints = hints;

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  Shell topology consistency: detect & fix displaced vertices
        //  Works on raw UV2 array + UvShell list (independent of TargetTransferState)
        // ═══════════════════════════════════════════════════════════

        static void EnforceShellTopologyOnUv2(
            Vector2[] uv2, Vector3[] verts, int[] triangles, List<UvShell> shells)
        {
            if (uv2 == null || uv2.Length == 0) return;

            int faceCount = triangles.Length / 3;

            // Build adjacency and face lists
            var vertNeighbors = new Dictionary<int, HashSet<int>>();
            var vertexToFaces = new Dictionary<int, List<int>>();

            for (int f = 0; f < faceCount; f++)
            {
                for (int j = 0; j < 3; j++)
                {
                    int vi = triangles[f * 3 + j];

                    if (!vertexToFaces.TryGetValue(vi, out var fList))
                    {
                        fList = new List<int>();
                        vertexToFaces[vi] = fList;
                    }
                    fList.Add(f);

                    int v0 = vi;
                    int v1 = triangles[f * 3 + (j + 1) % 3];

                    if (!vertNeighbors.TryGetValue(v0, out var n0))
                    {
                        n0 = new HashSet<int>();
                        vertNeighbors[v0] = n0;
                    }
                    n0.Add(v1);

                    if (!vertNeighbors.TryGetValue(v1, out var n1))
                    {
                        n1 = new HashSet<int>();
                        vertNeighbors[v1] = n1;
                    }
                    n1.Add(v0);
                }
            }

            // Iterative Laplacian displacement detection (max 3 passes)
            int totalFixed = 0;
            const float displacementThreshold = 2.0f;

            for (int iteration = 0; iteration < 3; iteration++)
            {
                var candidates = new List<(int vi, float ratio)>();

                foreach (var kv in vertNeighbors)
                {
                    int vi = kv.Key;
                    var neighbors = kv.Value;

                    if (neighbors.Count < 2) continue;
                    if (vi >= uv2.Length) continue;

                    Vector2 actualUv = uv2[vi];
                    if (float.IsNaN(actualUv.x) || float.IsNaN(actualUv.y)) continue;

                    // Laplacian prediction
                    Vector2 predicted = Vector2.zero;
                    float totalW = 0f;
                    foreach (int ni in neighbors)
                    {
                        float len3D = (verts[vi] - verts[ni]).magnitude;
                        float w = 1f / Mathf.Max(len3D, 1e-6f);
                        predicted += uv2[ni] * w;
                        totalW += w;
                    }
                    if (totalW < 1e-6f) continue;
                    predicted /= totalW;

                    float displacement = (actualUv - predicted).magnitude;

                    // Local scale: average neighbor-to-neighbor UV edge length (excluding vi)
                    float neighborEdgeSum = 0f;
                    int neighborEdgeCount = 0;
                    foreach (int ni in neighbors)
                    {
                        if (!vertNeighbors.TryGetValue(ni, out var niN)) continue;
                        foreach (int nni in niN)
                        {
                            if (nni == vi) continue;
                            neighborEdgeSum += (uv2[ni] - uv2[nni]).magnitude;
                            neighborEdgeCount++;
                        }
                    }
                    if (neighborEdgeCount == 0) continue;
                    float avgNeighborEdge = neighborEdgeSum / neighborEdgeCount;
                    if (avgNeighborEdge < 1e-8f) continue;

                    float ratio = displacement / avgNeighborEdge;
                    if (ratio > displacementThreshold)
                        candidates.Add((vi, ratio));
                }

                candidates.Sort((a, b) => b.ratio.CompareTo(a.ratio));

                int fixedThisPass = 0;
                foreach (var (vi, _) in candidates)
                {
                    var neighbors = vertNeighbors[vi];

                    // Recompute after previous fixes
                    Vector2 predicted = Vector2.zero;
                    float totalW = 0f;
                    foreach (int ni in neighbors)
                    {
                        float len3D = (verts[vi] - verts[ni]).magnitude;
                        float w = 1f / Mathf.Max(len3D, 1e-6f);
                        predicted += uv2[ni] * w;
                        totalW += w;
                    }
                    if (totalW < 1e-6f) continue;
                    predicted /= totalW;

                    float displacement = (uv2[vi] - predicted).magnitude;

                    float neighborEdgeSum = 0f;
                    int neighborEdgeCount = 0;
                    foreach (int ni in neighbors)
                    {
                        if (!vertNeighbors.TryGetValue(ni, out var niN)) continue;
                        foreach (int nni in niN)
                        {
                            if (nni == vi) continue;
                            neighborEdgeSum += (uv2[ni] - uv2[nni]).magnitude;
                            neighborEdgeCount++;
                        }
                    }
                    if (neighborEdgeCount == 0) continue;
                    float avgNeighborEdge = neighborEdgeSum / neighborEdgeCount;
                    if (avgNeighborEdge < 1e-8f) continue;

                    if (displacement / avgNeighborEdge <= displacementThreshold) continue;

                    // Check neighbors are consistent
                    int consistentCount = 0;
                    foreach (int ni in neighbors)
                    {
                        if (!vertNeighbors.TryGetValue(ni, out var niN)) continue;
                        Vector2 niPred = Vector2.zero;
                        float niW = 0f;
                        foreach (int nni in niN)
                        {
                            if (nni == vi) continue;
                            float len3D = (verts[ni] - verts[nni]).magnitude;
                            float w = 1f / Mathf.Max(len3D, 1e-6f);
                            niPred += uv2[nni] * w;
                            niW += w;
                        }
                        if (niW < 1e-6f) continue;
                        niPred /= niW;
                        float niDisp = (uv2[ni] - niPred).magnitude;
                        if (niDisp < avgNeighborEdge * displacementThreshold)
                            consistentCount++;
                    }

                    if (consistentCount < (neighbors.Count + 1) / 2) continue;

                    // No triangle flip
                    bool wouldFlip = false;
                    if (vertexToFaces.TryGetValue(vi, out var facesOfVi))
                    {
                        foreach (int f in facesOfVi)
                        {
                            int i0 = triangles[f * 3];
                            int i1 = triangles[f * 3 + 1];
                            int i2 = triangles[f * 3 + 2];

                            Vector2 a = i0 == vi ? predicted : uv2[i0];
                            Vector2 b = i1 == vi ? predicted : uv2[i1];
                            Vector2 c = i2 == vi ? predicted : uv2[i2];

                            float areaBefore = (uv2[i1].x - uv2[i0].x) * (uv2[i2].y - uv2[i0].y) -
                                                (uv2[i1].y - uv2[i0].y) * (uv2[i2].x - uv2[i0].x);
                            float areaAfter = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

                            if (Mathf.Abs(areaBefore) > 1e-9f && Mathf.Abs(areaAfter) > 1e-9f &&
                                Mathf.Sign(areaBefore) != Mathf.Sign(areaAfter))
                            {
                                wouldFlip = true;
                                break;
                            }
                        }
                    }
                    if (wouldFlip) continue;

                    UvtLog.Info($"[ShellTopology] iter={iteration} Fixed vertex {vi}: " +
                        $"({uv2[vi].x:F4},{uv2[vi].y:F4}) → ({predicted.x:F4},{predicted.y:F4}) " +
                        $"disp/scale={displacement / avgNeighborEdge:F2}");

                    uv2[vi] = predicted;
                    fixedThisPass++;
                }

                totalFixed += fixedThisPass;
                if (fixedThisPass == 0) break;
            }

            if (totalFixed > 0)
            {
                UvtLog.Info($"[GroupedTransfer] Shell topology enforcement fixed {totalFixed} displaced vertices");
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  Count triangle issues (inverted + zero-area) for a UV2 candidate
        //  Used to pick between transform and interpolation per shell
        // ═══════════════════════════════════════════════════════════

        static Vector3 ComputeShellAvgNormal(UvShell shell, Vector3[] normals)
        {
            if (normals == null || normals.Length == 0) return Vector3.up;
            Vector3 sum = Vector3.zero;
            foreach (int vi in shell.vertexIndices)
            {
                if (vi < normals.Length)
                    sum += normals[vi];
            }
            return sum.sqrMagnitude > 1e-8f ? sum.normalized : Vector3.up;
        }

        static int CountShellIssues(List<int> faceIndices, int[] tris, Vector2[] uv0,
            Dictionary<int, Vector2> candidateUv2)
        {
            // Only count missing UV2 and degenerate faces as issues.
            // Winding flip (UV0 vs UV2 opposite sign) is normal — UV0 and UV2 are
            // independent coordinate spaces, so different winding is expected.
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
            }
            return issues;
        }

        static float ComputeShellStretchMedian(List<int> faceIndices, int[] tris, Vector2[] uv0,
            Dictionary<int, Vector2> candidateUv2)
        {
            if (candidateUv2 == null || candidateUv2.Count == 0)
                return float.NaN;

            var ratios = new List<float>(faceIndices.Count);
            foreach (int f in faceIndices)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                if (i0 >= uv0.Length || i1 >= uv0.Length || i2 >= uv0.Length) continue;
                if (!candidateUv2.TryGetValue(i0, out var a2) ||
                    !candidateUv2.TryGetValue(i1, out var b2) ||
                    !candidateUv2.TryGetValue(i2, out var c2))
                    continue;

                var a0 = uv0[i0]; var b0 = uv0[i1]; var c0 = uv0[i2];
                float area0 = Mathf.Abs(SignedArea2D(a0, b0, c0));
                if (area0 < 1e-10f) continue;

                float area2 = Mathf.Abs(SignedArea2D(a2, b2, c2));
                ratios.Add(area2 / area0);
            }

            if (ratios.Count == 0)
                return float.NaN;

            ratios.Sort();
            return ratios[ratios.Count / 2];
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

        /// <summary>
        /// Affine UV0→UV2 map through a single source triangle with extrapolation limit.
        /// Computes raw barycentric coords in source UV0 triangle and applies them
        /// to source UV2 triangle. When any barycentric exceeds the extrapolation
        /// limit, clamps all three to prevent thin-shell UV2 from crossing into
        /// neighboring shells.  Preserves winding by construction.
        /// </summary>
        const float kAffineMaxExtrap = 0.5f;
        static Vector2 AffineUv0ToUv2(Vector2 p, Vector2 a0, Vector2 b0, Vector2 c0,
            Vector2 a2, Vector2 b2, Vector2 c2, float invDet)
        {
            float dx = p.x - a0.x, dy = p.y - a0.y;
            float bx = b0.x - a0.x, by = b0.y - a0.y;
            float cx = c0.x - a0.x, cy = c0.y - a0.y;
            float u = (dx * cy - cx * dy) * invDet; // weight for b0/b2
            float v = (bx * dy - dx * by) * invDet; // weight for c0/c2
            float w = 1f - u - v;                   // weight for a0/a2

            // Limit extrapolation to prevent thin-strip overlap
            float minB = Mathf.Min(w, Mathf.Min(u, v));
            float maxB = Mathf.Max(w, Mathf.Max(u, v));
            if (minB < -kAffineMaxExtrap || maxB > 1f + kAffineMaxExtrap)
            {
                // Clamp barycentrics and renormalize
                u = Mathf.Clamp(u, -kAffineMaxExtrap, 1f + kAffineMaxExtrap);
                v = Mathf.Clamp(v, -kAffineMaxExtrap, 1f + kAffineMaxExtrap);
                w = 1f - u - v;
                // If w is also out of range, renormalize all
                if (w < -kAffineMaxExtrap || w > 1f + kAffineMaxExtrap)
                {
                    w = Mathf.Clamp(w, -kAffineMaxExtrap, 1f + kAffineMaxExtrap);
                    float s = u + v + w;
                    if (Mathf.Abs(s) > 1e-8f) { float inv = 1f / s; u *= inv; v *= inv; w *= inv; }
                }
            }

            return a2 * w + b2 * u + c2 * v;
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

        // ═══════════════════════════════════════════════════════════
        //  Unified overlap candidate generation
        //  Generates ALL candidate UV2 maps for a single source shell
        //  and returns them ranked by quality (issues count).
        // ═══════════════════════════════════════════════════════════

        struct OverlapCandidate
        {
            public Dictionary<int, Vector2> uv2;
            public int issues;
            public int coverage;
            public string method;
        }

        /// <summary>
        /// Generate all overlap-aware UV2 candidates for a target shell
        /// projected onto a single source shell. Tries (in order):
        ///   1. Ray/nearest with partition filtering (3D→UV2)
        ///   2. Partition-constrained UV0 interpolation (UV0→UV2 via partition BVH)
        ///   3. Per-partition similarity transform (UV0→UV2 via partition xform)
        ///   4. Strip parameterization (3D param space→UV2, normal-filtered)
        ///   5. Full-shell similarity transform with cross-source UV2 guard
        /// Returns candidates sorted by quality (ascending issues, descending coverage).
        /// </summary>
        static List<OverlapCandidate> GenerateOverlapCandidates(
            UvShell tShell, int srcIdx,
            // Target mesh data
            Vector3[] tVerts, Vector3[] tNormals, Vector2[] tUv0,
            // Source mesh data
            List<UvShell> srcShells, Vector3[] srcVerts,
            Vector2[] srcUv0, Vector2[] srcUv2, int[] srcTris,
            // Pre-computed triangle arrays
            Vector2[] triUv0A, Vector2[] triUv0B, Vector2[] triUv0C,
            Vector2[] triUv2A, Vector2[] triUv2B, Vector2[] triUv2C,
            Vector3[] triNormal,
            // BVH structures
            TriangleBvh srcBvh3D, int[] faceMap3D, Vector3[] faceNormals3D,
            TriangleBvh2D[] perPartBvh,
            // Partition & transform data
            SpatialPartitioner.ShellPartitionResult srcPR,
            SimilarityTransform[] perPartXform,
            SimilarityTransform fullXform,
            // Ribbon data
            bool isRibbon, Vector3 ribbonAxis, Vector3 ribbonAxis2, Vector3 ribbonCentroid,
            // Cross-source UV2 guard data
            Vector2[] srcUv2Min, Vector2[] srcUv2Max, List<int> overlapGroupMembers,
            // Thresholds
            float kRayMaxDist)
        {
            var candidates = new List<OverlapCandidate>();
            int[] tgtTris = null; // not needed — issues counted by caller

            bool hasPart = srcPR != null && srcPR.hasOverlap && srcPR.partitionCount > 1
                && perPartBvh != null;

            // ── Candidate 1: Ray/nearest with partition filtering ──
            if (srcBvh3D != null)
            {
                var uv2Map = new Dictionary<int, Vector2>();

                foreach (int vi in tShell.vertexIndices)
                {
                    if (vi >= tVerts.Length) continue;
                    Vector3 tPos = tVerts[vi];
                    Vector3 tNrm = (tNormals != null && vi < tNormals.Length)
                        ? tNormals[vi] : Vector3.up;

                    int vertexPid = -1;
                    if (hasPart)
                        vertexPid = SpatialPartitioner.MatchPartition(srcPR, tPos, tNrm);

                    int hitFace = -1;
                    Vector3 hitBary = Vector3.zero;

                    // Primary: ray along normal (bidirectional) with back-face culling
                    if (tNrm.sqrMagnitude > 0.5f)
                    {
                        var rayHit = srcBvh3D.RaycastBidirectional(
                            tPos, tNrm.normalized, kRayMaxDist);
                        if (rayHit.triangleIndex >= 0)
                        {
                            int gf = (rayHit.triangleIndex < faceMap3D.Length)
                                ? faceMap3D[rayHit.triangleIndex] : -1;
                            bool partOk = true;
                            // Back-face culling: reject hits on opposite-facing triangles
                            if (gf >= 0 && gf < triNormal.Length
                                && Vector3.Dot(triNormal[gf], tNrm) <= 0f)
                                partOk = false;
                            if (vertexPid >= 0 && gf >= 0 &&
                                srcPR.facePartitionId.TryGetValue(gf, out int fp) &&
                                fp != vertexPid)
                                partOk = false;

                            if (partOk)
                            {
                                hitFace = rayHit.triangleIndex;
                                hitBary = rayHit.barycentric;
                            }
                        }
                    }

                    // Fallback: nearest-point with normal filter (dot > 0.3)
                    if (hitFace < 0 && faceNormals3D != null)
                    {
                        var nearest = srcBvh3D.FindNearestNormalFiltered(
                            tPos, tNrm, faceNormals3D, 0.3f);
                        if (nearest.triangleIndex >= 0)
                        {
                            int gf = (nearest.triangleIndex < faceMap3D.Length)
                                ? faceMap3D[nearest.triangleIndex] : -1;
                            bool partOk = true;
                            if (vertexPid >= 0 && gf >= 0 &&
                                srcPR.facePartitionId.TryGetValue(gf, out int fp2) &&
                                fp2 != vertexPid)
                                partOk = false;

                            if (partOk)
                            {
                                hitFace = nearest.triangleIndex;
                                hitBary = nearest.barycentric;
                            }
                        }
                    }

                    // Partition-constrained UV0-interp fallback for rejected vertices
                    if (hitFace < 0 && vertexPid >= 0 && vi < tUv0.Length
                        && perPartBvh[vertexPid] != null)
                    {
                        var hit = perPartBvh[vertexPid].FindNearest(tUv0[vi]);
                        if (hit.faceIndex >= 0)
                        {
                            uv2Map[vi] = triUv2A[hit.faceIndex] * hit.u
                                       + triUv2B[hit.faceIndex] * hit.v
                                       + triUv2C[hit.faceIndex] * hit.w;
                            continue;
                        }
                    }

                    if (hitFace >= 0 && hitFace < faceMap3D.Length)
                    {
                        int globalFace = faceMap3D[hitFace];
                        uv2Map[vi] = triUv2A[globalFace] * hitBary.x
                                   + triUv2B[globalFace] * hitBary.y
                                   + triUv2C[globalFace] * hitBary.z;
                    }
                }

                if (uv2Map.Count > 0)
                    candidates.Add(new OverlapCandidate
                    {
                        uv2 = uv2Map, issues = -1, coverage = uv2Map.Count,
                        method = "ray+partition"
                    });
            }

            // ── Candidate 2: Partition-constrained UV0 interpolation ──
            if (hasPart)
            {
                var uv2Map = new Dictionary<int, Vector2>();

                foreach (int vi in tShell.vertexIndices)
                {
                    if (vi >= tUv0.Length || vi >= tVerts.Length) continue;
                    Vector3 tNrm = (tNormals != null && vi < tNormals.Length)
                        ? tNormals[vi] : Vector3.up;
                    Vector3 tPos = tVerts[vi];

                    int pid = SpatialPartitioner.MatchPartition(srcPR, tPos, tNrm);
                    if (pid < 0 || perPartBvh[pid] == null) continue;

                    var hit = perPartBvh[pid].FindNearest(tUv0[vi]);
                    if (hit.faceIndex >= 0)
                    {
                        uv2Map[vi] = triUv2A[hit.faceIndex] * hit.u
                                   + triUv2B[hit.faceIndex] * hit.v
                                   + triUv2C[hit.faceIndex] * hit.w;
                    }
                }

                if (uv2Map.Count > 0)
                    candidates.Add(new OverlapCandidate
                    {
                        uv2 = uv2Map, issues = -1, coverage = uv2Map.Count,
                        method = "partition-uv0-interp"
                    });
            }

            // ── Candidate 3: Per-partition similarity transform ──
            if (hasPart && perPartXform != null)
            {
                var uv2Map = new Dictionary<int, Vector2>();

                foreach (int vi in tShell.vertexIndices)
                {
                    if (vi >= tUv0.Length || vi >= tVerts.Length) continue;
                    Vector3 tNrm = (tNormals != null && vi < tNormals.Length)
                        ? tNormals[vi] : Vector3.up;
                    Vector3 tPos = tVerts[vi];

                    int pid = SpatialPartitioner.MatchPartition(srcPR, tPos, tNrm);
                    if (pid < 0 || !perPartXform[pid].valid) continue;

                    uv2Map[vi] = perPartXform[pid].Apply(tUv0[vi]);
                }

                // Reject if any vertex goes outside 0-1 range
                bool partXfRejected = false;
                if (uv2Map.Count > 0)
                {
                    Vector2 pxMin = new Vector2(float.MaxValue, float.MaxValue);
                    Vector2 pxMax = new Vector2(float.MinValue, float.MinValue);
                    foreach (var kv in uv2Map)
                    {
                        Vector2 uv = kv.Value;
                        if (uv.x < -0.01f || uv.x > 1.01f || uv.y < -0.01f || uv.y > 1.01f)
                        {
                            partXfRejected = true;
                            break;
                        }
                        pxMin = Vector2.Min(pxMin, uv);
                        pxMax = Vector2.Max(pxMax, uv);
                    }

                    // Reject if result extends too far beyond source's UV2 AABB.
                    // Transform can extrapolate when target UV0 differs from source UV0,
                    // placing vertices outside the source's UV2 region → protrusions.
                    if (!partXfRejected && srcUv2Min != null && srcUv2Max != null)
                    {
                        Vector2 sMin = srcUv2Min[srcIdx];
                        Vector2 sMax = srcUv2Max[srcIdx];
                        Vector2 sSize = sMax - sMin;
                        float margin = Mathf.Max(sSize.x, sSize.y) * 0.3f;
                        if (pxMin.x < sMin.x - margin || pxMax.x > sMax.x + margin ||
                            pxMin.y < sMin.y - margin || pxMax.y > sMax.y + margin)
                            partXfRejected = true;
                    }

                    // Reject if result overlaps another overlap-group member's UV2 region
                    if (!partXfRejected && overlapGroupMembers != null)
                    {
                        foreach (int si in overlapGroupMembers)
                        {
                            if (si == srcIdx) continue;
                            if (pxMin.x < srcUv2Max[si].x && pxMax.x > srcUv2Min[si].x &&
                                pxMin.y < srcUv2Max[si].y && pxMax.y > srcUv2Min[si].y)
                            {
                                partXfRejected = true;
                                break;
                            }
                        }
                    }
                }

                if (!partXfRejected && uv2Map.Count > 0)
                    candidates.Add(new OverlapCandidate
                    {
                        uv2 = uv2Map, issues = -1, coverage = uv2Map.Count,
                        method = "partition-xform"
                    });
            }

            // ── Candidate 4: Strip parameterization (ribbons only) ──
            if (isRibbon && srcPR != null && srcPR.hasOverlap)
            {
                var uv2Map = StripParameterization.TransferNormalFiltered(
                    tShell, srcShells[srcIdx],
                    tVerts, tNormals, srcVerts, srcUv0, srcUv2, srcTris,
                    triUv2A, triUv2B, triUv2C,
                    ribbonAxis, ribbonAxis2, ribbonCentroid);

                if (uv2Map != null && uv2Map.Count > 0)
                    candidates.Add(new OverlapCandidate
                    {
                        uv2 = uv2Map, issues = -1, coverage = uv2Map.Count,
                        method = "strip-param"
                    });
            }

            // ── Candidate 5: Full-shell similarity transform with cross-source guard ──
            if (fullXform.valid)
            {
                var uv2Map = new Dictionary<int, Vector2>();
                foreach (int vi in tShell.vertexIndices)
                {
                    if (vi >= tUv0.Length) continue;
                    uv2Map[vi] = fullXform.Apply(tUv0[vi]);
                }

                // Guard: reject if xform result goes outside source shell's UV2 AABB
                // (with margin) or outside 0-1 range, or overlaps another source's UV2
                bool rejected = false;
                if (uv2Map.Count > 0)
                {
                    Vector2 xfMin = new Vector2(float.MaxValue, float.MaxValue);
                    Vector2 xfMax = new Vector2(float.MinValue, float.MinValue);
                    foreach (var kv in uv2Map)
                    {
                        xfMin = Vector2.Min(xfMin, kv.Value);
                        xfMax = Vector2.Max(xfMax, kv.Value);
                    }

                    // Reject if result goes outside 0-1 range (catches wild extrapolation)
                    if (xfMin.x < -0.01f || xfMax.x > 1.01f ||
                        xfMin.y < -0.01f || xfMax.y > 1.01f)
                        rejected = true;

                    // Reject if result extends too far beyond source's UV2 AABB
                    if (!rejected && srcUv2Min != null && srcUv2Max != null)
                    {
                        Vector2 sMin = srcUv2Min[srcIdx];
                        Vector2 sMax = srcUv2Max[srcIdx];
                        Vector2 sSize = sMax - sMin;
                        float margin = Mathf.Max(sSize.x, sSize.y) * 0.3f;
                        if (xfMin.x < sMin.x - margin || xfMax.x > sMax.x + margin ||
                            xfMin.y < sMin.y - margin || xfMax.y > sMax.y + margin)
                            rejected = true;
                    }

                    // Reject if result overlaps another overlap-group member's UV2 region.
                    // Only check shells in same overlap group (same UV0, different UV2) —
                    // checking all source shells causes false positives from unrelated
                    // shells that happen to be adjacent in UV2 packing.
                    if (!rejected && overlapGroupMembers != null)
                    {
                        foreach (int si in overlapGroupMembers)
                        {
                            if (si == srcIdx) continue;
                            if (xfMin.x < srcUv2Max[si].x && xfMax.x > srcUv2Min[si].x &&
                                xfMin.y < srcUv2Max[si].y && xfMax.y > srcUv2Min[si].y)
                            {
                                rejected = true;
                                break;
                            }
                        }
                    }
                }

                if (!rejected && uv2Map.Count > 0)
                    candidates.Add(new OverlapCandidate
                    {
                        uv2 = uv2Map, issues = -1, coverage = uv2Map.Count,
                        method = "full-xform"
                    });
            }

            return candidates;
        }

        /// <summary>
        /// Score all candidates via CountShellIssues and return the best one.
        /// Ties broken by coverage (more vertices covered wins).
        /// Xform candidates only win on strictly fewer issues (not equal).
        /// </summary>
        static OverlapCandidate? SelectBestCandidate(
            List<OverlapCandidate> candidates,
            List<int> faceIndices, int[] tgtTris, Vector2[] tUv0)
        {
            if (candidates == null || candidates.Count == 0) return null;

            // Score all candidates
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                c.issues = CountShellIssues(faceIndices, tgtTris, tUv0, c.uv2);
                candidates[i] = c;
            }

            // Find best: fewest issues, then most coverage
            // Xform-based methods must be strictly better to win over interp-based
            int bestIdx = 0;
            for (int i = 1; i < candidates.Count; i++)
            {
                var curr = candidates[i];
                var best = candidates[bestIdx];

                bool currIsXform = curr.method == "full-xform" || curr.method == "partition-xform";
                bool bestIsXform = best.method == "full-xform" || best.method == "partition-xform";

                if (curr.issues < best.issues)
                {
                    bestIdx = i;
                }
                else if (curr.issues == best.issues)
                {
                    // On equal issues: interp-based methods are preferred over xform
                    // because interp stays within source UV2 convex hull.
                    // Among same-type methods, prefer more coverage.
                    if (currIsXform && !bestIsXform)
                    {
                        // xform doesn't win on tie — keep interp
                    }
                    else if (!currIsXform && bestIsXform)
                    {
                        // interp replaces xform on tie
                        bestIdx = i;
                    }
                    else if (curr.coverage > best.coverage)
                    {
                        bestIdx = i;
                    }
                }
            }

            return candidates[bestIdx];
        }

        // Legacy overload
        public static TransferResult Transfer(Mesh targetMesh, SourceShellInfo[] sourceInfos)
        {
            return new TransferResult { uv2 = new Vector2[targetMesh.vertexCount], verticesTotal = targetMesh.vertexCount };
        }

        // ─── Pre-matching fragment merger ───────────────────────────────
        // When LOD simplification splits a UV0 shell into fragments (e.g. a
        // strip becomes two shorter strips), shell matching sees 2 targets →
        // 1 source → same-source conflict. This method detects and merges
        // such fragments into virtual shells BEFORE matching, eliminating the
        // conflict at the source.

        /// <summary>
        /// Detect target shells that are UV0-contained fragments of a single source
        /// shell and merge them into virtual shells. Returns a new shell list
        /// (may be smaller than input). Sets mergeCount to number of fragments merged.
        /// isFragmentMerged[i] is true for shells created by merging multiple fragments.
        /// </summary>
        static List<UvShell> MergeFragmentShells(
            List<UvShell> tgtShells, Vector2[] tUv0, int[] tgtTris, Vector3[] tVerts,
            List<UvShell> srcShells, Vector2[] srcUv0, int[] srcTris, Vector3[] srcVerts,
            out int mergeCount, out bool[] isFragmentMerged,
            out int[] fragmentMergeSource)
        {
            mergeCount = 0;
            isFragmentMerged = null;
            fragmentMergeSource = null;
            int tgtCount = tgtShells.Count;
            int srcCount = srcShells.Count;
            if (tgtCount < 2 || srcCount == 0) return tgtShells;

            // Precompute source 3D centroids + UV0 areas
            var srcCentroid3D = new Vector3[srcCount];
            var srcUv0Area = new float[srcCount];
            for (int si = 0; si < srcCount; si++)
            {
                Vector3 c = Vector3.zero; int n = 0;
                foreach (int vi in srcShells[si].vertexIndices)
                    if (vi < srcVerts.Length) { c += srcVerts[vi]; n++; }
                if (n > 0) srcCentroid3D[si] = c / n;

                double area = 0;
                foreach (int f in srcShells[si].faceIndices)
                {
                    int i0 = srcTris[f * 3], i1 = srcTris[f * 3 + 1], i2 = srcTris[f * 3 + 2];
                    if (i0 >= srcUv0.Length || i1 >= srcUv0.Length || i2 >= srcUv0.Length) continue;
                    float cross = (srcUv0[i1].x - srcUv0[i0].x) * (srcUv0[i2].y - srcUv0[i0].y)
                                - (srcUv0[i2].x - srcUv0[i0].x) * (srcUv0[i1].y - srcUv0[i0].y);
                    area += Mathf.Abs(cross) * 0.5f;
                }
                srcUv0Area[si] = (float)area;
            }

            // Precompute target 3D centroids + UV0 areas
            var tgtCentroid3D = new Vector3[tgtCount];
            var tgtUv0Area = new float[tgtCount];
            for (int ti = 0; ti < tgtCount; ti++)
            {
                Vector3 c = Vector3.zero; int n = 0;
                foreach (int vi in tgtShells[ti].vertexIndices)
                    if (vi < tVerts.Length) { c += tVerts[vi]; n++; }
                if (n > 0) tgtCentroid3D[ti] = c / n;

                double area = 0;
                foreach (int f in tgtShells[ti].faceIndices)
                {
                    int i0 = tgtTris[f * 3], i1 = tgtTris[f * 3 + 1], i2 = tgtTris[f * 3 + 2];
                    if (i0 >= tUv0.Length || i1 >= tUv0.Length || i2 >= tUv0.Length) continue;
                    float cross = (tUv0[i1].x - tUv0[i0].x) * (tUv0[i2].y - tUv0[i0].y)
                                - (tUv0[i2].x - tUv0[i0].x) * (tUv0[i1].y - tUv0[i0].y);
                    area += Mathf.Abs(cross) * 0.5f;
                }
                tgtUv0Area[ti] = (float)area;
            }

            // For each target shell, find the best-matching source shell whose UV0
            // bbox fully contains the target's UV0 bbox. Use 3D centroid distance
            // as tiebreaker when multiple sources contain the target.
            const float bboxPad = 0.002f; // small padding for float imprecision
            var tgtToSrcContainer = new int[tgtCount];
            for (int ti = 0; ti < tgtCount; ti++) tgtToSrcContainer[ti] = -1;

            for (int ti = 0; ti < tgtCount; ti++)
            {
                var t = tgtShells[ti];
                int bestSrc = -1;
                float bestDist = float.MaxValue;

                for (int si = 0; si < srcCount; si++)
                {
                    var s = srcShells[si];
                    // UV0 bbox containment: target fully inside source
                    if (t.boundsMin.x < s.boundsMin.x - bboxPad) continue;
                    if (t.boundsMin.y < s.boundsMin.y - bboxPad) continue;
                    if (t.boundsMax.x > s.boundsMax.x + bboxPad) continue;
                    if (t.boundsMax.y > s.boundsMax.y + bboxPad) continue;

                    float dist = Vector3.SqrMagnitude(tgtCentroid3D[ti] - srcCentroid3D[si]);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestSrc = si;
                    }
                }
                tgtToSrcContainer[ti] = bestSrc;
            }

            // Group target shells by their containing source shell
            var srcToFragments = new Dictionary<int, List<int>>();
            for (int ti = 0; ti < tgtCount; ti++)
            {
                int src = tgtToSrcContainer[ti];
                if (src < 0) continue;
                if (!srcToFragments.TryGetValue(src, out var list))
                {
                    list = new List<int>();
                    srcToFragments[src] = list;
                }
                list.Add(ti);
            }

            // Identify mergeable groups: ≥2 target shells contained by the same source.
            // Validate: combined UV0 area should be roughly comparable to source area.
            // LOD simplification reduces geometry, so allow generous tolerance.
            //
            // IMPORTANT: Skip source shells whose UV0 bbox overlaps with another
            // source shell. Overlapping UV0 means the source has front/back or
            // tiling geometry — target shells inside it may belong to DIFFERENT
            // physical sides and must NOT be merged into one virtual shell.
            var srcHasUv0Overlap = new bool[srcCount];
            for (int si = 0; si < srcCount; si++)
            {
                for (int sj = si + 1; sj < srcCount; sj++)
                {
                    if (srcShells[si].boundsMin.x < srcShells[sj].boundsMax.x + bboxPad &&
                        srcShells[si].boundsMax.x > srcShells[sj].boundsMin.x - bboxPad &&
                        srcShells[si].boundsMin.y < srcShells[sj].boundsMax.y + bboxPad &&
                        srcShells[si].boundsMax.y > srcShells[sj].boundsMin.y - bboxPad)
                    {
                        srcHasUv0Overlap[si] = true;
                        srcHasUv0Overlap[sj] = true;
                    }
                }
            }

            const float kMaxFragSpreadFactor = 2.0f;
            var mergeGroups = new List<(int srcIdx, List<int> fragments)>();
            foreach (var kv in srcToFragments)
            {
                var group = kv.Value;
                if (group.Count < 2) continue;

                // Source UV0 overlaps with other source shells (front/back belt,
                // tiling). Fragments from different physical sides must NOT be
                // merged — they need unique UV2 regions. Skip this group entirely;
                // the multiplicative normal penalty in FindBestSourceShell ensures each
                // fragment matches the correct side, and dedup shared-source logic
                // handles the rest.
                if (srcHasUv0Overlap[kv.Key])
                {
                    UvtLog.Info($"[GroupedTransfer] Fragment merge: skipping src#{kv.Key} group " +
                        $"({group.Count} shells) — UV0 overlap, individual matching preferred");
                    continue;
                }

                float combinedArea = 0;
                for (int i = 0; i < group.Count; i++)
                    combinedArea += tgtUv0Area[group[i]];

                float srcArea = srcUv0Area[kv.Key];
                if (srcArea > 1e-8f && combinedArea > 1e-8f)
                {
                    float ratio = combinedArea / srcArea;
                    if (ratio < 0.15f || ratio > 3.0f) continue; // area mismatch → not fragments
                }

                // Guard: reject physically distant candidates (tiling instances, not fragments).
                // True fragments from LOD splitting are near their parent source shell.
                // Tiling instances share UV0 bbox but are far apart in 3D.
                float maxFragDist = 0;
                for (int i = 0; i < group.Count; i++)
                    for (int j = i + 1; j < group.Count; j++)
                    {
                        float d = Vector3.Distance(tgtCentroid3D[group[i]], tgtCentroid3D[group[j]]);
                        if (d > maxFragDist) maxFragDist = d;
                    }

                // Reference: source shell 3D bounding sphere diameter
                Vector3 srcC = srcCentroid3D[kv.Key];
                float maxR = 0;
                foreach (int vi in srcShells[kv.Key].vertexIndices)
                    if (vi < srcVerts.Length)
                        maxR = Mathf.Max(maxR, Vector3.Distance(srcVerts[vi], srcC));
                float srcDiameter = maxR * 2f;

                if (srcDiameter > 1e-6f && maxFragDist > srcDiameter * kMaxFragSpreadFactor)
                {
                    UvtLog.Info($"[GroupedTransfer] Fragment merge: skipping src#{kv.Key} group " +
                        $"({group.Count} shells) — fragment spread {maxFragDist:F4} > " +
                        $"{kMaxFragSpreadFactor}× source diameter {srcDiameter:F4}");
                    continue;
                }

                mergeGroups.Add((kv.Key, group));
            }

            if (mergeGroups.Count == 0) return tgtShells;

            // Build merged shell list
            var mergedSet = new HashSet<int>();
            var newShells = new List<UvShell>();

            for (int gi = 0; gi < mergeGroups.Count; gi++)
            {
                int srcIdx = mergeGroups[gi].srcIdx;
                var group = mergeGroups[gi].fragments;

                // Create merged virtual shell from all fragments in this group
                var merged = new UvShell();
                merged.faceIndices = new List<int>();
                merged.vertexIndices = new HashSet<int>();
                Vector2 mn = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 mx = new Vector2(float.MinValue, float.MinValue);

                float combinedArea = 0;
                for (int i = 0; i < group.Count; i++)
                {
                    int ti = group[i];
                    mergedSet.Add(ti);
                    var frag = tgtShells[ti];
                    merged.faceIndices.AddRange(frag.faceIndices);
                    merged.vertexIndices.UnionWith(frag.vertexIndices);
                    mn = Vector2.Min(mn, frag.boundsMin);
                    mx = Vector2.Max(mx, frag.boundsMax);
                    combinedArea += tgtUv0Area[ti];
                }

                merged.boundsMin = mn;
                merged.boundsMax = mx;
                merged.bboxArea = Mathf.Max(0f, (mx.x - mn.x) * (mx.y - mn.y));
                newShells.Add(merged);
                mergeCount += group.Count;

                // Log fragment IDs being merged
                var fragIds = new System.Text.StringBuilder();
                for (int i = 0; i < group.Count; i++)
                {
                    if (i > 0) fragIds.Append(',');
                    fragIds.Append(tgtShells[group[i]].shellId);
                }
                float areaRatio = srcUv0Area[srcIdx] > 1e-8f
                    ? combinedArea / srcUv0Area[srcIdx] : 0f;
                UvtLog.Info($"[GroupedTransfer] Merged {group.Count} fragment shells " +
                    $"(IDs: {fragIds}) into virtual shell (src#{srcIdx}, " +
                    $"area ratio: {areaRatio:F2})");
            }

            // Add unmerged shells
            for (int ti = 0; ti < tgtCount; ti++)
            {
                if (!mergedSet.Contains(ti))
                    newShells.Add(tgtShells[ti]);
            }

            // Reassign shell IDs for consistency
            for (int i = 0; i < newShells.Count; i++)
                newShells[i].shellId = i;

            UvtLog.Info($"[GroupedTransfer] Fragment merge: {mergeGroups.Count} group(s), " +
                $"{mergeCount} fragments merged, shells {tgtCount} → {newShells.Count}");

            // Mark which output shells are fragment-merged (they appear first)
            // and record which source shell each merged group belongs to.
            isFragmentMerged = new bool[newShells.Count];
            fragmentMergeSource = new int[newShells.Count];
            for (int i = 0; i < newShells.Count; i++)
                fragmentMergeSource[i] = -1;
            for (int i = 0; i < mergeGroups.Count; i++)
            {
                isFragmentMerged[i] = true;
                fragmentMergeSource[i] = mergeGroups[i].srcIdx;
            }

            return newShells;
        }
    }
}
