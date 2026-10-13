using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Existing-edge paths from reflex border corners across narrow joins.
    /// UV shape alone proposes cuts; a compactness gate keeps ordinary charts intact.</summary>
    internal static class UvJunctionCuts
    {
        internal sealed class Plan
        {
            internal int[] regions;
            internal int cuts, originalCharts, charts;
        }

        sealed class Edge
        {
            internal int a, b;
            internal readonly List<int> faces = new List<int>(2);
        }

        internal struct Box
        {
            internal Vector2 axis, min, max;
            internal double Area => (double)(max.x - min.x) * (max.y - min.y);
            internal Vector2 Project(Vector2 p) => new Vector2(Vector2.Dot(p, axis), axis.x * p.y - axis.y * p.x);
        }

        static long Key(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
        static double Cross(Vector2 a, Vector2 b) => (double)a.x * b.y - (double)a.y * b.x;

        internal static Plan Find(Vector2[] uv, int[] triangles, CancellationToken token = default)
        {
            var plan = new Plan { regions = new int[triangles.Length / 3] };
            var edges = new Dictionary<long, Edge>();
            var connected = new DisjointSet(plan.regions.Length);
            for (int f = 0; f < plan.regions.Length; ++f)
            {
                if ((f & 1023) == 0) token.ThrowIfCancellationRequested();
                for (int k = 0; k < 3; ++k)
                {
                    int a = triangles[f * 3 + k], b = triangles[f * 3 + (k + 1) % 3];
                    long key = Key(a, b);
                    if (!edges.TryGetValue(key, out var edge))
                    {
                        edge = new Edge { a = a, b = b };
                        edges.Add(key, edge);
                    }
                    else connected.Union(f, edge.faces[0]);
                    edge.faces.Add(f);
                }
            }
            var groups = Enumerable.Range(0, plan.regions.Length).GroupBy(connected.Find).OrderBy(g => g.Min());
            var byComponent = edges.Values.GroupBy(e => connected.Find(e.faces[0])).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                ++plan.originalCharts;
                var faces = group.ToArray();
                var partEdges = byComponent[group.Key];
                // Bounds on the optional search, not a refusal of the input mesh.
                // Large or nonmanifold charts retain their original placement.
                var selected = faces.Length <= 8192 ? Propose(uv, triangles, faces, partEdges, token) : new HashSet<long>();
                var slots = faces.Select((f, i) => (f, i)).ToDictionary(p => p.f, p => p.i);
                var split = new DisjointSet(faces.Length);
                foreach (var edge in partEdges)
                    if (!selected.Contains(Key(edge.a, edge.b)))
                        for (int i = 1; i < edge.faces.Count; ++i) split.Union(slots[edge.faces[0]], slots[edge.faces[i]]);
                var pieces = faces.GroupBy(f => split.Find(slots[f])).Select(g => g.ToArray()).OrderBy(g => g.Min()).ToArray();
                if (pieces.Length > 1 && WorthCutting(uv, triangles, faces, pieces, token))
                {
                    foreach (var edge in partEdges)
                        if (edge.faces.Count == 2 && split.Find(slots[edge.faces[0]]) != split.Find(slots[edge.faces[1]])) ++plan.cuts;
                    foreach (var piece in pieces)
                    {
                        foreach (int f in piece) plan.regions[f] = plan.charts;
                        ++plan.charts;
                    }
                }
                else
                {
                    foreach (int f in faces) plan.regions[f] = plan.charts;
                    ++plan.charts;
                }
            }
            return plan;
        }

        static HashSet<long> Propose(Vector2[] uv, int[] triangles, int[] faces, List<Edge> edges, CancellationToken token)
        {
            var selected = new HashSet<long>();
            int sign = 0;
            foreach (int f in faces)
            {
                int t = f * 3;
                double area = Cross(uv[triangles[t + 1]] - uv[triangles[t]], uv[triangles[t + 2]] - uv[triangles[t]]);
                if (double.IsNaN(area) || double.IsInfinity(area) || area == 0) return selected;
                int current = Math.Sign(area);
                if (sign != 0 && sign != current) return selected;
                sign = current;
            }
            var next = new Dictionary<int, int>(); var previous = new Dictionary<int, int>();
            var internalEdges = new Dictionary<int, List<Edge>>();
            foreach (var edge in edges)
            {
                if (edge.faces.Count > 2) return selected;
                if (edge.faces.Count == 1)
                {
                    if (next.ContainsKey(edge.a) || previous.ContainsKey(edge.b)) return selected;
                    next.Add(edge.a, edge.b); previous.Add(edge.b, edge.a);
                }
                else
                {
                    foreach (int v in new[] { edge.a, edge.b })
                    {
                        if (!internalEdges.TryGetValue(v, out var list))
                        {
                            list = new List<Edge>();
                            internalEdges.Add(v, list);
                        }
                        list.Add(edge);
                    }
                }
            }
            if (next.Count == 0 || next.Keys.Any(v => !previous.ContainsKey(v))) return selected;
            var reflex = new HashSet<int>();
            foreach (int v in next.Keys)
            {
                var incoming = uv[v] - uv[previous[v]]; var outgoing = uv[next[v]] - uv[v];
                if (Cross(incoming, outgoing) * sign < -1e-6 * Math.Sqrt((double)incoming.sqrMagnitude * outgoing.sqrMagnitude)) reflex.Add(v);
            }
            long remaining = 2000000;
            foreach (int v in reflex.OrderBy(v => v))
            {
                token.ThrowIfCancellationRequested();
                if (!internalEdges.ContainsKey(v)) continue;
                double run = Math.Max(BorderRun(v, next, uv, ref remaining), BorderRun(v, previous, uv, ref remaining));
                if (remaining < 0) return new HashSet<long>();
                var path = AcrossJoin(v, uv, internalEdges, next, reflex, run * .5, token, ref remaining);
                if (remaining < 0) return new HashSet<long>();
                foreach (long edge in path) selected.Add(edge);
            }
            return selected;
        }

        // A join may contain intermediate tessellation vertices. Search through
        // the interior, stop at its opposite border and avoid a bent detour that
        // would just peel triangles off the corner. No face interior is changed.
        static List<long> AcrossJoin(int start, Vector2[] uv, Dictionary<int, List<Edge>> edges,
            Dictionary<int, int> border, HashSet<int> reflex, double limit, CancellationToken token, ref long remaining)
        {
            var queue = new SortedSet<(double distance, int vertex)> { (0, start) };
            var distances = new Dictionary<int, double> { [start] = 0 };
            var parents = new Dictionary<int, int>();
            int winner = -1; double best = double.MaxValue; bool paired = false;
            while (queue.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (--remaining < 0) return new List<long>();
                var item = queue.Min; queue.Remove(item);
                if (item.distance > limit || item.distance > best * 1.00001) break;
                int v = item.vertex;
                if (v != start && border.ContainsKey(v))
                {
                    double chord = (uv[start] - uv[v]).magnitude;
                    if (chord <= 0 || item.distance > chord * 1.25) continue;
                    bool joinsReflex = reflex.Contains(v), tie = Math.Abs(item.distance - best) <= best * 1e-5;
                    if (winner < 0 || (!tie && item.distance < best) || (tie && joinsReflex && !paired)
                        || (tie && joinsReflex == paired && v < winner))
                    { winner = v; best = item.distance; paired = joinsReflex; }
                    continue;
                }
                if (!edges.TryGetValue(v, out var incident)) continue;
                foreach (var edge in incident)
                {
                    if (--remaining < 0) return new List<long>();
                    int other = edge.a == v ? edge.b : edge.a;
                    double distance = item.distance + (uv[v] - uv[other]).magnitude;
                    if (distance > limit || (distances.TryGetValue(other, out double old) && distance >= old)) continue;
                    if (distances.TryGetValue(other, out old)) queue.Remove((old, other));
                    distances[other] = distance; parents[other] = v; queue.Add((distance, other));
                }
            }
            var result = new List<long>();
            for (int v = winner; v >= 0 && v != start; v = parents[v]) result.Add(Key(v, parents[v]));
            return result;
        }

        static double BorderRun(int start, Dictionary<int, int> walk, Vector2[] uv, ref long remaining)
        {
            int v = start; double length = 0;
            var direction = uv[walk[start]] - uv[start];
            do
            {
                if (--remaining < 0) return 0;
                int end = walk[v]; var edge = uv[end] - uv[v];
                if (Vector2.Dot(edge, direction) <= 0
                    || Math.Abs(Cross(edge, direction)) > 1e-5 * Math.Sqrt((double)edge.sqrMagnitude * direction.sqrMagnitude)) break;
                length += edge.magnitude; v = end;
            } while (v != start);
            return length;
        }

        static bool WorthCutting(Vector2[] uv, int[] triangles, int[] faces, int[][] pieces, CancellationToken token)
        {
            double area = BestBox(uv, triangles, faces, token).Area;
            double sum = 0;
            foreach (var piece in pieces) sum += BestBox(uv, triangles, piece, token).Area;
            return area > 0 && sum < area * .85;
        }

        internal static Box BestBox(Vector2[] uv, int[] triangles, int[] faces, CancellationToken token)
        {
            var best = MeasureBox(uv, triangles, faces, Vector2.right);
            // At most 64 directions; samples include every edge on small fixtures.
            int step = Math.Max(1, (faces.Length * 3 + 63) / 64);
            for (int i = 0; i < faces.Length * 3; i += step)
            {
                token.ThrowIfCancellationRequested();
                int t = faces[i / 3] * 3; int k = i % 3;
                var axis = uv[triangles[t + (k + 1) % 3]] - uv[triangles[t + k]];
                if (axis.sqrMagnitude == 0) continue;
                var box = MeasureBox(uv, triangles, faces, axis.normalized);
                if (box.Area < best.Area) best = box;
            }
            return best;
        }

        internal static Box AxisBox(Vector2[] uv, int[] triangles, int[] faces)
            => MeasureBox(uv, triangles, faces, Vector2.right);

        static Box MeasureBox(Vector2[] uv, int[] triangles, int[] faces, Vector2 axis)
        {
            var box = new Box { axis = axis, min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue) };
            foreach (int f in faces)
                for (int k = 0; k < 3; ++k)
                {
                    var p = box.Project(uv[triangles[f * 3 + k]]);
                    box.min = Vector2.Min(box.min, p); box.max = Vector2.Max(box.max, p);
                }
            return box;
        }

        internal static Mesh CopyForRepack(Mesh source, Vector2 metric, CancellationToken token = default)
        {
            var uv = source.uv.Select(p => Vector2.Scale(p, metric)).ToArray();
            if (uv.Length != source.vertexCount) return null;
            var triangles = source.triangles;
            var plan = Find(uv, triangles, token);
            if (plan.cuts == 0) return null;
            var tags = new Vector2[triangles.Length];
            for (int i = 0; i < tags.Length; ++i) tags[i] = new Vector2(plan.regions[i / 3], 0);
            var copy = ReverseUvMesh.Copy(source, triangles, tags);
            copy.name = source.name;
            UvtLog.Info(UvtLog.Category.Repack, $"[JunctionCuts] '{source.name}': {plan.originalCharts} → {plan.charts} charts; {plan.cuts} existing-edge cuts; UV0 and donor corners preserved.");
            return copy;
        }

        internal static Vector2[] Pack(Vector3[] corners, Vector2[] pixels, int padding, CancellationToken token, bool alignToAxis = true)
        {
            var unique = new Dictionary<(Vector3, Vector2), int>();
            var uv = new List<Vector2>(); var indices = new int[corners.Length];
            for (int i = 0; i < indices.Length; ++i)
            {
                var key = (corners[i], pixels[i]);
                if (!unique.TryGetValue(key, out int vertex))
                { vertex = uv.Count; unique.Add(key, vertex); uv.Add(pixels[i]); }
                indices[i] = vertex;
            }
            var array = uv.ToArray(); var plan = Find(array, indices, token);
            if (plan.cuts == 0) return pixels;
            var pieces = Enumerable.Range(0, plan.regions.Length).GroupBy(f => plan.regions[f])
                .Select(g => (faces: g.ToArray(), box: alignToAxis ? BestBox(array, indices, g.ToArray(), token)
                    : AxisBox(array, indices, g.ToArray()))).ToArray();
            double rectangles = pieces.Sum(p => (p.box.max.x - p.box.min.x + padding) * (double)(p.box.max.y - p.box.min.y + padding));
            float shelf = Math.Max(pieces.Max(p => p.box.max.x - p.box.min.x), (float)Math.Sqrt(rectangles * 1.25));
            var output = new Vector2[pixels.Length]; float x = 0, y = 0, height = 0;
            foreach (var piece in pieces.OrderByDescending(p => p.box.max.y - p.box.min.y).ThenBy(p => p.faces[0]))
            {
                token.ThrowIfCancellationRequested();
                float width = piece.box.max.x - piece.box.min.x, h = piece.box.max.y - piece.box.min.y;
                if (x > 0 && x + width > shelf) { y += height + padding; x = 0; height = 0; }
                foreach (int f in piece.faces)
                    for (int k = 0; k < 3; ++k)
                        output[f * 3 + k] = piece.box.Project(pixels[f * 3 + k]) - piece.box.min + new Vector2(x, y);
                x += width + padding; height = Math.Max(height, h);
            }
            UvtLog.Info(UvtLog.Category.Repack, $"[JunctionCuts] Reverse charts: {plan.originalCharts} → {plan.charts}; {plan.cuts} existing-edge cuts.");
            return output;
        }
    }
}
