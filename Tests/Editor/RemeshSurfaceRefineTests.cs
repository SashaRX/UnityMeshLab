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
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, ix);
        }

        [Test]
        public void InvalidTopologyIsNotRepairedByChangingTheSurface()
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up };
            var ix = new[] { 0, 1, 2, 0, 1, 2 }; var input = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            Assert.AreSame(input, RemeshSurfaceRefine.Apply(input, p, ix, .1f, CancellationToken.None, out _));
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
