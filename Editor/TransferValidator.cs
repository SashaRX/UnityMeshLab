// TransferValidator.cs — Post-transfer quality validation
// Checks: inverted triangles, stretch outliers, zero-area, OOB vertices
// Called after GroupedShellTransfer.Transfer(), before Apply.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
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
        }

        public class ValidationReport
        {
            public int totalTriangles;
            public int invertedCount;
            public int stretchedCount;
            public int zeroAreaCount;
            public int oobCount;
            public int cleanCount;

            // UV2 shell overlap
            public int overlapShellPairs;       // number of shell pairs with actual tri overlap
            public int overlapTriangleCount;    // triangles involved in overlaps

            // Per-triangle data for visualization
            public TriIssue[] perTriangle;

            // Per-triangle stretch ratio (areaUV2/areaUV0), NaN if UV0 degenerate
            public float[] stretchRatios;

            // Shell-level stats
            public float[] shellMedianStretch;   // indexed by target shell
            public int[] shellInvertedCount;

            public bool HasIssues => invertedCount > 0 || stretchedCount > 0
                                  || zeroAreaCount > 0 || oobCount > 0
                                  || overlapShellPairs > 0;

            public string Summary =>
                $"Tri:{totalTriangles} Clean:{cleanCount} " +
                $"Inv:{invertedCount} Stretch:{stretchedCount} " +
                $"Zero:{zeroAreaCount} OOB:{oobCount}" +
                (overlapShellPairs > 0 ? $" Ovlp:{overlapShellPairs}pairs/{overlapTriangleCount}tri" : "");
        }

        /// <summary>
        /// Validate transfer result. Compares UV0 and UV2 per-triangle.
        /// stretchThreshold: ratio deviation from shell median to flag (e.g. 3.0 = 3x median)
        /// </summary>
        public static ValidationReport Validate(
            Mesh targetMesh,
            Vector2[] uv2,
            GroupedShellTransfer.TransferResult transferResult = null,
            float stretchThreshold = 3.0f)
        {
            var tris = targetMesh.triangles;
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
            };

            // ── Phase 1: Per-triangle metrics ──
            var areaUv0 = new float[faceCount];
            var areaUv2 = new float[faceCount];
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

                // Stretch ratio
                if (areaUv0[f] > 1e-10f)
                    report.stretchRatios[f] = areaUv2[f] / areaUv0[f];
                else
                    report.stretchRatios[f] = float.NaN;
            }

            // ── Phase 2: Compute per-shell median stretch ──
            // Use target shell info from transferResult if available
            int[] vertShell = transferResult?.vertexToSourceShell;
            int shellCount = 0;
            int[] triShell = null; // target shell per triangle

            if (vertShell != null)
            {
                // Determine triangle's shell from majority of its vertices
                triShell = new int[faceCount];
                var shellStretchLists = new Dictionary<int, List<float>>();

                for (int f = 0; f < faceCount; f++)
                {
                    int i0 = tris[f * 3];
                    int sh = (i0 < vertShell.Length) ? vertShell[i0] : -1;
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

                // Inverted: UV0 and UV2 have opposite winding
                if (Mathf.Abs(windingUv0[f]) > 1e-10f && Mathf.Abs(windingUv2[f]) > 1e-10f)
                {
                    if (Mathf.Sign(windingUv0[f]) != Mathf.Sign(windingUv2[f]))
                        report.perTriangle[f] |= TriIssue.Inverted;
                }

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

            // ── Count ──
            for (int f = 0; f < faceCount; f++)
            {
                var fl = report.perTriangle[f];
                if (fl == TriIssue.None) report.cleanCount++;
                if ((fl & TriIssue.Inverted) != 0) report.invertedCount++;
                if ((fl & TriIssue.Stretched) != 0) report.stretchedCount++;
                if ((fl & TriIssue.ZeroArea) != 0) report.zeroAreaCount++;
                if ((fl & TriIssue.OutOfBounds) != 0) report.oobCount++;
            }

            return report;
        }

        // ═══════════════════════════════════════════════════════════
        //  UV2 Shell Overlap Detection
        //  Extracts UV2 shells, finds AABB-overlapping pairs,
        //  then does SAT triangle-triangle test for actual overlaps.
        // ═══════════════════════════════════════════════════════════

        public static void DetectUv2Overlaps(Mesh targetMesh, Vector2[] uv2,
            ValidationReport report)
        {
            if (uv2 == null || uv2.Length == 0) return;
            var tris = targetMesh.triangles;
            int faceCount = tris.Length / 3;

            // Extract UV2 shells
            var shells = UvShellExtractor.Extract(uv2, tris);
            if (shells.Count < 2) return;

            // AABB overlap candidate pairs
            int pairsChecked = 0;
            int overlapPairs = 0;
            var overlapTris = new HashSet<int>();

            for (int i = 0; i < shells.Count; i++)
            {
                for (int j = i + 1; j < shells.Count; j++)
                {
                    if (!AabbOverlap(shells[i], shells[j])) continue;
                    pairsChecked++;

                    // SAT triangle-triangle test between shells
                    bool pairHasOverlap = false;
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
                                overlapTris.Add(fI);
                                overlapTris.Add(fJ);
                                pairHasOverlap = true;
                            }
                        }
                    }

                    if (pairHasOverlap) overlapPairs++;
                }
            }

            report.overlapShellPairs = overlapPairs;
            report.overlapTriangleCount = overlapTris.Count;

            // Mark overlapping triangles in perTriangle flags
            if (report.perTriangle != null)
            {
                foreach (int f in overlapTris)
                    if (f < report.perTriangle.Length)
                        report.perTriangle[f] |= TriIssue.Overlap;
            }

            if (overlapPairs > 0)
                Debug.LogWarning($"[Validator] UV2 overlap: {overlapPairs} shell pairs, " +
                    $"{overlapTris.Count} triangles affected");
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
