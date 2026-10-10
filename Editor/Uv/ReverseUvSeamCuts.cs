using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        readonly struct CutPoint
        {
            internal readonly double x, y;
            internal CutPoint(double x, double y) { this.x = x; this.y = y; }
            internal Vector3 Bary => new Vector3(Math.Abs(1 - x - y) < 1e-10 ? 0 : (float)(1 - x - y),
                Math.Abs(x) < 1e-10 ? 0 : (float)x, Math.Abs(y) < 1e-10 ? 0 : (float)y);
        }

        sealed class CutFace
        {
            internal Surface surface;
            internal int face;
            internal List<List<CutPoint>> polygons;
            internal bool split;
            internal bool locked;
        }

        static readonly CutPoint[] WholeTriangle = { new CutPoint(0, 0), new CutPoint(1, 0), new CutPoint(0, 1) };

        /// <summary>Cut rejected target faces in their own plane, using local donor
        /// rims/seams. Edge subdivisions propagate to every incident target face.
        /// No source position is moved and no donor seam is welded away.</summary>
        static void RefineAtDonorSeams(Surface[] donor, Surface[] targets, Options options, CancellationToken token)
        {
            var graph = ReverseUvCorrespondenceGraph.Build(donor, token);
            var seams = graph.links.Where(l => l.reason != "continuous" && l.reason != "nonmanifold").ToArray();
            if (seams.Length == 0) return;
            var edgePositions = new Vector3[seams.Length * 3];
            for (int i = 0; i < seams.Length; ++i)
            { edgePositions[i * 3] = seams[i].a; edgePositions[i * 3 + 1] = edgePositions[i * 3 + 2] = seams[i].b; }
            var bvh = new TriangleBvh(edgePositions, Enumerable.Range(0, edgePositions.Length).ToArray());
            var plans = new List<CutFace>(); long work = 0;
            float density = Density(donor);
            foreach (var s in targets)
                for (int f = 0; f < s.faces.Length; ++f)
                {
                    token.ThrowIfCancellationRequested();
                    var plan = new CutFace { surface = s, face = f, polygons = new List<List<CutPoint>> { WholeTriangle.ToList() } };
                    plans.Add(plan);
                    int sourceFace = s.input.sourceFaces == null ? f : s.input.sourceFaces[f];
                    plan.locked = options.seamCutExclusions != null && options.seamCutExclusions.TryGetValue((s.lod, s.node), out var excluded) && excluded.Contains(sourceFace);
                    if (plan.locked) { s.faces[f].cutRefusal = "preserved-finer-inheritance"; continue; }
                    if (s.faces[f].inherited || s.faces[f].rejection == "overlap") continue;
                    int t = f * 3;
                    var a = s.positions[s.indices[t]]; var b = s.positions[s.indices[t + 1]]; var c = s.positions[s.indices[t + 2]];
                    var bounds = new Bounds(a, Vector3.zero); bounds.Encapsulate(b); bounds.Encapsulate(c);
                    bounds.Expand(options.projectionReach * 2);
                    var candidates = new List<int>(); bvh.CollectOverlapping(bounds, candidates); candidates.Sort();
                    var normal = Normal(s, f);
                    foreach (int edge in candidates)
                    {
                        token.ThrowIfCancellationRequested();
                        if (++work > options.comparisonBudget) throw new InvalidOperationException("Reverse UV donor seam budget exceeded.");
                        var seam = seams[edge];
                        if (!seam.uses.Any(u => Vector3.Dot(Normal(u.surface, u.face), normal) >= options.normalDot)) continue;
                        var x = InTargetPlane(seam.a, a, b, c); var y = InTargetPlane(seam.b, a, b, c);
                        if (!SegmentInTriangle(x, y)) continue;
                        var divided = new List<List<CutPoint>>();
                        foreach (var polygon in plan.polygons)
                        {
                            var positive = ClipHalf(polygon, x, y, 1); var negative = ClipHalf(polygon, x, y, -1);
                            if (Area(positive) > 1e-12 && Area(negative) > 1e-12)
                            { divided.Add(positive); divided.Add(negative); }
                            else divided.Add(polygon);
                        }
                        // Optional refinement is bounded per input face. Keep the
                        // original complete face if its local arrangement explodes.
                        if (divided.Count > 128) { plan.polygons = new List<List<CutPoint>> { WholeTriangle.ToList() }; break; }
                        plan.polygons = divided;
                    }
                    plan.split = plan.polygons.Count > 1;
                }
            if (!plans.Any(p => p.split)) return;
            Dictionary<(Vector3, Vector3), SortedSet<double>> edgePoints;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                edgePoints = new Dictionary<(Vector3, Vector3), SortedSet<double>>();
                foreach (var plan in plans.Where(p => p.split))
                    foreach (var point in plan.polygons.SelectMany(p => p)) CollectEdgePoint(plan, point, edgePoints);
                var invalid = plans.Where(p => (p.locked && PlanEdges(p).Any(edgePoints.ContainsKey))
                    || !ValidPlan(p, edgePoints, density, options.maxAtlasSize, options.maxAnisotropy)).ToArray();
                if (invalid.Length == 0) break;
                var refusedEdges = new HashSet<(Vector3, Vector3)>(invalid.SelectMany(PlanEdges));
                var refused = plans.Where(p => p.split && (invalid.Contains(p) || PlanEdges(p).Any(refusedEdges.Contains))).ToArray();
                if (refused.Length == 0) throw new InvalidOperationException("Reverse UV uncut surface cannot be represented at float precision.");
                foreach (var plan in refused)
                {
                    plan.polygons = new List<List<CutPoint>> { WholeTriangle.ToList() }; plan.split = false;
                    plan.surface.faces[plan.face].cutRefusal = "seam-precision";
                }
            }
            foreach (var surface in targets)
                ApplyCuts(surface, plans.Where(p => p.surface == surface).ToArray(), edgePoints, token);
        }

        static IEnumerable<(Vector3, Vector3)> PlanEdges(CutFace plan)
        {
            var s = plan.surface; int start = plan.face * 3;
            for (int e = 0; e < 3; ++e) yield return ReverseUvCorrespondenceGraph.Key(s.positions[s.indices[start + e]], s.positions[s.indices[start + (e + 1) % 3]]);
        }

        static List<CutPoint> Triangulate(List<CutPoint> polygon)
        {
            if (polygon.Count == 3) return polygon;
            var triangles = new List<CutPoint>();
            var center = new CutPoint(polygon.Average(p => p.x), polygon.Average(p => p.y));
            for (int e = 0; e < polygon.Count; ++e) triangles.AddRange(new[] { center, polygon[e], polygon[(e + 1) % polygon.Count] });
            return triangles;
        }

        static bool ValidPlan(CutFace plan, Dictionary<(Vector3, Vector3), SortedSet<double>> edgePoints, float density, int maxAtlasSize, float maxAnisotropy)
        {
            var s = plan.surface; int start = plan.face * 3; double area = 0;
            foreach (var polygon in plan.polygons)
            {
                var expanded = InsertEdgePoints(s, start, polygon, edgePoints);
                var triangles = Triangulate(expanded);
                bool altered = plan.split || expanded.Count > 3;
                for (int t = 0; t < triangles.Count; t += 3)
                {
                    area += Side(triangles[t], triangles[t + 1], triangles[t + 2]) * .5;
                    var p = new Vector3[3];
                    for (int k = 0; k < 3; ++k)
                    {
                        var b = triangles[t + k].Bary;
                        p[k] = s.positions[s.indices[start]] * b.x + s.positions[s.indices[start + 1]] * b.y + s.positions[s.indices[start + 2]] * b.z;
                    }
                    if (!MeshGeometry.HasArea(p[0], p[1], p[2])) return false;
                    if (altered)
                    {
                        var frame = TriangleFrame(p[0], p[1], p[2]);
                        double longest = Math.Max(frame.length, Math.Max((p[2] - p[0]).magnitude, (p[2] - p[1]).magnitude));
                        // A new altitude needs several representable texels at
                        // the largest allowed pixel coordinate. Never remove a
                        // thin original face to meet this optional-cut criterion.
                        if (frame.length * frame.y / longest * density < maxAtlasSize * 1.1920928955078125e-7 * 8) return false;
                        if (s.faces[plan.face].inherited)
                        {
                            var uv = new Vector2[3];
                            for (int k = 0; k < 3; ++k)
                            {
                                var b = triangles[t + k].Bary;
                                uv[k] = s.pixels[start] * b.x + s.pixels[start + 1] * b.y + s.pixels[start + 2] * b.z;
                            }
                            if (TriangleAnisotropy(p[0], p[1], p[2], uv[0], uv[1], uv[2]) > maxAnisotropy) return false;
                        }
                    }
                }
            }
            return Math.Abs(area - .5) < 1e-7;
        }

        static CutPoint InTargetPlane(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            double ex = (double)b.x - a.x, ey = (double)b.y - a.y, ez = (double)b.z - a.z;
            double dx = (double)c.x - a.x, dy = (double)c.y - a.y, dz = (double)c.z - a.z;
            double px = (double)p.x - a.x, py = (double)p.y - a.y, pz = (double)p.z - a.z;
            double ee = ex * ex + ey * ey + ez * ez, dd = dx * dx + dy * dy + dz * dz;
            double ed = ex * dx + ey * dy + ez * dz, pe = px * ex + py * ey + pz * ez, pd = px * dx + py * dy + pz * dz;
            double determinant = ee * dd - ed * ed;
            return new CutPoint((pe * dd - pd * ed) / determinant, (pd * ee - pe * ed) / determinant);
        }

        static double Side(CutPoint a, CutPoint b, CutPoint p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
        static CutPoint Lerp(CutPoint a, CutPoint b, double t) => new CutPoint(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);

        static bool SegmentInTriangle(CutPoint a, CutPoint b)
        {
            double lo = 0, hi = 1;
            for (int e = 0; e < 3; ++e)
            {
                double x = Side(WholeTriangle[e], WholeTriangle[(e + 1) % 3], a);
                double y = Side(WholeTriangle[e], WholeTriangle[(e + 1) % 3], b);
                if (x < 0 && y < 0) return false;
                if (x < 0) lo = Math.Max(lo, x / (x - y));
                if (y < 0) hi = Math.Min(hi, x / (x - y));
            }
            return hi - lo > 1e-9;
        }

        static List<CutPoint> ClipHalf(List<CutPoint> polygon, CutPoint a, CutPoint b, int sign)
        {
            var output = new List<CutPoint>(); var previous = polygon[polygon.Count - 1];
            double last = sign * Side(a, b, previous);
            foreach (var point in polygon)
            {
                double next = sign * Side(a, b, point);
                if ((last > 1e-12 && next < -1e-12) || (last < -1e-12 && next > 1e-12))
                    output.Add(Lerp(previous, point, last / (last - next)));
                if (next >= -1e-12) output.Add(point);
                previous = point; last = next;
            }
            for (int i = output.Count - 1; i >= 0 && output.Count > 1; --i)
            {
                var x = output[i]; var y = output[(i + 1) % output.Count];
                if (Math.Abs(x.x - y.x) + Math.Abs(x.y - y.y) < 1e-10) output.RemoveAt(i);
            }
            return output;
        }

        static double Area(List<CutPoint> polygon)
        {
            double area = 0;
            for (int i = 1; i + 1 < polygon.Count; ++i) area += Side(polygon[0], polygon[i], polygon[i + 1]);
            return Math.Abs(area) * .5;
        }

        static void CollectEdgePoint(CutFace plan, CutPoint p, Dictionary<(Vector3, Vector3), SortedSet<double>> points)
        {
            for (int edge = 0; edge < 3; ++edge)
            {
                if (Math.Abs(Side(WholeTriangle[edge], WholeTriangle[(edge + 1) % 3], p)) > 1e-9) continue;
                double t = edge == 0 ? p.x : edge == 1 ? p.y : 1 - p.y;
                if (t <= 1e-8 || t >= 1 - 1e-8) continue;
                var s = plan.surface; int start = plan.face * 3;
                var a = s.positions[s.indices[start + edge]]; var b = s.positions[s.indices[start + (edge + 1) % 3]];
                var key = ReverseUvCorrespondenceGraph.Key(a, b);
                double canonical = Math.Round(a.Equals(key.Item1) ? t : 1 - t, 9);
                if (!points.TryGetValue(key, out var values)) { values = new SortedSet<double>(); points.Add(key, values); }
                values.Add(canonical);
            }
        }

        static void ApplyCuts(Surface s, CutFace[] plans, Dictionary<(Vector3, Vector3), SortedSet<double>> edgePoints, CancellationToken token)
        {
            var positions = new List<Vector3>(); var barycentrics = new List<Vector3>(); var sourceFaces = new List<int>();
            var pixels = new List<Vector2>(); var faces = new List<Face>(); int changed = 0;
            foreach (var plan in plans)
            {
                token.ThrowIfCancellationRequested(); int start = plan.face * 3;
                var polygons = plan.polygons.Select(p => InsertEdgePoints(s, start, p, edgePoints)).ToArray();
                bool altered = plan.split || polygons.Any(p => p.Count > 3);
                if (altered) ++changed;
                foreach (var polygon in polygons)
                {
                    var triangles = Triangulate(polygon);
                    for (int t = 0; t < triangles.Count; t += 3)
                    {
                        var face = s.faces[plan.face];
                        faces.Add(new Face { inherited = face.inherited, chart = face.chart, parentLod = face.parentLod,
                            parentMesh = face.parentMesh, parentFace = face.parentFace, layer = face.layer, distance = face.distance,
                            ambiguous = !altered && face.ambiguous, rejection = altered ? null : face.rejection, cutRefusal = face.cutRefusal,
                            intentionalOverlap = face.intentionalOverlap, overlayMesh = face.overlayMesh, overlayFace = face.overlayFace,
                            protectedInheritance = face.protectedInheritance });
                        sourceFaces.Add(s.sourceFaces[plan.face]);
                        for (int k = 0; k < 3; ++k)
                        {
                            var bary = triangles[t + k].Bary;
                            barycentrics.Add(bary);
                            positions.Add(s.positions[s.indices[start]] * bary.x + s.positions[s.indices[start + 1]] * bary.y + s.positions[s.indices[start + 2]] * bary.z);
                            pixels.Add(s.pixels[start] * bary.x + s.pixels[start + 1] * bary.y + s.pixels[start + 2] * bary.z);
                        }
                    }
                }
            }
            if (changed == 0) return;
            if (faces.Count > 250000) throw new InvalidOperationException("Reverse UV seam refinement exceeds 250000 output faces per input.");
            for (int t = 0; t < positions.Count; t += 3)
                if (!MeshGeometry.HasArea(positions[t], positions[t + 1], positions[t + 2]))
                    throw new InvalidOperationException($"Reverse UV seam refinement '{s.input.key}' loses a triangle at float precision; no result was published.");
            s.positions = positions.ToArray(); s.indices = Enumerable.Range(0, positions.Count).ToArray();
            s.pixels = pixels.ToArray(); s.faces = faces.ToArray(); s.sourceFaces = sourceFaces.ToArray();
            s.sourceBarycentrics = barycentrics.ToArray(); s.splitSourceFaces = changed;
        }

        static List<CutPoint> InsertEdgePoints(Surface s, int start, List<CutPoint> polygon, Dictionary<(Vector3, Vector3), SortedSet<double>> points)
        {
            var output = new List<CutPoint>();
            for (int i = 0; i < polygon.Count; ++i)
            {
                var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count]; output.Add(a);
                for (int edge = 0; edge < 3; ++edge)
                {
                    var x = WholeTriangle[edge]; var y = WholeTriangle[(edge + 1) % 3];
                    if (Math.Abs(Side(x, y, a)) > 1e-9 || Math.Abs(Side(x, y, b)) > 1e-9) continue;
                    var pa = s.positions[s.indices[start + edge]]; var pb = s.positions[s.indices[start + (edge + 1) % 3]];
                    var key = ReverseUvCorrespondenceGraph.Key(pa, pb);
                    if (!points.TryGetValue(key, out var values)) continue;
                    var between = new List<(double along, CutPoint point)>();
                    foreach (double value in values)
                    {
                        var p = Lerp(x, y, pa.Equals(key.Item1) ? value : 1 - value);
                        double dx = b.x - a.x, dy = b.y - a.y;
                        double along = ((p.x - a.x) * dx + (p.y - a.y) * dy) / (dx * dx + dy * dy);
                        if (along > 1e-8 && along < 1 - 1e-8) between.Add((along, p));
                    }
                    output.AddRange(between.OrderBy(p => p.along).Select(p => p.point));
                }
            }
            return output;
        }
    }
}
