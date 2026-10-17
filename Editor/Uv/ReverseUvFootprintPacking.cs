using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Pack the union of every descendant triangle in a family. Empty
    /// space inside a ring or branch is available to unrelated padded families;
    /// overlays never become independent packing items.</summary>
    internal static class ReverseUvFootprintPacking
    {
        internal sealed class Diagnostics
        {
            internal long tighteningWork, repackingWork, validations;
            internal int tightenedFamilies, completedOrders, retainedFamilies;
            internal bool nativePacked;
            internal List<string> refusedOrders = new List<string>();
        }

        sealed class Mask
        {
            internal int id, width, height;
            internal Vector2[] source;
            internal Vector2 minimum;
            internal double sourceWidth, sourceHeight, density;
            internal int turn;
            internal float unit = 1;

            internal Vector2[] Place(int left, int bottom, float phase)
            {
                var result = new Vector2[source.Length];
                for (int i = 0; i < result.Length; ++i)
                {
                    double x = (double)source[i].x - minimum.x, y = (double)source[i].y - minimum.y, old = x;
                    if (turn == 1)
                    {
                        x = sourceHeight - y;
                        y = old;
                    }
                    else if (turn == 2)
                    {
                        x = sourceWidth - x;
                        y = sourceHeight - y;
                    }
                    else if (turn == 3)
                    {
                        x = y;
                        y = sourceWidth - old;
                    }
                    result[i] = new Vector2((float)((x * density + left + phase + padding) * unit),
                        (float)((y * density + bottom + phase + padding) * unit));
                }
                return result;
            }
            internal int padding;
            internal List<(int start, int end)>[] rows;
            (int row, int start, int end)[] compiledSpans;
            internal (int row, int start, int end)[] CompileSpans()
                => compiledSpans ??= rows.SelectMany((spans, row) => spans.Select(span => (row, span.start, span.end))).ToArray();
        }

        sealed class SearchProfile : IDisposable
        {
            readonly TransferPerformanceProfile profile;
            internal long searchTicks, searches, auditTicks, audits, rowIndexTicks, rowIndexes, nativeSetupTicks, nativeSearches;
            internal SearchProfile(TransferPerformanceProfile profile)
            {
                this.profile = profile;
            }
            public void Dispose()
            {
                profile.Record("Footprint candidate search", searchTicks, searches);
                profile.Record("Footprint placement audits", auditTicks, audits);
                profile.Record("Footprint row index construction", rowIndexTicks, rowIndexes);
                profile.Record("Native footprint search snapshots", nativeSetupTicks, nativeSearches);
            }
        }

        struct RowCursor
        {
            internal int row, threshold, index;
        }

        // Occupancy stays immutable inside Find. Label integer x positions by
        // the first colliding mask span, then by its first occupied interval.
        // Unassigned cells resume after the fully indexed prefix. A bounded
        // partial final span is safe: assigned cells have their earliest hit;
        // unassigned cells still test that span in the original traversal.
        internal sealed class CollisionRowIndex
        {
            readonly int[] visits, jumps, unassigned;
            int minimum, length;
            internal int completeSpans;
            internal CollisionRowIndex(int maxX)
            {
                visits = new int[maxX + 1];
                jumps = new int[maxX + 1];
                unassigned = new int[maxX + 2];
            }

            internal void Build((int row, int start, int end)[] spans, List<(int start, int end)>[] rows,
                int y, int minX, int maxX, CancellationToken token, int effortLimit = 4096, int spanLimit = 4096)
            {
                token.ThrowIfCancellationRequested();
                minimum = minX;
                length = maxX - minX + 1;
                completeSpans = 0;
                Array.Clear(visits, 0, length);
                for (int i = 0; i <= length; ++i)
                    unassigned[i] = i;
                int effort = 0;
                for (int s = 0; s < Math.Min(spans.Length, spanLimit); ++s)
                {
                    token.ThrowIfCancellationRequested();
                    var span = spans[s];
                    var occupied = rows[y + span.row];
                    int first = FirstAfter(occupied, minX + span.start);
                    for (int i = first; i < occupied.Count && occupied[i].start < maxX + span.end; ++i)
                    {
                        if (++effort > effortLimit)
                            return;
                        int lo = Math.Max(minX, occupied[i].start - span.end + 1) - minX;
                        int hi = Math.Min(maxX, occupied[i].end - span.start - 1) - minX;
                        if (lo > hi)
                            continue;
                        int cell = NextUnassigned(lo);
                        while (cell <= hi)
                        {
                            if (++effort > effortLimit)
                                return;
                            if ((effort & 255) == 0)
                                token.ThrowIfCancellationRequested();
                            visits[cell] = s + 1;
                            jumps[cell] = occupied[i].end - span.start;
                            unassigned[cell] = NextUnassigned(cell + 1);
                            cell = unassigned[cell];
                        }
                        if (NextUnassigned(0) == length)
                        {
                            completeSpans = s + 1;
                            return;
                        }
                    }
                    completeSpans = s + 1;
                }
            }

            int NextUnassigned(int cell)
            {
                int root = cell;
                while (unassigned[root] != root)
                    root = unassigned[root];
                while (unassigned[cell] != cell)
                {
                    int next = unassigned[cell];
                    unassigned[cell] = root;
                    cell = next;
                }
                return root;
            }

            internal bool TryCollision(int x, out int next, out int checkedSpans)
            {
                int cell = x - minimum;
                checkedSpans = cell >= 0 && cell < length ? visits[cell] : 0;
                next = checkedSpans == 0 ? 0 : Math.Max(x + 1, jumps[cell]);
                return checkedSpans > 0;
            }
        }

        internal static bool ConsumeSearchWork(int visits, ref long work)
        {
            if (visits == 0)
                return true;
            // Stop after the first over-budget span, as in the reference.
            if (work >= 128000000)
            {
                ++work;
                return false;
            }
            work += Math.Min(visits, 128000001 - work);
            return work <= 128000000;
        }

        internal static Vector2[][] Improve(Vector2[][] source, Vector2[][] baseline, int side, float scale,
            ReverseUvTransfer.Options options, CancellationToken token, Func<int, Vector2[], int, bool> validate,
            out float finalScale, out bool improved, Diagnostics diagnostics = null, Action<int> preparedSide = null, bool testNative = true)
        {
            using var profile = options.performance == null ? null : new SearchProfile(options.performance);
            finalScale = scale;
            improved = false;
            Mask[][] Masks(float density) => PackingMasks(source, options, density, token);
            var masks = Masks(scale);
            var best = baseline;
            float bestScale = scale;
            double bestCost = BoundsArea(best) / (scale * (double)scale);
            long work = 0, validations = 0;
            var tightened = Tighten(baseline, masks, side, options, token, validate, ref work, ref validations, profile);
            if (diagnostics != null)
            {
                diagnostics.tighteningWork = work;
                diagnostics.tightenedFamilies = tightened == null ? 0 : Enumerable.Range(0, baseline.Length)
                    .Count(g => !baseline[g].SequenceEqual(tightened[g]));
            }
            if (tightened != null && BoundsArea(tightened) / (scale * (double)scale) < bestCost)
            {
                best = tightened;
                bestCost = BoundsArea(best) / (scale * (double)scale);
                improved = true;
            }
            bool nativePacked = false;
            if (testNative && source.Length > 256 && options.fillFinalAtlas)
            {
                var native = ReverseUvNativeFamilyPacking.TryPack(source, side, options, token, validate, out float nativeScale, out int nativeSide,
                    allowLargerCanvas: preparedSide != null);
                if (native != null)
                    UvtLog.Info(UvtLog.Category.Repack, $"[ReverseUV] Native family bbox cost {BoundsArea(native) / (nativeScale * (double)nativeScale):G6}; retained cost {bestCost:G6}; scale {nativeScale:G6}.");
                if (native != null && BoundsArea(native) / (nativeScale * (double)nativeScale) < bestCost)
                {
                    best = native;
                    bestScale = nativeScale;
                    bestCost = BoundsArea(native) / (nativeScale * (double)nativeScale);
                    improved = true;
                    nativePacked = true;
                    side = nativeSide;
                }
            }
            if (diagnostics != null && nativePacked)
            {
                diagnostics.nativePacked = true;
                ++diagnostics.completedOrders;
            }
            // A rectangle capacity scale can leave no room for conservative
            // triangle masks. Repack also at requested density, then uniformly
            // fill the completed placement. Compare bbox cost per unit density;
            // merely shrinking islands cannot win this comparison.
            bool completedFootprints = nativePacked;
            if (source.Length > 256 && side > 1024 && options.fillFinalAtlas)
            {
                float cell = side / 1024f;
                var rasterOptions = new ReverseUvTransfer.Options
                {
                    padding = (int)Math.Ceiling(options.padding / cell),
                    rotateCharts = options.rotateCharts,
                    performance = options.performance,
                    indexFootprintSearch = options.indexFootprintSearch,
                    cacheCollisionRows = options.cacheCollisionRows,
                    nativeFootprintSearch = options.nativeFootprintSearch,
                    footprintSearchApi = options.footprintSearchApi,
                    reuseOccupancySnapshots = options.reuseOccupancySnapshots,
                    burstFootprintRaster = options.burstFootprintRaster
                };
                foreach (float candidateScale in new[] { scale, scale * .85f, scale * .65f })
                {
                    var coarse = source.Select((points, id) => Enumerable.Range(0, options.rotateCharts ? 4 : 1)
                        .Select(turn => Raster(points, id, turn, candidateScale / cell, rasterOptions.padding, token, cell,
                            options.burstFootprintRaster, options.performance)).ToArray()).ToArray();
                    // Work in a bounded occupancy grid; actual output corners
                    // are always rebuilt from the originals in double precision.
                    var order = coarse.OrderBy(m => ReverseUvFamilyPacking.MinimumAltitude(source[m[0].id]) < .001 ? 0 : 1)
                        .ThenBy(m => ReverseUvFamilyPacking.MinimumAltitude(source[m[0].id]) < .001
                            ? ReverseUvFamilyPacking.MinimumAltitude(source[m[0].id]) : 0)
                        .ThenByDescending(m => Math.Max(m[0].width, m[0].height)).ThenBy(m => m[0].id).ToArray();
                    long packingWork = 0, packingValidations = 0;
                    var packed = TryPack(order, 1024, rasterOptions, token, validate, ref packingWork, ref packingValidations, diagnostics, profile: profile);
                    work += packingWork;
                    validations += packingValidations;
                    if (packed == null)
                        continue;
                    if (diagnostics != null)
                        ++diagnostics.completedOrders;
                    double cost = BoundsArea(packed) / (candidateScale * (double)candidateScale);
                    UvtLog.Info(UvtLog.Category.Repack, $"[ReverseUV] Coarse-grid family cost {cost:G6}; retained {bestCost:G6}.");
                    if (cost >= bestCost)
                        continue;
                    best = packed;
                    bestScale = candidateScale;
                    bestCost = cost;
                    improved = true;
                    completedFootprints = true;
                    if (diagnostics != null)
                        diagnostics.nativePacked = false;
                }
            }
            var scales = completedFootprints ? Array.Empty<float>() : options.fillFinalAtlas && scale > 1
                ? new[] { scale, 1f } : new[] { scale };
            foreach (float packingScale in scales.Distinct())
            {
                bool usesBaselineScale = packingScale.CompareTo(scale) == 0;
                var variants = usesBaselineScale ? masks : Masks(packingScale);
                foreach (var order in PackingOrders(source, variants).GroupBy(o => string.Join(",", o.Select(m => m[0].id))).Select(g => g.First()))
                {
                    token.ThrowIfCancellationRequested();
                    // Every order is an independent bounded search.
                    long packingWork = 0, packingValidations = 0;
                    var packed = TryPack(order, side, options, token, validate, ref packingWork, ref packingValidations, diagnostics, usesBaselineScale ? baseline : null, profile);
                    work += packingWork;
                    validations += packingValidations;
                    if (packed == null)
                        continue;
                    if (diagnostics != null)
                        ++diagnostics.completedOrders;
                    double cost = BoundsArea(packed) / (packingScale * (double)packingScale);
                    if (cost >= bestCost)
                        continue;
                    bestCost = cost;
                    best = packed;
                    bestScale = packingScale;
                    improved = true;
                }
            }
            if (diagnostics != null)
            {
                diagnostics.repackingWork = work - diagnostics.tighteningWork;
                diagnostics.validations = validations;
            }
            finalScale = bestScale;
            preparedSide?.Invoke(side);
            if (!improved || !options.fillFinalAtlas)
                return best;
            var pointsAll = best.SelectMany(p => p).ToArray();
            var min = pointsAll.Aggregate(Vector2.Min);
            var max = pointsAll.Aggregate(Vector2.Max);
            double fit = (side - options.padding * 2) / Math.Max((double)max.x - min.x, (double)max.y - min.y);
            if (fit <= 1)
                return best;
            var filled = best.Select(points => points.Select(p => new Vector2((float)(((double)p.x - min.x) * fit + options.padding),
                (float)(((double)p.y - min.y) * fit + options.padding))).ToArray()).ToArray();
            for (int group = 0; group < filled.Length; ++group)
                if (validate != null && !validate(group, filled[group], side))
                    return best;
            finalScale = (float)(bestScale * fit);
            return filled;
        }

        // Reuse the existing conservative masks and bounded search at a fixed
        // density. Never enlarge the supplied canvas or scale families to fit.
        internal static bool TryPackWithin(Vector2[][] source,int side,float scale,ReverseUvTransfer.Options options,
            CancellationToken token,Func<int,Vector2[],int,bool> validate,out Vector2[][] placement,out int packedSide)
        {
            token.ThrowIfCancellationRequested();
            placement=null;packedSide=side;
            if((long)side*side>16777216) return false;
            var masks=PackingMasks(source,options,scale,token);
            long work=0,validations=0;
            foreach(var order in PackingOrders(source,masks).SelectMany(PackingStarts))
            {
                token.ThrowIfCancellationRequested();
                var candidate=TryPack(order,side,options,token,validate,ref work,ref validations,null);
                if(candidate==null) continue;
                int extent=Math.Max(16,(int)Math.Ceiling(candidate.SelectMany(p=>p).Max(p=>Math.Max(p.x,p.y))+options.padding));
                if(extent>packedSide || !FitsCanvas(candidate,extent,validate)) continue;
                placement=candidate;packedSide=extent;
            }
            return placement!=null;
        }

        // Locally equal square extents can hide very different free regions.
        // Try each permitted first-family quarter-turn, sharing the same work
        // and validation counters across every order. No larger canvas or scale.
        static IEnumerable<Mask[][]> PackingStarts(Mask[][] order)
        {
            if(order.Length==0) yield break;
            for(int start=0;start<order[0].Length;++start)
            {
                var trial=(Mask[][])order.Clone();
                trial[0]=order[0].Skip(start).Concat(order[0].Take(start)).ToArray();
                yield return trial;
            }
        }

        static bool FitsCanvas(Vector2[][] candidate,int side,Func<int,Vector2[],int,bool> validate)
        {
            for(int g=0;g<candidate.Length;++g)
                if(validate!=null && !validate(g,candidate[g],side)) return false;
            return true;
        }

        // Used only when rectangle reservations fail. There is no projection
        // layout to pin here: each order places every family on an empty canvas.
        internal static Vector2[][] PackFresh(Vector2[][] source, int initialSide, ReverseUvTransfer.Options options,
            CancellationToken token, Func<int, Vector2[], int, bool> validate, out int side)
        {
            using var profile = options.performance == null ? null : new SearchProfile(options.performance);
            var masks = PackingMasks(source, options, 1, token);
            var orders = PackingOrders(source, masks);
            for (side = Math.Max(16, initialSide); side <= options.maxAtlasSize; side *= 2)
                foreach (var order in orders)
                {
                    token.ThrowIfCancellationRequested();
                    long work = 0, validations = 0;
                    var packed = TryPack(order, side, options, token, validate, ref work, ref validations, null, profile: profile);
                    if (packed != null)
                        return packed;
                }
            throw new InvalidOperationException("Complete family footprints cannot be packed on an empty canvas within the atlas limit.");
        }

        static Mask[][] PackingMasks(Vector2[][] source, ReverseUvTransfer.Options options, float density, CancellationToken token)
        {
            using var timing = options.performance?.Measure("Footprint raster masks");
            return source.Select((points, id) => Enumerable.Range(0, options.rotateCharts ? 4 : 1)
                .Select(turn => Raster(points, id, turn, density, options.padding, token, burst: options.burstFootprintRaster,
                    performance: options.performance)).ToArray()).ToArray();
        }

        static Mask[][][] PackingOrders(Vector2[][] source, Mask[][] variants) => new[] {
                variants.OrderBy(m=>ReverseUvFamilyPacking.MinimumAltitude(source[m[0].id])<.0001?0:1)
                    .ThenBy(m=>ReverseUvFamilyPacking.MinimumAltitude(source[m[0].id])<.0001
                        ?ReverseUvFamilyPacking.MinimumAltitude(source[m[0].id]):0)
                    .ThenByDescending(m=>Math.Max(m[0].width,m[0].height)).ThenBy(m=>m[0].id).ToArray(),
                variants.OrderByDescending(m=>Math.Max(m[0].width,m[0].height)).ThenBy(m=>m[0].id).ToArray(),
                variants.OrderByDescending((Mask[] m)=>MaskArea(m[0])).ThenBy(m=>m[0].id).ToArray()
            };

        static int MaskArea(Mask mask)
        {
            int area = 0;
            foreach (var row in mask.rows)
                foreach (var span in row)
                    area += span.end - span.start;
            return area;
        }

        // Start from the already validated family placement. A failed move of
        // one thin family must not discard the compaction of every other family.
        // Counts permit removing one footprint even when conservative gutters
        // of two valid baseline families share raster cells.
        static Vector2[][] Tighten(Vector2[][] baseline, Mask[][] variants, int side, ReverseUvTransfer.Options options,
            CancellationToken token, Func<int, Vector2[], int, bool> validate, ref long work, ref long validations, SearchProfile profile)
        {
            using var timing = options.performance?.Measure("Footprint tightening");
            if ((long)side * side > 16777216)
                return null;
            var counts = new int[side * side];
            long rasterWork = 0;
            var rows = Enumerable.Range(0, side).Select(_ => new List<(int start, int end)>()).ToArray();
            var output = baseline.ToArray();
            var placed = new (Mask mask, int x, int y)[baseline.Length];
            for (int id = 0; id < baseline.Length; ++id)
            {
                token.ThrowIfCancellationRequested();
                var min = baseline[id].Aggregate(Vector2.Min);
                placed[id] = (Raster(baseline[id], id, 0, 1, options.padding, token, burst: options.burstFootprintRaster, performance: options.performance),
                    (int)Math.Floor(min.x) - options.padding, (int)Math.Floor(min.y) - options.padding);
                Occupy(placed[id], 1, false);
                if (work + rasterWork > 128000000)
                {
                    work += rasterWork;
                    return null;
                }
            }
            for (int y = 0; y < side; ++y)
                Refresh(y);
            var order = Enumerable.Range(0, baseline.Length).OrderByDescending(id => baseline[id].Max(p => Math.Max(p.x, p.y)))
                .ThenByDescending(id => baseline[id].Max(p => p.x) * baseline[id].Max(p => p.y)).ThenBy(id => id).ToArray();
            var globalMax = baseline.SelectMany(p => p).Aggregate(Vector2.Max);
            foreach (int id in order)
            {
                token.ThrowIfCancellationRequested();
                if (work + rasterWork > 128000000)
                    break;
                var old = placed[id];
                Occupy(old, -1, true);
                var previousMax = output[id].Aggregate(Vector2.Max);
                float bestScore = Math.Max(previousMax.x, previousMax.y);
                Vector2[] selected = null;
                bool Valid(int group, Vector2[] points, int atlas)
                {
                    var max = points.Aggregate(Vector2.Max);
                    return max.x <= globalMax.x && max.y <= globalMax.y && Math.Max(max.x, max.y) < bestScore
                        && (validate == null || validate(group, points, atlas));
                }
                using var snapshot = PrepareSnapshot(variants[id], rows, options, token);
                foreach (var mask in variants[id])
                {
                    work += rasterWork;
                    rasterWork = 0;
                    if (!Find(mask, rows, options, token, Valid, ref work, ref validations,
                        Math.Max(8192, baseline.Length * 8L), out _, out _, out var points, profile: profile, snapshot: snapshot))
                        continue;
                    var max = points.Aggregate(Vector2.Max);
                    bestScore = Math.Max(max.x, max.y);
                    selected = points;
                }
                if (selected != null)
                {
                    output[id] = selected;
                    var min = selected.Aggregate(Vector2.Min);
                    placed[id] = (Raster(selected, id, 0, 1, options.padding, token, burst: options.burstFootprintRaster, performance: options.performance),
                        (int)Math.Floor(min.x) - options.padding, (int)Math.Floor(min.y) - options.padding);
                }
                Occupy(placed[id], 1, true);
            }
            work += rasterWork;
            return output;

            void Occupy((Mask mask, int x, int y) placement, int delta, bool refresh)
            {
                for (int r = 0; r < placement.mask.height; ++r)
                {
                    int y = placement.y + r;
                    if (y < 0 || y >= side)
                        continue;
                    foreach (var span in placement.mask.rows[r])
                        for (int x = Math.Max(0, placement.x + span.start); x < Math.Min(side, placement.x + span.end); ++x)
                        {
                            counts[y * side + x] += delta;
                            ++rasterWork;
                        }
                    if (refresh)
                        Refresh(y);
                }
            }
            void Refresh(int y)
            {
                var spans = rows[y];
                spans.Clear();
                int start = -1;
                for (int x = 0; x <= side; ++x)
                {
                    bool occupied = x < side && counts[y * side + x] > 0;
                    if (occupied && start < 0)
                        start = x;
                    else if (!occupied && start >= 0)
                    {
                        spans.Add((start, x));
                        start = -1;
                    }
                }
                rasterWork += side;
            }
        }

        static double BoundsArea(Vector2[][] coordinates)
        {
            var points = coordinates.SelectMany(p => p).ToArray();
            var min = points.Aggregate(Vector2.Min);
            var max = points.Aggregate(Vector2.Max);
            return ((double)max.x - min.x) * ((double)max.y - min.y);
        }

        static Mask Raster(Vector2[] points, int id, int turn, double scale, int padding, CancellationToken token, float unit = 1,
            bool burst = true, TransferPerformanceProfile performance = null)
        {
            var min = points.Aggregate(Vector2.Min);
            var max = points.Aggregate(Vector2.Max);
            double width = (double)max.x - min.x, height = (double)max.y - min.y;
            var local = points.Select(p =>
            {
                double x = (double)p.x - min.x, y = (double)p.y - min.y, old = x;
                if (turn == 1)
                {
                    x = height - y;
                    y = old;
                }
                else if (turn == 2)
                {
                    x = width - x;
                    y = height - y;
                }
                else if (turn == 3)
                {
                    x = y;
                    y = width - old;
                }
                return new Vector2((float)(x * scale + padding), (float)(y * scale + padding));
            }).ToArray();
            int w = (int)Math.Ceiling((turn % 2 == 0 ? width : height) * scale) + padding * 2 + 2;
            int h = (int)Math.Ceiling((turn % 2 == 0 ? height : width) * scale) + padding * 2 + 2;
            var rows = Enumerable.Range(0, h).Select(_ => new List<(int start, int end)>()).ToArray();
            // Each family contributes half the requested gutter. One additional
            // texel conservatively contains subpixel placement phases and edges.
            double radius = padding * .5 + 1;
            long rasterStarted = performance == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            if (burst && local.Length >= 96)
                performance?.Accumulate("Burst raster qualifying triangles", 0, local.Length / 3);
            var bulk = burst ? ReverseUvRasterAcceleration.TryRasterize(local, w, h, radius, token) : null;
            if (bulk != null)
            {
                foreach (var span in bulk.spans)
                {
                    token.ThrowIfCancellationRequested();
                    Reserve(rows[span.row], span.start, span.end);
                }
                performance?.Accumulate("Burst footprint raster spans", System.Diagnostics.Stopwatch.GetTimestamp() - rasterStarted, local.Length / 3);
            }
            else
            {
                for (int t = 0; t < local.Length; t += 3)
                {
                    token.ThrowIfCancellationRequested();
                    var triangle = new[] { local[t], local[t + 1], local[t + 2] };
                    int lo = Math.Max(0, (int)Math.Floor(triangle.Min(p => p.y) - radius));
                    int hi = Math.Min(h, (int)Math.Ceiling(triangle.Max(p => p.y) + radius));
                    for (int y = lo; y < hi; ++y)
                    {
                        double bottom = y - radius, top = y + 1 + radius, left = double.MaxValue, right = double.MinValue;
                        foreach (var p in triangle)
                        {
                            if (p.y >= bottom && p.y <= top)
                            {
                                left = Math.Min(left, p.x);
                                right = Math.Max(right, p.x);
                            }
                        }
                        for (int e = 0; e < 3; ++e)
                        {
                            var a = triangle[e];
                            var b = triangle[(e + 1) % 3];
                            double rise = (double)b.y - a.y;
                            if (rise == 0)
                                continue;
                            foreach (double v in new[] { bottom, top })
                            {
                                double fraction = (v - a.y) / rise;
                                if (fraction < 0 || fraction > 1)
                                    continue;
                                double x = a.x + ((double)b.x - a.x) * fraction;
                                left = Math.Min(left, x);
                                right = Math.Max(right, x);
                            }
                        }
                        if (left <= right)
                            Reserve(rows[y], Math.Max(0, (int)Math.Floor(left - radius)), Math.Min(w, (int)Math.Ceiling(right + radius)));
                    }
                }
            }
            return new Mask
            {
                id = id,
                width = w,
                height = h,
                rows = rows,
                source = points,
                minimum = min,
                sourceWidth = width,
                sourceHeight = height,
                turn = turn,
                density = scale,
                padding = padding,
                unit = unit
            };
        }

        static Vector2[][] TryPack(Mask[][] order, int side, ReverseUvTransfer.Options options, CancellationToken token,
            Func<int, Vector2[], int, bool> validate, ref long work, ref long validations, Diagnostics diagnostics, Vector2[][] baseline = null, SearchProfile profile = null)
        {
            using var timing = options.performance?.Measure("Footprint packing order");
            var retained = new HashSet<int>();
            for (int attempt = 0; attempt <= Math.Min(64, order.Length); ++attempt)
            {
                var rows = Enumerable.Range(0, side).Select(_ => new List<(int start, int end)>()).ToArray();
                var output = new Vector2[order.Length][];
                foreach (int id in retained)
                {
                    var points = baseline[id];
                    var min = points.Aggregate(Vector2.Min);
                    var mask = Raster(points, id, 0, 1, options.padding, token, burst: options.burstFootprintRaster, performance: options.performance);
                    int x = (int)Math.Floor(min.x) - options.padding, y = (int)Math.Floor(min.y) - options.padding;
                    output[id] = points;
                    for (int r = 0; r < mask.height; ++r)
                        if (y + r >= 0 && y + r < side)
                            foreach (var span in mask.rows[r])
                                Reserve(rows[y + r], Math.Max(0, x + span.start), Math.Min(side, x + span.end));
                }
                int failed = -1;
                foreach (var variants in order)
                {
                    token.ThrowIfCancellationRequested();
                    if (retained.Contains(variants[0].id))
                        continue;
                    Mask selected = null;
                    Vector2[] placed = null;
                    int left = 0, bottom = 0, best = int.MaxValue;
                    using var snapshot = PrepareSnapshot(variants, rows, options, token);
                    foreach (var mask in variants)
                    {
                        if (!Find(mask, rows, options, token, validate, ref work, ref validations,
                            Math.Max(8192, order.Length * 8L), out int x, out int y, out var points, optimizeExtent: order.Length <= 256,
                            profile: profile, snapshot: snapshot))
                            continue;
                        int score = Math.Max(x + mask.width, y + mask.height);
                        if (score >= best)
                            continue;
                        selected = mask;
                        placed = points;
                        left = x;
                        bottom = y;
                        best = score;
                    }
                    if (selected == null)
                    {
                        failed = variants[0].id;
                        break;
                    }
                    output[selected.id] = placed;
                    for (int r = 0; r < selected.height; ++r)
                        foreach (var span in selected.rows[r])
                            Reserve(rows[bottom + r], left + span.start, left + span.end);
                }
                if (failed < 0)
                {
                    if (diagnostics != null)
                        diagnostics.retainedFamilies = Math.Max(diagnostics.retainedFamilies, retained.Count);
                    return output;
                }
                // One precision-sensitive family must not cancel a complete
                // placement. Reserve its audited baseline and repack the rest.
                // Keep the same common scale and stop on exhausted work budgets.
                if (baseline == null || work >= 128000000 || validations >= Math.Max(8192, order.Length * 8L)
                    || (validate != null && !validate(failed, baseline[failed], side)) || !retained.Add(failed))
                {
                    diagnostics?.refusedOrders.Add($"family {failed}, retained {retained.Count}, work {work}, validations {validations}");
                    return null;
                }
            }
            diagnostics?.refusedOrders.Add($"local retention limit, retained {retained.Count}, work {work}, validations {validations}");
            return null;
        }

        static bool Find(Mask mask, List<(int start, int end)>[] rows, ReverseUvTransfer.Options options, CancellationToken token,
            Func<int, Vector2[], int, bool> validate, ref long work, ref long validations, long validationBudget,
            out int left, out int bottom, out Vector2[] placed, bool optimizeExtent = true, SearchProfile profile = null,
            ReverseUvOccupancySearch.Snapshot snapshot = null)
        {
            long started = profile == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                return FindCore(mask, rows, options, token, validate, ref work, ref validations, validationBudget,
                out left, out bottom, out placed, optimizeExtent, profile, snapshot);
            }
            finally
            {
                if (profile != null)
                {
                    profile.searchTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                    ++profile.searches;
                }
            }
        }

        static bool FindCore(Mask mask, List<(int start, int end)>[] rows, ReverseUvTransfer.Options options, CancellationToken token,
            Func<int, Vector2[], int, bool> validate, ref long work, ref long validations, long validationBudget,
            out int left, out int bottom, out Vector2[] placed, bool optimizeExtent, SearchProfile profile,
            ReverseUvOccupancySearch.Snapshot snapshot)
        {
            placed = null;
            left = bottom = 0;
            int localValidations = 0, best = int.MaxValue;
            // Occupancy is immutable throughout this search. Cache the lower
            // bound for each mask row while x increases; a later span can move
            // the query ahead, so reset if its first span is queried behind it.
            var cursors = options.indexFootprintSearch && mask.width <= rows.Length && mask.height <= rows.Length
                ? new RowCursor[mask.height] : null;
            var spans = cursors == null ? null : mask.CompileSpans();
            long setupStarted = profile == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            using var native = options.nativeFootprintSearch
                ? snapshot != null ? snapshot.CreateSearch(mask.width, mask.height, mask.CompileSpans(), rows, token)
                    : ReverseUvOccupancySearch.TryCreate(mask.width, mask.height, mask.CompileSpans(), rows, token, options.footprintSearchApi) : null;
            if (profile != null && native != null)
            {
                profile.nativeSetupTicks += System.Diagnostics.Stopwatch.GetTimestamp() - setupStarted;
                ++profile.nativeSearches;
            }
            CollisionRowIndex rowIndex = null;
            int y = 0;
            while (y + mask.height <= rows.Length)
            {
                token.ThrowIfCancellationRequested();
                if (y + mask.height >= best)
                    break;
                int x = 0, queries = 0;
                long rowWork = work;
                bool indexedRow = false;
                while (x + mask.width <= rows.Length)
                {
                    if (native == null && options.cacheCollisionRows && spans != null && spans.Length >= 4 && rows.Length <= 8192
                        && !indexedRow && queries >= 8 && work - rowWork >= 2048 && work < 128000000)
                    {
                        rowIndex ??= new CollisionRowIndex(rows.Length - mask.width);
                        long started = profile == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
                        rowIndex.Build(spans, rows, y, x, rows.Length - mask.width, token);
                        if (profile != null)
                        {
                            profile.rowIndexTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                            ++profile.rowIndexes;
                        }
                        indexedRow = true;
                    }
                    ++queries;
                    int next;
                    if (native != null)
                    {
                        native.x = x;
                        native.y = y;
                        if (!native.Next(best, ref work, token, out bool exhausted))
                        {
                            if (exhausted)
                                return placed != null;
                            return false;
                        }
                        x = native.x;
                        y = native.y;
                        next = 0;
                    }
                    else
                        next = NextPosition(mask, spans, rows, cursors, x, y, ref work, indexedRow ? rowIndex : null);
                    if (next < 0)
                        return false;
                    bool free = next == 0;
                    if (free)
                        next = x + 1;
                    if (free)
                    {
                        int score = Math.Max(x + mask.width, y + mask.height);
                        if (score >= best)
                            break;
                        foreach (float phase in new[] { 0f, .125f, .25f, .375f })
                        {
                            // Free cells can still be unrepresentable at Float32
                            // precision. Do not exhaust the canvas with expensive
                            // metric/contact audits for one such family.
                            if (++validations > validationBudget || ++localValidations > 64)
                                return placed != null;
                            // The raster includes one extra texel for phase
                            // changes. Keep the offset inside that reserve even
                            // when the requested gutter is large.
                            float offset = phase;
                            long started = profile == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
                            var points = mask.Place(x, y, offset);
                            int canvas = (int)(rows.Length * mask.unit);
                            float gutter = options.padding * mask.unit;
                            bool valid = !points.Any(p => p.x < gutter || p.y < gutter || p.x > canvas - gutter || p.y > canvas - gutter)
                                && (validate == null || validate(mask.id, points, canvas));
                            if (profile != null)
                            {
                                profile.auditTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                                ++profile.audits;
                            }
                            if (!valid)
                                continue;
                            // Bottom-left placement is deterministic. Searching
                            // every later free row for this one family exhausted
                            // the order before thousands of small charts were
                            // placed. Rotation and complete packing orders still
                            // compete using the final bbox cost.
                            left = x;
                            bottom = y;
                            placed = points;
                            if (!optimizeExtent)
                                return true;
                            best = score;
                            break;
                        }
                        if (best == Math.Max(mask.width, mask.height))
                            return true;
                    }
                    if (free && placed != null && bottom == y)
                        break;
                    x = next;
                }
                ++y;
            }
            return placed != null;
        }

        // Flatten only immutable mask spans, preserving row/span order. Empty
        // rows perform no work in the reference traversal and can be skipped.
        // Keep that traversal as the internal benchmark/regression control.
        static ReverseUvOccupancySearch.Snapshot PrepareSnapshot(Mask[] variants, List<(int start, int end)>[] rows,
            ReverseUvTransfer.Options options, CancellationToken token)
            => options.nativeFootprintSearch && options.reuseOccupancySnapshots &&
                variants.Any(m => m.width <= rows.Length && m.height <= rows.Length)
                ? ReverseUvOccupancySearch.Snapshot.TryCapture(rows, token, options.performance, options.footprintSearchApi) : null;

        static int NextPosition(Mask mask, (int row, int start, int end)[] spans, List<(int start, int end)>[] rows,
            RowCursor[] cursors, int x, int y, ref long work, CollisionRowIndex rowIndex = null)
        {
            if (spans != null)
            {
                int first = 0;
                if (rowIndex != null)
                {
                    if (rowIndex.TryCollision(x, out int next, out int visits))
                        return ConsumeSearchWork(visits, ref work) ? next : -1;
                    first = rowIndex.completeSpans;
                    if (!ConsumeSearchWork(first, ref work))
                        return -1;
                }
                for (int s = first; s < spans.Length; ++s)
                {
                    var span = spans[s];
                    if (++work > 128000000)
                        return -1;
                    var occupied = rows[y + span.row];
                    int i = FirstAfter(occupied, x + span.start, y, ref cursors[span.row]);
                    if (i < occupied.Count && occupied[i].start < x + span.end)
                        return Math.Max(x + 1, occupied[i].end - span.start);
                }
            }
            else
            {
                for (int r = 0; r < mask.height; ++r)
                    foreach (var span in mask.rows[r])
                    {
                        if (++work > 128000000)
                            return -1;
                        var occupied = rows[y + r];
                        int i = FirstAfter(occupied, x + span.start);
                        if (i < occupied.Count && occupied[i].start < x + span.end)
                            return Math.Max(x + 1, occupied[i].end - span.start);
                    }
            }
            return 0;
        }

        static int FirstAfter(List<(int start, int end)> spans, int x)
        {
            int lo = 0, hi = spans.Count;
            while (lo < hi)
            {
                int middle = (lo + hi) / 2;
                if (spans[middle].end <= x)
                    lo = middle + 1;
                else
                    hi = middle;
            }
            return lo;
        }

        static int FirstAfter(List<(int start, int end)> spans, int x, int row, ref RowCursor cursor)
        {
            int count = spans.Count;
            if (count < 2)
                return count == 0 || spans[0].end <= x ? count : 0;
            int lo = cursor.row == row && x >= cursor.threshold ? cursor.index : 0;
            cursor.row = row;
            cursor.threshold = x;
            if (lo < count && spans[lo].end <= x)
            {
                ++lo;
                if (lo < count && spans[lo].end <= x)
                {
                    int hi = count;
                    while (lo < hi)
                    {
                        int middle = (lo + hi) / 2;
                        if (spans[middle].end <= x)
                            lo = middle + 1;
                        else
                            hi = middle;
                    }
                }
            }
            cursor.index = lo;
            return lo;
        }

        static void Reserve(List<(int start, int end)> spans, int start, int end)
        {
            if (start >= end)
                return;
            int first = FirstAfter(spans, start), last = first;
            while (last < spans.Count && spans[last].start <= end)
            {
                start = Math.Min(start, spans[last].start);
                end = Math.Max(end, spans[last].end);
                ++last;
            }
            if (last > first)
                spans.RemoveRange(first, last - first);
            spans.Insert(first, (start, end));
        }
    }
}
