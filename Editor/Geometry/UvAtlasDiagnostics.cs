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

        internal static Report Measure(RemeshNative.Geometry g, CancellationToken token,
            bool sameChartOnly = false, long comparisonBudget = 2_000_000, bool collectConflicts = false)
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
            triangles.Sort((a, b) => {
                int order = a.minX.CompareTo(b.minX);
                return order != 0 ? order : a.face.CompareTo(b.face);
            });
            var bufferA = new Point[8]; var bufferB = new Point[8];
            for (int i = 0; i < triangles.Count; ++i)
            {
                var a = triangles[i];
                for (int j = i + 1; j < triangles.Count; ++j)
                {
                    var b = triangles[j];
                    if (b.minX >= a.maxX) break;
                    if (++report.comparisons > comparisonBudget) { report.complete = false; return report; }
                    if ((report.comparisons & 4095) == 0) token.ThrowIfCancellationRequested();
                    if (sameChartOnly && a.chart != b.chart || b.minY >= a.maxY || a.minY >= b.maxY) continue;
                    double area = IntersectionArea(a, b, bufferA, bufferB);
                    // Ignore numerical slivers, not all neighbours. Relative tolerance
                    // scales with the smaller triangle; absolute floor is in UV units².
                    if (area <= Math.Max(1e-16, Math.Min(a.area, b.area) * 1e-8)) continue;
                    ++report.pairs;
                    if (collectConflicts) report.conflicts.Add((a.face, b.face));
                    if (a.chart == b.chart) ++report.sameChartPairs; else ++report.crossChartPairs;
                    report.pairAreaSum += area;
                    if (report.samples.Count < 8)
                        report.samples.Add(FormattableString.Invariant($"faces={a.face}/{b.face} charts={a.chart}/{b.chart} area={area:G6}"));
                }
            }
            return report;
        }

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
            UvtLog.Info(UvtLog.Category.RemeshDiag, FormattableString.Invariant(
                $"[UV] {phase}: faces={g.indices.Length / 3}, charts={g.chartCount}, small={quality.smallCharts}, mean={quality.meanStretch:G6}, worst={quality.maxStretch:G6}, valid={quality.valid}; overlapPairs={overlap.pairs}, sameChart={overlap.sameChartPairs}, crossChart={overlap.crossChartPairs}, pairAreaSum={overlap.pairAreaSum:G6}, degenerate={overlap.degenerateFaces}, invalid={overlap.invalidFaces}, outOfBoundsVertices={overlap.outOfBoundsVertices}; scope={(sameChartOnly ? "within-chart (unpacked)" : "whole-atlas")}, complete={overlap.complete}, comparisons={overlap.comparisons}, ms={clock.ElapsedMilliseconds}"));
            foreach (string sample in overlap.samples)
                UvtLog.Info(UvtLog.Category.RemeshDiag, $"[UV] {phase}: overlap {sample}");
            if (!overlap.complete)
                UvtLog.Warn(UvtLog.Category.RemeshDiag, $"[UV] {phase}: overlap scan budget reached; counts are lower bounds, zero does not prove no overlaps.");
        }
    }
}
