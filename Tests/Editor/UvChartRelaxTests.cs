using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class UvChartRelaxTests
    {
        // Every triangle has independent corner/tangent vertices, including along
        // the simulated joined seam. Compact UV topology must reconnect them.
        static RemeshNative.Geometry Grid(bool mirror = false)
        {
            var positions = new List<Vector3>(); var uv = new List<Vector2>(); var indices = new List<int>(); var charts = new List<int>();
            void Add(int x, int y)
            {
                positions.Add(new Vector3(x, 0, y));
                Vector2 p = new Vector2(.1f + x * .1f, .1f + y * .1f);
                if (x == 1 && y == 1) p += new Vector2(.03f, -.02f);
                if (mirror) p.y = .6f - p.y;
                uv.Add(p); indices.Add(indices.Count); charts.Add(0);
            }
            for (int y = 0; y < 3; ++y)
                for (int x = 0; x < 3; ++x)
                {
                    Add(x, y); Add(x + 1, y); Add(x + 1, y + 1);
                    Add(x, y); Add(x + 1, y + 1); Add(x, y + 1);
                }
            positions.AddRange(new[] { new Vector3(5, 0, 0), new Vector3(6, 0, 0), new Vector3(5, 0, 1) });
            uv.AddRange(new[] { new Vector2(.8f, .8f), new Vector2(.9f, .8f), new Vector2(.8f, .9f) });
            for (int i = 0; i < 3; ++i) { indices.Add(indices.Count); charts.Add(1); }
            return new RemeshNative.Geometry { positions = positions.ToArray(), uv = uv.ToArray(), indices = indices.ToArray(), charts = charts.ToArray(),
                chartCount = 2, normals = new Vector3[positions.Count], tangents = new Vector4[positions.Count] };
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void RelaxImprovesDistortionPreservesWeldedSeamDensityAndSourceCorners(bool mirror, bool arap)
        {
            var g = Grid(mirror);
            var positions = g.positions; var indices = g.indices; var charts = g.charts; var normals = g.normals; var tangents = g.tangents;
            var original = (Vector2[])g.uv.Clone();
            var before = UvChartQuality.Measure(g, CancellationToken.None);
            double area = Area(g);
            Assert.AreEqual(1, UvChartRelax.Apply(g, new HashSet<int> { 0 }, 50, CancellationToken.None, arap));
            var after = UvChartQuality.Measure(g, CancellationToken.None);
            Assert.IsTrue(after.valid); Assert.Less(after.meanStretch, before.meanStretch); Assert.Less(after.maxStretch, before.maxStretch);
            var scan = UvAtlasDiagnostics.Measure(g, CancellationToken.None);
            Assert.IsTrue(scan.complete); Assert.AreEqual(0, scan.pairs); Assert.AreEqual(0, scan.degenerateFaces);
            Assert.AreEqual(area, Area(g), 1e-7);
            Assert.AreSame(positions, g.positions); Assert.AreSame(indices, g.indices); Assert.AreSame(charts, g.charts);
            Assert.AreSame(normals, g.normals); Assert.AreSame(tangents, g.tangents);
            Assert.AreEqual(2, g.chartCount);
            var atPosition = new Dictionary<Vector3, Vector2>();
            for (int i = 0; i < g.positions.Length - 3; ++i)
            {
                if (atPosition.TryGetValue(g.positions[i], out var previous)) Assert.AreEqual(previous, g.uv[i]);
                else atPosition.Add(g.positions[i], g.uv[i]);
            }
            for (int i = g.uv.Length - 3; i < g.uv.Length; ++i) Assert.AreEqual(original[i], g.uv[i], "unselected chart");
            int a = g.indices[0], b = g.indices[1], c = g.indices[2];
            float cross = Cross(g.uv[b] - g.uv[a], g.uv[c] - g.uv[a]);
            Assert.AreEqual(mirror ? -1 : 1, Math.Sign(cross), "winding");
        }

        [Test]
        public void RelaxSkipsAmbiguousExistingUvCutsBitExactly()
        {
            var g = Grid(); g.uv[0].x += .015f;
            var original = (Vector2[])g.uv.Clone();
            Assert.AreEqual(0, UvChartRelax.Apply(g, new HashSet<int> { 0 }, 50, CancellationToken.None));
            CollectionAssert.AreEqual(original, g.uv);
        }

        [Test]
        public void ClosedChartIsSkippedWithoutChangingUvs()
        {
            var g = new RemeshNative.Geometry {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up, new Vector2(.4f, .3f) },
                indices = new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3 }, charts = new int[4], chartCount = 1
            };
            var original = (Vector2[])g.uv.Clone();
            Assert.AreEqual(0, UvChartRelax.Apply(g, new HashSet<int> { 0 }, 50, CancellationToken.None));
            CollectionAssert.AreEqual(original, g.uv);
        }

        [Test]
        public void LongerOptimizationKeepsEarlierSafeCheckpoints()
        {
            var shortRun = Grid(); var longRun = Grid();
            Assert.AreEqual(1, UvChartRelax.Apply(shortRun, new HashSet<int> { 0 }, 15, CancellationToken.None));
            Assert.AreEqual(1, UvChartRelax.Apply(longRun, new HashSet<int> { 0 }, 50, CancellationToken.None));
            var shortQuality = UvChartQuality.Measure(shortRun, CancellationToken.None);
            var longQuality = UvChartQuality.Measure(longRun, CancellationToken.None);
            Assert.LessOrEqual(longQuality.meanStretch, shortQuality.meanStretch * (1 + 1e-6));
            Assert.LessOrEqual(longQuality.maxStretch, shortQuality.maxStretch * (1 + 1e-6));
        }

        [Test]
        public void RelaxIsDeterministicAndCancellationPreservesTheCaller()
        {
            var first = Grid(); var second = Grid();
            UvChartRelax.Apply(first, new HashSet<int> { 0 }, 50, CancellationToken.None);
            UvChartRelax.Apply(second, new HashSet<int> { 0 }, 50, CancellationToken.None);
            CollectionAssert.AreEqual(first.uv, second.uv);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); var g = Grid(); var original = (Vector2[])g.uv.Clone();
                Assert.Throws<OperationCanceledException>(() => UvChartRelax.Apply(g, new HashSet<int> { 0 }, 50, cancelled.Token));
                CollectionAssert.AreEqual(original, g.uv);
            }
        }

        [TestCase(1)]
        [TestCase(-1)]
        public void AnalyticDistortionGradientMatchesCentralDifferences(int winding)
        {
            var type = typeof(UvDistortionOptimizer);
            var build = type.GetMethod("BuildElements", BindingFlags.Static | BindingFlags.NonPublic);
            var evaluate = type.GetMethod("Evaluate", BindingFlags.Static | BindingFlags.NonPublic);
            var positions = new[] { Vector3.zero, new Vector3(2, .3f, .1f), new Vector3(.4f, 1.2f, .8f) };
            var uv = new[] { .1, winding * .2, .9, winding * .1, .4, winding * .8 };
            var elements = build.Invoke(null, new object[] { positions, new[] { 0, 1, 2 }, uv });
            var gradient = new double[uv.Length];
            evaluate.Invoke(null, new object[] { elements, uv, gradient, 0d, 0d });
            const double epsilon = 1e-6;
            for (int i = 0; i < uv.Length; ++i)
            {
                double original = uv[i]; uv[i] = original + epsilon;
                double high = (double)evaluate.Invoke(null, new object[] { elements, uv, new double[uv.Length], 0d, 0d });
                uv[i] = original - epsilon;
                double low = (double)evaluate.Invoke(null, new object[] { elements, uv, new double[uv.Length], 0d, 0d });
                uv[i] = original;
                Assert.AreEqual((high - low) / (2 * epsilon), gradient[i], Math.Max(1e-5, Math.Abs(gradient[i]) * 1e-5), "coordinate " + i);
            }
        }

        [Test]
        public void FlipStepBoundStopsAtFirstRootEvenWhenTheFarEndpointIsPositive()
        {
            // (1 - 2t)(1 - 4t): positive at t=0 and t=1, inverted between
            // t=.25 and .5. An endpoint-only determinant test would miss it.
            Assert.AreEqual(.25, UvDistortionOptimizer.FirstPositiveRoot(8, -6, 1), 1e-12);
            Assert.AreEqual(.5, UvDistortionOptimizer.FirstPositiveRoot(0, -2, 1), 1e-12);
            Assert.IsTrue(double.IsPositiveInfinity(UvDistortionOptimizer.FirstPositiveRoot(1, 0, 1)));
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        static double Area(RemeshNative.Geometry g)
        {
            double sum = 0;
            for (int f = 0; f < g.indices.Length; f += 3)
                sum += Math.Abs(Cross(g.uv[g.indices[f + 1]] - g.uv[g.indices[f]], g.uv[g.indices[f + 2]] - g.uv[g.indices[f]])) * .5;
            return sum;
        }
    }
}
