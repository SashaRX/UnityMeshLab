using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Fit final boxes or extruded profiles before a 3D voxel decomposition.
    /// Fit distances and projected gaps are sampled estimates, not exact Hausdorff bounds.</summary>
    internal static class CollisionShapeFitter
    {
        sealed class Region
        {
            internal RemeshNative.IndexedMesh source, proxy;
            internal List<Vector2> profile;
            internal float gap, excessArea, error, score;
        }

        internal static bool ValidSettings(CollisionMeshBuilder.ConvexDecompSettings s) =>
            Finite(s.shapeFitError) && s.shapeFitError >= .0001f && s.shapeFitError <= .1f &&
            Finite(s.projectionThicknessRatio) && s.projectionThicknessRatio >= .01f && s.projectionThicknessRatio <= 1;

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static bool TryBuild(Mesh mesh, CollisionMeshBuilder.ConvexDecompSettings settings, int group,
            out CollisionMeshBuilder.ConvexDecompResult result)
        {
            result = new CollisionMeshBuilder.ConvexDecompResult { error = "Degenerate bounds." };
            CollisionMeshBuilder.ExtractMeshData(mesh, out var points, out var indices);
            var source = new RemeshNative.IndexedMesh { positions = points, indices = indices };
            var bounds = MeshGeometry.PrincipalBounds(new List<Vector3>(new HashSet<Vector3>(points)));
            float diagonal = bounds.Size.magnitude;
            if (diagonal <= 0) return false;
            float tolerance = diagonal * settings.shapeFitError;
            var box = CollisionElementPreprocessor.Box(bounds);
            float boxError = Mathf.Max(CollisionElementPreprocessor.MaxSurfaceDistance(source, box), BoxFaceError(box, source));
            if (Mathf.Min(bounds.Size.x, Mathf.Min(bounds.Size.y, bounds.Size.z)) > diagonal * .00001f && boxError <= tolerance)
            {
                result = Result(mesh.name, new[] { box }, new[] { new CollisionMeshBuilder.ShapeReport {
                    kind = CollisionMeshBuilder.ShapeKind.Box, groupIndex = group,
                    center = bounds.Point((bounds.min + bounds.max) * .5f), size = bounds.Size,
                    rotation = Quaternion.LookRotation(bounds.axes[2], bounds.axes[1]),
                    sampledError = boxError / diagonal,
                    detail = "Final oriented box; surface samples include an 8x8 grid on every face." } });
                return true;
            }
            int normalAxis = 0;
            for (int i = 1; i < 3; i++) if (bounds.Size[i] < bounds.Size[normalAxis]) normalAxis = i;
            int u = (normalAxis + 1) % 3, v = (normalAxis + 2) % 3;
            if (bounds.Size[normalAxis] > Mathf.Min(bounds.Size[u], bounds.Size[v]) * settings.projectionThicknessRatio) {
                result.error = $"Box fit {boxError / diagonal:P1}; thickness ratio {bounds.Size[normalAxis] / Mathf.Min(bounds.Size[u], bounds.Size[v]):P1} exceeds projection limit.";
                return false;
            }
            var frame = new MeshGeometry.OrientedBounds { origin = bounds.origin,
                axes = new[] { bounds.axes[u], bounds.axes[v], bounds.axes[normalAxis] } };
            var local = new Vector3[points.Length];
            for (int i = 0; i < local.Length; i++) {
                var delta = points[i] - frame.origin;
                local[i] = new Vector3(Vector3.Dot(delta, frame.axes[0]), Vector3.Dot(delta, frame.axes[1]), Vector3.Dot(delta, frame.axes[2]));
            }
            int corners = Mathf.Clamp(settings.maxVertsPerHull, 8, CollisionMeshBuilder.MaxConvexVertices) / 2;
            var regions = new List<Region> { Analyze(new RemeshNative.IndexedMesh { positions = local, indices = indices }, corners, diagonal) };
            int budget = Mathf.Clamp(settings.maxHulls, 1, 64);
            while (true)
            {
                int worst = -1;
                for (int i = 0; i < regions.Count; i++)
                    if (!Fits(regions[i], tolerance) && (worst < 0 || regions[i].score > regions[worst].score)) worst = i;
                if (worst < 0) break;
                if (regions.Count >= budget || !Split(regions[worst], corners, diagonal, out var left, out var right)) {
                    result.error = $"Profile needs more than {regions.Count} leaves or an improving cut: sampled fit {regions[worst].error / diagonal:P1}, projected gap {regions[worst].gap / diagonal:P1}.";
                    return false;
                }
                regions[worst] = left;
                regions.Insert(worst + 1, right);
            }
            var proxies = new List<RemeshNative.IndexedMesh>();
            var reports = new List<CollisionMeshBuilder.ShapeReport>();
            foreach (var fittedRegion in regions)
            {
                var region = fittedRegion;
                // The vertex limit is a ceiling, not a target. Prefer the lowest
                // corner count that still meets the same surface and gap tolerance.
                for (int count = 3; count < fittedRegion.profile.Count; count++) {
                    var reduced = Analyze(fittedRegion.source, count, diagonal);
                    if (Fits(reduced, tolerance)) { region = reduced; break; }
                }
                for (int i = 0; i < region.proxy.positions.Length; i++) region.proxy.positions[i] = frame.Point(region.proxy.positions[i]);
                proxies.Add(region.proxy);
                reports.Add(new CollisionMeshBuilder.ShapeReport {
                    kind = CollisionMeshBuilder.ShapeKind.Projected, groupIndex = group,
                    projectionNormal = frame.axes[2], profileVertices = region.profile.Count,
                    sampledError = region.error / diagonal,
                    detail = "Convex 2D profile extruded along the least-thickness axis; gaps sampled on a 48x48 grid." });
            }
            result = Result(mesh.name, proxies, reports);
            return true;
        }

        static bool Fits(Region r, float tolerance) => r.proxy != null && Finite(r.gap) && Finite(r.error) && r.gap <= tolerance && r.error <= tolerance;

        static Region Analyze(RemeshNative.IndexedMesh source, int corners, float diagonal)
        {
            var xy = new List<Vector2>();
            float low = float.MaxValue, high = float.MinValue;
            foreach (var p in source.positions) { xy.Add(new Vector2(p.x, p.y)); low = Mathf.Min(low, p.z); high = Mathf.Max(high, p.z); }
            var profile = Hull(xy);
            var region = new Region { source = source, profile = profile, error = float.MaxValue, score = float.MaxValue };
            if (profile.Count < 3) return region;
            Reduce(profile, corners);
            // PhysX needs positive volume, including for an authored flat surface.
            float minimumThickness = diagonal * .001f;
            if (high - low < minimumThickness) { float center = (high + low) * .5f; low = center - minimumThickness * .5f; high = center + minimumThickness * .5f; }
            region.proxy = Extrude(profile, low, high);
            ProjectionGap(source, profile, out region.gap, out region.excessArea);
            region.error = CollisionElementPreprocessor.MaxSurfaceDistance(source, region.proxy);
            float area = 0;
            for (int i = 0; i < profile.Count; i++) area += Cross(profile[i], profile[(i+1) % profile.Count]);
            region.score = region.excessArea + Mathf.Abs(area) * 5 * (region.error * region.error + region.gap * region.gap) / (diagonal * diagonal);
            return region;
        }

        static bool Split(Region region, int corners, float diagonal, out Region left, out Region right)
        {
            left = right = null;
            float best = float.MaxValue;
            var min = region.profile[0]; var max = min;
            foreach (var p in region.profile) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            for (int axis = 0; axis < 2; axis++)
                foreach (float fraction in new[] { .25f, .5f, .75f })
                {
                    float cut = Mathf.Lerp(min[axis], max[axis], fraction);
                    var a = Clip(region.source, axis, cut, true);
                    var b = Clip(region.source, axis, cut, false);
                    if (a.indices.Length == 0 || b.indices.Length == 0) continue;
                    var ra = Analyze(a, corners, diagonal); var rb = Analyze(b, corners, diagonal);
                    if (ra.proxy == null || rb.proxy == null) continue;
                    float score = ra.score + rb.score;
                    if (score < best) { best = score; left = ra; right = rb; }
                }
            return left != null && best < region.score * .98f;
        }

        /// <summary>Clip whole triangles so faces crossing a split plane remain on both sides.</summary>
        internal static RemeshNative.IndexedMesh Clip(RemeshNative.IndexedMesh source, int axis, float cut, bool lower)
        {
            var points = new List<Vector3>(); var triangles = new List<int>();
            for (int i = 0; i < source.indices.Length; i += 3)
            {
                var polygon = new[] { source.positions[source.indices[i]], source.positions[source.indices[i+1]], source.positions[source.indices[i+2]] };
                var clipped = new List<Vector3>();
                var previous = polygon[2]; bool previousInside = lower ? previous[axis] <= cut : previous[axis] >= cut;
                foreach (var current in polygon)
                {
                    bool inside = lower ? current[axis] <= cut : current[axis] >= cut;
                    if (inside != previousInside) {
                        var intersection = Vector3.LerpUnclamped(previous, current, (cut - previous[axis]) / (current[axis] - previous[axis]));
                        intersection[axis] = cut; clipped.Add(intersection);
                    }
                    if (inside) clipped.Add(current);
                    previous = current; previousInside = inside;
                }
                if (clipped.Count < 3) continue;
                for (int j = 1; j < clipped.Count - 1; j++)
                    if (Vector3.Cross(clipped[j] - clipped[0], clipped[j+1] - clipped[0]).sqrMagnitude > 0) {
                        int offset = points.Count;
                        points.Add(clipped[0]); points.Add(clipped[j]); points.Add(clipped[j+1]);
                        triangles.AddRange(new[] { offset, offset+1, offset+2 });
                    }
            }
            return new RemeshNative.IndexedMesh { positions = points.ToArray(), indices = triangles.ToArray() };
        }

        static void ProjectionGap(RemeshNative.IndexedMesh source, List<Vector2> profile, out float gap, out float excessArea)
        {
            var min = profile[0]; var max = min;
            foreach (var p in profile) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            // The UV BVH uses an absolute degeneracy epsilon. Normalize coordinates
            // for its queries and convert the resulting distance back to mesh units.
            float scale = Mathf.Max(max.x - min.x, max.y - min.y);
            int faces = source.indices.Length / 3;
            var a = new Vector2[faces]; var b = new Vector2[faces]; var c = new Vector2[faces];
            for (int i = 0; i < faces; i++) {
                a[i] = ((Vector2)source.positions[source.indices[i*3]] - min) / scale;
                b[i] = ((Vector2)source.positions[source.indices[i*3+1]] - min) / scale;
                c[i] = ((Vector2)source.positions[source.indices[i*3+2]] - min) / scale;
            }
            var bvh = new TriangleBvh2D(a, b, c);
            gap = 0; int empty = 0;
            var step = (max - min) / 48;
            for (int y = 0; y < 48; y++) for (int x = 0; x < 48; x++)
            {
                var p = min + new Vector2((x + .5f) * step.x, (y + .5f) * step.y);
                if (!Inside(profile, p)) continue;
                float distance = Mathf.Sqrt(bvh.FindNearest((p - min) / scale).distSq) * scale;
                gap = Mathf.Max(gap, distance);
                if (distance > step.magnitude * .25f) empty++;
            }
            excessArea = empty * step.x * step.y;
        }

        static bool Inside(List<Vector2> polygon, Vector2 point)
        {
            for (int i = 0; i < polygon.Count; i++)
                if (Cross(polygon[(i+1) % polygon.Count] - polygon[i], point - polygon[i]) < 0) return false;
            return true;
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static List<Vector2> Hull(List<Vector2> points)
        {
            points.Sort((a,b) => {
                int xOrder = a.x.CompareTo(b.x);
                return xOrder == 0 ? a.y.CompareTo(b.y) : xOrder;
            });
            var min = points[0]; var max = min;
            foreach (var p in points) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            float areaEpsilon = (max - min).sqrMagnitude * .000001f;
            var unique = new List<Vector2>();
            foreach (var p in points) if (unique.Count == 0 || !p.Equals(unique[unique.Count-1])) unique.Add(p);
            var hull = new List<Vector2>();
            foreach (var p in unique) {
                while (hull.Count >= 2 && Cross(hull[hull.Count-1] - hull[hull.Count-2], p - hull[hull.Count-1]) <= areaEpsilon) hull.RemoveAt(hull.Count-1);
                hull.Add(p);
            }
            int lowerCount = hull.Count;
            for (int i = unique.Count - 2; i >= 0; i--) {
                var p = unique[i];
                while (hull.Count > lowerCount && Cross(hull[hull.Count-1] - hull[hull.Count-2], p - hull[hull.Count-1]) <= areaEpsilon) hull.RemoveAt(hull.Count-1);
                hull.Add(p);
            }
            if (hull.Count > 1) hull.RemoveAt(hull.Count-1);
            return hull;
        }

        static void Reduce(List<Vector2> polygon, int corners)
        {
            // Remove a supporting edge by intersecting its two neighbors. This
            // circumscribed profile contains the original outline, unlike simply
            // dropping a vertex, which can remove the foot or another contact point.
            while (polygon.Count > corners)
            {
                int remove = -1; float area = float.MaxValue; var replacement = Vector2.zero;
                for (int i = 0; i < polygon.Count; i++) {
                    var previous = polygon[(i+polygon.Count-1) % polygon.Count];
                    var a = polygon[i]; var b = polygon[(i+1) % polygon.Count];
                    var next = polygon[(i+2) % polygon.Count];
                    var r = a - previous; var s = next - b;
                    float cross = Cross(r, s);
                    if (Mathf.Abs(cross) <= r.magnitude * s.magnitude * .000001f) continue;
                    float t = Cross(b - previous, s) / cross;
                    float u = Cross(b - previous, r) / cross;
                    if (t < 1 || u > 0) continue;
                    var point = previous + r * t;
                    float value = Mathf.Abs(Cross(a - point, b - point));
                    if (Finite(value) && value < area) { area = value; remove = i; replacement = point; }
                }
                if (remove < 0) break;
                polygon[remove] = replacement;
                polygon.RemoveAt((remove + 1) % polygon.Count);
            }
        }

        internal static RemeshNative.IndexedMesh Extrude(List<Vector2> polygon, float low, float high)
        {
            int n = polygon.Count;
            var positions = new Vector3[n*2]; var triangles = new List<int>();
            for (int i = 0; i < n; i++) {
                positions[i] = new Vector3(polygon[i].x, polygon[i].y, low);
                positions[i+n] = new Vector3(polygon[i].x, polygon[i].y, high);
                int j = (i+1) % n;
                triangles.AddRange(new[] { i,j,j+n, i,j+n,i+n });
            }
            for (int i = 1; i < n-1; i++) triangles.AddRange(new[] { 0,i+1,i, n,n+i,n+i+1 });
            return new RemeshNative.IndexedMesh { positions = positions, indices = triangles.ToArray() };
        }

        static float BoxFaceError(RemeshNative.IndexedMesh box, RemeshNative.IndexedMesh source)
        {
            var bvh = new TriangleBvh(source.positions, source.indices);
            float distance = 0;
            foreach (var face in new[] { new[] {0,1,3}, new[] {4,5,7}, new[] {0,1,4}, new[] {3,2,7}, new[] {0,3,4}, new[] {1,2,5} })
                for (int y = 0; y <= 8; y++) for (int x = 0; x <= 8; x++) {
                    var a = box.positions[face[0]];
                    var point = a + (box.positions[face[1]] - a) * (x / 8f) + (box.positions[face[2]] - a) * (y / 8f);
                    distance = Mathf.Max(distance, bvh.FindNearest(point).distSq);
                }
            return Mathf.Sqrt(distance);
        }

        static CollisionMeshBuilder.ConvexDecompResult Result(string name, IEnumerable<RemeshNative.IndexedMesh> proxies,
            IEnumerable<CollisionMeshBuilder.ShapeReport> reports)
        {
            var result = new CollisionMeshBuilder.ConvexDecompResult { ok = true, hulls = new List<Mesh>(),
                shapeReports = new List<CollisionMeshBuilder.ShapeReport>(reports) };
            try {
                foreach (var proxy in proxies) {
                    var mesh = new Mesh { name = $"{name}_hull{result.hulls.Count}" };
                    result.hulls.Add(mesh);
                    mesh.SetVertices(proxy.positions); mesh.SetTriangles(proxy.indices, 0); mesh.RecalculateBounds();
                }
                return result;
            }
            catch
            {
                foreach (var mesh in result.hulls)
                    UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
        }
    }
}
