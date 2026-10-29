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

        static bool Finite(Vector2 p) => !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsInfinity(p.x) && !float.IsInfinity(p.y);

        /// <summary>The squared UV2 distance within which a point counts as on a chart: far below a texel.</summary>
        const float OnChart = 1e-12f;

        /// <summary>The cosine within which a LOD0 face counts as oriented like the target face.</summary>
        const float Facing = .5f;

        /// <summary>The unit normal of a face; zero when its float cross product underflows.</summary>
        static Vector3 Normal(Vector3 a, Vector3 b, Vector3 c)
        {
            var cross = Vector3.Cross(b - a, c - a);
            float length = cross.magnitude;
            return length > 0 ? cross / length : Vector3.zero;
        }

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

        /// <summary>The LOD0 faces with 3D and UV2 area, searchable whole and per chart.</summary>
        sealed class Surface
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
                    footprint = new TriangleBvh2D(uvA, uvB, uvC, list) };
                charts.Add(shell, chart);
                return chart;
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
            /// where another chart is packed; such a face takes the one point under its centre, still
            /// on its own surface, never a place in another chart.
            /// </summary>
            internal bool Place(Vector3[] corners, Report report, out Vector2[] uv, out int shell)
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
                return true;
            }

            /// <summary>
            /// Whether a UV2 triangle lies on its chart: its lattice points at quarters of each
            /// edge (centre and edge midpoints among them) are on the chart's faces, so a
            /// footprint across a concave chart's gap is refused. The corners lie on it by construction.
            /// </summary>
            static bool Covers(Chart chart, Vector2[] uv)
            {
                const int steps = 4;
                for (int i = 0; i <= steps; ++i)
                    for (int j = 0; i + j <= steps; ++j)
                    {
                        int k = steps - i - j;
                        if (i == steps || j == steps || k == steps) continue;
                        var point = (uv[0] * i + uv[1] * j + uv[2] * k) / steps;
                        var hit = chart.footprint.FindNearest(point);
                        if (hit.faceIndex < 0 || hit.distSq > OnChart) return false;
                    }
                return true;
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
