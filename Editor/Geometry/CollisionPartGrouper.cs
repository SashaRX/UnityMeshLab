using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Group disconnected geometry by proximity and principal plane.
    /// This preserves independently oriented structures before convex decomposition.</summary>
    internal static class CollisionPartGrouper
    {
        internal sealed class Part
        {
            internal readonly List<int> indices = new List<int>();
            internal readonly List<Vector3> points = new List<Vector3>();
            internal Vector3 normal;
            internal readonly List<Part> elements = new List<Part>();
            internal IEnumerable<Part> Elements
            {
                get
                {
                    if (elements.Count == 0) yield return this;
                    else foreach (var element in elements) yield return element;
                }
            }
        }

        internal static List<Part> Group(Vector3[] vertices, int[] indices, float gapRatio, float angle)
        {
            var slots = MeshGeometry.WeldPositions(vertices, out int count);
            var forest = Forest(count);
            for (int i = 0; i < indices.Length; i += 3)
            {
                Join(forest, slots[indices[i]], slots[indices[i + 1]]);
                Join(forest, slots[indices[i]], slots[indices[i + 2]]);
            }
            var parts = new List<Part>();
            var rootToPart = new Dictionary<int, int>();
            var pointPart = new Dictionary<Vector3, int>();
            Vector3 min = vertices[indices[0]], max = min;
            foreach (int index in indices)
            {
                int root = Find(forest, slots[index]);
                if (!rootToPart.TryGetValue(root, out int part))
                {
                    part = parts.Count;
                    rootToPart.Add(root, part);
                    parts.Add(new Part());
                }
                parts[part].indices.Add(index);
                var point = vertices[index];
                if (pointPart.ContainsKey(point)) continue;
                pointPart.Add(point, part);
                parts[part].points.Add(point);
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
            foreach (var part in parts)
                MeshGeometry.PrincipalExtents(part.points, out part.normal);
            float gap = (max - min).magnitude * gapRatio;
            if (gap <= 0 || parts.Count < 2) return parts;
            var groups = Forest(parts.Count);
            float cosine = Mathf.Cos(angle * Mathf.Deg2Rad);
            var grid = new Dictionary<Vector3Int, List<Vector3>>();
            // Incremental spatial hash: each geometric point is inserted once.
            // Coordinates are relative to the bounds, independent of mesh origin.
            foreach (var pair in pointPart)
            {
                var cell = Cell(pair.Key, min, gap);
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                        {
                            if (!grid.TryGetValue(cell + new Vector3Int(x, y, z), out var neighbors)) continue;
                            foreach (var point in neighbors)
                            {
                                int other = pointPart[point];
                                if (Find(groups, pair.Value) == Find(groups, other)) continue;
                                if (Mathf.Abs(Vector3.Dot(parts[pair.Value].normal, parts[other].normal)) < cosine) continue;
                                if ((pair.Key - point).sqrMagnitude <= gap * gap) Join(groups, pair.Value, other);
                            }
                        }
                if (!grid.TryGetValue(cell, out var points)) grid[cell] = points = new List<Vector3>();
                points.Add(pair.Key);
            }
            var merged = new List<Part>();
            rootToPart.Clear();
            for (int i = 0; i < parts.Count; i++)
            {
                int root = Find(groups, i);
                if (!rootToPart.TryGetValue(root, out int group))
                {
                    group = merged.Count;
                    rootToPart.Add(root, group);
                    merged.Add(new Part());
                }
                merged[group].elements.Add(parts[i]);
                merged[group].indices.AddRange(parts[i].indices);
                merged[group].points.AddRange(parts[i].points);
            }
            return merged;
        }

        static Vector3Int Cell(Vector3 point, Vector3 origin, float size)
        {
            var p = (point - origin) / size;
            return new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
        }

        static int[] Forest(int count)
        {
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;
            return parent;
        }

        static int Find(int[] parent, int index)
        {
            while (parent[index] != index) { parent[index] = parent[parent[index]]; index = parent[index]; }
            return index;
        }

        static void Join(int[] parent, int a, int b)
        {
            a = Find(parent, a);
            b = Find(parent, b);
            if (a != b) parent[b] = a;
        }
    }
}
