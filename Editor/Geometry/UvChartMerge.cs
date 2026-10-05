// UvChartMerge.cs — deterministic chart merging after the Remesh unwrap (UV0).
//
// Greedy rounds over adjacent chart pairs. For a pair, an orientation-preserving
// similarity (Procrustes: rotation + uniform scale + translation after aligning chart winding) fits
// one chart's UVs onto its neighbour through the seam vertices. The seam is then
// snapped bit-exact onto the acceptor's UVs — xatlas reconnects charts by UV
// colocalization (faceMaterial only separates charts, it never joins them), so a
// 1e-6-close seam would come back as two islands after the re-pack. Because a
// similarity leaves per-triangle stretch untouched, the real distortion appears
// in that snap: acceptance therefore re-measures the stretch of the moved faces
// plus the acceptor's seam faces after snapping, and runs a full triangle-triangle
// overlap test (edge-edge crossings included, seam contact excluded). User island
// area/border limits are respected in source units. A guarded free-boundary
// conformal relax reduces distortion over the resulting charts, preserving seam
// colocalization and chart UV area. Merged charts are re-packed
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
        // Broader proposals are candidates only: relax/repack must still pass the
        // original global stretch gates and preserve the narrow atlas's packing.
        const float BroadSeamResidual = .1f;
        const float BroadLocalWorstStretch = 6f;
        const float StretchSlack = 1.1f;       // ...and never >10% worse than the same faces pre-merge
        const uint OrphanChart = 0xFFFFFFFFu;

        // ── Entry point ─────────────────────────────────────────────────────

        /// <summary>Merges adjacent charts and re-packs the atlas. Mutates `geometry`
        /// only when every gate passes; otherwise restores the pre-merge state.
        /// `preMerge` is the quality of the geometry exactly as passed in.</summary>
        internal static void Apply(RemeshNative.Geometry geometry, UvChartQuality preMerge, RemeshSettings settings, CancellationToken token)
        {
            if (geometry == null || settings == null || geometry.charts == null || geometry.uv == null ||
                geometry.indices == null || geometry.chartCount < 2) return;
            // Evaluate both strategies on separate buffers. Broad seam fits can join
            // curved patches that rigid matching excludes; relax must distribute the
            // temporary deformation before the unchanged final atlas gates accept it.
            var narrow = CloneCandidate(geometry);
            ApplyStrategy(narrow, preMerge, settings, token, MaxSeamResidual, MaxWorstStretch);
            var broad = CloneCandidate(geometry);
            ApplyStrategy(broad, preMerge, settings, token, BroadSeamResidual, BroadLocalWorstStretch);
            var narrowQuality = UvChartQuality.Measure(narrow, token);
            var broadQuality = UvChartQuality.Measure(broad, token);
            var narrowPacking = UvPackingQuality.Measure(narrow, token);
            var broadPacking = UvPackingQuality.Measure(broad, token);
            bool useBroad = broadQuality.Improves(narrowQuality, preMerge) && broadPacking.Preserves(narrowPacking);
            UvtLog.Info(UvtLog.Category.RemeshDiag, FormattableString.Invariant(
                $"[UV] merge-strategy: narrow charts={narrow.chartCount} fill={narrowPacking.filledArea:G6}; broad charts={broad.chartCount} fill={broadPacking.filledArea:G6}; selected={(useBroad ? "broad" : "narrow")}."));
            token.ThrowIfCancellationRequested();
            var best = useBroad ? broad : narrow;
            geometry.positions = best.positions; geometry.normals = best.normals; geometry.indices = best.indices;
            geometry.uv = best.uv; geometry.charts = best.charts; geometry.tangents = best.tangents;
            geometry.chartCount = best.chartCount; geometry.smallChartCount = best.smallChartCount;
            if (geometry.chartCount < preMerge.charts)
                UvtLog.Info($"[Remesh] Chart merge: {preMerge.charts} → {geometry.chartCount} islands; {(useBroad ? "broad" : "narrow")} seam strategy passed final gates.");
        }

        static RemeshNative.Geometry CloneCandidate(RemeshNative.Geometry g)
            => new RemeshNative.Geometry {
                positions = g.positions, normals = g.normals, indices = g.indices,
                uv = (Vector2[])g.uv.Clone(), charts = (int[])g.charts.Clone(),
                tangents = g.tangents == null ? null : (Vector4[])g.tangents.Clone(),
                chartCount = g.chartCount, smallChartCount = g.smallChartCount, draftUv = g.draftUv,
                originalChartCount = g.originalChartCount, originalSmallChartCount = g.originalSmallChartCount
            };

        static void ApplyStrategy(RemeshNative.Geometry geometry, UvChartQuality preMerge, RemeshSettings settings,
            CancellationToken token, float seamResidual, float localWorstStretch)
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
            var positionsSnapshot = geometry.positions;
            var normalsSnapshot = geometry.normals;
            var indicesSnapshot = geometry.indices;
            UvAtlasDiagnostics.Log(geometry, "merge-baseline", token);
            var packingBaseline = UvPackingQuality.Measure(geometry, token);
            try
            {
                int mergeLimit = int.MaxValue;
                int acceptedLimit = 0, rejectedLimit = int.MaxValue;
                RemeshNative.Geometry bestCandidate = null;
                // Packing is discontinuous: dropping half the joins and accepting
                // the first passing atlas leaves many avoidable seams. Keep every
                // passing checkpoint and probe nearer the rejected merge count.
                // Brute-force and high-resolution packing are already expensive.
                // Keep their original halving/first-success path; refinement is
                // bounded to the interactive 512px fast-packing case.
                bool refinePacking = !settings.packBruteForce && settings.textureResolution <= 512;
                int packingAttempts = refinePacking ? 6 : 32;
                for (int attempt = 0; attempt < packingAttempts; ++attempt)
                {
                    var mergedCharts = new HashSet<int>();
                    int merged = MergeChartsLimited(geometry, settings, token, mergedCharts, mergeLimit, seamResidual, localWorstStretch);
                    UvtLog.Info(UvtLog.Category.RemeshDiag,
                        $"[UV] merge-candidate: charts={chartCountSnapshot}->{geometry.chartCount}, acceptedMerges={merged}; candidate is not the final unwrap.");
                    if (merged <= 0) return;
                    var beforeRelax = UvChartQuality.Measure(geometry, token);
                    var relaxWatch = System.Diagnostics.Stopwatch.StartNew();
                    const int relaxIterations = 50;
                    var relaxCharts = new HashSet<int>(geometry.charts);
                    int relaxed = UvChartRelax.Apply(geometry, relaxCharts, relaxIterations, token);
                    var afterRelax = UvChartQuality.Measure(geometry, token);
                    UvtLog.Info(UvtLog.Category.RemeshDiag, FormattableString.Invariant(
                        $"[UV] merge-relax: relaxed={relaxed}/{relaxCharts.Count} charts, iterations={relaxIterations}, mean={beforeRelax.meanStretch:G6}->{afterRelax.meanStretch:G6} worst={beforeRelax.maxStretch:G6}->{afterRelax.maxStretch:G6}, elapsedMs={relaxWatch.Elapsed.TotalMilliseconds:F1}; repack and final gates follow."));
                    UvAtlasDiagnostics.Log(geometry, "merge-before-pack", token, sameChartOnly: true);
                    if (!Repack(geometry, settings, token))
                        throw new InvalidOperationException("the re-pack rejected the merged charts");
                    UvAtlasDiagnostics.Log(geometry, "merge-after-pack (candidate)", token);
                    var atlasCheck = UvAtlasDiagnostics.Measure(geometry, token);
                    if (!atlasCheck.complete || atlasCheck.pairs > 0 || atlasCheck.invalidFaces > 0 || atlasCheck.degenerateFaces > 0)
                        throw new InvalidOperationException("merged atlas is not overlap-free (pairs=" + atlasCheck.pairs + ", complete=" + atlasCheck.complete + ")");
                    var post = UvChartQuality.Measure(geometry, token);
                    UvtLog.Info(UvtLog.Category.RemeshDiag, FormattableString.Invariant(
                        $"[UV] merge-quality-gate: baseline charts={preMerge.charts} small={preMerge.smallCharts} mean={preMerge.meanStretch:G6} worst={preMerge.maxStretch:G6} valid={preMerge.valid}; candidate charts={post.charts} small={post.smallCharts} mean={post.meanStretch:G6} worst={post.maxStretch:G6} valid={post.valid}; meanLimit={Math.Max(1.15, preMerge.meanStretch * 1.1):G6} worstLimit={Math.Max(4, preMerge.maxStretch * 1.1):G6}; result={post.ImprovementFailure(preMerge, preMerge)}"));
                    bool preservesStretch = post.Improves(preMerge, preMerge);
                    var packing = UvPackingQuality.Measure(geometry, token);
                    bool preservesPacking = packing.Preserves(packingBaseline);
                    UvtLog.Info(UvtLog.Category.RemeshDiag, FormattableString.Invariant(
                        $"[UV] merge-packing-gate: uvArea={packingBaseline.filledArea:G6}->{packing.filledArea:G6}, chartDensityCV={packingBaseline.densityDeviation:G6}->{packing.densityDeviation:G6}, accepted={preservesPacking}, merges={merged}."));
                    if (!preservesPacking || !preservesStretch)
                    {
                        rejectedLimit = Math.Min(rejectedLimit, merged);
                        if (merged <= 1) break;
                        mergeLimit = !refinePacking ? merged / 2 : acceptedLimit > 0
                            ? acceptedLimit + (rejectedLimit - acceptedLimit) / 2
                            : Math.Max(1, merged * 3 / 4);
                    }
                    else
                    {
                        bestCandidate = CloneCandidate(geometry);
                        acceptedLimit = merged;
                        UvtLog.Info(UvtLog.Category.RemeshDiag, $"[UV] merge-checkpoint: {chartCountSnapshot} → {geometry.chartCount} islands ({merged} merges); retained while probing further joins.");
                        token.ThrowIfCancellationRequested();
                        if (!refinePacking || rejectedLimit == int.MaxValue || rejectedLimit - acceptedLimit <= 1) break;
                        mergeLimit = acceptedLimit + (rejectedLimit - acceptedLimit) / 2;
                    }
                    if (mergeLimit <= acceptedLimit || attempt + 1 == packingAttempts) break;
                    RestoreGeometry();
                    UvtLog.Info(UvtLog.Category.RemeshDiag, $"[UV] merge-packing-retry: merge budget={mergeLimit}; restarting from the baseline; accepted budget={acceptedLimit}, rejected budget={rejectedLimit}.");
                }
                RestoreGeometry();
                if (bestCandidate != null)
                {
                    CopyBuffers(bestCandidate, geometry);
                    UvtLog.Info(UvtLog.Category.RemeshDiag, $"[UV] merge-strategy-candidate: {chartCountSnapshot} → {geometry.chartCount} islands; best validated checkpoint selected.");
                }
            }
            catch (OperationCanceledException) { RestoreGeometry(); throw; }
            catch (Exception error)
            {
                RestoreGeometry();
                UvtLog.Warn("[Remesh] Chart merge candidate rejected; restoring its baseline before strategy selection. " + error.Message);
                UvtLog.Info(UvtLog.Category.RemeshDiag,
                    $"[UV] merge-rollback: restored original UV/chart/tangent arrays; final charts={chartCountSnapshot}, small={smallChartSnapshot}.");
            }
            void RestoreGeometry()
            {
                geometry.positions = positionsSnapshot; geometry.normals = normalsSnapshot; geometry.indices = indicesSnapshot;
                geometry.uv = (Vector2[])uvSnapshot.Clone(); geometry.charts = (int[])chartsSnapshot.Clone();
                geometry.tangents = tangentsSnapshot == null ? null : (Vector4[])tangentsSnapshot.Clone();
                geometry.chartCount = chartCountSnapshot; geometry.smallChartCount = smallChartSnapshot;
            }
        }

        static void CopyBuffers(RemeshNative.Geometry source, RemeshNative.Geometry destination)
        {
            destination.positions = source.positions; destination.normals = source.normals; destination.indices = source.indices;
            destination.uv = source.uv; destination.charts = source.charts; destination.tangents = source.tangents;
            destination.chartCount = source.chartCount; destination.smallChartCount = source.smallChartCount;
        }

        // ── Merge core (pure: no native calls, unit-testable) ───────────────

        /// <summary>Greedy merge rounds over adjacent chart pairs followed by id
        /// compaction. Mutates `geometry` (uv/charts/chartCount/smallChartCount)
        /// only through accepted merges; tangents are left for the post-pack rebuild.
        /// `mergedChartIds` collects the compacted ids of charts that absorbed or were
        /// absorbed. Returns the accepted merge count.</summary>
        internal static int MergeCharts(RemeshNative.Geometry geometry, RemeshSettings settings, CancellationToken token, HashSet<int> mergedChartIds)
            => MergeChartsLimited(geometry, settings, token, mergedChartIds, int.MaxValue, MaxSeamResidual, MaxWorstStretch);

        static int MergeChartsLimited(RemeshNative.Geometry geometry, RemeshSettings settings, CancellationToken token,
            HashSet<int> mergedChartIds, int mergeLimit, float seamResidual, float localWorstStretch)
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
            for (int round = 0; round < chartCount && merged < mergeLimit; ++round)
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
                // Prefer joins that remove a large fraction of the smaller chart's
                // border, rather than letting chart ids dictate long chains/fins.
                var seamCoverage = new Dictionary<(int, int), double>();
                foreach (var pair in pairs)
                {
                    double length = 0;
                    foreach (long edge in adjacency[pair]) length += EdgeLength(geometry.positions, slotVertex, edge);
                    double border = Math.Min(chartBoundary[pair.Item1], chartBoundary[pair.Item2]);
                    seamCoverage[pair] = border > 0 ? length / border : 0;
                }
                pairs.Sort((x, y) => {
                    int order = seamCoverage[y].CompareTo(seamCoverage[x]);
                    if (order != 0) return order;
                    return x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1) : x.Item2.CompareTo(y.Item2);
                });

                bool accepted = false;
                foreach (var pair in pairs)
                {
                    token.ThrowIfCancellationRequested();
                    if (TryMergePair(geometry, faceChart, slots, slotVertex, edgeFaces, pair.Item1, pair.Item2,
                            adjacency[pair], chartFaces, chartVerts, chartArea, chartBoundary, chartUvMin, chartUvMax,
                            settings, mergedChartIds, token, seamResidual, localWorstStretch))
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
            public bool mirror;
        }

        /// <summary>Evaluates both merge directions for one adjacent pair and applies
        /// the deterministic best one. The geometry is left untouched unless a merge
        /// is applied. Returns true when a merge was applied.</summary>
        static bool TryMergePair(RemeshNative.Geometry g, int[] faceChart, int[] slots, int[] slotVertex,
            Dictionary<long, List<int>> edgeFaces, int chartA, int chartB, List<long> seamKeys,
            List<int>[] chartFaces, List<int>[] chartVerts, float[] chartArea, float[] chartBoundary,
            Vector2[] chartUvMin, Vector2[] chartUvMax, RemeshSettings settings, HashSet<int> mergedChartIds,
            CancellationToken token, float seamResidual, float localWorstStretch)
        {
            // Only shared edges define the join. A coincident corner can be on
            // another side of an existing UV cut and must not be snapped closed.
            var seamSlots = new HashSet<int>();
            foreach (long key in seamKeys) { seamSlots.Add((int)(key >> 32)); seamSlots.Add((int)key); }
            if (!TryBuildSlotMap(g, chartVerts[chartA], slots, seamSlots, out var slotsA) ||
                !TryBuildSlotMap(g, chartVerts[chartB], slots, seamSlots, out var slotsB)) return false;
            var seamSlotList = new List<int>(seamSlots);
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
                        acceptor, mover, seamLength, settings, seamResidual, localWorstStretch, out var candidate) &&
                    (!haveBest || Better(candidate, best)))
                {
                    best = candidate;
                    haveBest = true;
                }
            }
            if (!haveBest) return false;

            // Re-apply the winning transform from the pristine UVs (evaluations restore).
            var moverVerts = chartVerts[best.mover];
            ApplySimilarity(g, moverVerts, best.cos, best.sin, best.scale, best.offset, best.mirror);
            SnapSeam(g, moverVerts, slots, best.acceptor == chartA ? slotsA : slotsB);
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
            RemeshSettings settings, float seamResidual, float localWorstStretch, out Candidate candidate)
        {
            candidate = default;
            if (seamSlots.Count < 2) return false;
            int acceptorSign = ChartWinding(g, chartFaces[acceptor]);
            int moverSign = ChartWinding(g, chartFaces[mover]);
            if (acceptorSign == 0 || moverSign == 0) return false;
            bool mirror = acceptorSign != moverSign;

            // ── Procrustes fit over the seam (orientation-preserving) ──
            var from = new Vector2[seamSlots.Count];
            var to = new Vector2[seamSlots.Count];
            for (int i = 0; i < seamSlots.Count; ++i)
            {
                from[i] = g.uv[moverSlotOf[seamSlots[i]]];
                if (mirror) from[i].x = -from[i].x;
                to[i] = g.uv[acceptorSlotOf[seamSlots[i]]];
            }
            if (!FitSimilarity(from, to, out float cos, out float sin, out float scale, out Vector2 offset, out float residual))
                return false;
            float normalize = Mathf.Max((chartUvMax[acceptor] - chartUvMin[acceptor]).magnitude, 1e-6f);
            if (scale < MinDensityScale || scale > MaxDensityScale) return false;           // texel-density gate
            if (residual > seamResidual * normalize) return false;                         // seam proposal gate
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

            var moverVerts = new List<int>();
            for (int v = 0; v < g.charts.Length; ++v) if (g.charts[v] == mover) moverVerts.Add(v);
            var backup = new Vector2[moverVerts.Count];
            for (int i = 0; i < moverVerts.Count; ++i) backup[i] = g.uv[moverVerts[i]];

            ApplySimilarity(g, moverVerts, cos, sin, scale, offset, mirror);
            SnapSeam(g, moverVerts, slots, acceptorSlotOf);

            bool passes = true;
            for (int i = 0; i < localFaces.Count && passes; ++i)
            {
                int postSign = UvWindingSign(g, localFaces[i]);
                if (postSign != acceptorSign) passes = false; // the entire joined chart must have one winding
            }
            if (passes)
            {
                MeasureStretch(g, localFaces, out double postMean, out var postWorst);
                if (double.IsNaN(postMean) || double.IsNaN(postWorst) ||
                    postMean > Math.Max(MaxMeanStretch, preMean * StretchSlack) ||
                    postWorst > Math.Max(localWorstStretch, preWorst * StretchSlack))
                    passes = false;
            }
            if (passes && !OverlapFree(g, chartFaces[acceptor], moverFaces)) passes = false;
            // The snap is not a similarity: it moves individual boundary vertices, so a
            // tightly folded mover can overlap itself without flipping a face or
            // tripping the stretch gate. Shared edges and corners stay
            // legal only when the triangles intersect with zero area.
            if (passes && !OverlapFree(g, moverFaces, moverFaces)) passes = false;

            for (int i = 0; i < moverVerts.Count; ++i) g.uv[moverVerts[i]] = backup[i]; // restore, bit-exact
            if (!passes) return false;
            candidate = new Candidate {
                residual = residual, movedVertices = moverVerts.Count, acceptor = acceptor, mover = mover,
                cos = cos, sin = sin, scale = scale, offset = offset, mirror = mirror,
            };
            return true;
        }

        static void ApplySimilarity(RemeshNative.Geometry g, List<int> vertices, float cos, float sin, float scale, Vector2 offset, bool mirror)
        {
            foreach (int v in vertices)
            {
                var uv = g.uv[v];
                if (mirror) uv.x = -uv.x;
                g.uv[v] = scale * new Vector2(cos * uv.x - sin * uv.y, sin * uv.x + cos * uv.y) + offset;
            }
        }

        static bool TryBuildSlotMap(RemeshNative.Geometry g, List<int> vertices, int[] slots,
            HashSet<int> seamSlots, out Dictionary<int, int> map)
        {
            map = new Dictionary<int, int>(vertices.Count);
            foreach (int v in vertices)
            {
                if (!seamSlots.Contains(slots[v])) continue;
                if (map.TryGetValue(slots[v], out int previous))
                {
                    // Ambiguity on the proposed seam is unsafe. Cuts elsewhere
                    // remain independent; tangent copies on this seam snap together.
                    if (BitConverter.SingleToInt32Bits(g.uv[v].x) != BitConverter.SingleToInt32Bits(g.uv[previous].x) ||
                        BitConverter.SingleToInt32Bits(g.uv[v].y) != BitConverter.SingleToInt32Bits(g.uv[previous].y)) return false;
                }
                else map.Add(slots[v], v);
            }
            return true;
        }

        // Snap every tangent/normal duplicate, not just one representative per position.
        static void SnapSeam(RemeshNative.Geometry g, List<int> moverVertices, int[] slots, Dictionary<int, int> acceptorSlots)
        {
            foreach (int v in moverVertices)
                if (acceptorSlots.TryGetValue(slots[v], out int anchor)) g.uv[v] = g.uv[anchor];
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
        /// the mover with positive area. X-sorted sweep broad phase followed by
        /// double-precision polygon clipping. Shared edges/corners are allowed only
        /// when their intersection has zero area.</summary>
        static bool OverlapFree(RemeshNative.Geometry g, List<int> facesA, List<int> facesB)
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

            var intersection = new UvAtlasDiagnostics.IntersectionTest();
            bool sameFaces = ReferenceEquals(facesA, facesB);
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
                    int fa = facesA[aPos], fb = facesB[bPos];
                    if (sameFaces && fa >= fb) continue;
                    if (intersection.Overlaps(g, fa, fb)) return false;
                }
            }
            return true;
        }

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

        static int ChartWinding(RemeshNative.Geometry g, List<int> faces)
        {
            int sign = 0;
            foreach (int f in faces)
            {
                int current = UvWindingSign(g, f);
                if (current == 0 || sign != 0 && current != sign) return 0;
                sign = current;
            }
            return sign;
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
        /// re-packs. Reads complete output buffers and rebuilds every chart's tangent
        /// frame from the final layout. False — with the geometry untouched — on any
        /// anomaly (busy session, refused pack, unexpected output shape).</summary>
        internal static bool Repack(RemeshNative.Geometry geometry, RemeshSettings settings, CancellationToken token)
            => RepackWithPrecision(geometry, settings, token, 4);

        const long RepackCostBudget = 20_000_000_000L;

        // The largest safe retry can be between the nominal 8/16/32 stages.
        // Select it before opening a native session, rather than rejecting an
        // oversized 32x request when useful higher precision still fits.
        internal static int HighPrecisionLimit(int chartCount, int resolution)
        {
            if (chartCount <= 0 || resolution <= 0) return 0;
            int limit = Math.Min(32, 16384 / resolution);
            while (limit > 4) {
                long internalResolution = (long)resolution * limit;
                if (chartCount * internalResolution * internalResolution <= RepackCostBudget) return limit;
                --limit;
            }
            return 0;
        }

        internal static bool RepackHighPrecision(RemeshNative.Geometry geometry, RemeshSettings settings, CancellationToken token,
            int requestedOversample = 32)
        {
            int oversample = Math.Min(requestedOversample, HighPrecisionLimit(geometry.chartCount, settings.textureResolution));
            return oversample > 4 && RepackWithPrecision(geometry, settings, token, oversample);
        }

        internal static bool RepackForRepair(RemeshNative.Geometry geometry, RemeshSettings settings, CancellationToken token,
            int oversample = 4)
        {
            if (oversample > 4)
                oversample = Math.Min(oversample, HighPrecisionLimit(geometry.chartCount, settings.textureResolution));
            return oversample >= 4 && RepackWithPrecision(geometry, settings, token, oversample, waitForSession: true);
        }

        static bool RepackWithPrecision(RemeshNative.Geometry geometry, RemeshSettings settings, CancellationToken token,
            int oversample, bool waitForSession = false)
        {
            token.ThrowIfCancellationRequested();
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
            // place a rotated chart anywhere in the plane. UvMesh cannot see 3D area:
            // restore each chart's area density before the global [0,1] normalization.
            var densityUv = UvPackingQuality.NormalizeChartAreas(geometry, token);
            Vector2 min = densityUv[0], max = densityUv[0];
            foreach (var p in densityUv)
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
                flatUv[i * 2] = (densityUv[i].x - min.x) * normalize;
                flatUv[i * 2 + 1] = (densityUv[i].y - min.y) * normalize;
            }
            var indices = new uint[geometry.indices.Length];
            for (int i = 0; i < geometry.indices.Length; ++i) indices[i] = (uint)geometry.indices[i];
            var faceMaterials = new uint[faceCount];
            for (int f = 0; f < faceCount; ++f) faceMaterials[f] = (uint)faceChart[f];

            bool waited = false;
            if (!XatlasRepack.TryAcquireNativeSession())
            {
                if (!waitForSession) {
                    UvtLog.Warn("[Remesh] Chart merge skipped: an xatlas repack session is already in progress.");
                    return false;
                }
                UvtLog.Info(UvtLog.Category.RemeshDiag, "[UV] repair-packing-wait: waiting for the active xatlas repack session; cancellation remains available.");
                XatlasRepack.AcquireNativeSession(token);
                waited = true;
            }
            try
            {
                token.ThrowIfCancellationRequested();
                if (waited) UvtLog.Info(UvtLog.Category.RemeshDiag, "[UV] repair-packing-resumed: acquired the xatlas repack session.");
                int rotate = settings.packRotate ? 1 : 0;
                if (PackAndRead(geometry, settings, flatUv, indices, faceMaterials, rotate, rotate, token, oversample)) return true;
                // Both rotation flags are separate xatlas knobs; a rotate placement is the
                // one way a per-input-vertex UV can come back ambiguous. Fresh session —
                // a second PackCharts on a packed atlas is not a defined state.
                token.ThrowIfCancellationRequested();
                if (PackAndRead(geometry, settings, flatUv, indices, faceMaterials, 0, 0, token, oversample)) return true;
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
            uint[] faceMaterials, int rotateCharts, int rotateToAxis, CancellationToken token, int oversample)
        {
            // Usually pack at 4× the user-facing resolution, like XatlasRepack;
            // overlap repair can retry at higher precision. xatlas
            // ceil-rounds each chart's extents to texel dimensions, so a layout whose
            // charts sum near the full atlas size cannot fit — the packer then places
            // charts at negative coordinates and its texcoord assert aborts the editor
            // (xatlas.cpp:8598). The oversample turns the rounding into a fraction of
            // the internal atlas; the bridge normalizes the output back to [0,1], so
            // the user-facing resolution is unchanged. Padding scales with it to keep
            // the gap fraction in UV space constant.
            uint internalRes = (uint)settings.textureResolution * (uint)oversample;
            uint internalPad = (uint)settings.padding * (uint)oversample;
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
                if ((long)geometry.chartCount * internalRes * internalRes > RepackCostBudget)
                {
                    UvtLog.Warn("[Remesh] Chart merge re-pack refused: the pack cost is past the safety budget.");
                    return false;
                }
                token.ThrowIfCancellationRequested();
                XatlasNative.xatlasComputeCharts();
                XatlasNative.xatlasPackCharts(0, internalPad, 0f, internalRes, 1,
                    settings.packBlockAlign ? 1 : 0, settings.packBruteForce ? 1 : 0, rotateCharts, rotateToAxis);
                return ReadPackedUv(geometry, token);
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
                    "repack_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".bin");
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

        /// <summary>Read the complete native mesh, including vertices split across charts.
        /// Verify every output corner maps to the original source corner before replacing
        /// any buffer. Rebuild ALL tangents: packing rotates untouched charts too.</summary>
        static bool ReadPackedUv(RemeshNative.Geometry geometry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (XatlasNative.xatlasGetMeshCount() != 1) return false;
            int outVerts = XatlasNative.xatlasGetOutputVertexCount(0);
            if (outVerts <= 0 || XatlasNative.xatlasGetOutputIndexCount(0) != geometry.indices.Length) return false;
            uint width = XatlasNative.xatlasGetAtlasWidth(), height = XatlasNative.xatlasGetAtlasHeight();
            if (width == 0 || height == 0) return false;
            // The bridge divides texel coordinates by width/height independently.
            // Embed the rectangular pack into a square texture with one scale;
            // otherwise even a rigidly packed triangle acquires anisotropic stretch.
            float atlasExtent = Math.Max(width, height);
            float uScale = width / atlasExtent, vScale = height / atlasExtent;
            var xref = new uint[outVerts]; var uv = new float[outVerts * 2]; var chartIndex = new uint[outVerts];
            if (XatlasNative.xatlasGetOutputVertexData(0, xref, uv, chartIndex, outVerts) != outVerts) return false;
            var nativeIndices = new uint[geometry.indices.Length];
            if (XatlasNative.xatlasGetOutputIndices(0, nativeIndices, nativeIndices.Length) != nativeIndices.Length) return false;
            var indices = new int[nativeIndices.Length];
            for (int i = 0; i < indices.Length; ++i)
            {
                uint vertex = nativeIndices[i];
                if (vertex >= (uint)outVerts || xref[vertex] != (uint)geometry.indices[i]) return false;
                indices[i] = (int)vertex;
            }
            var positions = new Vector3[outVerts];
            var normals = geometry.normals == null ? null : new Vector3[outVerts];
            var tangents = geometry.tangents == null ? null : new Vector4[outVerts];
            var packed = new Vector2[outVerts]; var charts = new int[outVerts];
            var ids = new Dictionary<uint, int>();
            for (int o = 0; o < outVerts; ++o)
            {
                if (chartIndex[o] == OrphanChart || xref[o] >= (uint)geometry.positions.Length) return false;
                uint source = xref[o];
                var value = new Vector2(uv[o * 2] * uScale, uv[o * 2 + 1] * vScale);
                if (float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsInfinity(value.x) || float.IsInfinity(value.y) ||
                    value.x < -1e-4f || value.x > 1.0001f || value.y < -1e-4f || value.y > 1.0001f) return false;
                if (!ids.TryGetValue(chartIndex[o], out int id)) {
                    id = ids.Count;
                    ids.Add(chartIndex[o], id);
                }
                charts[o] = id; packed[o] = value; positions[o] = geometry.positions[source];
                if (normals != null) normals[o] = geometry.normals[source];
                if (tangents != null) tangents[o] = geometry.tangents[source];
            }
            if ((uint)ids.Count != XatlasNative.xatlasGetChartCount()) return false;
            // Native UVs may split a disconnected chart into several islands. Count
            // the actual output IDs rather than rejecting a valid seam split.
            geometry.positions = positions; geometry.normals = normals; geometry.tangents = tangents;
            geometry.indices = indices; geometry.uv = packed; geometry.charts = charts; geometry.chartCount = ids.Count;
            var quality = UvChartQuality.Measure(geometry, token);
            geometry.smallChartCount = quality.smallCharts;
            RebuildChartTangents(geometry, new HashSet<int>(charts), token);
            return true;
        }

        /// <summary>Rebuilds the tangent frames of the merged charts from the final
        /// atlas layout: per-face Lengyel tangents, area-weighted accumulation, one
        /// orthonormal frame per vertex with handedness from the accumulated bitangent.
        /// Deriving from the final UVs is the only thing that stays correct through
        /// everything the merge did — the fit rotation, the seam snap's per-vertex
        /// displacement, and the packer's per-chart per-axis ceil stretch, which is not
        /// a similarity a rotated stale tangent could track. Degenerate faces leave the
        /// old tangent. Repack callers rebuild every output chart.</summary>
        internal static void RebuildChartTangents(RemeshNative.Geometry g, HashSet<int> chartIds, CancellationToken token)
        {
            if (g.tangents == null || g.normals == null || chartIds.Count == 0 || g.positions.Length == 0) return;
            var tan = new Vector3[g.positions.Length];
            var bit = new Vector3[g.positions.Length];
            var touched = new bool[g.positions.Length];
            var minimum = g.positions[0]; var maximum = minimum;
            foreach (var p in g.positions) { minimum = Vector3.Min(minimum, p); maximum = Vector3.Max(maximum, p); }
            double extent = Math.Max((double)maximum.x - minimum.x,
                Math.Max((double)maximum.y - minimum.y, (double)maximum.z - minimum.z));
            if (!(extent > 0) || double.IsInfinity(extent)) return;
            for (int f = 0; f < g.indices.Length; f += 3)
            {
                if ((f & 4095) == 0) token.ThrowIfCancellationRequested();
                // f walks index space in steps of three — no second ×3 here.
                int i0 = g.indices[f], i1 = g.indices[f + 1], i2 = g.indices[f + 2];
                if (!chartIds.Contains(g.charts[i0])) continue;
                // Scale before taking the area: its magnitude must not decide
                // whether a millimetre mesh keeps stale pre-merge tangents.
                Vector3 e1 = ScaledEdge(g.positions[i0], g.positions[i1], extent);
                Vector3 e2 = ScaledEdge(g.positions[i0], g.positions[i2], extent);
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
            // A removed UV seam can retain separate vertex indices. Identical
            // final position/normal/UV copies need the same accumulated frame,
            // otherwise texture filtering crosses an artificial tangent cut.
            var groups = new Dictionary<(Vector3, Vector3, Vector2, int), (Vector3 tangent, Vector3 bitangent)>();
            for (int v = 0; v < touched.Length; ++v) {
                if ((v & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!touched[v]) continue;
                var key = (g.positions[v], g.normals[v], g.uv[v], g.charts[v]);
                groups.TryGetValue(key, out var sum);
                groups[key] = (sum.tangent + tan[v], sum.bitangent + bit[v]);
            }
            for (int v = 0; v < touched.Length; ++v)
            {
                if ((v & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!touched[v]) continue;
                var sum = groups[(g.positions[v], g.normals[v], g.uv[v], g.charts[v])];
                var n = TangentDirection(g.normals[v]); var direction = TangentDirection(sum.tangent);
                if (n == Vector3.zero || direction == Vector3.zero) continue;
                var t = TangentDirection(direction - n * Vector3.Dot(n, direction));
                if (t == Vector3.zero) continue;
                float handedness = Vector3.Dot(TangentDirection(sum.bitangent), Vector3.Cross(n, t)) < 0f ? -1f : 1f;
                g.tangents[v] = new Vector4(t.x, t.y, t.z, handedness);
            }
        }

        static Vector3 ScaledEdge(Vector3 a, Vector3 b, double extent) => new Vector3(
            (float)(((double)b.x - a.x) / extent), (float)(((double)b.y - a.y) / extent), (float)(((double)b.z - a.z) / extent));

        static Vector3 TangentDirection(Vector3 value)
        {
            float scale = Mathf.Max(Mathf.Abs(value.x), Mathf.Max(Mathf.Abs(value.y), Mathf.Abs(value.z)));
            return scale > 0 && float.IsFinite(scale) ? MeshGeometry.UnitDirection(value / scale) : Vector3.zero;
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
