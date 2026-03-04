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

            // Subsample target vertices for large shells
            var vertList = new List<int>(tShell.vertexIndices);
            int step = Mathf.Max(1, vertList.Count / kMaxSampleVerts);

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
            Vector3[] targetShellCentroids)
        {
            const float wArea     = 0.20f;
            const float wNormal   = 0.30f;
            const float wDist     = 0.15f;
            const float wCoverage = 0.35f;
            const float kCoverageAcceptThreshold = 0.70f;

            float diagSq = meshDiagonal * meshDiagonal;
            if (diagSq < 1e-12f) diagSq = 1f;

            int rescued = 0;

            for (int tsi = 0; tsi < tgtShells.Count; tsi++)
            {
                if (!tgtIsMerged[tsi]) continue;

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
                    float distScore = 1f - Mathf.Clamp01(centDistSq / diagSq);

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

                    if (score > bestScore)
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
                if (faces.Count > kMinFacesForShellBvh)
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

            UvtLog.Verbose($"[GroupedTransfer] Adaptive: meshDiag={meshDiagonal:F4}, " +
                $"avgUv0Edge={avgUv0Edge:F6}, goodDistSq={kGoodDistSq:F6}, " +
                $"uv0BadThresh={kUv0BadThreshold:F6}, maxRetries={kMaxRetries}, " +
                $"overlapGroups={overlapGroups.Count}(maxSize={maxOverlapGroupSize})");

            // ── Phase 2a: Match each target shell → best source shell ──
            result.targetShellToSourceShell = new int[tgtShells.Count];
            result.targetShellMethod = new int[tgtShells.Count]; // 0=interp, 1=xform, 2=merged
            result.targetShellCentroids = new Vector3[tgtShells.Count];
            result.targetShellMatchDistSqr = new float[tgtShells.Count];

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

                // Find best source shell (with BVH acceleration + subsampling)
                FindBestSourceShell(tShell, tVerts, srcShells, srcCentroid3D,
                    triPosA, triPosB, triPosC,
                    shellBvh3D, shellBvh3DFaceMap,
                    tCentroid, kMaxRetries, kGoodDistSq, null,
                    out int chosenSrc, out float chosenDistSq, out float chosenAvg3D);

                if (chosenSrc < 0) continue;

                result.targetShellToSourceShell[tsi] = chosenSrc;
                result.targetShellMatchDistSqr[tsi] = chosenDistSq;
                tgtChosenAvg3D[tsi] = chosenAvg3D;

                // Detect merged shell via UV0 coverage (BVH + adaptive threshold)
                tgtIsMerged[tsi] = DetectMergedShell(tShell, tUv0,
                    srcShells[chosenSrc].faceIndices, triUv0A, triUv0B, triUv0C,
                    shellUv0Bvh[chosenSrc], kUv0BadThreshold);
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
                result.targetShellCentroids);

            // ── Phase 2b: Deduplicate — resolve same-source conflicts ──
            // When multiple non-merged target shells claim the same source shell
            // (common with overlapping/tiling UV0), keep the best match and
            // reassign others to different source shells at the same 3D location.
            // Hoisted: used by Phase 3 overlap guard for merged shells
            var claimed = new HashSet<int>();
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
                                out int newSrc, out float newDistSq, out float newAvg3D);

                            if (newSrc >= 0)
                            {
                                // Re-check merged status with new source (BVH + adaptive threshold)
                                bool newIsMerged = DetectMergedShell(tShell, tUv0,
                                    srcShells[newSrc].faceIndices, triUv0A, triUv0B, triUv0C,
                                    shellUv0Bvh[newSrc], kUv0BadThreshold);

                                // If reassignment would make a non-merged shell become merged,
                                // force 3D-primary merged mode instead of reverting to
                                // overlapping source. 3D projection naturally separates
                                // targets at different 3D positions, avoiding same-source
                                // UV2 overlap that causes lightmap seams.
                                if (newIsMerged && !tgtIsMerged[tsi] && oldSrc >= 0)
                                {
                                    UvtLog.Info($"[GroupedTransfer] Dedup: t{tsi} → merged+3D " +
                                        $"(src{oldSrc}, new src{newSrc} would force merged)");
                                    tgtIsMerged[tsi] = true;
                                    tgtForce3DFallback[tsi] = true;
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

            // Track UV2 AABBs of placed force3D shells to prevent mutual overlap
            var force3DUsedRegions = new List<(Vector2 min, Vector2 max)>();

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

                    // force3D: try all-source first (to find UV2 space in a different
                    // source region), fall back to constrained if overlap guard rejects.
                    // Normal merged: constrained first, all-source fallback (unchanged).
                    bool force3D = tgtForce3DFallback[tsi];
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

                    // ── Pass 2: UV0-only constrained fallback for broken force3D ──
                    // Cascade: C (strip param) → A/B (partition BVH) → original (normal filter)
                    if (force3D && bestMergedIssues > tShell.faceIndices.Count / 2
                        && chosenSrc >= 0 && srcFacesChosen != null)
                    {
                        var partResult = srcPartitions[chosenSrc];
                        bool usedPartition = false;

                        // ── Approach C: Strip parameterization for ribbon shells ──
                        if (srcIsRibbon[chosenSrc] && partResult.hasOverlap)
                        {
                            var candidateStrip = StripParameterization.TransferNormalFiltered(
                                tShell, srcShells[chosenSrc],
                                tVerts, tNormals, srcVerts, srcUv0, srcUv2, srcTris,
                                triUv2A, triUv2B, triUv2C,
                                srcRibbonAxis[chosenSrc], srcRibbonAxis2[chosenSrc],
                                srcRibbonCentroid[chosenSrc]);

                            int issuesStrip = CountShellIssues(tShell.faceIndices, tgtTris, tUv0, candidateStrip);
                            if (issuesStrip < bestMergedIssues)
                            {
                                bestMergedIssues = issuesStrip;
                                bestMergedUv2 = candidateStrip;
                                bestMergedConsistencyFixes = 0;
                                bestWasConstrained = true;
                                usedPartition = true;

                                UvtLog.Info($"[GroupedTransfer]   t{tsi}: strip parameterization transfer " +
                                    $"({issuesStrip} issues)");
                            }
                        }

                        // ── Approach A/B: Per-vertex partition-constrained BVH ──
                        // Always try even if strip was used — partition may do better
                        if (partResult.hasOverlap && partResult.partitionCount > 1
                            && partitionBvh[chosenSrc] != null)
                        {
                            var candidatePart = new Dictionary<int, Vector2>();

                            foreach (int vi in tShell.vertexIndices)
                            {
                                if (vi >= tUv0.Length) continue;
                                Vector2 tUv = tUv0[vi];
                                Vector3 vPos = (vi < tVerts.Length) ? tVerts[vi] : Vector3.zero;
                                Vector3 vNrm = (tNormals != null && vi < tNormals.Length)
                                    ? tNormals[vi] : Vector3.up;

                                int pid = SpatialPartitioner.MatchPartition(partResult, vPos, vNrm);
                                var bvh = (pid >= 0 && partitionBvh[chosenSrc][pid] != null)
                                    ? partitionBvh[chosenSrc][pid]
                                    : shellUv0Bvh[chosenSrc];

                                if (bvh == null) continue;
                                var hit = bvh.FindNearest(tUv);
                                if (hit.faceIndex >= 0)
                                {
                                    candidatePart[vi] = triUv2A[hit.faceIndex] * hit.u
                                                      + triUv2B[hit.faceIndex] * hit.v
                                                      + triUv2C[hit.faceIndex] * hit.w;
                                }
                            }

                            int issuesPart = CountShellIssues(tShell.faceIndices, tgtTris, tUv0, candidatePart);
                            if (issuesPart < bestMergedIssues)
                            {
                                bestMergedIssues = issuesPart;
                                bestMergedUv2 = candidatePart;
                                bestMergedConsistencyFixes = 0;
                                bestWasConstrained = true;
                                usedPartition = true;

                                UvtLog.Info($"[GroupedTransfer]   t{tsi}: per-vertex partition UV0-interp " +
                                    $"({issuesPart} issues)");
                            }
                        }

                        // ── Original fallback: inverted normal filter ──
                        if (!usedPartition)
                        {
                            var candidateUv0 = new Dictionary<int, Vector2>();
                            int localFixesUv0 = 0;

                            TriangleBvh2D uv0Bvh = shellUv0Bvh[chosenSrc];

                            foreach (int vi in tShell.vertexIndices)
                            {
                                if (vi >= tUv0.Length) continue;
                                Vector2 tUv = tUv0[vi];

                                int bestF = -1;
                                float bestU = 0, bestV = 0, bestW = 0;
                                float bestDSq = float.MaxValue;

                                Vector3 tNrm = (tNormals != null && vi < tNormals.Length)
                                    ? tNormals[vi] : Vector3.up;

                                if (uv0Bvh != null)
                                {
                                    var hit = tNrm.sqrMagnitude > 0.5f
                                        ? uv0Bvh.FindNearestNormalFiltered(tUv, -tNrm, triNormal, kBackfaceDot)
                                        : uv0Bvh.FindNearest(tUv);
                                    bestF = hit.faceIndex;
                                    bestU = hit.u; bestV = hit.v; bestW = hit.w;
                                    bestDSq = hit.distSq;
                                }
                                else
                                {
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
                                {
                                    candidateUv0[vi] = triUv2A[bestF] * bestU
                                                     + triUv2B[bestF] * bestV
                                                     + triUv2C[bestF] * bestW;
                                }
                            }

                            int issuesUv0 = CountShellIssues(tShell.faceIndices, tgtTris, tUv0, candidateUv0);

                            if (issuesUv0 < bestMergedIssues)
                            {
                                bestMergedIssues = issuesUv0;
                                bestMergedUv2 = candidateUv0;
                                bestMergedConsistencyFixes = localFixesUv0;
                                bestWasConstrained = true;

                                UvtLog.Info($"[GroupedTransfer]   t{tsi}: UV0-interp fallback " +
                                    $"({issuesUv0} issues, was 3D-primary)");
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

                    chosenUv2 = bestMergedUv2;
                    shellsMerged++;
                    result.targetShellMethod[tsi] = 2; // merged
                    result.consistencyCorrected += bestMergedConsistencyFixes;
                    string mergedLabel = force3D ? "3D-primary"
                        : (bestWasConstrained ? "src-constrained" : "all-source");
                    UvtLog.Info($"[GroupedTransfer]   t{tsi} merged({tShell.faceIndices.Count}f): " +
                        $"{mergedLabel} ({bestMergedIssues} issues)");
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
                            for (int si = 0; si < srcShells.Count; si++)
                            {
                                if (si == chosenSrc) continue;
                                if (xfBMin.x < srcUv2Max[si].x && xfBMax.x > srcUv2Min[si].x &&
                                    xfBMin.y < srcUv2Max[si].y && xfBMax.y > srcUv2Min[si].y)
                                {
                                    issuesTransform = int.MaxValue;
                                    break;
                                }
                            }
                            if (issuesTransform < int.MaxValue)
                                issuesTransform += xfOob;
                        }
                    }

                    // Candidate B: per-vertex UV0 interpolation (BVH + normal filtering for thin details)
                    var uv2_interp = new Dictionary<int, Vector2>();
                    var srcBvh = shellUv0Bvh[chosenSrc]; // may be null for small shells
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
