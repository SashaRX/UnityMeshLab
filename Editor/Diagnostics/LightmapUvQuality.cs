using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Read-only diagnostic. No score is used as a transfer acceptance gate.</summary>
    internal static class LightmapUvQuality
    {
        internal sealed class Part
        {
            internal Vector3[] positions; // World space, captured on the editor thread.
            internal Vector3[] metricPositions; // Stored mesh corners before world-space rounding.
            internal Matrix4x4 metricToWorld = Matrix4x4.identity;
            internal Vector2[] uv;
            internal int[] triangles;
            internal string key;
            internal string sourceKey;
            internal ReverseUvAudit.ProvenanceEntry provenance;
        }

        [Serializable] internal sealed class Report
        {
            public int schema = 1, score, packingScore, stretchScore, densityScore, defectScore, lodScore = -1;
            public int faces, invalidFaces, collapsedFaces, zeroGeometryFaces, unexpectedOverlapPairs, allowedOverlapPairs;
            public bool complete = true, hasUv = true;
            public double fill, largestEmptyRectangle, worstStretch, stretchP95, severeArea, densityCv;
            public double lodCoverage, lodAlignedArea, lodMeanError, lodWorstError;
            public Vector2 bboxMin, bboxMax;
            public string limitation;
            [NonSerialized] internal byte[] occupancy;
            [NonSerialized] internal int rasterWidth = 256, rasterHeight = 256;
        }

        readonly struct Sample
        {
            internal readonly double value, weight;
            internal Sample(double value, double weight) { this.value = value; this.weight = weight; }
        }

        internal static Report Measure(Part[] parts, Part[] lod0 = null, CancellationToken token = default,
            long rasterBudget = 16000000, long overlapBudget = 2000000)
        {
            var report = new Report();
            var stretch = new List<Sample>(); var densities = new List<Sample>();
            var pixels = new List<Vector2>(); var indices = new List<int>();
            var owners = new List<(int part, int face)>();
            double area = 0, logStretch = 0;
            for (int p = 0; p < parts.Length; ++p)
                Append(parts[p], p, report, pixels, indices, owners, stretch, densities, ref area, ref logStretch, token);
            report.stretchP95 = Percentile(stretch, .95);
            report.severeArea = area > 0 ? stretch.Where(s => s.value > 4).Sum(s => s.weight) / area : 0;
            report.stretchScore = Score(area > 0 ? 100 * Math.Exp(-2 * logStretch / area) : 0);
            report.densityCv = Variation(densities);
            report.densityScore = Score(100 * Math.Exp(-2 * report.densityCv));
            ScanPacking(report, pixels.ToArray(), indices.ToArray(), token, rasterBudget);
            ScanOverlap(report, parts, pixels.ToArray(), indices.ToArray(), owners, token, overlapBudget);
            if (lod0 != null && lod0.Length > 0) CompareLods(report, parts, lod0, token);
            Finish(report);
            return report;
        }

        static void Append(Part part, int owner, Report report, List<Vector2> uv, List<int> indices,
            List<(int part, int face)> owners, List<Sample> stretch, List<Sample> densities,
            ref double area, ref double logStretch, CancellationToken token)
        {
            int count = part.triangles.Length / 3;
            report.faces += count;
            if (part.uv == null || part.uv.Length != part.positions.Length) {
                report.invalidFaces += count; report.hasUv = false; return;
            }
            for (int f = 0; f < count; ++f) {
                if ((f & 1023) == 0) token.ThrowIfCancellationRequested();
                int a = part.triangles[f * 3], b = part.triangles[f * 3 + 1], c = part.triangles[f * 3 + 2];
                if (!Index(a, part) || !Index(b, part) || !Index(c, part) || !Finite(part.uv[a])
                    || !Finite(part.uv[b]) || !Finite(part.uv[c]) || !Finite(part.positions[a])
                    || !Finite(part.positions[b]) || !Finite(part.positions[c])) { ++report.invalidFaces; continue; }
                indices.Add(uv.Count); uv.Add(part.uv[a]);
                indices.Add(uv.Count); uv.Add(part.uv[b]);
                indices.Add(uv.Count); uv.Add(part.uv[c]);
                owners.Add((owner, f));
                var frame = MetricFrame(part, a, b, c);
                double length = frame.length, cross = length * frame.y;
                if (!(length > 0 && cross > 0)) { ++report.zeroGeometryFaces; continue; }
                double worldArea = cross * .5, x = frame.x, y = frame.y;
                double ux = (double)part.uv[b].x - part.uv[a].x, uy = (double)part.uv[b].y - part.uv[a].y;
                double vx = (double)part.uv[c].x - part.uv[a].x, vy = (double)part.uv[c].y - part.uv[a].y;
                double j00 = ux / length, j10 = uy / length;
                double j01 = (vx - ux * x / length) / y, j11 = (vy - uy * x / length) / y;
                double aa = j00 * j00 + j10 * j10, bb = j01 * j01 + j11 * j11, ab = j00 * j01 + j10 * j11;
                double determinant = j00 * j11 - j10 * j01;
                double maximum = (aa + bb + Math.Sqrt(Math.Max(0, (aa - bb) * (aa - bb) + 4 * ab * ab))) * .5;
                double ratio = determinant != 0 ? maximum / Math.Abs(determinant) : double.PositiveInfinity;
                area += worldArea;
                if (double.IsNaN(ratio) || double.IsInfinity(ratio)) {
                    ++report.collapsedFaces; logStretch += worldArea * Math.Log(1e12); continue;
                }
                ratio = Math.Max(1, ratio);
                report.worstStretch = Math.Max(report.worstStretch, ratio);
                logStretch += worldArea * Math.Log(ratio);
                stretch.Add(new Sample(ratio, worldArea));
                densities.Add(new Sample(Math.Sqrt(Math.Abs(determinant)), worldArea));
            }
        }

        static void ScanPacking(Report report, Vector2[] uv, int[] triangles, CancellationToken token, long budget)
        {
            report.occupancy = new byte[report.rasterWidth * report.rasterHeight];
            if (uv.Length == 0) return;
            var min = uv[0]; var max = uv[0];
            foreach (var point in uv) { min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
            report.bboxMin = min; report.bboxMax = max;
            double width = (double)max.x - min.x, height = (double)max.y - min.y;
            if (!(width > 0 && height > 0)) return;
            long work = 0;
            for (int f = 0; f < triangles.Length; f += 3) {
                token.ThrowIfCancellationRequested();
                var a = RasterPoint(uv[triangles[f]], min, width, height, report);
                var b = RasterPoint(uv[triangles[f + 1]], min, width, height, report);
                var c = RasterPoint(uv[triangles[f + 2]], min, width, height, report);
                int x0 = Math.Clamp((int)Math.Floor(Math.Min(a.x, Math.Min(b.x, c.x))), 0, report.rasterWidth - 1);
                int x1 = Math.Clamp((int)Math.Floor(Math.Max(a.x, Math.Max(b.x, c.x))), 0, report.rasterWidth - 1);
                int y0 = Math.Clamp((int)Math.Floor(Math.Min(a.y, Math.Min(b.y, c.y))), 0, report.rasterHeight - 1);
                int y1 = Math.Clamp((int)Math.Floor(Math.Max(a.y, Math.Max(b.y, c.y))), 0, report.rasterHeight - 1);
                for (int y = y0; y <= y1; ++y)
                    for (int x = x0; x <= x1; ++x) {
                        if (++work > budget) { report.complete = false; report.limitation = "Packing scan budget reached"; return; }
                        if ((work & 4095) == 0) token.ThrowIfCancellationRequested();
                        int cell = y * report.rasterWidth + x;
                        if (report.occupancy[cell] == 15) continue;
                        for (int s = 0; s < 4; ++s) {
                            var point = new Vector2(x + (s % 2 == 0 ? .25f : .75f), y + (s < 2 ? .25f : .75f));
                            if (Contains(a, b, c, point)) report.occupancy[cell] |= (byte)(1 << s);
                        }
                    }
            }
            double covered = 0;
            foreach (byte mask in report.occupancy)
                for (int s = 0; s < 4; ++s) if ((mask & (1 << s)) != 0) ++covered;
            report.fill = covered / (report.occupancy.Length * 4);
            report.largestEmptyRectangle = EmptyRectangle(report.occupancy, report.rasterWidth, report.rasterHeight)
                / (double)report.occupancy.Length;
            report.packingScore = Score(Math.Min(100, report.fill / .75 * 100)
                * Math.Exp(-3 * Math.Max(0, report.largestEmptyRectangle - .05)));
        }

        static Vector2 RasterPoint(Vector2 p, Vector2 min, double width, double height, Report r)
            => new Vector2((float)(((double)p.x - min.x) / width * r.rasterWidth),
                (float)(((double)p.y - min.y) / height * r.rasterHeight));

        static bool Contains(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
        {
            double x = Cross(a, b, p), y = Cross(b, c, p), z = Cross(c, a, p);
            return Cross(a, b, c) != 0 && ((x >= 0 && y >= 0 && z >= 0) || (x <= 0 && y <= 0 && z <= 0));
        }

        static int EmptyRectangle(byte[] cells, int width, int height)
        {
            var heights = new int[width + 1]; var stack = new int[width + 1]; int best = 0;
            for (int y = 0; y < height; ++y) {
                for (int x = 0; x < width; ++x) heights[x] = cells[y * width + x] == 0 ? heights[x] + 1 : 0;
                int top = -1;
                for (int x = 0; x <= width; ++x) {
                    while (top >= 0 && heights[stack[top]] > heights[x]) {
                        int h = heights[stack[top--]];
                        best = Math.Max(best, h * (top >= 0 ? x - stack[top] - 1 : x));
                    }
                    stack[++top] = x;
                }
            }
            return best;
        }

        static void ScanOverlap(Report report, Part[] parts, Vector2[] uv, int[] indices,
            List<(int part, int face)> owners, CancellationToken token, long budget)
        {
            // Normalize for scale-invariant numerical predicates; occupancy always uses original bbox.
            double span = Math.Max((double)report.bboxMax.x - report.bboxMin.x, (double)report.bboxMax.y - report.bboxMin.y);
            if (!(span > 0)) return;
            // Keep normalization in double inside the predicates. Rounding a
            // new Float32 atlas here can make disjoint near-collinear rims cross.
            var scan = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry { uv = uv,
                indices = indices, charts = new int[uv.Length] }, token, comparisonBudget: budget, collectConflicts: true,
                coordinateOrigin:report.bboxMin,coordinateScale:1/span);
            report.complete &= scan.complete;
            if (!scan.complete) report.limitation = "Overlap scan incomplete; counts are lower bounds";
            var allowed = AllowedPairs(parts, token);
            foreach (var pair in scan.conflicts) {
                token.ThrowIfCancellationRequested();
                var a = owners[pair.a]; var b = owners[pair.b];
                if (allowed.Contains((a.part, a.face, b.part, b.face)) || allowed.Contains((b.part, b.face, a.part, a.face))) ++report.allowedOverlapPairs;
                else ++report.unexpectedOverlapPairs;
            }
        }

        static HashSet<(int partA, int faceA, int partB, int faceB)> AllowedPairs(Part[] parts, CancellationToken token)
        {
            var nodes = new Dictionary<(int lod, int node), int>(); var duplicates = new HashSet<(int lod, int node)>();
            var allowed = new HashSet<(int, int, int, int)>();
            for (int i = 0; i < parts.Length; ++i) {
                if (!ValidProvenance(parts[i])) continue;
                var p = parts[i].provenance; int node = Node(p);
                if (node < 0) continue;
                var key = (p.lod, node);
                if (nodes.ContainsKey(key)) duplicates.Add(key); else nodes.Add(key, i);
            }
            foreach (var key in duplicates) nodes.Remove(key); // Ambiguous identity must not authorize an overlap.
            for (int i = 0; i < parts.Length; ++i) {
                var p = parts[i].provenance;
                if (!ValidProvenance(parts[i]) || p.overlaps == null) continue;
                foreach (var relation in p.overlaps) {
                    token.ThrowIfCancellationRequested();
                    if (relation == null || !nodes.TryGetValue((relation.lod, relation.underMesh), out int under)
                        || under != i || !nodes.TryGetValue((relation.lod, relation.overMesh), out int over)) continue;
                    var upper = parts[over].provenance; int a = relation.underFace, b = relation.overFace;
                    if ((uint)a >= (uint)p.faceCount || (uint)b >= (uint)upper.faceCount) continue;
                    if ((p.IsInherited(a) || p.HasProjectionSupport(a)) && upper.IsInherited(b) && upper.IsIntentionalOverlap(b)
                        && p.Layer(a) < upper.Layer(b) && p.Chart(a) == upper.Chart(b))
                        allowed.Add((under, a, over, b));
                }
            }
            return allowed;
        }

        static bool ValidProvenance(Part p) => p.provenance != null && p.provenance.Describes(p.triangles.Length / 3)
            && p.provenance.key == (p.sourceKey ?? p.key);
        static int Node(ReverseUvAudit.ProvenanceEntry p) => p.chain?.FirstOrDefault(n => n.lod == p.lod && n.key == p.key)?.node ?? -1;

        static void CompareLods(Report report, Part[] parts, Part[] reference, CancellationToken token)
        {
            double total = 0, covered = 0, aligned = 0, error = 0;
            // Compare within the same renderer group. Unmatched geometry is visible as missing coverage.
            var donors = new Dictionary<string, Part>(StringComparer.Ordinal);
            foreach (var part in reference) if (part.key != null && !donors.ContainsKey(part.key)) donors.Add(part.key, part);
            bool measured = false;
            foreach (var part in parts) {
                if (part.uv == null || part.uv.Length != part.positions.Length) continue;
                if (part.key == null || !donors.TryGetValue(part.key, out var donor) || !SafeForBvh(donor)) {
                    total += SurfaceArea(part); continue;
                }
                measured = true;
                var bvh = new TriangleBvh(donor.positions, donor.triangles);
                var bounds = new Bounds(donor.positions[0], Vector3.zero);
                foreach (var p in donor.positions) bounds.Encapsulate(p);
                float reach = Math.Max(1e-7f, bounds.size.magnitude * .01f);
                // UV error is a fraction of the LOD0 rectangle diagonal, independent of Bakery scale/offset.
                var min = donor.uv[0]; var max = min;
                foreach (var uv in donor.uv) { min = Vector2.Min(min, uv); max = Vector2.Max(max, uv); }
                double uvScale = Math.Max(1e-20, (max - min).magnitude);
                for (int f = 0; f < part.triangles.Length; f += 3) {
                    if ((f & 1023) == 0) token.ThrowIfCancellationRequested();
                    int a = part.triangles[f], b = part.triangles[f + 1], c = part.triangles[f + 2];
                    if (!Index(a, part) || !Index(b, part) || !Index(c, part)) continue;
                    var frame = MetricFrame(part, a, b, c);
                    double weight = frame.length * frame.y / 6;
                    if (!(weight > 0) || double.IsInfinity(weight)) continue;
                    // Interior samples avoid making every UV seam an arbitrary nearest-face tie.
                    for (int sample = 0; sample < 3; ++sample) {
                        float wa = sample == 0 ? .6f : .2f, wb = sample == 1 ? .6f : .2f, wc = sample == 2 ? .6f : .2f;
                        total += weight;
                        var hit = bvh.FindNearest(part.positions[a] * wa + part.positions[b] * wb + part.positions[c] * wc, reach);
                        if (hit.triangleIndex < 0) continue;
                        int t = hit.triangleIndex * 3;
                        var target = part.uv[a] * wa + part.uv[b] * wb + part.uv[c] * wc;
                        var expected = donor.uv[donor.triangles[t]] * hit.barycentric.x
                            + donor.uv[donor.triangles[t + 1]] * hit.barycentric.y + donor.uv[donor.triangles[t + 2]] * hit.barycentric.z;
                        if (!Finite(target) || !Finite(expected)) continue;
                        double delta = (target - expected).magnitude / uvScale;
                        covered += weight; error += delta * weight;
                        report.lodWorstError = Math.Max(report.lodWorstError, delta);
                        if (delta <= .01) aligned += weight;
                    }
                }
            }
            if (!measured || !(total > 0)) return;
            report.lodCoverage = covered / total; report.lodAlignedArea = aligned / total;
            report.lodMeanError = covered > 0 ? error / covered : 0;
            report.lodScore = Score(100 * report.lodAlignedArea);
        }

        static bool SafeForBvh(Part p) => p.positions.Length > 0 && p.triangles.Length > 0
            && p.uv != null && p.uv.Length == p.positions.Length && p.positions.All(Finite)
            && p.uv.All(Finite) && p.triangles.All(i => Index(i, p));

        static double SurfaceArea(Part p)
        {
            double sum = 0;
            for (int f = 0; f < p.triangles.Length; f += 3) {
                int a = p.triangles[f], b = p.triangles[f + 1], c = p.triangles[f + 2];
                if (Index(a, p) && Index(b, p) && Index(c, p)) {
                    var frame = MetricFrame(p, a, b, c);
                    double area = frame.length * frame.y * .5;
                    if (!double.IsNaN(area) && !double.IsInfinity(area)) sum += area;
                }
            }
            return sum;
        }

        static void Finish(Report r)
        {
            r.defectScore = r.invalidFaces + r.collapsedFaces + r.zeroGeometryFaces + r.unexpectedOverlapPairs == 0 ? 100 : 0;
            double weighted = .2 * r.packingScore + .3 * r.stretchScore + .15 * r.densityScore + .25 * r.defectScore;
            r.score = Score(r.lodScore >= 0 ? weighted + .1 * r.lodScore : weighted / .9);
            if (r.invalidFaces > 0 || !r.hasUv || r.faces == 0) r.score = 0;
            else if (r.collapsedFaces > 0 || r.zeroGeometryFaces > 0 || r.worstStretch > 100) r.score = Math.Min(r.score, 39);
            else if (r.unexpectedOverlapPairs > 0 || r.worstStretch > 16) r.score = Math.Min(r.score, 49);
            else if (r.worstStretch > 4) r.score = Math.Min(r.score, 69);
            // A healthy map with sparse packing or unrelated LOD placement still needs attention.
            if (r.packingScore < 40) r.score = Math.Min(r.score, 79);
            if (r.packingScore < 20) r.score = Math.Min(r.score, 59);
            if (r.lodScore >= 0 && r.lodScore < 80) r.score = Math.Min(r.score, 79);
            if (r.lodScore >= 0 && r.lodScore < 60) r.score = Math.Min(r.score, 59);
            if (r.densityScore < 40) r.score = Math.Min(r.score, 69);
            if (!r.complete) r.score = Math.Min(r.score, 69);
        }

        static double Percentile(List<Sample> values, double fraction)
        {
            values.Sort((a, b) => a.value.CompareTo(b.value));
            double limit = values.Sum(v => v.weight) * fraction, sum = 0;
            foreach (var value in values) { sum += value.weight; if (sum >= limit) return value.value; }
            return 0;
        }
        static double Variation(List<Sample> values)
        {
            double weight = values.Sum(v => v.weight), mean = weight > 0 ? values.Sum(v => v.value * v.weight) / weight : 0;
            return mean > 0 ? Math.Sqrt(values.Sum(v => v.weight * (v.value - mean) * (v.value - mean)) / weight) / mean : 0;
        }
        static int Score(double value) => double.IsNaN(value) ? 0 : (int)Math.Round(Math.Clamp(value, 0, 100));
        static bool Index(int i, Part p) => (uint)i < (uint)p.positions.Length;
        static bool Finite(Vector2 p) => Finite(p.x) && Finite(p.y);
        static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
        static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        static (double length, double x, double y) MetricFrame(Part part, int a, int b, int c)
        {
            var positions = part.metricPositions ?? part.positions;
            return TransferUvQuality.WorldFrame(positions[a], positions[b], positions[c],
                part.metricPositions == null ? Matrix4x4.identity : part.metricToWorld);
        }
        static double Cross(Vector2 origin, Vector2 edge, Vector2 point) => ((double)edge.x - origin.x) * ((double)point.y - origin.y)
            - ((double)edge.y - origin.y) * ((double)point.x - origin.x);
    }
}
