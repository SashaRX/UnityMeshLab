using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Guarded free-boundary distortion proposals for joined UV charts. Works on
    /// compact UV topology so seam/tangent copies remain exactly colocal. Does not
    /// change source geometry or charts; packing and final atlas validation follow.</summary>
    internal static class UvChartRelax
    {
        internal static int Apply(RemeshNative.Geometry geometry, HashSet<int> chartIds, int iterations, CancellationToken token, bool arap = false)
        {
            token.ThrowIfCancellationRequested();
            if (iterations <= 0 || chartIds.Count == 0) return 0;
            // Commit all chart changes together. Cancellation cannot publish a
            // half-relaxed atlas to the caller.
            var working = new RemeshNative.Geometry {
                positions = geometry.positions, uv = (Vector2[])geometry.uv.Clone(),
                indices = geometry.indices, charts = geometry.charts, chartCount = geometry.chartCount
            };
            var slots = MeshGeometry.WeldPositions(geometry.positions, out _);
            var faces = new List<int>[geometry.chartCount];
            for (int f = 0; f < geometry.indices.Length / 3; ++f)
            {
                int chart = geometry.charts[geometry.indices[f * 3]];
                if (!chartIds.Contains(chart)) continue;
                if (faces[chart] == null) faces[chart] = new List<int>();
                faces[chart].Add(f);
            }
            int accepted = 0;
            for (int chart = 0; chart < faces.Length; ++chart)
            {
                token.ThrowIfCancellationRequested();
                if (faces[chart] != null && RelaxChart(working, faces[chart], slots, iterations, token, arap)) ++accepted;
            }
            token.ThrowIfCancellationRequested();
            if (accepted > 0) Array.Copy(working.uv, geometry.uv, geometry.uv.Length);
            return accepted;
        }

        static bool RelaxChart(RemeshNative.Geometry source, List<int> faces, int[] slots, int iterations, CancellationToken token, bool arap)
        {
            // Distinct UV cuts at one 3D position must remain distinct. Only copies
            // with the same welded position AND bit-identical UV share an unknown.
            var vertices = new SortedSet<int>();
            foreach (int face in faces)
                for (int k = 0; k < 3; ++k) vertices.Add(source.indices[face * 3 + k]);
            var keys = new Dictionary<(int, int, int), int>();
            var uvAtSlot = new Dictionary<int, Vector2>();
            var localOf = new Dictionary<int, int>();
            var positions = new List<Vector3>();
            var uv = new List<Vector2>();
            foreach (int v in vertices)
            {
                var key = (slots[v], BitConverter.SingleToInt32Bits(source.uv[v].x), BitConverter.SingleToInt32Bits(source.uv[v].y));
                // The merge core never joins ambiguous existing cuts. A standalone
                // caller must not accidentally close one through relaxation either.
                if (uvAtSlot.TryGetValue(slots[v], out var previous) &&
                    (BitConverter.SingleToInt32Bits(previous.x) != key.Item2 || BitConverter.SingleToInt32Bits(previous.y) != key.Item3)) return false;
                uvAtSlot[slots[v]] = source.uv[v];
                if (!keys.TryGetValue(key, out int local))
                {
                    local = positions.Count; keys.Add(key, local);
                    positions.Add(source.positions[v]); uv.Add(source.uv[v]);
                }
                localOf.Add(v, local);
            }
            var indices = new int[faces.Count * 3];
            for (int f = 0; f < faces.Count; ++f)
                for (int k = 0; k < 3; ++k) indices[f * 3 + k] = localOf[source.indices[faces[f] * 3 + k]];
            if (!ConnectedOpenChart(indices, positions.Count)) return false;
            var localGeometry = new RemeshNative.Geometry {
                positions = positions.ToArray(), uv = uv.ToArray(), indices = indices,
                charts = new int[positions.Count], chartCount = 1
            };
            var before = UvChartQuality.Measure(localGeometry, token);
            if (!before.valid || before.maxStretch < 1.001) return false;
            var original = (Vector2[])localGeometry.uv.Clone();
            double winding = SignedArea(original, indices);
            List<Vector2[]> proposals;
            if (!arap)
            {
                if (!UvDistortionOptimizer.Optimize(localGeometry.positions, indices, original, iterations, token, out proposals)) return false;
            }
            else
            {
                if (!ArapProposal(localGeometry, iterations, winding, token, out var arapProposal)) return false;
                proposals = new List<Vector2[]> { arapProposal };
            }
            var best = before;
            Vector2[] bestUv = null;
            // A proposal is not a global injectivity guarantee. Backtracking permits
            // a useful smaller step; each endpoint needs its own full overlap scan.
            foreach (var proposal in proposals)
            {
                if (!AlignAndPreserveArea(proposal, original, indices)) continue;
                for (float amount = 1; amount >= 1f / 32; amount *= .5f)
                {
                    token.ThrowIfCancellationRequested();
                    for (int i = 0; i < original.Length; ++i)
                        localGeometry.uv[i] = Vector2.LerpUnclamped(original[i], proposal[i], amount);
                    var quality = UvChartQuality.Measure(localGeometry, token);
                    if (!quality.valid || SignedArea(localGeometry.uv, indices) * winding <= 0 ||
                        quality.meanStretch > before.meanStretch * (1 + 1e-6) ||
                        quality.maxStretch > before.maxStretch * (1 + 1e-6)) continue;
                    bool improves = quality.meanStretch < best.meanStretch - 1e-5 && quality.maxStretch <= best.maxStretch * (1 + 1e-6) ||
                        quality.maxStretch < best.maxStretch - 1e-4 && quality.meanStretch <= best.meanStretch * (1 + 1e-6);
                    if (!improves) continue;
                    var scan = UvAtlasDiagnostics.Measure(localGeometry, token);
                    if (!scan.complete || scan.pairs != 0 || scan.invalidFaces != 0 || scan.degenerateFaces != 0) continue;
                    best = quality; bestUv = (Vector2[])localGeometry.uv.Clone();
                }
            }
            if (bestUv == null) return false;
            // A blend may alter chart area; restore exact total UV area uniformly.
            // Similarity does not change conformal stretch or intersection topology.
            if (!AlignAndPreserveArea(bestUv, original, indices)) return false;
            localGeometry.uv = bestUv;
            var finalQuality = UvChartQuality.Measure(localGeometry, token);
            var finalScan = UvAtlasDiagnostics.Measure(localGeometry, token);
            if (!finalQuality.valid || finalQuality.meanStretch > before.meanStretch * (1 + 1e-6) ||
                finalQuality.maxStretch > before.maxStretch * (1 + 1e-6) || !finalScan.complete || finalScan.pairs != 0 ||
                finalScan.invalidFaces != 0 || finalScan.degenerateFaces != 0) return false;
            // WeldPositions has a positional tolerance. Also measure the original
            // corner positions, rather than only the compact representatives.
            if (!OriginalCornersImprove(source, faces, vertices, localOf, bestUv, token)) return false;
            foreach (int v in vertices) source.uv[v] = bestUv[localOf[v]];
            return true;
        }

        static bool OriginalCornersImprove(RemeshNative.Geometry source, List<int> faces, SortedSet<int> vertices,
            Dictionary<int, int> localOf, Vector2[] bestUv, CancellationToken token)
        {
            var expandedOf = new Dictionary<int, int>();
            var positions = new Vector3[vertices.Count]; var original = new Vector2[vertices.Count]; var candidate = new Vector2[vertices.Count];
            foreach (int v in vertices)
            {
                int i = expandedOf.Count; expandedOf.Add(v, i);
                positions[i] = source.positions[v]; original[i] = source.uv[v]; candidate[i] = bestUv[localOf[v]];
            }
            var indices = new int[faces.Count * 3];
            for (int f = 0; f < faces.Count; ++f)
                for (int k = 0; k < 3; ++k) indices[f * 3 + k] = expandedOf[source.indices[faces[f] * 3 + k]];
            var g = new RemeshNative.Geometry { positions = positions, indices = indices, uv = original, charts = new int[positions.Length], chartCount = 1 };
            var before = UvChartQuality.Measure(g, token); g.uv = candidate;
            var after = UvChartQuality.Measure(g, token);
            return before.valid && after.valid && after.meanStretch <= before.meanStretch * (1 + 1e-6) && after.maxStretch <= before.maxStretch * (1 + 1e-6);
        }

        static bool ArapProposal(RemeshNative.Geometry geometry, int iterations, double winding, CancellationToken token, out Vector2[] proposal)
        {
            proposal = null;
            var flat = new float[geometry.uv.Length * 2];
            for (int i = 0; i < geometry.uv.Length; ++i)
            {
                flat[i * 2] = geometry.uv[i].x; flat[i * 2 + 1] = winding < 0 ? -geometry.uv[i].y : geometry.uv[i].y;
            }
            var allFaces = new int[geometry.indices.Length / 3]; var allVertices = new int[geometry.uv.Length];
            for (int i = 0; i < allFaces.Length; ++i) allFaces[i] = i;
            for (int i = 0; i < allVertices.Length; ++i) allVertices[i] = i;
            token.ThrowIfCancellationRequested();
            if (!ArapParameterization.Reparameterize(geometry.positions, geometry.indices, allFaces, allVertices, flat, iterations, out _)) return false;
            token.ThrowIfCancellationRequested();
            proposal = new Vector2[geometry.uv.Length];
            for (int i = 0; i < proposal.Length; ++i)
                proposal[i] = new Vector2(flat[i * 2], winding < 0 ? -flat[i * 2 + 1] : flat[i * 2 + 1]);
            return true;
        }

        static bool AlignAndPreserveArea(Vector2[] proposal, Vector2[] original, int[] indices)
        {
            double oldArea = SignedArea(original, indices), newArea = SignedArea(proposal, indices);
            if (!(oldArea * newArea > 0)) return false;
            double areaScale = Math.Sqrt(oldArea / newArea);
            if (double.IsNaN(areaScale) || double.IsInfinity(areaScale)) return false;
            if (!UvChartMerge.FitSimilarity(proposal, original, out float cos, out float sin, out _, out _, out _)) return false;
            Vector2 from = Vector2.zero, to = Vector2.zero;
            for (int i = 0; i < proposal.Length; ++i) { from += proposal[i]; to += original[i]; }
            from /= proposal.Length; to /= proposal.Length;
            for (int i = 0; i < proposal.Length; ++i)
            {
                Vector2 p = proposal[i] - from;
                proposal[i] = to + (float)areaScale * new Vector2(cos * p.x - sin * p.y, sin * p.x + cos * p.y);
            }
            return true;
        }

        static double SignedArea(Vector2[] uv, int[] indices)
        {
            double sum = 0;
            for (int f = 0; f < indices.Length; f += 3)
            {
                var a = uv[indices[f]]; var b = uv[indices[f + 1]]; var c = uv[indices[f + 2]];
                sum += ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);
            }
            return sum * .5;
        }

        static bool ConnectedOpenChart(int[] indices, int count)
        {
            var edges = new Dictionary<(int, int), int>();
            var adjacent = new List<int>[count];
            for (int i = 0; i < count; ++i) adjacent[i] = new List<int>();
            for (int f = 0; f < indices.Length; f += 3)
                for (int k = 0; k < 3; ++k)
                {
                    int a = indices[f + k], b = indices[f + (k + 1) % 3];
                    if (a == b) return false;
                    var edge = a < b ? (a, b) : (b, a);
                    edges.TryGetValue(edge, out int incidences);
                    if (incidences == 2) return false;
                    edges[edge] = incidences + 1;
                    adjacent[a].Add(b); adjacent[b].Add(a);
                }
            bool boundary = false;
            foreach (int incidences in edges.Values) if (incidences == 1) boundary = true;
            if (!boundary) return false;
            var seen = new bool[count]; var pending = new Queue<int>();
            seen[0] = true; pending.Enqueue(0); int reached = 1;
            while (pending.Count > 0)
                foreach (int v in adjacent[pending.Dequeue()])
                    if (!seen[v]) { seen[v] = true; ++reached; pending.Enqueue(v); }
            return reached == count;
        }
    }
}
