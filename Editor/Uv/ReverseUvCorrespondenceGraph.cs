using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Geometry adjacency and UV ancestry are separate: a donor chart can
    /// have disconnected descendants, while an intentional detail keeps its layer.</summary>
    internal static class ReverseUvCorrespondenceGraph
    {
        [Serializable] internal sealed class Edge
        {
            public int lod, meshA, faceA, edgeA, meshB = -1, faceB = -1, edgeB = -1;
            public string reason;
        }

        internal sealed class Link
        {
            internal Vector3 a, b;
            internal readonly List<(ReverseUvTransfer.Surface surface, int face, int edge)> uses = new List<(ReverseUvTransfer.Surface, int, int)>();
            internal string reason;
        }

        internal sealed class Graph
        {
            internal readonly List<Link> links = new List<Link>();
            internal readonly List<Edge> edges = new List<Edge>();
        }

        internal static (Vector3, Vector3) Key(Vector3 a, Vector3 b)
            => Compare(a, b) <= 0 ? (a, b) : (b, a);

        static int Compare(Vector3 a, Vector3 b)
        {
            int order = a.x.CompareTo(b.x);
            if (order == 0) order = a.y.CompareTo(b.y);
            return order == 0 ? a.z.CompareTo(b.z) : order;
        }

        internal static Graph Build(ReverseUvTransfer.Surface[] surfaces, CancellationToken token)
        {
            var graph = new Graph();
            var lookup = new Dictionary<(Vector3, Vector3), Link>();
            var ids = new Dictionary<(int, int), int>();
            foreach (var s in surfaces)
                for (int f = 0; f < s.faces.Length; ++f)
                {
                    token.ThrowIfCancellationRequested();
                    ids.Add((s.node, f), ids.Count);
                    for (int e = 0; e < 3; ++e)
                    {
                        var key = Key(s.positions[s.indices[f * 3 + e]], s.positions[s.indices[f * 3 + (e + 1) % 3]]);
                        if (!lookup.TryGetValue(key, out var link))
                        {
                            link = new Link { a = key.Item1, b = key.Item2 };
                            lookup.Add(key, link); graph.links.Add(link);
                        }
                        link.uses.Add((s, f, e));
                    }
                }
            var union = new DisjointSet(ids.Count);
            foreach (var link in graph.links)
            {
                token.ThrowIfCancellationRequested();
                var a = link.uses[0];
                link.reason = link.uses.Count == 1 ? "open" : link.uses.Count > 2 ? "nonmanifold" : Reason(link);
                var record = new Edge { lod = a.surface.lod, meshA = a.surface.node, faceA = a.face, edgeA = a.edge, reason = link.reason };
                if (link.uses.Count == 2)
                {
                    var b = link.uses[1];
                    record.meshB = b.surface.node; record.faceB = b.face; record.edgeB = b.edge;
                    if (link.reason == "continuous") union.Union(ids[(a.surface.node, a.face)], ids[(b.surface.node, b.face)]);
                }
                graph.edges.Add(record);
            }
            var groups = new Dictionary<int, int>();
            foreach (var s in surfaces)
                for (int f = 0; f < s.faces.Length; ++f)
                {
                    int root = union.Find(ids[(s.node, f)]);
                    if (!groups.TryGetValue(root, out int group)) { group = groups.Count; groups.Add(root, group); }
                    s.faces[f].group = group;
                }
            return graph;
        }

        static string Reason(Link link)
        {
            var a = link.uses[0]; var b = link.uses[1];
            var fa = a.surface.faces[a.face]; var fb = b.surface.faces[b.face];
            if (fa.layer != fb.layer) return "layer-seam";
            if (fa.inherited != fb.inherited) return "new-boundary";
            if (fa.chart != fb.chart) return "chart-seam";
            for (int k = 0; k < 2; ++k)
            {
                var p = k == 0 ? link.a : link.b;
                int ca = Corner(a.surface, a.face, p); int cb = Corner(b.surface, b.face, p);
                if ((a.surface.pixels[ca] - b.surface.pixels[cb]).sqrMagnitude > 1e-6f) return "uv-seam";
            }
            return "continuous";
        }

        static int Corner(ReverseUvTransfer.Surface s, int face, Vector3 p)
        {
            for (int k = 0; k < 3; ++k)
                if (s.positions[s.indices[face * 3 + k]].Equals(p)) return face * 3 + k;
            throw new InvalidOperationException("Correspondence edge has no matching corner.");
        }
    }
}
