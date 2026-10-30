using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// For LODs that share the LOD0 lightmap: a face reads the light of the LOD0
    /// surface its UV2 lies on. A target shell whose UV2 collapsed to a point or a
    /// line (a rejected shell stays at (0,0)) reads one texel or one row of them,
    /// whatever lies there. Each of its faces takes instead the UV2 of the LOD0
    /// surface under it, within one LOD0 chart, and may overlap other faces lying
    /// on that surface: under a shared lightmap they read the same light.
    /// </summary>
    internal static class SharedLightmapFallback
    {
        /// <summary>What the fallback did, for the log.</summary>
        internal sealed class Report
        {
            /// <summary>Target shells with 3D area whose UV2 collapsed to a point or a line.</summary>
            public int shells;
            /// <summary>Their faces with 3D area.</summary>
            public int faces;
            /// <summary>Faces given the UV2 of the LOD0 surface under them.</summary>
            public int placed;
            /// <summary>Placed faces whose corners met at one point of their chart: they take the one point under their centre.</summary>
            public int atPoint;
            /// <summary>Faces with no LOD0 face facing their way.</summary>
            public int withoutSurface;
            /// <summary>Placed faces that read a LOD0 surface facing away, none facing their way (Reverse UV only).</summary>
            public int facingAway;
            /// <summary>The farthest LOD0 surface a placed face took its UV2 from, in model units.</summary>
            public float farthest;

            public override string ToString()
            {
                string text = $"{placed} of {faces} face(s) of {shells} shell(s) collapsed to a point or a line take the UV2 of the LOD0 surface under them";
                if (placed > 0) text += $" (farthest {farthest:G3}" + (atPoint > 0 ? $"; {atPoint} at the one point under their centre)" : ")");
                if (withoutSurface > 0) text += $", {withoutSurface} without a LOD0 face facing their way";
                return text;
            }
        }

        /// <summary>
        /// The target with the faces of its collapsed shells placed on the LOD0 surface,
        /// and each new vertex's source shell (the source's UV0 shell, as the transfer
        /// records it); default when no face was placed.
        /// </summary>
        internal static (Mesh mesh, int[] sources) Apply(Mesh source, Mesh target, Vector2[] uv2, int[] vertexSources, Report report)
        {
            var positions = target.vertices; var triangles = target.triangles;
            if (uv2 == null || uv2.Length != positions.Length || vertexSources == null || vertexSources.Length != positions.Length) return default;
            var faces = CollapsedShellFaces(triangles, positions, uv2, report);
            if (faces.Count == 0) return default;

            var sourcePositions = source.vertices; var sourceTriangles = source.triangles; var sourceUv2 = source.uv2;
            if (sourceUv2.Length != sourcePositions.Length) return default;
            var surface = new Surface(sourcePositions, sourceTriangles, sourceUv2, source.uv);
            if (surface.faces.Length == 0) { report.withoutSurface = faces.Count; return default; }

            var cornerUv = triangles.Select(v => uv2[v]).ToArray();
            var cornerSources = triangles.Select(v => vertexSources[v]).ToArray();
            foreach (int f in faces)
            {
                var corners = new[] {positions[triangles[f * 3]], positions[triangles[f * 3 + 1]], positions[triangles[f * 3 + 2]]};
                if (!surface.Place(corners, report, out var placedUv, out int shell)) continue;
                for (int k = 0; k < 3; ++k) { cornerUv[f * 3 + k] = placedUv[k]; cornerSources[f * 3 + k] = shell; }
            }
            if (report.placed == 0) return default;
            var sources = new List<int>();
            var unit = new[] {Vector3.right, Vector3.up, Vector3.forward};
            var identity = Enumerable.Range(0, triangles.Length).Select(i => unit[i % 3]).ToArray();
            var mesh = ReverseUvRefinedMesh.Copy(target, Enumerable.Range(0, triangles.Length / 3).ToArray(), identity, cornerUv,
                (vertex, corner) => sources.Add(cornerSources[corner]));
            return (mesh, sources.ToArray());
        }

        /// <summary>The squared UV2 distance within which a point counts as on a chart: far below a texel.</summary>
        const float OnChart = 1e-12f;

        /// <summary>The cosine within which a LOD0 face counts as oriented like the target face.</summary>
        const float Facing = .5f;

        /// <summary>
        /// The faces with 3D area of the target shells (connected by shared vertices,
        /// as the transfer extracts them) whose UV2 is less than
        /// <see cref="GroupedShellTransfer.CollapsedUvExtent"/> wide or high, or whose faces
        /// with 3D area cover less UV2 than that extent squared: a line at any angle.
        /// </summary>
        internal static List<int> CollapsedShellFaces(int[] triangles, Vector3[] positions, Vector2[] uv2, Report report)
        {
            var faces = new List<int>();
            foreach (var shell in UvShellExtractor.Extract(uv2, triangles))
            {
                var withArea = shell.faceIndices.Where(f => MeshGeometry.HasArea(positions[triangles[f * 3]],
                    positions[triangles[f * 3 + 1]], positions[triangles[f * 3 + 2]])).ToList();
                if (withArea.Count == 0) continue;
                var size = shell.boundsMax - shell.boundsMin;
                const double extent = GroupedShellTransfer.CollapsedUvExtent;
                bool thin = size.x < extent || size.y < extent;
                if (!thin && withArea.Sum(f => System.Math.Abs(ForwardUvReference.Area(uv2[triangles[f * 3]],
                    uv2[triangles[f * 3 + 1]], uv2[triangles[f * 3 + 2]]))) * .5 >= extent * extent) continue;
                ++report.shells;
                report.faces += withArea.Count;
                faces.AddRange(withArea);
            }
            faces.Sort();
            return faces;
        }

        /// <summary>
        /// How many faces with 3D area of <paramref name="mesh"/> read one texel or one row in
        /// <paramref name="uv2"/>: those <see cref="CollapsedShellFaces"/> finds, and any other
        /// with no UV2 area (a face placed at one point can share a vertex with a healthy one).
        /// The count the log gives before and after the fallback: a shell thinner than the
        /// extent still has UV2 area, so a measure of faces without it alone misses them.
        /// </summary>
        internal static int CollapsedFaceCount(Mesh mesh, Vector2[] uv2)
        {
            if (uv2 == null || uv2.Length != mesh.vertexCount) return 0;
            var triangles = mesh.triangles; var positions = mesh.vertices;
            var faces = new HashSet<int>(CollapsedShellFaces(triangles, positions, uv2, new Report()));
            for (int f = 0; f < triangles.Length / 3; ++f)
            {
                int a = triangles[f * 3], b = triangles[f * 3 + 1], c = triangles[f * 3 + 2];
                if (ForwardUvReference.Area(uv2[a], uv2[b], uv2[c]) == 0 && MeshGeometry.HasArea(positions[a], positions[b], positions[c]))
                    faces.Add(f);
            }
            return faces.Count;
        }

        /// <summary>The LOD0 faces with 3D and UV2 area, searchable whole and per chart.</summary>
        internal sealed class Surface
        {
            internal readonly int[] faces;
            internal readonly Vector3[] normals;
            internal readonly TriangleBvh all;
            internal readonly int[] shellOf;
            readonly Vector3[] positions;
            readonly int[] triangles;
            readonly Vector2[] uv2;
            readonly Vector2[] uvA, uvB, uvC;
            readonly Dictionary<int, Chart> charts = new Dictionary<int, Chart>();
            readonly Dictionary<int, List<int>> chartFaces = new Dictionary<int, List<int>>();

            internal sealed class Chart
            {
                internal int[] faces;
                internal Vector3[] normals;
                internal TriangleBvh bvh;
                internal TriangleBvh2D footprint;
                /// <summary>Endpoint pairs of the UV2 edges bounding the chart's faces.</summary>
                internal Vector2[] border;
            }

            internal Surface(Vector3[] positions, int[] triangles, Vector2[] uv2, Vector2[] uv0)
            {
                this.positions = positions; this.triangles = triangles; this.uv2 = uv2;
                // The transfer records a vertex's source as the source's UV0 shell; the
                // same vertex-connected shell is one chart of the source's UV2.
                shellOf = new int[triangles.Length / 3];
                uvA = new Vector2[shellOf.Length]; uvB = new Vector2[shellOf.Length]; uvC = new Vector2[shellOf.Length];
                for (int f = 0; f < shellOf.Length; ++f)
                { uvA[f] = uv2[triangles[f * 3]]; uvB[f] = uv2[triangles[f * 3 + 1]]; uvC[f] = uv2[triangles[f * 3 + 2]]; }
                foreach (var shell in UvShellExtractor.Extract(uv0.Length == positions.Length ? uv0 : uv2, triangles))
                    foreach (int f in shell.faceIndices) shellOf[f] = shell.shellId;
                var valid = new List<int>();
                for (int f = 0; f < shellOf.Length; ++f)
                {
                    int t = f * 3;
                    if (!MeshGeometry.HasArea(positions[triangles[t]], positions[triangles[t + 1]], positions[triangles[t + 2]])
                        || ForwardUvReference.Area(uv2[triangles[t]], uv2[triangles[t + 1]], uv2[triangles[t + 2]]) == 0) continue;
                    valid.Add(f);
                    if (!chartFaces.TryGetValue(shellOf[f], out var list))
                    {
                        list = new List<int>();
                        chartFaces.Add(shellOf[f], list);
                    }
                    list.Add(f);
                }
                faces = valid.ToArray();
                normals = faces.Select(FaceNormal).ToArray();
                all = new TriangleBvh(positions, Corners(faces));
            }

            Chart ChartFor(int shell)
            {
                if (charts.TryGetValue(shell, out var chart)) return chart;
                var list = chartFaces[shell].ToArray();
                chart = new Chart { faces = list, normals = list.Select(FaceNormal).ToArray(), bvh = new TriangleBvh(positions, Corners(list)),
                    footprint = new TriangleBvh2D(uvA, uvB, uvC, list), border = Border(list) };
                charts.Add(shell, chart);
                return chart;
            }

            /// <summary>
            /// The UV2 edges of a chart's faces not lying between exactly two of them, one on
            /// either side: its outline and its holes. A fold or a vertex split inside the
            /// chart adds a border edge, which only refuses more.
            /// </summary>
            Vector2[] Border(int[] list)
            {
                var sides = new Dictionary<(Vector2, Vector2), (int count, int side)>();
                foreach (int f in list)
                {
                    int t = f * 3;
                    int sign = System.Math.Sign(ForwardUvReference.Area(uv2[triangles[t]], uv2[triangles[t + 1]], uv2[triangles[t + 2]]));
                    for (int k = 0; k < 3; ++k)
                    {
                        Vector2 p = uv2[triangles[t + k]], q = uv2[triangles[t + (k + 1) % 3]];
                        // A face wound counter-clockwise in UV2 lies left of its edge p→q.
                        int byX = p.x.CompareTo(q.x);
                        bool forward = byX < 0 || (byX == 0 && p.y < q.y);
                        var key = forward ? (p, q) : (q, p);
                        sides.TryGetValue(key, out var edge);
                        sides[key] = (edge.count + 1, edge.side + (forward ? sign : -sign));
                    }
                }
                return sides.Where(e => e.Value.count != 2 || e.Value.side != 0)
                    .SelectMany(e => new[] {e.Key.Item1, e.Key.Item2}).ToArray();
            }

            Vector2 Uv(int face, Vector3 barycentric)
                => uv2[triangles[face * 3]] * barycentric.x + uv2[triangles[face * 3 + 1]] * barycentric.y
                    + uv2[triangles[face * 3 + 2]] * barycentric.z;

            /// <summary>
            /// The UV2 of a target face from the LOD0 surface under it: the face nearest its centroid
            /// (within 60° of its orientation first, so a nearer perpendicular side does not lend it
            /// its light, then any facing its way) picks one chart, and each corner takes the UV2 of
            /// that chart's nearest point, so every corner reads light of that chart's surface. A face
            /// reaching far past its chart can press all its corners onto one point of the chart's
            /// border, and on a concave chart a face with every corner on it can still span a gap
            /// where another chart is packed, or a hole; such a face takes the one point under its
            /// centre, still on its own surface, never a place in another chart. With
            /// <paramref name="anyFacing"/>, a face with no LOD0 face facing its way reads the
            /// nearest one facing away (a sheet whose LODs are wound opposite ways).
            /// </summary>
            internal bool Place(Vector3[] corners, Report report, out Vector2[] uv, out int shell, bool anyFacing = false)
            {
                uv = null; shell = -1;
                var normal = Normal(corners[0], corners[1], corners[2]);
                var center = (corners[0] + corners[1] + corners[2]) / 3f;
                float facing = Facing;
                var hit = all.FindNearestNormalFiltered(center, normal, normals, facing);
                if (hit.triangleIndex < 0)
                {
                    facing = 0f;
                    hit = all.FindNearestNormalFiltered(center, normal, normals, facing);
                }
                bool away = hit.triangleIndex < 0 && anyFacing;
                if (away)
                {
                    facing = float.NegativeInfinity;
                    hit = all.FindNearestNormalFiltered(center, normal, normals, facing);
                }
                if (hit.triangleIndex < 0) { ++report.withoutSurface; return false; }
                int under = faces[hit.triangleIndex];
                var chart = ChartFor(shellOf[under]);
                var projected = new Vector2[3];
                for (int k = 0; k < 3; ++k)
                {
                    var nearest = chart.bvh.FindNearestNormalFiltered(corners[k], normal, chart.normals, facing);
                    if (nearest.triangleIndex < 0) nearest = chart.bvh.FindNearest(corners[k]);
                    projected[k] = Uv(chart.faces[nearest.triangleIndex], nearest.barycentric);
                }
                if (!projected.All(Finite) || ForwardUvReference.Area(projected[0], projected[1], projected[2]) == 0
                    || !Covers(chart, projected))
                {
                    var point = Uv(under, hit.barycentric);
                    if (!Finite(point)) { ++report.withoutSurface; return false; }
                    projected = new[] {point, point, point};
                    ++report.atPoint;
                }
                uv = projected; shell = shellOf[under];
                report.farthest = Mathf.Max(report.farthest, Mathf.Sqrt(hit.distSq));
                ++report.placed;
                if (away) ++report.facingAway;
                return true;
            }

            /// <summary>
            /// The share of a corner's reach from the face's centre by which <see cref="Covers"/>
            /// pulls it in: a corner pressed onto the chart's border sits on it, and a border
            /// reaching no further into the face leaves far less than a texel of it off the chart.
            /// </summary>
            const double Margin = 1e-4;

            /// <summary>
            /// Whether a UV2 triangle lies on its chart: its centre is on the chart's faces and
            /// no border edge of the chart meets it with its corners pulled in by
            /// <see cref="Margin"/>, so no gap of a concave chart and no hole, however narrow,
            /// lies under it. The corners lie on the chart by construction.
            /// </summary>
            static bool Covers(Chart chart, Vector2[] uv)
            {
                var hit = chart.footprint.FindNearest((uv[0] + uv[1] + uv[2]) / 3f);
                if (hit.faceIndex < 0 || hit.distSq > OnChart) return false;
                double cx = ((double)uv[0].x + uv[1].x + uv[2].x) / 3, cy = ((double)uv[0].y + uv[1].y + uv[2].y) / 3;
                var x = new double[3]; var y = new double[3];
                for (int k = 0; k < 3; ++k)
                {
                    x[k] = cx + (uv[k].x - cx) * (1 - Margin);
                    y[k] = cy + (uv[k].y - cy) * (1 - Margin);
                }
                int sign = System.Math.Sign((x[1] - x[0]) * (y[2] - y[0]) - (y[1] - y[0]) * (x[2] - x[0]));
                double minX = x.Min(), maxX = x.Max(), minY = y.Min(), maxY = y.Max();
                var border = chart.border;
                for (int s = 0; s < border.Length; s += 2)
                {
                    Vector2 p = border[s], q = border[s + 1];
                    if (System.Math.Max(p.x, q.x) < minX || System.Math.Min(p.x, q.x) > maxX
                        || System.Math.Max(p.y, q.y) < minY || System.Math.Min(p.y, q.y) > maxY) continue;
                    if (Meets(x, y, sign, p, q)) return false;
                }
                return true;
            }

            /// <summary>
            /// Whether segment pq meets the closed triangle (x, y) wound by sign: clipped to the
            /// inner side of each of its edges, pq keeps a part.
            /// </summary>
            static bool Meets(double[] x, double[] y, int sign, Vector2 p, Vector2 q)
            {
                double from = 0, to = 1, dx = (double)q.x - p.x, dy = (double)q.y - p.y;
                for (int k = 0; k < 3; ++k)
                {
                    int n = (k + 1) % 3;
                    double ex = x[n] - x[k], ey = y[n] - y[k];
                    double inside = sign * (ex * (p.y - y[k]) - ey * (p.x - x[k]));
                    double rate = sign * (ex * dy - ey * dx);
                    if (rate == 0)
                    {
                        if (inside < 0) return false;
                        continue;
                    }
                    double t = -inside / rate;
                    if (rate > 0) from = System.Math.Max(from, t);
                    else to = System.Math.Min(to, t);
                    if (from > to) return false;
                }
                return true;
            }

            static bool Finite(Vector2 p) => !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsInfinity(p.x) && !float.IsInfinity(p.y);

            /// <summary>The unit normal of a face; zero when its float cross product underflows.</summary>
            static Vector3 Normal(Vector3 a, Vector3 b, Vector3 c)
            {
                var cross = Vector3.Cross(b - a, c - a);
                float length = cross.magnitude;
                return length > 0 ? cross / length : Vector3.zero;
            }

            Vector3 FaceNormal(int face)
                => Normal(positions[triangles[face * 3]], positions[triangles[face * 3 + 1]],
                    positions[triangles[face * 3 + 2]]);

            int[] Corners(int[] list)
            {
                var corners = new int[list.Length * 3];
                for (int i = 0; i < list.Length; ++i)
                    for (int k = 0; k < 3; ++k) corners[i * 3 + k] = triangles[list[i] * 3 + k];
                return corners;
            }
        }
    }
}
