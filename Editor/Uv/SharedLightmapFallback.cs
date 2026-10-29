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
            /// <summary>Faces with no LOD0 face facing their way.</summary>
            public int withoutSurface;
            /// <summary>Faces whose LOD0 surface still gave them no UV2 area.</summary>
            public int collapsed;
            /// <summary>The farthest LOD0 surface a placed face took its UV2 from, in model units.</summary>
            public float farthest;

            public override string ToString()
            {
                string text = $"{placed} of {faces} face(s) of {shells} shell(s) collapsed to a point or a line take the UV2 of the LOD0 surface under them";
                if (placed > 0) text += $" (farthest {farthest:G3})";
                if (withoutSurface > 0) text += $", {withoutSurface} without a LOD0 face facing their way";
                if (collapsed > 0) text += $", {collapsed} still collapsed";
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
            int placed = 0;
            foreach (int f in faces)
            {
                Vector3 a = positions[triangles[f * 3]], b = positions[triangles[f * 3 + 1]], c = positions[triangles[f * 3 + 2]];
                var normal = Normal(a, b, c);
                // A face within 60° of the face's own orientation first, so a nearer
                // perpendicular side does not lend it its light; then any facing its way.
                float facing = Facing;
                var hit = surface.all.FindNearestNormalFiltered((a + b + c) / 3f, normal, surface.normals, facing);
                if (hit.triangleIndex < 0)
                    hit = surface.all.FindNearestNormalFiltered((a + b + c) / 3f, normal, surface.normals, facing = 0f);
                if (hit.triangleIndex < 0) { ++report.withoutSurface; continue; }
                int under = surface.faces[hit.triangleIndex];
                var chart = surface.ChartFor(surface.shellOf[under]);
                var placedUv = new Vector2[3];
                for (int k = 0; k < 3; ++k)
                {
                    var corner = positions[triangles[f * 3 + k]];
                    var nearest = chart.bvh.FindNearestNormalFiltered(corner, normal, chart.normals, facing);
                    if (nearest.triangleIndex < 0) nearest = chart.bvh.FindNearest(corner);
                    placedUv[k] = surface.Uv(chart.faces[nearest.triangleIndex], nearest.barycentric);
                }
                // A face reaching past its chart presses its corners onto the chart's
                // border; the plane of the face under it keeps its shape instead.
                if (!Placed(placedUv))
                {
                    var extended = new Vector2[3];
                    bool planar = true;
                    for (int k = 0; k < 3 && planar; ++k) planar = surface.Extend(under, positions[triangles[f * 3 + k]], out extended[k]);
                    if (!planar || !Placed(extended)) { ++report.collapsed; continue; }
                    placedUv = extended;
                }
                for (int k = 0; k < 3; ++k) { cornerUv[f * 3 + k] = placedUv[k]; cornerSources[f * 3 + k] = surface.shellOf[under]; }
                report.farthest = Mathf.Max(report.farthest, Mathf.Sqrt(hit.distSq));
                ++placed;
            }
            report.placed = placed;
            if (placed == 0) return default;
            var sources = new List<int>();
            var identity = Enumerable.Range(0, triangles.Length)
                .Select(i => i % 3 == 0 ? Vector3.right : i % 3 == 1 ? Vector3.up : Vector3.forward).ToArray();
            var mesh = ReverseUvRefinedMesh.Copy(target, Enumerable.Range(0, triangles.Length / 3).ToArray(), identity, cornerUv,
                (vertex, corner) => sources.Add(cornerSources[corner]));
            return (mesh, sources.ToArray());
        }

        /// <summary>A face layout with finite corners and UV area.</summary>
        static bool Placed(Vector2[] uv)
            => uv.All(p => Finite(p.x) && Finite(p.y)) && ForwardUvReference.Area(uv[0], uv[1], uv[2]) != 0;

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>
        /// The UV of a point projected onto the plane of triangle abc, extending the triangle's
        /// affine map; false for a triangle too thin to fix a plane in double precision.
        /// </summary>
        internal static bool Extend(Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc, Vector3 point, out Vector2 uv)
        {
            double abx = (double)b.x - a.x, aby = (double)b.y - a.y, abz = (double)b.z - a.z;
            double acx = (double)c.x - a.x, acy = (double)c.y - a.y, acz = (double)c.z - a.z;
            double apx = (double)point.x - a.x, apy = (double)point.y - a.y, apz = (double)point.z - a.z;
            double d00 = abx * abx + aby * aby + abz * abz, d01 = abx * acx + aby * acy + abz * acz;
            double d11 = acx * acx + acy * acy + acz * acz;
            double d20 = apx * abx + apy * aby + apz * abz, d21 = apx * acx + apy * acy + apz * acz;
            double denominator = d00 * d11 - d01 * d01;
            uv = default;
            if (!(denominator > 0) || !Finite(denominator)) return false;
            double v = (d11 * d20 - d01 * d21) / denominator, w = (d00 * d21 - d01 * d20) / denominator, u = 1 - v - w;
            double x = ua.x * u + ub.x * v + uc.x * w, y = ua.y * u + ub.y * v + uc.y * w;
            if (!Finite(x) || !Finite(y)) return false;
            uv = new Vector2((float)x, (float)y);
            return Finite(uv.x) && Finite(uv.y);
        }

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
        /// <see cref="GroupedShellTransfer.CollapsedUvExtent"/> wide or high.
        /// </summary>
        internal static List<int> CollapsedShellFaces(int[] triangles, Vector3[] positions, Vector2[] uv2, Report report)
        {
            var faces = new List<int>();
            foreach (var shell in UvShellExtractor.Extract(uv2, triangles))
            {
                var size = shell.boundsMax - shell.boundsMin;
                if (size.x >= GroupedShellTransfer.CollapsedUvExtent && size.y >= GroupedShellTransfer.CollapsedUvExtent) continue;
                var withArea = shell.faceIndices.Where(f => MeshGeometry.HasArea(positions[triangles[f * 3]],
                    positions[triangles[f * 3 + 1]], positions[triangles[f * 3 + 2]])).ToList();
                if (withArea.Count == 0) continue;
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
            readonly Dictionary<int, Chart> charts = new Dictionary<int, Chart>();
            readonly Dictionary<int, List<int>> chartFaces = new Dictionary<int, List<int>>();

            internal sealed class Chart
            {
                internal int[] faces;
                internal Vector3[] normals;
                internal TriangleBvh bvh;
            }

            internal Surface(Vector3[] positions, int[] triangles, Vector2[] uv2, Vector2[] uv0)
            {
                this.positions = positions; this.triangles = triangles; this.uv2 = uv2;
                // The transfer records a vertex's source as the source's UV0 shell; the
                // same vertex-connected shell is one chart of the source's UV2.
                shellOf = new int[triangles.Length / 3];
                foreach (var shell in UvShellExtractor.Extract(uv0.Length == positions.Length ? uv0 : uv2, triangles))
                    foreach (int f in shell.faceIndices) shellOf[f] = shell.shellId;
                var valid = new List<int>();
                for (int f = 0; f < shellOf.Length; ++f)
                {
                    int t = f * 3;
                    if (!MeshGeometry.HasArea(positions[triangles[t]], positions[triangles[t + 1]], positions[triangles[t + 2]])
                        || ForwardUvReference.Area(uv2[triangles[t]], uv2[triangles[t + 1]], uv2[triangles[t + 2]]) == 0) continue;
                    valid.Add(f);
                    if (!chartFaces.TryGetValue(shellOf[f], out var list)) chartFaces.Add(shellOf[f], list = new List<int>());
                    list.Add(f);
                }
                faces = valid.ToArray();
                normals = faces.Select(Normal).ToArray();
                all = new TriangleBvh(positions, Corners(faces));
            }

            internal Chart ChartFor(int shell)
            {
                if (charts.TryGetValue(shell, out var chart)) return chart;
                var list = chartFaces[shell].ToArray();
                chart = new Chart { faces = list, normals = list.Select(Normal).ToArray(), bvh = new TriangleBvh(positions, Corners(list)) };
                charts.Add(shell, chart);
                return chart;
            }

            internal Vector2 Uv(int face, Vector3 barycentric)
                => uv2[triangles[face * 3]] * barycentric.x + uv2[triangles[face * 3 + 1]] * barycentric.y
                    + uv2[triangles[face * 3 + 2]] * barycentric.z;

            /// <summary>The UV2 of a point projected onto the plane of a face, extending the face's affine map.</summary>
            internal bool Extend(int face, Vector3 point, out Vector2 uv)
                => SharedLightmapFallback.Extend(positions[triangles[face * 3]], positions[triangles[face * 3 + 1]],
                    positions[triangles[face * 3 + 2]], uv2[triangles[face * 3]], uv2[triangles[face * 3 + 1]],
                    uv2[triangles[face * 3 + 2]], point, out uv);

            Vector3 Normal(int face)
                => SharedLightmapFallback.Normal(positions[triangles[face * 3]], positions[triangles[face * 3 + 1]],
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
