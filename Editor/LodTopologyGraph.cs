using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal sealed class LodTopologyGraph
    {
        internal readonly struct Halfedge
        {
            internal readonly int face, slot, a, b;
            internal Halfedge(int face, int slot, int a, int b) { this.face = face; this.slot = slot; this.a = a; this.b = b; }
        }
        internal readonly Dictionary<long, List<Halfedge>> edges = new Dictionary<long, List<Halfedge>>();
        internal readonly Dictionary<int, HashSet<int>> neighbors = new Dictionary<int, HashSet<int>>();
        internal readonly Dictionary<int, List<int>> vertexFaces = new Dictionary<int, List<int>>();
        internal readonly HashSet<int> boundary = new HashSet<int>();
        internal (int components, int euler, int boundaries) signature;
        internal static long EdgeKey(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);

        internal static bool TryBuild(List<LodSourceTopology.Face> faces, LodMeshData data, out LodTopologyGraph graph, out string error)
        {
            graph = new LodTopologyGraph();
            error = null;
            var positions = new Dictionary<int, Vector3>();
            var boundaryNeighbors = new Dictionary<int, HashSet<int>>();
            for (int f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face.points.Length < 3 || face.points.Distinct().Count() != face.points.Length)
                    return Fail("Polygon has repeated or missing vertices.", out error);
                for (int i = 0; i < face.points.Length; i++)
                {
                    int a = face.points[i], b = face.points[(i + 1) % face.points.Length];
                    Vector3 p = data.positions[face.vertices[i]];
                    if (positions.TryGetValue(a, out var previous) && (previous - p).sqrMagnitude > data.scale * data.scale * 1e-12f)
                        return Fail("One source control point maps to different working positions.", out error);
                    positions[a] = p;
                    if (!graph.vertexFaces.TryGetValue(a, out var incident)) graph.vertexFaces[a] = incident = new List<int>();
                    incident.Add(f);
                    AddNeighbor(graph.neighbors, a, b); AddNeighbor(graph.neighbors, b, a);
                    long key = EdgeKey(a, b);
                    if (!graph.edges.TryGetValue(key, out var edge)) graph.edges[key] = edge = new List<Halfedge>();
                    edge.Add(new Halfedge(f, i, a, b));
                    if (edge.Count > 2) return Fail("Non-manifold edge in source polygons.", out error);
                }
            }
            foreach (var edge in graph.edges.Values)
            {
                var h = edge[0];
                if (edge.Count == 2)
                {
                    if (h.a != edge[1].b || h.b != edge[1].a) return Fail("Inconsistent polygon winding.", out error);
                }
                else
                {
                    graph.boundary.Add(h.a); graph.boundary.Add(h.b);
                    AddNeighbor(boundaryNeighbors, h.a, h.b); AddNeighbor(boundaryNeighbors, h.b, h.a);
                }
            }
            if (boundaryNeighbors.Values.Any(n => n.Count != 2)) return Fail("Non-manifold boundary vertex.", out error);
            foreach (var pair in graph.vertexFaces)
            {
                var connected = new HashSet<int> { pair.Value[0] };
                var queue = new Queue<int>(); queue.Enqueue(pair.Value[0]);
                while (queue.Count > 0)
                {
                    var face = faces[queue.Dequeue()];
                    int slot = Array.IndexOf(face.points, pair.Key);
                    foreach (int next in new[] { face.points[(slot + 1) % face.points.Length], face.points[(slot + face.points.Length - 1) % face.points.Length] })
                        foreach (var halfedge in graph.edges[EdgeKey(pair.Key, next)])
                            if (connected.Add(halfedge.face)) queue.Enqueue(halfedge.face);
                }
                if (connected.Count != pair.Value.Count) return Fail("Non-manifold vertex fan.", out error);
            }
            graph.signature = (Components(graph.neighbors), positions.Count - graph.edges.Count + faces.Count, Components(boundaryNeighbors));
            return true;
        }

        static bool Fail(string message, out string error) { error = message; return false; }
        static void AddNeighbor(Dictionary<int, HashSet<int>> map, int a, int b)
        {
            if (!map.TryGetValue(a, out var neighbors)) map[a] = neighbors = new HashSet<int>();
            neighbors.Add(b);
        }
        static int Components(Dictionary<int, HashSet<int>> map)
        {
            int count = 0;
            var seen = new HashSet<int>();
            var queue = new Queue<int>();
            foreach (int v in map.Keys)
            {
                if (!seen.Add(v)) continue;
                count++; queue.Enqueue(v);
                while (queue.Count > 0)
                    foreach (int next in map[queue.Dequeue()]) if (seen.Add(next)) queue.Enqueue(next);
            }
            return count;
        }
    }
}
