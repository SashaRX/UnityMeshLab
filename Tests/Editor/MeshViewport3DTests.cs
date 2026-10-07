using NUnit.Framework;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public class MeshViewport3DTests
    {
        [TestCase(0f, 900f, 700f, true)]
        [TestCase(320f, 900f, 700f, true)]
        [TestCase(500f, 900f, 700f, true)]
        [TestCase(800f, 900f, 700f, true)]
        [TestCase(1900f, 900f, 700f, true)]
        [TestCase(800f, 300f, 360f, true)]
        [TestCase(500f, 900f, 700f, false)]
        [TestCase(800f, 900f, 700f, false)]
        public void SidebarColumnsRemainInsideTheWindow(float windowWidth, float left, float right, bool hasRight)
        {
            var widths = UvToolHub.ResolveColumnWidths(windowWidth, left, right, hasRight);
            float handles = UvToolHub.SplitterWidth(windowWidth, hasRight) * (hasRight ? 2 : 1);
            Assert.GreaterOrEqual(widths.x, 0); Assert.GreaterOrEqual(widths.y, 0); Assert.GreaterOrEqual(widths.z, 0);
            Assert.That(widths.x + widths.y + widths.z + handles, Is.EqualTo(windowWidth).Within(.001f));
            if (!hasRight) Assert.AreEqual(0, widths.z);
            if (windowWidth >= 800) {
                Assert.GreaterOrEqual(widths.x, 220); Assert.GreaterOrEqual(widths.y, 120);
                if (hasRight) Assert.GreaterOrEqual(widths.z, 220);
            }
        }

        [Test]
        public void RightSplitterCanGrowWhenTheLeftPanelUsedAllAvailableRoom()
        {
            var initial = UvToolHub.ResolveColumnWidths(800, 900, 700, true);
            var resized = UvToolHub.ResizeColumns(800, initial, initial.z + 100, true, true);
            Assert.That(resized.z, Is.EqualTo(initial.z + 100).Within(.001f));
            Assert.Less(resized.x, initial.x); Assert.GreaterOrEqual(resized.y, 120);
        }

        [Test]
        public void PipelineWorkMapsToItsPreviewButton()
        {
            Assert.IsNull(RemeshPreview.PreviewStage(null));
            Assert.AreEqual(RemeshPreview.Stage.Remesh, RemeshPreview.PreviewStage(RemeshPipeline.Stage.Remesh));
            Assert.AreEqual(RemeshPreview.Stage.Simplified, RemeshPreview.PreviewStage(RemeshPipeline.Stage.Simplify));
            Assert.AreEqual(RemeshPreview.Stage.Result, RemeshPreview.PreviewStage(RemeshPipeline.Stage.Unwrap));
            Assert.AreEqual(RemeshPreview.Stage.Result, RemeshPreview.PreviewStage(RemeshPipeline.Stage.Bake));
        }

        [UnityTest]
        public IEnumerator PreviewPreparationRunsOnWorkerAndCoalescesSupersededRequests()
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId, workerThread = 0, snapshotThread = 0;
            int heartbeats = 0; bool supersededPrepared = false;
            var applied = new List<int>();
            EditorApplication.CallbackFunction heartbeat = () => ++heartbeats;
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var work = new PreviewWork<int>("test preview")) {
                EditorApplication.update += heartbeat;
                try {
                    work.Enqueue(() => {
                        snapshotThread = Thread.CurrentThread.ManagedThreadId;
                        return token => {
                            workerThread = Thread.CurrentThread.ManagedThreadId;
                            started.Set(); release.Wait(token);
                            return 1;
                        };
                    }, applied.Add);
                    Assert.IsFalse(started.IsSet, "Enqueue must leave preparation out of the GUI callback");
                    double deadline = EditorApplication.timeSinceStartup + 10;
                    while (!started.IsSet && EditorApplication.timeSinceStartup < deadline) yield return null;
                    Assert.IsTrue(started.IsSet);
                    Assert.AreEqual(mainThread, snapshotThread);
                    Assert.AreNotEqual(mainThread, workerThread);
                    int before = heartbeats;
                    for (int i = 0; i < 3; ++i) yield return null;
                    Assert.Greater(heartbeats, before, "Editor updates continue while preview preparation waits");
                    work.Enqueue(() => { supersededPrepared = true; return token => 2; }, applied.Add);
                    work.Enqueue(() => token => 3, applied.Add);
                    release.Set();
                    while (work.IsPending && EditorApplication.timeSinceStartup < deadline) yield return null;
                    Assert.IsFalse(work.IsPending);
                    Assert.IsFalse(supersededPrepared);
                    CollectionAssert.AreEqual(new[] { 3 }, applied, "A superseded worker never publishes stale preview data");
                }
                finally { release.Set(); EditorApplication.update -= heartbeat; }
            }
        }

        [UnityTest]
        public IEnumerator ClosingPreviewCancelsRunningWorkerWithoutPublishing()
        {
            using (var started = new ManualResetEventSlim())
            using (var finished = new ManualResetEventSlim())
            using (var work = new PreviewWork<int>("test preview")) {
                bool applied = false;
                work.Enqueue(() => token => {
                    started.Set();
                    try { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return 1; }
                    finally { finished.Set(); }
                }, value => applied = true);
                double deadline = EditorApplication.timeSinceStartup + 10;
                while (!started.IsSet && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(started.IsSet);
                work.Dispose();
                while (!finished.IsSet && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(finished.IsSet, "Closing must signal cancellation to a running worker");
                for (int i = 0; i < 3; ++i) yield return null;
                Assert.IsFalse(applied);
            }
        }

        [UnityTest]
        public IEnumerator WirePreviewIsDeferredAndInvalidationCancelsPendingUpload()
        {
            var mesh = Quad();
            using (var viewport = new MeshViewport3D())
            try {
                int repaints = 0; viewport.RequestRepaint = () => ++repaints;
                Assert.IsNull(viewport.WireOf(mesh), "First paint queues a snapshot instead of building wire edges");
                Mesh wire = null;
                double deadline = EditorApplication.timeSinceStartup + 10;
                while (!wire && EditorApplication.timeSinceStartup < deadline) { yield return null; wire = viewport.WireOf(mesh); }
                Assert.IsTrue(wire);
                Assert.AreEqual(MeshTopology.Lines, wire.GetTopology(0));
                CollectionAssert.AreEqual(MeshViewport3D.EdgeIndices(mesh), wire.GetIndices(0));
                CollectionAssert.AreEqual(mesh.vertices, wire.vertices);
                Assert.AreEqual(1, repaints);
                viewport.InvalidateMesh(mesh);
                Assert.IsFalse(wire, "Uploaded preview meshes are released on invalidation");
                Assert.IsNull(viewport.WireOf(mesh));
                viewport.InvalidateCaches();
                for (int i = 0; i < 5; ++i) yield return null;
                Assert.AreEqual(1, repaints, "A cancelled pending upload cannot repaint a closed or changed view");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [UnityTest]
        public IEnumerator WirePreviewAboveFaceLimitUploadsValidMeshAndPreservesOtherSubmeshes()
        {
            var indices = new int[1_000_001 * 3];
            for (int i = 0; i < indices.Length; i += 3) { indices[i + 1] = 1; indices[i + 2] = 2; }
            for (int otherSubmesh = 0; otherSubmesh < 2; ++otherSubmesh) {
                var mesh = new Mesh { name = "AboveWireFaceLimit" };
                using (var viewport = new MeshViewport3D())
                try {
                    mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                    mesh.subMeshCount = otherSubmesh + 1;
                    mesh.SetIndices(indices, MeshTopology.Triangles, 0);
                    if (otherSubmesh != 0) mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 1);
                    var expected = MeshViewport3D.EdgeIndices(mesh);
                    Assert.IsNotNull(expected, "An oversized submesh must never produce null upload indices");
                    Assert.AreEqual(otherSubmesh * 6, expected.Count);
                    Assert.IsNull(viewport.WireOf(mesh));
                    Mesh wire = null;
                    double deadline = EditorApplication.timeSinceStartup + 10;
                    while (!wire && EditorApplication.timeSinceStartup < deadline) { yield return null; wire = viewport.WireOf(mesh); }
                    Assert.IsTrue(wire, "Even an empty wire preview must finish uploading");
                    Assert.AreEqual(MeshTopology.Lines, wire.GetTopology(0));
                    CollectionAssert.AreEqual(expected, wire.GetIndices(0));
                    Assert.AreSame(wire, viewport.WireOf(mesh), "A completed preview is reused");
                    viewport.InvalidateMesh(mesh);
                    Assert.IsFalse(wire, "The cached preview must be released on invalidation");
                }
                finally { Object.DestroyImmediate(mesh); }
            }
        }

        [Test]
        public void InspectionWorksWithoutUvNormalsOrTangents()
        {
            var mesh = Quad();
            using (var inspection = new MeshInspection())
            try {
                mesh.uv = null; mesh.normals = null; mesh.tangents = null;
                Assert.IsTrue(MeshInspection.Supports(mesh, MeshViewport3D.Shading.Positions));
                Assert.IsFalse(MeshInspection.Supports(mesh, MeshViewport3D.Shading.UV0));
                Assert.IsFalse(MeshInspection.Supports(mesh, MeshViewport3D.Shading.Normals));
                Assert.IsNull(MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.Normals));
                var colors = MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.Positions);
                Assert.AreEqual(new Color32(0, 0, 128, 255), colors[0]);
                Assert.AreEqual(new Color32(255, 255, 128, 255), colors[3]);
                string report = inspection.Report(mesh);
                StringAssert.Contains("2 triangles", report);
                StringAssert.Contains("Position: Float32 ×3", report);
                // Absent channels must not appear in the model summary.
                StringAssert.DoesNotContain("UV0:", report);
                StringAssert.DoesNotContain("Normal:", report);
                StringAssert.DoesNotContain("Tangent:", report);
                StringAssert.DoesNotContain("Skinning", report);
                var items = new[] { new MeshViewport3D.Item(mesh, Matrix4x4.identity) };
                Assert.IsTrue(inspection.Pick(items, new Vector3(.05f, .05f, -2), Vector3.forward, out int item));
                Assert.AreEqual(0, item);
                // World-space depth ordering also works for nonuniformly scaled instances.
                var near = Matrix4x4.TRS(new Vector3(0, 0, -1), Quaternion.identity, new Vector3(2, 3, .25f));
                Assert.IsTrue(inspection.Pick(new[] { items[0], new MeshViewport3D.Item(mesh, near) },
                    new Vector3(.05f, .05f, -2), Vector3.forward, out item));
                Assert.AreEqual(1, item);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void InPlaceCompactionDropsTheCachedModelReport()
        {
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one, Vector3.left };
                mesh.triangles = new[] { 0, 1, 2, 1, 3, 2 };   // vertex 4 stays unreferenced
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one, Vector2.left };
                using (var inspection = new MeshInspection()) {
                    StringAssert.Contains("5 vertices", inspection.Report(mesh));
                    Assert.AreEqual(1, MeshHygieneUtility.CompactVertices(mesh));
                    StringAssert.Contains("4 vertices", inspection.Report(mesh));
                }
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [TestCase("en-US")]
        [TestCase("ru-RU")]
        [TestCase("hu-HU")]
        public void InspectionPreservesFourComponentUv7AndTangentAndColorAlpha(string cultureName)
        {
            var previousCulture = Thread.CurrentThread.CurrentCulture;
            var mesh = Quad();
            using (var inspection = new MeshInspection())
            try {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                mesh.SetUVs(7, new List<Vector4> { new Vector4(2.5f, -.25f, 3, 4), Vector4.zero, Vector4.one, Vector4.zero });
                mesh.colors = new[] { new Color(.1f, .2f, .3f, .25f), Color.white, Color.white, Color.white };
                Assert.IsTrue(MeshInspection.Supports(mesh, MeshViewport3D.Shading.UV7));
                var uv = MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.UV7);
                Assert.AreEqual(new Color32(127, 191, 0, 255), uv[0]);
                string report = inspection.Report(mesh);
                StringAssert.Contains("UV7: Float32 ×4", report);
                StringAssert.Contains($"U 0…{2.5f:0.###}", report);
                StringAssert.Contains($"V {-.25f:0.###}…1", report);
                Assert.AreEqual(new Color32(64, 64, 64, 255), MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.ColorAlpha)[0]);
                Assert.AreNotEqual(MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.TangentSign)[0],
                    MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.TangentSign)[1]);
            }
            finally { Thread.CurrentThread.CurrentCulture = previousCulture; Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SkinInspectionKeepsMoreThanFourInfluences()
        {
            var mesh = Quad();
            using (var inspection = new MeshInspection())
            using (var counts = new Unity.Collections.NativeArray<byte>(new byte[] { 5, 1, 1, 1 }, Unity.Collections.Allocator.Temp))
            using (var weights = new Unity.Collections.NativeArray<BoneWeight1>(new[] {
                new BoneWeight1 { boneIndex = 3, weight = .4f }, new BoneWeight1 { boneIndex = 4, weight = .3f },
                new BoneWeight1 { boneIndex = 5, weight = .15f }, new BoneWeight1 { boneIndex = 6, weight = .1f },
                new BoneWeight1 { boneIndex = 17, weight = .05f }, new BoneWeight1 { boneIndex = 0, weight = 1 },
                new BoneWeight1 { boneIndex = 0, weight = 1 }, new BoneWeight1 { boneIndex = 0, weight = 1 }
            }, Unity.Collections.Allocator.Temp))
            try {
                mesh.SetBoneWeights(counts, weights);
                Assert.IsTrue(MeshInspection.Supports(mesh, MeshViewport3D.Shading.BoneWeights));
                Assert.IsTrue(MeshInspection.Supports(mesh, MeshViewport3D.Shading.BoneIndices));
                StringAssert.Contains("Skinning: 8 influences", inspection.Report(mesh));
                Assert.AreEqual(new Color32(102, 102, 102, 255), MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.BoneWeights)[0]);
                Assert.AreNotEqual(MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.BoneIndices)[0],
                    MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.BoneIndices)[1]);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [TestCase(MeshViewport3D.Projection.XY, (int)MeshViewport3D.UpAxis.X)]
        [TestCase(MeshViewport3D.Projection.XY, (int)MeshViewport3D.UpAxis.Y)]
        [TestCase(MeshViewport3D.Projection.XY, (int)MeshViewport3D.UpAxis.Z)]
        [TestCase(MeshViewport3D.Projection.XZ, (int)MeshViewport3D.UpAxis.X)]
        [TestCase(MeshViewport3D.Projection.XZ, (int)MeshViewport3D.UpAxis.Y)]
        [TestCase(MeshViewport3D.Projection.XZ, (int)MeshViewport3D.UpAxis.Z)]
        [TestCase(MeshViewport3D.Projection.YZ, (int)MeshViewport3D.UpAxis.X)]
        [TestCase(MeshViewport3D.Projection.YZ, (int)MeshViewport3D.UpAxis.Y)]
        [TestCase(MeshViewport3D.Projection.YZ, (int)MeshViewport3D.UpAxis.Z)]
        public void PlanarPickingUsesParallelRays(MeshViewport3D.Projection projection, int up)
        {
            using (var viewport = new MeshViewport3D { ViewProjection = projection, Up = (MeshViewport3D.UpAxis)up }) {
                viewport.Frame(new Bounds(Vector3.zero, Vector3.one));
                typeof(MeshViewport3D).GetField("currentRect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(viewport, new Rect(0, 0, 100, 100));
                Assert.IsTrue(viewport.TryScreenRay(new Vector2(25, 50), out var left, out var direction));
                Assert.IsTrue(viewport.TryScreenRay(new Vector2(75, 50), out var right, out var other));
                Assert.That((direction - other).sqrMagnitude, Is.LessThan(1e-10f));
                Assert.That((left - right).magnitude, Is.GreaterThan(.1f));
                Assert.That(Mathf.Abs(Vector3.Dot(left - right, direction)), Is.LessThan(1e-6f));
                Vector3 expected = projection == MeshViewport3D.Projection.XY ? Vector3.forward :
                    projection == MeshViewport3D.Projection.XZ ? Vector3.down : Vector3.left;
                Assert.That((direction - expected).sqrMagnitude, Is.LessThan(1e-10f));
            }
        }

        [TestCase((int)MeshViewport3D.UpAxis.X)]
        [TestCase((int)MeshViewport3D.UpAxis.Y)]
        [TestCase((int)MeshViewport3D.UpAxis.Z)]
        public void PerspectivePickingAndFloorFollowTheChosenVerticalWithoutMovingTheMesh(int axis)
        {
            var up = (MeshViewport3D.UpAxis)axis;
            Vector3 vertical = up == MeshViewport3D.UpAxis.X ? Vector3.right :
                up == MeshViewport3D.UpAxis.Z ? Vector3.forward : Vector3.up;
            Vector3 horizontal = up == MeshViewport3D.UpAxis.X ? Vector3.down : Vector3.right;
            var center = new Vector3(.3f, -.2f, .4f);
            var positions = new[] { center - horizontal * 2 - vertical * 2, center + horizontal * 2 - vertical * 2,
                center + horizontal * 2 + vertical * 2, center - horizontal * 2 + vertical * 2 };
            var mesh = new Mesh { vertices = positions, triangles = new[] { 0, 1, 2, 0, 2, 3 } };
            using var viewport = new MeshViewport3D();
            using var inspection = new MeshInspection();
            try {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var type = typeof(MeshViewport3D);
                type.GetField("currentRect", flags).SetValue(viewport, new Rect(0, 0, 100, 100));
                type.GetField("orbit", flags).SetValue(viewport, Vector2.zero);
                viewport.Frame(mesh.bounds);
                var pivot = type.GetField("pivot", flags).GetValue(viewport);
                var distance = type.GetField("distance", flags).GetValue(viewport);
                viewport.Up = up;
                Assert.AreEqual(pivot, type.GetField("pivot", flags).GetValue(viewport));
                Assert.AreEqual(distance, type.GetField("distance", flags).GetValue(viewport));
                Assert.IsTrue(viewport.TryScreenRay(new Vector2(50, 25), out var origin, out var direction));
                Assert.That(Vector3.Dot(direction, vertical), Is.GreaterThan(.1f), "the upper screen half points up the model's selected axis");
                Assert.IsTrue(inspection.Pick(new[] { new MeshViewport3D.Item(mesh, Matrix4x4.identity) }, origin, direction, out int item));
                Assert.AreEqual(0, item);
                var a = viewport.GridPoint(mesh.bounds, 1, 2); var b = viewport.GridPoint(mesh.bounds, -3, 7);
                Assert.That(Vector3.Dot(b - a, vertical), Is.EqualTo(0).Within(1e-6f));
                Assert.That(Vector3.Dot(a, vertical), Is.LessThan(Vector3.Dot(mesh.bounds.min, vertical)), "floor lies below the model");
                CollectionAssert.AreEqual(positions, mesh.vertices);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void LineAndQuadTopologyRemainInspectable()
        {
            var mesh = Quad();
            using (var inspection = new MeshInspection())
            try {
                mesh.SetIndices(new[] { 0, 1, 3, 2 }, MeshTopology.LineStrip, 0);
                Assert.AreEqual(6, MeshViewport3D.EdgeIndices(mesh).Count);
                StringAssert.Contains("LineStrip", inspection.Report(mesh));
                Assert.IsFalse(MeshInspection.HasOnlyTriangles(mesh));
                Assert.IsNull(UvLayer3D.BuildBoundaryMesh(mesh, 0));
                mesh.SetIndices(new[] { 0, 1, 3, 2 }, MeshTopology.Quads, 0);
                Assert.AreEqual(8, MeshViewport3D.EdgeIndices(mesh).Count);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [TestCase(RenderTextureReadWrite.Default)]
        [TestCase(RenderTextureReadWrite.Linear)]
        [TestCase(RenderTextureReadWrite.sRGB)]
        public void AttributeColorsRenderIntoUvLayoutUsingTheSameEncodingAs3D(RenderTextureReadWrite readWrite)
        {
            var mesh = Quad(); var canvas = new UvCanvasView { FillHidden = false, ShowBorder = false,
                InspectionShading = MeshViewport3D.Shading.Normals };
            RenderTexture layer = null, sampled = null; Texture2D pixels = null;
            var previous = RenderTexture.active;
            bool previousSrgbWrite = GL.sRGBWrite;
            try {
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
                canvas.Init();
                // Default exercises the layer allocated by the canvas; the other cases
                // exercise caller-provided targets with explicit storage encodings.
                if (readWrite != RenderTextureReadWrite.Default)
                    layer = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32, readWrite);
                layer = canvas.RenderUvLayer(new UvToolContext { PreviewUvChannel = 0 }, mesh,
                    new MeshEntry { originalMesh = mesh }, layer, 32, false);
                Assert.IsNotNull(layer);
                // Read the values a shader samples, including the layer's sRGB decode.
                // Raw ReadPixels bytes from an sRGB target encode linear 0.5 as ~0.735.
                sampled = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                GL.sRGBWrite = false;
                Graphics.Blit(layer, sampled);
                RenderTexture.active = sampled;
                pixels = new Texture2D(32, 32, TextureFormat.RGBA32, false, true);
                pixels.ReadPixels(new Rect(0, 0, 32, 32), 0, 0); pixels.Apply();
                var color = pixels.GetPixel(16, 16);
                Color expected = MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.Normals)[0];
                Assert.That(color.r, Is.EqualTo(expected.r).Within(.01f));
                Assert.That(color.g, Is.EqualTo(expected.g).Within(.01f));
                Assert.That(color.b, Is.EqualTo(expected.b).Within(.01f));
                Assert.That(color.a, Is.EqualTo(expected.a).Within(.01f));
            }
            finally {
                GL.sRGBWrite = previousSrgbWrite;
                RenderTexture.active = previous; canvas.Cleanup();
                if (layer) { layer.Release(); Object.DestroyImmediate(layer); }
                if (sampled) { sampled.Release(); Object.DestroyImmediate(sampled); }
                if (pixels) Object.DestroyImmediate(pixels); Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void GeneratedUv0ContentChoosesItsChannelWithoutFallingBackToSceneMeshes()
        {
            var mesh = Quad();
            var context = new UvToolContext { PreviewUvChannel = 1 };
            var canvas = new UvCanvasView { EntriesOverride = new List<MeshEntry> {
                new MeshEntry { include = true, originalMesh = mesh } } };
            try {
                Assert.IsTrue(canvas.EnsurePreviewChannel(context));
                Assert.AreEqual(0, context.PreviewUvChannel);
                Assert.IsTrue(canvas.HasPreviewChannel(context, 0));
                Assert.IsFalse(canvas.HasPreviewChannel(context, 1));
                mesh.uv = null;
                canvas.ClearFrameCaches();
                Assert.IsFalse(canvas.HasPreviewChannel(context, 0));
                canvas.EntriesOverride.Clear();
                Assert.AreEqual(0, canvas.Entries(context).Count);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void BordersFollowUvSeamsAndIgnoreNormalSplits()
        {
            var mesh = new Mesh();
            Mesh continuous = null, seam = null;
            try {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up,
                    Vector3.right, Vector3.one, Vector3.up };
                // Keep the quad planar; duplicated edge vertices represent hard normals.
                var p = mesh.vertices; p[4].z = 0; mesh.vertices = p;
                mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up,
                    Vector2.right, Vector2.one, Vector2.up };
                mesh.uv2 = new[] { Vector2.zero, Vector2.right, Vector2.up,
                    new Vector2(2, 0), new Vector2(3, 1), new Vector2(2, 1) };
                continuous = UvLayer3D.BuildBoundaryMesh(mesh, 0);
                seam = UvLayer3D.BuildBoundaryMesh(mesh, 1);
                Assert.AreEqual(MeshTopology.Lines, continuous.GetTopology(0));
                Assert.AreEqual(8, continuous.GetIndices(0).Length, "only four outside edges on continuous UV0");
                Assert.AreEqual(12, seam.GetIndices(0).Length, "UV1 seam exposes both triangle borders");
                Assert.IsNull(UvLayer3D.BuildBoundaryMesh(mesh, 2));
                CollectionAssert.AreEqual(mesh.vertices, seam.vertices);
            }
            finally {
                Object.DestroyImmediate(mesh);
                if (continuous) Object.DestroyImmediate(continuous);
                if (seam) Object.DestroyImmediate(seam);
            }
        }

        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        public void CheckerLayerRendersForMeshWithoutSceneRenderer(bool colorMode, bool showR, bool showG)
        {
            var mesh = Quad();
            var canvas = new UvCanvasView { CheckerEnabled = true, FillHidden = true, ShowBorder = false,
                CheckerColorMode = colorMode, CheckerShowR = showR, CheckerShowG = showG };
            var context = new UvToolContext { PreviewUvChannel = 0 };
            RenderTexture layer = null; Texture2D pixels = null;
            var previous = RenderTexture.active;
            try {
                canvas.Init();
                var entry = new MeshEntry { originalMesh = mesh, include = true, previewTexture = Texture2D.redTexture };
                layer = canvas.RenderUvLayer(context, mesh, entry, null, 64, drawBorders: false);
                Assert.IsNotNull(layer);
                RenderTexture.active = layer;
                pixels = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); pixels.Apply();
                float min = 1, max = 0;
                foreach (var color in pixels.GetPixels()) {
                    Assert.That(color.a, Is.GreaterThan(0f));
                    min = Mathf.Min(min, color.r); max = Mathf.Max(max, color.r);
                }
                Assert.That(max - min, Is.GreaterThan(.02f), "checker has distinct cells");
                if (colorMode) {
                    var low = pixels.GetPixel(16, 16); var high = pixels.GetPixel(48, 48);
                    Assert.That(high.r - low.r, Is.GreaterThan(.05f), "UV values form a gradient");
                    if (showR && showG) Assert.That(high.b, Is.LessThan(.01f));
                    else {
                        Assert.That(high.g, Is.EqualTo(high.r).Within(.01f));
                        Assert.That(high.b, Is.EqualTo(high.r).Within(.01f));
                    }
                }
            }
            finally {
                RenderTexture.active = previous;
                canvas.Cleanup();
                if (layer) { layer.Release(); Object.DestroyImmediate(layer); }
                if (pixels) Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(mesh);
            }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator ShellPreviewColorsToolMeshWithoutRendererOrFillCallbacks()
        {
            var mesh = Quad();
            var canvas = new UvCanvasView { CurrentPreviewMode = UvCanvasView.PreviewMode.Shells3D,
                FillHidden = true, ShowBorder = false };
            var context = new UvToolContext { PreviewUvChannel = 0 };
            RenderTexture layer = null; Texture2D pixels = null;
            var previous = RenderTexture.active;
            try {
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                var entry = new MeshEntry { originalMesh = mesh, include = true, previewTexture = Texture2D.blackTexture };
                canvas.Init();
                double deadline = UnityEditor.EditorApplication.timeSinceStartup + 10;
                while (canvas.GetPreviewShellCache(context, mesh, 0) == null && UnityEditor.EditorApplication.timeSinceStartup < deadline) {
                    canvas.PollPreviewJobs(); yield return null;
                }
                Assert.IsNotNull(canvas.GetPreviewShellCache(context, mesh, 0), "async shell cache completes");
                layer = canvas.RenderUvLayer(context, mesh, entry, null, 64, drawBorders: false);
                RenderTexture.active = layer;
                pixels = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); pixels.Apply();
                Color color = pixels.GetPixel(16, 16);
                Assert.That(color.a, Is.GreaterThan(.99f), "shell mode is opaque, independent of the tool's fill modes");
                Assert.That(Mathf.Max(color.r, Mathf.Max(color.g, color.b)), Is.GreaterThan(.1f), "AO black texture cannot hide shell colours");
                Assert.That(Mathf.Max(color.r, Mathf.Max(color.g, color.b)) - Mathf.Min(color.r, Mathf.Min(color.g, color.b)),
                    Is.GreaterThan(.05f), "shell surface has a palette colour");
            }
            finally {
                RenderTexture.active = previous; canvas.Cleanup();
                if (layer) { layer.Release(); Object.DestroyImmediate(layer); }
                if (pixels) Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(mesh);
            }
        }

        [UnityTest]
        public IEnumerator CanvasAndSceneShellColorsMatchOnEverySelectedUvChannel()
        {
            var mesh = Quad();
            var canvas = new UvCanvasView();
            var context = new UvToolContext();
            try {
                for (int channel = 0; channel < 3; ++channel) {
                    var offset = Vector2.one * channel * .2f;
                    mesh.SetUVs(channel, new List<Vector2> { offset, offset + Vector2.right,
                        offset + Vector2.up, offset + Vector2.one });
                }
                var entry = new MeshEntry { originalMesh = mesh, include = true };
                canvas.Init();
                foreach (int channel in new[] { 0, 1, 2, 0 }) {
                    context.PreviewUvChannel = channel;
                    double deadline = EditorApplication.timeSinceStartup + 10;
                    var shells = canvas.GetPreviewShellCache(context, mesh, channel);
                    while (shells == null && EditorApplication.timeSinceStartup < deadline) {
                        canvas.PollPreviewJobs(); yield return null;
                        shells = canvas.GetPreviewShellCache(context, mesh, channel);
                    }
                    Assert.IsNotNull(shells, "Async shell preparation must complete");
                    var sceneKeys = new ShellColorModelPreview.PreviewShellCache(channel).GetOrBuild(mesh);
                    foreach (var shell in shells.shells) {
                        int canvasKey = canvas.GetShellColorKey(context, shell, entry);
                        foreach (int face in shell.faceIndices)
                            Assert.AreEqual(sceneKeys[face], canvasKey, "2D and 3D shell colors must agree on UV" + channel);
                    }
                }
            }
            finally { canvas.Cleanup(); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SceneShellCacheUsesRequestedPreviewUvChannel()
        {
            var mesh = Quad();
            try {
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                mesh.SetUVs(2, new List<Vector2> { Vector2.one, Vector2.one + Vector2.right,
                    Vector2.one + Vector2.up, Vector2.one * 2 });
                var uv0 = new ShellColorModelPreview.PreviewShellCache(0).GetOrBuild(mesh);
                var uv2 = new ShellColorModelPreview.PreviewShellCache(2).GetOrBuild(mesh);
                Assert.AreEqual(uv0[0], uv0[1], "continuous chart");
                Assert.AreNotEqual(uv0[0], uv2[0], "shell colour key is taken from the requested channel");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        static Mesh Quad()
        {
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0) };
            mesh.normals = new[] { Vector3.forward, Vector3.back, Vector3.up, Vector3.right };
            mesh.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, -1), new Vector4(0, 0, 1, 1), new Vector4(-1, 0, 0, 1) };
            mesh.uv = new[] { Vector2.zero, new Vector2(1.5f, 0.25f), new Vector2(-0.25f, 1f), Vector2.one };
            mesh.triangles = new[] { 0, 1, 2, 1, 3, 2 };
            return mesh;
        }

        [TestCase(false, 0, .59f)]
        [TestCase(false, 1, 1f)]
        [TestCase(true, 0, .59f)]
        [TestCase(true, 1, 1f)]
        public void CheckerOn3DModelIsOpaqueAndIgnoresIslandFill(bool colorMode, int channel, float fillAlpha)
        {
            var mesh = Quad();
            var canvas = new UvCanvasView { CheckerEnabled = true, CheckerColorMode = colorMode,
                CheckerShowR = true, CheckerShowG = false, ShowBorder = false, FillAlpha = fillAlpha };
            var context = new UvToolContext { PreviewUvChannel = channel };
            var cameraObject = new GameObject("Checker surface test camera") { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture layer = null, target = null; Texture2D pixels = null;
            Material material = null, baseMaterial = null;
            var previous = RenderTexture.active;
            int fillCalls = 0;
            try {
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                mesh.uv2 = new[] { Vector2.zero, Vector2.right * .5f, Vector2.up * .5f, Vector2.one * .5f };
                canvas.SetFillModes(new List<UvCanvasView.FillModeEntry> { new UvCanvasView.FillModeEntry {
                    name = "Opaque island fill", drawCallback = (view, x, y, size, m, entry) => {
                        ++fillCalls;
                        view.GlCheckerBg(x, y, size, 1, 1f);
                    } } });
                canvas.Init();
                layer = canvas.RenderUvLayer(context, mesh,
                    new MeshEntry { originalMesh = mesh, previewTexture = Texture2D.redTexture }, null, 64, false);
                RenderTexture.active = layer;
                pixels = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); pixels.Apply();
                foreach (var color in pixels.GetPixels()) Assert.That(color.a, Is.GreaterThan(.99f), "checker must fully replace the baked surface");
                Assert.AreEqual(0, fillCalls, "Islands fill must not obscure Checker even at alpha 1");

                var shader = Shader.Find("Hidden/MeshLab/UvOverlay"); Assert.IsNotNull(shader);
                material = new Material(shader);
                material.SetTexture("_MainTex", layer); material.SetColor("_Color", Color.white);
                material.SetFloat("_UVChannel", channel);
                baseMaterial = new Material(Shader.Find("Unlit/Color")); baseMaterial.color = Color.red;
                target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32); target.Create();
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false; camera.orthographic = true; camera.orthographicSize = .5f;
                camera.transform.position = new Vector3(.5f, .5f, -2);
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.red;
                camera.cullingMask = 1 << 31; camera.targetTexture = target;
                Graphics.DrawMesh(mesh, Matrix4x4.identity, baseMaterial, 31, camera, 0, null,
                    UnityEngine.Rendering.ShadowCastingMode.Off, false);
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 31, camera, 0, null,
                    UnityEngine.Rendering.ShadowCastingMode.Off, false);
                camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); pixels.Apply();
                if (colorMode) {
                    var low = pixels.GetPixel(8, 32); var high = pixels.GetPixel(56, 32);
                    Assert.That(high.r - low.r, Is.GreaterThan(.25f), "UV gradient remains visible over the baked red surface");
                    Assert.That(high.g, Is.EqualTo(high.r).Within(.03f), "red baked material must not leak through the grayscale checker");
                    Assert.That(high.r, channel == 0 ? Is.GreaterThan(.7f) : Is.LessThan(.7f), "3D samples the selected UV channel");
                }
                else {
                    float min = 1, max = 0;
                    foreach (var color in pixels.GetPixels()) { min = Mathf.Min(min, color.g); max = Mathf.Max(max, color.g); }
                    Assert.That(max - min, Is.GreaterThan(.3f), "3D checker retains cell contrast over a baked material");
                }
            }
            finally {
                RenderTexture.active = previous; canvas.Cleanup(); Object.DestroyImmediate(cameraObject);
                if (layer) { layer.Release(); Object.DestroyImmediate(layer); }
                if (target) { target.Release(); Object.DestroyImmediate(target); }
                if (pixels) Object.DestroyImmediate(pixels);
                if (material) Object.DestroyImmediate(material);
                if (baseMaterial) Object.DestroyImmediate(baseMaterial);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void UvOverlayAppliesVertexColorsOnlyWhenAsked()
        {
            // The viewport's grid draws its fades as vertex colours through this shader;
            // the UV layer on a model must keep ignoring the model's own vertex colours.
            var mesh = Quad();
            var cameraObject = new GameObject("UV overlay vertex colour test camera") { hideFlags = HideFlags.HideAndDontSave };
            Material material = null; RenderTexture target = null; Texture2D pixels = null;
            var previous = RenderTexture.active;
            try {
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                var tint = new Color(1f, 0f, 0f, .5f);
                mesh.colors = new[] { tint, tint, tint, tint };
                var shader = Shader.Find("Hidden/MeshLab/UvOverlay"); Assert.IsNotNull(shader);
                material = new Material(shader);
                material.SetTexture("_MainTex", Texture2D.whiteTexture);
                material.SetColor("_Color", Color.white);
                target = new RenderTexture(16, 16, 24, RenderTextureFormat.ARGB32); target.Create();
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false; camera.orthographic = true; camera.orthographicSize = .5f;
                camera.transform.position = new Vector3(.5f, .5f, -2);
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 31; camera.targetTexture = target;
                pixels = new Texture2D(16, 16, TextureFormat.RGBA32, false);

                Color Render()
                {
                    Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 31, camera, 0, null,
                        UnityEngine.Rendering.ShadowCastingMode.Off, false);
                    camera.Render();
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 16, 16), 0, 0); pixels.Apply();
                    return pixels.GetPixel(8, 8);
                }

                var plain = Render();
                Assert.That(plain.g, Is.GreaterThan(.9f), "by default the overlay is the texture times the tint, whatever the vertices carry");
                material.SetFloat("_UseVertexColor", 1);
                var coloured = Render();
                Assert.That(coloured.g, Is.LessThan(.1f), "asked for, the vertex colour multiplies in");
                // Half alpha over black: .5 in a gamma project, ~.73 read back sRGB-encoded in a linear one.
                Assert.That(coloured.r, Is.InRange(.35f, .8f), "and its alpha blends the line over the background");
            }
            finally {
                RenderTexture.active = previous;
                Object.DestroyImmediate(cameraObject);
                if (target) { target.Release(); Object.DestroyImmediate(target); }
                if (pixels) Object.DestroyImmediate(pixels);
                if (material) Object.DestroyImmediate(material);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void UvOverlayShowsCheckerEvenWhenModelVertexColorsAreBlack()
        {
            var mesh = Quad();
            var cameraObject = new GameObject("UV overlay test camera") { hideFlags = HideFlags.HideAndDontSave };
            Material material = null; RenderTexture target = null; Texture2D pixels = null;
            var previous = RenderTexture.active;
            try {
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                mesh.colors = new[] { Color.clear, Color.clear, Color.clear, Color.clear };
                var shader = Shader.Find("Hidden/MeshLab/UvOverlay"); Assert.IsNotNull(shader);
                material = new Material(shader);
                material.SetTexture("_MainTex", CheckerTexturePreview.GetCheckerTexture());
                material.SetColor("_Color", Color.white);
                target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32); target.Create();
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false; camera.orthographic = true; camera.orthographicSize = .5f;
                camera.transform.position = new Vector3(.5f, .5f, -2);
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 31; camera.targetTexture = target;
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 31, camera, 0, null,
                    UnityEngine.Rendering.ShadowCastingMode.Off, false);
                camera.Render();
                RenderTexture.active = target;
                pixels = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); pixels.Apply();
                float min = 1, max = 0;
                foreach (var color in pixels.GetPixels()) { min = Mathf.Min(min, color.r); max = Mathf.Max(max, color.r); }
                Assert.That(max - min, Is.GreaterThan(.1f), "3D checker is independent of model vertex tint/alpha");
            }
            finally {
                RenderTexture.active = previous;
                Object.DestroyImmediate(cameraObject);
                if (target) { target.Release(); Object.DestroyImmediate(target); }
                if (pixels) Object.DestroyImmediate(pixels);
                if (material) Object.DestroyImmediate(material);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void ShadingModesEncodeTheMeshDataAsColors()
        {
            var mesh = Quad();
            try {
                var normals = MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.Normals);
                Assert.AreEqual(new Color32(128, 128, 255, 255), normals[0]);   // +z
                Assert.AreEqual(new Color32(128, 128, 0, 255), normals[1]);     // -z
                Assert.AreEqual(new Color32(128, 255, 128, 255), normals[2]);   // +y
                var tangents = MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.Tangents);
                Assert.AreEqual(new Color32(255, 128, 128, 255), tangents[0]);
                Assert.AreEqual(new Color32(0, 128, 128, 255), tangents[3]);
                // UVs wrap: 1.5 → 0.5, -0.25 → 0.75, 1.0 → 0.
                var uv = MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.UV0);
                Assert.AreEqual(127, uv[1].r); Assert.AreEqual(63, uv[1].g);
                Assert.AreEqual(191, uv[2].r); Assert.AreEqual(0, uv[2].g); // 1.0 wraps to 0, as above
                Assert.AreEqual(0, uv[3].r); Assert.AreEqual(0, uv[3].g);
                // Missing data: no UV1 → null; no vertex colours → neutral grey.
                Assert.IsNull(MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.UV1));
                Assert.AreEqual(new Color32(200, 200, 200, 255), MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.VertexColors)[0]);
                // Unique edges of two triangles sharing one: five lines.
                Assert.AreEqual(10, MeshViewport3D.EdgeIndices(mesh).Count);
            }
            finally { Object.DestroyImmediate(mesh); }
        }
        [TestCase(false)]
        [TestCase(true)]
        public void LightmapSpotOutlineUsesTheRendererAtlasTransform(bool selected)
        {
            var mesh = Quad(); mesh.uv2 = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            var root = new GameObject("Lightmap spot outline");
            var renderer = root.AddComponent<MeshRenderer>();
            renderer.lightmapIndex = 0; renderer.lightmapScaleOffset = new Vector4(.25f, .25f, .5f, .25f);
            var entry = new MeshEntry { originalMesh = mesh, renderer = renderer };
            var context = new UvToolContext { PreviewUvChannel = 1 };
            var canvas = new UvCanvasView { SpotMode = true, CurrentPreviewMode = UvCanvasView.PreviewMode.Lightmap };
            context.PreviewShellDataCache[((long)mesh.GetInstanceID() << 8) | 1] = UvTopology.BuildShellData(mesh.uv2, mesh.triangles);
            var target = new RenderTexture(128, 128, 0, RenderTextureFormat.ARGB32); target.Create();
            var pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            try {
                canvas.Init();
                canvas.ApplySpotHit(true, new ShellUvHit { meshEntry = entry, shellId = 0 }, null);
                if (selected) Assert.IsTrue(canvas.SelectSpotHover());
                RenderTexture.active = target; GL.Clear(true, true, Color.clear);
                GL.PushMatrix();
                try {
                    GL.LoadPixelMatrix(0, 128, 128, 0); canvas.GlMat.SetPass(0);
                    typeof(UvCanvasView).GetMethod("DrawSpotOutlines", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Invoke(canvas, new object[] { context, new List<System.ValueTuple<Mesh, MeshEntry, int>> { (mesh, entry, 0) }, 0f, 0f, 128f });
                }
                finally { GL.PopMatrix(); }
                pixels.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); pixels.Apply();
                float green = 0;
                for (int y = 47; y <= 49; ++y)
                    for (int x = 63; x <= 65; ++x) green = Mathf.Max(green, pixels.GetPixel(x, y).g);
                Assert.That(green, Is.GreaterThan(.7f), "Spot outlines must follow the scaled, offset lightmap UVs");
            }
            finally {
                RenderTexture.active = previous; canvas.Cleanup(); target.Release();
                Object.DestroyImmediate(target); Object.DestroyImmediate(pixels); Object.DestroyImmediate(root); Object.DestroyImmediate(mesh);
            }
        }

    }
}
