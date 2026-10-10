using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Place new connected charts in vacant texels of the entire LOD
    /// chain. Inherited placements are immutable; all moves are rigid.</summary>
    internal static class ReverseUvAtlasPlacement
    {
        sealed class Island
        {
            internal readonly List<int> corners = new List<int>();
            internal Vector2 min, max;
        }

        internal static Vector2[] Place(Vector3[] positions, Vector2[] pixels, List<ReverseUvTransfer.Surface[]> previous,
            ReverseUvTransfer.Surface[] current, ReverseUvTransfer.Options options, float width, float height, CancellationToken token)
        {
            var islands = Islands(positions, pixels);
            int side = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Math.Max(width, height)));
            for (; side <= options.maxAtlasSize; side *= 2)
            {
                token.ThrowIfCancellationRequested();
                var rows = Enumerable.Range(0, side).Select(_ => new List<(int start, int end)>()).ToArray();
                foreach (var level in previous) Occupy(level, rows, options.padding, false, token);
                Occupy(current, rows, options.padding, true, token);
                var output = new Vector2[pixels.Length]; bool complete = true; long work = 0;
                foreach (var island in islands.OrderByDescending(i => Math.Max(i.max.x - i.min.x, i.max.y - i.min.y))
                    .ThenByDescending(i => (i.max.x - i.min.x) * (i.max.y - i.min.y)))
                {
                    int w = Mathf.CeilToInt(island.max.x - island.min.x) + options.padding * 2;
                    int h = Mathf.CeilToInt(island.max.y - island.min.y) + options.padding * 2;
                    if (!Find(rows, w, h, options.padding, token, ref work, out int x, out int y)) { complete = false; break; }
                    foreach (int corner in island.corners)
                        output[corner] = pixels[corner] - island.min + new Vector2(x + options.padding, y + options.padding);
                    for (int r = y; r < y + h; ++r) Reserve(rows[r], x, x + w);
                }
                if (complete) return output;
            }
            throw new InvalidOperationException("New reverse UV charts cannot fit within the configured atlas limit.");
        }

        static List<Island> Islands(Vector3[] positions, Vector2[] pixels)
        {
            var union = new DisjointSet(pixels.Length / 3);
            var edges = new Dictionary<((Vector3, Vector2), (Vector3, Vector2)), int>();
            for (int f = 0; f < pixels.Length / 3; ++f)
                for (int e = 0; e < 3; ++e)
                {
                    int a = f * 3 + e, b = f * 3 + (e + 1) % 3;
                    var x = (positions[a], pixels[a]); var y = (positions[b], pixels[b]);
                    if (edges.TryGetValue((x, y), out int other) || edges.TryGetValue((y, x), out other)) union.Union(f, other);
                    else edges.Add((x, y), f);
                }
            var grouped = new Dictionary<int, Island>();
            for (int f = 0; f < pixels.Length / 3; ++f)
            {
                int root = union.Find(f);
                if (!grouped.TryGetValue(root, out var island))
                { island = new Island { min = pixels[f * 3], max = pixels[f * 3] }; grouped.Add(root, island); }
                for (int k = 0; k < 3; ++k)
                {
                    int corner = f * 3 + k; island.corners.Add(corner);
                    island.min = Vector2.Min(island.min, pixels[corner]); island.max = Vector2.Max(island.max, pixels[corner]);
                }
            }
            return grouped.Values.ToList();
        }

        static bool Find(List<(int start, int end)>[] rows, int width, int height, int border, CancellationToken token,
            ref long work, out int x, out int y)
        {
            for (y = border; y + height <= rows.Length; ++y)
            {
                token.ThrowIfCancellationRequested();
                x = border;
                while (x + width <= rows.Length)
                {
                    bool free = true; int nextX = x + 1;
                    for (int r = y; r < y + height && free; ++r)
                    {
                        if (++work > 128000000) throw new InvalidOperationException("Reverse UV atlas vacancy search exceeded its work budget.");
                        int first = FirstAfter(rows[r], x);
                        if (first == rows[r].Count || rows[r][first].start >= x + width) continue;
                        nextX = rows[r][first].end; free = false;
                    }
                    if (free) return true;
                    x = nextX;
                }
            }
            x = y = 0; return false;
        }

        static int FirstAfter(List<(int start, int end)> intervals, int x)
        {
            int lo = 0, hi = intervals.Count;
            while (lo < hi) { int middle = (lo + hi) / 2; if (intervals[middle].end <= x) lo = middle + 1; else hi = middle; }
            return lo;
        }

        static void Reserve(List<(int start, int end)> intervals, int start, int end)
        {
            if (start >= end) return;
            int first = FirstAfter(intervals, start - 1), last = first;
            while (last < intervals.Count && intervals[last].start <= end)
            { start = Math.Min(start, intervals[last].start); end = Math.Max(end, intervals[last].end); ++last; }
            if (last > first) intervals.RemoveRange(first, last - first);
            intervals.Insert(first, (start, end));
        }

        static void Occupy(ReverseUvTransfer.Surface[] surfaces, List<(int start, int end)>[] rows, int padding, bool inheritedOnly, CancellationToken token)
        {
            foreach (var s in surfaces)
                for (int f = 0; f < s.faces.Length; ++f)
                {
                    token.ThrowIfCancellationRequested();
                    if (inheritedOnly && !s.faces[f].inherited) continue;
                    var triangle = new[] { s.pixels[f * 3], s.pixels[f * 3 + 1], s.pixels[f * 3 + 2] };
                    int lo = Math.Max(0, Mathf.FloorToInt(triangle.Min(p => p.y) - padding));
                    int hi = Math.Min(rows.Length, Mathf.CeilToInt(triangle.Max(p => p.y) + padding));
                    for (int y = lo; y < hi; ++y)
                    {
                        float min = float.MaxValue, max = float.MinValue;
                        float bottom = y - padding, top = y + 1 + padding;
                        foreach (var p in triangle)
                            if (p.y >= bottom && p.y <= top) { min = Math.Min(min, p.x); max = Math.Max(max, p.x); }
                        for (int e = 0; e < 3; ++e)
                        {
                            var a = triangle[e]; var b = triangle[(e + 1) % 3];
                            float rise = b.y - a.y;
                            if (Math.Abs(rise) <= 1e-6f)
                            {
                                // A nearly horizontal edge can straddle a row
                                // boundary. Reserve its full span conservatively.
                                if (Math.Min(a.y, b.y) <= top && Math.Max(a.y, b.y) >= bottom)
                                { min = Math.Min(min, Math.Min(a.x, b.x)); max = Math.Max(max, Math.Max(a.x, b.x)); }
                                continue;
                            }
                            foreach (float v in new[] { bottom, top })
                            {
                                float t = (v - a.y) / rise;
                                if (t < 0 || t > 1) continue;
                                float x = a.x + (b.x - a.x) * t; min = Math.Min(min, x); max = Math.Max(max, x);
                            }
                        }
                        if (min > max) continue;
                        Reserve(rows[y], Math.Max(0, Mathf.FloorToInt(min - padding)), Math.Min(rows.Length, Mathf.CeilToInt(max + padding)));
                    }
                }
        }
    }
}
