using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Geometry boundaries after position welding. UV and normal seams do not create holes.</summary>
    internal sealed class ViewportTopology
    {
        internal sealed class Edge { internal int a, b, uses; }
        internal sealed class Rim
        {
            internal int[] edges;
            internal bool closed;
            internal Bounds bounds;
            internal float length;
            internal string Reason => closed ? "Closed boundary loop. It may be an intentional opening." : "Open or branched boundary; not a simple hole loop.";
        }
        internal Vector3[] positions;
        internal int[] triangles, vertexRepresentatives;
        internal Edge[] edges;
        internal Rim[] rims;
        internal TriangleBvh bvh;
        internal int degenerateFaces, nonmanifoldEdges;

        internal static ViewportTopology Build(Vector3[] positions, int[] triangles, CancellationToken token)
        {
            if (triangles.Length % 3 != 0) throw new ArgumentException("Viewport triangle indices must be complete faces.");
            var result = new ViewportTopology { positions = positions, triangles = triangles };
            var weld = MeshGeometry.WeldPositions(positions, out int count);
            var representatives = new int[count];
            for (int i = 0; i < weld.Length; ++i) representatives[weld[i]] = i;
            var used = new HashSet<int>();
            var edges = new List<Edge>(); var lookup = new Dictionary<(int, int), int>();
            for (int f = 0; f < triangles.Length; f += 3) {
                token.ThrowIfCancellationRequested();
                int a = triangles[f], b = triangles[f + 1], c = triangles[f + 2];
                if (a < 0 || b < 0 || c < 0 || a >= positions.Length || b >= positions.Length || c >= positions.Length)
                    throw new ArgumentException("Invalid mesh index in viewport topology.");
                used.Add(weld[a]); used.Add(weld[b]); used.Add(weld[c]);
                if (weld[a] == weld[b] || weld[b] == weld[c] || weld[c] == weld[a] ||
                    Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).sqrMagnitude == 0) {
                    ++result.degenerateFaces; continue;
                }
                Add(a, b); Add(b, c); Add(c, a);
            }
            void Add(int a, int b)
            {
                int wa = weld[a], wb = weld[b];
                var key = wa < wb ? (wa, wb) : (wb, wa);
                if (!lookup.TryGetValue(key, out int index)) {
                    index = edges.Count; lookup.Add(key, index);
                    edges.Add(new Edge { a = representatives[key.Item1], b = representatives[key.Item2] });
                }
                ++edges[index].uses;
            }
            var incident = new Dictionary<int, List<int>>();
            for (int i = 0; i < edges.Count; ++i) {
                token.ThrowIfCancellationRequested();
                var edge = edges[i];
                if (edge.uses > 2) ++result.nonmanifoldEdges;
                if (edge.uses != 1) continue;
                foreach (int v in new[] { edge.a, edge.b }) {
                    if (!incident.TryGetValue(v, out var list)) incident[v] = list = new List<int>();
                    list.Add(i);
                }
            }
            var rims = new List<Rim>(); var seen = new HashSet<int>();
            for (int seed = 0; seed < edges.Count; ++seed) {
                if (edges[seed].uses != 1 || !seen.Add(seed)) continue;
                var group = new List<int>(); var queue = new Queue<int>(); queue.Enqueue(seed);
                var bounds = new Bounds(positions[edges[seed].a], Vector3.zero);
                bool closed = true; float length = 0;
                while (queue.Count > 0) {
                    token.ThrowIfCancellationRequested();
                    int index = queue.Dequeue(); group.Add(index); var edge = edges[index];
                    length += Vector3.Distance(positions[edge.a], positions[edge.b]);
                    foreach (int v in new[] { edge.a, edge.b }) {
                        bounds.Encapsulate(positions[v]); closed &= incident[v].Count == 2;
                        foreach (int other in incident[v]) if (seen.Add(other)) queue.Enqueue(other);
                    }
                }
                group.Sort(); rims.Add(new Rim { edges = group.ToArray(), closed = closed, bounds = bounds, length = length });
            }
            result.edges = edges.ToArray(); result.rims = rims.ToArray();
            var visibleVertices = new List<int>();
            for (int i = 0; i < representatives.Length; ++i) if (triangles.Length == 0 || used.Contains(i)) visibleVertices.Add(representatives[i]);
            result.vertexRepresentatives = visibleVertices.ToArray();
            token.ThrowIfCancellationRequested();
            if (triangles.Length > 0) result.bvh = new TriangleBvh(positions, triangles);
            return result;
        }
    }
}
