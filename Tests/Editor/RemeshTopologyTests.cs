using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshTopologyTests
    {
        static readonly int[] Tetrahedron = { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3 };
        static Vector3[] TetraPositions() => new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward };

        [Test]
        public void FailedSolidObserverReceivesBothAttemptsAndNeverRunsOnSuccessOrCancellation()
        {
            var opened = new RemeshNative.IndexedMesh { positions = TetraPositions(), indices = new[] { 0, 2, 1 } };
            var retry = new RemeshNative.IndexedMesh { positions = TetraPositions(), indices = new[] { 0, 1, 3 } };
            int observations = 0;
            Assert.Throws<InvalidOperationException>(() => RemeshNative.GuardVoxelSolid(opened, 1u, 128,
                CancellationToken.None, _ => retry, (first, rejected, flags, reason) => {
                    observations++; Assert.AreSame(opened, first); Assert.AreSame(retry, rejected);
                    Assert.AreEqual(0u, flags); StringAssert.Contains("boundary", reason);
                }));
            Assert.AreEqual(1, observations);
            Assert.Throws<InvalidOperationException>(() => RemeshNative.GuardVoxelSolid(opened, 0u, 128,
                CancellationToken.None, _ => throw new Exception("Unexpected retry"), (first, rejected, flags, _) => {
                    observations++; Assert.AreSame(opened, first); Assert.AreSame(opened, rejected); Assert.AreEqual(0u, flags);
                }));
            Assert.AreEqual(2, observations);
            var closed = new RemeshNative.IndexedMesh { positions = TetraPositions(), indices = (int[])Tetrahedron.Clone() };
            RemeshNative.GuardVoxelSolid(opened, 1u, 128, CancellationToken.None, _ => closed,
                (_, _, _, _) => Assert.Fail("Successful fallback must not produce a failure capture"));
            RemeshNative.GuardVoxelSolid(closed, 0u, 128, CancellationToken.None, _ => closed,
                (_, _, _, _) => Assert.Fail("Valid solid must not produce a failure capture"));
            RemeshNative.GuardVoxelSolid(opened, 2u, 128, CancellationToken.None, _ => closed,
                (_, _, _, _) => Assert.Fail("Shell bypass must not be reported as a rejected solid"));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => RemeshNative.GuardVoxelSolid(opened, 1u, 128,
                cancellation.Token, _ => retry, (_, _, _, _) => Assert.Fail("Cancellation is not topology failure")));
        }

        [Test]
        public void FailureCaptureRoundTripsAllStagesAndPrunesOnlyCompletedFailureFiles()
        {
            string directory = Path.Combine(Path.GetTempPath(), "meshlab-failure-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {
                string unrelated = Path.Combine(directory, "unrelated.bin"); File.WriteAllText(unrelated, "keep");
                var source = TetraPositions(); var original = (int[])Tetrahedron.Clone();
                var raw = new RemeshNative.IndexedMesh { positions = TetraPositions(), indices = new[] { 0, 2, 1 } };
                var input = new RemeshNative.IndexedMesh { positions = TetraPositions(), indices = new[] { 0, 1, 3 } };
                var metadata = new RemeshGeometryDiagnostics.FailureMetadata {
                    stage = "Simplify", node = "Synthetic tetra", reason = "invalid topology", resolution = 128,
                    initialFlags = 1, resultFlags = 0, settingsJson = JsonUtility.ToJson(new RemeshSettings { shell = true })
                };
                for (int i = 0; i < 4; i++) {
                    string path = RemeshGeometryDiagnostics.WriteFailure(directory, source, original, i == 3 ? null : raw, input, metadata);
                    using var reader = new BinaryReader(File.OpenRead(path));
                    Assert.AreEqual(0x524D4C42, reader.ReadInt32()); Assert.AreEqual(2, reader.ReadInt32());
                    var readMetadata = JsonUtility.FromJson<RemeshGeometryDiagnostics.FailureMetadata>(reader.ReadString());
                    Assert.AreEqual(metadata.node, readMetadata.node); Assert.AreEqual(metadata.stage, readMetadata.stage);
                    Assert.AreEqual(128, readMetadata.resolution); Assert.AreEqual(0u, readMetadata.resultFlags);
                    Assert.IsTrue(JsonUtility.FromJson<RemeshSettings>(readMetadata.settingsJson).shell);
                    AssertCaptureMesh(reader, source, original);
                    if (i == 3) Assert.IsFalse(reader.ReadBoolean());
                    else AssertCaptureMesh(reader, raw.positions, raw.indices);
                    AssertCaptureMesh(reader, input.positions, input.indices);
                    Assert.AreEqual(reader.BaseStream.Length, reader.BaseStream.Position);
                }
                Assert.AreEqual(3, Directory.GetFiles(directory, "remesh_failure_*.bin").Length);
                Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length);
                Assert.IsTrue(File.Exists(unrelated));
                CollectionAssert.AreEqual(Tetrahedron, original);
                CollectionAssert.AreEqual(new[] { 0, 1, 3 }, input.indices);
            }
            finally { Directory.Delete(directory, true); }
        }

        static void AssertCaptureMesh(BinaryReader reader, Vector3[] positions, int[] indices)
        {
            Assert.IsTrue(reader.ReadBoolean());
            Assert.AreEqual(positions.Length, reader.ReadInt32()); Assert.AreEqual(indices.Length, reader.ReadInt32());
            foreach (var p in positions) {
                Assert.AreEqual(p.x, reader.ReadSingle()); Assert.AreEqual(p.y, reader.ReadSingle()); Assert.AreEqual(p.z, reader.ReadSingle());
            }
            foreach (int index in indices) Assert.AreEqual(index, reader.ReadInt32());
        }

        static void AssertCompactedTetraPositions(Vector3[] input, Vector3[] output)
        {
            // Native compaction visits the first triangle (0,2,1) first. Compare
            // scalar coordinates with a tolerance far below the apex height;
            // Vector3's approximate == treats the
            // thin apex as equal to the origin and confuses multiset assertions.
            Assert.AreEqual(4, output.Length);
            int[] order = { 0, 2, 1, 3 };
            float tolerance = input[1].magnitude * 1e-12f;
            for (int v = 0; v < order.Length; ++v) {
                Assert.AreEqual(input[order[v]].x, output[v].x, tolerance);
                Assert.AreEqual(input[order[v]].y, output[v].y, tolerance);
                Assert.AreEqual(input[order[v]].z, output[v].z, tolerance);
            }
        }

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

        [TestCase(0u)] [TestCase(1u)]
        public void SolidVoxelGuardRepairsFinWithoutRetryAndRetainsStrictRefusal(uint flags)
        {
            var p = new[] {Vector3.zero,Vector3.right,Vector3.up,Vector3.forward,new Vector3(.5f,-1,0)};
            var ix = new[] {0,2,1,0,1,3,1,2,3,2,0,3,0,1,4,1,0,4};
            var input = new RemeshNative.IndexedMesh {positions=p,indices=ix};
            var output = RemeshNative.GuardVoxelSolid(input,flags,256,default,_=>throw new Exception("Unexpected fallback"));
            Assert.AreEqual(4,output.TriangleCount); CollectionAssert.AreEqual(ix,input.indices);
            Assert.IsTrue(RemeshTopology.Inspect(output.positions,output.indices).Valid);
            var broken = new RemeshNative.IndexedMesh {positions=p,indices=new[] {0,2,1,0,1,3,1,2,3,0,1,4,1,0,4}};
            Assert.Throws<InvalidOperationException>(()=>RemeshNative.GuardVoxelSolid(broken,flags,256,default,_=>broken));
            Assert.AreSame(input,RemeshNative.GuardVoxelSolid(input,flags|2,256,default,_=>throw new Exception("Unexpected shell fallback")));
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

        [TestCase(257)]
        [TestCase(512)]
        public void WideNativeVoxelProducesClosedSurfaceAndPreparedChannels(int resolution)
        {
            RequireNative();
            var p = new[] {
                new Vector3(-1, -.04f, -.02f), new Vector3(1, -.04f, -.02f),
                new Vector3(1, .04f, -.02f), new Vector3(-1, .04f, -.02f),
                new Vector3(-1, -.04f, .02f), new Vector3(1, -.04f, .02f),
                new Vector3(1, .04f, .02f), new Vector3(-1, .04f, .02f) };
            var ix = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4,
                3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 };
            var settings = new RemeshSettings { voxelResolution = resolution, solve = true, shell = false };
            var result = RemeshNative.Voxelize(p, ix, settings, CancellationToken.None);
            var topology = RemeshTopology.Inspect(result.positions, result.indices);
            Assert.IsTrue(topology.Valid, topology.Description);
            Assert.AreEqual(0, topology.boundary.Count);
            Assert.Greater(result.TriangleCount, 0);
            Assert.AreEqual(result.positions.Length, result.normals.Length);
            Assert.AreEqual(result.positions.Length, result.uv.Length);
            Assert.IsTrue(result.draftUv);
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
            AssertCompactedTetraPositions(p, result.positions);
            Assert.AreEqual(4, result.TriangleCount);
            CollectionAssert.AreEqual(Tetrahedron, input.indices);
            Assert.AreEqual(0, error);
        }

        [TestCase(1f)]
        [TestCase(.001f)]
        public void NativeSimplifyDirectlyRetainsThinFacesWithoutManagedFallback(float scale)
        {
            RequireNative();
            var p = TetraPositions(); p[3] *= 1e-8f;
            for (int v = 0; v < p.Length; ++v) p[v] *= scale;
            var original = (Vector3[])p.Clone(); var ix = (int[])Tetrahedron.Clone();
            var settings = new MeshSimplifier.GeometrySettings { targetTriangles = 4, maximumError = 0 };
            var result = MeshSimplifier.SimplifyGeometry(p, ix, settings, CancellationToken.None, out float error);
            var topology = RemeshTopology.Inspect(result.positions, result.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count);
            Assert.AreEqual(12, result.indices.Length); Assert.AreEqual(0, error);
            AssertCompactedTetraPositions(p, result.positions);
            CollectionAssert.AreEqual(original, p); CollectionAssert.AreEqual(Tetrahedron, ix);
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
