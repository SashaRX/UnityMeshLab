// UvChartMerge.cs — deterministic chart merging after the Remesh unwrap (UV0).
//
// Greedy rounds over adjacent chart pairs. For a pair, an orientation-preserving
// similarity (Procrustes: rotation + uniform scale + translation, no mirror) fits
// one chart's UVs onto its neighbour through the seam vertices. The seam is then
// snapped bit-exact onto the acceptor's UVs — xatlas reconnects charts by UV
// colocalization (faceMaterial only separates charts, it never joins them), so a
// 1e-6-close seam would come back as two islands after the re-pack. Because a
// similarity leaves per-triangle stretch untouched, the real distortion appears
// in that snap: acceptance therefore re-measures the stretch of the moved faces
// plus the acceptor's seam faces after snapping, and runs a full triangle-triangle
// overlap test (edge-edge crossings included, seam contact excluded). User island
// area/border limits are respected in source units. Merged charts are re-packed
// through the xatlas UvMesh bridge and their tangent frames are rebuilt from the
// final atlas layout — that covers the fit rotation, the seam snap's per-vertex
// displacement and the packer's per-axis ceil stretch alike, none of which a
// rotated stale tangent can represent. Any ambiguity rolls the geometry back to
// the pre-merge snapshot.
//
// Author: SashaRX.UnityMeshLab

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class UvChartMerge
    {
        // Gate constants (Experiment #1; protocol in Documentation~/EXPERIMENTS.md).
        // Seam fit error, relative to the acceptor's UV bbox diagonal.
        internal const float MaxSeamResidual = 0.02f;
        // Fit scale bounds: a merge must not change texel density by more than 2×.
        internal const float MinDensityScale = 0.5f;
        internal const float MaxDensityScale = 2f;
        // Absolute local stretch bounds — the same numbers UvChartQuality.Improves
        // uses for the global gate.
        internal const float MaxMeanStretch = 1.15f;
        internal const float MaxWorstStretch = 4f;
        const float StretchSlack = 1.1f;       // ...and never >10% worse than the same faces pre-merge
        const float ContainmentEps = 1e-6f;
        const uint OrphanChart = 0xFFFFFFFFu;

        // ── Entry point ─────────────────────────────────────────────────────

        /// <summary>Merges adjacent charts and re-packs the atlas. Mutates `geometry`
        /// only when every gate passes; otherwise restores the pre-merge state.
        /// `preMerge` is the quality of the geometry exactly as passed in.</summary>
        internal static void Apply(RemeshNative.Geometry geometry, UvChartQuality preMerge, RemeshSettings settings, CancellationToken token)
        {
            if (geometry == null || settings == null || geometry.charts == null || geometry.uv == null ||
                geometry.indices == null || geometry.chartCount < 2)
                return;
            token.ThrowIfCancellationRequested();
            var uvSnapshot = (Vector2[])geometry.uv.Clone();
            var chartsSnapshot = (int[])geometry.charts.Clone();
            var tangentsSnapshot = geometry.tangents == null ? null : (Vector4[])geometry.tangents.Clone();
            int chartCountSnapshot = geometry.chartCount;
            int smallChartSnapshot = geometry.smallChartCount;
            UvAtlasDiagnostics.Log(geometry, "merge-baseline", token);
            try
            {
                var mergedCharts = new HashSet<int>();
                int merged = MergeCharts(geometry, settings, token, mergedCharts);
                UvtLog.Info(UvtLog.Category.RemeshDiag,
                    $"[UV] merge-candidate: charts={chartCountSnapshot}->{geometry.chartCount}, acceptedMerges={merged}; candidate is not the final unwrap.");
                if (merged <= 0) return;
                UvAtlasDiagnostics.Log(geometry, "merge-before-pack", token, sameChartOnly: true);
                if (!Repack(geometry, settings, token, mergedCharts))
                    throw new InvalidOperationException("the re-pack rejected the merged charts");
                var post = UvChartQuality.Measure(geometry, token);
                UvAtlasDiagnostics.Log(geometry, "merge-after-pack (candidate)", token);
                UvtLog.Info(UvtLog.Category.RemeshDiag, FormattableString.Invariant(
                    $"[UV] merge-quality-gate: baseline charts={preMerge.charts} small={preMerge.smallCharts} mean={preMerge.meanStretch:G6} worst={preMerge.maxStretch:G6} valid={preMerge.valid}; candidate charts={post.charts} small={post.smallCharts} mean={post.meanStretch:G6} worst={post.maxStretch:G6} valid={post.valid}; meanLimit={Math.Max(1.15, preMerge.meanStretch * 1.1):G6} worstLimit={Math.Max(4, preMerge.maxStretch * 1.1):G6}; result={post.ImprovementFailure(preMerge, preMerge)}"));
                if (!post.Improves(preMerge, preMerge))
                    throw new InvalidOperationException(
                        "quality gate failed: " + post.ImprovementFailure(preMerge, preMerge));
                UvtLog.Info($"[Remesh] Chart merge: {chartCountSnapshot} → {geometry.chartCount} islands ({merged} merge(s) accepted).");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                geometry.uv = uvSnapshot;
                geometry.charts = chartsSnapshot;
                geometry.tangents = tangentsSnapshot;
                geometry.chartCount = chartCountSnapshot;
                geometry.smallChartCount = smallChartSnapshot;
                UvtLog.Warn("[Remesh] Chart merge reverted; keeping the unmerged unwrap. " + error.Message);
                UvtLog.Info(UvtLog.Category.RemeshDiag,
                    $"[UV] merge-rollback: restored original UV/chart/tangent arrays; final charts={chartCountSnapshot}, small={smallChartSnapshot}.");
            }
        }

        // ── Merge core (pure: no native calls, unit-testable) ───────────────

        /// <summary>Greedy merge rounds over adjacent chart pairs followed by id
        /// compaction. Mutates `geometry` (uv/charts/chartCount/smallChartCount)
        /// only through accepted merges; tangents are left for the post-pack rebuild.
        /// `mergedChartIds` collects the compacted ids of charts that absorbed or were
        /// absorbed. Returns the accepted merge count.</summary>
        internal static int MergeCharts(RemeshNative.Geometry geometry, RemeshSettings settings, CancellationToken token, HashSet<int> mergedChartIds)
        {
            int faceCount = geometry.indices.Length / 3;
            var faceChart = new int[faceCount];
            for (int f = 0; f < faceCount; ++f)
            {
                int chart = geometry.charts[geometry.indices[f * 3]];
                if (geometry.charts[geometry.indices[f * 3 + 1]] != chart || geometry.charts[geometry.indices[f * 3 + 2]] != chart)
                    return 0; // a face spanning two charts cannot be merged consistently
                faceChart[f] = chart;
            }
            int chartCount = geometry.chartCount;
            var slots = MeshGeometry.WeldPositions(geometry.positions, out int slotCount);
            var slotVertex = new int[slotCount];
            for (int v = 0; v < slots.Length; ++v) slotVertex[slots[v]] = v;

            // Welded edge incidences. Non-manifold edges (>2 faces) never merge and
            // count toward no chart's boundary.
            var edgeFaces = new Dictionary<long, List<int>>();
            for (int f = 0; f < faceCount; ++f)
                for (int k = 0; k < 3; ++k)
                {
                    int sa = slots[geometry.indices[f * 3 + k]];
                    int sb = slots[geometry.indices[f * 3 + (k + 1) % 3]];
                    if (sa == sb) continue;
                    long key = EdgeKey(sa, sb);
                    if (!edgeFaces.TryGetValue(key, out var list)) edgeFaces[key] = list = new List<int>(2);
                    list.Add(f);
                }
            var edgeKeys = new long[edgeFaces.Count]; // canonical order: float sums stay deterministic
            edgeFaces.Keys.CopyTo(edgeKeys, 0);
            Array.Sort(edgeKeys);

            var chartFaces = new List<int>[chartCount];
            var chartVerts = new List<int>[chartCount];
            var chartArea = new float[chartCount];
            var chartBoundary = new float[chartCount];
            var chartUvMin = new Vector2[chartCount];
            var chartUvMax = new Vector2[chartCount];

            int merged = 0;
            for (int round = 0; round < chartCount; ++round)
            {
                token.ThrowIfCancellationRequested();
                // ── per-chart aggregates for the current face assignment ──
                for (int c = 0; c < chartCount; ++c)
                {
                    chartFaces[c]?.Clear();
                    chartVerts[c]?.Clear();
                    chartArea[c] = 0f;
                    chartBoundary[c] = 0f;
                    chartUvMin[c] = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                    chartUvMax[c] = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                }
                for (int f = 0; f < faceCount; ++f)
                {
                    int c = faceChart[f];
                    (chartFaces[c] ??= new List<int>()).Add(f);
                    int i0 = geometry.indices[f * 3], i1 = geometry.indices[f * 3 + 1], i2 = geometry.indices[f * 3 + 2];
                    chartArea[c] += Vector3.Cross(geometry.positions[i1] - geometry.positions[i0],
                        geometry.positions[i2] - geometry.positions[i0]).magnitude * 0.5f;
                }
                for (int v = 0; v < geometry.charts.Length; ++v)
                {
                    int c = geometry.charts[v];
                    (chartVerts[c] ??= new List<int>()).Add(v);
                    chartUvMin[c] = Vector2.Min(chartUvMin[c], geometry.uv[v]);
                    chartUvMax[c] = Vector2.Max(chartUvMax[c], geometry.uv[v]);
                }
                // Per-chart 3D boundary: edges with exactly one face of that chart —
                // including seam edges shared with the chart's neighbours.
                foreach (long key in edgeKeys)
                {
                    var incident = edgeFaces[key];
                    if (incident.Count > 2) continue;
                    if (incident.Count == 1) { chartBoundary[faceChart[incident[0]]] += EdgeLength(geometry.positions, slotVertex, key); continue; }
                    int ca = faceChart[incident[0]], cb = faceChart[incident[1]];
                    if (ca == cb) continue;
                    float length = EdgeLength(geometry.positions, slotVertex, key);
                    chartBoundary[ca] += length;
                    chartBoundary[cb] += length;
                }
                // Adjacency: count-2 edges spanning two charts, with their seam edges.
                var adjacency = new Dictionary<(int, int), List<long>>();
                foreach (long key in edgeKeys)
                {
                    var incident = edgeFaces[key];
                    if (incident.Count != 2) continue;
                    int ca = faceChart[incident[0]], cb = faceChart[incident[1]];
                    if (ca == cb) continue;
                    var pair = ca < cb ? (ca, cb) : (cb, ca);
                    if (!adjacency.TryGetValue(pair, out var seams)) adjacency[pair] = seams = new List<long>();
                    seams.Add(key);
                }
                if (adjacency.Count == 0) break;
                var pairs = new List<(int, int)>(adjacency.Keys);
                pairs.Sort((x, y) => x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1) : x.Item2.CompareTo(y.Item2));

                bool accepted = false;
                foreach (var pair in pairs)
                {
                    token.ThrowIfCancellationRequested();
                    if (TryMergePair(geometry, faceChart, slots, slotVertex, edgeFaces, pair.Item1, pair.Item2,
                            adjacency[pair], chartFaces, chartVerts, chartArea, chartBoundary, chartUvMin, chartUvMax,
                            settings, mergedChartIds, token))
                    {
                        ++merged;
                        accepted = true;
                        for (int f = 0; f < faceCount; ++f)
                            for (int k = 0; k < 3; ++k)
                                geometry.charts[geometry.indices[f * 3 + k]] = faceChart[f];
                        break; // first-accept greedy; the next round re-evaluates everything
                    }
                }
                if (!accepted) break;
            }
            if (merged > 0) CompactChartIds(geometry, faceChart, faceCount, mergedChartIds);
            return merged;
        }

        struct Candidate
        {
            public float residual;
            public int movedVertices;
            public int acceptor, mover;
            public float cos, sin, scale;
            public Vector2 offset;
        }

        /// <summary>Evaluates both merge directions for one adjacent pair and applies
        /// the deterministic best one. The geometry is left untouched unless a merge
        /// is applied. Returns true when a merge was applied.</summary>
        static bool TryMergePair(RemeshNative.Geometry g, int[] faceChart, int[] slots, int[] slotVertex,
            Dictionary<long, List<int>> edgeFaces, int chartA, int chartB, List<long> seamKeys,
            List<int>[] chartFaces, List<int>[] chartVerts, float[] chartArea, float[] chartBoundary,
            Vector2[] chartUvMin, Vector2[] chartUvMax, RemeshSettings settings, HashSet<int> mergedChartIds,
            CancellationToken token)
        {
            // Slot → vertex maps per chart; the intersection is the seam. Every
            // shared position is snapped, not only edge-seam ones — a corner shared
            // without a shared edge would otherwise leave a UV split inside the chart
            // and xatlas would split it again.
            var slotsA = new Dictionary<int, int>(chartVerts[chartA].Count);
            foreach (int v in chartVerts[chartA]) slotsA[slots[v]] = v;
            var slotsB = new Dictionary<int, int>(chartVerts[chartB].Count);
            foreach (int v in chartVerts[chartB]) slotsB[slots[v]] = v;
            var seamSlotList = new List<int>();
            foreach (var kv in slotsA) if (slotsB.ContainsKey(kv.Key)) seamSlotList.Add(kv.Key);
            seamSlotList.Sort();
            float seamLength = 0f;
            foreach (long key in seamKeys) seamLength += EdgeLength(g.positions, slotVertex, key);

            Candidate best = default;
            bool haveBest = false;
            for (int dir = 0; dir < 2; ++dir)
            {
                token.ThrowIfCancellationRequested();
                int acceptor = dir == 0 ? chartA : chartB;
                int mover = dir == 0 ? chartB : chartA;
                // The slot maps follow the direction: the acceptor's UVs are the fit
                // target, the mover's seam copies are the ones transformed and snapped.
                var acceptorSlotOf = dir == 0 ? slotsA : slotsB;
                var moverSlotOf = dir == 0 ? slotsB : slotsA;
                if (EvaluateDirection(g, faceChart, slots, edgeFaces, acceptorSlotOf, moverSlotOf, seamSlotList, seamKeys,
                        chartFaces, chartArea, chartBoundary, chartUvMin, chartUvMax,
                        acceptor, mover, seamLength, settings, out var candidate) &&
                    (!haveBest || Better(candidate, best)))
                {
                    best = candidate;
                    haveBest = true;
                }
            }
            if (!haveBest) return false;

            // Re-apply the winning transform from the pristine UVs (evaluations restore).
            var moverVerts = chartVerts[best.mover];
            ApplySimilarity(g, moverVerts, best.cos, best.sin, best.scale, best.offset);
            SnapSeam(g, seamSlotList, best.acceptor == chartA ? slotsA : slotsB, best.acceptor == chartA ? slotsB : slotsA);
            foreach (int f in chartFaces[best.mover]) faceChart[f] = best.acceptor;
            mergedChartIds.Add(best.acceptor);
            mergedChartIds.Add(best.mover);
            return true;
        }

        // Smaller seam residual wins; then fewer moved vertices; then lower acceptor id.
        static bool Better(Candidate candidate, Candidate best)
        {
            if (candidate.residual < best.residual - 1e-12f) return true;
            if (best.residual < candidate.residual - 1e-12f) return false;
            if (candidate.movedVertices != best.movedVertices) return candidate.movedVertices < best.movedVertices;
            return candidate.acceptor < best.acceptor;
        }

        /// <summary>One merge direction: fit, gates, staged transform + snap, local
        /// stretch and overlap checks. Always restores the mover's UVs; on success
        /// returns the candidate for re-application by the caller.</summary>
        static bool EvaluateDirection(RemeshNative.Geometry g, int[] faceChart, int[] slots,
            Dictionary<long, List<int>> edgeFaces, Dictionary<int, int> acceptorSlotOf,
            Dictionary<int, int> moverSlotOf, List<int> seamSlots, List<long> seamKeys,
            List<int>[] chartFaces, float[] chartArea, float[] chartBoundary,
            Vector2[] chartUvMin, Vector2[] chartUvMax, int acceptor, int mover, float seamLength,
            RemeshSettings settings, out Candidate candidate)
        {
            candidate = default;
            if (seamSlots.Count < 2) return false;

            // ── Procrustes fit over the seam (orientation-preserving) ──
            var from = new Vector2[seamSlots.Count];
            var to = new Vector2[seamSlots.Count];
            for (int i = 0; i < seamSlots.Count; ++i)
            {
                from[i] = g.uv[moverSlotOf[seamSlots[i]]];
                to[i] = g.uv[acceptorSlotOf[seamSlots[i]]];
            }
            if (!FitSimilarity(from, to, out float cos, out float sin, out float scale, out Vector2 offset, out float residual))
                return false;
            float normalize = Mathf.Max((chartUvMax[acceptor] - chartUvMin[acceptor]).magnitude, 1e-6f);
            if (scale < MinDensityScale || scale > MaxDensityScale) return false;           // texel-density gate
            if (residual > MaxSeamResidual * normalize) return false;                       // seam misalignment gate
            float area = chartArea[acceptor] + chartArea[mover];
            if (settings.maxChartArea > 0f && area > settings.maxChartArea) return false;   // user island area limit
            float boundary = chartBoundary[acceptor] + chartBoundary[mover] - 2f * seamLength;
            if (settings.maxChartBoundary > 0f && boundary > settings.maxChartBoundary) return false;

            var moverFaces = chartFaces[mover];
            var localFaces = new List<int>(moverFaces);
            var acceptorSeamFaces = new HashSet<int>();
            foreach (long key in seamKeys)
                foreach (int f in edgeFaces[key])
                    if (faceChart[f] == acceptor && acceptorSeamFaces.Add(f))
                        localFaces.Add(f);

            // Pre-merge stretch and winding of exactly the faces the snap can distort.
            MeasureStretch(g, localFaces, out double preMean, out var preWorst);
            var preSigns = new int[localFaces.Count];
            for (int i = 0; i < localFaces.Count; ++i) preSigns[i] = UvWindingSign(g, localFaces[i]);

            var moverVerts = new List<int>();
            for (int v = 0; v < g.charts.Length; ++v) if (g.charts[v] == mover) moverVerts.Add(v);
            var backup = new Vector2[moverVerts.Count];
            for (int i = 0; i < moverVerts.Count; ++i) backup[i] = g.uv[moverVerts[i]];

            ApplySimilarity(g, moverVerts, cos, sin, scale, offset);
            SnapSeam(g, seamSlots, acceptorSlotOf, moverSlotOf);

            bool passes = true;
            for (int i = 0; i < localFaces.Count && passes; ++i)
            {
                int postSign = UvWindingSign(g, localFaces[i]);
                if (preSigns[i] != 0 && postSign != 0 && postSign != preSigns[i]) passes = false; // flipped face
            }
            if (passes)
            {
                MeasureStretch(g, localFaces, out double postMean, out var postWorst);
                if (double.IsNaN(postMean) || double.IsNaN(postWorst) ||
                    postMean > Math.Max(MaxMeanStretch, preMean * StretchSlack) ||
                    postWorst > Math.Max(MaxWorstStretch, preWorst * StretchSlack))
                    passes = false;
            }
            if (passes && !OverlapFree(g, slots, chartFaces[acceptor], moverFaces)) passes = false;
            // The snap is not a similarity: it moves individual boundary vertices, so a
            // tightly folded mover can overlap itself without flipping a face or
            // tripping the stretch gate. Faces sharing an edge or a welded corner stay
            // legal (the seam and its corner fans); only true crossings reject.
            if (passes && !OverlapFree(g, slots, moverFaces, moverFaces)) passes = false;

            for (int i = 0; i < moverVerts.Count; ++i) g.uv[moverVerts[i]] = backup[i]; // restore, bit-exact
            if (!passes) return false;
            candidate = new Candidate {
                residual = residual, movedVertices = moverVerts.Count, acceptor = acceptor, mover = mover,
                cos = cos, sin = sin, scale = scale, offset = offset,
            };
            return true;
        }

        static void ApplySimilarity(RemeshNative.Geometry g, List<int> vertices, float cos, float sin, float scale, Vector2 offset)
        {
            foreach (int v in vertices)
            {
                var uv = g.uv[v];
                g.uv[v] = scale * new Vector2(cos * uv.x - sin * uv.y, sin * uv.x + cos * uv.y) + offset;
            }
        }

        // Bit-exact: the mover's seam copies receive the acceptor's UV values verbatim.
        static void SnapSeam(RemeshNative.Geometry g, List<int> seamSlots,
            Dictionary<int, int> acceptorSlotOf, Dictionary<int, int> moverSlotOf)
        {
            foreach (int slot in seamSlots) g.uv[moverSlotOf[slot]] = g.uv[acceptorSlotOf[slot]];
        }

        static void CompactChartIds(RemeshNative.Geometry geometry, int[] faceChart, int faceCount, HashSet<int> mergedChartIds)
        {
            var seen = new bool[geometry.chartCount];
            foreach (int chart in faceChart) seen[chart] = true;
            var remap = new int[geometry.chartCount];
            int next = 0;
            for (int c = 0; c < remap.Length; ++c) remap[c] = seen[c] ? next++ : -1;
            var remapped = new HashSet<int>();
            foreach (int id in mergedChartIds)
            {
                int mapped = remap[id];
                if (mapped >= 0) remapped.Add(mapped);
            }
            mergedChartIds.Clear();
            foreach (int id in remapped) mergedChartIds.Add(id);
            for (int f = 0; f < faceCount; ++f) faceChart[f] = remap[faceChart[f]];
            for (int f = 0; f < faceCount; ++f)
                for (int k = 0; k < 3; ++k)
                    geometry.charts[geometry.indices[f * 3 + k]] = faceChart[f];
            geometry.chartCount = next;
            var counts = new int[next];
            for (int f = 0; f < faceCount; ++f) ++counts[faceChart[f]];
            int small = 0;
            foreach (int count in counts) if (count > 0 && count <= 8) ++small;
            geometry.smallChartCount = small;
        }

        // ── Procrustes fit ──────────────────────────────────────────────────

        /// <summary>Orientation-preserving similarity (rotation + uniform scale +
        /// translation) mapping `from` onto `to` in the least-squares sense — no
        /// mirror. `residual` is the absolute RMS error. False when degenerate.</summary>
        internal static bool FitSimilarity(Vector2[] from, Vector2[] to,
            out float cos, out float sin, out float scale, out Vector2 offset, out float residual)
        {
            cos = 1f; sin = 0f; scale = 1f; offset = Vector2.zero; residual = float.MaxValue;
            int n = Math.Min(from.Length, to.Length);
            if (n < 2) return false;
            Vector2 cf = Vector2.zero, ct = Vector2.zero;
            for (int i = 0; i < n; ++i) { cf += from[i]; ct += to[i]; }
            cf /= n; ct /= n;
            double p = 0, q = 0, denom = 0;
            for (int i = 0; i < n; ++i)
            {
                double fx = from[i].x - cf.x, fy = from[i].y - cf.y;
                double tx = to[i].x - ct.x, ty = to[i].y - ct.y;
                p += tx * fx + ty * fy;
                q += ty * fx - tx * fy;
                denom += fx * fx + fy * fy;
            }
            if (denom < 1e-20) return false;
            double angle = Math.Atan2(q, p);
            cos = (float)Math.Cos(angle);
            sin = (float)Math.Sin(angle);
            scale = (float)((cos * p + sin * q) / denom);
            if (!(scale > 1e-12f)) return false;
            offset = ct - scale * new Vector2(cos * cf.x - sin * cf.y, sin * cf.x + cos * cf.y);
            double sum = 0;
            for (int i = 0; i < n; ++i)
            {
                Vector2 mapped = scale * new Vector2(cos * from[i].x - sin * from[i].y, sin * from[i].x + cos * from[i].y) + offset;
                float dx = mapped.x - to[i].x, dy = mapped.y - to[i].y;
                sum += (double)dx * dx + (double)dy * dy;
            }
            residual = (float)Math.Sqrt(sum / n);
            return true;
        }

        // ── Overlap test ────────────────────────────────────────────────────

        struct FaceBox
        {
            public Vector2 min, max;
        }

        static FaceBox FaceBoxOf(RemeshNative.Geometry g, int f)
        {
            var a = g.uv[g.indices[f * 3]];
            var b = g.uv[g.indices[f * 3 + 1]];
            var c = g.uv[g.indices[f * 3 + 2]];
            return new FaceBox { min = Vector2.Min(a, Vector2.Min(b, c)), max = Vector2.Max(a, Vector2.Max(b, c)) };
        }

        /// <summary>True when no UV triangle of the acceptor intersects a UV triangle of
        /// the mover. X-sorted sweep broad phase; the exact test is segment intersections
        /// plus strict containment. Contact along a shared welded edge is the intended
        /// seam and never counts.</summary>
        static bool OverlapFree(RemeshNative.Geometry g, int[] slots, List<int> facesA, List<int> facesB)
        {
            int na = facesA.Count, nb = facesB.Count;
            var aBox = new FaceBox[na];
            var aOrder = new int[na];
            var aMinX = new float[na];
            for (int i = 0; i < na; ++i)
            {
                aBox[i] = FaceBoxOf(g, facesA[i]);
                aOrder[i] = i;
                aMinX[i] = aBox[i].min.x;
            }
            var bBox = new FaceBox[nb];
            var bOrder = new int[nb];
            var bMinX = new float[nb];
            for (int i = 0; i < nb; ++i)
            {
                bBox[i] = FaceBoxOf(g, facesB[i]);
                bOrder[i] = i;
                bMinX[i] = bBox[i].min.x;
            }
            Array.Sort(aMinX, aOrder);
            Array.Sort(bMinX, bOrder);

            var active = new List<int>(); // positions into the A arrays
            int pointer = 0;
            for (int bi = 0; bi < nb; ++bi)
            {
                int bPos = bOrder[bi];
                ref var bb = ref bBox[bPos];
                while (pointer < na && aBox[aOrder[pointer]].min.x <= bb.max.x) active.Add(aOrder[pointer++]);
                for (int i = active.Count - 1; i >= 0; --i)
                    if (aBox[active[i]].max.x < bb.min.x) active.RemoveAt(i);
                foreach (int aPos in active)
                {
                    ref var ba = ref aBox[aPos];
                    if (ba.max.x < bb.min.x || bb.max.x < ba.min.x ||
                        ba.max.y < bb.min.y || bb.max.y < ba.min.y) continue;
                    if (TrianglesIntersect(g, slots, facesA[aPos], facesB[bPos])) return false;
                }
            }
            return true;
        }

        /// <summary>Exact tri-tri overlap in UV space. Faces sharing a welded edge are
        /// the intentional seam contact and never overlap; everything else must keep
        /// clear of everything: proper crossings, collinear overlaps, T-junctions and
        /// strict containment all reject — only same-slot corner touches are allowed.</summary>
        static bool TrianglesIntersect(RemeshNative.Geometry g, int[] slots, int fa, int fb)
        {
            int a0 = g.indices[fa * 3], a1 = g.indices[fa * 3 + 1], a2 = g.indices[fa * 3 + 2];
            int b0 = g.indices[fb * 3], b1 = g.indices[fb * 3 + 1], b2 = g.indices[fb * 3 + 2];
            long a01 = EdgeKey(slots[a0], slots[a1]), a12 = EdgeKey(slots[a1], slots[a2]), a20 = EdgeKey(slots[a2], slots[a0]);
            long b01 = EdgeKey(slots[b0], slots[b1]), b12 = EdgeKey(slots[b1], slots[b2]), b20 = EdgeKey(slots[b2], slots[b0]);
            if (a01 == b01 || a01 == b12 || a01 == b20 ||
                a12 == b01 || a12 == b12 || a12 == b20 ||
                a20 == b01 || a20 == b12 || a20 == b20) return false;

            var au0 = g.uv[a0]; var au1 = g.uv[a1]; var au2 = g.uv[a2];
            var bu0 = g.uv[b0]; var bu1 = g.uv[b1]; var bu2 = g.uv[b2];
            if (SegmentsCross(au0, au1, slots[a0], slots[a1], bu0, bu1, slots[b0], slots[b1]) ||
                SegmentsCross(au0, au1, slots[a0], slots[a1], bu1, bu2, slots[b1], slots[b2]) ||
                SegmentsCross(au0, au1, slots[a0], slots[a1], bu2, bu0, slots[b2], slots[b0]) ||
                SegmentsCross(au1, au2, slots[a1], slots[a2], bu0, bu1, slots[b0], slots[b1]) ||
                SegmentsCross(au1, au2, slots[a1], slots[a2], bu1, bu2, slots[b1], slots[b2]) ||
                SegmentsCross(au1, au2, slots[a1], slots[a2], bu2, bu0, slots[b2], slots[b0]) ||
                SegmentsCross(au2, au0, slots[a2], slots[a0], bu0, bu1, slots[b0], slots[b1]) ||
                SegmentsCross(au2, au0, slots[a2], slots[a0], bu1, bu2, slots[b1], slots[b2]) ||
                SegmentsCross(au2, au0, slots[a2], slots[a0], bu2, bu0, slots[b2], slots[b0])) return true;
            if (StrictlyInside(au0, bu0, bu1, bu2) || StrictlyInside(au1, bu0, bu1, bu2) || StrictlyInside(au2, bu0, bu1, bu2)) return true;
            if (StrictlyInside(bu0, au0, au1, au2) || StrictlyInside(bu1, au0, au1, au2) || StrictlyInside(bu2, au0, au1, au2)) return true;
            return false;
        }

        /// <summary>True when the two UV segments touch in a way a merged chart must
        /// not: a proper crossing, a positive-length collinear overlap, a T-junction,
        /// or an endpoint meeting of two DIFFERENT welded positions (a fold). Endpoint
        /// meetings of the same welded position — the seam corner fans — are fine.</summary>
        static bool SegmentsCross(Vector2 p1, Vector2 p2, int s1, int s2, Vector2 q1, Vector2 q2, int r1, int r2)
        {
            Vector2 dp = p2 - p1, dq = q2 - q1;
            float cross = dp.x * dq.y - dp.y * dq.x;
            float scale = dp.magnitude * dq.magnitude;
            Vector2 qp = q1 - p1;
            if (Mathf.Abs(cross) <= 1e-10f * Mathf.Max(1e-12f, scale))
            {
                float lenSq = dp.sqrMagnitude;
                if (lenSq <= 0f || dq.sqrMagnitude <= 0f) return false;
                float cross2 = dp.x * qp.y - dp.y * qp.x;
                if (Mathf.Abs(cross2) > 1e-6f * Mathf.Max(1e-12f, dp.magnitude * qp.magnitude)) return false; // parallel, not collinear
                float t1 = Vector2.Dot(qp, dp) / lenSq;
                float t2 = t1 + Vector2.Dot(dq, dp) / lenSq;
                float lo = Mathf.Max(Mathf.Min(t1, t2), 0f);
                float hi = Mathf.Min(Mathf.Max(t1, t2), 1f);
                if (hi < lo - 1e-6f) return false;                                        // no overlap
                if (hi - lo > 1e-6f) return true;                                         // collinear overlap
                int slotP = Mathf.Abs(lo) <= 1e-6f ? s1 : s2;                             // single-point contact
                int slotQ = Mathf.Abs(t1 - lo) <= 1e-6f ? r1 : r2;
                return slotP != slotQ;
            }
            float t = (qp.x * dq.y - qp.y * dq.x) / cross;
            float u = (qp.x * dp.y - qp.y * dp.x) / cross;
            if (t < -1e-6f || t > 1f + 1e-6f || u < -1e-6f || u > 1f + 1e-6f) return false;   // miss
            bool tEnd = t <= 1e-6f || t >= 1f - 1e-6f;
            bool uEnd = u <= 1e-6f || u >= 1f - 1e-6f;
            if (!tEnd && !uEnd) return true;                                                  // proper crossing
            if (tEnd && uEnd)
            {
                int slotP = t < 0.5f ? s1 : s2;
                int slotQ = u < 0.5f ? r1 : r2;
                return slotP != slotQ;                                                        // corner touch of one slot is fine
            }
            return true;                                                                      // T-junction
        }

        static bool StrictlyInside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
            => MeshGeometry.Barycentric(p, a, b, c, out var w) &&
               w.x > ContainmentEps && w.y > ContainmentEps && w.z > ContainmentEps;

        // ── Local stretch ───────────────────────────────────────────────────

        /// <summary>Area-weighted mean and worst conformal stretch (σmax/σmin) over the
        /// given faces — the same per-face math as UvChartQuality.Measure.</summary>
        static void MeasureStretch(RemeshNative.Geometry g, List<int> faces, out double mean, out double worst)
        {
            double weighted = 0, area = 0;
            worst = 0;
            foreach (int f in faces)
            {
                int i0 = g.indices[f * 3], i1 = g.indices[f * 3 + 1], i2 = g.indices[f * 3 + 2];
                Vector3 e1 = g.positions[i1] - g.positions[i0], e2 = g.positions[i2] - g.positions[i0];
                Vector2 u1 = g.uv[i1] - g.uv[i0], u2 = g.uv[i2] - g.uv[i0];
                double length = e1.magnitude, twiceArea = Vector3.Cross(e1, e2).magnitude;
                if (length <= 0 || twiceArea <= 0) continue;
                double tx = Vector3.Dot(e1, e2) / length, ty = twiceArea / length;
                double j00 = u1.x / length, j10 = u1.y / length;
                double j01 = (u2.x - j00 * tx) / ty, j11 = (u2.y - j10 * tx) / ty;
                double trace = j00 * j00 + j10 * j10 + j01 * j01 + j11 * j11;
                double determinant = j00 * j11 - j10 * j01;
                double largest = (trace + Math.Sqrt(Math.Max(0, trace * trace - 4 * determinant * determinant))) * .5;
                double stretch = determinant == 0 ? double.PositiveInfinity : largest / Math.Abs(determinant);
                weighted += stretch * twiceArea;
                area += twiceArea;
                if (stretch > worst) worst = stretch;
            }
            mean = area > 0 ? weighted / area : 0;
        }

        static int UvWindingSign(RemeshNative.Geometry g, int f)
        {
            Vector2 u1 = g.uv[g.indices[f * 3 + 1]] - g.uv[g.indices[f * 3]];
            Vector2 u2 = g.uv[g.indices[f * 3 + 2]] - g.uv[g.indices[f * 3]];
            double det = (double)u1.x * u2.y - (double)u1.y * u2.x;
            return det > 0 ? 1 : det < 0 ? -1 : 0;
        }

        // ── Re-pack through the xatlas UvMesh bridge ────────────────────────

        /// <summary>Feeds the merged charts to xatlas (one faceMaterial per chart) and
        /// re-packs. Writes the packed UVs back and rebuilds the merged charts' tangent
        /// frames from the final layout. False — with the geometry untouched — on any
        /// anomaly (busy session, refused pack, unexpected output shape).</summary>
        static bool Repack(RemeshNative.Geometry geometry, RemeshSettings settings, CancellationToken token, HashSet<int> mergedCharts)
        {
            int vertexCount = geometry.positions.Length;
            int faceCount = geometry.indices.Length / 3;
            var faceChart = new int[faceCount];
            for (int f = 0; f < faceCount; ++f)
            {
                int chart = geometry.charts[geometry.indices[f * 3]];
                if (geometry.charts[geometry.indices[f * 3 + 1]] != chart || geometry.charts[geometry.indices[f * 3 + 2]] != chart)
                    return false;
                faceChart[f] = chart;
            }
            // xatlas's UvMesh packer requires input UVs inside [0,1] (it asserts and
            // crashes on negative texel coords, xatlas.cpp:8598), and the merge fit can
            // place a rotated chart anywhere in the plane. Normalize the whole layout
            // with one uniform scale + translation: chart shapes and relative texel
            // densities are preserved, and the pack output replaces these UVs anyway.
            Vector2 min = geometry.uv[0], max = geometry.uv[0];
            foreach (var p in geometry.uv)
            {
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.x) || float.IsInfinity(p.y)) return false;
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }
            float extent = Mathf.Max(max.x - min.x, max.y - min.y);
            if (!(extent > 1e-12f)) return false;
            float normalize = 1f / extent;
            var flatUv = new float[vertexCount * 2];
            for (int i = 0; i < vertexCount; ++i)
            {
                flatUv[i * 2] = (geometry.uv[i].x - min.x) * normalize;
                flatUv[i * 2 + 1] = (geometry.uv[i].y - min.y) * normalize;
            }
            var indices = new uint[geometry.indices.Length];
            for (int i = 0; i < geometry.indices.Length; ++i) indices[i] = (uint)geometry.indices[i];
            var faceMaterials = new uint[faceCount];
            for (int f = 0; f < faceCount; ++f) faceMaterials[f] = (uint)faceChart[f];

            if (!XatlasRepack.TryAcquireNativeSession())
            {
                UvtLog.Warn("[Remesh] Chart merge skipped: an xatlas repack session is already in progress.");
                return false;
            }
            try
            {
                int rotate = settings.packRotate ? 1 : 0;
                if (PackAndRead(geometry, settings, flatUv, indices, faceMaterials, rotate, rotate, mergedCharts, token)) return true;
                // Both rotation flags are separate xatlas knobs; a rotate placement is the
                // one way a per-input-vertex UV can come back ambiguous. Fresh session —
                // a second PackCharts on a packed atlas is not a defined state.
                token.ThrowIfCancellationRequested();
                if (PackAndRead(geometry, settings, flatUv, indices, faceMaterials, 0, 0, mergedCharts, token)) return true;
                UvtLog.Warn("[Remesh] Chart merge re-pack did not map back cleanly; reverting.");
                return false;
            }
            finally
            {
                XatlasRepack.ReleaseNativeSession();
            }
        }

        /// <summary>One fresh create → add → pack → read → destroy cycle, all on the
        /// calling worker thread. Everything that reads the bridge's global atlas stays
        /// between create and destroy on this one thread — no editor APIs, no pool
        /// tasks — so no exception can race the destroy against an in-flight pack.</summary>
        static bool PackAndRead(RemeshNative.Geometry geometry, RemeshSettings settings, float[] flatUv, uint[] indices,
            uint[] faceMaterials, int rotateCharts, int rotateToAxis, HashSet<int> mergedCharts, CancellationToken token)
        {
            // Pack at 4× the user-facing resolution, exactly like XatlasRepack: xatlas
            // ceil-rounds each chart's extents to texel dimensions, so a layout whose
            // charts sum near the full atlas size cannot fit — the packer then places
            // charts at negative coordinates and its texcoord assert aborts the editor
            // (xatlas.cpp:8598). The oversample turns the rounding into a fraction of
            // the internal atlas; the bridge normalizes the output back to [0,1], so
            // the user-facing resolution is unchanged. Padding scales with it to keep
            // the gap fraction in UV space constant.
            const int kPackOversample = 4;
            uint internalRes = (uint)settings.textureResolution * kPackOversample;
            uint internalPad = (uint)settings.padding * kPackOversample;
            DumpRepackInputs(flatUv, indices, faceMaterials, internalRes, internalPad,
                rotateCharts, rotateToAxis, settings.packBlockAlign ? 1 : 0, settings.packBruteForce ? 1 : 0);
            XatlasNative.xatlasCreate();
            try
            {
                int addError = XatlasNative.xatlasAddUvMesh(flatUv, (uint)geometry.positions.Length, indices,
                    (uint)indices.Length, faceMaterials, (uint)faceMaterials.Length);
                if (addError != 0)
                {
                    UvtLog.Warn("[Remesh] Chart merge re-pack rejected the merged charts (xatlas error " + addError + ").");
                    return false;
                }
                // The pack runs INLINE on this worker thread. The unwrap stage is
                // already background work, so there is no editor to keep responsive,
                // and RunNativePackAsync's machinery is main-thread-only: it starts
                // the native pack on a pool task and then calls
                // EditorApplication.timeSinceStartup / UvProgress, which THROW here.
                // That throw raced the finally-destroy below against the still-running
                // pack task and freed the atlas under it — the editor crashed inside
                // PackCharts with wandering access violations (three dumps,
                // 2026-10-03/04). One thread, no editor APIs, no orphan task.
                if ((long)geometry.chartCount * internalRes * internalRes > 20_000_000_000L)
                {
                    UvtLog.Warn("[Remesh] Chart merge re-pack refused: the pack cost is past the safety budget.");
                    return false;
                }
                token.ThrowIfCancellationRequested();
                XatlasNative.xatlasComputeCharts();
                XatlasNative.xatlasPackCharts(0, internalPad, 0f, internalRes, 1,
                    settings.packBlockAlign ? 1 : 0, settings.packBruteForce ? 1 : 0, rotateCharts, rotateToAxis);
                return ReadPackedUv(geometry, token, mergedCharts);
            }
            finally { XatlasNative.xatlasDestroy(); }
        }

        /// <summary>Crash-capture harness for the chart-merge experiment: the exact
        /// native re-pack inputs are dumped to %TEMP%/meshlab-uvmerge before the pack.
        /// The merge re-pack has produced editor crashes that depend on the model's
        /// data and could not be reproduced synthetically; with the inputs on disk a
        /// crash becomes replayable in a native harness (Documentation~/EXPERIMENTS.md).
        /// Best-effort: diagnostics must never break the stage.</summary>
        static void DumpRepackInputs(float[] flatUv, uint[] indices,
            uint[] faceMaterials, uint resolution, uint padding, int rotateCharts, int rotateToAxis,
            int blockAlign, int bruteForce)
        {
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meshlab-uvmerge");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir,
                    "repack_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin");
                using (var writer = new System.IO.BinaryWriter(System.IO.File.Create(path)))
                {
                    writer.Write((uint)flatUv.Length / 2);   // vertexCount
                    writer.Write((uint)indices.Length);      // indexCount
                    writer.Write(resolution);
                    writer.Write(padding);
                    writer.Write(rotateCharts);
                    writer.Write(rotateToAxis);
                    writer.Write(blockAlign);
                    writer.Write(bruteForce);
                    foreach (float f in flatUv) writer.Write(f);
                    foreach (uint i in indices) writer.Write(i);
                    foreach (uint m in faceMaterials) writer.Write(m);
                }
                // Only the newest capture is interesting; keep the directory small.
                var stale = new List<string>(System.IO.Directory.GetFiles(dir, "repack_*.bin"));
                stale.Sort(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < stale.Count - 5; ++i)
                    System.IO.File.Delete(stale[i]);
                UvtLog.Info("[Remesh] Chart merge re-pack inputs captured to " + path);
            }
            catch (Exception error)
            {
                UvtLog.Verbose("[Remesh] Chart merge input capture failed: " + error.Message);
            }
        }

        /// <summary>Maps the pack output back: exactly one consistent UV per input
        /// vertex, no orphans, and a chart count that still matches. The merged charts'
        /// tangents are rebuilt from the packed layout afterwards.</summary>
        static bool ReadPackedUv(RemeshNative.Geometry geometry, CancellationToken token, HashSet<int> mergedCharts)
        {
            token.ThrowIfCancellationRequested();
            if (XatlasNative.xatlasGetMeshCount() == 0) return false;
            if (XatlasNative.xatlasGetChartCount() != (uint)geometry.chartCount) return false;
            int outVerts = XatlasNative.xatlasGetOutputVertexCount(0);
            if (outVerts <= 0 || XatlasNative.xatlasGetOutputIndexCount(0) != geometry.indices.Length) return false;
            var xref = new uint[outVerts];
            var uv = new float[outVerts * 2];
            var chartIndex = new uint[outVerts];
            XatlasNative.xatlasGetOutputVertexData(0, xref, uv, chartIndex, outVerts);
            var packed = new Vector2[geometry.uv.Length];
            var assigned = new bool[packed.Length];
            for (int o = 0; o < outVerts; ++o)
            {
                if (chartIndex[o] == OrphanChart) return false;
                uint source = xref[o];
                if (source >= (uint)packed.Length) return false;
                var value = new Vector2(uv[o * 2], uv[o * 2 + 1]);
                // Bit-pattern compare on purpose: the same input vertex must carry one
                // identical UV. Unity's Vector2 == is epsilon-based and float ==/!=/Equals
                // is banned outright (S1244), so equality goes through the int bits; any
                // NaN is an anomaly that must revert rather than pass.
                if (assigned[source])
                {
                    if (float.IsNaN(value.x) || float.IsNaN(value.y) ||
                        BitConverter.SingleToInt32Bits(packed[source].x) != BitConverter.SingleToInt32Bits(value.x) ||
                        BitConverter.SingleToInt32Bits(packed[source].y) != BitConverter.SingleToInt32Bits(value.y)) return false;
                }
                else
                {
                    packed[source] = value;
                    assigned[source] = true;
                }
            }
            for (int i = 0; i < packed.Length; ++i) if (!assigned[i]) return false;
            // The bridge divides by the atlas dimensions, but the merge contract is a
            // [0,1] atlas — verify with a float-rounding slack and revert otherwise.
            for (int i = 0; i < packed.Length; ++i)
            {
                if (packed[i].x < -1e-4f || packed[i].x > 1f + 1e-4f ||
                    packed[i].y < -1e-4f || packed[i].y > 1f + 1e-4f) return false;
            }
            geometry.uv = packed;
            RebuildChartTangents(geometry, mergedCharts, token);
            return true;
        }

        /// <summary>Rebuilds the tangent frames of the merged charts from the final
        /// atlas layout: per-face Lengyel tangents, area-weighted accumulation, one
        /// orthonormal frame per vertex with handedness from the accumulated bitangent.
        /// Deriving from the final UVs is the only thing that stays correct through
        /// everything the merge did — the fit rotation, the seam snap's per-vertex
        /// displacement, and the packer's per-chart per-axis ceil stretch, which is not
        /// a similarity a rotated stale tangent could track. Degenerate faces leave the
        /// old tangent; untouched charts keep their native meshoptimizer frames.</summary>
        internal static void RebuildChartTangents(RemeshNative.Geometry g, HashSet<int> chartIds, CancellationToken token)
        {
            if (g.tangents == null || g.normals == null || chartIds.Count == 0) return;
            var tan = new Vector3[g.positions.Length];
            var bit = new Vector3[g.positions.Length];
            var touched = new bool[g.positions.Length];
            for (int f = 0; f < g.indices.Length; f += 3)
            {
                if ((f & 4095) == 0) token.ThrowIfCancellationRequested();
                // f walks index space in steps of three — no second ×3 here.
                int i0 = g.indices[f], i1 = g.indices[f + 1], i2 = g.indices[f + 2];
                if (!chartIds.Contains(g.charts[i0])) continue;
                Vector3 e1 = g.positions[i1] - g.positions[i0];
                Vector3 e2 = g.positions[i2] - g.positions[i0];
                float weight = Vector3.Cross(e1, e2).magnitude; // 2× face area
                if (weight <= 0f) continue;
                Vector2 d1 = g.uv[i1] - g.uv[i0];
                Vector2 d2 = g.uv[i2] - g.uv[i0];
                float det = d1.x * d2.y - d2.x * d1.y;
                if (Mathf.Abs(det) < 1e-12f) continue;
                // Solve [T|B] from Q = s·T + t·B per Lengyel. det carries the UV winding,
                // so a flipped face contributes an oppositely signed frame that cancels
                // out instead of corrupting the vertex.
                var faceT = (d2.y * e1 - d1.y * e2) / det * weight;
                var faceB = (d1.x * e2 - d2.x * e1) / det * weight;
                tan[i0] += faceT; bit[i0] += faceB; touched[i0] = true;
                tan[i1] += faceT; bit[i1] += faceB; touched[i1] = true;
                tan[i2] += faceT; bit[i2] += faceB; touched[i2] = true;
            }
            for (int v = 0; v < touched.Length; ++v)
            {
                if (!touched[v] || tan[v].sqrMagnitude < 1e-12f) continue;
                var n = g.normals[v];
                if (n.sqrMagnitude < 1e-12f) continue;
                var t = tan[v] - n * Vector3.Dot(n, tan[v]);
                if (t.sqrMagnitude < 1e-12f) continue;
                t = MeshGeometry.UnitDirection(t);
                float handedness = Vector3.Dot(bit[v], Vector3.Cross(n, t)) < 0f ? -1f : 1f;
                g.tangents[v] = new Vector4(t.x, t.y, t.z, handedness);
            }
        }

        // ── Small helpers ───────────────────────────────────────────────────

        static long EdgeKey(int slotA, int slotB)
            => slotA < slotB ? ((long)slotA << 32) | (uint)slotB : ((long)slotB << 32) | (uint)slotA;

        static float EdgeLength(Vector3[] positions, int[] slotVertex, long key)
        {
            int a = slotVertex[(int)(key >> 32)];
            int b = slotVertex[(int)(key & 0xFFFFFFFFL)];
            return (positions[a] - positions[b]).magnitude;
        }
    }
}
