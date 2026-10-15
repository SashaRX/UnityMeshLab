using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ViewportPerformanceTests
    {
        [Test]
        public void UvBatchesSurviveMotionAndReleaseReplacedOrInvalidatedGeometry()
        {
            using var cache = new UvPreviewGeometry();
            var uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            var triangles = new[] { 0, 1, 2 };
            int builds = 0;
            void Build(UvPreviewGeometry.Builder batch)
            {
                ++builds; batch.SetColor(Color.red);
                foreach (var value in uv) batch.Vertex(value);
            }
            var first = cache.Get(uv, triangles, "fill", 0, MeshTopology.Triangles, Build);
            for (int i = 0; i < 120; ++i) {
                Assert.AreSame(first, cache.Get(uv, triangles, "fill", 0, MeshTopology.Triangles, Build));
                var point = UvPreviewGeometry.Matrix(i, i * 2, i + 1).MultiplyPoint3x4(new Vector3(.25f, .75f, 0));
                Assert.That(point.x, Is.EqualTo(i + .25f * (i + 1)).Within(.0001f));
                Assert.That(point.y, Is.EqualTo(i * 2 + .75f * (i + 1)).Within(.0001f));
            }
            Assert.AreEqual(1, builds, "motion must not iterate over triangles or upload another mesh");
            var replacement = cache.Get(uv, triangles, "fill", 1, MeshTopology.Triangles, Build);
            Assert.IsFalse(first, "colour/selection changes release the old batch");
            Assert.AreEqual(2, builds);
            cache.Dispose();
            Assert.IsFalse(replacement, "topology changes and closing the canvas release GPU buffers");
        }

        [Test]
        public void GridAndAxesFollowTranslationWithoutRebuildingAndReleaseOnInvalidation()
        {
            using var view = new MeshViewport3D();
            var bounds = new Bounds(Vector3.zero, new Vector3(2, 3, 4));
            var grid = view.GridMesh(bounds); var axes = view.AxesMesh();
            bounds.center = new Vector3(100, -20, 15);
            Assert.AreSame(grid, view.GridMesh(bounds)); Assert.AreSame(axes, view.AxesMesh());
            view.Up = MeshViewport3D.UpAxis.Z;
            var next = view.GridMesh(bounds);
            Assert.IsFalse(grid); Assert.IsTrue(next);
            view.InvalidateCaches();
            Assert.IsFalse(next); Assert.IsFalse(axes);
        }

        [Test]
        public void LightmapUvsAreReusedUntilTheAtlasTransformChanges()
        {
            var go = new GameObject("UV transform test");
            var canvas = new UvCanvasView { CurrentPreviewMode = UvCanvasView.PreviewMode.Lightmap };
            try {
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.lightmapIndex = 0; renderer.lightmapScaleOffset = new Vector4(.5f, .25f, .1f, .2f);
                var entry = new MeshEntry { renderer = renderer };
                var ctx = new UvToolContext { PreviewUvChannel = 1 };
                var uv = new[] { new Vector2(.4f, .8f) };
                var display = canvas.DisplayUvs(ctx, entry, uv);
                Assert.That(display[0].x, Is.EqualTo(.3f).Within(.0001f));
                Assert.That(display[0].y, Is.EqualTo(.4f).Within(.0001f));
                go.transform.position = Vector3.one;
                Assert.AreSame(display, canvas.DisplayUvs(ctx, entry, uv));
                renderer.lightmapScaleOffset = new Vector4(1, 1, 0, 0);
                Assert.AreNotSame(display, canvas.DisplayUvs(ctx, entry, uv));
                canvas.CurrentPreviewMode = UvCanvasView.PreviewMode.Off;
                Assert.AreSame(uv, canvas.DisplayUvs(ctx, entry, uv));
            }
            finally { canvas.Cleanup(); Object.DestroyImmediate(go); }
        }

        [Test]
        public void CachedInspectionAvailabilityFollowsChannelChanges()
        {
            using var inspection = new MeshInspection();
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            try {
                Assert.IsFalse(inspection.SupportsMode(mesh, MeshViewport3D.Shading.VertexColors));
                VertexChannels.SetColors(mesh, new[] { Color.red, Color.green, Color.blue });
                Assert.IsTrue(inspection.SupportsMode(mesh, MeshViewport3D.Shading.VertexColors));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void UvSelectionValidationAndChannelWritesRefreshCachedPixels()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("This pixel regression requires a graphics device.");
            var uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            var triangles = new[] { 0, 1, 2 };
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, uv = uv, triangles = triangles };
            var canvas = new UvCanvasView(); canvas.Init();
            var ctx = new UvToolContext { PreviewUvChannel = 0 };
            var entry = new MeshEntry { originalMesh = mesh };
            var shell = new UvShell { shellId = 0, faceIndices = new List<int> { 0 } };
            ctx.PreviewShellDataCache[(long)mesh.GetInstanceID() << 8] = new PreviewShellData {
                shells = new List<UvShell> { shell }, uvs = uv, triangles = triangles,
                colorKeys = new Dictionary<int, int> { [0] = 0 }
            };
            var target = new RenderTexture(32, 32, 0);
            var pixels = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try {
                target.Create();
                var normal = Render(() => canvas.GlFillSh(ctx, 0, 0, 32, mesh, 1, 3, entry, -1, -1));
                var selected = Render(() => canvas.GlFillSh(ctx, 0, 0, 32, mesh, 1, 3, entry, -1, 0));
                Assert.Greater(selected.g, normal.g + .1f, "selection replaces the cached colours");
                var unselected = Render(() => canvas.GlFillSh(ctx, 0, 0, 32, mesh, 1, 3, entry, -1, -1));
                Assert.That(unselected.g, Is.EqualTo(normal.g).Within(.01f));

                var issues = new[] { TransferValidator.TriIssue.Overlap };
                Assert.Greater(Render(() => canvas.GlFillValidation(0, 0, 32, uv, triangles, 1, 3, issues)).r, .05f);
                canvas.ValidationFilterMask = TransferValidator.TriIssue.Inverted;
                Assert.Less(Render(() => canvas.GlFillValidation(0, 0, 32, uv, triangles, 1, 3, issues)).r, .01f);
                issues = new[] { TransferValidator.TriIssue.Inverted };
                Assert.Greater(Render(() => canvas.GlFillValidation(0, 0, 32, uv, triangles, 1, 3, issues)).r, .05f);

                var original = canvas.RdUvCached(mesh, 0);
                var statuses = new[] { TriangleStatus.Accepted };
                Assert.Greater(Render(() => canvas.GlFillSt(0, 0, 32, original, triangles, 1, 3, statuses)).g, .1f);
                VertexChannels.SetUvs(mesh, 0, new[] { Vector2.one, Vector2.one + Vector2.right, Vector2.one + Vector2.up });
                var updated = canvas.RdUvCached(mesh, 0);
                Assert.AreNotSame(original, updated);
                Assert.Less(Render(() => canvas.GlFillSt(0, 0, 32, updated, triangles, 1, 3, statuses)).g, .01f);
            }
            finally {
                RenderTexture.active = previous; canvas.Cleanup(); Object.DestroyImmediate(mesh);
                target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(pixels);
            }

            Color Render(Action draw)
            {
                RenderTexture.active = target; GL.Clear(false, true, Color.black); GL.PushMatrix();
                try { GL.LoadPixelMatrix(0, 32, 32, 0); canvas.GlMat.SetPass(0); draw(); }
                finally { GL.PopMatrix(); }
                pixels.ReadPixels(new Rect(0, 0, 32, 32), 0, 0); pixels.Apply();
                return pixels.GetPixel(8, 8);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CachedUvDrawMatchesImmediateDrawingDuringPanAndZoom(bool wire)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("This pixel regression requires a graphics device.");
            var canvas = new UvCanvasView(); canvas.Init();
            var uv = new[] { new Vector2(.13f, .17f), new Vector2(.83f, .26f), new Vector2(.34f, .87f) };
            var triangles = new[] { 0, 1, 2 };
            var statuses = new[] { TriangleStatus.Accepted };
            var target = new RenderTexture(128, 128, 0, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try {
                target.Create();
                foreach (float size in new[] { 70f, 110f, 155f }) {
                    const float x = -7, y = 11;
                    var expected = Render(() => {
                        GL.Begin(wire ? GL.LINES : GL.TRIANGLES);
                        GL.Color(wire ? new Color(.3f, .8f, 1, .35f) : UvCanvasView.cAccept);
                        if (wire) {
                            UvCanvasView.Vx(x, y, size, uv[0]); UvCanvasView.Vx(x, y, size, uv[1]);
                            UvCanvasView.Vx(x, y, size, uv[1]); UvCanvasView.Vx(x, y, size, uv[2]);
                            UvCanvasView.Vx(x, y, size, uv[2]); UvCanvasView.Vx(x, y, size, uv[0]);
                        }
                        else foreach (var point in uv) UvCanvasView.Vx(x, y, size, point);
                        GL.End();
                    });
                    var actual = Render(() => {
                        if (wire) canvas.GlWr(x, y, size, uv, triangles, 1, 3);
                        else canvas.GlFillSt(x, y, size, uv, triangles, 1, 3, statuses);
                    });
                    int lit = 0;
                    for (int i = 0; i < actual.Length; ++i) {
                        if (Mathf.Max(expected[i].r, Mathf.Max(expected[i].g, expected[i].b)) > .05f) ++lit;
                        Assert.That(actual[i].r, Is.EqualTo(expected[i].r).Within(.01f));
                        Assert.That(actual[i].g, Is.EqualTo(expected[i].g).Within(.01f));
                        Assert.That(actual[i].b, Is.EqualTo(expected[i].b).Within(.01f));
                    }
                    Assert.Greater(lit, wire ? 30 : 200, "a blank reference cannot pass the comparison");
                }
            }
            finally {
                RenderTexture.active = previous; canvas.Cleanup();
                target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(pixels);
            }

            Color[] Render(Action draw)
            {
                RenderTexture.active = target; GL.Clear(false, true, Color.black);
                GL.PushMatrix();
                try { GL.LoadPixelMatrix(0, 128, 128, 0); canvas.GlMat.SetPass(0); draw(); }
                finally { GL.PopMatrix(); }
                pixels.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); pixels.Apply();
                return pixels.GetPixels();
            }
        }
    }
}
