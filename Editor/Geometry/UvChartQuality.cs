using System;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Chart fragmentation and scale-independent conformal stretch on array data.</summary>
    internal readonly struct UvChartQuality
    {
        internal readonly int charts, smallCharts;
        internal readonly double meanStretch, maxStretch;
        internal readonly bool valid;

        internal UvChartQuality(int charts, int smallCharts, double meanStretch, double maxStretch, bool valid)
        {
            this.charts = charts; this.smallCharts = smallCharts;
            this.meanStretch = meanStretch; this.maxStretch = maxStretch; this.valid = valid;
        }

        internal static UvChartQuality Measure(RemeshNative.Geometry geometry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var counts = new int[geometry.chartCount];
            var winding = new int[geometry.chartCount];
            double weightedStretch = 0, area = 0, worst = 0;
            bool usable = true;
            for (int f = 0; f < geometry.indices.Length; f += 3) {
                if ((f & 4095) == 0) token.ThrowIfCancellationRequested();
                int a = geometry.indices[f], b = geometry.indices[f + 1], c = geometry.indices[f + 2];
                int chart = geometry.charts[a];
                if ((uint)chart >= (uint)counts.Length || geometry.charts[b] != chart || geometry.charts[c] != chart) {
                    usable = false; continue;
                }
                ++counts[chart];
                Vector3 e1 = geometry.positions[b] - geometry.positions[a], e2 = geometry.positions[c] - geometry.positions[a];
                Vector2 u1 = geometry.uv[b] - geometry.uv[a], u2 = geometry.uv[c] - geometry.uv[a];
                double length = e1.magnitude, twiceArea = Vector3.Cross(e1, e2).magnitude;
                if (length <= 0 || twiceArea <= 0) continue;
                double det = (double)u1.x * u2.y - (double)u1.y * u2.x;
                int sign = 0;
                if (det > 0) sign = 1;
                else if (det < 0) sign = -1;
                if (sign == 0 || winding[chart] != 0 && winding[chart] != sign) usable = false;
                winding[chart] = sign;
                // Express the 3D triangle in its own orthonormal 2D frame. The
                // singular-value ratio of its UV Jacobian ignores uniform scale.
                double tx = Vector3.Dot(e1, e2) / length, ty = twiceArea / length;
                double j00 = u1.x / length, j10 = u1.y / length;
                double j01 = (u2.x - j00 * tx) / ty, j11 = (u2.y - j10 * tx) / ty;
                double trace = j00 * j00 + j10 * j10 + j01 * j01 + j11 * j11;
                double determinant = j00 * j11 - j10 * j01;
                double largest = (trace + Math.Sqrt(Math.Max(0, trace * trace - 4 * determinant * determinant))) * .5;
                // sigmaMax / sigmaMin = sigmaMax² / |det J|. This form avoids
                // subtracting nearly equal eigenvalues on very thin triangles.
                double stretch = determinant == 0 ? double.PositiveInfinity : largest / Math.Abs(determinant);
                if (double.IsNaN(stretch) || double.IsInfinity(stretch)) usable = false;
                weightedStretch += stretch * twiceArea; area += twiceArea;
                worst = Math.Max(worst, stretch);
            }
            int small = 0;
            foreach (int count in counts) if (count > 0 && count <= 8) ++small;
            return new UvChartQuality(geometry.chartCount, small, area > 0 ? weightedStretch / area : 0, worst, usable && area > 0);
        }

        internal bool Improves(UvChartQuality current, UvChartQuality original)
        {
            if (!valid || charts > current.charts || smallCharts > current.smallCharts ||
                charts == current.charts && smallCharts == current.smallCharts) return false;
            if (!original.valid) return true;
            return meanStretch <= Math.Max(1.15, original.meanStretch * 1.1) &&
                maxStretch <= Math.Max(4, original.maxStretch * 1.1);
        }

        /// <summary>Explains the existing gate without changing its acceptance rules.</summary>
        internal string ImprovementFailure(UvChartQuality current, UvChartQuality original)
        {
            var failures = new System.Collections.Generic.List<string>();
            if (!valid) failures.Add("invalid UV quality (degenerate, non-finite, inconsistent chart or winding)");
            if (charts > current.charts) failures.Add("chart count increased");
            if (smallCharts > current.smallCharts) failures.Add("small chart count increased");
            if (charts == current.charts && smallCharts == current.smallCharts) failures.Add("fragmentation unchanged");
            if (original.valid)
            {
                double meanLimit = Math.Max(1.15, original.meanStretch * 1.1);
                double worstLimit = Math.Max(4, original.maxStretch * 1.1);
                if (!(meanStretch <= meanLimit)) failures.Add(FormattableString.Invariant($"mean {meanStretch:G6} > limit {meanLimit:G6}"));
                if (!(maxStretch <= worstLimit)) failures.Add(FormattableString.Invariant($"worst {maxStretch:G6} > limit {worstLimit:G6}"));
            }
            return failures.Count == 0 ? "accepted" : string.Join("; ", failures);
        }
    }
}
