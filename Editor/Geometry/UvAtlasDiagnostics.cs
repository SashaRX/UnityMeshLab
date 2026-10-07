using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Read-only UV triangle intersection diagnostics, including folded neighbours.
    /// Shared edges and corners are legal only when intersection area is zero.</summary>
    internal static class UvAtlasDiagnostics
    {
        /// <summary>Capture source geometry and exact settings; rounded inspector
        /// values and UV-only repack dumps cannot reproduce simplification output.
        /// Verbose only: the dump is the whole input mesh, written on every unwrap.</summary>
        internal static void CaptureInput(RemeshNative.IndexedMesh input, RemeshSettings settings)
        {
            if (UvtLog.Current < UvtLog.Level.Verbose || !UvtLog.IsCategoryEnabled(UvtLog.Category.RemeshDiag)) return;
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meshlab-uvmerge");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "unwrap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") +
                    "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".bin");
                using (var writer = new System.IO.BinaryWriter(System.IO.File.Create(path)))
                {
                    writer.Write(0x554D4C42); writer.Write(1); // magic and format version
                    writer.Write(JsonUtility.ToJson(settings));
                    writer.Write(input.positions.Length); writer.Write(input.indices.Length);
                    foreach (var p in input.positions) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
                    foreach (int i in input.indices) writer.Write(i);
                }
                UvtLog.Info(UvtLog.Category.RemeshDiag, "[UV] unwrap source mesh and settings captured to " + path);
                var stale = new List<string>(System.IO.Directory.GetFiles(dir, "unwrap_*.bin"));
                stale.Sort(StringComparer.Ordinal);
                for (int i = 0; i < stale.Count - 5; ++i) System.IO.File.Delete(stale[i]);
            }
            catch (Exception error) { UvtLog.Warn(UvtLog.Category.RemeshDiag, "[UV] input capture failed: " + error.Message); }
        }

        internal sealed class Report
        {
            internal int pairs, sameChartPairs, crossChartPairs, degenerateFaces, invalidFaces, outOfBoundsVertices;
            internal long comparisons;
            internal bool complete = true;
            // Sum over pairs, not the union area: triple coverage is counted more than once.
            internal double pairAreaSum;
            internal readonly List<string> samples = new List<string>();
            internal readonly List<(int a, int b)> conflicts = new List<(int, int)>();
        }

        struct Point
        {
            internal double x, y;
            internal Point(Vector2 p) { x = p.x; y = p.y; }
        }

        struct Triangle
        {
            internal int face, chart;
            internal Point a, b, c;
            internal double minX, maxX, minY, maxY, area;
        }

        /// <summary>Every overlapping UV triangle pair of the atlas. <paramref name="comparisonBudget"/>
        /// bounds the candidate pairs examined (negative = scaled from the face count); a scan
        /// that runs out reports <c>complete = false</c> and its counts are lower bounds.</summary>
        internal static Report Measure(RemeshNative.Geometry g, CancellationToken token,
            bool sameChartOnly = false, long comparisonBudget = -1, bool collectConflicts = false)
        {
            token.ThrowIfCancellationRequested();
            var report = new Report();
            foreach (var uv in g.uv)
                if (Finite(uv) && (uv.x < -1e-4f || uv.x > 1.0001f || uv.y < -1e-4f || uv.y > 1.0001f))
                    ++report.outOfBoundsVertices;
            var triangles = new List<Triangle>(g.indices.Length / 3);
            for (int f = 0; f < g.indices.Length / 3; ++f)
            {
                if ((f & 4095) == 0) token.ThrowIfCancellationRequested();
                int i = g.indices[f * 3], j = g.indices[f * 3 + 1], k = g.indices[f * 3 + 2];
                var a = g.uv[i]; var b = g.uv[j]; var c = g.uv[k];
                if (!Finite(a) || !Finite(b) || !Finite(c)) { ++report.invalidFaces; continue; }
                var t = new Triangle {
                    face = f, chart = g.charts[i], a = new Point(a), b = new Point(b), c = new Point(c),
                    minX = Math.Min(a.x, Math.Min(b.x, c.x)), maxX = Math.Max(a.x, Math.Max(b.x, c.x)),
                    minY = Math.Min(a.y, Math.Min(b.y, c.y)), maxY = Math.Max(a.y, Math.Max(b.y, c.y))
                };
                t.area = Math.Abs(Cross(t.a, t.b, t.c)) * .5;
                if (t.area <= 1e-16) { ++report.degenerateFaces; continue; }
                triangles.Add(t);
            }
            int count = triangles.Count;
            if (count < 2) return report;
            // A uniform grid over the atlas, each pair tested once from the lowest-index
            // cell both bounding boxes share. The former x-sweep visited every pair whose
            // x-extents overlapped, about N·√N of them on a packed atlas, and ran out of
            // its fixed budget past some twenty thousand faces; the repair then refused
            // the atlas as uncertifiable. Cells scale with the face count, so a packed
            // atlas costs a few comparisons per face whatever its size.
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var t in triangles) { minX = Math.Min(minX, t.minX); maxX = Math.Max(maxX, t.maxX); minY = Math.Min(minY, t.minY); maxY = Math.Max(maxY, t.maxY); }
            double width = Math.Max(maxX - minX, 1e-12), height = Math.Max(maxY - minY, 1e-12);
            int resolution = Math.Clamp((int)Math.Ceiling(Math.Sqrt(count)), 1, 4096);
            var cells = new int[count * 4]; // cx0, cy0, cx1, cy1 per triangle
            long entries;
            while (true)
            {
                entries = 0;
                for (int i = 0; i < count; ++i)
                {
                    var t = triangles[i];
                    int cx0 = Cell(t.minX, minX, width, resolution), cx1 = Cell(t.maxX, minX, width, resolution);
                    int cy0 = Cell(t.minY, minY, height, resolution), cy1 = Cell(t.maxY, minY, height, resolution);
                    cells[i * 4] = cx0; cells[i * 4 + 1] = cy0; cells[i * 4 + 2] = cx1; cells[i * 4 + 3] = cy1;
                    entries += (long)(cx1 - cx0 + 1) * (cy1 - cy0 + 1);
                }
                // Large triangles span many cells; a coarser grid keeps the lists bounded.
                if (entries <= 8L * count + 65536 || resolution == 1) break;
                resolution = Math.Max(1, resolution / 2);
            }
            int cellCount = resolution * resolution;
            var offsets = new int[cellCount + 1];
            for (int i = 0; i < count; ++i)
                for (int cy = cells[i * 4 + 1]; cy <= cells[i * 4 + 3]; ++cy)
                    for (int cx = cells[i * 4]; cx <= cells[i * 4 + 2]; ++cx) ++offsets[cy * resolution + cx + 1];
            for (int c = 0; c < cellCount; ++c) offsets[c + 1] += offsets[c];
            var members = new int[entries];
            var fill = new int[cellCount];
            for (int i = 0; i < count; ++i)
                for (int cy = cells[i * 4 + 1]; cy <= cells[i * 4 + 3]; ++cy)
                    for (int cx = cells[i * 4]; cx <= cells[i * 4 + 2]; ++cx) { int c = cy * resolution + cx; members[offsets[c] + fill[c]++] = i; }
            if (comparisonBudget < 0) comparisonBudget = Math.Max(2_000_000L, 256L * count);
            var bufferA = new Point[8]; var bufferB = new Point[8];
            Scan();
            // Deterministic whatever the grid: the repair's greedy cover walks this list.
            report.conflicts.Sort((x, y) => x.a != y.a ? x.a.CompareTo(y.a) : x.b.CompareTo(y.b));
            return report;

            void Scan()
            {
                for (int c = 0; c < cellCount; ++c)
                {
                    int cx = c % resolution, cy = c / resolution;
                    int begin = offsets[c], end = offsets[c + 1];
                    for (int m = begin; m < end; ++m)
                    {
                        int i = members[m]; var a = triangles[i];
                        for (int n = m + 1; n < end; ++n)
                        {
                            int j = members[n];
                            if (++report.comparisons > comparisonBudget) { report.complete = false; return; }
                            if ((report.comparisons & 4095) == 0) token.ThrowIfCancellationRequested();
                            // Once per pair: only the lowest-index cell the two boxes share tests it.
                            if (Math.Max(cells[i * 4], cells[j * 4]) != cx || Math.Max(cells[i * 4 + 1], cells[j * 4 + 1]) != cy) continue;
                            var b = triangles[j];
                            if (sameChartOnly && a.chart != b.chart || b.minX >= a.maxX || a.minX >= b.maxX || b.minY >= a.maxY || a.minY >= b.maxY) continue;
                            double area = IntersectionArea(a, b, bufferA, bufferB);
                            // Ignore numerical slivers, not all neighbours. Relative tolerance
                            // scales with the smaller triangle; absolute floor is in UV units².
                            if (area <= Math.Max(1e-16, Math.Min(a.area, b.area) * 1e-8)) continue;
                            ++report.pairs;
                            if (collectConflicts) report.conflicts.Add((Math.Min(a.face, b.face), Math.Max(a.face, b.face)));
                            if (a.chart == b.chart) ++report.sameChartPairs; else ++report.crossChartPairs;
                            report.pairAreaSum += area;
                            if (report.samples.Count < 8)
                                report.samples.Add(FormattableString.Invariant($"faces={a.face}/{b.face} charts={a.chart}/{b.chart} area={area:G6}"));
                        }
                    }
                }
            }
        }

        static int Cell(double value, double origin, double span, int resolution)
            => Math.Clamp((int)((value - origin) / span * resolution), 0, resolution - 1);

        /// <summary>Reusable scratch buffers for exact area checks during merge trials.</summary>
        internal sealed class IntersectionTest
        {
            readonly Point[] bufferA = new Point[8], bufferB = new Point[8];

            internal bool Overlaps(RemeshNative.Geometry g, int faceA, int faceB)
            {
                var a = FromFace(g, faceA); var b = FromFace(g, faceB);
                return IntersectionArea(a, b, bufferA, bufferB) > Math.Max(1e-16, Math.Min(a.area, b.area) * 1e-8);
            }

            static Triangle FromFace(RemeshNative.Geometry g, int face)
            {
                var t = new Triangle {
                    a = new Point(g.uv[g.indices[face * 3]]),
                    b = new Point(g.uv[g.indices[face * 3 + 1]]),
                    c = new Point(g.uv[g.indices[face * 3 + 2]])
                };
                t.area = Math.Abs(Cross(t.a, t.b, t.c)) * .5;
                return t;
            }
        }

        static bool Finite(Vector2 p)
            => !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsInfinity(p.x) && !float.IsInfinity(p.y);

        static double Cross(Point a, Point b, Point p)
            => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        static double IntersectionArea(Triangle a, Triangle b, Point[] bufferA, Point[] bufferB)
        {
            bufferA[0] = a.a; bufferA[1] = a.b; bufferA[2] = a.c;
            int count = 3;
            double sign = Cross(b.a, b.b, b.c) > 0 ? 1 : -1;
            count = Clip(bufferA, count, bufferB, b.a, b.b, sign);
            count = Clip(bufferB, count, bufferA, b.b, b.c, sign);
            count = Clip(bufferA, count, bufferB, b.c, b.a, sign);
            double twiceArea = 0;
            for (int i = 1; i + 1 < count; ++i) twiceArea += Cross(bufferB[0], bufferB[i], bufferB[i + 1]);
            return Math.Abs(twiceArea) * .5;
        }

        static int Clip(Point[] input, int count, Point[] output, Point edgeA, Point edgeB, double sign)
        {
            if (count == 0) return 0;
            int written = 0;
            Point previous = input[count - 1];
            double previousSide = sign * Cross(edgeA, edgeB, previous);
            for (int i = 0; i < count; ++i)
            {
                Point current = input[i];
                double currentSide = sign * Cross(edgeA, edgeB, current);
                if ((previousSide >= 0) != (currentSide >= 0))
                {
                    double t = previousSide / (previousSide - currentSide);
                    output[written++] = new Point { x = previous.x + t * (current.x - previous.x), y = previous.y + t * (current.y - previous.y) };
                }
                if (currentSide >= 0) output[written++] = current;
                previous = current; previousSide = currentSide;
            }
            return written;
        }

        internal static void Log(RemeshNative.Geometry g, string phase, CancellationToken token, bool sameChartOnly = false)
        {
            if (UvtLog.Current < UvtLog.Level.Info || !UvtLog.IsCategoryEnabled(UvtLog.Category.RemeshDiag)) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var quality = UvChartQuality.Measure(g, token);
            var overlap = Measure(g, token, sameChartOnly);
            var packing = UvPackingQuality.Measure(g, token);
            UvtLog.Info(UvtLog.Category.RemeshDiag, FormattableString.Invariant(
                $"[UV] {phase}: faces={g.indices.Length / 3}, charts={g.chartCount}, small={quality.smallCharts}, mean={quality.meanStretch:G6}, worst={quality.maxStretch:G6}, valid={quality.valid}; uvArea={packing.filledArea:G6}, chartDensityCV={packing.densityDeviation:G6}; overlapPairs={overlap.pairs}, sameChart={overlap.sameChartPairs}, crossChart={overlap.crossChartPairs}, pairAreaSum={overlap.pairAreaSum:G6}, degenerate={overlap.degenerateFaces}, invalid={overlap.invalidFaces}, outOfBoundsVertices={overlap.outOfBoundsVertices}; scope={(sameChartOnly ? "within-chart (unpacked)" : "whole-atlas")}, complete={overlap.complete}, comparisons={overlap.comparisons}, ms={clock.ElapsedMilliseconds}"));
            foreach (string sample in overlap.samples)
                UvtLog.Info(UvtLog.Category.RemeshDiag, $"[UV] {phase}: overlap {sample}");
            if (!overlap.complete)
                UvtLog.Warn(UvtLog.Category.RemeshDiag, $"[UV] {phase}: overlap scan budget reached; counts are lower bounds, zero does not prove no overlaps.");
        }
    }
}
