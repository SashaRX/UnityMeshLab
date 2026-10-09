using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Bounded plane evidence on continuous rims. Evidence does not
    /// choose disk versus Bridge intent or reconstruct missing feature vertices.</summary>
    internal static class RemeshCapPlanes
    {
        internal enum Kind { Planar, TwoPlanes, Ambiguous, Unsupported }

        internal sealed class Analysis
        {
            internal Kind kind;
            internal List<int> firstArc;
            internal int hypotheses, fits;
        }

        internal readonly struct Plane
        {
            internal readonly double nx, ny, nz;
            internal readonly int drop;
            internal Plane(double x, double y, double z)
            {
                double norm = Math.Sqrt(x * x + y * y + z * z);
                nx = x / norm; ny = y / norm; nz = z / norm;
                drop = Math.Abs(x) >= Math.Abs(y) && Math.Abs(x) >= Math.Abs(z) ? 0 : Math.Abs(y) >= Math.Abs(z) ? 1 : 2;
            }
        }

        // Strongest noncollinear support, in double with a local origin. Every
        // sample is checked; an RMS residual cannot hide an off-plane rim corner.
        // legacyRank preserves the already-shipped disk acceptance profile.
        internal static bool TryPlane(Vector3[] p, List<int> vertices, double tolerance,
            bool legacyRank, out Plane plane)
        {
            plane = default;
            var origin = p[vertices[0]];
            int far = vertices[0]; double longest = 0;
            foreach (int v in vertices) {
                double dx = (double)p[v].x - origin.x, dy = (double)p[v].y - origin.y, dz = (double)p[v].z - origin.z;
                double length = dx * dx + dy * dy + dz * dz;
                if (length > longest) { longest = length; far = v; }
            }
            double ux = (double)p[far].x - origin.x, uy = (double)p[far].y - origin.y, uz = (double)p[far].z - origin.z;
            double nx = 0, ny = 0, nz = 0, best = 0;
            foreach (int v in vertices) {
                double vx = (double)p[v].x - origin.x, vy = (double)p[v].y - origin.y, vz = (double)p[v].z - origin.z;
                double x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
                double length = x * x + y * y + z * z;
                if (length > best) { best = length; nx = x; ny = y; nz = z; }
            }
            if (!(best > longest * longest * (legacyRank ? 1e-20 : 1e-12))) return false;
            plane = new Plane(nx, ny, nz);
            double norm = Math.Sqrt(best);
            if (tolerance < 0) tolerance = Math.Sqrt(longest) * 1e-5;
            foreach (int v in vertices) {
                double residual = Math.Abs(nx * ((double)p[v].x - origin.x) +
                    ny * ((double)p[v].y - origin.y) + nz * ((double)p[v].z - origin.z)) / norm;
                if (residual > tolerance) return false;
            }
            return true;
        }

        internal static Analysis Analyze(Vector3[] p, List<int> loop, CancellationToken token,
            int maxFits = 32768, int maxSamples = 2000000)
        {
            token.ThrowIfCancellationRequested();
            if (loop.Count < 3 || loop.Count > 512 || maxFits < 1 || maxSamples < 1)
                throw new InvalidOperationException("Local Cap plane analysis has invalid support or budgets.");
            var result = new Analysis { kind = Kind.Unsupported };
            var low = p[loop[0]]; var high = low;
            foreach (int v in loop) { low = Vector3.Min(low, p[v]); high = Vector3.Max(high, p[v]); }
            double dx = (double)high.x - low.x, dy = (double)high.y - low.y, dz = (double)high.z - low.z;
            double tolerance = Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1e-5;
            int samples = 0;
            Plane Fit(List<int> support, out bool supported)
            {
                token.ThrowIfCancellationRequested();
                if (++result.fits > maxFits || (samples += support.Count) > maxSamples)
                    throw new InvalidOperationException("Local Cap plane analysis exceeded its search budget; no partial hypothesis was accepted.");
                supported = TryPlane(p, support, tolerance, false, out var plane);
                return plane;
            }
            Fit(loop, out bool whole);
            if (whole) { result.kind = Kind.Planar; result.hypotheses = 1; return result; }

            var corners = new List<int>();
            for (int i = 0; i < loop.Count; ++i) {
                // Subdivision along a straight rim must not introduce fake plane
                // junctions. The 20-degree turn is evidence, not a closure score.
                var a = p[loop[(i + loop.Count - 1) % loop.Count]];
                var b = p[loop[i]]; var c = p[loop[(i + 1) % loop.Count]];
                double ax = (double)b.x - a.x, ay = (double)b.y - a.y, az = (double)b.z - a.z;
                double bx = (double)c.x - b.x, by = (double)c.y - b.y, bz = (double)c.z - b.z;
                double length = Math.Sqrt((ax * ax + ay * ay + az * az) * (bx * bx + by * by + bz * bz));
                if (length > 0 && (ax * bx + ay * by + az * bz) / length <= Math.Cos(Math.PI / 9)) corners.Add(i);
            }
            for (int a = 0; a < corners.Count; ++a) for (int b = a + 1; b < corners.Count; ++b) {
                int start = corners[a], end = corners[b], length = end - start;
                if (length < 2 || loop.Count - length < 2) continue;
                var first = Arc(loop, start, length); var second = Arc(loop, end, loop.Count - length);
                var x = Fit(first, out bool firstPlane); var y = Fit(second, out bool secondPlane);
                if (!firstPlane || !secondPlane) continue;
                double dot = Math.Abs(x.nx * y.nx + x.ny * y.ny + x.nz * y.nz);
                if (dot > Math.Cos(Math.PI / 9)) continue;
                ++result.hypotheses;
                if (result.firstArc == null) result.firstArc = first;
            }
            result.kind = result.hypotheses == 1 ? Kind.TwoPlanes : result.hypotheses > 1 ? Kind.Ambiguous : Kind.Unsupported;
            return result;
        }

        static List<int> Arc(List<int> loop, int start, int length)
        {
            var arc = new List<int>(length + 1);
            for (int i = 0; i <= length; ++i) arc.Add(loop[(start + i) % loop.Count]);
            return arc;
        }
    }
}
