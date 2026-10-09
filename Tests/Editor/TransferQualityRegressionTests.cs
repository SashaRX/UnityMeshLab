using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class TransferQualityRegressionTests
    {
        [TestCase(-1f, 8)]
        [TestCase(1f, 6)]
        [TestCase(0f, 6)]
        public void UvEdgeWeldPreservesMirroredTangentSeams(float secondHandedness, int expectedVertices)
        {
            var mesh = new Mesh { name = "Adjacent charts with a tangent seam" };
            Mesh welded = null;
            try {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up,
                    Vector3.right, new Vector3(2, 0, 0), new Vector3(2, 1, 0), new Vector3(1, 1, 0) };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up,
                    Vector2.right, new Vector2(2, 0), new Vector2(2, 1), Vector2.one };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
                mesh.RecalculateNormals();
                if (secondHandedness != 0) {
                    var tangents = new Vector4[8];
                    for (int i = 0; i < tangents.Length; ++i)
                        tangents[i] = new Vector4(1, 0, 0, i < 4 ? 1 : secondHandedness);
                    mesh.tangents = tangents;
                }
                welded = Uv0Analyzer.UvEdgeWeld(mesh);
                Assert.AreEqual(expectedVertices, welded.vertexCount);
                var originalTriangles = mesh.triangles;
                var weldedTriangles = welded.triangles;
                for (int i = 0; i < originalTriangles.Length; ++i) {
                    Assert.AreEqual(mesh.vertices[originalTriangles[i]], welded.vertices[weldedTriangles[i]]);
                    Assert.AreEqual(mesh.uv[originalTriangles[i]], welded.uv[weldedTriangles[i]]);
                    if (secondHandedness != 0)
                        Assert.AreEqual(mesh.tangents[originalTriangles[i]].w, welded.tangents[weldedTriangles[i]].w,
                            "Every triangle corner must keep its tangent handedness.");
                }
            }
            finally {
                if (welded != mesh) Object.DestroyImmediate(welded);
                Object.DestroyImmediate(mesh);
            }
        }

        [TestCase(.0001f)]
        [TestCase(1f)]
        [TestCase(1000f)]
        public void TopologyCorrectionPreservesAnIsometricThinTriangle(float scale)
        {
            var positions = new[] { Vector3.zero, new Vector3(100, 0, 0) * scale, Vector3.up * scale };
            var uv = new[] { new Vector2(.1f, .1f), new Vector2(.9f, .1f), new Vector2(.1f, .108f) };
            var original = (Vector2[])uv.Clone();
            var triangles = new[] { 0, 1, 2 };
            var method = typeof(GroupedShellTransfer).GetMethod("EnforceShellTopologyOnUv2", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            method.Invoke(null, new object[] { uv, positions, triangles, UvShellExtractor.Extract(uv, triangles) });
            CollectionAssert.AreEqual(original, uv, "A valid one-triangle island must not collapse onto its opposite edge.");
            Assert.AreEqual(0, GroupedShellTransfer.LastTopologyFixed);
        }

        [Test]
        public void PipelinePreOptimizationKeepsPointContactUvChartsSeparate()
        {
            var mesh = PointContactAtlas();
            try {
                var originalUv2 = mesh.uv2;
                PreOptimize(mesh);
                Assert.AreEqual(8, mesh.vertexCount, "Coincident UV0 corners are still separate chart vertices.");
                Assert.AreEqual(2, UvShellExtractor.Extract(mesh.uv, mesh.triangles).Count);
                CollectionAssert.AreEqual(originalUv2, mesh.uv2);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void PipelinePreOptimizationPreservesTheTargetAtlasAfterTransfer()
        {
            var source = PointContactAtlas(); var target = Object.Instantiate(source);
            try {
                PreOptimize(target);
                var result = GroupedShellTransfer.Transfer(target, source, sourceAtlasWidth: 512, sourceAtlasHeight: 512);
                var quality = TransferUvQuality.Measure(target, result.uv2, Vector2.one, Matrix4x4.identity);
                Assert.AreEqual(0, quality.degenerateFaces);
                Assert.AreEqual(0, quality.overlapPairs);
                Assert.AreEqual(1, quality.areaWeightedAnisotropy, .001);
                CollectionAssert.AreEqual(source.uv2, result.uv2);
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(target); }
        }

        static void PreOptimize(Mesh mesh)
        {
            var root = new GameObject("Preoptimization regression");
            try {
                var context = new UvToolContext { LodGroup = root.AddComponent<LODGroup>() };
                context.MeshEntries.Add(new MeshEntry { originalMesh = mesh });
                var workflow = new UvTransferWorkflow();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(UvTransferWorkflow).GetField("ctx", flags).SetValue(workflow, context);
                typeof(UvTransferWorkflow).GetMethod("ExecMeshOptimize", flags).Invoke(workflow, null);
            }
            finally { Object.DestroyImmediate(root); }
        }

        static Mesh PointContactAtlas()
        {
            var mesh = new Mesh { name = "Separate point-contact UV charts" };
            mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(0, -1, 0), Vector3.zero, new Vector3(-1, 0, 0),
                Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up };
            mesh.uv = new[] { new Vector2(-1, -1), new Vector2(0, -1), Vector2.zero, new Vector2(-1, 0),
                Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.uv2 = new[] { new Vector2(.05f, .05f), new Vector2(.15f, .05f), new Vector2(.15f, .15f), new Vector2(.05f, .15f),
                new Vector2(.85f, .85f), new Vector2(.95f, .85f), new Vector2(.95f, .95f), new Vector2(.85f, .95f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            mesh.RecalculateNormals(); return mesh;
        }

        [Test]
        public void TopologyMoveRejectsCollapseFlipAndNewStretchButAllowsImprovement()
        {
            var a = Vector2.zero; var b = Vector2.right; var c = Vector2.up;
            Assert.IsFalse(Safe(a, b, c, a, b, new Vector2(.5f, 0)));
            Assert.IsFalse(Safe(a, b, c, a, b, -c));
            Assert.IsFalse(Safe(a, b, c, a, b, c * .2f));
            Assert.IsTrue(Safe(a, b, c * .2f, a, b, c));
        }

        static bool Safe(Vector2 a0, Vector2 b0, Vector2 c0, Vector2 a1, Vector2 b1, Vector2 c1)
            => GroupedShellTransfer.TopologyMovePreservesTriangle(Vector3.zero, Vector3.right, Vector3.up,
                a0, b0, c0, a1, b1, c1);

        [Test]
        public void CandidateScoringDetectsRoundoffWidthButAcceptsIsometricSlivers()
        {
            var triangle = new[] { 0, 1, 2 }; var faces = new List<int> { 0 };
            var uv = new Dictionary<int, Vector2> { [0] = new Vector2(.1f, .1f),
                [1] = new Vector2(.9f, .1f), [2] = new Vector2(.9f, .1000001f) };
            var positions = new[] { Vector3.zero, Vector3.right, Vector3.one };
            Assert.AreEqual(1, GroupedShellTransfer.CountShellIssues(faces, triangle, positions, uv));
            positions[2] = new Vector3(1, (.1000001f - .1f) / .8f, 0);
            Assert.AreEqual(0, GroupedShellTransfer.CountShellIssues(faces, triangle, positions, uv));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReparameterizationDoesNotRestoreZeroAreaAfterRepairingCollapsedUv0(bool singleFace)
        {
            var positions = singleFace ? new[] { Vector3.zero, new Vector3(2, 0, 0), Vector3.up }
                : new[] { Vector3.zero, new Vector3(2, 0, 0), new Vector3(2, 1, 0), Vector3.up };
            var triangles = singleFace ? new[] { 0, 1, 2 } : new[] { 0, 1, 2, 0, 2, 3 };
            var uv = new float[positions.Length * 2];
            for (int i = 0; i < uv.Length; ++i) uv[i] = .333f;
            var vertices = new List<int>(); for (int i = 0; i < positions.Length; ++i) vertices.Add(i);
            var faces = singleFace ? new[] { 0 } : new[] { 0, 1 };
            Assert.IsTrue(ArapParameterization.Reparameterize(positions, triangles, faces, vertices, uv, 25, out _));
            var mesh = new Mesh { vertices = positions, triangles = triangles };
            try {
                var mapped = new Vector2[positions.Length];
                for (int i = 0; i < mapped.Length; ++i) mapped[i] = new Vector2(uv[i * 2], uv[i * 2 + 1]);
                var quality = TransferUvQuality.Measure(mesh, mapped, Vector2.one, Matrix4x4.identity);
                Assert.AreEqual(0, quality.degenerateFaces); Assert.AreEqual(0, quality.overlapPairs);
                Assert.AreEqual(1, quality.areaWeightedAnisotropy, .01);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [TestCase(.01f)]
        [TestCase(.0001f)]
        [TestCase(.000001f)]
        public void SmallUvTrianglesPreserveAllCornersAndCollapsedTrianglesUseTheirEdges(float scale)
        {
            var offset = new Vector2(.1f, .1f);
            var a = offset; var b = offset + Vector2.right * scale; var c = offset + Vector2.up * scale;
            foreach (var vertex in new[] { (a, Vector3.right), (b, Vector3.up), (c, Vector3.forward) }) {
                float distance = TriangleBvh2D.PointToTri2D(vertex.Item1, a, b, c, out float u, out float v, out float w);
                Assert.AreEqual(0, distance);
                Assert.That(Vector3.Distance(vertex.Item2, new Vector3(u, v, w)), Is.LessThan(1e-6f));
            }
            float lineDistance = TriangleBvh2D.PointToTri2D(b, a, b, a, out float lu, out float lv, out float lw);
            Assert.AreEqual(0, lineDistance); Assert.AreEqual(1, lv); Assert.AreEqual(0, lu + lw);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TinyUvChartsTransferWithoutCollapsingInEitherDirection(bool reverse)
        {
            using var input = TransferBenchmarkFixtures.Create("retriangulated-lod");
            foreach (var mesh in new[] { input.source, input.target }) {
                var uv = mesh.uv;
                for (int i = 0; i < uv.Length; ++i) uv[i] = new Vector2(.1f, .1f) + uv[i] * .0001f;
                mesh.uv = uv;
            }
            var source = reverse ? input.target : input.source;
            var target = reverse ? input.source : input.target;
            var result = GroupedShellTransfer.Transfer(target, source, sourceAtlasWidth: 512, sourceAtlasHeight: 512);
            Assert.IsNotNull(result.uv2);
            var quality = TransferUvQuality.Measure(target, result.uv2, Vector2.one, Matrix4x4.identity);
            Assert.AreEqual(0, quality.degenerateFaces); Assert.AreEqual(0, quality.overlapPairs);
            Assert.AreEqual(1, quality.areaWeightedAnisotropy, .003);
            for (int i = 0; i < target.vertexCount; ++i)
                Assert.That(Vector2.Distance(target.uv2[i], result.uv2[i]), Is.LessThan(.0001f));
        }

        [TestCase("texture-1024x2048-corrected")]
        [TestCase("mirrored-stacked-uv0")]
        [TestCase("close-opposite-surfaces")]
        [TestCase("retriangulated-lod")]
        public void ReverseTransferPreservesTheAnalyticAtlas(string fixture)
        {
            using var input = TransferBenchmarkFixtures.Create(fixture);
            var result = GroupedShellTransfer.Transfer(input.source, input.target, sourceAtlasWidth: 512, sourceAtlasHeight: 512);
            Assert.IsNotNull(result.uv2);
            Assert.AreEqual(input.source.vertexCount, result.verticesTransferred);
            var expected = input.source.uv2;
            for (int i = 0; i < expected.Length; ++i)
                Assert.That(Vector2.Distance(expected[i], result.uv2[i]), Is.LessThan(1e-5f), "vertex " + i);
            var quality = TransferUvQuality.Measure(input.source, result.uv2, Vector2.one, Matrix4x4.identity);
            Assert.AreEqual(0, quality.degenerateFaces);
            Assert.AreEqual(0, quality.overlapPairs);
            Assert.IsTrue(quality.overlapScanComplete);
        }

        [Test]
        public void DecimatedVerticesCrossingAThinSheetKeepTheNormalCompatibleAtlas()
        {
            using var input = TransferBenchmarkFixtures.Create("close-opposite-surfaces");
            var result = GroupedShellTransfer.Transfer(input.target, input.source, sourceAtlasWidth: 512, sourceAtlasHeight: 512);
            Assert.IsNotNull(result.uv2);
            for (int i = 0; i < input.reference.Length; ++i)
                Assert.That(Vector2.Distance(input.reference[i], result.uv2[i]), Is.LessThan(1e-5f));
            Assert.AreEqual(0, TransferUvQuality.Measure(input.target, result.uv2, Vector2.one, Matrix4x4.identity).overlapPairs);
        }

        [Test]
        public void DisplacedAuthoredUvRowDoesNotCollapseTheMatchedSurface()
        {
            using var input = TransferBenchmarkFixtures.Create("retriangulated-lod");
            foreach (var mesh in new[] { input.source, input.target }) {
                var uv = mesh.uv;
                for (int i = 0; i < uv.Length; ++i)
                    uv[i].y = uv[i].y * .005f + (mesh == input.target ? .01f : 0);
                mesh.uv = uv;
            }
            var result = GroupedShellTransfer.Transfer(input.target, input.source, sourceAtlasWidth: 512, sourceAtlasHeight: 512);
            Assert.IsNotNull(result.uv2);
            var quality = TransferUvQuality.Measure(input.target, result.uv2, Vector2.one, Matrix4x4.identity);
            Assert.AreEqual(0, quality.degenerateFaces); Assert.AreEqual(0, quality.overlapPairs);
            for (int i = 0; i < input.target.vertexCount; ++i)
                Assert.That(Vector2.Distance(input.target.uv2[i], result.uv2[i]), Is.LessThan(1e-5f));
            Assert.AreEqual(1, result.shellsUv0Aligned);
        }

        [Test]
        public void NearbyParallelChartDoesNotHideAnExactSurfaceMatch()
        {
            var source = new Mesh { name = "Parallel charts" };
            var target = new Mesh { name = "Exact surface fragment" };
            try {
                source.vertices = new[] {
                    new Vector3(0, 0, .01f), new Vector3(1, 0, .01f), new Vector3(1, 1, .01f), new Vector3(0, 1, .01f),
                    Vector3.zero, new Vector3(2, 0, 0), new Vector3(2, 1, 0), Vector3.up
                };
                source.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up,
                    Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                source.uv2 = new[] {
                    new Vector2(.05f, .1f), new Vector2(.35f, .1f), new Vector2(.35f, .4f), new Vector2(.05f, .4f),
                    new Vector2(.55f, .1f), new Vector2(.85f, .1f), new Vector2(.85f, .25f), new Vector2(.55f, .25f)
                };
                source.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
                target.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up };
                target.uv = new[] { Vector2.zero, new Vector2(.5f, 0), new Vector2(.5f, 1), Vector2.up };
                target.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                source.RecalculateNormals(); target.RecalculateNormals();
                var result = GroupedShellTransfer.Transfer(target, source, sourceAtlasWidth: 512, sourceAtlasHeight: 512);
                Assert.AreEqual(1, result.targetShellToSourceShell[0]);
                var expected = new[] { new Vector2(.55f, .1f), new Vector2(.7f, .1f),
                    new Vector2(.7f, .25f), new Vector2(.55f, .25f) };
                for (int i = 0; i < expected.Length; ++i)
                    Assert.That(Vector2.Distance(expected[i], result.uv2[i]), Is.LessThan(1e-5f));
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(target); }
        }

        [Test]
        public void ExactFragmentOfCurvedSourceWinsAgainstRemoteNormalAlignedChart()
        {
            var source = new Mesh { name = "U shaped chart and remote wall" };
            var target = new Mesh { name = "Exact side of U shaped chart" };
            try {
                source.vertices = new[] {
                    new Vector3(.02f, 0, 0), new Vector3(.02f, 1, 0),
                    new Vector3(.02f, 1, 1), new Vector3(.02f, 0, 1),
                    Vector3.zero, new Vector3(2, 0, 0), new Vector3(2, 1, 0), Vector3.up,
                    Vector3.forward, new Vector3(0, 1, 1), new Vector3(2, 0, 1), new Vector3(2, 1, 1)
                };
                source.uv = new[] {
                    Vector2.right, Vector2.one, Vector2.up, Vector2.zero,
                    Vector2.right, new Vector2(3, 0), new Vector2(3, 1), Vector2.one,
                    Vector2.zero, Vector2.up, new Vector2(4, 0), new Vector2(4, 1)
                };
                var sourceUv2 = source.uv;
                for (int i = 0; i < sourceUv2.Length; ++i)
                    sourceUv2[i] = sourceUv2[i] * .18f + new Vector2(.1f, i < 4 ? .1f : .55f);
                source.uv2 = sourceUv2;
                source.triangles = new[] {
                    0, 1, 2, 0, 2, 3,
                    4, 5, 6, 4, 6, 7, 4, 7, 9, 4, 9, 8, 5, 10, 11, 5, 11, 6
                };
                target.vertices = new[] { Vector3.zero, Vector3.up, new Vector3(0, 1, 1), Vector3.forward };
                target.uv = new[] { Vector2.right, Vector2.one, Vector2.up, Vector2.zero };
                target.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                source.RecalculateNormals(); target.RecalculateNormals();
                var result = GroupedShellTransfer.Transfer(target, source, sourceAtlasWidth: 512, sourceAtlasHeight: 512);
                Assert.AreEqual(1, result.targetShellToSourceShell[0],
                    "The source chart's average normal is orthogonal to this exact fragment.");
                var sourceVertices = new[] { 4, 7, 9, 8 };
                for (int i = 0; i < sourceVertices.Length; ++i)
                    Assert.That(Vector2.Distance(sourceUv2[sourceVertices[i]], result.uv2[i]), Is.LessThan(1e-5f));
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(target); }
        }

        [Test]
        public void RetainedVerticesPreserveUv2WhenAuthoredUv0CornersCoincide()
        {
            using var input = TransferBenchmarkFixtures.Create("retriangulated-lod");
            var source = Object.Instantiate(input.target);
            try {
                var uv = source.uv; uv[1] = uv[0]; source.uv = uv;
                input.target.uv = uv;
                var result = GroupedShellTransfer.Transfer(input.target, source, sourceAtlasWidth: 512, sourceAtlasHeight: 512);
                CollectionAssert.AreEqual(source.uv2, result.uv2);
                var quality = TransferUvQuality.Measure(input.target, result.uv2, Vector2.one, Matrix4x4.identity);
                Assert.AreEqual(0, quality.degenerateFaces); Assert.AreEqual(0, quality.overlapPairs);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void CleanSourceAtlasSupportsReverseTransferDespiteCollapsedAuthoredUv0()
        {
            using var input = TransferBenchmarkFixtures.Create("retriangulated-lod");
            foreach (var mesh in new[] { input.source, input.target }) {
                var uv = mesh.uv; for (int i = 0; i < uv.Length; ++i) uv[i] = new Vector2(.3f, .3f);
                mesh.uv = uv;
            }
            var positions = input.source.vertices;
            for (int i = 0; i < positions.Length; ++i) positions[i].z += .01f;
            input.source.vertices = positions;
            var result = GroupedShellTransfer.Transfer(input.source, input.target, sourceAtlasWidth: 512, sourceAtlasHeight: 512);
            Assert.AreEqual(1, result.shellsGeometryFallback);
            for (int i = 0; i < input.source.vertexCount; ++i)
                Assert.That(Vector2.Distance(input.source.uv2[i], result.uv2[i]), Is.LessThan(1e-5f));
            var quality = TransferUvQuality.Measure(input.source, result.uv2, Vector2.one, Matrix4x4.identity);
            Assert.AreEqual(0, quality.degenerateFaces); Assert.AreEqual(0, quality.overlapPairs);
        }

        [TestCase(SymmetrySplitShells.ThresholdMode.LegacyFixed)]
        [TestCase(SymmetrySplitShells.ThresholdMode.Adaptive)]
        public void DenseCleanChartIsNotMistakenForBinarySymmetry(SymmetrySplitShells.ThresholdMode mode)
        {
            var savedMode = SymmetrySplitShells.CurrentThresholdMode;
            var mesh = DenseChart();
            try {
                SymmetrySplitShells.CurrentThresholdMode = mode;
                var original = TransferMeshSnapshot.Capture(mesh);
                var shells = UvShellExtractor.Extract(mesh.uv, mesh.triangles, true);
                Assert.IsFalse(SymmetrySplitShells.HasUv0Overlap(shells[0], mesh.uv, mesh.triangles));
                var partitions = SpatialPartitioner.PartitionShells(shells, mesh.uv, mesh.triangles, mesh.vertices);
                Assert.IsFalse(partitions[0].hasOverlap);
                Assert.AreEqual(1, partitions[0].partitionCount);
                Assert.AreEqual(0, SymmetrySplitShells.Split(mesh, shells));
                CollectionAssert.AreEqual(original, TransferMeshSnapshot.Capture(mesh));
            }
            finally { SymmetrySplitShells.CurrentThresholdMode = savedMode; Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void PrescribedRotationalSymmetryDoesNotCutADenseCleanTarget()
        {
            var mesh = DenseChart();
            try {
                var original = TransferMeshSnapshot.Capture(mesh);
                var shells = UvShellExtractor.Extract(mesh.uv, mesh.triangles, true);
                var parameters = new List<SymmetrySplitShells.SplitParams> {
                    new SymmetrySplitShells.SplitParams { foldCount = 4, axis = 2, center = new Vector3(2, 2, 0) }
                };
                Assert.AreEqual(0, SymmetrySplitShells.SplitWithParams(mesh, shells, parameters));
                CollectionAssert.AreEqual(original, TransferMeshSnapshot.Capture(mesh));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ActualStackingIsDetectedDespiteDifferentTriangulation()
        {
            var uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            var triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 1, 3, 1, 2, 3 };
            var shells = UvShellExtractor.Extract(uv, triangles);
            Assert.IsTrue(SymmetrySplitShells.HasUv0Overlap(shells[0], uv, triangles));
        }

        [TestCase(SymmetrySplitShells.ThresholdMode.LegacyFixed)]
        [TestCase(SymmetrySplitShells.ThresholdMode.Adaptive)]
        public void ActualBinaryFoldIsStillSplit(SymmetrySplitShells.ThresholdMode mode)
        {
            var savedMode = SymmetrySplitShells.CurrentThresholdMode;
            var mesh = DenseChart();
            try {
                SymmetrySplitShells.CurrentThresholdMode = mode;
                var uv = mesh.uv; var positions = mesh.vertices;
                for (int i = 0; i < uv.Length; ++i) uv[i].x = Mathf.Abs(positions[i].x - 2) * .001f;
                mesh.uv = uv;
                var shells = UvShellExtractor.Extract(uv, mesh.triangles, true);
                Assert.IsTrue(SymmetrySplitShells.HasUv0Overlap(shells[0], uv, mesh.triangles));
                Assert.Greater(SymmetrySplitShells.Split(mesh, shells), 0);
                Assert.Greater(mesh.vertexCount, positions.Length);
                Assert.AreEqual(2, shells.Count);
                foreach (var shell in shells)
                    Assert.IsFalse(SymmetrySplitShells.HasUv0Overlap(shell, mesh.uv, mesh.triangles));
            }
            finally { SymmetrySplitShells.CurrentThresholdMode = savedMode; Object.DestroyImmediate(mesh); }
        }

        static Mesh DenseChart()
        {
            const int width = 4;
            var p = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            for (int y = 0; y <= width; ++y)
                for (int x = 0; x <= width; ++x) {
                    p.Add(new Vector3(x, y, 0)); uv.Add(new Vector2(x, y) * .001f);
                }
            for (int y = 0; y < width; ++y)
                for (int x = 0; x < width; ++x) {
                    int a = y * (width + 1) + x, b = a + 1, c = b + width + 1, d = a + width + 1;
                    tri.AddRange(new[] { a, b, c, a, c, d });
                }
            var mesh = new Mesh { name = "Dense clean UV chart" };
            mesh.SetVertices(p); mesh.SetUVs(0, uv); mesh.SetTriangles(tri, 0); mesh.RecalculateNormals();
            return mesh;
        }
    }
}
