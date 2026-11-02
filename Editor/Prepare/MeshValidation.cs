// MeshValidation.cs — the checks of one mesh against ModelRequirements: finite values,
// degenerate faces, the vertex limit, unused vertices, non-manifold edges, flipped
// normals, the required channels and a lightmap UV inside [0,1]. The counting helpers read plain arrays; Check
// reads a readable mesh (the caller supplies a readable copy and destroys it).

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    internal static class MeshValidation
    {
        /// <summary>A face is flat when its height is at most this share of its longest edge.</summary>
        internal const double FlatRatio = 1e-6;

        /// <summary>How far outside [0,1] a lightmap UV may lie: rounding (the atlas audit's bound).</summary>
        internal const float LightmapTolerance = 1e-4f;

        /// <summary>
        /// Faces with no area: a repeated or missing vertex, or a height at most
        /// <see cref="FlatRatio"/> of the longest edge; <c>zeroEdge</c> counts those among them
        /// with an edge that short (two corners at one point).
        /// </summary>
        internal static (int faces, int zeroEdge) DegenerateFaces(Vector3[] positions, int[] triangles)
        {
            int faces = 0, zeroEdge = 0;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                if (i0 == i1 || i0 == i2 || i1 == i2 || !InRange(i0, positions) || !InRange(i1, positions) || !InRange(i2, positions))
                { ++faces; ++zeroEdge; continue; }
                Vector3 a = positions[i0], b = positions[i1], c = positions[i2];
                double ab = SquaredLength(a, b), bc = SquaredLength(b, c), ca = SquaredLength(c, a);
                double longest = System.Math.Max(ab, System.Math.Max(bc, ca));
                double shortest = System.Math.Min(ab, System.Math.Min(bc, ca));
                // 2 × area / longest² is the face's height over its longest edge.
                if (!(longest > 0) || DoubleArea(a, b, c) <= FlatRatio * longest)
                {
                    ++faces;
                    if (!(shortest > FlatRatio * FlatRatio * longest)) ++zeroEdge;
                }
            }
            return (faces, zeroEdge);
        }

        /// <summary>Edges, by vertex position, that more than two faces share. Faces with a repeated position are left out.</summary>
        internal static int NonManifoldEdges(Vector3[] positions, int[] triangles)
        {
            var slots = MeshGeometry.WeldPositions(positions, out _);
            var faces = new Dictionary<long, int>();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                if (!InRange(i0, positions) || !InRange(i1, positions) || !InRange(i2, positions)) continue;
                int a = slots[i0], b = slots[i1], c = slots[i2];
                if (a == b || b == c || a == c) continue;
                Count(faces, a, b); Count(faces, b, c); Count(faces, c, a);
            }
            return faces.Values.Count(n => n > 2);
        }

        /// <summary>Faces with area whose three vertex normals all point against the winding's normal.</summary>
        internal static int FlippedFaces(Vector3[] positions, Vector3[] normals, int[] triangles)
        {
            if (normals == null || normals.Length != positions.Length) return 0;
            int flipped = 0;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                if (!InRange(i0, positions) || !InRange(i1, positions) || !InRange(i2, positions)) continue;
                Vector3 face = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
                if (!(face.sqrMagnitude > 0f)) continue;
                if (Vector3.Dot(face, normals[i0]) < 0f && Vector3.Dot(face, normals[i1]) < 0f && Vector3.Dot(face, normals[i2]) < 0f)
                    ++flipped;
            }
            return flipped;
        }

        /// <summary>Vertices no face uses.</summary>
        internal static int UnusedVertices(int[] triangles, int vertexCount)
            => vertexCount - MeshGeometry.FaceVertexCount(triangles, vertexCount);

        /// <summary>UVs farther than <paramref name="tolerance"/> outside [0,1], and the bounds of all of them.</summary>
        internal static (int outside, Vector2 min, Vector2 max) OutsideUnit(IReadOnlyList<Vector2> uv, float tolerance)
        {
            int outside = 0;
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var value in uv)
            {
                min = Vector2.Min(min, value); max = Vector2.Max(max, value);
                if (XatlasRepack.UnitOvershoot(value) > tolerance) ++outside;
            }
            return (outside, min, max);
        }

        /// <summary>
        /// Checks <paramref name="mesh"/> (readable) into <paramref name="report"/>. A collision
        /// mesh is held to finite values and degenerate faces only: it needs no channels and
        /// PhysX takes any edge sharing and winding.
        /// </summary>
        internal static void Check(Mesh mesh, string subject, bool collision, ModelRequirements requirements, PrepareReport report)
        {
            var positions = mesh.vertices;
            var triangles = Triangles(mesh);
            var normals = mesh.HasVertexAttribute(VertexAttribute.Normal) ? mesh.normals : null;

            if (requirements.finiteValues != RequirementLevel.Off)
            {
                var (vertices, streams) = NonFiniteVertices(mesh, positions, normals);
                if (vertices > 0)
                    report.Add(requirements.finiteValues, subject, "Finite values",
                        $"{Plural(vertices, "vertex", "vertices")} with NaN or infinite values ({string.Join(", ", streams)})");
            }

            if (requirements.degenerateFaces != RequirementLevel.Off)
            {
                var (faces, zeroEdge) = DegenerateFaces(positions, triangles);
                if (faces > 0)
                    report.Add(requirements.degenerateFaces, subject, "Degenerate faces",
                        $"{Plural(faces, "face", "faces")} without area ({zeroEdge} with a zero-length edge); they break lightmap UVs, tangents and collision");
            }

            if (collision) return;

            if (requirements.maxVertices > 0 && positions.Length > requirements.maxVertices)
                report.Add(requirements.vertexLimit, subject, "Vertex limit",
                    $"{positions.Length} vertices, over the limit of {requirements.maxVertices}");

            if (requirements.unusedVertices != RequirementLevel.Off)
            {
                int unused = UnusedVertices(triangles, positions.Length);
                if (unused > 0)
                    report.Add(requirements.unusedVertices, subject, "Unused vertices", $"{Plural(unused, "vertex", "vertices")} in no face");
            }

            if (requirements.nonManifoldEdges != RequirementLevel.Off)
            {
                int edges = NonManifoldEdges(positions, triangles);
                if (edges > 0)
                    report.Add(requirements.nonManifoldEdges, subject, "Non-manifold edges", $"{Plural(edges, "edge", "edges")} shared by more than two faces");
            }

            if (requirements.flippedNormals != RequirementLevel.Off && normals != null)
            {
                int flipped = FlippedFaces(positions, normals, triangles);
                if (flipped > 0)
                    report.Add(requirements.flippedNormals, subject, "Flipped normals",
                        $"{Plural(flipped, "face", "faces")} whose vertex normals point against the face winding");
            }

            CheckChannels(mesh, subject, requirements, report);
            CheckLightmap(mesh, subject, requirements, report);
        }

        static void CheckChannels(Mesh mesh, string subject, ModelRequirements requirements, PrepareReport report)
        {
            if (requirements.channels == RequirementLevel.Off) return;
            var missing = new List<string>();
            if (requirements.requireUv0 && !mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) missing.Add("UV0");
            if (requirements.requireLightmapUv && !mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) missing.Add("UV1 (lightmap)");
            if (requirements.requireNormals && !mesh.HasVertexAttribute(VertexAttribute.Normal)) missing.Add("normals");
            if (requirements.requireTangents && !mesh.HasVertexAttribute(VertexAttribute.Tangent)) missing.Add("tangents");
            if (missing.Count > 0) report.Add(requirements.channels, subject, "Channels", $"missing {string.Join(", ", missing)}");
        }

        static void CheckLightmap(Mesh mesh, string subject, ModelRequirements requirements, PrepareReport report)
        {
            if (requirements.lightmapInsideUnit == RequirementLevel.Off || !mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) return;
            var uv = new List<Vector2>();
            mesh.GetUVs(1, uv);
            var (outside, min, max) = OutsideUnit(uv, LightmapTolerance);
            if (outside > 0)
                report.Add(requirements.lightmapInsideUnit, subject, "Lightmap UV",
                    $"{Plural(outside, "UV1 vertex lies", "UV1 vertices lie")} outside [0,1] (U {min.x:F3}..{max.x:F3}, V {min.y:F3}..{max.y:F3}); they sample other charts' texels");
        }

        /// <summary>Vertices with a non-finite value in any stream, and the streams that have one.</summary>
        static (int vertices, List<string> streams) NonFiniteVertices(Mesh mesh, Vector3[] positions, Vector3[] normals)
        {
            var bad = new bool[positions.Length];
            var streams = new List<string>();
            void Mark(string stream, int count, System.Func<int, bool> finite)
            {
                bool any = false;
                for (int i = 0; i < count && i < bad.Length; ++i)
                    if (!finite(i)) { bad[i] = true; any = true; }
                if (any) streams.Add(stream);
            }
            Mark("positions", positions.Length, i => Finite(positions[i]));
            if (normals != null) Mark("normals", normals.Length, i => Finite(normals[i]));
            if (mesh.HasVertexAttribute(VertexAttribute.Tangent))
            {
                var tangents = mesh.tangents;
                Mark("tangents", tangents.Length, i => Finite(tangents[i]));
            }
            var uv = new List<Vector4>();
            for (int channel = 0; channel < 8; ++channel)
            {
                if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + channel)) continue;
                mesh.GetUVs(channel, uv);
                Mark($"UV{channel}", uv.Count, i => Finite(uv[i]));
            }
            return (bad.Count(b => b), streams);
        }

        /// <summary>
        /// The triangle indices of every triangle and quad submesh, in submesh order: a quad
        /// submesh (Keep Quads) is read triangulated, two triangles per quad, as
        /// <see cref="SubmeshTopology"/> reads it. Line and point submeshes have no faces.
        /// </summary>
        static int[] Triangles(Mesh mesh)
        {
            var all = new List<int>();
            for (int sub = 0; sub < mesh.subMeshCount; ++sub)
            {
                var topology = mesh.GetTopology(sub);
                if (topology == MeshTopology.Triangles || topology == MeshTopology.Quads) all.AddRange(mesh.GetTriangles(sub));
            }
            return all.ToArray();
        }

        static void Count(Dictionary<long, int> faces, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            faces.TryGetValue(key, out int n);
            faces[key] = n + 1;
        }

        static bool InRange(int index, Vector3[] positions) => index >= 0 && index < positions.Length;

        static double SquaredLength(Vector3 a, Vector3 b)
        {
            double x = (double)b.x - a.x, y = (double)b.y - a.y, z = (double)b.z - a.z;
            return x * x + y * y + z * z;
        }

        static double DoubleArea(Vector3 a, Vector3 b, Vector3 c)
        {
            double abx = (double)b.x - a.x, aby = (double)b.y - a.y, abz = (double)b.z - a.z;
            double acx = (double)c.x - a.x, acy = (double)c.y - a.y, acz = (double)c.z - a.z;
            double x = aby * acz - abz * acy, y = abz * acx - abx * acz, z = abx * acy - aby * acx;
            return System.Math.Sqrt(x * x + y * y + z * z);
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        static bool Finite(Vector4 v) => Finite(v.x) && Finite(v.y) && Finite(v.z) && Finite(v.w);

        internal static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
    }
}
