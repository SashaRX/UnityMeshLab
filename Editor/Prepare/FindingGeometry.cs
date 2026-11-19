// FindingGeometry.cs — the faces, edges and vertices a mesh finding is about, so that it can be
// drawn on the model: the faces whose normals are flipped, the faces without area or with too
// little, the short and the non-manifold edges, the vertices no face uses, the lightmap UVs
// outside [0,1] with the faces on them. The same tests MeshValidation counts with, as lists.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    /// <summary>What to draw of one finding, in the mesh's own space: triangles, edges and points as vertex indices.</summary>
    internal sealed class FindingShape
    {
        internal Vector3[] positions = System.Array.Empty<Vector3>();
        /// <summary>Three indices per face.</summary>
        internal readonly List<int> triangles = new List<int>();
        /// <summary>Two indices per edge.</summary>
        internal readonly List<int> edges = new List<int>();
        internal readonly List<int> points = new List<int>();

        internal bool IsEmpty => triangles.Count == 0 && edges.Count == 0 && points.Count == 0;

        /// <summary>The vertices drawn, each once.</summary>
        internal IEnumerable<int> Vertices() => triangles.Concat(edges).Concat(points).Distinct();
    }

    internal static class FindingGeometry
    {
        /// <summary>Whether a check's finding has geometry to draw.</summary>
        internal static bool Draws(string check)
        {
            switch (check)
            {
                case "Flipped normals": case "Degenerate faces": case "Tiny faces": case "Short edges":
                case "Unused vertices": case "Non-manifold edges": case "Lightmap UV":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The geometry of <paramref name="check"/>'s finding on <paramref name="mesh"/> (readable),
        /// by the thresholds of <paramref name="requirements"/>; null for a check with nothing to draw.
        /// </summary>
        internal static FindingShape Build(string check, Mesh mesh, ModelRequirements requirements)
        {
            if (mesh == null || !Draws(check)) return null;
            requirements = requirements ?? ModelRequirements.Default;
            var positions = mesh.vertices;
            var triangles = MeshValidation.Triangles(mesh);
            var shape = new FindingShape { positions = positions };
            switch (check)
            {
                case "Flipped normals":
                    AddFaces(shape, triangles, MeshValidation.FlippedFaceMask(positions, mesh.normals, triangles));
                    break;
                case "Degenerate faces":
                    // Without area a face shows as nothing: its edges and corners are drawn too.
                    AddFaces(shape, triangles, MeshValidation.DegenerateFaceMask(positions, triangles), outline: true);
                    break;
                case "Tiny faces":
                {
                    var small = MeshValidation.SmallFaceMask(positions, triangles, requirements.maxFaceArea);
                    var degenerate = MeshValidation.DegenerateFaceMask(positions, triangles);
                    for (int f = 0; f < small.Length; ++f) small[f] &= !degenerate[f];
                    AddFaces(shape, triangles, small, outline: true);
                    break;
                }
                case "Short edges":
                    AddPoints(shape, MeshValidation.ShortEdges(positions, triangles, requirements.maxEdgeLength).vertices);
                    break;
                case "Unused vertices":
                {
                    var used = new bool[positions.Length];
                    foreach (int i in triangles) if (i >= 0 && i < used.Length) used[i] = true;
                    for (int i = 0; i < used.Length; ++i) if (!used[i]) shape.points.Add(i);
                    break;
                }
                case "Non-manifold edges":
                    foreach (var (a, b) in MeshValidation.NonManifoldEdgeList(positions, triangles)) { shape.edges.Add(a); shape.edges.Add(b); }
                    break;
                case "Lightmap UV":
                    AddLightmapOutside(shape, mesh, triangles);
                    break;
            }
            return shape;
        }

        static void AddLightmapOutside(FindingShape shape, Mesh mesh, int[] triangles)
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) return;
            var uv = new List<Vector2>();
            mesh.GetUVs(1, uv);
            var outside = new bool[shape.positions.Length];
            for (int i = 0; i < uv.Count && i < outside.Length; ++i)
                outside[i] = XatlasRepack.UnitOvershoot(uv[i]) > MeshValidation.LightmapTolerance;
            AddPoints(shape, outside);
            var faces = new bool[triangles.Length / 3];
            for (int f = 0; f < faces.Length; ++f)
                for (int k = 0; k < 3; ++k)
                {
                    int i = triangles[f * 3 + k];
                    if (i >= 0 && i < outside.Length && outside[i]) faces[f] = true;
                }
            AddFaces(shape, triangles, faces);
        }

        static void AddFaces(FindingShape shape, int[] triangles, bool[] mask, bool outline = false)
        {
            for (int f = 0; f < mask.Length; ++f)
            {
                if (!mask[f]) continue;
                int a = triangles[f * 3], b = triangles[f * 3 + 1], c = triangles[f * 3 + 2];
                if (!InRange(shape, a) || !InRange(shape, b) || !InRange(shape, c)) continue;
                shape.triangles.Add(a); shape.triangles.Add(b); shape.triangles.Add(c);
                if (!outline) continue;
                shape.edges.AddRange(new[] { a, b, b, c, c, a });
                shape.points.Add(a);
            }
        }

        static void AddPoints(FindingShape shape, bool[] mask)
        {
            for (int i = 0; i < mask.Length && i < shape.positions.Length; ++i) if (mask[i]) shape.points.Add(i);
        }

        static bool InRange(FindingShape shape, int index) => index >= 0 && index < shape.positions.Length;
    }
}
