using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ViewportElementsTests
    {
        static readonly Vector3[] Positions = { Vector3.zero, Vector3.right, Vector3.up,
            new Vector3(10, 0, 0), new Vector3(11, 0, 0), new Vector3(10, 1, 0) };
        static readonly Vector2[] Uv = { Vector2.zero, Vector2.right, Vector2.up, Vector2.zero, Vector2.right, Vector2.up };
        static readonly int[] Triangles = { 0, 1, 2, 3, 4, 5 };

        [Test]
        public void DiagnosticBuffersAreReusedAndRestoreReleasesThemWithoutChangingTheSource()
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one * 9 },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }, triangles = new[] { 0, 1, 2 } };
            var owner = new GameObject("Diagnostic preview");
            var filter = owner.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
            var renderer = owner.AddComponent<MeshRenderer>();
            var ctx = new UvToolContext();
            ctx.MeshEntries.Add(new MeshEntry { originalMesh = mesh, meshFilter = filter, renderer = renderer });
            using var preview = new PrefabBuilderPreview(); using var view = new MeshViewport3D();
            try {
                preview.ActivateProblemPreview(ctx); preview.BuildEdgeOverlays(ctx);
                preview.DrawEdgeWireframe(view); preview.DrawUnusedVertexDots(view);
                var edges = Buffers("edgeBuffers"); var points = Buffers("pointBuffers");
                Assert.AreEqual(1, edges.Count); Assert.AreEqual(1, points.Count);
                var edge = edges[0].mesh; var point = points[0].mesh;
                Assert.AreEqual(MeshTopology.Lines, edge.GetTopology(0)); Assert.AreEqual(6, edge.vertexCount);
                Assert.AreEqual(1, point.vertexCount); Assert.AreEqual(Vector3.one * 9, point.vertices[0]);
                Assert.AreNotSame(mesh, filter.sharedMesh);
                preview.DrawEdgeWireframe(view); preview.DrawUnusedVertexDots(view);
                Assert.AreSame(edge, edges[0].mesh); Assert.AreSame(point, points[0].mesh);
                preview.Restore();
                Assert.IsFalse(edge); Assert.IsFalse(point); Assert.AreSame(mesh, filter.sharedMesh);
                Assert.AreEqual(0, edges.Count); Assert.AreEqual(0, points.Count);
                Assert.AreEqual(4, mesh.vertexCount); Assert.AreEqual(0, mesh.colors.Length);
            }
            finally { preview.Restore(); Object.DestroyImmediate(owner); Object.DestroyImmediate(mesh); }

            List<(Mesh mesh, Matrix4x4 matrix)> Buffers(string field) =>
                (List<(Mesh mesh, Matrix4x4 matrix)>)typeof(PrefabBuilderPreview).GetField(field,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(preview);
        }

        [Test]
        public void DisconnectedOverlappingUvIslandsHaveDistinctColorsAndLodOrderDoesNotMatter()
        {
            var source = new ShellPreviewColors.SourceData(Positions, Uv, Triangles);
            // Same UV bounds, opposite face order and a shrunken LOD surface.
            var target = UvTopology.BuildShellData(Uv, new[] { 3, 4, 5, 0, 1, 2 });
            var positions = (Vector3[])Positions.Clone();
            positions[1] *= .6f; positions[2] *= .6f;
            var colors = ShellPreviewColors.Match(source, target, positions, Matrix4x4.identity, null);
            Assert.AreEqual(1, colors[0]); Assert.AreEqual(0, colors[1]);
            Assert.AreNotEqual(colors[0], colors[1], "connectivity, not equal UV boxes, owns the palette IDs");
            var provenance = new[] { 1, 1, 1, 0, 0, 0 };
            colors = ShellPreviewColors.Match(source, target, positions, Matrix4x4.identity, provenance);
            Assert.AreEqual(0, colors[0]); Assert.AreEqual(1, colors[1], "explicit transfer association wins over nearest geometry");
        }

        [UnityTest]
        public IEnumerator LodColorBindingsArePreparedOnceAndSurviveSceneMotion()
        {
            var source = new Mesh { name = "Part_LOD0", vertices = Positions, uv = Uv, triangles = Triangles };
            var target = new Mesh { name = "Part_LOD1", vertices = Positions, uv = Uv, triangles = new[] { 3, 4, 5, 0, 1, 2 } };
            using var colors = new ShellPreviewColors();
            var ctx = new UvToolContext { PreviewUvChannel = 1 };
            var first = new MeshEntry { originalMesh = source, lodIndex = 0, meshGroupKey = "Part" };
            var second = new MeshEntry { originalMesh = target, lodIndex = 1, meshGroupKey = "Part" };
            ctx.MeshEntries.Add(first); ctx.MeshEntries.Add(second);
            var data = UvTopology.BuildShellData(Uv, target.triangles);
            try {
                bool ready = false; Dictionary<int, int> keys = null;
                double deadline = EditorApplication.timeSinceStartup + 10;
                while (!ready && EditorApplication.timeSinceStartup < deadline) { keys = colors.Get(ctx, target, data, second, out ready); yield return null; }
                Assert.IsTrue(ready); Assert.AreEqual(1, keys[0]); Assert.AreEqual(0, keys[1]);
                var revision = colors.Revision(second, 1);
                Assert.AreSame(keys, colors.Get(ctx, target, data, second, out ready));
                Assert.AreEqual(revision, colors.Revision(second, 1));
                second.shellTransferResult = new GroupedShellTransfer.TransferResult { vertexToSourceShell = new[] { 1, 1, 1, 0, 0, 0 } };
                colors.Get(ctx, target, data, second, out ready); Assert.IsFalse(ready);
                while (!ready && EditorApplication.timeSinceStartup < deadline) { keys = colors.Get(ctx, target, data, second, out ready); yield return null; }
                Assert.IsTrue(ready); Assert.AreEqual(0, keys[0]); Assert.AreEqual(1, keys[1]);
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(target); }
        }

        [UnityTest]
        public IEnumerator CanvasKeepsCorrespondenceAcrossLodFrameClearsAndDropsStaleUvTopology()
        {
            var source = new Mesh { name = "Part_LOD0", vertices = Positions, uv = Uv, triangles = Triangles };
            var target = new Mesh { name = "Part_LOD1", vertices = Positions, uv = Uv, uv2 = Uv, triangles = new[] { 3, 4, 5, 0, 1, 2 } };
            var ctx = new UvToolContext { PreviewUvChannel = 1 };
            var entry = new MeshEntry { originalMesh = target, lodIndex = 1, meshGroupKey = "Part",
                shellTransferResult = new GroupedShellTransfer.TransferResult { vertexToSourceShell = new[] { 1, 1, 1 } } };
            ctx.MeshEntries.Add(new MeshEntry { originalMesh = source, lodIndex = 0, meshGroupKey = "Part", include = false });
            ctx.MeshEntries.Add(entry);
            var canvas = new UvCanvasView(); canvas.Init();
            try {
                int[] keys = null; double deadline = EditorApplication.timeSinceStartup + 10;
                while (keys == null && EditorApplication.timeSinceStartup < deadline) { canvas.PollPreviewJobs(); keys = canvas.FaceColorKeys(ctx, target, entry); yield return null; }
                Assert.IsNotNull(keys); Assert.AreEqual(1, keys[0]); Assert.AreEqual(0, keys[1]);
                int revision = canvas.ShellColorRevision(entry, 1);
                canvas.ClearFrameCaches();
                Assert.IsNotNull(canvas.FaceColorKeys(ctx, target, entry), "switching the displayed LOD must retain completed correspondence");
                Assert.AreEqual(revision, canvas.ShellColorRevision(entry, 1));
                var previous = canvas.GetPreviewShellCache(ctx, target, 1);
                VertexChannels.SetUvs(target, 1, new[] { Vector2.zero, Vector2.right * .5f, Vector2.up * .5f, Vector2.one, Vector2.right + Vector2.one, Vector2.up + Vector2.one });
                Assert.IsNull(canvas.GetPreviewShellCache(ctx, target, 1), "UV writes must not reuse stale shell/BVH data");
                PreviewShellData next = null;
                while (next == null && EditorApplication.timeSinceStartup < deadline) { canvas.PollPreviewJobs(); next = canvas.GetPreviewShellCache(ctx, target, 1); yield return null; }
                Assert.IsNotNull(next); Assert.AreNotSame(previous, next);
            }
            finally { canvas.Cleanup(); Object.DestroyImmediate(source); Object.DestroyImmediate(target); }
        }

        [UnityTest]
        public IEnumerator DenseDrawPreparationRunsOnAWorkerAndInvalidationReleasesItsMesh()
        {
            using var cache = new UvPreviewGeometry();
            var uv = new[] { Vector2.zero, Vector2.right, Vector2.up }; var source = new object();
            int main = Thread.CurrentThread.ManagedThreadId, worker = main, builds = 0;
            void Build(UvPreviewGeometry.Builder batch) { worker = Thread.CurrentThread.ManagedThreadId; ++builds; foreach (var p in uv) batch.Vertex(p); }
            Mesh mesh = null; double deadline = EditorApplication.timeSinceStartup + 10;
            Assert.IsNull(cache.Get(uv, source, "fill", 0, MeshTopology.Triangles, Build, true));
            while (!mesh && EditorApplication.timeSinceStartup < deadline) { yield return null; mesh = cache.Get(uv, source, "fill", 0, MeshTopology.Triangles, Build, true); }
            Assert.IsTrue(mesh); Assert.AreNotEqual(main, worker); Assert.AreEqual(1, builds);
            Assert.Greater(cache.Revision(uv), 0);
            cache.InvalidateUv(uv); Assert.IsFalse(mesh);
        }

        [UnityTest]
        public IEnumerator DenseAsyncFillInvalidatesThe3DLayerWhenItsGpuBufferBecomesReady()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires graphics.");
            const int side = 48;
            var vertices = new Vector3[(side + 1) * (side + 1)]; var uv = new Vector2[vertices.Length]; var triangles = new int[side * side * 6];
            for (int y = 0; y <= side; ++y)
                for (int x = 0; x <= side; ++x) { int v = y * (side + 1) + x; uv[v] = new Vector2((float)x / side, (float)y / side); vertices[v] = uv[v]; }
            for (int y = 0, t = 0; y < side; ++y)
                for (int x = 0; x < side; ++x) {
                    int v = y * (side + 1) + x;
                    triangles[t++] = v; triangles[t++] = v + 1; triangles[t++] = v + side + 1;
                    triangles[t++] = v + 1; triangles[t++] = v + side + 2; triangles[t++] = v + side + 1;
                }
            var mesh = new Mesh { vertices = vertices, uv = uv, triangles = triangles };
            var statuses = new TriangleStatus[triangles.Length / 3]; for (int i = 0; i < statuses.Length; ++i) statuses[i] = TriangleStatus.Accepted;
            var canvas = new UvCanvasView { ShowWireframe = false, ShowBorder = false }; canvas.Init();
            canvas.SetFillModes(new List<UvCanvasView.FillModeEntry> { new UvCanvasView.FillModeEntry { name = "Status",
                drawCallback = (cv, x, y, size, m, e) => cv.GlFillSt(x, y, size, uv, triangles, statuses.Length, uv.Length, statuses) } });
            var ctx = new UvToolContext { PreviewUvChannel = 0 }; var entry = new MeshEntry { originalMesh = mesh };
            var items = new[] { new MeshViewport3D.Item(mesh, Matrix4x4.identity) }; var entries = new[] { entry };
            using var view = new MeshViewport3D(); using var layer = new UvLayer3D { LayerSize = 32 };
            var pixels = new Texture2D(32, 32, TextureFormat.RGBA32, false); var previous = RenderTexture.active;
            try {
                Color color = Color.clear; double deadline = EditorApplication.timeSinceStartup + 10;
                while (color.g - color.r < .1f && EditorApplication.timeSinceStartup < deadline) {
                    layer.Draw(view, canvas, ctx, items, entries);
                    var layers = (IDictionary)typeof(UvLayer3D).GetField("layers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(layer);
                    foreach (DictionaryEntry item in layers) {
                        var texture = (RenderTexture)item.Value.GetType().GetField("texture").GetValue(item.Value);
                        RenderTexture.active = texture; pixels.ReadPixels(new Rect(0, 0, 32, 32), 0, 0); pixels.Apply(); color = pixels.GetPixel(8, 8);
                    }
                    yield return null;
                }
                Assert.Greater(color.g - color.r, .1f, "a cached 3D layer must not retain the blank frame rendered while its fill was pending");
            }
            finally { RenderTexture.active = previous; canvas.Cleanup(); Object.DestroyImmediate(pixels); Object.DestroyImmediate(mesh); }
        }

        [UnityTest]
        public IEnumerator ShellHighlightsUploadOnlyUsedVerticesAndReplaceThePreviousHover()
        {
            var mesh = new Mesh { vertices = Positions, uv = Uv, triangles = Triangles };
            var ctx = new UvToolContext { PreviewUvChannel = 0 }; var canvas = new UvCanvasView();
            var data = UvTopology.BuildShellData(Uv, Triangles); data.positions = Positions;
            ctx.PreviewShellDataCache[(long)mesh.GetInstanceID() << 8] = data;
            using var layer = new UvLayer3D();
            try {
                Mesh first = null; double deadline = EditorApplication.timeSinceStartup + 10;
                while (!first && EditorApplication.timeSinceStartup < deadline) { first = layer.ShellMesh(canvas, ctx, mesh, 0, false, null); yield return null; }
                Assert.IsTrue(first); Assert.AreEqual(3, first.vertexCount); Assert.AreEqual(3, first.GetIndexCount(0));
                Assert.IsNull(layer.ShellMesh(canvas, ctx, mesh, 1, false, null)); Assert.IsFalse(first);
                Mesh next = null;
                while (!next && EditorApplication.timeSinceStartup < deadline) { next = layer.ShellMesh(canvas, ctx, mesh, 1, false, null); yield return null; }
                Assert.IsTrue(next); Assert.AreEqual(3, next.vertexCount);
                layer.Invalidate(); Assert.IsFalse(next);
            }
            finally { canvas.Cleanup(); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void UvPickingFindsTheActualFaceBeyondTheOldScanBudgetAndRejectsHoles()
        {
            const int count = 7001;
            var uv = new Vector2[count * 3]; var triangles = new int[uv.Length]; var positions = new Vector3[uv.Length];
            for (int f = 0; f < count; ++f) {
                float x = f == count - 1 ? .8f : 0;
                uv[f * 3] = new Vector2(x, 0); uv[f * 3 + 1] = new Vector2(x + .1f, 0); uv[f * 3 + 2] = new Vector2(x, .1f);
                for (int c = 0; c < 3; ++c) triangles[f * 3 + c] = f * 3 + c;
            }
            var mesh = new Mesh { vertices = positions, uv = uv, triangles = triangles };
            var entry = new MeshEntry { originalMesh = mesh };
            var canvas = new UvCanvasView { EntriesOverride = new List<MeshEntry> { entry } }; var ctx = new UvToolContext { PreviewUvChannel = 0 };
            try {
                ctx.PreviewShellDataCache[(long)mesh.GetInstanceID() << 8] = UvTopology.BuildShellData(uv, triangles);
                ShellUvHit hit = default;
                Assert.IsTrue(canvas.TryPickUvHit(ctx, new Vector2(.82f, .02f), ref hit)); Assert.AreEqual(7000, hit.faceIndex);
                Assert.IsFalse(canvas.TryPickUvHit(ctx, new Vector2(.89f, .09f), ref hit), "empty space inside a shell's box is not a face");
            }
            finally { canvas.Cleanup(); Object.DestroyImmediate(mesh); }
        }

        [UnityTest]
        public IEnumerator PointBuffersAreCameraIndependentAndReleaseWithTheirSource()
        {
            using var cache = new PreviewPoints();
            var source = new Mesh { vertices = new[] { Vector3.zero, Vector3.one } };
            try {
                Mesh mesh = null; double deadline = EditorApplication.timeSinceStartup + 10;
                while (!mesh && EditorApplication.timeSinceStartup < deadline) { mesh = cache.Get(source); yield return null; }
                Assert.IsTrue(mesh); Assert.AreEqual(8, mesh.vertexCount); Assert.AreSame(mesh, cache.Get(source));
                Object.DestroyImmediate(source); cache.Prune(); Assert.IsFalse(mesh);
            }
            finally { if (source) Object.DestroyImmediate(source); }
        }

        [UnityTest]
        public IEnumerator PointPreparationAcceptsASourceDestroyedBeforeTheSnapshot()
        {
            using var cache = new PreviewPoints();
            var source = new Mesh { vertices = new[] { Vector3.zero } };
            cache.Get(source); Object.DestroyImmediate(source);
            yield return null; yield return null; yield return null;
            cache.Prune();
            Assert.IsNull(cache.Get(source));
        }

        [Test]
        public void ShellFillKeepsOverlappingUvFacesIndependentAndSplitsColorSeams()
        {
            var data = new PreviewShellData {
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
                shells = new List<UvShell> {
                    new UvShell { shellId = 7, faceIndices = new List<int> { 0 } },
                    new UvShell { shellId = 9, faceIndices = new List<int> { 1 } }
                }
            };
            var positions = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up };
            var keys = new Dictionary<int, int> { [7] = 0, [9] = 1 };
            var fill = UvLayer3D.BuildFillData(data, positions, keys, -1, false);
            Assert.AreEqual(6, fill.positions.Count, "shared corners split only where face colours differ");
            Assert.AreEqual(UvCanvasView.pal[0], fill.colors[fill.indices[0]]);
            Assert.AreEqual(UvCanvasView.pal[1], fill.colors[fill.indices[3]]);
            Assert.AreEqual(positions[0], fill.positions[fill.indices[0]]);
            Assert.AreEqual(positions[0], fill.positions[fill.indices[3]]);
            keys[9] = 0;
            Assert.AreEqual(4, UvLayer3D.BuildFillData(data, positions, keys, -1, false).positions.Count,
                "corresponding source-shell keys reuse the same palette colour and corners");
        }

        [Test]
        public void ShellSeamsReconnectUvSplitCornersButExcludeCreasesAndNonManifoldEdges()
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.one-Vector3.forward, Vector3.zero,
                Vector3.one-Vector3.forward, Vector3.up, new Vector3(.5f,-.5f,0) };
            var triangles = new[] {0,1,2,3,4,5};
            var seam = ShellFillSeams.Build(triangles,p,new[] {0,1},default);
            Assert.AreEqual(1,seam.neighbors[0].y); Assert.AreEqual(0,seam.neighbors[1].z);
            var duplicate = ShellFillSeams.Build(new[] {0,1,2,0,1,2},p,new[] {0,1},default);
            Assert.AreEqual(new Vector3(-1,-1,-1),duplicate.neighbors[0], "coincident overlapping faces are not opposite sides of a seam");
            p[5] = Vector3.forward;
            var crease = ShellFillSeams.Build(triangles,p,new[] {0,1},default);
            Assert.AreEqual(new Vector3(-1,-1,-1),crease.neighbors[0]);
            p[5] = Vector3.up;
            var nonManifold = ShellFillSeams.Build(new[] {0,1,2,3,4,5,0,2,6},p,new[] {0,1,2},default);
            Assert.AreEqual(new Vector3(-1,-1,-1),nonManifold.neighbors[0]);
            Assert.AreEqual(new Vector3(-1,-1,-1),nonManifold.neighbors[1]);
        }

        [TestCase(.4f)]
        [TestCase(1f)]
        public void ShellBoundaryCoverageMixesAdjacentColorsWithoutAffectingInterior(float opacity)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires graphics.");
            var p = new[] { Vector3.zero,Vector3.right,new Vector3(1,1,0),Vector3.up };
            var data = new PreviewShellData { triangles = new[] {0,1,2,0,2,3}, shells = new List<UvShell> {
                new UvShell {shellId=0,faceIndices=new List<int>{0}},new UvShell {shellId=1,faceIndices=new List<int>{1}} } };
            var mesh = UvLayer3D.BuildFillData(data,p,null,-1,false).Upload();
            var mat = new Material(Shader.Find("Hidden/MeshLab/ViewportOverlay")); MeshViewport3D.BindShellFillPalette(mat);
            mat.SetFloat("_ShellFill",1); mat.SetFloat("_ShellAA",1); mat.SetFloat("_ShellOpacity",opacity);
            mat.SetFloat("_UseVertexColor",1); mat.SetFloat("_ZTest",(float)CompareFunction.Always); mat.SetColor("_Color",Color.white);
            var target = new RenderTexture(32,32,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(32,32,TextureFormat.RGBA32,false,true);
            var previous = RenderTexture.active; bool srgb = GL.sRGBWrite; var camera = Shader.GetGlobalVector("_WorldSpaceCameraPos");
            try {
                target.Create(); RenderTexture.active=target; GL.sRGBWrite=false; GL.Clear(false,true,Color.white);
                GL.PushMatrix();
                try { GL.LoadOrtho(); GL.modelview=Matrix4x4.identity; Shader.SetGlobalVector("_WorldSpaceCameraPos",new Vector4(.5f,.5f,10,1)); mat.SetPass(0); Graphics.DrawMeshNow(mesh,Matrix4x4.identity); }
                finally {GL.PopMatrix();}
                pixels.ReadPixels(new Rect(0,0,32,32),0,0); pixels.Apply();
                var actual=pixels.GetPixel(16,16); var expected=Color.Lerp(Color.white,Color.Lerp(UvCanvasView.pal[0],UvCanvasView.pal[1],.5f),opacity);
                Assert.That(actual.r,Is.EqualTo(expected.r).Within(.02f));
                Assert.That(actual.g,Is.EqualTo(expected.g).Within(.02f));
                Assert.That(actual.b,Is.EqualTo(expected.b).Within(.02f));
                var interior=pixels.GetPixel(24,8); var baseColor=Color.Lerp(Color.white,UvCanvasView.pal[0],opacity);
                Assert.That(interior.r,Is.EqualTo(baseColor.r).Within(.015f));
                Assert.That(interior.b,Is.EqualTo(baseColor.b).Within(.015f));
            }
            finally {
                Shader.SetGlobalVector("_WorldSpaceCameraPos",camera); GL.sRGBWrite=srgb; RenderTexture.active=previous;
                target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(pixels); Object.DestroyImmediate(mat); Object.DestroyImmediate(mesh);
            }
        }

        [UnityTest]
        public IEnumerator ShellFillReusesBuffersAcrossOpacityAndReleasesOnInvalidation()
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up }, triangles = new[] { 0, 1, 2 } };
            var original = TransferMeshSnapshot.Capture(mesh);
            var context = new UvToolContext { PreviewUvChannel = 0 };
            var entry = new MeshEntry { originalMesh = mesh }; context.MeshEntries.Add(entry);
            var canvas = new UvCanvasView(); canvas.Init();
            using var layer = new UvLayer3D();
            try {
                Mesh fill = null;
                for (int i = 0; i < 300 && !fill; ++i) { fill = layer.FillMesh(canvas, context, mesh, entry, null); yield return null; }
                Assert.IsNotNull(fill);
                for (int i = 0; i < 120; ++i) {
                    canvas.FillAlpha = i / 120f; canvas.Pan = new Vector2(i, -i); canvas.Zoom = i + 1;
                    Assert.AreSame(fill, layer.FillMesh(canvas, context, mesh, entry, null));
                }
                CollectionAssert.AreEqual(original, TransferMeshSnapshot.Capture(mesh));
                layer.Invalidate(); Assert.IsFalse(fill);
            }
            finally { canvas.Cleanup(); Object.DestroyImmediate(mesh); }
        }

        [TestCase(.25f, false)]
        [TestCase(.4f, false)]
        [TestCase(.8f, false)]
        [TestCase(.25f, true)]
        [TestCase(.4f, true)]
        [TestCase(.8f, true)]
        public void TransparentShellLayerCompositesOnceWithTheRequestedOpacity(float alpha, bool ownedAntialiasedTarget)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires graphics.");
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up }, triangles = new[] { 0, 1, 2 } };
            var ctx = new UvToolContext { PreviewUvChannel = 0 }; var entry = new MeshEntry { originalMesh = mesh };
            var canvas = new UvCanvasView { FillAlpha = alpha }; canvas.Init();
            var overlay = new Material(Shader.Find("Hidden/MeshLab/UvOverlay")); overlay.SetFloat("_Premultiplied", 1);
            RenderTexture layer = null;
            var readback = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var composite = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(32, 32, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            try {
                ctx.PreviewShellDataCache[(long)mesh.GetInstanceID() << 8] = UvTopology.BuildShellData(mesh.uv, mesh.triangles);
                canvas.SetFillModes(new List<UvCanvasView.FillModeEntry> { new UvCanvasView.FillModeEntry {
                    name = "Shells", drawCallback = (c,x,y,s,m,e) => c.GlFillSh(ctx,x,y,s,m,1,3,e,-1,-1) } });
                if (!ownedAntialiasedTarget) { layer = new RenderTexture(32,32,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear); layer.Create(); }
                composite.Create(); readback.Create(); layer = canvas.RenderUvLayer(ctx, mesh, entry, layer, 32, false);
                Graphics.Blit(layer,readback);
                RenderTexture.active = readback; pixels.ReadPixels(new Rect(0,0,32,32),0,0); pixels.Apply();
                var stored = pixels.GetPixel(8,8);
                Assert.That(stored.a, Is.EqualTo(alpha).Within(.01f));
                Assert.That(stored.b, Is.EqualTo(alpha).Within(.01f), "UV target carries premultiplied colour");
                if (ownedAntialiasedTarget && layer.antiAliasing > 1)
                    Assert.IsTrue(Array.Exists(pixels.GetPixels32(), value => value.a > 0 && value.a < alpha * 255 - 2), "AA creates fractional edge coverage");
                RenderTexture.active = composite; GL.Clear(false, true, Color.white);
                Graphics.Blit(layer, composite, overlay);
                RenderTexture.active = composite; pixels.ReadPixels(new Rect(0,0,32,32),0,0); pixels.Apply();
                var actual = pixels.GetPixel(8,8); var expected = Color.Lerp(Color.white, UvCanvasView.pal[0], alpha);
                Assert.That(actual.r, Is.EqualTo(expected.r).Within(.015f));
                Assert.That(actual.g, Is.EqualTo(expected.g).Within(.015f));
                Assert.That(actual.b, Is.EqualTo(expected.b).Within(.015f));
                Assert.That(actual.a, Is.EqualTo(1).Within(.01f));
            }
            finally {
                RenderTexture.active = previous; canvas.Cleanup(); if (layer) { layer.Release(); Object.DestroyImmediate(layer); }
                composite.Release(); readback.Release(); Object.DestroyImmediate(readback);
                Object.DestroyImmediate(composite); Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(overlay); Object.DestroyImmediate(mesh);
            }
        }

        [TestCase(.4f, .7f, TransferValidator.TriIssue.Overlap, TransferValidator.TriIssue.None)]
        [TestCase(0f, 1f, TransferValidator.TriIssue.Overlap, TransferValidator.TriIssue.None)]
        [TestCase(.8f, .5f, TransferValidator.TriIssue.ZeroArea, TransferValidator.TriIssue.Overlap)]
        [TestCase(.25f, 1f, TransferValidator.TriIssue.None, TransferValidator.TriIssue.None)]
        public void GeometryShellFillCompositesValidationAndSurfaceOpacity(float opacity, float surfaceOpacity,
            TransferValidator.TriIssue flags, TransferValidator.TriIssue mask)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires graphics.");
            var positions = new[] { Vector3.zero, Vector3.right, Vector3.up };
            var data = new PreviewShellData { triangles = new[] { 0,1,2 },
                shells = new List<UvShell> { new UvShell { shellId = 0, faceIndices = new List<int> { 0 } } } };
            var mesh = UvLayer3D.BuildFillData(data, positions, null, -1, false, issues: new[] { flags }, mask: mask).Upload();
            var material = new Material(Shader.Find("Hidden/MeshLab/ViewportOverlay"));
            material.SetFloat("_ShellFill",1); material.SetFloat("_ShellOpacity",opacity); material.SetFloat("_UseVertexColor",1);
            material.SetFloat("_ZTest",(float)CompareFunction.Always); material.SetColor("_Color",new Color(1,1,1,surfaceOpacity));
            var target = new RenderTexture(32,32,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(32,32,TextureFormat.RGBA32,false,true);
            var previous = RenderTexture.active; bool previousSrgb = GL.sRGBWrite;
            try {
                target.Create(); RenderTexture.active = target; GL.sRGBWrite = false; GL.Clear(false,true,Color.white);
                GL.PushMatrix();
                try { GL.LoadOrtho(); GL.modelview = Matrix4x4.identity; material.SetPass(0); Graphics.DrawMeshNow(mesh,Matrix4x4.identity); }
                finally { GL.PopMatrix(); }
                pixels.ReadPixels(new Rect(0,0,32,32),0,0); pixels.Apply();
                var actual = pixels.GetPixel(8,8); var issue = UvCanvasView.ValidationOverlayColor(flags,mask);
                float alpha = issue.a + opacity * (1-issue.a);
                var premultiplied = issue * issue.a + UvCanvasView.pal[0] * (opacity * (1-issue.a));
                var expected = Color.white * (1-alpha*surfaceOpacity) + premultiplied * surfaceOpacity;
                Assert.That(actual.r, Is.EqualTo(expected.r).Within(.015f));
                Assert.That(actual.g, Is.EqualTo(expected.g).Within(.015f));
                Assert.That(actual.b, Is.EqualTo(expected.b).Within(.015f));
            }
            finally {
                GL.sRGBWrite = previousSrgb; RenderTexture.active = previous;
                target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(material); Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void ShellOpacityChangesMatchLegacyPixelsWithoutReplacingTheGeometry()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires graphics.");
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up }, triangles = new[] { 0, 1, 2 } };
            var ctx = new UvToolContext { PreviewUvChannel = 0 }; var canvas = new UvCanvasView(); canvas.Init();
            var entry = new MeshEntry { originalMesh = mesh };
            ctx.PreviewShellDataCache[(long)mesh.GetInstanceID() << 8] = UvTopology.BuildShellData(mesh.uv, mesh.triangles);
            var target = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(32, 32, TextureFormat.RGBA32, false, true);
            var legacy = new Material(Shader.Find("Hidden/Internal-Colored"));
            legacy.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); legacy.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            legacy.SetInt("_Cull", 0); legacy.SetInt("_ZWrite", 0);
            var previous = RenderTexture.active;
            try {
                target.Create();
                Sample(false, .25f);
                var ids = BatchIds();
                foreach (float alpha in new[] { .1f, .25f, .8f, 1f }) {
                    var expected = Sample(true, alpha); var actual = Sample(false, alpha);
                    Assert.That(actual.r, Is.EqualTo(expected.r).Within(.01f));
                    Assert.That(actual.g, Is.EqualTo(expected.g).Within(.01f));
                    Assert.That(actual.b, Is.EqualTo(expected.b).Within(.01f));
                    CollectionAssert.AreEquivalent(ids, BatchIds(), "opacity is a GPU parameter, not a geometry revision");
                }
            }
            finally { RenderTexture.active = previous; canvas.Cleanup(); target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(pixels); Object.DestroyImmediate(legacy); Object.DestroyImmediate(mesh); }

            Color Sample(bool immediate, float alpha)
            {
                RenderTexture.active = target; GL.Clear(false, true, Color.black); GL.PushMatrix();
                try {
                    GL.LoadPixelMatrix(0, 32, 32, 0);
                    if (immediate) {
                        legacy.SetPass(0); var color = Color.Lerp(UvCanvasView.pal[0], Color.white, .45f); color.a = Mathf.Clamp01(alpha * 1.85f);
                        GL.Begin(GL.TRIANGLES); GL.Color(color);
                        foreach (var uv in mesh.uv) UvCanvasView.Vx(0, 0, 32, uv);
                        GL.End();
                    }
                    else { canvas.FillAlpha = alpha; canvas.GlFillSh(ctx, 0, 0, 32, mesh, 1, 3, entry, -1, 0); }
                }
                finally { GL.PopMatrix(); }
                pixels.ReadPixels(new Rect(0, 0, 32, 32), 0, 0); pixels.Apply(); return pixels.GetPixel(8, 8);
            }
            int[] BatchIds()
            {
                var ids = new List<int>();
                foreach (var item in Resources.FindObjectsOfTypeAll<Mesh>()) if (item.name == "MeshLab UV batch") ids.Add(item.GetInstanceID());
                return ids.ToArray();
            }
        }

        [Test]
        public void CheckerBatchMatchesImmediatePixelsAcrossTilesMotionAndOpacity()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires graphics.");
            var canvas = new UvCanvasView(); canvas.Init();
            var target = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(64, 64, TextureFormat.RGBA32, false, true);
            var tiles = new HashSet<Vector2Int> { Vector2Int.zero, Vector2Int.right, Vector2Int.down };
            var previous = RenderTexture.active;
            try {
                target.Create();
                foreach (bool neutral in new[] { false, true })
                    foreach (float alpha in new[] { .25f, .8f }) {
                        var expected = Sample(true, alpha, neutral); var actual = Sample(false, alpha, neutral);
                        for (int i = 0; i < expected.Length; ++i)
                            Assert.That(actual[i].r, Is.EqualTo(expected[i].r).Within(.01f), "checker coverage and blending");
                    }
            }
            finally { RenderTexture.active = previous; canvas.Cleanup(); target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(pixels); }

            Color[] Sample(bool immediate, float alpha, bool neutral)
            {
                RenderTexture.active = target; GL.Clear(false, true, Color.black); GL.PushMatrix();
                try {
                    GL.LoadPixelMatrix(0, 64, 64, 0); canvas.GlMat.SetPass(0);
                    if (!immediate) canvas.GlCheckerBg(3, 5, 24, 8, alpha, neutral, tiles);
                    else {
                        GL.Begin(GL.QUADS);
                        foreach (var tile in tiles)
                            for (int y = 0; y < 8; ++y)
                                for (int x = 0; x < 8; ++x) {
                                    float c = ((x + y) & 1) == 0 ? (neutral ? .24f : .20f) : (neutral ? .32f : .38f);
                                    GL.Color(new Color(c, c, c, alpha));
                                    float px = 3 + tile.x * 24 + x * 3, py = 5 - tile.y * 24 + y * 3;
                                    GL.Vertex3(px, py, 0); GL.Vertex3(px + 3, py, 0); GL.Vertex3(px + 3, py + 3, 0); GL.Vertex3(px, py + 3, 0);
                                }
                        GL.End();
                    }
                }
                finally { GL.PopMatrix(); }
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); pixels.Apply(); return pixels.GetPixels();
            }
        }

        [UnityTest]
        public IEnumerator PointShaderKeepsPixelSizeDuringOrbitAndProjectionChanges()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires graphics.");
            var pipeline = GraphicsSettings.defaultRenderPipeline; var quality = QualitySettings.renderPipeline;
            GraphicsSettings.defaultRenderPipeline = null; QualitySettings.renderPipeline = null;
            var cameraObject = new GameObject("Point test camera"); var owner = new GameObject("Point test object") { layer = 31 };
            var mesh = PreviewPoints.Build(new[] { new Vector3(0, 0, 2) }).Upload();
            var material = new Material(Shader.Find("Hidden/MeshLab/PreviewPoints"));
            material.SetVector("_ViewportSize", new Vector4(128, 128, 0, 0)); material.SetFloat("_PointSize", 6); material.SetColor("_Color", Color.white);
            owner.AddComponent<MeshFilter>().sharedMesh = mesh; owner.AddComponent<MeshRenderer>().sharedMaterial = material;
            var target = new RenderTexture(128, 128, 24); var pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false; camera.cullingMask = 1 << 31;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.targetTexture = target;
            var previous = RenderTexture.active;
            try {
                target.Create(); yield return null;
                foreach (bool orthographic in new[] { false, true }) {
                    camera.orthographic = orthographic; camera.transform.position = new Vector3(1, .7f, 0); camera.transform.LookAt(new Vector3(0, 0, 2));
                    camera.Render(); RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); pixels.Apply();
                    int lit = 0; foreach (var pixel in pixels.GetPixels()) if (pixel.r > .5f) ++lit;
                    Assert.That(lit, Is.InRange(25, 49), "the same static quad stays six pixels wide under both projections");
                }
            }
            finally {
                RenderTexture.active = previous; Object.DestroyImmediate(owner); Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(material); target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(pixels);
                GraphicsSettings.defaultRenderPipeline = pipeline; QualitySettings.renderPipeline = quality;
            }
            yield return null;
        }
    }
}
