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

        [TestCase(.6f, 0)]
        [TestCase(0f, 0)]
        [TestCase(-.2f, 1)]
        public void SharedSourceOverlapRequiresPositiveTriangleArea(float corner, int expected)
        {
            var uv = FragmentUvs(corner);
            var triangles = new[] { 0, 1, 2, 3, 4, 5 };
            var shells = UvShellExtractor.Extract(uv, triangles);
            Assert.AreEqual(expected, (int)GroupedShellTransfer.CompareShellUvOverlap(shells[0], shells[1], uv, triangles));
        }

        [Test]
        public void SharedSourceOverlapBudgetDoesNotCertifyFragmentsAsClean()
        {
            var uv = FragmentUvs(.6f);
            var triangles = new[] { 0, 1, 2, 3, 4, 5 };
            var shells = UvShellExtractor.Extract(uv, triangles);
            Assert.AreEqual(GroupedShellTransfer.ShellUvOverlap.Incomplete,
                GroupedShellTransfer.CompareShellUvOverlap(shells[0], shells[1], uv, triangles, comparisonBudget: 0));
        }

        static Vector2[] FragmentUvs(float corner) => new[] { Vector2.zero, Vector2.right, Vector2.up,
            Vector2.one, new Vector2(corner, 1), new Vector2(1, corner) };

        [Test]
        public void DisjointUvFragmentsWithIntersectingBoundsKeepTheirSharedSource()
        {
            var source = new Mesh { name = "Shared square chart" };
            var target = new Mesh { name = "Disjoint triangular fragments" };
            try {
                // Another physical surface has the same authored UV0. This prevents
                // pre-merging the fragments and exercises shared-source dedup itself.
                source.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up,
                    Vector3.forward, new Vector3(1, 0, 1), Vector3.one, new Vector3(0, 1, 1) };
                source.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up,
                    Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                source.uv2 = new[] { new Vector2(.1f, .1f), new Vector2(.45f, .1f),
                    new Vector2(.45f, .45f), new Vector2(.1f, .45f), new Vector2(.55f, .55f), new Vector2(.9f, .55f),
                    new Vector2(.9f, .9f), new Vector2(.55f, .9f) };
                source.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }; source.RecalculateNormals();
                var uv = FragmentUvs(.6f);
                var vertices = new Vector3[uv.Length];
                for (int i = 0; i < uv.Length; i++) vertices[i] = new Vector3(uv[i].x, uv[i].y, 0);
                target.vertices = vertices; target.uv = uv;
                target.triangles = new[] { 0, 1, 2, 3, 4, 5 }; target.RecalculateNormals();
                var result = GroupedShellTransfer.Transfer(target, source);
                Assert.AreEqual(2, result.shellsMatched);
                Assert.AreEqual(0, result.dedupConflicts, "Disjoint UV triangles are not a shared-source conflict.");
                Assert.AreEqual(0, result.shellsMerged, "Disjoint fragments must not be evicted or forced into a merged fallback.");
                CollectionAssert.AreEqual(new[] { 0, 0 }, result.targetShellToSourceShell);
                for (int i = 0; i < uv.Length; i++)
                    Assert.That(Vector2.Distance(uv[i] * .35f + Vector2.one * .1f, result.uv2[i]), Is.LessThan(1e-5));
                Assert.AreEqual(0, UvAtlasDiagnostics.Measure(new RemeshNative.Geometry
                    { uv = result.uv2, indices = target.triangles, charts = new int[target.vertexCount] },
                    System.Threading.CancellationToken.None).pairs);
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(target); }
        }

        [TestCase(12f, 0f)]
        [TestCase(100f, 45f)]
        public void CollapseDiagnosticAcceptsIsometricThinChartsAtAnyRotation(float length, float uvAngle)
        {
            var uv = new[] { Vector2.zero, new Vector2(length, 0), new Vector2(length, 1), Vector2.up };
            var vertices = new Vector3[uv.Length];
            var packed = new Vector2[uv.Length];
            for (int i = 0; i < uv.Length; i++) {
                var point = new Vector3(uv[i].x, uv[i].y, 0);
                vertices[i] = Quaternion.Euler(20, 30, 45) * point;
                packed[i] = (Vector2)(Quaternion.Euler(0, 0, uvAngle) * point) * .005f + Vector2.one * .1f;
            }
            var original = (Vector2[])packed.Clone();
            var triangles = new[] { 0, 1, 2, 0, 2, 3 };
            Assert.AreEqual(0, GroupedShellTransfer.DiagnoseCollapsedTargetShells(
                UvShellExtractor.Extract(uv, triangles), triangles, vertices, packed));
            CollectionAssert.AreEqual(original, packed);
        }

        [TestCase(0f)]
        [TestCase(.02f)]
        public void CollapseDiagnosticStillReportsCollapsedAndStretchedCharts(float height)
        {
            var uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            var vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up };
            var packed = new[] { Vector2.zero, Vector2.right, new Vector2(1, height), new Vector2(0, height) };
            var triangles = new[] { 0, 1, 2, 0, 2, 3 };
            Assert.AreEqual(1, GroupedShellTransfer.DiagnoseCollapsedTargetShells(
                UvShellExtractor.Extract(uv, triangles), triangles, vertices, packed));
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
        public void SmoothedLodNormalsDoNotFoldACleanBentChart()
        {
            var source = new Mesh { name = "Bent clean chart" }; Mesh target = null;
            try {
                source.vertices = new[] { new Vector3(-1, 0, 0), Vector3.forward, Vector3.right,
                    new Vector3(-1, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 0) };
                var uv = new[] { new Vector2(-1, 0), Vector2.zero, Vector2.right,
                    new Vector2(-1, 1), Vector2.up, Vector2.one };
                source.uv = uv;
                var packed = new Vector2[uv.Length];
                for (int i = 0; i < uv.Length; ++i) packed[i] = uv[i] * .25f + Vector2.one * .4f;
                source.uv2 = packed;
                source.triangles = new[] { 0, 1, 4, 0, 4, 3, 1, 2, 5, 1, 5, 4 }; source.RecalculateNormals();
                target = Object.Instantiate(source);
                var positions = target.vertices;
                for (int i = 0; i < positions.Length; ++i) positions[i].z += .001f;
                target.vertices = positions;
                uv[1].x = .001f; uv[4].x = -.001f; target.uv = uv;
                target.normals = new[] { Vector3.left, Vector3.right, Vector3.left, Vector3.right, Vector3.left, Vector3.right };
                var trace = new TransferMatchTrace();
                var result = GroupedShellTransfer.TransferWithDiagnostics(target, source, null, null, 512, 512, trace);
                var quality = TransferUvQuality.Measure(target, result.uv2, Vector2.one, Matrix4x4.identity);
                Assert.AreEqual(0, quality.degenerateFaces); Assert.AreEqual(0, quality.overlapPairs);
                Assert.Less(quality.worstAnisotropy, 1.5);
                Assert.Greater(trace.shells[0].normalFallbackVertices, 0);
                Assert.IsNotNull(trace.shells[0].beforeTopologyQuality); Assert.IsNotNull(trace.shells[0].finalQuality);
            }
            finally { if (target) Object.DestroyImmediate(target); Object.DestroyImmediate(source); }
        }

        [Test]
        public void CandidateComparisonDoesNotTreatARoundoffSliverAsARepairedLine()
        {
            var triangles = new[] { 0, 1, 2 };
            var positions = new[] { Vector3.zero, Vector3.right, Vector3.up };
            var shell = UvShellExtractor.Extract(new[] { Vector2.zero, Vector2.right, Vector2.up }, triangles)[0];
            var uv = new Dictionary<int, Vector2> { [0] = Vector2.zero, [1] = Vector2.right, [2] = new Vector2(.5f, 0) };
            var line = TransferCandidateQuality.Measure(shell, triangles, positions, uv);
            uv[2] = new Vector2(.5f, 1e-8f);
            var sliver = TransferCandidateQuality.Measure(shell, triangles, positions, uv);
            Assert.AreEqual(1, line.issues); Assert.AreEqual(1, sliver.issues);
            Assert.IsFalse(sliver.Improves(line));
        }

        [Test]
        public void InheritedSourceStretchIsReportedInTheFinalShellStatus()
        {
            var source = new Mesh { name = "Stretched source atlas" }; Mesh target = null;
            try {
                source.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up };
                source.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                source.uv2 = new[] { new Vector2(.1f, .1f), new Vector2(.9f, .1f),
                    new Vector2(.9f, .2f), new Vector2(.1f, .2f) };
                source.triangles = new[] { 0, 1, 2, 0, 2, 3 }; source.RecalculateNormals();
                target = Object.Instantiate(source);
                var result = GroupedShellTransfer.Transfer(target, source);
                CollectionAssert.AreEqual(source.uv2, result.uv2);
                Assert.AreEqual(GroupedShellTransfer.ShellStatus.Poor, result.targetShellStatus[0],
                    "An inherited non-degenerate but stretched atlas must not be labelled Accepted.");
            }
            finally { if (target) Object.DestroyImmediate(target); Object.DestroyImmediate(source); }
        }

        [Test]
        public void CandidateComparisonDoesNotTradeFoldsForLowerStretch()
        {
            var positions = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up };
            var triangles = new[] { 0, 1, 2, 0, 2, 3 };
            var shell = UvShellExtractor.Extract(new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }, triangles)[0];
            var clean = new Dictionary<int, Vector2> { [0] = Vector2.zero, [1] = Vector2.right,
                [2] = Vector2.one, [3] = Vector2.up };
            var folded = new Dictionary<int, Vector2>(clean) { [3] = Vector2.right };
            var cleanQuality = TransferCandidateQuality.Measure(shell, triangles, positions, clean);
            var foldedQuality = TransferCandidateQuality.Measure(shell, triangles, positions, folded);
            Assert.Greater(foldedQuality.overlapPairs, 0);
            Assert.IsFalse(foldedQuality.Improves(cleanQuality));
            Assert.IsTrue(cleanQuality.Improves(foldedQuality));
        }

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

        [TestCase(.001f, false)]
        [TestCase(1f, false)]
        [TestCase(1000f, false)]
        [TestCase(.001f, true)]
        [TestCase(1f, true)]
        [TestCase(1000f, true)]
        public void FilledChartDoesNotMatchCoplanarFrameSharingItsBoundary(float scale, bool wrongPreviousLodHint)
        {
            var source = new Mesh { name = "Frame and filled chart with a shared geometric boundary" };
            var target = new Mesh { name = "Filled target chart" };
            try {
                var outline = new[] { new Vector2(-1, -1), new Vector2(2, -1), new Vector2(2, 2), new Vector2(-1, 2),
                    Vector2.zero, Vector2.right, Vector2.one, Vector2.up,
                    Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                var positions = new Vector3[outline.Length]; var atlas = new Vector2[outline.Length];
                for (int i = 0; i < outline.Length; ++i) {
                    positions[i] = new Vector3(outline[i].x, outline[i].y, 0) * scale;
                    atlas[i] = i < 8 ? (outline[i] + Vector2.one) * .1f + Vector2.one * .05f
                        : outline[i] * .25f + Vector2.one * .65f;
                }
                var indices = new List<int>();
                for (int i = 0; i < 4; ++i) {
                    int next = (i + 1) % 4;
                    indices.AddRange(new[] { i, next, next + 4, i, next + 4, i + 4 });
                }
                indices.AddRange(new[] { 8, 9, 10, 8, 10, 11 });
                source.vertices = positions; source.uv = outline; source.uv2 = atlas;
                source.SetTriangles(indices, 0); source.RecalculateNormals();
                target.vertices = new[] { positions[8], positions[9], positions[10], positions[11] };
                target.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                target.triangles = new[] { 0, 1, 2, 0, 2, 3 }; target.RecalculateNormals();
                var trace = new TransferMatchTrace();
                var hints = wrongPreviousLodHint ? new List<GroupedShellTransfer.CrossLodMatchHint> {
                    new GroupedShellTransfer.CrossLodMatchHint {
                        sourceShellIndex = 0, centroid3D = new Vector3(.5f, .5f, 0) * scale,
                        uv0Centroid = Vector2.one * .5f, uv0BoundsMin = Vector2.zero, uv0BoundsMax = Vector2.one,
                        quality = GroupedShellTransfer.ShellStatus.Accepted
                    }
                } : null;
                var result = GroupedShellTransfer.TransferWithDiagnostics(target, source, null, hints, 512, 512, trace);
                Assert.AreEqual(1, result.targetShellToSourceShell[0],
                    "Coincident boundary vertices do not mean the frame covers the target face interiors.");
                var frame = trace.shells[0].candidates.Find(candidate => candidate.sourceShell == 0 && candidate.phase == "match");
                var filled = trace.shells[0].candidates.Find(candidate => candidate.sourceShell == 1 && candidate.phase == "match");
                Assert.That(frame.faceInteriorDistanceSquared, Is.GreaterThan(scale * scale * .01f));
                Assert.That(filled.faceInteriorDistanceSquared, Is.LessThan(scale * scale * 1e-8f));
                if (wrongPreviousLodHint) {
                    Assert.IsFalse(trace.shells[0].hintMatched);
                    StringAssert.Contains("hint rejected", trace.shells[0].initialReason);
                }
                for (int i = 0; i < 4; ++i)
                    Assert.That(Vector2.Distance(atlas[8 + i], result.uv2[i]), Is.LessThan(1e-5f));
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(target); }
        }

        [Test]
        public void CrossLodHintRetainsUv0FeatureWhenDecimationMovesSurface()
        {
            var source = new Mesh { name = "Parallel surfaces with different UV0 features" };
            var target = new Mesh { name = "Detail decimated onto nearby surface" };
            try {
                var corners = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                var vertices = new Vector3[8]; var uv0 = new Vector2[8]; var atlas = new Vector2[8];
                for (int i = 0; i < 8; ++i) {
                    vertices[i] = new Vector3(corners[i % 4].x, corners[i % 4].y, i < 4 ? 0 : .01f);
                    uv0[i] = corners[i % 4] + (i < 4 ? Vector2.zero : Vector2.right * 2);
                    atlas[i] = corners[i % 4] * .25f + Vector2.one * (i < 4 ? .05f : .65f);
                }
                source.vertices = vertices; source.uv = uv0; source.uv2 = atlas;
                source.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }; source.RecalculateNormals();
                target.vertices = new[] { vertices[4], vertices[5], vertices[6], vertices[7] };
                target.uv = corners; target.triangles = new[] { 0, 1, 2, 0, 2, 3 }; target.RecalculateNormals();
                var hints = new List<GroupedShellTransfer.CrossLodMatchHint> {
                    new GroupedShellTransfer.CrossLodMatchHint {
                        sourceShellIndex = 0, centroid3D = new Vector3(.5f, .5f, 0),
                        uv0Centroid = Vector2.one * .5f, uv0BoundsMin = Vector2.zero, uv0BoundsMax = Vector2.one,
                        quality = GroupedShellTransfer.ShellStatus.Accepted
                    }
                };
                var trace = new TransferMatchTrace();
                var result = GroupedShellTransfer.TransferWithDiagnostics(target, source, null, hints, 512, 512, trace);
                Assert.AreEqual(0, result.targetShellToSourceShell[0]);
                Assert.IsTrue(trace.shells[0].hintMatched, "Geometric proximity cannot discard a surviving UV0 feature.");
                for (int i = 0; i < 4; ++i)
                    Assert.That(Vector2.Distance(atlas[i], result.uv2[i]), Is.LessThan(1e-5f));
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(target); }
        }

        [TestCase(8, false)]
        [TestCase(40, false)]
        [TestCase(8, true)]
        [TestCase(40, true)]
        public void CurvedChartMatchDoesNotFlipWhenSmallFacesAreSubdivided(int subdivisions, bool subdivideSource)
        {
            var source = FoldedStrip(subdivideSource ? 1 : 4, subdivideSource ? subdivisions : 2,
                remoteWall: true, remoteForward: subdivideSource);
            var target = FoldedStrip(subdivideSource ? 4 : 1, subdivideSource ? 2 : subdivisions,
                remoteWall: false, remoteForward: false);
            try {
                var expected = target.uv;
                for (int i = 0; i < expected.Length; ++i) expected[i] = expected[i] * .25f + Vector2.one * .05f;
                var result = GroupedShellTransfer.Transfer(target, source, sourceAtlasWidth: 512, sourceAtlasHeight: 512);
                Assert.AreEqual(0, result.targetShellToSourceShell[0],
                    "Changing tessellation must not make the chart match a remote wall.");
                for (int i = 0; i < expected.Length; ++i)
                    Assert.That(Vector2.Distance(expected[i], result.uv2[i]), Is.LessThan(1e-5f));
                var quality = TransferUvQuality.Measure(target, result.uv2, Vector2.one, Matrix4x4.identity);
                Assert.AreEqual(0, quality.degenerateFaces); Assert.AreEqual(0, quality.overlapPairs);
                Assert.IsTrue(quality.overlapScanComplete);
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(target); }
        }

        static Mesh FoldedStrip(int frontSegments, int backSegments, bool remoteWall, bool remoteForward)
        {
            var positions = new List<Vector3>(); var uv = new List<Vector2>(); var indices = new List<int>();
            void Column(float x, float z, float distance) {
                positions.Add(new Vector3(x, 0, z)); positions.Add(new Vector3(x, 1, z));
                uv.Add(new Vector2(distance / 2.7f, 0)); uv.Add(new Vector2(distance / 2.7f, 1));
            }
            for (int i = 0; i <= frontSegments; ++i) Column(2f * i / frontSegments, 0, 2f * i / frontSegments);
            Column(2, .2f, 2.2f);
            for (int i = 1; i <= backSegments; ++i) Column(2f - .5f * i / backSegments, .2f, 2.2f + .5f * i / backSegments);
            for (int i = 0; i < positions.Count - 2; i += 2)
                indices.AddRange(new[] { i, i + 2, i + 3, i, i + 3, i + 1 });
            var uv2 = new List<Vector2>();
            foreach (var value in uv) uv2.Add(value * .25f + Vector2.one * .05f);
            if (remoteWall) {
                int first = positions.Count;
                positions.AddRange(new[] { new Vector3(0, 0, 2), new Vector3(2, 0, 2), new Vector3(2, 1, 2), new Vector3(0, 1, 2) });
                uv.AddRange(new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up });
                uv2.AddRange(new[] { new Vector2(.65f, .65f), new Vector2(.9f, .65f), new Vector2(.9f, .9f), new Vector2(.65f, .9f) });
                indices.AddRange(remoteForward ? new[] { first, first + 1, first + 2, first, first + 2, first + 3 }
                    : new[] { first, first + 2, first + 1, first, first + 3, first + 2 });
            }
            var mesh = new Mesh { name = "Retessellated folded strip" };
            mesh.SetVertices(positions); mesh.SetUVs(0, uv); mesh.SetUVs(1, uv2); mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals(); return mesh;
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
