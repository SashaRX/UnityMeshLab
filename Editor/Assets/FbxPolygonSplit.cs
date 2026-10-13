// A local split uses the source import's triangulation, never a newly guessed diagonal.
using System;
using System.Collections.Generic;
using System.Linq;

namespace SashaRX.UnityMeshLab
{
    internal sealed class FbxPolygonSplit
    {
        public int[] sizes, corners, polygons;
        public int splitCount;

        public static FbxPolygonSplit Plan(int[] sizes, bool[] conflicts, int[] triangles)
        {
            if (sizes == null || conflicts == null || triangles == null || triangles.Length % 3 != 0
                || sizes.Any(size => size < 3) || sizes.Sum() != conflicts.Length)
                throw new ArgumentException("FBX split requires complete polygon and triangle arrays.");
            var (parent, starts) = PolygonCorners(sizes, conflicts.Length);
            var faces = SourceTriangles(sizes, triangles, parent);
            var newSizes = new List<int>(); var newCorners = new List<int>(); var newPolygons = new List<int>();
            var plan = new FbxPolygonSplit();
            for (int p = 0; p < sizes.Length; ++p)
                plan.splitCount += AppendPolygon(p, sizes[p], starts[p], conflicts, faces[p], newSizes, newCorners, newPolygons);
            plan.sizes = newSizes.ToArray(); plan.corners = newCorners.ToArray(); plan.polygons = newPolygons.ToArray();
            return plan;
        }

        static (int[] parent, int[] starts) PolygonCorners(int[] sizes, int cornerCount)
        {
            var parent = new int[cornerCount]; var starts = new int[sizes.Length];
            int end = 0;
            for (int p = 0; p < sizes.Length; ++p)
            {
                starts[p] = end;
                for (int k = 0; k < sizes[p]; ++k) parent[end++] = p;
            }
            if (end != cornerCount) throw new InvalidOperationException("FBX split corner count differs from its polygons.");
            return (parent, starts);
        }

        static List<int[]>[] SourceTriangles(int[] sizes, int[] triangles, int[] parent)
        {
            var faces = new List<int[]>[sizes.Length];
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= parent.Length || b >= parent.Length || c >= parent.Length) continue;
                int p = parent[a];
                if (parent[b] != p || parent[c] != p) continue;
                // Restore FBX winding from cyclic corner order (Unity mirrors X).
                int n = sizes[p];
                if ((b - a + n) % n + (c - b + n) % n + (a - c + n) % n != n) (b, c) = (c, b);
                (faces[p] ??= new List<int[]>()).Add(new[] { a, b, c });
            }
            return faces;
        }

        static int AppendPolygon(int p, int size, int start, bool[] conflicts, List<int[]> faces,
            List<int> sizes, List<int> corners, List<int> polygons)
        {
            bool split = size > 3 && Enumerable.Range(start, size).Any(c => conflicts[c]);
            if (!split)
            {
                sizes.Add(size); polygons.Add(p); corners.AddRange(Enumerable.Range(start, size));
                return 0;
            }
            Audit(size, start, faces);
            foreach (var face in faces) { sizes.Add(3); polygons.Add(p); corners.AddRange(face); }
            return 1;
        }

        static void Audit(int size, int start, List<int[]> faces)
        {
            if (faces == null || faces.Count != size - 2)
                throw new InvalidOperationException("FBX seam split refused: source triangulation is incomplete.");
            var edges = new Dictionary<(int, int), int>();
            foreach (var face in faces)
            {
                if (face.Distinct().Count() != 3) throw new InvalidOperationException("FBX seam split has a degenerate triangle.");
                for (int k = 0; k < 3; k++)
                {
                    var edge = (face[k], face[(k + 1) % 3]);
                    edges.TryGetValue(edge, out int count); edges[edge] = count + 1;
                }
            }
            for (int k = 0; k < size; k++)
            {
                var rim = (start + k, start + (k + 1) % size);
                if (!edges.TryGetValue(rim, out int count) || count != 1)
                    throw new InvalidOperationException("FBX seam split does not preserve the polygon boundary.");
                edges.Remove(rim);
            }
            foreach (var pair in edges)
                if (pair.Value != 1 || !edges.TryGetValue((pair.Key.Item2, pair.Key.Item1), out int count) || count != 1)
                    throw new InvalidOperationException("FBX seam split has an unpaired or overlapping internal edge.");
        }

        public T[] Expand<T>(T[] values, int stride = 1)
        {
            if (values == null) return null;
            var expanded = new T[corners.Length * stride];
            for (int c = 0; c < corners.Length; c++)
                for (int k = 0; k < stride; k++)
                    if (corners[c] * stride + k < values.Length) expanded[c * stride + k] = values[corners[c] * stride + k];
            return expanded;
        }
    }
}
