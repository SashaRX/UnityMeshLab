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

        /// <summary>
        /// Same-chart pairs are charged to the budget but never clipped: completeness,
        /// comparisons and cross-chart overlaps match the whole scan at every budget.
        /// </summary>
        [Test]
        public void CrossChartScan_ChargesSameChartPairsButReportsOnlyCrossChartOverlaps()
        {
            // Two duplicate triangles in chart 0 and a shifted copy in chart 1 overlapping both.
            var g = Geometry(new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1),
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1),
                new Vector2(.2f, .2f), new Vector2(1.2f, .2f), new Vector2(.2f, 1.2f) },
                new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, new[] { 0, 0, 0, 0, 0, 0, 1, 1, 1 });
            var whole = UvAtlasDiagnostics.Measure(g, CancellationToken.None);
            var cross = UvAtlasDiagnostics.Measure(g, CancellationToken.None, crossChartOnly: true);
            Assert.AreEqual(1, whole.sameChartPairs);
            Assert.AreEqual(2, whole.crossChartPairs);
            Assert.AreEqual(0, cross.sameChartPairs);
            Assert.AreEqual(whole.crossChartPairs, cross.crossChartPairs);
            Assert.AreEqual(cross.crossChartPairs, cross.pairs);
            Assert.AreEqual(whole.comparisons, cross.comparisons);
            for (long budget = 0; budget <= whole.comparisons; ++budget)
                Assert.AreEqual(UvAtlasDiagnostics.Measure(g, CancellationToken.None, comparisonBudget: budget).complete,
                    UvAtlasDiagnostics.Measure(g, CancellationToken.None, comparisonBudget: budget, crossChartOnly: true).complete,
                    $"budget {budget}");
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
        public void DoubleNormalizationDoesNotTurnPartialSharedEdgesIntoOverlaps()
        {
            var uv=new[] {Vector2.zero,Vector2.right,Vector2.up,new Vector2(.3f,.7f),new Vector2(.7f,.3f),Vector2.one};
            var g=Geometry(uv,new[] {0,1,2,3,4,5},new int[6]);
            var origin=new Vector2(-.6394268274307251f,-.02501075528562069f);double scale=1/1.6225557327270508;
            var rounded=Geometry(Array.ConvertAll(uv,p=>new Vector2((float)(((double)p.x-origin.x)*scale),
                (float)(((double)p.y-origin.y)*scale))),g.indices,g.charts);
            Assert.AreEqual(1,UvAtlasDiagnostics.Measure(rounded,default).pairs,"Control reproduces Float32 normalization damage.");
            Assert.AreEqual(0,UvAtlasDiagnostics.Measure(g,default,coordinateOrigin:origin,coordinateScale:scale).pairs);
            g.uv[3]=new Vector2(.29f,.69f);
            Assert.AreEqual(1,UvAtlasDiagnostics.Measure(g,default,coordinateOrigin:origin,coordinateScale:scale).pairs,
                "Real positive-area intersections must still be detected.");
        }

        [Test]
        public void ReusableSmallFamilyPredicatesAgreeWithTheAtlasScan()
        {
            var scratch=new UvAtlasDiagnostics.IntersectionTest();var random=new System.Random(319);
            for(int trial=0;trial<200;++trial)
            {
                var uv=new Vector2[6];
                for(int i=0;i<6;++i) uv[i]=new Vector2((float)random.NextDouble(),(float)random.NextDouble());
                var g=Geometry(uv,new[] {0,1,2,3,4,5},new int[6]);
                var report=UvAtlasDiagnostics.Measure(g,default);
                Assert.AreEqual(report.pairs>0,scratch.OverlapsInAtlas(g,0,1));
                Assert.AreEqual(report.degenerateFaces>0,UvAtlasDiagnostics.IntersectionTest.Degenerate(g,0)||UvAtlasDiagnostics.IntersectionTest.Degenerate(g,1));
            }
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
