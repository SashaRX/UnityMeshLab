using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshNormalsTests
    {
        static Vector3[] FoldPositions() => new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward };
        static int[] FoldIndices() => new[] { 0, 1, 2, 1, 0, 3 };

        static void AssertChannels(Vector3[] positions, Vector3[] normals, Vector2[] uv)
        {
            Assert.AreEqual(positions.Length, normals.Length);
            Assert.AreEqual(positions.Length, uv.Length);
            foreach (var n in normals) {
                Assert.IsTrue(MeshGeometry.UsableNormal(n));
                Assert.That(n.magnitude, Is.EqualTo(1).Within(1e-5f));
            }
            foreach (var v in uv) {
                Assert.That(v.x, Is.InRange(0f, 1f)); Assert.That(v.y, Is.InRange(0f, 1f));
            }
        }

        [TestCase(1f)]
        [TestCase(.001f)]
        [TestCase(.0001f)]
        public void AveragedNormalsCrossPositionDuplicatesAtEveryScale(float scale)
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.right, Vector3.zero, Vector3.forward };
            for (int i = 0; i < p.Length; ++i) p[i] *= scale;
            var n = MeshGeometry.AveragedNormals(p, new[] { 0, 1, 2, 3, 4, 5 });
            Assert.That(Vector3.Distance(n[0], (Vector3.up + Vector3.forward).normalized), Is.LessThan(1e-5f));
            Assert.AreEqual(n[0], n[4]); Assert.AreEqual(n[1], n[3]);
            foreach (var normal in n) Assert.That(normal.magnitude, Is.EqualTo(1).Within(1e-5f));
        }

        [Test]
        public void CancellingAndDegenerateFansStillHaveUsableNormals()
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one * 5 };
            var n = MeshGeometry.AveragedNormals(p, new[] { 0, 1, 2, 0, 2, 1, 3, 3, 3 });
            foreach (var normal in n) Assert.That(normal.magnitude, Is.EqualTo(1).Within(1e-5f));
            Assert.AreEqual(Vector3.forward, n[0]);
            Assert.AreEqual(Vector3.up, n[3]);
        }

        [Test]
        public void TemporaryUvNormalizesLocalXYAndCentersFlatAxis()
        {
            var p = new[] { new Vector3(-4, 10, 90), new Vector3(6, 20, -90), new Vector3(1, 15, 0) };
            CollectionAssert.AreEqual(new[] { Vector2.zero, Vector2.one, Vector2.one * .5f }, MeshGeometry.NormalizedXYUv(p));
            p[1].y = p[2].y = p[0].y;
            foreach (var uv in MeshGeometry.NormalizedXYUv(p)) Assert.AreEqual(.5f, uv.y);
            Assert.IsEmpty(MeshGeometry.NormalizedXYUv(Array.Empty<Vector3>()));
        }

        [Test]
        public void PrepareChannelsRefreshesNormalsAndPreservesExistingUv()
        {
            var uv = new[] { new Vector2(-1, 2), Vector2.zero, Vector2.one, new Vector2(3, -4) };
            var data = new RemeshNative.IndexedMesh { positions = FoldPositions(), indices = FoldIndices(), uv = uv,
                normals = new[] { Vector3.left, Vector3.left, Vector3.left, Vector3.left } }.PrepareChannels();
            Assert.AreSame(uv, data.uv);
            Assert.IsFalse(data.draftUv, "Existing UVs are not marked draft merely by preparing normals");
            Assert.That(Vector3.Dot(data.normals[0], Vector3.left), Is.EqualTo(0).Within(1e-5f));
            data.uv = null; data.PrepareChannels();
            Assert.IsTrue(data.draftUv);
            AssertChannels(data.positions, data.normals, data.uv);
        }

        [Test]
        public void OwnedMeshRepairsOnlyMissingChannelsAndBadNormalEntries()
        {
            var mesh = new Mesh { vertices = FoldPositions(), triangles = FoldIndices() };
            try {
                MeshGeometry.EnsureMeshChannels(mesh);
                AssertChannels(mesh.vertices, mesh.normals, mesh.uv);
                var authoredUv = new List<Vector4> { new Vector4(-1, 2, 3, 4), Vector4.one, Vector4.zero, Vector4.one * 5 };
                mesh.SetUVs(0, authoredUv);
                mesh.normals = new[] { Vector3.left, Vector3.zero, new Vector3(float.NaN, 0, 0), Vector3.forward };
                MeshGeometry.EnsureMeshChannels(mesh);
                var preserved = new List<Vector4>(); mesh.GetUVs(0, preserved);
                CollectionAssert.AreEqual(authoredUv, preserved);
                Assert.AreEqual(4, mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0));
                Assert.AreEqual(Vector3.left, mesh.normals[0]); Assert.AreEqual(Vector3.forward, mesh.normals[3]);
                Assert.IsTrue(MeshGeometry.UsableNormal(mesh.normals[1])); Assert.IsTrue(MeshGeometry.UsableNormal(mesh.normals[2]));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void IntermediateAndResultMeshBuildersCompleteChannels()
        {
            Mesh intermediate = null, result = null;
            try {
                intermediate = RemeshPipeline.BuildMesh("intermediate", FoldPositions(), FoldIndices());
                AssertChannels(intermediate.vertices, intermediate.normals, intermediate.uv);
                Assert.IsTrue(MeshUvState.IsDraft(intermediate));
                Assert.IsTrue(UvTopology.HasUv(intermediate, 0)); Assert.IsFalse(UvTopology.HasFinalUv(intermediate, 0));
                result = RemeshPipeline.BuildResultMesh("result", new RemeshNative.Geometry {
                    positions = FoldPositions(), indices = FoldIndices(), tangents = new[] {
                        new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) } }, out _);
                AssertChannels(result.vertices, result.normals, result.uv);
                Assert.IsTrue(MeshUvState.IsDraft(result), "Missing result UVs must not masquerade as a completed atlas");
            }
            finally { if (intermediate) Object.DestroyImmediate(intermediate); if (result) Object.DestroyImmediate(result); }
        }

        static RemeshNative.Geometry SeamFold()
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.right, Vector3.zero, Vector3.forward };
            var indices = new[] { 0, 1, 2, 3, 4, 5 };
            return new RemeshNative.Geometry { positions = p, indices = indices,
                normals = MeshGeometry.AveragedNormals(p, indices), charts = new[] { 0, 0, 0, 1, 1, 1 }, chartCount = 2,
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up, new Vector2(.9f, 0), new Vector2(.6f, 0), new Vector2(.6f, .3f) },
                tangents = new[] { new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1),
                    new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1) } };
        }

        [TestCase(RemeshHardEdges.Smooth, 120, false)]
        [TestCase(RemeshHardEdges.Angle, 120, false)]
        [TestCase(RemeshHardEdges.Angle, 60, true)]
        [TestCase(RemeshHardEdges.UvIslands, 120, true)]
        [TestCase(RemeshHardEdges.UvIslandsAndAngle, 120, true)]
        public void FinalModesUseRealChartBordersWithoutMovingUvCorners(RemeshHardEdges mode, int crease, bool hard)
        {
            var geometry = SeamFold(); var positions = (Vector3[])geometry.positions.Clone(); var uv = (Vector2[])geometry.uv.Clone();
            RemeshNormals.ApplyFinal(geometry, new RemeshSettings { hardEdges = mode, normalCrease = crease, normalSmoothing = 2 }, CancellationToken.None);
            Assert.AreEqual(hard, Vector3.Dot(geometry.normals[geometry.indices[0]], geometry.normals[geometry.indices[4]]) < .5f);
            for (int c = 0; c < geometry.indices.Length; ++c) {
                int v = geometry.indices[c]; Assert.AreEqual(positions[c], geometry.positions[v]); Assert.AreEqual(uv[c], geometry.uv[v]);
                Assert.AreEqual(c < 3 ? 0 : 1, geometry.charts[v]);
                var tangent = geometry.tangents[v];
                Assert.That(Vector3.Dot(geometry.normals[v], new Vector3(tangent.x, tangent.y, tangent.z)), Is.EqualTo(0).Within(1e-5f));
                Assert.AreEqual(-1, tangent.w);
            }
            AssertChannels(geometry.positions, geometry.normals, geometry.uv);
        }

        [Test]
        public void FinalCreaseSplitsSharedVertexInsideSingleChart()
        {
            var p = FoldPositions(); var indices = FoldIndices();
            var geometry = new RemeshNative.Geometry { positions = p, indices = indices, normals = MeshGeometry.AveragedNormals(p, indices),
                uv = MeshGeometry.NormalizedXYUv(p), charts = new int[p.Length], chartCount = 1, tangents = new Vector4[p.Length] };
            RemeshNormals.ApplyFinal(geometry, new RemeshSettings { hardEdges = RemeshHardEdges.Angle, normalCrease = 60, normalSmoothing = 2 }, CancellationToken.None);
            Assert.AreEqual(6, geometry.positions.Length); Assert.AreEqual(1, geometry.chartCount);
            for (int c = 0; c < indices.Length; ++c) {
                int v = geometry.indices[c]; Assert.AreEqual(p[indices[c]], geometry.positions[v]);
                Assert.AreEqual(c < 3 ? Vector3.forward : Vector3.up, geometry.normals[v]);
            }
        }

        static void RequireNative()
        {
            try { RemeshNative.CheckAvailable(); }
            catch (InvalidOperationException error) when (error.InnerException is DllNotFoundException ||
                error.InnerException is EntryPointNotFoundException || error.InnerException is BadImageFormatException) { Assert.Ignore(error.Message); }
        }

        [TestCase(RemeshHardEdges.Smooth)]
        [TestCase(RemeshHardEdges.Angle)]
        [TestCase(RemeshHardEdges.UvIslands)]
        [TestCase(RemeshHardEdges.UvIslandsAndAngle)]
        public void NativeAtlasIgnoresFinalNormalsAndTemporaryUv(RemeshHardEdges mode)
        {
            RequireNative();
            var input = new RemeshNative.IndexedMesh { positions = FoldPositions(), indices = FoldIndices() }.PrepareChannels();
            var settings = new RemeshSettings { hardEdges = RemeshHardEdges.Smooth, normalSmoothing = 0, textureResolution = 256 };
            var smooth = RemeshNative.Unwrap(input, settings, CancellationToken.None);
            input.normals = new Vector3[input.positions.Length]; input.uv = new Vector2[input.positions.Length];
            settings.hardEdges = mode; settings.normalCrease = 30; settings.normalWeighting = RemeshNormalWeighting.CornerAngle; settings.normalSmoothing = 3;
            var result = RemeshNative.Unwrap(input, settings, CancellationToken.None);
            Assert.IsTrue(input.draftUv); Assert.IsFalse(result.draftUv);
            var finalMesh = RemeshPipeline.BuildResultMesh("final", result, out _);
            try { Assert.IsFalse(MeshUvState.IsDraft(finalMesh)); Assert.IsTrue(UvTopology.HasFinalUv(finalMesh, 0)); }
            finally { Object.DestroyImmediate(finalMesh); }
            Assert.AreEqual(smooth.chartCount, result.chartCount); Assert.AreEqual(smooth.indices.Length, result.indices.Length);
            for (int c = 0; c < result.indices.Length; ++c) {
                int a = smooth.indices[c], b = result.indices[c]; Assert.AreEqual(smooth.positions[a], result.positions[b]);
                Assert.AreEqual(smooth.uv[a], result.uv[b]); Assert.AreEqual(smooth.charts[a], result.charts[b]);
            }
            AssertChannels(result.positions, result.normals, result.uv);
        }

        [Test]
        public void VoxelSimplifyTrimAndBoxesAllProvideCompleteIntermediateChannels()
        {
            RequireNative();
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try {
                var mesh = cube.GetComponent<MeshFilter>().sharedMesh;
                var settings = new RemeshSettings { voxelResolution = 16, targetTriangles = 80, maximumError = .1f };
                var voxel = RemeshNative.Voxelize(mesh.vertices, mesh.triangles, settings, CancellationToken.None);
                AssertChannels(voxel.positions, voxel.normals, voxel.uv);
                Assert.IsTrue(voxel.draftUv);
                var simplified = RemeshNative.Simplify(voxel, settings, CancellationToken.None, out _);
                AssertChannels(simplified.positions, simplified.normals, simplified.uv);
                Assert.IsTrue(simplified.draftUv);
                Assert.Less(simplified.TriangleCount, voxel.TriangleCount);
                CollectionAssert.AreEqual(MeshGeometry.AveragedNormals(simplified.positions, simplified.indices), simplified.normals);
                var trimmed = RemeshTrim.Trim(voxel, mesh.vertices, mesh.triangles, .15f, CancellationToken.None);
                AssertChannels(trimmed.mesh.positions, trimmed.mesh.normals, trimmed.mesh.uv);
                Assert.IsTrue(trimmed.mesh.draftUv);
                var boxes = new RemeshSource { positions = mesh.vertices, indices = mesh.triangles,
                    vertexRenderer = new int[mesh.vertexCount], rendererToSpace = new[] { Matrix4x4.identity } }.OrientedBoxes();
                AssertChannels(boxes.positions, boxes.normals, boxes.uv);
                Assert.IsTrue(boxes.draftUv);
            }
            finally { Object.DestroyImmediate(cube); }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void GeneralSimplifierCompletesMissingChannelsAndPreservesAuthoredUv(bool authored, bool draft)
        {
            RequireNative();
            var mesh = new Mesh { vertices = FoldPositions(), triangles = FoldIndices() }; Mesh output = null;
            try {
                var originalUv = new List<Vector4> { new Vector4(-1, 2, 3, 4), Vector4.one, Vector4.zero, Vector4.one * 5 };
                if (authored) { mesh.SetUVs(0, originalUv); mesh.normals = new[] { Vector3.left, Vector3.left, Vector3.left, Vector3.left }; }
                MeshUvState.SetDraft(mesh, draft);
                var options = MeshSimplifier.SimplifySettings.Default; options.targetRatio = 1;
                var result = MeshSimplifier.Simplify(mesh, options); Assert.IsTrue(result.ok, result.error); output = result.simplifiedMesh;
                Assert.AreEqual(!authored || draft, result.draftUv);
                Assert.AreEqual(result.draftUv, MeshUvState.IsDraft(output));
                Assert.AreEqual(result.draftUv, new MeshEntry { originalMesh = output }.draftUv);
                Assert.AreEqual(output.vertexCount, output.normals.Length); Assert.AreEqual(output.vertexCount, output.uv.Length);
                if (authored) {
                    var uv = new List<Vector4>(); output.GetUVs(0, uv);
                    for (int i = 0; i < output.vertexCount; ++i) {
                        int source = Array.IndexOf(mesh.vertices, output.vertices[i]);
                        Assert.AreEqual(originalUv[source], uv[i]); Assert.AreEqual(Vector3.left, output.normals[i]);
                    }
                    Assert.AreEqual(4, output.GetVertexAttributeDimension(VertexAttribute.TexCoord0));
                }
                else { AssertChannels(output.vertices, output.normals, output.uv); Assert.IsEmpty(mesh.normals); Assert.IsEmpty(mesh.uv); }
            }
            finally { if (output) Object.DestroyImmediate(output); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void DraftUvBlocksFinalNormalsAndBakeUntilRealUnwrap()
        {
            var geometry = SeamFold(); geometry.draftUv = true;
            var normals = geometry.normals; var indices = geometry.indices;
            Assert.Throws<InvalidOperationException>(() => RemeshNormals.ApplyFinal(geometry, new RemeshSettings(), CancellationToken.None));
            Assert.AreSame(normals, geometry.normals); Assert.AreSame(indices, geometry.indices);
            Assert.Throws<InvalidOperationException>(() => RemeshBaker.Bake(null, geometry, geometry.tangents, new RemeshSettings(), CancellationToken.None));
        }

        [Test]
        public void PreviewCopyReadsDraftStatusFromItsActualMeshAndCanClearIt()
        {
            var draft = RemeshPipeline.BuildMesh("draft", FoldPositions(), FoldIndices());
            var final = RemeshPipeline.BuildMesh("final", FoldPositions(), FoldIndices(), uv: new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one });
            try {
                var entry = new MeshEntry { originalMesh = final };
                Assert.IsFalse(entry.draftUv); Assert.IsTrue(entry.PreviewCopy(draft).draftUv);
                MeshUvState.SetDraft(draft, false); Assert.IsFalse(entry.PreviewCopy(draft).draftUv);
                Assert.IsTrue(UvTopology.HasFinalUv(draft, 0));
            }
            finally { Object.DestroyImmediate(draft); Object.DestroyImmediate(final); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReadableCopiesKeepDraftStatusAndTextureAoRequiresFinalUv(bool draft)
        {
            var mesh = RemeshPipeline.BuildMesh("UV provenance", FoldPositions(), FoldIndices()); Mesh copy = null;
            try {
                MeshUvState.SetDraft(mesh, draft);
                copy = MeshAccess.ReadableCopy(mesh);
                Assert.AreEqual(draft, MeshUvState.IsDraft(copy));
                CollectionAssert.AreEqual(mesh.uv, copy.uv);
                if (draft) Assert.Throws<InvalidOperationException>(() => TextureAoBakePanel.CaptureTarget(copy, 0));
                else Assert.IsFalse(TextureAoBakePanel.CaptureTarget(copy, 0).draftUv);
                // The marker describes UV0 only; a separately supplied UV2 remains usable.
                copy.uv2 = mesh.uv;
                Assert.IsTrue(UvTopology.HasFinalUv(copy, 1));
                Assert.IsFalse(TextureAoBakePanel.CaptureTarget(copy, 1).draftUv);
            }
            finally { if (copy) Object.DestroyImmediate(copy); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void CancelledPreparationAndFinalNormalsDoNotProceed()
        {
            using (var cancel = new CancellationTokenSource()) {
                cancel.Cancel();
                Assert.Throws<OperationCanceledException>(() => new RemeshNative.IndexedMesh {
                    positions = FoldPositions(), indices = FoldIndices() }.PrepareChannels(cancel.Token));
                var geometry = SeamFold(); var original = geometry.normals;
                Assert.Throws<OperationCanceledException>(() => RemeshNormals.ApplyFinal(geometry, new RemeshSettings(), cancel.Token));
                Assert.AreSame(original, geometry.normals);
            }
        }
    }
}
