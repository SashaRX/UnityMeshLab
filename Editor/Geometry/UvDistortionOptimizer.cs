using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Free-boundary, scale-independent conformal optimization on compact
    /// UV topology. MIPS trace/|det| = kappa + 1/kappa, with a quadratic tail for
    /// high distortion. Determinant barriers and line search prevent local flips;
    /// callers must still check global triangle intersections.</summary>
    internal static class UvDistortionOptimizer
    {
        struct Element
        {
            internal int a, b, c;
            internal double x1, y1, y2, area, weight, sign, detFloor;
        }
        sealed class History
        {
            internal double[] step, change;
            internal double inverseCurvature;
        }

        internal static bool Optimize(Vector3[] positions, int[] indices, Vector2[] initial,
            int iterations, CancellationToken token, out List<Vector2[]> proposals)
        {
            proposals = new List<Vector2[]>();
            token.ThrowIfCancellationRequested();
            var x = new double[initial.Length * 2];
            Vector2 min = initial[0], max = min;
            foreach (var uv in initial) { min = Vector2.Min(min, uv); max = Vector2.Max(max, uv); }
            double size = (max - min).magnitude;
            if (!(size > 0)) return false;
            for (int i = 0; i < initial.Length; ++i) { x[i * 2] = (initial[i].x - min.x) / size; x[i * 2 + 1] = (initial[i].y - min.y) / size; }
            var elements = BuildElements(positions, indices, x);
            if (elements == null) return false;
            var gradient = new double[x.Length];
            double energy = Evaluate(elements, x, gradient, out double initialMean, out double initialWorst);
            if (!Finite(energy)) return false;
            var history = new List<History>();
            var trial = new double[x.Length]; var trialGradient = new double[x.Length];
            double[] best = null; double bestMean = initialMean, bestWorst = initialWorst;
            double[] captured = null;
            for (int iteration = 0; iteration < iterations; ++iteration)
            {
                token.ThrowIfCancellationRequested();
                var direction = Direction(gradient, history);
                double descent = Dot(gradient, direction);
                if (!(descent < -1e-20)) break;
                double maximum = 0;
                for (int i = 0; i < direction.Length; i += 2)
                    maximum = Math.Max(maximum, Math.Sqrt(direction[i] * direction[i] + direction[i + 1] * direction[i + 1]));
                double step = Math.Min(1, .15 / maximum);
                step = Math.Min(step, .8 * FlipStepLimit(elements, x, direction));
                double nextEnergy = double.PositiveInfinity, mean = 0, worst = 0;
                bool accepted = false;
                for (int search = 0; search < 24; ++search)
                {
                    token.ThrowIfCancellationRequested();
                    for (int i = 0; i < x.Length; ++i) trial[i] = x[i] + step * direction[i];
                    nextEnergy = Evaluate(elements, trial, trialGradient, out mean, out worst);
                    if (Finite(nextEnergy) && nextEnergy <= energy + 1e-4 * step * descent) { accepted = true; break; }
                    step *= .5;
                }
                if (!accepted) break;
                AddHistory(history, x, gradient, trial, trialGradient);
                double gain = energy - nextEnergy;
                Array.Copy(trial, x, x.Length); Array.Copy(trialGradient, gradient, x.Length);
                energy = nextEnergy;
                if (mean <= initialMean * (1 + 1e-6) && worst <= initialWorst * (1 + 1e-6) &&
                    (mean < bestMean - 1e-5 && worst <= bestWorst * (1 + 1e-6) ||
                     worst < bestWorst - 1e-4 && mean <= bestMean * (1 + 1e-6)))
                {
                    best = (double[])x.Clone(); bestMean = mean; bestWorst = worst;
                }
                // Keep a small set of improving checkpoints. A later proposal may
                // self-intersect globally even with positive triangle determinants;
                // its rejection must not discard an earlier useful layout.
                if ((iteration + 1) % 5 == 0 && best != null && !ReferenceEquals(best, captured))
                {
                    proposals.Add(ToUv(best, min, size)); captured = best;
                }
                if (gain <= 1e-10 * (1 + energy)) break;
            }
            if (best == null) return false;
            if (!ReferenceEquals(best, captured)) proposals.Add(ToUv(best, min, size));
            return true;
        }

        static Vector2[] ToUv(double[] coordinates, Vector2 origin, double size)
        {
            var uv = new Vector2[coordinates.Length / 2];
            for (int i = 0; i < uv.Length; ++i)
                uv[i] = new Vector2((float)(origin.x + coordinates[i * 2] * size), (float)(origin.y + coordinates[i * 2 + 1] * size));
            return uv;
        }

        static Element[] BuildElements(Vector3[] positions, int[] indices, double[] uv)
        {
            var elements = new Element[indices.Length / 3]; double total = 0;
            for (int f = 0; f < elements.Length; ++f)
            {
                int a = indices[f * 3], b = indices[f * 3 + 1], c = indices[f * 3 + 2];
                var e1 = positions[b] - positions[a]; var e2 = positions[c] - positions[a];
                double length = e1.magnitude, area = Vector3.Cross(e1, e2).magnitude;
                if (!(length > 0 && area > 0)) return null;
                double tx = Vector3.Dot(e1, e2) / length, ty = area / length;
                var element = new Element { a = a, b = b, c = c, x1 = 1 / length, y1 = -tx / (length * ty), y2 = 1 / ty, area = area };
                double determinant = ((uv[b * 2] - uv[a * 2]) * (uv[c * 2 + 1] - uv[a * 2 + 1]) -
                    (uv[b * 2 + 1] - uv[a * 2 + 1]) * (uv[c * 2] - uv[a * 2])) / area;
                if (!Finite(determinant) || determinant == 0) return null;
                element.sign = Math.Sign(determinant); element.detFloor = Math.Abs(determinant) * 1e-8;
                elements[f] = element; total += area;
            }
            for (int i = 0; i < elements.Length; ++i) elements[i].weight = .9 * elements[i].area / total + .1 / elements.Length;
            return elements;
        }

        static double Evaluate(Element[] elements, double[] uv, double[] gradient, out double mean, out double worst)
        {
            Array.Clear(gradient, 0, gradient.Length);
            double energy = 0, weighted = 0, total = 0; worst = 0;
            foreach (var e in elements)
            {
                double ux = uv[e.b * 2] - uv[e.a * 2], uy = uv[e.c * 2] - uv[e.a * 2];
                double vx = uv[e.b * 2 + 1] - uv[e.a * 2 + 1], vy = uv[e.c * 2 + 1] - uv[e.a * 2 + 1];
                double a = ux * e.x1, b = ux * e.y1 + uy * e.y2;
                double c = vx * e.x1, d = vx * e.y1 + vy * e.y2;
                double determinant = e.sign * (a * d - b * c);
                if (!(determinant > e.detFloor)) { mean = double.PositiveInfinity; return double.PositiveInfinity; }
                double trace = a * a + b * b + c * c + d * d;
                double mips = trace / determinant, excess = Math.Max(0, mips - 2);
                energy += e.weight * (excess + .25 * excess * excess);
                double kappa = (mips + Math.Sqrt(Math.Max(0, mips * mips - 4))) * .5;
                weighted += e.area * kappa; total += e.area; worst = Math.Max(worst, kappa);
                double factor = e.weight * (1 + .5 * excess) / determinant;
                double cofactor = mips * e.sign;
                double ga = factor * (2 * a - cofactor * d), gb = factor * (2 * b + cofactor * c);
                double gc = factor * (2 * c + cofactor * b), gd = factor * (2 * d - cofactor * a);
                double bu = ga * e.x1 + gb * e.y1, cu = gb * e.y2;
                double bv = gc * e.x1 + gd * e.y1, cv = gd * e.y2;
                gradient[e.a * 2] -= bu + cu; gradient[e.b * 2] += bu; gradient[e.c * 2] += cu;
                gradient[e.a * 2 + 1] -= bv + cv; gradient[e.b * 2 + 1] += bv; gradient[e.c * 2 + 1] += cv;
            }
            mean = weighted / total; return energy;
        }

        static double[] Direction(double[] gradient, List<History> history)
        {
            var q = (double[])gradient.Clone(); var alpha = new double[history.Count];
            for (int i = history.Count - 1; i >= 0; --i)
            {
                var h = history[i]; alpha[i] = h.inverseCurvature * Dot(h.step, q);
                for (int k = 0; k < q.Length; ++k) q[k] -= alpha[i] * h.change[k];
            }
            double scale = 1;
            if (history.Count > 0)
            {
                var h = history[history.Count - 1]; scale = Dot(h.step, h.change) / Dot(h.change, h.change);
            }
            for (int k = 0; k < q.Length; ++k) q[k] *= scale;
            for (int i = 0; i < history.Count; ++i)
            {
                var h = history[i]; double beta = h.inverseCurvature * Dot(h.change, q);
                for (int k = 0; k < q.Length; ++k) q[k] += h.step[k] * (alpha[i] - beta);
            }
            for (int k = 0; k < q.Length; ++k) q[k] = -q[k];
            if (Dot(gradient, q) >= 0)
            {
                history.Clear();
                for (int k = 0; k < q.Length; ++k) q[k] = -gradient[k];
            }
            return q;
        }

        static void AddHistory(List<History> history, double[] current, double[] gradient, double[] trial, double[] trialGradient)
        {
            var step = new double[current.Length]; var change = new double[current.Length];
            for (int i = 0; i < step.Length; ++i) { step[i] = trial[i] - current[i]; change[i] = trialGradient[i] - gradient[i]; }
            double curvature = Dot(step, change);
            if (!(curvature > 1e-12 * Math.Sqrt(Dot(step, step) * Dot(change, change)))) return;
            if (history.Count == 6) history.RemoveAt(0);
            history.Add(new History { step = step, change = change, inverseCurvature = 1 / curvature });
        }

        // det(J + tD) is quadratic. Stop before its first positive root so line
        // search cannot jump over an inversion and land on a positive endpoint.
        static double FlipStepLimit(Element[] elements, double[] uv, double[] direction)
        {
            double limit = double.PositiveInfinity;
            foreach (var e in elements)
            {
                double a = (uv[e.b * 2] - uv[e.a * 2]) * e.x1;
                double b = (uv[e.b * 2] - uv[e.a * 2]) * e.y1 + (uv[e.c * 2] - uv[e.a * 2]) * e.y2;
                double c = (uv[e.b * 2 + 1] - uv[e.a * 2 + 1]) * e.x1;
                double d = (uv[e.b * 2 + 1] - uv[e.a * 2 + 1]) * e.y1 + (uv[e.c * 2 + 1] - uv[e.a * 2 + 1]) * e.y2;
                double da = (direction[e.b * 2] - direction[e.a * 2]) * e.x1;
                double db = (direction[e.b * 2] - direction[e.a * 2]) * e.y1 + (direction[e.c * 2] - direction[e.a * 2]) * e.y2;
                double dc = (direction[e.b * 2 + 1] - direction[e.a * 2 + 1]) * e.x1;
                double dd = (direction[e.b * 2 + 1] - direction[e.a * 2 + 1]) * e.y1 + (direction[e.c * 2 + 1] - direction[e.a * 2 + 1]) * e.y2;
                limit = Math.Min(limit, FirstPositiveRoot(e.sign * (da * dd - db * dc),
                    e.sign * (a * dd + da * d - b * dc - db * c), e.sign * (a * d - b * c) - e.detFloor));
            }
            return limit;
        }

        internal static double FirstPositiveRoot(double quadratic, double linear, double constant)
        {
            double scale = Math.Max(Math.Abs(quadratic), Math.Max(Math.Abs(linear), Math.Abs(constant)));
            if (!(scale > 0)) return 0;
            double a = quadratic / scale, b = linear / scale, c = constant / scale;
            if (a == 0) return b < 0 ? -c / b : double.PositiveInfinity;
            double discriminant = b * b - 4 * a * c;
            if (discriminant < 0) return double.PositiveInfinity;
            double root = Math.Sqrt(discriminant);
            double q = -.5 * (b + (b >= 0 ? root : -root));
            if (q == 0) return double.PositiveInfinity;
            double first = q / a, second = c / q;
            double limit = double.PositiveInfinity;
            if (first > 0) limit = first;
            if (second > 0) limit = Math.Min(limit, second);
            return limit;
        }
        static double Dot(double[] a, double[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; ++i) sum += a[i] * b[i];
            return sum;
        }
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
