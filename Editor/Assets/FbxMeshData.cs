// FbxMeshData.cs — a mesh in FBX terms (control points, polygons, per-corner layer
// values), built from a Unity-space triangle mesh through an FbxSpaceFit. The FBX SDK
// side (FbxStructureEdit) writes it into a document as it is.
//
// Plain arrays in and out: no UnityEngine and no FBX SDK types.

using System;
using System.Collections.Generic;

namespace SashaRX.UnityMeshLab
{
    internal sealed class FbxMeshData
    {
        /// <summary>xyz per control point, FBX mesh space.</summary>
        public double[] controlPoints;
        public int[] polygonSizes;
        /// <summary>Control point per corner, polygon by polygon.</summary>
        public int[] polygonVertices;
        /// <summary>Material index (on the node) per polygon; null writes no material element.</summary>
        public int[] polygonMaterials;
        /// <summary>xyz per corner; null writes no normals.</summary>
        public double[] normals;
        /// <summary>UV sets in FBX set order, uv per corner.</summary>
        public readonly List<double[]> uvs = new List<double[]>();
        public readonly List<string> uvNames = new List<string>();
        /// <summary>rgba per corner; null writes no colours.</summary>
        public double[] colors;

        public int CornerCount => polygonVertices.Length;

        /// <summary>A Unity-space triangle mesh, flattened.</summary>
        internal sealed class Source
        {
            public float[] positions;
            /// <summary>Triangle indices of each submesh.</summary>
            public int[][] submeshTriangles;
            public float[] normals;
            /// <summary>uv per vertex of each FBX UV set, in FBX set order.</summary>
            public readonly List<float[]> uvs = new List<float[]>();
            public readonly List<string> uvNames = new List<string>();
            public float[] colors;
        }

        /// <summary>
        /// The FBX mesh for <paramref name="source"/>: vertices at one position share a control
        /// point, every triangle is a polygon, normals / UVs / colours are per corner.
        /// <paramref name="reverseWinding"/> undoes the reversal the import applies to a
        /// mirrored mesh (<see cref="FbxSpaceFit.WindingRelation"/> = -1), so a reimport gives
        /// the triangles back as they are. Triangles that collapse onto fewer than three
        /// control points are left out.
        /// </summary>
        internal static FbxMeshData FromTriangles(Source source, FbxSpaceFit fit, bool reverseWinding, int[] submeshMaterials)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (fit == null) throw new ArgumentNullException(nameof(fit));
            int vertexCount = source.positions.Length / 3;
            var weld = Weld(source.positions);

            var data = new FbxMeshData();
            var cornerVertex = data.BuildPolygons(source, weld, fit, reverseWinding, submeshMaterials);
            int corners = cornerVertex.Count;

            if (source.normals != null && source.normals.Length == vertexCount * 3)
            {
                data.normals = new double[corners * 3];
                for (int c = 0; c < corners; c++)
                {
                    int v = cornerVertex[c];
                    fit.NormalToFbx(source.normals[v * 3], source.normals[v * 3 + 1], source.normals[v * 3 + 2],
                        out data.normals[c * 3], out data.normals[c * 3 + 1], out data.normals[c * 3 + 2]);
                }
            }
            for (int set = 0; set < source.uvs.Count; set++)
            {
                data.uvs.Add(PerCorner(source.uvs[set], 2, cornerVertex));
                data.uvNames.Add(set < source.uvNames.Count && !string.IsNullOrEmpty(source.uvNames[set]) ? source.uvNames[set] : "UVChannel_" + (set + 1));
            }
            if (source.colors != null && source.colors.Length == vertexCount * 4)
                data.colors = PerCorner(source.colors, 4, cornerVertex);
            return data;
        }

        // Point index of every vertex: vertices at one exact position share a point.
        static int[] Weld(float[] positions)
        {
            var pointOfKey = new Dictionary<FbxCornerMatch.PositionKey, int>();
            var pointOfVertex = new int[positions.Length / 3];
            for (int v = 0; v < pointOfVertex.Length; v++)
            {
                var key = new FbxCornerMatch.PositionKey(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
                if (!pointOfKey.TryGetValue(key, out int point)) pointOfKey[key] = point = pointOfKey.Count;
                pointOfVertex[v] = point;
            }
            return pointOfVertex;
        }

        // Fills control points, polygons and materials; returns the source vertex of every corner.
        List<int> BuildPolygons(Source source, int[] pointOfVertex, FbxSpaceFit fit, bool reverseWinding, int[] submeshMaterials)
        {
            var cornerVertex = new List<int>();
            var corners = new List<int>();
            var points = new List<double>();
            var materials = submeshMaterials != null ? new List<int>() : null;
            var controlPointOf = new Dictionary<int, int>(); // weld point → control point, in first use
            var triangle = new int[3];
            for (int s = 0; s < source.submeshTriangles.Length; s++)
            {
                var tris = source.submeshTriangles[s] ?? Array.Empty<int>();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    triangle[0] = tris[t];
                    triangle[1] = reverseWinding ? tris[t + 2] : tris[t + 1];
                    triangle[2] = reverseWinding ? tris[t + 1] : tris[t + 2];
                    if (Collapsed(triangle, pointOfVertex)) continue;
                    foreach (int v in triangle)
                    {
                        if (!controlPointOf.TryGetValue(pointOfVertex[v], out int cp))
                        {
                            controlPointOf[pointOfVertex[v]] = cp = points.Count / 3;
                            fit.ToFbx(source.positions[v * 3], source.positions[v * 3 + 1], source.positions[v * 3 + 2], out double x, out double y, out double z);
                            points.Add(x); points.Add(y); points.Add(z);
                        }
                        corners.Add(cp);
                        cornerVertex.Add(v);
                    }
                    materials?.Add(submeshMaterials[Math.Min(s, submeshMaterials.Length - 1)]);
                }
            }
            controlPoints = points.ToArray();
            polygonVertices = corners.ToArray();
            polygonSizes = new int[corners.Count / 3];
            for (int p = 0; p < polygonSizes.Length; p++) polygonSizes[p] = 3;
            polygonMaterials = materials?.ToArray();
            return cornerVertex;
        }

        static bool Collapsed(int[] triangle, int[] pointOfVertex)
        {
            int a = pointOfVertex[triangle[0]], b = pointOfVertex[triangle[1]], c = pointOfVertex[triangle[2]];
            return a == b || b == c || a == c;
        }

        static double[] PerCorner(float[] perVertex, int arity, List<int> cornerVertex)
        {
            var values = new double[cornerVertex.Count * arity];
            for (int c = 0; c < cornerVertex.Count; c++)
                for (int k = 0; k < arity; k++) values[c * arity + k] = perVertex[cornerVertex[c] * arity + k];
            return values;
        }
    }
}
