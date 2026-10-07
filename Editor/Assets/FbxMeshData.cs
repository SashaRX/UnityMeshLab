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
        /// <summary>xyz per corner each; null writes no tangent frame.</summary>
        public double[] tangents, binormals;

        public int CornerCount => polygonVertices.Length;

        /// <summary>A Unity-space triangle (or quad) mesh, flattened.</summary>
        internal sealed class Source
        {
            public float[] positions;
            /// <summary>Face indices of each submesh: triangles, or quads where <see cref="submeshFaceSizes"/> says 4.</summary>
            public int[][] submeshTriangles;
            /// <summary>Corners per face of each submesh; null means triangles throughout.</summary>
            public int[] submeshFaceSizes;
            public float[] normals;
            /// <summary>xyzw per vertex (w: bitangent sign); null writes no tangent frame.</summary>
            public float[] tangents;
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
        /// the triangles back as they are. Faces without area (corners on fewer distinct
        /// control points than they have, or on one line) are left out.
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
            if (data.normals != null && source.tangents != null && source.tangents.Length == vertexCount * 4)
                data.WriteTangentFrame(source, fit, cornerVertex);
            for (int set = 0; set < source.uvs.Count; set++)
            {
                if (source.uvs[set] == null || source.uvs[set].Length != vertexCount * 2) break; // a set must exist at every corner, and sets go in order
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
            var sizes = new List<int>();
            var points = new List<double>();
            bool hasMaterials = submeshMaterials != null && submeshMaterials.Length > 0;
            var materials = hasMaterials ? new List<int>() : null;
            var controlPointOf = new Dictionary<int, int>(); // weld point → control point, in first use
            for (int s = 0; s < source.submeshTriangles.Length; s++)
            {
                var indices = source.submeshTriangles[s] ?? Array.Empty<int>();
                int size = source.submeshFaceSizes != null && s < source.submeshFaceSizes.Length ? source.submeshFaceSizes[s] : 3;
                var face = new int[size];
                for (int f = 0; f + size <= indices.Length; f += size)
                {
                    // Reversed: the first corner stays, the rest run backwards.
                    for (int k = 0; k < size; k++) face[k] = indices[f + (reverseWinding && k > 0 ? size - k : k)];
                    if (Degenerate(face, pointOfVertex, source.positions)) continue;
                    foreach (int v in face)
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
                    sizes.Add(size);
                    materials?.Add(submeshMaterials[Math.Min(s, submeshMaterials.Length - 1)]);
                }
            }
            controlPoints = points.ToArray();
            polygonVertices = corners.ToArray();
            polygonSizes = sizes.ToArray();
            polygonMaterials = materials?.ToArray();
            return cornerVertex;
        }

        // A face whose corners do not land on as many distinct points, or that spans no area
        // (collinear corners): a polygon without a normal (checklist: no degenerate polygons).
        static bool Degenerate(int[] face, int[] pointOfVertex, float[] positions)
        {
            for (int i = 0; i < face.Length; i++)
                for (int j = i + 1; j < face.Length; j++)
                    if (pointOfVertex[face[i]] == pointOfVertex[face[j]]) return true;
            // Newell's normal is twice the area; against the longest edge it is the face's
            // thickness, here a millionth of its length or less.
            double nx = 0, ny = 0, nz = 0, longest = 0;
            for (int i = 0; i < face.Length; i++)
            {
                int a = face[i] * 3, b = face[(i + 1) % face.Length] * 3;
                double ax = positions[a], ay = positions[a + 1], az = positions[a + 2];
                double bx = positions[b], by = positions[b + 1], bz = positions[b + 2];
                nx += (ay - by) * (az + bz);
                ny += (az - bz) * (ax + bx);
                nz += (ax - bx) * (ay + by);
                longest = Math.Max(longest, (bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
            }
            return nx * nx + ny * ny + nz * nz <= 1e-12 * longest * longest;
        }

        // Tangent and binormal per corner. Unity keeps a tangent and the bitangent's sign; the
        // FBX keeps both vectors, so the binormal is rebuilt as cross(normal, tangent) · sign in
        // Unity space and both go back as directions through the inverse map.
        void WriteTangentFrame(Source source, FbxSpaceFit fit, List<int> cornerVertex)
        {
            tangents = new double[cornerVertex.Count * 3];
            binormals = new double[cornerVertex.Count * 3];
            for (int c = 0; c < cornerVertex.Count; c++)
            {
                int v = cornerVertex[c];
                float tx = source.tangents[v * 4], ty = source.tangents[v * 4 + 1], tz = source.tangents[v * 4 + 2], w = source.tangents[v * 4 + 3];
                float nx = source.normals[v * 3], ny = source.normals[v * 3 + 1], nz = source.normals[v * 3 + 2];
                float bx = (ny * tz - nz * ty) * w, by = (nz * tx - nx * tz) * w, bz = (nx * ty - ny * tx) * w;
                fit.DirectionToFbx(tx, ty, tz, out tangents[c * 3], out tangents[c * 3 + 1], out tangents[c * 3 + 2]);
                fit.DirectionToFbx(bx, by, bz, out binormals[c * 3], out binormals[c * 3 + 1], out binormals[c * 3 + 2]);
            }
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
