using System;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class UvAtlasDiagnosticsTests
    {
        static RemeshNative.Geometry Geometry(Vector2[] uv, int[] indices, int[] charts)
            => new RemeshNative.Geometry { uv = uv, indices = indices, charts = charts };

        static RemeshNative.Geometry DuplicateTriangles()
            => Geometry(new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1),
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1) },
                new[] { 0, 1, 2, 3, 4, 5 }, new[] { 0, 0, 0, 1, 1, 1 });

        [Test]
        public void SharedEdgeContact_HasZeroOverlap()
        {
            var g = Geometry(new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) },
                new[] { 0, 1, 2, 1, 3, 2 }, new[] { 0, 0, 0, 0 });
            var report = UvAtlasDiagnostics.Measure(g, CancellationToken.None);
            Assert.IsTrue(report.complete);
            Assert.AreEqual(0, report.pairs);
        }

        [Test]
        public void FoldedNeighbours_SharingEdge_AreNotSkipped()
        {
            var g = Geometry(new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(.25f, .25f) },
                new[] { 0, 1, 2, 0, 1, 3 }, new[] { 0, 0, 0, 0 });
            var report = UvAtlasDiagnostics.Measure(g, CancellationToken.None);
            Assert.AreEqual(1, report.sameChartPairs);
            Assert.AreEqual(.125, report.pairAreaSum, 1e-12);
        }

        [Test]
        public void DuplicateTriangles_WithOppositeWinding_AreOverlaps()
        {
            var g = DuplicateTriangles();
            g.indices = new[] { 0, 1, 2, 5, 4, 3 };
            var report = UvAtlasDiagnostics.Measure(g, CancellationToken.None);
            Assert.AreEqual(1, report.crossChartPairs);
            Assert.AreEqual(.5, report.pairAreaSum, 1e-12);
        }

        [Test]
        public void CrossingEdges_WithoutContainedVertices_AreOverlaps()
        {
            var g = Geometry(new[] { new Vector2(0, .25f), new Vector2(1, .25f), new Vector2(.5f, 1),
                new Vector2(0, .75f), new Vector2(.5f, 0), new Vector2(1, .75f) },
                new[] { 0, 1, 2, 3, 4, 5 }, new[] { 0, 0, 0, 1, 1, 1 });
            Assert.AreEqual(1, UvAtlasDiagnostics.Measure(g, CancellationToken.None).pairs);
        }

        [Test]
        public void UnpackedStage_OnlyChecksWithinCharts()
        {
            var g = DuplicateTriangles();
            Assert.AreEqual(0, UvAtlasDiagnostics.Measure(g, CancellationToken.None, sameChartOnly: true).pairs);
            Assert.AreEqual(1, UvAtlasDiagnostics.Measure(g, CancellationToken.None).pairs);
        }

        [Test]
        public void ScanBudget_ReportsIncompleteRatherThanClean()
        {
            var report = UvAtlasDiagnostics.Measure(DuplicateTriangles(), CancellationToken.None, comparisonBudget: 0);
            Assert.IsFalse(report.complete);
        }

        [Test]
        public void InvalidAndDegenerateFaces_AreReported()
        {
            var g = Geometry(new[] { new Vector2(float.NaN, 0), new Vector2(0, 0), new Vector2(1, 0), new Vector2(2, 0) },
                new[] { 0, 1, 2, 1, 2, 3 }, new[] { 0, 0, 0, 0 });
            var report = UvAtlasDiagnostics.Measure(g, CancellationToken.None);
            Assert.AreEqual(1, report.invalidFaces);
            Assert.AreEqual(1, report.degenerateFaces);
            Assert.AreEqual(1, report.outOfBoundsVertices);
        }

        [Test]
        public void CancelledScan_StopsImmediately()
            => Assert.Throws<OperationCanceledException>(() => UvAtlasDiagnostics.Measure(DuplicateTriangles(), new CancellationToken(true)));

        [Test]
        public void Diagnostics_LeaveUvAndIndicesUntouched()
        {
            var g = DuplicateTriangles();
            var uv = (Vector2[])g.uv.Clone(); var indices = (int[])g.indices.Clone(); var charts = (int[])g.charts.Clone();
            UvAtlasDiagnostics.Measure(g, CancellationToken.None);
            CollectionAssert.AreEqual(uv, g.uv);
            CollectionAssert.AreEqual(indices, g.indices);
            CollectionAssert.AreEqual(charts, g.charts);
        }

        [Test]
        public void QualityFailure_ExplainsMeanLimitAndUnchangedFragmentation()
        {
            var baseline = new UvChartQuality(80, 10, 1, 3, true);
            var candidate = new UvChartQuality(48, 8, 1.18, 3.8, true);
            Assert.IsFalse(candidate.Improves(baseline, baseline));
            StringAssert.Contains("mean 1.18 > limit 1.15", candidate.ImprovementFailure(baseline, baseline));
            StringAssert.Contains("fragmentation unchanged", baseline.ImprovementFailure(baseline, baseline));
            Assert.AreEqual("accepted", new UvChartQuality(48, 8, 1.1, 3.8, true).ImprovementFailure(baseline, baseline));
        }
    }
}
