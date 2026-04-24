// TransferValidator.cs — Post-transfer quality validation
// Checks: inverted triangles, stretch outliers, zero-area, OOB vertices
// Called after GroupedShellTransfer.Transfer(), before Apply.

using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public static class TransferValidator
    {
        // ─── Per-triangle issue flags ───
        [System.Flags]
        public enum TriIssue
        {
            None          = 0,
            Inverted      = 1 << 0,   // UV2 winding flipped vs UV0
            Stretched     = 1 << 1,   // area ratio outlier vs shell median
            ZeroArea      = 1 << 2,   // degenerate UV2 triangle
            OutOfBounds   = 1 << 3,   // any vertex outside 0-1
            Overlap       = 1 << 4,   // UV2 shell overlap with another shell
            TexelDensity  = 1 << 5,   // areaWorld/areaUV2 ratio outlier (suspicious texel density)
        }

        public class ValidationReport
        {
            public int totalTriangles;
            public int invertedCount;
            public int stretchedCount;
            public int zeroAreaCount;
            public int oobCount;
            public int cleanCount;

            // UV2 shell overlap (diff-source only — these are real problems)
            public int overlapShellPairs;       // number of diff-src shell pairs with actual tri overlap
            public int overlapTriangleCount;    // triangles involved in diff-src overlaps

            // Same-source overlap (expected for tiling/symmetric geometry — informational only)
            public int overlapSameSrcPairs;
            public int overlapSameSrcTriCount;

            // Texel density (areaWorld/areaUV2)
            public int texelDensityBadCount;    // triangles with suspicious world/UV2 ratio
            public float texelDensityMedian;    // median areaWorld/areaUV2 across all triangles

            // Per-triangle data for visualization
            public TriIssue[] perTriangle;

            // Per-triangle stretch ratio (areaUV2/areaUV0), NaN if UV0 degenerate
            public float[] stretchRatios;

            // Per-triangle texel density ratio (areaWorld/areaUV2), NaN if UV2 degenerate
            public float[] texelDensityRatios;

            // Shell-level stats
            public float[] shellMedianStretch;   // indexed by target shell
            public int[] shellInvertedCount;

            public bool HasIssues => invertedCount > 0 || stretchedCount > 0
                                  || zeroAreaCount > 0 || oobCount > 0
                                  || overlapShellPairs > 0;  // diff-src only

            public string Summary =>
                $"Tri:{totalTriangles} Clean:{cleanCount} " +
                $"Inv:{invertedCount} Stretch:{stretchedCount} " +
                $"Zero:{zeroAreaCount} OOB:{oobCount}" +
                (texelDensityBadCount > 0 ? $" TxlBad:{texelDensityBadCount}" : "") +
                (overlapShellPairs > 0 ? $" Ovlp:{overlapShellPairs}pairs/{overlapTriangleCount}tri" : "") +
                (overlapSameSrcPairs > 0 ? $" SameSrcOvlp:{overlapSameSrcPairs}pairs(ok)" : "");
        }

        /// <summary>
        /// Validate transfer result. Compares UV0 and UV2 per-triangle.
        /// stretchThreshold: ratio deviation from shell median to flag (e.g. 3.0 = 3x median)
        /// </summary>
        public static ValidationReport Validate(
            Mesh targetMesh,
            Vector2[] uv2,
            GroupedShellTransfer.TransferResult transferResult = null,
            float stretchThreshold = 3.0f,
            float texelDensityThreshold = 200.0f)
        {
            var tris = targetMesh.triangles;
            var verts = targetMesh.vertices;
            var uv0List = new List<Vector2>();
            targetMesh.GetUVs(0, uv0List);
            var uv0 = uv0List.ToArray();
            int faceCount = tris.Length / 3;
            int vertCount = targetMesh.vertexCount;

            var report = new ValidationReport
            {
                totalTriangles = faceCount,
                perTriangle = new TriIssue[faceCount],
                stretchRatios = new float[faceCount],
                texelDensityRatios = new float[faceCount],
            };

            // ── Phase 1: Per-triangle metrics ──
            var areaUv0 = new float[faceCount];
            var areaUv2 = new float[faceCount];
            var areaWorld = new float[faceCount];
            var windingUv0 = new float[faceCount]; // signed area
            var windingUv2 = new float[faceCount];

            for (int f = 0; f < faceCount; f++)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];

                if (i0 < uv0.Length && i1 < uv0.Length && i2 < uv0.Length)
                {
                    windingUv0[f] = SignedArea2D(uv0[i0], uv0[i1], uv0[i2]);
                    areaUv0[f] = Mathf.Abs(windingUv0[f]);
                }

                if (i0 < uv2.Length && i1 < uv2.Length && i2 < uv2.Length)
                {
                    windingUv2[f] = SignedArea2D(uv2[i0], uv2[i1], uv2[i2]);
                    areaUv2[f] = Mathf.Abs(windingUv2[f]);
                }

                // World-space triangle area
                if (i0 < verts.Length && i1 < verts.Length && i2 < verts.Length)
                {
                    Vector3 cross = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
                    areaWorld[f] = cross.magnitude * 0.5f;
                }

                // Stretch ratio (UV2 vs UV0)
                if (areaUv0[f] > 1e-10f)
                    report.stretchRatios[f] = areaUv2[f] / areaUv0[f];
                else
                    report.stretchRatios[f] = float.NaN;

                // Texel density ratio (world area / UV2 area)
                if (areaUv2[f] > 1e-10f)
                    report.texelDensityRatios[f] = areaWorld[f] / areaUv2[f];
                else
                    report.texelDensityRatios[f] = float.NaN;
            }

            // ── Phase 2: Compute per-shell median stretch ──
            // Use target shell info from transferResult if available
            int[] vertShell = transferResult?.vertexToSourceShell;
            int shellCount = 0;
            int[] triShell = null; // target shell per triangle

            if (vertShell != null)
            {
                // Determine triangle shell by majority vote over all 3 vertices.
                // If no majority exists, fallback to the first valid vertex shell, else -1.
                triShell = new int[faceCount];
                var shellStretchLists = new Dictionary<int, List<float>>();

                for (int f = 0; f < faceCount; f++)
                {
                    int i0 = tris[f * 3];
                    int i1 = tris[f * 3 + 1];
                    int i2 = tris[f * 3 + 2];

                    int sh0 = (i0 < vertShell.Length) ? vertShell[i0] : -1;
                    int sh1 = (i1 < vertShell.Length) ? vertShell[i1] : -1;
                    int sh2 = (i2 < vertShell.Length) ? vertShell[i2] : -1;

                    int sh;
                    if (sh0 >= 0 && (sh0 == sh1 || sh0 == sh2))
                    {
                        sh = sh0;
                    }
                    else if (sh1 >= 0 && sh1 == sh2)
                    {
                        sh = sh1;
                    }
                    else if (sh0 >= 0)
                    {
                        sh = sh0;
                    }
                    else if (sh1 >= 0)
                    {
                        sh = sh1;
                    }
                    else if (sh2 >= 0)
                    {
                        sh = sh2;
                    }
                    else
                    {
                        sh = -1;
                    }

                    triShell[f] = sh;

                    float sr = report.stretchRatios[f];
                    if (!float.IsNaN(sr) && sh >= 0)
                    {
                        if (!shellStretchLists.TryGetValue(sh, out var list))
                        {
                            list = new List<float>();
                            shellStretchLists[sh] = list;
                        }
                        list.Add(sr);
                    }
                }

                // Compute medians
                shellCount = shellStretchLists.Count;
                var shellMedians = new Dictionary<int, float>();
                foreach (var kv in shellStretchLists)
                {
                    kv.Value.Sort();
                    shellMedians[kv.Key] = kv.Value[kv.Value.Count / 2];
                }

                // Flag stretch outliers
                for (int f = 0; f < faceCount; f++)
                {
                    float sr = report.stretchRatios[f];
                    if (float.IsNaN(sr)) continue;
                    int sh = triShell[f];
                    if (sh >= 0 && shellMedians.TryGetValue(sh, out float median) && median > 1e-10f)
                    {
                        float ratio = sr / median;
                        if (ratio > stretchThreshold || ratio < 1f / stretchThreshold)
                            report.perTriangle[f] |= TriIssue.Stretched;
                    }
                }
            }

            // ── Phase 3: Flag issues ──
            for (int f = 0; f < faceCount; f++)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];

                // Inverted: UV2 has negative winding (bad for lightmaps).
                // Note: UV0 may intentionally have negative winding (mirrored shells
                // for texture tiling), so we check UV2 sign alone, not vs UV0.
                if (Mathf.Abs(windingUv2[f]) > 1e-10f && windingUv2[f] < 0f)
                    report.perTriangle[f] |= TriIssue.Inverted;

                // Zero area UV2
                if (areaUv2[f] < 1e-10f && areaUv0[f] > 1e-10f)
                    report.perTriangle[f] |= TriIssue.ZeroArea;

                // Out of bounds
                for (int j = 0; j < 3; j++)
                {
                    int vi = tris[f * 3 + j];
                    if (vi < uv2.Length)
                    {
                        var uv = uv2[vi];
                        if (uv.x < -0.01f || uv.x > 1.01f || uv.y < -0.01f || uv.y > 1.01f)
                        {
                            report.perTriangle[f] |= TriIssue.OutOfBounds;
                            break;
                        }
                    }
                }
            }

            // ── Phase 4: Texel density outliers ──
            {
                // Compute global median texel density
                var validDensities = new List<float>();
                for (int f = 0; f < faceCount; f++)
                {
                    if (!float.IsNaN(report.texelDensityRatios[f]))
                        validDensities.Add(report.texelDensityRatios[f]);
                }
                if (validDensities.Count > 0)
                {
                    validDensities.Sort();
                    report.texelDensityMedian = validDensities[validDensities.Count / 2];

                    // Flag outliers: ratio > texelDensityThreshold × median
                    float median = report.texelDensityMedian;
                    if (median > 1e-10f)
                    {
                        for (int f = 0; f < faceCount; f++)
                        {
                            float td = report.texelDensityRatios[f];
                            if (float.IsNaN(td)) continue;
                            float ratio = td / median;
                            if (ratio > texelDensityThreshold || ratio < 1f / texelDensityThreshold)
                                report.perTriangle[f] |= TriIssue.TexelDensity;
                        }
                    }
                }
            }

            // ── Count ──
            for (int f = 0; f < faceCount; f++)
            {
                var fl = report.perTriangle[f];
                if (fl == TriIssue.None) report.cleanCount++;
                if ((fl & TriIssue.Inverted) != 0) report.invertedCount++;
                if ((fl & TriIssue.Stretched) != 0) report.stretchedCount++;
                if ((fl & TriIssue.ZeroArea) != 0) report.zeroAreaCount++;
                if ((fl & TriIssue.OutOfBounds) != 0) report.oobCount++;
                if ((fl & TriIssue.TexelDensity) != 0) report.texelDensityBadCount++;
            }

            return report;
        }

        // ═══════════════════════════════════════════════════════════
        //  UV2 Shell Overlap Detection
        //  Extracts UV2 shells, finds AABB-overlapping pairs,
        //  then does SAT triangle-triangle test for actual overlaps.
        // ═══════════════════════════════════════════════════════════

        static readonly string[] kMethodNames = { "interp", "xform", "merged", "?" };

        public static void DetectUv2Overlaps(Mesh targetMesh, Vector2[] uv2,
            ValidationReport report,
            GroupedShellTransfer.TransferResult transferResult = null)
        {
            if (uv2 == null || uv2.Length == 0) return;
            var tris = targetMesh.triangles;
            int faceCount = tris.Length / 3;

            // Extract UV2 shells
            var shells = UvShellExtractor.Extract(uv2, tris);
            if (shells.Count < 2) return;

            // ── Pre-build: UV2 shell → dominant target shell + source shell + method ──
            int[] uv2ShellToTargetShell = null;
            int[] uv2ShellToSourceShell = null;
            int[] uv2ShellMethod = null;

            if (transferResult?.faceToTargetShell != null &&
                transferResult?.targetShellToSourceShell != null &&
                transferResult?.targetShellMethod != null)
            {
                uv2ShellToTargetShell = new int[shells.Count];
                uv2ShellToSourceShell = new int[shells.Count];
                uv2ShellMethod = new int[shells.Count];

                for (int si = 0; si < shells.Count; si++)
                {
                    // Find dominant target shell for this UV2 shell
                    var tgtShellCounts = new Dictionary<int, int>();
                    foreach (int f in shells[si].faceIndices)
                    {
                        if (f < transferResult.faceToTargetShell.Length)
                        {
                            int ts = transferResult.faceToTargetShell[f];
                            if (ts >= 0)
                            {
                                tgtShellCounts.TryGetValue(ts, out int cnt);
                                tgtShellCounts[ts] = cnt + 1;
                            }
                        }
                    }

                    int bestTs = -1, bestCnt = 0;
                    foreach (var kv in tgtShellCounts)
                        if (kv.Value > bestCnt) { bestTs = kv.Key; bestCnt = kv.Value; }

                    uv2ShellToTargetShell[si] = bestTs;
                    uv2ShellToSourceShell[si] = (bestTs >= 0 && bestTs < transferResult.targetShellToSourceShell.Length)
                        ? transferResult.targetShellToSourceShell[bestTs] : -1;
                    uv2ShellMethod[si] = (bestTs >= 0 && bestTs < transferResult.targetShellMethod.Length)
                        ? transferResult.targetShellMethod[bestTs] : 3;
                }
            }

            // AABB overlap candidate pairs
            int pairsChecked = 0;
            int overlapPairsDiffSrc = 0;
            int overlapPairsSameSrc = 0;
            var overlapTrisDiffSrc = new HashSet<int>();
            var overlapTrisSameSrc = new HashSet<int>();

            // Diagnostic accumulators
            int ovlpInterp = 0, ovlpXform = 0, ovlpMerged = 0;
            var overlapDetails = new List<string>();

            for (int i = 0; i < shells.Count; i++)
            {
                for (int j = i + 1; j < shells.Count; j++)
                {
                    if (!AabbOverlap(shells[i], shells[j])) continue;
                    pairsChecked++;

                    // Pre-check same-source status
                    bool sameSrc = false;
                    if (uv2ShellToSourceShell != null)
                    {
                        int srcI = uv2ShellToSourceShell[i], srcJ = uv2ShellToSourceShell[j];
                        sameSrc = srcI >= 0 && srcI == srcJ;
                    }

                    // SAT triangle-triangle test between shells
                    bool pairHasOverlap = false;
                    int pairOverlapCount = 0;
                    var facesI = shells[i].faceIndices;
                    var facesJ = shells[j].faceIndices;

                    // Limit brute-force: skip if product too large
                    if ((long)facesI.Count * facesJ.Count > 500000) continue;

                    for (int fi = 0; fi < facesI.Count; fi++)
                    {
                        int fI = facesI[fi];
                        int ai0 = tris[fI*3], ai1 = tris[fI*3+1], ai2 = tris[fI*3+2];
                        if (ai0 >= uv2.Length || ai1 >= uv2.Length || ai2 >= uv2.Length) continue;
                        Vector2 a0 = uv2[ai0], a1 = uv2[ai1], a2 = uv2[ai2];

                        // Skip degenerate
                        if (Mathf.Abs(SignedArea2D(a0,a1,a2)) < 1e-10f) continue;

                        for (int fj = 0; fj < facesJ.Count; fj++)
                        {
                            int fJ = facesJ[fj];
                            int bj0 = tris[fJ*3], bj1 = tris[fJ*3+1], bj2 = tris[fJ*3+2];
                            if (bj0 >= uv2.Length || bj1 >= uv2.Length || bj2 >= uv2.Length) continue;
                            Vector2 b0 = uv2[bj0], b1 = uv2[bj1], b2 = uv2[bj2];

                            if (Mathf.Abs(SignedArea2D(b0,b1,b2)) < 1e-10f) continue;

                            if (TrianglesOverlap2D(a0,a1,a2, b0,b1,b2))
                            {
                                if (sameSrc)
                                {
                                    overlapTrisSameSrc.Add(fI);
                                    overlapTrisSameSrc.Add(fJ);
                                }
                                else
                                {
                                    overlapTrisDiffSrc.Add(fI);
                                    overlapTrisDiffSrc.Add(fJ);
                                }
                                pairHasOverlap = true;
                                pairOverlapCount++;
                            }
                        }
                    }

                    if (pairHasOverlap)
                    {
                        if (sameSrc) overlapPairsSameSrc++;
                        else         overlapPairsDiffSrc++;

                        // ── Diagnostic: classify this overlap pair ──
                        if (uv2ShellMethod != null)
                        {
                            int mI = uv2ShellMethod[i], mJ = uv2ShellMethod[j];
                            int srcI = uv2ShellToSourceShell[i], srcJ = uv2ShellToSourceShell[j];
                            int tsI = uv2ShellToTargetShell[i], tsJ = uv2ShellToTargetShell[j];

                            // Count method involvement (diff-src only)
                            if (!sameSrc)
                            {
                                if (mI == 1 || mJ == 1) ovlpXform++;
                                else if (mI == 2 || mJ == 2) ovlpMerged++;
                                else ovlpInterp++;
                            }

                            string mNameI = kMethodNames[Mathf.Clamp(mI, 0, 3)];
                            string mNameJ = kMethodNames[Mathf.Clamp(mJ, 0, 3)];

                            // Log first 10 pairs in detail
                            if (overlapDetails.Count < 10)
                            {
                                overlapDetails.Add(
                                    $"  uv2sh[{i}]({mNameI},src{srcI},tgt{tsI},{facesI.Count}f) " +
                                    $"vs uv2sh[{j}]({mNameJ},src{srcJ},tgt{tsJ},{facesJ.Count}f): " +
                                    $"{pairOverlapCount} tri-pairs, " +
                                    (sameSrc ? "SAME-SRC(ok)" : "diff-src"));
                            }
                        }
                    }
                }
            }

            report.overlapShellPairs = overlapPairsDiffSrc;
            report.overlapTriangleCount = overlapTrisDiffSrc.Count;
            report.overlapSameSrcPairs = overlapPairsSameSrc;
            report.overlapSameSrcTriCount = overlapTrisSameSrc.Count;

            // Mark only diff-src overlapping triangles in perTriangle flags
            if (report.perTriangle != null)
            {
                foreach (int f in overlapTrisDiffSrc)
                    if (f < report.perTriangle.Length)
                        report.perTriangle[f] |= TriIssue.Overlap;
            }

            if (overlapPairsDiffSrc > 0)
            {
                UvtLog.Warn($"[Validator] UV2 overlap (diff-src): {overlapPairsDiffSrc} shell pairs, " +
                    $"{overlapTrisDiffSrc.Count} triangles affected");
            }

            if (overlapPairsSameSrc > 0)
            {
                UvtLog.Info($"[Validator] UV2 overlap (same-src, expected): {overlapPairsSameSrc} shell pairs, " +
                    $"{overlapTrisSameSrc.Count} triangles — tiling/symmetric geometry");
            }

            if ((overlapPairsDiffSrc > 0 || overlapPairsSameSrc > 0) && uv2ShellMethod != null)
            {
                int totalPairs = overlapPairsDiffSrc + overlapPairsSameSrc;
                if (overlapPairsDiffSrc > 0)
                {
                    UvtLog.Warn($"[Validator] Diff-src breakdown: " +
                        $"interp-only:{ovlpInterp} xform-involved:{ovlpXform} " +
                        $"merged-involved:{ovlpMerged}");
                }
                foreach (var detail in overlapDetails)
                {
                    bool isSameSrc = detail.Contains("SAME-SRC");
                    if (isSameSrc)
                        UvtLog.Info($"[Validator]{detail}");
                    else
                        UvtLog.Warn($"[Validator]{detail}");
                }
                if (totalPairs > overlapDetails.Count)
                    UvtLog.Info($"[Validator]  ... and {totalPairs - overlapDetails.Count} more pairs");
            }
        }

        // ─── AABB overlap test ───
        static bool AabbOverlap(UvShell a, UvShell b)
        {
            return a.boundsMin.x < b.boundsMax.x && a.boundsMax.x > b.boundsMin.x &&
                   a.boundsMin.y < b.boundsMax.y && a.boundsMax.y > b.boundsMin.y;
        }

        // ─── SAT 2D triangle-triangle overlap ───
        static bool TrianglesOverlap2D(Vector2 a0, Vector2 a1, Vector2 a2,
                                        Vector2 b0, Vector2 b1, Vector2 b2)
        {
            // 6 separating axes: 3 edge normals from each triangle
            if (SeparatedOnAxis(Perp(a1-a0), a0,a1,a2, b0,b1,b2)) return false;
            if (SeparatedOnAxis(Perp(a2-a1), a0,a1,a2, b0,b1,b2)) return false;
            if (SeparatedOnAxis(Perp(a0-a2), a0,a1,a2, b0,b1,b2)) return false;
            if (SeparatedOnAxis(Perp(b1-b0), a0,a1,a2, b0,b1,b2)) return false;
            if (SeparatedOnAxis(Perp(b2-b1), a0,a1,a2, b0,b1,b2)) return false;
            if (SeparatedOnAxis(Perp(b0-b2), a0,a1,a2, b0,b1,b2)) return false;
            return true;
        }

        static Vector2 Perp(Vector2 v) => new Vector2(-v.y, v.x);

        static bool SeparatedOnAxis(Vector2 axis, Vector2 a0, Vector2 a1, Vector2 a2,
                                                   Vector2 b0, Vector2 b1, Vector2 b2)
        {
            float sqLen = Vector2.Dot(axis, axis);
            if (sqLen < 1e-12f) return false; // degenerate axis

            float pa0 = Vector2.Dot(axis, a0), pa1 = Vector2.Dot(axis, a1), pa2 = Vector2.Dot(axis, a2);
            float pb0 = Vector2.Dot(axis, b0), pb1 = Vector2.Dot(axis, b1), pb2 = Vector2.Dot(axis, b2);

            float aMin = Mathf.Min(pa0, Mathf.Min(pa1, pa2));
            float aMax = Mathf.Max(pa0, Mathf.Max(pa1, pa2));
            float bMin = Mathf.Min(pb0, Mathf.Min(pb1, pb2));
            float bMax = Mathf.Max(pb0, Mathf.Max(pb1, pb2));

            return aMax <= bMin || bMax <= aMin;
        }

        static float SignedArea2D(Vector2 a, Vector2 b, Vector2 c)
        {
            return 0.5f * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));
        }
    }
}
