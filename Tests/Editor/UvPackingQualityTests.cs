using System;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class UvPackingQualityTests
    {
        static RemeshNative.Geometry Patches(float secondDensity = 1)
        {
            return new RemeshNative.Geometry {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.up,
                    Vector3.zero, Vector3.right, Vector3.up },
                uv = new[] { Vector2.zero, new Vector2(.5f, 0), new Vector2(0, .5f),
                    new Vector2(.6f, .6f), new Vector2(.6f + .2f * secondDensity, .6f), new Vector2(.6f, .6f + .2f * secondDensity) },
                indices = new[] { 0, 1, 2, 3, 4, 5 }, charts = new[] { 0, 0, 0, 1, 1, 1 }, chartCount = 2
            };
        }

        [Test]
        public void UniformChartScalingRestoresAreaDensityWithoutChangingTriangleShapes()
        {
            var g = Patches();
            var original = (Vector2[])g.uv.Clone();
            var quality = UvChartQuality.Measure(g, CancellationToken.None);
            var normalized = UvPackingQuality.NormalizeChartAreas(g, CancellationToken.None);
            CollectionAssert.AreEqual(original, g.uv, "preparing a repack must not mutate input");
            g.uv = normalized;
            Assert.AreEqual(0, UvPackingQuality.Measure(g, CancellationToken.None).densityDeviation, 1e-6);
            Assert.AreEqual(quality.meanStretch, UvChartQuality.Measure(g, CancellationToken.None).meanStretch, 1e-6);
            Assert.IsTrue(UvChartQuality.Measure(g, CancellationToken.None).valid);
        }

        [Test]
        public void TangentDuplicatesReceiveIdenticalNormalizedCoordinates()
        {
            var g = Patches();
            Array.Resize(ref g.uv, 7); Array.Resize(ref g.positions, 7); Array.Resize(ref g.charts, 7);
            g.uv[6] = g.uv[1]; g.positions[6] = g.positions[1]; g.charts[6] = g.charts[1];
            g.indices[1] = 6;
            var uv = UvPackingQuality.NormalizeChartAreas(g, CancellationToken.None);
            Assert.AreEqual(uv[1], uv[6]);
        }

        [Test]
        public void AngularlyPerfectAtlasWithLowerUtilizationFailsPackingGate()
        {
            var g = Patches(2.5f);
            var baseline = UvPackingQuality.Measure(g, CancellationToken.None);
            for (int i = 0; i < g.uv.Length; ++i) g.uv[i] *= .8f;
            Assert.AreEqual(1, UvChartQuality.Measure(g, CancellationToken.None).meanStretch, 1e-5);
            Assert.IsFalse(UvPackingQuality.Measure(g, CancellationToken.None).Preserves(baseline));
        }

        [Test]
        public void SameFilledAreaWithUnequalChartDensityFailsPackingGate()
        {
            var uniform = Patches(2.5f);
            var baseline = UvPackingQuality.Measure(uniform, CancellationToken.None);
            var uneven = Patches();
            float scale = (float)Math.Sqrt(baseline.filledArea / UvPackingQuality.Measure(uneven, CancellationToken.None).filledArea);
            for (int i = 0; i < uneven.uv.Length; ++i) uneven.uv[i] *= scale;
            var result = UvPackingQuality.Measure(uneven, CancellationToken.None);
            Assert.AreEqual(baseline.filledArea, result.filledArea, 1e-7);
            Assert.IsFalse(result.Preserves(baseline));
            Assert.IsTrue(baseline.Preserves(baseline));
        }

        [Test]
        public void DegenerateChartCannotBeNormalizedOrAccepted()
        {
            var g = Patches(); g.uv[2] = g.uv[1];
            Assert.IsFalse(UvPackingQuality.Measure(g, CancellationToken.None).valid);
            Assert.Throws<InvalidOperationException>(() => UvPackingQuality.NormalizeChartAreas(g, CancellationToken.None));
        }

        [Test]
        public void CancelledNormalizationLeavesInputUntouched()
        {
            var g = Patches(); var original = (Vector2[])g.uv.Clone();
            Assert.Throws<OperationCanceledException>(() => UvPackingQuality.NormalizeChartAreas(g, new CancellationToken(true)));
            CollectionAssert.AreEqual(original, g.uv);
        }
    }
}
