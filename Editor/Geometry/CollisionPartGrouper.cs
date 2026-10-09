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
            internal Bounds bounds;
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
            if (indices.Length == 0) return new List<Part>();
            var slots = MeshGeometry.WeldPositions(vertices, out _);
            var forest = Forest(indices.Length / 3);
            var edgeFace = new Dictionary<(int, int), int>();
            // UV/normal seams share geometric edges. A single shared point does
            // not make two shells one manifold element.
            for (int f = 0; f < forest.Length; f++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int a = slots[indices[f * 3 + k]], b = slots[indices[f * 3 + (k + 1) % 3]];
                    if (a == b) continue;
                    var edge = a < b ? (a, b) : (b, a);
                    if (edgeFace.TryGetValue(edge, out int other)) Join(forest, f, other);
                    else edgeFace.Add(edge, f);
                }
            }
            var parts = new List<Part>();
            var rootToPart = new Dictionary<int, int>();
            var pointSets = new List<HashSet<Vector3>>();
            Vector3 min = vertices[indices[0]], max = min;
            for (int f = 0; f < forest.Length; f++)
            {
                int root = Find(forest, f);
                if (!rootToPart.TryGetValue(root, out int part))
                {
                    part = parts.Count;
                    rootToPart.Add(root, part);
                    parts.Add(new Part());
                    pointSets.Add(new HashSet<Vector3>());
                }
                for (int k = 0; k < 3; k++)
                {
                    int index = indices[f * 3 + k];
                    parts[part].indices.Add(index);
                    var point = vertices[index];
                    if (!pointSets[part].Add(point)) continue;
                    if (parts[part].points.Count == 0) parts[part].bounds = new Bounds(point, Vector3.zero);
                    else parts[part].bounds.Encapsulate(point);
                    parts[part].points.Add(point);
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                }
            }
            foreach (var part in parts)
                MeshGeometry.PrincipalExtents(part.points, out part.normal);
            float gap = (max - min).magnitude * gapRatio;
            if (gap <= 0 || parts.Count < 2) return parts;
            var groups = Forest(parts.Count);
            float cosine = Mathf.Cos(angle * Mathf.Deg2Rad);
            var order = new List<int>();
            for (int i = 0; i < parts.Count; i++) order.Add(i);
            order.Sort((a, b) => parts[a].bounds.min.x.CompareTo(parts[b].bounds.min.x));
            var surfaces = new TriangleBvh[parts.Count];
            // Sweep broad phase, then exact triangle-surface proximity. Vertex
            // sampling alone misses vertex-to-face and edge-to-edge approaches.
            for (int n = 0; n < order.Count; n++)
            {
                int a = order[n];
                for (int m = n + 1; m < order.Count; m++)
                {
                    int b = order[m];
                    if (parts[b].bounds.min.x > parts[a].bounds.max.x + gap) break;
                    if (Find(groups, a) == Find(groups, b)) continue;
                    if (Mathf.Abs(Vector3.Dot(parts[a].normal, parts[b].normal)) < cosine) continue;
                    if (MeshGeometry.BoundsDistance(parts[a].bounds, parts[b].bounds) > gap) continue;
                    if (surfaces[b] == null) surfaces[b] = new TriangleBvh(vertices, parts[b].indices.ToArray());
                    if (SurfacesNear(vertices, parts[a].indices, surfaces[b], gap)) Join(groups, a, b);
                }
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

        static bool SurfacesNear(Vector3[] positions, List<int> indices, TriangleBvh target, float gap)
        {
            for (int f = 0; f < indices.Count; f += 3)
                if (target.HasTriangleWithin(positions[indices[f]], positions[indices[f + 1]], positions[indices[f + 2]], gap)) return true;
            return false;
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
