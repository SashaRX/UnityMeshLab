using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FoliageSurfaceSamplerTests
    {
        internal static FoliageNormalsSettings Settings() => new FoliageNormalsSettings { relativeSpacing = false, sampleSpacing = .1f,
            includeSourceVertices = false, includeTriangleCentroids = false, gpuProjection = false, recalculateTangents = false };

        [TestCase(false)] [TestCase(true)]
        public void QuadWeldsAcrossSplitVerticesAndSamplingIsDeterministic(bool split)
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up, Vector3.zero, Vector3.one };
            var t = split ? new[] { 0,1,2, 4,5,3 } : new[] { 0,1,2, 0,2,3 };
            var settings = Settings();
            var a = FoliageSurfaceSampler.Sample(p, t, settings, CancellationToken.None);
            var b = FoliageSurfaceSampler.Sample(p, t, settings, CancellationToken.None);
            Assert.AreEqual(1, a.islands); Assert.Greater(a.samples.Length, 10);
            CollectionAssert.AreEqual(a.samples, b.samples); CollectionAssert.AreEqual(a.sampleIslands, b.sampleIslands);
            foreach (var q in a.samples) {
                var hit = new TriangleBvh(p, t).FindNearest(q);
                Assert.That(hit.distSq, Is.LessThan(1e-10)); Assert.That(hit.barycentric.x + hit.barycentric.y + hit.barycentric.z, Is.EqualTo(1).Within(1e-5));
            }
        }

        [Test]
        public void AreaAllocationTinyIslandDedupAndBudgetAreDeterministic()
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up,
                Vector3.right * 10, Vector3.right * 12, new Vector3(10,2,0),
                Vector3.right * 20, new Vector3(20.001f,0,0), new Vector3(20,.001f,0) };
            var settings = Settings(); settings.dedupSpacingFraction = .001f;
            var r = FoliageSurfaceSampler.Sample(p, new[] { 0,1,2, 3,4,5, 6,7,8 }, settings, CancellationToken.None);
            Assert.AreEqual(3, r.islands); Assert.That(r.area, Is.EqualTo(2.5000005).Within(1e-5));
            int a = r.sampleIslands.Count(i => i == 0), b = r.sampleIslands.Count(i => i == 1);
            Assert.That(b / (float)a, Is.InRange(3.8f, 4.2f)); Assert.AreEqual(1, r.sampleIslands.Count(i => i == 2));
            Assert.GreaterOrEqual(r.minNeighbour + 1e-6f, r.spacing * settings.dedupSpacingFraction);
            settings.maximumPrimitiveCount = 17;
            var limited = FoliageSurfaceSampler.Sample(p, new[] { 0,1,2, 3,4,5, 6,7,8 }, settings, CancellationToken.None);
            Assert.AreEqual(17, limited.samples.Length); Assert.AreEqual(3, limited.sampleIslands.Distinct().Count());
            CollectionAssert.AreEqual(limited.samples, FoliageSurfaceSampler.Sample(p, new[] { 0,1,2, 3,4,5, 6,7,8 }, settings, CancellationToken.None).samples);
        }

        [Test]
        public void OptionalVerticesCentroidsAndDegeneratesRemainFiniteAndRespectDedup()
        {
            var s = Settings(); s.includeSourceVertices = true; s.includeTriangleCentroids = true;
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up };
            var r = FoliageSurfaceSampler.Sample(p, new[] { 0,1,2, 0,0,1 }, s, CancellationToken.None);
            Assert.AreEqual(1, r.skippedDegenerate);
            foreach (var q in r.samples) Assert.IsTrue(FoliageTargetSnapshot.Finite(q));
            for (int i = 0; i < r.samples.Length; i++) for (int j = i + 1; j < r.samples.Length; j++)
                Assert.GreaterOrEqual(Vector3.Distance(r.samples[i], r.samples[j]) + 1e-6f, s.sampleSpacing * s.dedupSpacingFraction);
            Assert.Throws<InvalidOperationException>(() => FoliageSurfaceSampler.Sample(p, new[] { 0,0,1 }, s, CancellationToken.None));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => FoliageSurfaceSampler.Sample(p, new[] { 0,1,2 }, s, cancelled.Token));
        }

        [Test]
        public void UniformBarycentricFormulaStaysInsideAndSamplesTriangleMean()
        {
            Vector3 sum = Vector3.zero; const int n = 100;
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) {
                var p = FoliageSurfaceSampler.Inside(Vector3.zero, Vector3.right, Vector3.up, (i + .5f) / n, (j + .5f) / n);
                Assert.GreaterOrEqual(p.x, 0); Assert.GreaterOrEqual(p.y, 0); Assert.LessOrEqual(p.x + p.y, 1.000001f); sum += p;
            }
            Assert.That((sum / (n*n) - new Vector3(1f/3, 1f/3, 0)).magnitude, Is.LessThan(.001));
        }
    }
}
