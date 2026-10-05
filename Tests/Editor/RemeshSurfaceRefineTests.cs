using System;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshSurfaceRefineTests
    {
        [TestCase(1f)]
        [TestCase(.001f)]
        public void CurvedPatchChoosesSourceDiagonalEvenWithEqualTriangleQuality(float scale)
        {
            var p = new[] { new Vector3(-1,0,0), new Vector3(1,0,0), new Vector3(0,1,.2f), new Vector3(0,-1,.2f) };
            for (int v = 0; v < p.Length; v++) p[v] *= scale;
            var old = new[] { 0,1,2, 1,0,3 }; var correct = new[] { 2,3,1, 3,2,0 };
            var input = new RemeshNative.IndexedMesh { positions = p, indices = old };
            Assert.That(RemeshSurfaceRefine.Quality(p[0],p[1],p[2]), Is.EqualTo(RemeshSurfaceRefine.Quality(p[2],p[3],p[1])).Within(1e-6f));
            var result = RemeshSurfaceRefine.Retriangulate(input,p,correct,scale * .1f,CancellationToken.None,out var report);
            Assert.AreEqual(1,report.flips); Assert.IsFalse(report.reverted);
            CollectionAssert.AreEqual(correct,result.indices); CollectionAssert.AreEqual(p,result.positions);
            CollectionAssert.AreEqual(new[] { 0,1,2, 1,0,3 },input.indices);
            var before = RemeshTopology.Inspect(p,old); var after = RemeshTopology.Inspect(result.positions,result.indices);
            Assert.IsTrue(after.Valid,after.Description); Assert.IsTrue(after.PreservesBoundary(before)); Assert.IsTrue(after.PreservesComponents(before,false));
            var repeat = RemeshSurfaceRefine.Retriangulate(input,p,correct,scale * .1f,CancellationToken.None,out _);
            CollectionAssert.AreEqual(result.indices,repeat.indices);
            var stable = RemeshSurfaceRefine.Retriangulate(result,p,correct,scale * .1f,CancellationToken.None,out var again);
            Assert.AreEqual(0,again.flips); CollectionAssert.AreEqual(result.indices,stable.indices);
        }

        [TestCase(1f)]
        [TestCase(.001f)]
        public void DoesNotTradeExactSourceFitForBetterTriangleShape(float scale)
        {
            var p = new[] { new Vector3(0,0,0), new Vector3(2,0,0), new Vector3(2,1,.15f), new Vector3(0,.2f,0) };
            for (int v = 0; v < p.Length; v++) p[v] *= scale;
            var ix = new[] { 0,1,2, 0,2,3 }; var input = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            var result = RemeshSurfaceRefine.Retriangulate(input,p,ix,scale * .1f,CancellationToken.None,out var report);
            Assert.AreEqual(0,report.flips); CollectionAssert.AreEqual(ix,result.indices); CollectionAssert.AreEqual(p,result.positions);
        }

        [TestCase(1f)]
        [TestCase(.001f)]
        public void PlanarSourceDoesNotSpendTriangleQualityOnFitNoise(float scale)
        {
            var p = new[] { new Vector3(-.9f,0,0), new Vector3(.9f,0,0), new Vector3(0,1,.02f), new Vector3(0,-1,.02f) };
            var source = (Vector3[])p.Clone(); source[0].z = source[1].z = .02f;
            for (int v = 0; v < p.Length; v++) { p[v] *= scale; source[v] *= scale; }
            var ix = new[] { 0,1,2, 1,0,3 }; var input = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            Assert.Less(RemeshSurfaceRefine.Quality(p[2],p[3],p[1]), RemeshSurfaceRefine.Quality(p[0],p[1],p[2]));
            var result = RemeshSurfaceRefine.Retriangulate(input,source,ix,scale * .1f,CancellationToken.None,out var report);
            Assert.AreEqual(0,report.flips); CollectionAssert.AreEqual(ix,result.indices); CollectionAssert.AreEqual(p,result.positions);
        }

        [TestCase(1f)]
        [TestCase(.001f)]
        public void FlipsBadPlanarDiagonalWithoutMovingBoundary(float scale)
        {
            var p = new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 1, 0), new Vector3(0, .2f, 0) };
            for (int i = 0; i < p.Length; i++) p[i] *= scale;
            var ix = new[] { 0, 1, 2, 0, 2, 3 };
            var input = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            var result = RemeshSurfaceRefine.Apply(input, p, ix, scale * .1f, CancellationToken.None, out var report);
            Assert.Greater(report.flips, 0);
            Assert.Greater(report.meanQualityAfter, report.meanQualityBefore);
            CollectionAssert.AreEqual(p, result.positions);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 0, 2, 3 }, input.indices);
            var before = RemeshTopology.Inspect(p, ix); var after = RemeshTopology.Inspect(result.positions, result.indices);
            Assert.IsTrue(after.Valid, after.Description); Assert.IsTrue(after.PreservesBoundary(before));
            Assert.IsTrue(after.PreservesComponents(before, false));
        }

        [TestCase(1f)]
        [TestCase(.001f)]
        public void FitsInteriorStairToSourceWithinCellBudget(float scale)
        {
            var source = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0), Vector3.zero };
            for (int i = 0; i < source.Length; i++) source[i] *= scale;
            var p = (Vector3[])source.Clone(); p[4].z = scale * .02f;
            var ix = new[] { 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4 };
            var input = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            var result = RemeshSurfaceRefine.Apply(input, source, ix, scale * .1f, CancellationToken.None, out var report);
            Assert.That(Mathf.Abs(result.positions[4].z), Is.LessThan(scale * 1e-5f));
            Assert.LessOrEqual(report.maxDisplacement, scale * .035001f);
            Assert.AreEqual(scale * .02f, input.positions[4].z);
            for (int v = 0; v < 4; v++) Assert.AreEqual(source[v], result.positions[v]);
            Assert.IsFalse(report.reverted);
            Assert.AreEqual(report.moves, report.attemptedMoves);
            Assert.IsFalse(report.movementFallback);
            Assert.IsNull(report.fallbackReason);
            Assert.AreEqual(1f, report.motionScale); Assert.AreEqual(0, report.motionBacktracks);
        }

        [TestCase(1f)]
        [TestCase(.001f)]
        public void FlipsOnlyFallbackReportsDiscardedFittingAndFailedSurfaceSamples(float scale)
        {
            // A tiny nearby sheet attracts the interior vertex, but lifting the
            // surrounding fan loses the broad source plane. The independent
            // planar quad still has a useful diagonal change to retain.
            var p = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0), new Vector3(0,0,.022f),
                new Vector3(4,0,0), new Vector3(6,0,0), new Vector3(6,1,0), new Vector3(4,.2f,0) };
            var source = new[] { p[0], p[1], p[2], p[3], Vector3.zero, p[5], p[6], p[7], p[8],
                new Vector3(-.001f,-.001f,.04f), new Vector3(.001f,-.001f,.04f), new Vector3(0,.001f,.04f) };
            for (int v = 0; v < p.Length; v++) p[v] *= scale;
            for (int v = 0; v < source.Length; v++) source[v] *= scale;
            var ix = new[] { 0,1,4, 1,2,4, 2,3,4, 3,0,4, 5,6,7, 5,7,8 };
            var sourceIx = new[] { 0,1,4, 1,2,4, 2,3,4, 3,0,4, 5,6,7, 5,7,8, 9,10,11 };
            var input = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            var result = RemeshSurfaceRefine.Apply(input, source, sourceIx, scale * .1f, CancellationToken.None, out var report);
            Assert.IsTrue(report.movementFallback);
            Assert.Greater(report.attemptedMoves, 0);
            Assert.Greater(report.attemptedFeatures, 0);
            Assert.Greater(report.attemptedMaxDisplacement, 0);
            Assert.AreEqual(0, report.moves); Assert.AreEqual(0, report.features); Assert.AreEqual(0, report.maxDisplacement);
            Assert.AreEqual(0, report.motionScale); Assert.AreEqual(3, report.motionBacktracks);
            Assert.Greater(report.flips, 0); Assert.IsFalse(report.reverted);
            StringAssert.Contains("target-to-source", report.fallbackReason);
            StringAssert.Contains("RMS", report.fallbackReason); StringAssert.Contains("max", report.fallbackReason);
            StringAssert.Contains("limit", report.fallbackReason); StringAssert.Contains("cells", report.fallbackReason);
            CollectionAssert.AreEqual(p, result.positions);
            CollectionAssert.AreEqual(new[] { 0,1,4, 1,2,4, 2,3,4, 3,0,4, 5,6,7, 5,7,8 }, input.indices);
        }

        [TestCase(1f)]
        [TestCase(.001f)]
        public void BacktracksRejectedFittingToPartialMotionWithinOriginalSurfaceGates(float scale)
        {
            var source = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0), Vector3.zero,
                new Vector3(-.001f,-.001f,.04f), new Vector3(.001f,-.001f,.04f), new Vector3(0,.001f,.04f),
                new Vector3(4,0,0), new Vector3(4.0001f,0,0), new Vector3(4,.0001f,0) };
            for (int v = 0; v < source.Length; v++) source[v] *= scale;
            var p = new[] { source[0], source[1], source[2], source[3], new Vector3(0,0,.034f * scale), source[8], source[9], source[10] };
            // The fixed tiny component is below the extra source-fitting area
            // margin. It must neither veto a useful trial nor lose area.
            var ix = new[] { 0,1,4, 1,2,4, 2,3,4, 3,0,4, 5,6,7 };
            var sourceIx = new[] { 0,1,4, 1,2,4, 2,3,4, 3,0,4, 5,6,7, 8,9,10 };
            var input = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            var result = RemeshSurfaceRefine.Apply(input, source, sourceIx, scale * .1f, CancellationToken.None, out var report);
            Assert.IsFalse(report.reverted); Assert.IsFalse(report.movementFallback);
            Assert.Greater(report.attemptedMoves, 0); Assert.Greater(report.attemptedFeatures, 0);
            Assert.AreEqual(.25f, report.motionScale); Assert.AreEqual(2, report.motionBacktracks);
            Assert.Greater(report.moves, 0); Assert.AreEqual(0, report.features);
            Assert.Greater(result.positions[4].z, p[4].z); Assert.Less(result.positions[4].z, source[5].z);
            Assert.That(report.maxDisplacement, Is.EqualTo(report.attemptedMaxDisplacement * .25f).Within(scale * 1e-7f));
            StringAssert.Contains("target-to-source", report.fallbackReason);
            for (int f = 0; f < ix.Length; f += 3) {
                var original = Vector3.Cross(p[ix[f + 1]] - p[ix[f]], p[ix[f + 2]] - p[ix[f]]);
                var fitted = Vector3.Cross(result.positions[result.indices[f + 1]] - result.positions[result.indices[f]],
                    result.positions[result.indices[f + 2]] - result.positions[result.indices[f]]);
                Assert.Greater(Vector3.Dot(original, fitted), 0);
            }
            var after = RemeshTopology.Inspect(result.positions, result.indices);
            var before = RemeshTopology.Inspect(p, ix);
            Assert.IsTrue(after.Valid, after.Description); Assert.IsTrue(after.PreservesBoundary(before));
            Assert.IsTrue(after.PreservesComponents(before, false));
            var repeated = RemeshSurfaceRefine.Apply(input, source, sourceIx, scale * .1f, CancellationToken.None, out var again);
            CollectionAssert.AreEqual(result.positions, repeated.positions); CollectionAssert.AreEqual(result.indices, repeated.indices);
            Assert.AreEqual(report.motionScale, again.motionScale); Assert.AreEqual(report.motionBacktracks, again.motionBacktracks);
            for (int v = 5; v < p.Length; v++) Assert.AreEqual(p[v], result.positions[v]);
            Assert.AreEqual(.034f * scale, input.positions[4].z); CollectionAssert.AreEqual(new[] { 0,1,4, 1,2,4, 2,3,4, 3,0,4, 5,6,7 }, input.indices);
        }

        [Test]
        public void PreservesSharpFoldAndDoesNotSnapToOppositeSheet()
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward };
            var ix = new[] { 0, 1, 2, 1, 0, 3 };
            var mesh = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            var result = RemeshSurfaceRefine.Apply(mesh, p, ix, .1f, CancellationToken.None, out var report);
            Assert.AreEqual(0, report.flips); CollectionAssert.AreEqual(ix, result.indices);
            CollectionAssert.AreEqual(p, result.positions);
            var unchangedFold = RemeshSurfaceRefine.Retriangulate(mesh, p, ix, .1f, CancellationToken.None, out var diagonal);
            Assert.AreEqual(0, diagonal.flips); CollectionAssert.AreEqual(ix, unchangedFold.indices);
            // Every source face points backwards; a front-facing target must not jump to it.
            var front = new RemeshNative.IndexedMesh {
                positions = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0), Vector3.zero },
                indices = new[] { 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4 }
            };
            var source = (Vector3[])front.positions.Clone();
            for (int v = 0; v < source.Length; v++) source[v].z += .005f;
            var reversed = (int[])front.indices.Clone();
            for (int f = 0; f < reversed.Length; f += 3) (reversed[f + 1], reversed[f + 2]) = (reversed[f + 2], reversed[f + 1]);
            var unchanged = RemeshSurfaceRefine.Apply(front, source, reversed, .1f, CancellationToken.None, out _);
            CollectionAssert.AreEqual(front.positions, unchanged.positions);
        }

        [Test]
        public void CancellationLeavesInputBuffersUntouched()
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up };
            var ix = new[] { 0, 1, 2 }; var input = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => RemeshSurfaceRefine.Apply(input, p, ix, .1f, cancel.Token, out _));
            Assert.Throws<OperationCanceledException>(() => RemeshSurfaceRefine.Retriangulate(input, p, ix, .1f, cancel.Token, out _));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, ix);
        }

        [Test]
        public void InvalidTopologyIsNotRepairedByChangingTheSurface()
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up };
            var ix = new[] { 0, 1, 2, 0, 1, 2 }; var input = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            Assert.AreSame(input, RemeshSurfaceRefine.Apply(input, p, ix, .1f, CancellationToken.None, out _));
            Assert.AreSame(input, RemeshSurfaceRefine.Retriangulate(input, p, ix, .1f, CancellationToken.None, out _));
        }

        [Test]
        [Timeout(600000)] // Includes the bounded high-precision native repair pack.
        public void DiagonalBoxKeepsCollapseBudgetAndUnwrapsWithoutOverlaps()
        {
            try { RemeshNative.CheckAvailable(); }
            catch (InvalidOperationException e) when (e.InnerException is DllNotFoundException || e.InnerException is EntryPointNotFoundException || e.InnerException is BadImageFormatException) {
                Assert.Ignore("Native plugin unavailable: " + e.Message);
            }
            var p = new[] { new Vector3(-1,-1,-1), new Vector3(1,-1,-1), new Vector3(1,1,-1), new Vector3(-1,1,-1),
                new Vector3(-1,-1,1), new Vector3(1,-1,1), new Vector3(1,1,1), new Vector3(-1,1,1) };
            var rotation = Quaternion.Euler(23, 31, 17);
            for (int i = 0; i < p.Length; i++) p[i] = rotation * Vector3.Scale(p[i], new Vector3(1, .6f, .4f));
            var ix = new[] { 4,5,6,4,6,7, 0,3,2,0,2,1, 0,4,7,0,7,3, 1,2,6,1,6,5, 0,1,5,0,5,4, 3,7,6,3,6,2 };
            var settings = new RemeshSettings { voxelResolution = 32, targetTriangles = 100, maximumError = .003f, mergeCharts = false, textureResolution = 512 };
            var voxel = RemeshNative.Voxelize(p, ix, settings, CancellationToken.None);
            var baseline = RemeshNative.Simplify(voxel, settings, CancellationToken.None, out _);
            var refined = RemeshSurfaceRefine.Simplify(voxel, p, ix, settings, CancellationToken.None, out _);
            Assert.LessOrEqual(refined.TriangleCount, baseline.TriangleCount * 1.1f);
            var topology = RemeshTopology.Inspect(refined.positions, refined.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count);
            var uv = RemeshNative.Unwrap(refined, settings, CancellationToken.None);
            var report = UvAtlasDiagnostics.Measure(uv, CancellationToken.None);
            Assert.IsTrue(report.complete); Assert.AreEqual(0, report.pairs); Assert.AreEqual(0, report.degenerateFaces);
            for (int i = 0; i < uv.indices.Length; i++) Assert.AreEqual(refined.positions[refined.indices[i]], uv.positions[uv.indices[i]]);
        }
    }
}
