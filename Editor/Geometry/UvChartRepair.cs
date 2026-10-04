using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Cut conflicting UV faces into independent charts, preserving each
    /// triangle's authored UV shape and all source corners. Packing removes their
    /// former overlaps. No vertex is shared across the new chart boundaries.</summary>
    internal static class UvChartRepair
    {
        internal static RemeshNative.Geometry Apply(RemeshNative.Geometry geometry, RemeshSettings settings, CancellationToken token)
        {
            var report = UvAtlasDiagnostics.Measure(geometry, token, collectConflicts: true);
            if (!report.complete || report.invalidFaces > 0 || report.degenerateFaces > 0)
                throw new InvalidOperationException("UV atlas cannot be certified: incomplete scan or invalid/degenerate UV faces.");
            if (report.pairs == 0) return geometry;
            var quality = UvChartQuality.Measure(geometry, token);
            var repaired = SplitConflicts(geometry, report.conflicts, token);
            UvtLog.Info(UvtLog.Category.RemeshDiag,
                $"[UV] repair-cuts: baseline charts={geometry.chartCount}, overlapPairs={report.pairs}; cut charts={repaired.chartCount}. UV triangle shapes preserved.");
            if (!UvChartMerge.Repack(repaired, settings, token))
                throw new InvalidOperationException("UV overlap repair could not pack its seam cuts safely.");
            var check = UvAtlasDiagnostics.Measure(repaired, token);
            var post = UvChartQuality.Measure(repaired, token);
            UvAtlasDiagnostics.Log(repaired, "repair-after-pack (candidate)", token);
            if (!check.complete || check.pairs > 0 || !post.valid ||
                post.meanStretch > Math.Max(1.15, quality.meanStretch * 1.1) ||
                post.maxStretch > Math.Max(4, quality.maxStretch * 1.1))
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"UV overlap repair rejected: pairs={check.pairs}, complete={check.complete}, valid={post.valid}, mean={post.meanStretch:G6} (limit {Math.Max(1.15, quality.meanStretch * 1.1):G6}), worst={post.maxStretch:G6} (limit {Math.Max(4, quality.maxStretch * 1.1):G6})."));
            UvAtlasDiagnostics.Log(repaired, "repair-final", token);
            return repaired;
        }

        /// <summary>Deterministic greedy vertex cover of the overlap graph. Each
        /// conflicting pair loses at least one face to a separate chart; clean chart
        /// interiors remain together. Input arrays are never modified.</summary>
        internal static RemeshNative.Geometry SplitConflicts(RemeshNative.Geometry g,
            List<(int a, int b)> conflicts, CancellationToken token)
        {
            var pending = new List<(int a, int b)>(conflicts);
            var cuts = new HashSet<int>();
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var degrees = new Dictionary<int, int>();
                foreach (var pair in pending) {
                    degrees.TryGetValue(pair.a, out int a); degrees[pair.a] = a + 1;
                    degrees.TryGetValue(pair.b, out int b); degrees[pair.b] = b + 1;
                }
                int best = -1, most = 0;
                foreach (var pair in degrees)
                    if (pair.Value > most || pair.Value == most && (best < 0 || pair.Key < best)) {
                        best = pair.Key; most = pair.Value;
                    }
                cuts.Add(best);
                pending.RemoveAll(p => p.a == best || p.b == best);
            }
            var faceCharts = new int[g.indices.Length / 3];
            int chartCount = g.chartCount;
            for (int f = 0; f < faceCharts.Length; ++f)
                faceCharts[f] = cuts.Contains(f) ? chartCount++ : g.charts[g.indices[f * 3]];
            var remap = new Dictionary<(int vertex, int chart), int>();
            var positions = new List<Vector3>(); var uv = new List<Vector2>(); var charts = new List<int>();
            var normals = g.normals == null ? null : new List<Vector3>();
            var tangents = g.tangents == null ? null : new List<Vector4>();
            var indices = new int[g.indices.Length];
            for (int i = 0; i < indices.Length; ++i)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                int v = g.indices[i], chart = faceCharts[i / 3];
                if (!remap.TryGetValue((v, chart), out int mapped)) {
                    mapped = positions.Count; remap.Add((v, chart), mapped);
                    positions.Add(g.positions[v]); uv.Add(g.uv[v]); charts.Add(chart);
                    normals?.Add(g.normals[v]); tangents?.Add(g.tangents[v]);
                }
                indices[i] = mapped;
            }
            // Removed charts can leave holes; compact in source-corner order.
            var ids = new Dictionary<int, int>();
            for (int v = 0; v < charts.Count; ++v) {
                if (!ids.TryGetValue(charts[v], out int compact)) {
                    compact = ids.Count;
                    ids.Add(charts[v], compact);
                }
                charts[v] = compact;
            }
            return new RemeshNative.Geometry {
                positions = positions.ToArray(), normals = normals?.ToArray(), tangents = tangents?.ToArray(),
                uv = uv.ToArray(), charts = charts.ToArray(), indices = indices, chartCount = ids.Count,
                originalChartCount = g.originalChartCount, originalSmallChartCount = g.originalSmallChartCount,
                draftUv = g.draftUv
            };
        }
    }
}
