using System;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Area utilization and chart texel density, independent of angular stretch.</summary>
    internal readonly struct UvPackingQuality
    {
        internal readonly double filledArea, densityDeviation;
        internal readonly bool valid;

        UvPackingQuality(double filledArea, double densityDeviation, bool valid)
        {
            this.filledArea = filledArea;
            this.densityDeviation = densityDeviation;
            this.valid = valid;
        }

        // Allow only a small raster-packing variation; fewer islands must not cost
        // a substantial portion of the available texture or introduce density drift.
        internal bool Preserves(UvPackingQuality original)
            => valid && original.valid && filledArea >= original.filledArea * .95 &&
                densityDeviation <= Math.Max(.02, original.densityDeviation * 1.1);

        static void Areas(RemeshNative.Geometry g, CancellationToken token, out double[] surface, out double[] parametric)
        {
            surface = new double[g.chartCount]; parametric = new double[g.chartCount];
            for (int i = 0; i < g.indices.Length; i += 3)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                int a = g.indices[i], b = g.indices[i + 1], c = g.indices[i + 2], chart = g.charts[a];
                if ((uint)chart >= (uint)g.chartCount || g.charts[b] != chart || g.charts[c] != chart)
                    throw new InvalidOperationException("A UV face spans inconsistent charts.");
                var p = g.positions[b] - g.positions[a]; var q = g.positions[c] - g.positions[a];
                double x = (double)p.y * q.z - (double)p.z * q.y;
                double y = (double)p.z * q.x - (double)p.x * q.z;
                double z = (double)p.x * q.y - (double)p.y * q.x;
                surface[chart] += .5 * Math.Sqrt(x * x + y * y + z * z);
                var u = g.uv[b] - g.uv[a]; var v = g.uv[c] - g.uv[a];
                parametric[chart] += .5 * Math.Abs((double)u.x * v.y - (double)u.y * v.x);
            }
        }

        internal static UvPackingQuality Measure(RemeshNative.Geometry g, CancellationToken token)
        {
            Areas(g, token, out var surface, out var parametric);
            double area = 0, uvArea = 0, densitySum = 0;
            bool valid = true;
            for (int c = 0; c < surface.Length; ++c)
            {
                if (surface[c] == 0 && parametric[c] == 0) continue;
                if (!(surface[c] > 0 && parametric[c] > 0) || double.IsInfinity(surface[c] + parametric[c]))
                { valid = false; continue; }
                area += surface[c]; uvArea += parametric[c];
                densitySum += Math.Sqrt(surface[c] * parametric[c]);
            }
            double mean = area > 0 ? densitySum / area : 0, variance = 0;
            for (int c = 0; c < surface.Length; ++c)
                if (surface[c] > 0 && parametric[c] > 0)
                {
                    double delta = Math.Sqrt(parametric[c] / surface[c]) - mean;
                    variance += surface[c] * delta * delta;
                }
            return new UvPackingQuality(uvArea, mean > 0 ? Math.Sqrt(variance / area) / mean : 0,
                valid && area > 0 && mean > 0);
        }

        /// <summary>UvMesh knows only UV area. Restore uniform 3D area density before
        /// packing; similarities and relax otherwise preserve accumulated density drift.
        /// Returns a separate buffer so failed/cancelled repacks never mutate input UV.</summary>
        internal static Vector2[] NormalizeChartAreas(RemeshNative.Geometry g, CancellationToken token)
        {
            Areas(g, token, out var surface, out var parametric);
            var anchor = new Vector2[g.chartCount]; var seen = new bool[g.chartCount];
            var scales = new double[g.chartCount];
            for (int c = 0; c < scales.Length; ++c)
            {
                if (surface[c] == 0 && parametric[c] == 0) { scales[c] = 1; continue; }
                scales[c] = Math.Sqrt(surface[c] / parametric[c]);
                if (!(scales[c] > 0) || double.IsInfinity(scales[c]))
                    throw new InvalidOperationException("Cannot normalize a degenerate UV chart.");
            }
            var uv = new Vector2[g.uv.Length];
            for (int v = 0; v < uv.Length; ++v)
            {
                if ((v & 4095) == 0) token.ThrowIfCancellationRequested();
                int c = g.charts[v];
                if (!seen[c]) { anchor[c] = g.uv[v]; seen[c] = true; }
                uv[v] = new Vector2((float)(((double)g.uv[v].x - anchor[c].x) * scales[c]),
                    (float)(((double)g.uv[v].y - anchor[c].y) * scales[c]));
            }
            return uv;
        }
    }
}
