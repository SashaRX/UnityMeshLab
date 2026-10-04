using System;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshTopologyTests
    {
        static readonly int[] Tetrahedron = { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3 };
        static Vector3[] TetraPositions() => new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward };

        [TestCase(1f)]
        [TestCase(.001f)]
        public void FittedSolidHoleRetriesBeforeTrimAndKeepsClosedGeometry(float scale)
        {
            var p = TetraPositions();
            for (int v = 0; v < p.Length; v++) p[v] *= scale;
            var opened = new RemeshNative.IndexedMesh { positions = p, indices = new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3 } };
            var closed = new RemeshNative.IndexedMesh { positions = (Vector3[])p.Clone(), indices = (int[])Tetrahedron.Clone() };
            var original = (int[])opened.indices.Clone(); int retries = 0;
            var result = RemeshNative.GuardVoxelSolid(opened, 1u, 256, CancellationToken.None, flags => {
                Assert.AreEqual(0u, flags); retries++; return closed;
            });
            Assert.AreEqual(1, retries); Assert.AreSame(closed, result);
            var topology = RemeshTopology.Inspect(result.positions, result.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count);
            CollectionAssert.AreEqual(original, opened.indices); CollectionAssert.AreEqual(p, opened.positions);
        }

        [Test]
        public void ClosedVoxelAndExplicitShellDoNotChangeGeometryOrRequestRetry()
        {
            var closed = new RemeshNative.IndexedMesh { positions = TetraPositions(), indices = (int[])Tetrahedron.Clone() };
            var sheet = new RemeshNative.IndexedMesh { positions = TetraPositions(), indices = new[] { 0, 2, 1 } };
            foreach (uint flags in new[] { 0u, 1u })
                Assert.AreSame(closed, RemeshNative.GuardVoxelSolid(closed, flags, 256, CancellationToken.None, _ => throw new Exception("Unexpected retry")));
            Assert.AreSame(sheet, RemeshNative.GuardVoxelSolid(sheet, 3u, 256, CancellationToken.None, _ => throw new Exception("Unexpected retry")));
        }

        [Test]
        public void SolidVoxelCannotPassAnUnrecoverableOpeningIntoSimplify()
        {
            var opened = new RemeshNative.IndexedMesh { positions = TetraPositions(), indices = new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3 } };
            Assert.Throws<InvalidOperationException>(() => RemeshNative.GuardVoxelSolid(opened, 1u, 256, CancellationToken.None, _ => opened));
            Assert.Throws<InvalidOperationException>(() => RemeshNative.GuardVoxelSolid(opened, 0u, 256, CancellationToken.None, _ => throw new Exception("Unexpected retry")));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => RemeshNative.GuardVoxelSolid(opened, 1u, 256, cancelled.Token, _ => opened));
        }

        [Test]
        public void FittedSolidRetriesInvalidWeldsEvenWithoutBoundaryEdges()
        {
            var invalid = new RemeshNative.IndexedMesh { positions = TetraPositions(),
                indices = new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3, 0, 2, 1, 0, 1, 2 } };
            var info = RemeshTopology.Inspect(invalid.positions, invalid.indices);
            Assert.IsFalse(info.Valid); Assert.AreEqual(0, info.boundary.Count);
            var closed = new RemeshNative.IndexedMesh { positions = TetraPositions(), indices = (int[])Tetrahedron.Clone() };
            int retries = 0;
            Assert.AreSame(closed, RemeshNative.GuardVoxelSolid(invalid, 1u, 256, CancellationToken.None, flags => {
                Assert.AreEqual(0u, flags); retries++; return closed;
            }));
            Assert.AreEqual(1, retries);
        }

        [TestCase(1f)]
        [TestCase(.001f)]
        public void TrimKeepsClosedVolumeAtSharpCorner(float scale)
        {
            var source = TetraPositions(); var p = TetraPositions();
            for (int i = 0; i < p.Length; i++) { source[i] *= scale; p[i] = (p[i] * .01f + Vector3.one * .001f) * scale; }
            // This miniature closed tetrahedron sits at a source corner. Its cap's
            // outward normal has no parallel source face within the trim reach.
            var mesh = new RemeshNative.IndexedMesh { positions = p, indices = (int[])Tetrahedron.Clone() };
            var result = RemeshTrim.Trim(mesh, source, Tetrahedron, scale * .05f, CancellationToken.None);
            Assert.AreEqual(0, result.removed);
            var topology = RemeshTopology.Inspect(result.mesh.positions, result.mesh.indices);
            Assert.IsTrue(topology.Valid, topology.Description);
            Assert.AreEqual(0, topology.boundary.Count);
        }

        [Test]
        public void ClosedVolumeDetectionSeparatesVolumeFromSheetsAndOppositeCopies()
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward,
                new Vector3(3, 0, 0), new Vector3(4, 0, 0), new Vector3(3, 1, 0) };
            var ix = new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3, 4, 5, 6, 4, 6, 5 };
            var closed = RemeshTopology.ClosedVolumeFaces(p, ix, CancellationToken.None);
            for (int i = 0; i < 4; i++) Assert.IsTrue(closed[i]);
            Assert.IsFalse(closed[4]); Assert.IsFalse(closed[5]);
        }

        [Test]
        public void MixedSourceKeepsClosedCornerButStillTrimsTheBackOfAnOpenSheet()
        {
            var source = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward,
                new Vector3(4, 0, 0), new Vector3(5, 0, 0), new Vector3(4, 1, 0) };
            var p = (Vector3[])source.Clone();
            for (int i = 0; i < 4; i++) p[i] = p[i] * .01f + Vector3.one * .001f;
            var input = new RemeshNative.IndexedMesh { positions = p,
                indices = new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3, 4, 5, 6, 4, 6, 5 } };
            var result = RemeshTrim.Trim(input, source, new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3, 4, 5, 6 }, .05f, CancellationToken.None);
            Assert.AreEqual(1, result.removed); Assert.AreEqual(RemeshTrim.Back, result.classes[5]);
            for (int f = 0; f < 5; f++) Assert.AreEqual(RemeshTrim.Kept, result.classes[f]);
            var topology = RemeshTopology.Inspect(result.mesh.positions, result.mesh.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(3, topology.boundary.Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FinPairRemovalPreservesClosedSurfaceAndDoesNotMutateInput(bool splitVertices)
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, new Vector3(.5f, -1, 0), Vector3.zero, Vector3.right };
            var ix = new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3, 0, 1, 4, splitVertices ? 6 : 1, splitVertices ? 5 : 0, 4 };
            var original = (int[])ix.Clone();
            var mesh = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            var result = RemeshTopology.RemoveCollapsedFins(mesh, CancellationToken.None, out int removed);
            Assert.AreEqual(2, removed); Assert.AreEqual(4, result.TriangleCount);
            CollectionAssert.AreEqual(original, ix);
            var topology = RemeshTopology.Inspect(result.positions, result.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count);
        }

        [Test]
        public void FinRemovalDoesNotOpenNeighbouringFaceOrEraseAnIsolatedDoubleSheet()
        {
            var p = TetraPositions();
            foreach (var ix in new[] { new[] { 0, 1, 2, 0, 2, 1 }, new[] { 0, 1, 2, 0, 2, 1, 0, 1, 3 } }) {
                var mesh = new RemeshNative.IndexedMesh { positions = p, indices = ix };
                var result = RemeshTopology.RemoveCollapsedFins(mesh, CancellationToken.None, out int removed);
                Assert.AreEqual(0, removed); Assert.AreSame(mesh, result);
            }
        }

        [Test]
        public void SnapshotFindsDisconnectedVertexFansDespiteManifoldEdges()
        {
            var mesh = RemeshTopology.Inspect(new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.left, Vector3.down },
                new[] { 0, 1, 2, 0, 3, 4 });
            Assert.AreEqual(0, mesh.nonManifoldEdges); Assert.AreEqual(1, mesh.nonManifoldVertices);
            Assert.IsFalse(mesh.Valid);
        }

        [Test]
        public void BoundaryGateRejectsNewHoleAndClosingAnExistingOpening()
        {
            var p = TetraPositions();
            var closed = RemeshTopology.Inspect(p, Tetrahedron);
            var opened = RemeshTopology.Inspect(p, new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3 });
            Assert.IsFalse(opened.PreservesBoundary(closed));
            Assert.IsFalse(closed.PreservesBoundary(opened));
        }

        [Test]
        public void TopologyWorkHonoursCancellation()
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => RemeshTopology.Inspect(TetraPositions(), Tetrahedron, cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => RemeshTopology.ClosedVolumeFaces(TetraPositions(), Tetrahedron, cancellation.Token));
        }

        static void RequireNative()
        {
            try { RemeshNative.CheckAvailable(); }
            catch (InvalidOperationException error) { Assert.Ignore(error.Message); }
        }

        [Test]
        public void NativeFittedVoxelDoesNotLeaveCleanupHoleInRotatedThinBox()
        {
            RequireNative();
            // Eight source vertices reproduce a one-face native Clean deletion
            // at resolution 48. Constants retain the captured float32 positions.
            var p = new[] {
                new Vector3(-.0020011793822050095f, -.001458699000068009f, -.0001976821367861703f),
                new Vector3(.00184518878813833f, -.0011100758565589786f, -.0012387937167659402f),
                new Vector3(.0018915702821686864f, .0016025769291445613f, -.00015908811474218965f),
                new Vector3(-.001954797888174653f, .0012539536692202091f, .0008820234215818346f),
                new Vector3(-.0018915702821686864f, -.0016025769291445613f, .00015908811474218965f),
                new Vector3(.001954797888174653f, -.0012539536692202091f, -.0008820234215818346f),
                new Vector3(.0020011793822050095f, .001458699000068009f, .0001976821367861703f),
                new Vector3(-.00184518878813833f, .0011100758565589786f, .0012387937167659402f) };
            var ix = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4,
                1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };
            var positionsBefore = (Vector3[])p.Clone(); var indicesBefore = (int[])ix.Clone();
            var settings = new RemeshSettings { voxelResolution = 48, solve = true, shell = false };
            var fitted = RemeshNative.Voxelize(p, ix, settings, CancellationToken.None);
            var topology = RemeshTopology.Inspect(fitted.positions, fitted.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count);
            Assert.Greater(fitted.TriangleCount, 0); Assert.IsTrue(settings.solve);
            var repeated = RemeshNative.Voxelize(p, ix, settings, CancellationToken.None);
            CollectionAssert.AreEqual(fitted.positions, repeated.positions); CollectionAssert.AreEqual(fitted.indices, repeated.indices);
            CollectionAssert.AreEqual(positionsBefore, p); CollectionAssert.AreEqual(indicesBefore, ix);
        }

        [Test]
        public void AggressiveNativeSimplifyRetainsNonemptyClosedVolume()
        {
            RequireNative();
            var p = TetraPositions(); var ix = (int[])Tetrahedron.Clone();
            var input = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            var settings = new RemeshSettings { targetTriangles = 1, maximumError = 1, preserveFolds = false, pruneSmallParts = true };
            var result = RemeshNative.Simplify(input, settings, CancellationToken.None, out _);
            Assert.GreaterOrEqual(result.TriangleCount, 4);
            var topology = RemeshTopology.Inspect(result.positions, result.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count);
            CollectionAssert.AreEqual(Tetrahedron, ix);
        }

        [Test]
        public void NativeSimplifyKeepsTheExistingBorderOfAnOpenGrid()
        {
            RequireNative();
            var p = new Vector3[25]; var ix = new int[4 * 4 * 6]; int write = 0;
            for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++) p[y * 5 + x] = new Vector3(x, y, 0);
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) {
                int a = y * 5 + x;
                foreach (int v in new[] { a, a + 1, a + 6, a, a + 6, a + 5 }) ix[write++] = v;
            }
            var input = new RemeshNative.IndexedMesh { positions = p, indices = ix };
            var settings = new RemeshSettings { targetTriangles = 2, maximumError = .5f, preserveFolds = false, pruneSmallParts = false };
            var result = RemeshNative.Simplify(input, settings, CancellationToken.None, out _);
            var topology = RemeshTopology.Inspect(result.positions, result.indices);
            Assert.IsTrue(topology.Valid, topology.Description);
            Assert.IsTrue(topology.PreservesBoundary(RemeshTopology.Inspect(p, ix)));
            Assert.Less(result.TriangleCount, input.TriangleCount);
        }

        [Test]
        public void NativeAreaCleanupCannotOpenAThinClosedInput()
        {
            RequireNative();
            var p = TetraPositions(); p[3] *= 1e-8f;
            var input = new RemeshNative.IndexedMesh { positions = p, indices = (int[])Tetrahedron.Clone() };
            var settings = new RemeshSettings { targetTriangles = 4, maximumError = .01f, pruneSmallParts = false };
            var result = RemeshNative.Simplify(input, settings, CancellationToken.None, out float error);
            var topology = RemeshTopology.Inspect(result.positions, result.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count);
            CollectionAssert.AreEqual(p, result.positions); CollectionAssert.AreEqual(Tetrahedron, result.indices);
            Assert.AreEqual(0, error);
        }

        [Test]
        public void ComponentGatePreservesGenusAndOnlyAllowsRequestedPartRemoval()
        {
            const int ring = 8, tube = 4;
            var p = new Vector3[ring * tube]; var ix = new int[ring * tube * 6]; int write = 0;
            for (int r = 0; r < ring; r++) for (int t = 0; t < tube; t++) {
                float u = 2 * Mathf.PI * r / ring, v = 2 * Mathf.PI * t / tube;
                p[r * tube + t] = new Vector3((2 + Mathf.Cos(v)) * Mathf.Cos(u), (2 + Mathf.Cos(v)) * Mathf.Sin(u), Mathf.Sin(v));
                int a = r * tube + t, b = ((r + 1) % ring) * tube + t;
                int c = ((r + 1) % ring) * tube + (t + 1) % tube, d = r * tube + (t + 1) % tube;
                foreach (int index in new[] { a, b, c, a, c, d }) ix[write++] = index;
            }
            var torus = RemeshTopology.Inspect(p, ix);
            var tetra = RemeshTopology.Inspect(TetraPositions(), Tetrahedron);
            Assert.IsTrue(torus.Valid, torus.Description); Assert.AreEqual(0, torus.euler[0]);
            Assert.IsTrue(torus.PreservesBoundary(tetra));
            Assert.IsFalse(tetra.PreservesComponents(torus, true));
            var twoParts = RemeshTopology.Inspect(new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward,
                Vector3.right * 4, Vector3.right * 5, Vector3.right * 4 + Vector3.up, Vector3.right * 4 + Vector3.forward },
                new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3, 4, 6, 5, 4, 5, 7, 5, 6, 7, 6, 4, 7 });
            Assert.IsTrue(tetra.PreservesComponents(twoParts, true));
            Assert.IsFalse(tetra.PreservesComponents(twoParts, false));
        }
    }
}
