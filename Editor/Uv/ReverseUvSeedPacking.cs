using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Normalize connected seed charts in world metric, then pack rigid
    /// rectangles. All seed charts may move; inherited charts never enter here.</summary>
    internal static class ReverseUvSeedPacking
    {
        sealed class Chart
        {
            internal int[] faces;
            internal Vector2[] local;
            internal float width, height;
            internal Vector2 origin;
        }

        internal static Vector2[] Prepare(Vector3[] positions, Vector2[] uv, float density,
            bool fixedDensity, ReverseUvTransfer.Options options, CancellationToken token, out int side, bool compact = true)
        {
            var fallback = ReverseUvNewCharts.Classify(positions, uv, options, token);
            side = options.seedResolution;
            for (int attempt = 0; attempt < 3; ++attempt)
            {
                var charts = Charts(positions, uv, fallback, density, options, token, compact);
                float scale = 1;
                Vector2[] pixels;
                if (!compact)
                {
                    var oldPixels = ReverseUvNewCharts.Prepare(positions, uv, density,
                        new ReverseUvTransfer.Options {padding=options.padding,maxAnisotropy=options.maxAnisotropy,
                            comparisonBudget=options.comparisonBudget},token,out _);
                    pixels = new Vector2[uv.Length];
                    foreach (var chart in charts)
                    {
                        chart.origin = chart.faces.SelectMany(f=>Enumerable.Range(0,3).Select(k=>oldPixels[f*3+k]))
                            .Aggregate(new Vector2(float.MaxValue,float.MaxValue),Vector2.Min);
                        double world=0,area=0,roundingArea=0;
                        foreach(int f in chart.faces)
                        {
                            int t=f*3;
                            var frame=ReverseUvTransfer.TriangleFrame(positions[t],positions[t+1],positions[t+2]);
                            world+=frame.length*frame.y;
                            var a=oldPixels[t+1]-oldPixels[t];var b=oldPixels[t+2]-oldPixels[t];
                            area+=Math.Abs((double)a.x*b.y-(double)a.y*b.x);
                            double coordinate=Enumerable.Range(0,3).Max(k=>Math.Max(Math.Abs(oldPixels[t+k].x),Math.Abs(oldPixels[t+k].y)));
                            double spacing=Math.Pow(2,Math.Floor(Math.Log(Math.Max(1,coordinate),2))-23);
                            roundingArea+=spacing*2*(a.magnitude+b.magnitude+(oldPixels[t+2]-oldPixels[t+1]).magnitude);
                        }
                        double ratio=density*Math.Sqrt(world/area);
                        // Keep already normalized chart coordinates byte for byte.
                        // Recentring a correct sub-pixel chart introduces fresh
                        // Float32 noise without improving its world density.
                        bool normalized=fallback[chart.faces[0]] || Math.Abs(ratio-1)<=.001
                            || Math.Abs(density*(double)density*world-area)<=roundingArea;
                        for(int i=0;i<chart.local.Length;++i)
                        {
                            int corner=chart.faces[i/3]*3+i%3;
                            pixels[corner]=normalized?oldPixels[corner]:chart.local[i]+chart.origin;
                        }
                    }
                    // Preserve the original canvas when a density correction
                    // shrinks a chart. Refitting it would move every otherwise
                    // unchanged chart and perturb later projection rounding.
                    var min = Vector2.Min(pixels.Aggregate(Vector2.Min),oldPixels.Aggregate(Vector2.Min));
                    var max = Vector2.Max(pixels.Aggregate(Vector2.Max),oldPixels.Aggregate(Vector2.Max));
                    float span = Math.Max(max.x-min.x,max.y-min.y);
                    side = fixedDensity ? Math.Max(16,Mathf.NextPowerOfTwo(Mathf.CeilToInt(span+options.padding*2))) : options.seedResolution;
                    if(side>options.maxAtlasSize) throw new InvalidOperationException("Normalized original seed layout exceeds the atlas limit.");
                    scale = fixedDensity ? 1 : (side-options.padding*2)/span;
                    // Keep the reference median for new-chart allocation.
                    // Per-chart corrections can reorder almost equal samples
                    // and shift the median by one Float32 step.
                    options.seedDensity=ReferenceDensity(positions,oldPixels.Select(p=>(p-min)*scale+Vector2.one*options.padding).ToArray());
                    for(int i=0;i<pixels.Length;++i) pixels[i]=(pixels[i]-min)*scale+Vector2.one*options.padding;
                }
                else if (fixedDensity)
                {
                    double rectangles = charts.Sum(c => (Math.Ceiling(c.width) + options.padding * 2)
                        * (Math.Ceiling(c.height) + options.padding * 2));
                    side = Math.Max(16, Mathf.NextPowerOfTwo(Mathf.CeilToInt((float)Math.Sqrt(rectangles) + options.padding * 2)));
                    while (!TryPack(charts, side, options, scale, token, out pixels))
                    {
                        side *= 2;
                        if (side > options.maxAtlasSize) throw new InvalidOperationException("Reverse seed charts exceed the atlas limit at the requested density.");
                    }
                }
                else
                {
                    // Fixed-size mode uses one global scale after per-chart
                    // normalization. Search the highest density that fits; no
                    // individual chart is stretched or squeezed to fill a gap.
                    float lo = 0, hi = 1;
                    while (TryPack(charts, side, options, hi, token, out _)) hi *= 2;
                    pixels = null;
                    for (int i = 0; i < 18; ++i)
                    {
                        float candidate = (lo + hi) * .5f;
                        if (TryPack(charts, side, options, candidate, token, out var packed))
                        { lo = candidate; pixels = packed; }
                        else hi = candidate;
                    }
                    if (pixels == null) throw new InvalidOperationException("Reverse seed padding cannot fit in the requested atlas.");
                    scale = lo;
                }
                ReverseUvNewCharts.Stabilize(positions, pixels, fallback, density * scale, options.maxAnisotropy, token: token,
                    reserveCollapsedFootprint: true);
                bool retry = false;
                for (int f = 0; f < fallback.Length; ++f)
                {
                    int t = f * 3;
                    double metric = ReverseUvTransfer.TriangleAnisotropy(positions[t], positions[t + 1], positions[t + 2],
                        pixels[t], pixels[t + 1], pixels[t + 2]);
                    if (metric <= options.maxAnisotropy) continue;
                    if (fallback[f] || attempt == 2)
                        throw new InvalidOperationException($"Reverse seed face {f} cannot retain its UV metric at final Float32 placement (anisotropy {metric:G5}).");
                    fallback[f] = true; retry = true;
                }
                if (retry) continue;
                var scan = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=pixels,
                    indices=Enumerable.Range(0,pixels.Length).ToArray(),charts=new int[pixels.Length]},token,
                    comparisonBudget:options.comparisonBudget);
                if(!scan.complete || scan.pairs>0 || scan.degenerateFaces>0)
                    throw new InvalidOperationException("Normalized seed layout contains overlaps or collapsed UV faces.");
                UvtLog.Info(UvtLog.Category.Repack, $"[ReverseUV] Prepared seed: {charts.Length} charts, {fallback.Count(f => f)} repaired faces, {side}², {density * scale:G6} tex/m; {(compact ? "compact" : "original")} packing, rotation {(compact && options.rotateCharts ? "on" : "off")}.");
                return pixels;
            }
            throw new InvalidOperationException("Reverse seed repair did not converge.");
        }

        static Chart[] Charts(Vector3[] positions, Vector2[] uv, bool[] fallback, float density,
            ReverseUvTransfer.Options options, CancellationToken token, bool compact)
        {
            var union = new DisjointSet(fallback.Length);
            var edges = new Dictionary<((Vector3, Vector2), (Vector3, Vector2)), int>();
            for (int f = 0; f < fallback.Length; ++f)
            {
                if (fallback[f]) continue;
                for (int k = 0; k < 3; ++k)
                {
                    int a = f * 3 + k, b = f * 3 + (k + 1) % 3;
                    var x = (positions[a], uv[a]); var y = (positions[b], uv[b]);
                    if (edges.TryGetValue((x, y), out int other) || edges.TryGetValue((y, x), out other)) union.Union(f, other);
                    else edges.Add((x, y), f);
                }
            }
            var charts = new List<Chart>();
            foreach (var group in Enumerable.Range(0, fallback.Length).GroupBy(union.Find))
            {
                token.ThrowIfCancellationRequested();
                var faces = group.ToArray();
                var local = new Vector2[faces.Length * 3];
                if (fallback[faces[0]])
                {
                    int t = faces[0] * 3;
                    var frame = ReverseUvTransfer.TriangleFrame(positions[t], positions[t + 1], positions[t + 2]);
                    local[1] = new Vector2((float)(frame.length * density), 0);
                    local[2] = new Vector2((float)(frame.x * density), (float)(frame.y * density));
                }
                else
                {
                    double world = 0, area = 0;
                    var origin = uv[faces[0] * 3];
                    for (int i = 0; i < faces.Length; ++i)
                    {
                        int t = faces[i] * 3;
                        var frame = ReverseUvTransfer.TriangleFrame(positions[t], positions[t + 1], positions[t + 2]);
                        world += frame.length * frame.y;
                        var a = uv[t + 1] - uv[t]; var b = uv[t + 2] - uv[t];
                        area += Math.Abs((double)a.x * b.y - (double)a.y * b.x);
                        for (int k = 0; k < 3; ++k) local[i * 3 + k] = uv[t + k] - origin;
                    }
                    double scale = density * Math.Sqrt(world / area);
                    for (int i = 0; i < local.Length; ++i)
                        local[i] = new Vector2((float)(local[i].x * scale), (float)(local[i].y * scale));
                }
                AddPieces(charts, positions, faces, local, fallback[faces[0]], options, token, compact);
            }
            return charts.OrderByDescending(c => Math.Max(c.width, c.height))
                .ThenByDescending(c => (double)c.width * c.height).ThenBy(c => c.faces[0]).ToArray();
        }

        static float ReferenceDensity(Vector3[] positions,Vector2[] pixels)
        {
            var samples=new List<double>();
            for(int t=0;t<positions.Length;t+=3)
            {
                double world=Vector3.Cross(positions[t+1]-positions[t],positions[t+2]-positions[t]).magnitude;
                var a=pixels[t+1]-pixels[t];var b=pixels[t+2]-pixels[t];
                samples.Add(Math.Sqrt(Math.Abs((double)a.x*b.y-(double)a.y*b.x)/world));
            }
            samples.Sort();
            float value=(float)samples[samples.Count/2];
            return float.IsNaN(value)||float.IsInfinity(value)?0:value;
        }

        static void AddPieces(List<Chart> charts, Vector3[] positions, int[] faces, Vector2[] pixels,
            bool rescue, ReverseUvTransfer.Options options, CancellationToken token, bool compact)
        {
            var indices = new int[pixels.Length]; var unique = new Dictionary<(Vector3, Vector2), int>();
            var coordinates = new List<Vector2>();
            for (int i = 0; i < indices.Length; ++i)
            {
                var key = (positions[faces[i / 3] * 3 + i % 3], pixels[i]);
                if (!unique.TryGetValue(key, out int v)) { v = coordinates.Count; unique.Add(key, v); coordinates.Add(pixels[i]); }
                indices[i] = v;
            }
            var uv = coordinates.ToArray();
            var regions = options.cutNarrowJunctions && !rescue ? UvJunctionCuts.Find(uv, indices, token).regions : new int[faces.Length];
            foreach (var group in Enumerable.Range(0, faces.Length).GroupBy(f => regions[f]))
            {
                var part = group.ToArray();
                // Rescue triangles keep their intrinsic basis. Arbitrary angle
                // rotation can destroy a sub-ULP altitude before stabilization.
                var box = compact && options.rotateCharts && options.rotateChartsToAxis && !rescue
                    ? UvJunctionCuts.BestBox(uv, indices, part, token) : UvJunctionCuts.AxisBox(uv, indices, part);
                charts.Add(new Chart { faces = part.Select(f => faces[f]).ToArray(),
                    local = part.SelectMany(f => Enumerable.Range(0, 3).Select(k => box.Project(pixels[f * 3 + k]) - box.min)).ToArray(),
                    width = box.max.x - box.min.x, height = box.max.y - box.min.y });
            }
        }

        static bool TryPack(Chart[] charts, int side, ReverseUvTransfer.Options options, float scale,
            CancellationToken token, out Vector2[] output)
        {
            output = new Vector2[charts.Sum(c => c.local.Length)];
            if (side > options.maxAtlasSize || side <= options.padding * 2) return false;
            var free = new List<RectInt> { new RectInt(options.padding, options.padding, side - options.padding * 2, side - options.padding * 2) };
            long work = 0;
            foreach (var chart in charts)
            {
                token.ThrowIfCancellationRequested();
                int width = Mathf.CeilToInt(chart.width * scale) + options.padding * 2;
                int height = Mathf.CeilToInt(chart.height * scale) + options.padding * 2;
                int best = int.MaxValue, waste = int.MaxValue; RectInt selected = default; bool turn = false, found = false;
                foreach (var rectangle in free)
                    for (int rotation = 0; rotation < (options.rotateCharts ? 2 : 1); ++rotation)
                    {
                        if (++work > 16000000) throw new InvalidOperationException("Reverse seed rectangle packing exceeded its work budget.");
                        int w = rotation == 0 ? width : height, h = rotation == 0 ? height : width;
                        if (w > rectangle.width || h > rectangle.height) continue;
                        int score = Math.Min(rectangle.width - w, rectangle.height - h);
                        int other = Math.Max(rectangle.width - w, rectangle.height - h);
                        if (found && (score > best || (score == best && other >= waste))) continue;
                        best = score; waste = other; selected = new RectInt(rectangle.x, rectangle.y, w, h); turn = rotation != 0; found = true;
                    }
                if (!found) { output = null; return false; }
                for (int i = 0; i < chart.local.Length; ++i)
                {
                    var p = chart.local[i] * scale;
                    if (turn) p = new Vector2(chart.height * scale - p.y, p.x);
                    output[chart.faces[i / 3] * 3 + i % 3] = p + new Vector2(selected.x + options.padding, selected.y + options.padding);
                }
                SplitFree(free, selected, ref work);
            }
            return true;
        }

        static void SplitFree(List<RectInt> free, RectInt used, ref long work)
        {
            var added = new List<RectInt>();
            for (int i = free.Count - 1; i >= 0; --i)
            {
                var r = free[i];
                if (!r.Overlaps(used)) continue;
                free.RemoveAt(i);
                if (used.xMin > r.xMin) added.Add(new RectInt(r.xMin, r.yMin, used.xMin - r.xMin, r.height));
                if (used.xMax < r.xMax) added.Add(new RectInt(used.xMax, r.yMin, r.xMax - used.xMax, r.height));
                if (used.yMin > r.yMin) added.Add(new RectInt(r.xMin, r.yMin, r.width, used.yMin - r.yMin));
                if (used.yMax < r.yMax) added.Add(new RectInt(r.xMin, used.yMax, r.width, r.yMax - used.yMax));
            }
            // Existing free rectangles are already pruned. Only new fragments
            // can be contained after this placement; avoid rescanning old pairs
            // for every chart in a large fragmented coarsest LOD.
            int firstAdded=free.Count;
            free.AddRange(added);
            for (int i = free.Count - 1; i >= firstAdded; --i)
                for (int j = 0; j < free.Count; ++j)
                {
                    if (++work > 16000000) throw new InvalidOperationException("Reverse seed rectangle packing exceeded its work budget.");
                    if (i == j) continue;
                    var a = free[i]; var b = free[j];
                    if (a.xMin < b.xMin || a.yMin < b.yMin || a.xMax > b.xMax || a.yMax > b.yMax) continue;
                    free.RemoveAt(i); break;
                }
        }
    }
}
