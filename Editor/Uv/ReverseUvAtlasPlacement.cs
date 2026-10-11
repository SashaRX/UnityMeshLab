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
            internal int rescueFace = -1;
            internal double rescueWidth, rescueHeight;
            internal (double length,double x,double y) rescueFrame;
        }

        internal static Vector2[] Place(Vector3[] positions, Vector2[] pixels, List<ReverseUvTransfer.Surface[]> previous,
            ReverseUvTransfer.Surface[] current, ReverseUvTransfer.Options options, float width, float height, CancellationToken token,
            bool[] fallback = null, float density = 0,(double length,double x,double y)[] frames=null)
        {
            var baseline = TryPlace(positions, pixels, previous, current, options, width, height, token, null, 0,frames);
            if (baseline != null && fallback != null)
            {
                ReverseUvNewCharts.Stabilize(positions, baseline, fallback, density, options.maxAnisotropy, token: token,frames:frames);
                // A different valid rescue placement can change the next LOD's
                // correspondence. Preserve every already successful layout.
                bool valid = true;
                for (int f = 0; f < fallback.Length; ++f)
                {
                    int t = f * 3;
                    bool fits=ReverseUvTransfer.TriangleAnisotropy(ReverseUvNewCharts.Frame(positions,t,frames),
                        baseline[t], baseline[t+1], baseline[t+2]) <= options.maxAnisotropy;
                    // A valid local chart can lose its tiny altitude after a
                    // large Float32 translation. Promote that face to an
                    // intrinsic rescue and retry placement, rather than publish
                    // stretched UVs or reject every new chart in the level.
                    if(!fits) {fallback[f]=true;valid=false;}
                }
                if (valid) return baseline;
            }
            else if (baseline != null) return baseline;
            if(fallback!=null && fallback.Any(f=>f))
                for(int attempt=0;attempt<=fallback.Length;++attempt)
                {
                    var repaired=TryPlace(positions,pixels,previous,current,options,width,height,token,fallback,density,frames);
                    if(repaired==null) break;
                    bool valid=true,extended=false;
                    for(int f=0;f<fallback.Length;++f)
                    {
                        int t=f*3;
                        if(ReverseUvTransfer.TriangleAnisotropy(ReverseUvNewCharts.Frame(positions,t,frames),
                            repaired[t],repaired[t+1],repaired[t+2])<=options.maxAnisotropy) continue;
                        valid=false;extended|=!fallback[f];fallback[f]=true;
                    }
                    if(valid) return repaired;
                    if(!extended) break;
                }
            throw new InvalidOperationException("New reverse UV charts cannot fit within the configured atlas limit.");
        }

        static Vector2[] TryPlace(Vector3[] positions, Vector2[] pixels, List<ReverseUvTransfer.Surface[]> previous,
            ReverseUvTransfer.Surface[] current, ReverseUvTransfer.Options options, float width, float height, CancellationToken token,
            bool[] fallback, float density,(double length,double x,double y)[] frames)
        {
            var islands = Islands(positions, pixels, fallback, density,frames);
            int side = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Math.Max(width, height)));
            for (; side <= options.maxAtlasSize; side *= 2)
            {
                token.ThrowIfCancellationRequested();
                var rows = Enumerable.Range(0, side).Select(_ => new List<(int start, int end)>()).ToArray();
                foreach (var level in previous) Occupy(level, rows, options.padding, false, token);
                Occupy(current, rows, options.padding, true, token);
                var output = new Vector2[pixels.Length]; bool complete = true; long work = 0;
                // Tiny intrinsic altitudes need a low coordinate on their thin
                // axis. Place these independent rescues before ordinary charts.
                foreach (var island in islands.OrderBy(i => i.rescueFace < 0 ? 1 : 0)
                    .ThenBy(i => i.rescueHeight)
                    .ThenByDescending(i => Math.Max(i.max.x - i.min.x, i.max.y - i.min.y))
                    .ThenByDescending(i => (i.max.x - i.min.x) * (i.max.y - i.min.y)))
                {
                    if (island.rescueFace >= 0)
                    {
                        if (!PlaceRescue(island, output, rows, options, density, token, ref work))
                        { complete = false; break; }
                        continue;
                    }
                    int w = Mathf.CeilToInt(island.max.x - island.min.x), h = Mathf.CeilToInt(island.max.y - island.min.y);
                    if (fallback != null) { w = Math.Max(1, w); h = Math.Max(1, h); }
                    w += options.padding * 2; h += options.padding * 2;
                    bool found = Find(rows, w, h, options.padding, token, ref work, out int x, out int y);
                    bool turn = false;
                    if (options.rotateCharts && Find(rows, h, w, options.padding, token, ref work, out int rx, out int ry)
                        && (!found || Math.Max(rx + h, ry + w) < Math.Max(x + w, y + h)))
                    { found = true; turn = true; x = rx; y = ry; (w, h) = (h, w); }
                    if (!found) { complete = false; break; }
                    foreach (int corner in island.corners)
                    {
                        var local = pixels[corner] - island.min;
                        if (turn) local = new Vector2(island.max.y - island.min.y - local.y, local.x);
                        output[corner] = local + new Vector2(x + options.padding, y + options.padding);
                    }
                    for (int r = y; r < y + h; ++r) Reserve(rows[r], x, x + w);
                }
                if (complete) return output;
            }
            return null;
        }

        static bool PlaceRescue(Island island, Vector2[] output,
            List<(int start, int end)>[] rows, ReverseUvTransfer.Options options, float density,
            CancellationToken token, ref long work)
        {
            int innerWidth = Math.Max(1, (int)Math.Ceiling(island.rescueWidth));
            int innerHeight = Math.Max(1, (int)Math.Ceiling(island.rescueHeight));
            for (int turn = 0; turn < 2; ++turn)
            {
                int iw = turn == 0 ? innerWidth : innerHeight, ih = turn == 0 ? innerHeight : innerWidth;
                int w = iw + options.padding * 2, h = ih + options.padding * 2;
                Vector2[] selected = null;
                bool Accept(int x, int y) => ReverseUvNewCharts.TryPlaceTriangle(island.rescueFrame,
                    density, options.maxAnisotropy, new Vector2(x + options.padding, y + options.padding), iw, ih, out selected);
                if (!Find(rows, w, h, options.padding, token, ref work, out int left, out int bottom, Accept)) continue;
                Array.Copy(selected, 0, output, island.rescueFace * 3, 3);
                for (int r = bottom; r < bottom + h; ++r) Reserve(rows[r], left, left + w);
                return true;
            }
            return false;
        }

        static List<Island> Islands(Vector3[] positions, Vector2[] pixels, bool[] fallback, float density,(double length,double x,double y)[] frames)
        {
            var union = new DisjointSet(pixels.Length / 3);
            var edges = new Dictionary<((Vector3, Vector2), (Vector3, Vector2)), int>();
            for (int f = 0; f < pixels.Length / 3; ++f)
            {
                // Float32 collapse must not connect two independent rescues by
                // coincident UV edges. Their topology is deliberately separate.
                if (fallback != null && fallback[f]) continue;
                for (int e = 0; e < 3; ++e)
                {
                    int a = f * 3 + e, b = f * 3 + (e + 1) % 3;
                    var x = (positions[a], pixels[a]); var y = (positions[b], pixels[b]);
                    if (edges.TryGetValue((x, y), out int other) || edges.TryGetValue((y, x), out other)) union.Union(f, other);
                    else edges.Add((x, y), f);
                }
            }
            var grouped = new Dictionary<int, Island>();
            for (int f = 0; f < pixels.Length / 3; ++f)
            {
                int root = union.Find(f);
                if (!grouped.TryGetValue(root, out var island))
                {
                    island = new Island { min = pixels[f * 3], max = pixels[f * 3] }; grouped.Add(root, island);
                    if (fallback != null && fallback[f])
                    {
                        island.rescueFace = f;
                        island.rescueFrame=ReverseUvNewCharts.Frame(positions,f*3,frames);
                        int basis = 0; double longest = 0;
                        for (int k = 0; k < 3; ++k)
                        {
                            var candidate = ReverseUvNewCharts.CyclicFrame(island.rescueFrame,k);
                            if (candidate.length > longest) { longest = candidate.length; basis = k; }
                        }
                        var frame = ReverseUvNewCharts.CyclicFrame(island.rescueFrame,basis);
                        island.rescueWidth = frame.length * density;
                        island.rescueHeight = frame.y * density;
                    }
                }
                for (int k = 0; k < 3; ++k)
                {
                    int corner = f * 3 + k; island.corners.Add(corner);
                    island.min = Vector2.Min(island.min, pixels[corner]); island.max = Vector2.Max(island.max, pixels[corner]);
                }
            }
            return grouped.Values.ToList();
        }

        static bool Find(List<(int start, int end)>[] rows, int width, int height, int border, CancellationToken token,
            ref long work, out int x, out int y, Func<int,int,bool> accept = null)
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
                    if (free)
                    {
                        if (accept == null || accept(x, y)) return true;
                        // All rigid sub-ULP phases were tested in this gap.
                        // Larger coordinates within it cannot improve spacing.
                        nextX = rows.Length;
                        for (int r = y; r < y + height; ++r)
                        {
                            int next = FirstAfter(rows[r], x + width);
                            if (next < rows[r].Count) nextX = Math.Min(nextX, rows[r][next].end);
                        }
                    }
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
