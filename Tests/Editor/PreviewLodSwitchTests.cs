// PreviewLodSwitchTests.cs — the 3D preview follows the preview LOD switched from the
// canvas toolbar: the checker moves to the renderers now shown, the previous LOD gets
// its materials back, and the UV channel the user picked returns when the LOD has it.
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    internal sealed class ViewportSpotInputWindow : UnityEditor.EditorWindow
    {
        internal System.Action<Event> Input;
        void OnEnable() => wantsMouseMove = true;
        void OnGUI() => Input?.Invoke(Event.current);
    }

    public class PreviewLodSwitchTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        readonly List<Object> owned = new List<Object>();
        UvToolHub hub;
        GameObject previousSelection;

        [SetUp]
        public void RememberSelection() => previousSelection = UnityEditor.Selection.activeGameObject;

        [TearDown]
        public void Cleanup()
        {
            // Closing the hub restores an active preview; the materials go after it.
            if (hub != null) Object.DestroyImmediate(hub);
            hub = null;
            UnityEditor.Selection.activeGameObject = previousSelection;
            foreach (var item in owned) if (item) Object.DestroyImmediate(item);
            owned.Clear();
        }

        Mesh Quad(string name, bool withUv1)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            if (withUv1) mesh.uv2 = new[] { Vector2.zero, Vector2.right * .5f, Vector2.one * .5f, Vector2.up * .5f };
            mesh.RecalculateNormals();
            owned.Add(mesh);
            return mesh;
        }

        MeshRenderer Lod(Transform root, string name, Mesh mesh)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            var material = new Material(Shader.Find("Hidden/InternalErrorShader")) { name = name + "_Mat" };
            owned.Add(material);
            renderer.sharedMaterial = material;
            return renderer;
        }

        LODGroup Group(bool lod1HasUv1, out MeshRenderer lod0, out MeshRenderer lod1)
        {
            var root = new GameObject("Prop");
            owned.Add(root);
            lod0 = Lod(root.transform, "Prop_LOD0", Quad("Prop_LOD0", true));
            lod1 = Lod(root.transform, "Prop_LOD1", Quad("Prop_LOD1", lod1HasUv1));
            var group = root.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(.5f, new Renderer[] { lod0 }), new LOD(.1f, new Renderer[] { lod1 }) });
            return group;
        }

        static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Private).GetValue(target);
        static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private).Invoke(target, args);
        static Bounds PreviewBounds(IReadOnlyList<MeshViewport3D.Item> items) => (Bounds)typeof(MeshViewport3D)
            .GetMethod("BoundsOf", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { items, false });
        static bool ShowsChecker(Renderer renderer) => CheckerTexturePreview.IsPreviewShader(renderer.sharedMaterial.shader.name);

        static ViewportSpotInputWindow OpenSpotInputWindow()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("IMGUI mouse input requires a graphics device; run without -nographics.");
            var window = ScriptableObject.CreateInstance<ViewportSpotInputWindow>();
            window.Show();
            return window;
        }

        static void ChooseCameraFrame(MeshViewport3D viewport)
        {
            Set(viewport, "pivot", new Vector3(3, 4, 5));
            Set(viewport, "orbit", new Vector2(25, -15));
            Set(viewport, "distance", 2f);
            Set(viewport, "radius", .75f);
        }

        static void AssertCameraFrame(MeshViewport3D viewport)
        {
            Assert.That(Get<Vector3>(viewport, "pivot"), Is.EqualTo(new Vector3(3, 4, 5)));
            Assert.That(Get<Vector2>(viewport, "orbit"), Is.EqualTo(new Vector2(25, -15)));
            Assert.That(Get<float>(viewport, "distance"), Is.EqualTo(2f));
        }

        // The hub on the group with the transfer tool active and UV1 picked, after one
        // frame's entry collection — the state the LOD row is clicked from.
        UvToolContext Open(LODGroup group)
        {
            hub = ScriptableObject.CreateInstance<UvToolHub>();
            Call(hub, "SelectToolById", "uv2_transfer");
            var ctx = Get<UvToolContext>(hub, "ctx");
            ctx.Refresh(group);
            Call(hub, "OnPreviewChannelChanged", 1);
            Call(hub, "CollectCanvasEntries");
            return ctx;
        }

        // The LOD row click, then the frame after it.
        void SwitchLod(int lod)
        {
            Call(hub, "SetPreviewLod", lod);
            Call(hub, "CollectCanvasEntries");
        }

        MeshRenderer Standalone(string name, bool withUv1 = true)
        {
            var renderer = Lod(null, name, Quad(name, withUv1));
            owned.Add(renderer.gameObject);
            return renderer;
        }

        void SelectModel(GameObject model)
        {
            UnityEditor.Selection.activeGameObject = model;
            Call(hub, "OnSelectionChange");
            Call(hub, "CollectCanvasEntries");
        }

        [TestCase(UvCanvasView.PreviewMode.Checker)]
        [TestCase(UvCanvasView.PreviewMode.Shells3D)]
        [TestCase(UvCanvasView.PreviewMode.Lightmap)]
        public void SourceTextureMetricReadsAuthoredMaterialsWithoutChangingPreview(UvCanvasView.PreviewMode mode)
        {
            var group = Group(true, out var renderer, out _);
            var texture = new Texture2D(8, 16) { name = "Rectangular authored albedo" }; owned.Add(texture);
            var authored = new Material(Shader.Find("Standard")) { mainTexture = texture, mainTextureScale = new Vector2(1, 2) };
            owned.Add(authored);
            var second = new Material(authored); owned.Add(second);
            renderer.sharedMaterials = new[] { authored, second };
            var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            var uv0 = mesh.uv;
            var expected = SourceTextureUvMetric.Resolve(renderer, mesh: mesh);
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                Open(group);
                Call(hub, "ApplyPreviewMode", mode);
                var previewMaterials = renderer.sharedMaterials;
                Assert.AreNotSame(authored, previewMaterials[0]);
                var metric = SourceTextureUvMetric.Resolve(renderer, mesh: mesh);
                Assert.AreEqual(expected.uvScale, metric.uvScale);
                Assert.AreEqual(2, metric.textures.Count, "Retain every authored material slot.");
                foreach (var info in metric.textures) {
                    Assert.AreEqual(texture.name, info.texture);
                    Assert.AreEqual(new Vector2(1, 2), info.tiling);
                    Assert.AreEqual(.25f, info.aspect);
                }
                CollectionAssert.AreEqual(previewMaterials, renderer.sharedMaterials);
                Assert.AreEqual(mode, Get<UvCanvasView>(hub, "canvas").CurrentPreviewMode);
                CollectionAssert.AreEqual(uv0, mesh.uv);
                Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
                CollectionAssert.AreEqual(new[] { authored, second }, renderer.sharedMaterials);
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void CheckerMovesToTheNextModelAndRestoresAllPreviousMaterialSlots(bool fromStandalone, bool toStandalone)
        {
            var group = Group(true, out var oldRenderer, out var oldHidden);
            var context = Open(group);
            if (fromStandalone) {
                oldRenderer = Standalone("First standalone");
                SelectModel(oldRenderer.gameObject);
            }
            var canvas = Get<UvCanvasView>(hub, "canvas");
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            var firstMaterial = oldRenderer.sharedMaterial;
            var secondMaterial = new Material(firstMaterial); owned.Add(secondMaterial);
            var originals = new[] { firstMaterial, secondMaterial };
            oldRenderer.sharedMaterials = originals;
            var originalMesh = oldRenderer.GetComponent<MeshFilter>().sharedMesh;
            var working = Quad("Temporary repacked first model", true);
            context.MeshEntries.Find(entry => entry.renderer == oldRenderer).repackedMesh = working;
            Set(hub, "_checkerColorMode", true); Set(hub, "_checkerShowR", false);
            Call(hub, "CollectCanvasEntries");
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            ChooseCameraFrame(viewport);
            canvas.Zoom = 3f; canvas.Pan = new Vector2(87, -31);
            Assert.IsTrue(ShowsChecker(oldRenderer));
            Assert.AreSame(working, oldRenderer.GetComponent<MeshFilter>().sharedMesh);

            LODGroup nextGroup = null;
            MeshRenderer nextRenderer, nextHidden = null;
            if (toStandalone) nextRenderer = Standalone("Next standalone");
            else nextGroup = Group(true, out nextRenderer, out nextHidden);
            var nextMaterials = nextRenderer.sharedMaterials;
            var nextMesh = nextRenderer.GetComponent<MeshFilter>().sharedMesh;
            UnityEditor.Selection.activeGameObject = toStandalone ? nextRenderer.gameObject : nextGroup.gameObject;
            Call(hub, "OnSelectionChange");
            CollectionAssert.AreEqual(originals, oldRenderer.sharedMaterials, "restore before collecting the new preview");
            Assert.IsFalse(CheckerTexturePreview.IsActive);
            Call(hub, "CollectCanvasEntries");

            Assert.AreEqual(UvCanvasView.PreviewMode.Checker, canvas.CurrentPreviewMode);
            Assert.IsTrue(canvas.CheckerEnabled); Assert.IsTrue(ShowsChecker(nextRenderer));
            Assert.IsTrue(canvas.CheckerColorMode); Assert.IsFalse(canvas.CheckerShowR); Assert.IsTrue(canvas.CheckerShowG);
            Assert.AreEqual(1, context.PreviewUvChannel);
            CollectionAssert.AreEqual(originals, oldRenderer.sharedMaterials);
            Assert.AreSame(originalMesh, oldRenderer.GetComponent<MeshFilter>().sharedMesh);
            Assert.IsTrue(working == null, "the old working mesh is destroyed after its preview is restored");
            Assert.IsFalse(ShowsChecker(oldHidden));
            if (nextHidden) Assert.IsFalse(ShowsChecker(nextHidden));
            AssertCameraFrame(viewport); Assert.AreEqual(3f, canvas.Zoom); Assert.AreEqual(new Vector2(87, -31), canvas.Pan);
            Assert.AreSame(nextMesh, context.MeshEntries.Find(entry => entry.renderer == nextRenderer).fbxMesh);
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
            CollectionAssert.AreEqual(nextMaterials, nextRenderer.sharedMaterials);
            Assert.AreSame(nextMesh, nextRenderer.GetComponent<MeshFilter>().sharedMesh);
            Assert.IsFalse(CheckerTexturePreview.IsActive);
        }

        void AssignLightmap(params MeshRenderer[] renderers)
        {
            var texture = new Texture2D(8, 8); owned.Add(texture);
            LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = texture } };
            foreach (var renderer in renderers) {
                renderer.lightmapIndex = 0;
                renderer.lightmapScaleOffset = new Vector4(.5f, .5f, .1f, .2f);
            }
        }

        [TestCase(UvCanvasView.PreviewMode.Off)]
        [TestCase(UvCanvasView.PreviewMode.Shells3D)]
        [TestCase(UvCanvasView.PreviewMode.Lightmap)]
        public void DisplayModeFollowsTheNextGroupAndRestoresItsPredecessor(UvCanvasView.PreviewMode mode)
        {
            var firstGroup = Group(true, out var first, out _);
            var context = Open(firstGroup);
            var secondGroup = Group(true, out var second, out _);
            var firstMaterial = first.sharedMaterial; var firstMesh = first.GetComponent<MeshFilter>().sharedMesh;
            var secondMaterial = second.sharedMaterial; var secondMesh = second.GetComponent<MeshFilter>().sharedMesh;
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(first, second);
                Call(hub, "ApplyPreviewMode", mode);
                var oldPreviewMesh = first.GetComponent<MeshFilter>().sharedMesh;
                SelectModel(secondGroup.gameObject);
                Assert.AreEqual(mode, Get<UvCanvasView>(hub, "canvas").CurrentPreviewMode);
                Assert.AreSame(firstMaterial, first.sharedMaterial);
                Assert.AreSame(firstMesh, first.GetComponent<MeshFilter>().sharedMesh);
                if (mode != UvCanvasView.PreviewMode.Off) {
                    Assert.IsTrue(oldPreviewMesh == null, "the previous preview clone was destroyed");
                    Assert.AreNotSame(secondMaterial, second.sharedMaterial);
                } else Assert.AreSame(secondMaterial, second.sharedMaterial);
                Assert.AreSame(secondMesh, context.MeshEntries.Find(entry => entry.renderer == second).fbxMesh);
                Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
                Assert.AreSame(secondMaterial, second.sharedMaterial);
                Assert.AreSame(secondMesh, second.GetComponent<MeshFilter>().sharedMesh);
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [TestCase(UvCanvasView.PreviewMode.Checker)]
        [TestCase(UvCanvasView.PreviewMode.Shells3D)]
        [TestCase(UvCanvasView.PreviewMode.Lightmap)]
        public void MissingPreviewDataDoesNotDiscardTheSelectedDisplayMode(UvCanvasView.PreviewMode mode)
        {
            var firstGroup = Group(true, out var first, out _);
            var context = Open(firstGroup);
            var firstMaterial = first.sharedMaterial;
            var unsupported = Standalone("Model without UVs", false);
            unsupported.GetComponent<MeshFilter>().sharedMesh.uv = System.Array.Empty<Vector2>();
            var unsupportedMaterial = unsupported.sharedMaterial;
            var supported = Standalone("Next model with UVs");
            var supportedMaterial = supported.sharedMaterial;
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(first, supported);
                Call(hub, "ApplyPreviewMode", mode);
                SelectModel(unsupported.gameObject);
                var canvas = Get<UvCanvasView>(hub, "canvas");
                Assert.AreEqual(mode, canvas.CurrentPreviewMode);
                Assert.IsFalse(canvas.CheckerEnabled);
                Assert.IsFalse(CheckerTexturePreview.IsActive); Assert.IsFalse(ShellColorModelPreview.IsActive);
                Assert.AreSame(firstMaterial, first.sharedMaterial);
                Assert.AreSame(unsupportedMaterial, unsupported.sharedMaterial);
                SelectModel(supported.gameObject);
                Assert.AreEqual(mode, canvas.CurrentPreviewMode);
                Assert.AreNotSame(supportedMaterial, supported.sharedMaterial);
                Assert.AreEqual(1, context.PreviewUvChannel);
                Assert.AreSame(unsupportedMaterial, unsupported.sharedMaterial);
                Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
                Assert.AreSame(supportedMaterial, supported.sharedMaterial);
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreferredCheckerChannelReturnsAcrossModelsAndOffRemainsOff()
        {
            Open(Group(true, out var first, out _));
            var canvas = Get<UvCanvasView>(hub, "canvas");
            var context = Get<UvToolContext>(hub, "ctx");
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            var uv0Only = Standalone("UV0-only model", false);
            var uv0Material = uv0Only.sharedMaterial;
            SelectModel(uv0Only.gameObject);
            Assert.AreEqual(UvCanvasView.PreviewMode.Checker, canvas.CurrentPreviewMode);
            Assert.AreEqual(0, context.PreviewUvChannel); Assert.IsTrue(ShowsChecker(uv0Only)); Assert.IsFalse(ShowsChecker(first));
            var uv1Model = Standalone("UV1 model");
            SelectModel(uv1Model.gameObject);
            Assert.AreEqual(1, context.PreviewUvChannel); Assert.IsTrue(ShowsChecker(uv1Model));
            Assert.AreSame(uv0Material, uv0Only.sharedMaterial);
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
            SelectModel(uv0Only.gameObject);
            Assert.AreEqual(UvCanvasView.PreviewMode.Off, canvas.CurrentPreviewMode);
            Assert.IsFalse(ShowsChecker(uv0Only)); Assert.IsFalse(ShowsChecker(uv1Model));
        }

        [Test]
        public void ModelSelectionClampsAnUnavailableLodAndRetainsInspectionShading()
        {
            Open(Group(true, out _, out _));
            SwitchLod(1);
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            viewport.Mode = MeshViewport3D.Shading.Normals;
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            var singleLod = Group(true, out var next, out _);
            singleLod.SetLODs(new[] { new LOD(.5f, new Renderer[] { next }) });
            SelectModel(singleLod.gameObject);
            Assert.AreEqual(0, Get<UvToolContext>(hub, "ctx").PreviewLod);
            Assert.AreEqual(MeshViewport3D.Shading.Normals, viewport.Mode);
            Assert.IsTrue(ShowsChecker(next));
        }

        [Test]
        public void UvAnd3DModesKeepTheCameraFrameUntilFrameIsRequested()
        {
            Open(Group(lod1HasUv1: true, out _, out _));
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            Call(hub, "SetCanvas3D", true);
            viewport.Frame(bounds);
            ChooseCameraFrame(viewport);

            Call(hub, "SetCanvas3D", false);
            Call(hub, "SetCanvas3D", true);
            Call(viewport, "FrameIfRequested", bounds, true);
            AssertCameraFrame(viewport);

            viewport.FrameContent();
            Call(viewport, "FrameIfRequested", bounds, true);
            Assert.That(Get<Vector3>(viewport, "pivot"), Is.EqualTo(bounds.center));
            Assert.That(Get<float>(viewport, "distance"), Is.Not.EqualTo(2f));
        }

        [Test]
        public void LodSwitchKeepsBothCameraAndUvFrame()
        {
            var ctx = Open(Group(lod1HasUv1: true, out var lod0, out var lod1));
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            var canvas = Get<UvCanvasView>(hub, "canvas");
            var firstMesh = lod0.GetComponent<MeshFilter>().sharedMesh;
            var secondMesh = lod1.GetComponent<MeshFilter>().sharedMesh;
            viewport.Frame(firstMesh.bounds);
            ChooseCameraFrame(viewport);
            canvas.Zoom = 3f;
            canvas.Pan = new Vector2(87, -31);

            SwitchLod(1);
            Call(viewport, "FrameIfRequested", secondMesh.bounds, true);
            AssertCameraFrame(viewport);
            Assert.That(canvas.Zoom, Is.EqualTo(3f));
            Assert.That(canvas.Pan, Is.EqualTo(new Vector2(87, -31)));

            SwitchLod(0);
            Call(viewport, "FrameIfRequested", firstMesh.bounds, true);
            AssertCameraFrame(viewport);
            Assert.That(ctx.PreviewLod, Is.EqualTo(0));
        }

        [Test]
        public void SelectingAnotherModelKeepsTheCameraFrame()
        {
            var ctx = Open(Group(lod1HasUv1: true, out _, out _));
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            viewport.Frame(new Bounds(Vector3.zero, Vector3.one));
            ChooseCameraFrame(viewport);

            var nextGroup = Group(lod1HasUv1: true, out _, out _);
            nextGroup.transform.position = new Vector3(20, -7, 30);
            Call(hub, "RestoreWorkingMeshes");
            ctx.Refresh(nextGroup);
            Call(hub, "CollectCanvasEntries");
            var items = Get<List<MeshViewport3D.Item>>(hub, "viewportItems");
            Assert.That(items[0].matrix.MultiplyPoint3x4(Vector3.zero), Is.EqualTo(Vector3.zero));
            Assert.That(nextGroup.transform.position, Is.EqualTo(new Vector3(20, -7, 30)), "preview does not move the scene object");
            var bounds = PreviewBounds(items);
            Call(viewport, "FrameIfRequested", bounds, true);
            AssertCameraFrame(viewport);
        }

        [Test]
        public void OriginPlacementPreservesHierarchyRotationScaleAndLodSwitch()
        {
            var group = Group(lod1HasUv1: true, out var lod0, out var lod1);
            var offset = new Vector3(40, -10, 70);
            group.transform.position = offset;
            group.transform.rotation = Quaternion.Euler(17, 35, -11);
            group.transform.localScale = new Vector3(2, 3, .5f);
            lod0.transform.localPosition = new Vector3(1, 2, -3);
            lod1.transform.localPosition = lod0.transform.localPosition;
            lod0.transform.localRotation = Quaternion.Euler(0, 25, 0);
            lod1.transform.localRotation = lod0.transform.localRotation;
            Open(group);
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            ChooseCameraFrame(viewport);
            for (int lod = 0; lod < 2; ++lod) {
                SwitchLod(lod);
                var item = Get<List<MeshViewport3D.Item>>(hub, "viewportItems")[0];
                var renderer = lod == 0 ? lod0 : lod1;
                foreach (var point in new[] { Vector3.zero, Vector3.one, new Vector3(-2, 3, 4) }) {
                    var expected = renderer.transform.TransformPoint(point) - offset;
                    Assert.That(Vector3.Distance(item.matrix.MultiplyPoint3x4(point), expected), Is.LessThan(1e-5f));
                }
                Call(viewport, "FrameIfRequested", PreviewBounds(new[] { item }), true);
                AssertCameraFrame(viewport);
            }
            Assert.That(group.transform.position, Is.EqualTo(offset));
        }

        [Test]
        public void StandalonePreviewUsesZeroPositionWithoutChangingItsSceneTransform()
        {
            var group = Group(lod1HasUv1: true, out var renderer, out _);
            var ctx = Open(group);
            renderer.transform.position = new Vector3(-30, 12, 17);
            renderer.transform.rotation = Quaternion.Euler(5, 25, 45);
            renderer.transform.localScale = new Vector3(2, 1, 3);
            var original = renderer.localToWorldMatrix;
            Call(hub, "RestoreWorkingMeshes");
            ctx.RefreshStandalone(renderer);
            Call(hub, "CollectCanvasEntries");
            var item = Get<List<MeshViewport3D.Item>>(hub, "viewportItems")[0];
            Assert.That(item.matrix.MultiplyPoint3x4(Vector3.zero), Is.EqualTo(Vector3.zero));
            Assert.That(Vector3.Distance(item.matrix.MultiplyVector(Vector3.one), original.MultiplyVector(Vector3.one)), Is.LessThan(1e-5f));
            Assert.That(renderer.localToWorldMatrix, Is.EqualTo(original));
        }

        [Test]
        public void TextureAoPreviewRemovesRootTranslationButPreservesExportPlacement()
        {
            var panel = new TextureAoBakePanel();
            var resultType = typeof(TextureAoBakePanel).GetNestedType("Result", BindingFlags.NonPublic);
            var result = System.Activator.CreateInstance(resultType, true);
            var mesh = Quad("AO preview", true);
            var placement = Matrix4x4.TRS(new Vector3(20, -30, 40), Quaternion.Euler(15, 30, 5), new Vector3(2, 3, 4));
            resultType.GetField("mesh", Private).SetValue(result, mesh);
            resultType.GetField("placement", Private).SetValue(result, placement);
            ((System.Collections.IList)typeof(TextureAoBakePanel).GetField("results", Private).GetValue(panel)).Add(result);
            var items = new List<MeshViewport3D.Item>();
            Assert.IsTrue(panel.Get3DContent(items));
            Assert.That(items[0].matrix.MultiplyPoint3x4(Vector3.zero), Is.EqualTo(Vector3.zero));
            Assert.That(items[0].matrix.MultiplyVector(Vector3.one), Is.EqualTo(placement.MultiplyVector(Vector3.one)));
            Assert.That(resultType.GetField("placement", Private).GetValue(result), Is.EqualTo(placement));
        }

        [Test]
        public void ContentIsFramedOnlyOnAnExplicitRequest()
        {
            using (var viewport = new MeshViewport3D())
            {
                var bounds = new Bounds(new Vector3(20, 0, 0), new Vector3(4, 3, 1));
                Call(viewport, "FrameIfRequested", bounds, true);
                Assert.That(Get<Vector3>(viewport, "pivot"), Is.EqualTo(Vector3.zero));
                Assert.That(Get<float>(viewport, "distance"), Is.EqualTo(5f));

                viewport.FrameContent();
                Call(viewport, "FrameIfRequested", bounds, false);
                Assert.That(Get<Vector3>(viewport, "pivot"), Is.EqualTo(Vector3.zero), "an empty view waits for content");
                Call(viewport, "FrameIfRequested", bounds, true);
                Assert.That(Get<Vector3>(viewport, "pivot"), Is.EqualTo(bounds.center));

                ChooseCameraFrame(viewport);
                Call(viewport, "FrameIfRequested", bounds, true);
                AssertCameraFrame(viewport);
            }
        }

        [Test]
        public void ContentSizeRefreshesZoomBoundsWithoutReframing([Values(.001f, 1000f)] float size)
        {
            using var view = new MeshViewport3D(); ChooseCameraFrame(view);
            var bounds = new Bounds(new Vector3(40, -20, 10), Vector3.one * size);
            Call(view, "FrameIfRequested", bounds, true);
            AssertCameraFrame(view);
            Assert.AreEqual(bounds.extents.magnitude, Get<float>(view, "radius"), size * .00001f);
        }

        [UnityTest]
        public IEnumerator DistantContentRemainsVisibleWithoutReframing()
        {
            var mesh = Quad("Distant preview", true);
            mesh.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up };
            var material = new Material(Shader.Find("Hidden/MeshLab/UvOverlay")); owned.Add(material);
            material.SetColor("_Color", Color.red);
            var items = new[] { new MeshViewport3D.Item(mesh, Matrix4x4.Translate(Vector3.forward * 20), new[] { material }) };
            using var view = new MeshViewport3D { ViewProjection = MeshViewport3D.Projection.XY, ShowAxes = false, ShowGrid = false };
            Set(view, "pivot", new Vector3(.5f, .5f, 0)); Set(view, "distance", 2f);
            var window = OpenSpotInputWindow(); int redPixels = 0;
            try {
                window.Input = current => {
                    if (current.type != EventType.Repaint) return;
                    view.Draw(new Rect(0, 0, 256, 128), items, null);
                    var copy = GpuReadback.Read(Get<RenderTexture>(view, "offscreen"), 256, 128, hdr: false);
                    if (!copy) return;
                    try { redPixels = 0; foreach (var pixel in copy.GetPixels()) if (pixel.r > .5f && pixel.g < .2f) ++redPixels; }
                    finally { Object.DestroyImmediate(copy); }
                };
                for (int i = 0; i < 30 && redPixels < 30; ++i) { window.Repaint(); yield return null; }
                Assert.Greater(redPixels, 30, "Current bounds must extend the clip plane without moving the camera.");
                Assert.AreEqual(new Vector3(.5f, .5f, -2), view.Camera.transform.position);
                Assert.AreEqual(2f, Get<float>(view, "distance"));
                Assert.Greater(view.Camera.farClipPlane, 22f);
            }
            finally { window.Input = null; window.Close(); Object.DestroyImmediate(window); }
        }

        [Test]
        public void CheckerFollowsThePreviewLod()
        {
            var group = Group(lod1HasUv1: true, out var lod0, out var lod1);
            var ctx = Open(group);
            var canvas = Get<UvCanvasView>(hub, "canvas");

            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            Assert.That(ShowsChecker(lod0), Is.True);
            Assert.That(ShowsChecker(lod1), Is.False);

            SwitchLod(1);
            Assert.That(ctx.PreviewLod, Is.EqualTo(1));
            Assert.That(canvas.CurrentPreviewMode, Is.EqualTo(UvCanvasView.PreviewMode.Checker), "the mode survives the switch");
            Assert.That(ShowsChecker(lod1), Is.True, "the checker moves to the LOD now shown");
            Assert.That(ShowsChecker(lod0), Is.False, "the hidden LOD gets its material back");

            SwitchLod(0);
            Assert.That(ShowsChecker(lod0), Is.True);
            Assert.That(ShowsChecker(lod1), Is.False);
        }

        [Test]
        public void PreviewFollowsCompletedAndRepeatedTransfer(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode,
            [Values(false, true)] bool threeD)
        {
            var ctx = Open(Group(true, out var sourceRenderer, out var targetRenderer));
            var source = ctx.MeshEntries.Find(e => e.renderer == sourceRenderer);
            var target = ctx.MeshEntries.Find(e => e.renderer == targetRenderer);
            var originalMaterial = targetRenderer.sharedMaterial;
            var originalMesh = target.fbxMesh;
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(targetRenderer);
                SwitchLod(1); Call(hub, "SetCanvas3D", threeD);
                SetPreviewFrame(); Call(hub, "ApplyPreviewMode", mode);
                for (int pass = 0; pass < 2; ++pass) {
                    var previousOutput = target.transferredMesh;
                    var packed = Object.Instantiate(source.fbxMesh); owned.Add(packed);
                    packed.uv2 = ShiftedUvs(.1f + pass * .2f); source.repackedMesh = packed;
                    source.repackedAtlasWidth = source.repackedAtlasHeight = 512;
                    UvProgress.Begin("Preview transfer regression", cancelable: true);
                    try { ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl", 1, false)).GetAwaiter().GetResult(); }
                    finally { UvProgress.End(); }
                    Assert.NotNull(target.transferredMesh);
                    Assert.IsTrue(previousOutput == null, "Repeated transfer releases its previous output.");
                    Assert.IsNotNull(targetRenderer.GetComponent<MeshFilter>().sharedMesh,
                        "Replacing a transfer output must not leave a renderer bound to a destroyed mesh until repaint.");
                    Call(hub, "CollectCanvasEntries");
                    AssertFreshPreview(ctx, target, mode);
                    AssertPreviewFrame();
                }
                Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
                Assert.AreSame(originalMaterial, targetRenderer.sharedMaterial);
                Assert.AreSame(originalMesh, targetRenderer.GetComponent<MeshFilter>().sharedMesh);
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreviewFollowsMeshReplacementAndInPlaceUvChanges(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode,
            [Values("original", "repack", "transfer")] string stage,
            [Values(false, true)] bool inPlace)
        {
            var ctx = Open(Group(true, out var source, out var target));
            SwitchLod(stage == "transfer" ? 1 : 0);
            var entry = ctx.MeshEntries.Find(e => e.renderer == (stage == "transfer" ? target : source));
            var mesh = Object.Instantiate(entry.fbxMesh); owned.Add(mesh);
            SetStageMesh(entry, stage, mesh);
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(entry.renderer as MeshRenderer);
                Call(hub, "CollectCanvasEntries"); SetPreviewFrame();
                Call(hub, "ApplyPreviewMode", mode); Call(hub, "CollectCanvasEntries");
                var oldPreview = entry.meshFilter.sharedMesh;
                if (inPlace) VertexChannels.SetUvs(mesh, 1, ShiftedUvs(.3f));
                else {
                    mesh = Object.Instantiate(entry.fbxMesh); owned.Add(mesh); mesh.uv2 = ShiftedUvs(.3f);
                    SetStageMesh(entry, stage, mesh);
                }
                var canvas = Get<UvCanvasView>(hub, "canvas");
                canvas.HasHoveredShell = true; canvas.HoveredShellId = 777;
                Call(hub, "CollectCanvasEntries");
                AssertFreshPreview(ctx, entry, mode); AssertPreviewFrame();
                Assert.IsFalse(canvas.HasHoveredShell, "Spot cannot retain a hit on obsolete UV data.");
                if (mode != UvCanvasView.PreviewMode.Checker) Assert.IsTrue(oldPreview == null, "Old preview clones are released.");
                var currentPreview = entry.meshFilter.sharedMesh;
                Call(hub, "CollectCanvasEntries");
                Assert.AreSame(currentPreview, entry.meshFilter.sharedMesh, "Repainting unchanged data must not recreate preview clones.");
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void AutoTuneRestoresTheSourceAtlasTogetherWithItsTransfer()
        {
            var ctx = Open(Group(true, out var sourceRenderer, out var targetRenderer));
            var source = ctx.MeshEntries.Find(e => e.renderer == sourceRenderer);
            var target = ctx.MeshEntries.Find(e => e.renderer == targetRenderer);
            var bestType = typeof(UvTransferWorkflow).GetNestedType("AutoTuneChoice", BindingFlags.NonPublic);
            var best = System.Activator.CreateInstance(bestType, true);
            source.repackedMesh = Object.Instantiate(source.fbxMesh);
            source.repackedMesh.uv2 = ShiftedUvs(.1f);
            source.repackedAtlasWidth = 512; source.repackedAtlasHeight = 512;
            target.transferredMesh = Object.Instantiate(target.fbxMesh);
            target.transferredMesh.uv2 = ShiftedUvs(.1f);
            var expected = source.repackedMesh.uv2;
            CallWorkflow("CaptureAutoTuneChoice", best);
            var savedAtlas = SavedAutoTuneMesh(best, "Atlases", source);
            var savedTransfer = SavedAutoTuneMesh(best, "Transfers", target);
            CallWorkflow("CaptureAutoTuneChoice", best);
            Assert.IsTrue(savedAtlas == null, "Replacing the best attempt releases its atlas snapshot.");
            Assert.IsTrue(savedTransfer == null, "Replacing the best attempt releases its transfer snapshot.");
            var oldAtlas = source.repackedMesh; var oldTransfer = target.transferredMesh;
            source.repackedMesh.uv2 = ShiftedUvs(.5f);
            source.repackedAtlasWidth = 1024; source.repackedAtlasHeight = 1024;
            target.transferredMesh.uv2 = ShiftedUvs(.5f);
            var restore = typeof(UvTransferWorkflow).GetMethod("RestoreAutoTuneChoice", Private | BindingFlags.Static);
            restore.Invoke(restore.IsStatic ? null : hub.DiagnosticWorkflow, new[] { best });
            CollectionAssert.AreEqual(expected, source.repackedMesh.uv2, "The target cannot use an atlas from a discarded attempt.");
            CollectionAssert.AreEqual(expected, target.transferredMesh.uv2);
            Assert.AreEqual(512, source.repackedAtlasWidth); Assert.AreEqual(512, source.repackedAtlasHeight);
            Assert.IsTrue(oldAtlas == null); Assert.IsTrue(oldTransfer == null);
            ((System.IDisposable)best).Dispose();
            Assert.IsNotNull(source.repackedMesh); Assert.IsNotNull(target.transferredMesh);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FullPipelineCannotTransferOrCompleteAfterFailedSourceRepack(bool partialSuccess)
        {
            var group = Group(true, out var sourceRenderer, out var targetRenderer);
            sourceRenderer.GetComponent<MeshFilter>().sharedMesh.uv = System.Array.Empty<Vector2>();
            if (partialSuccess) {
                var valid = Lod(group.transform, "Other_LOD0", Quad("Other_LOD0", true));
                group.SetLODs(new[] { new LOD(.5f, new Renderer[] { sourceRenderer, valid }),
                    new LOD(.1f, new Renderer[] { targetRenderer }) });
            }
            var ctx = Open(group); ctx.AtlasResolution = 64; ctx.InternalOversample = 1;
            ctx.RepackResolutionMode = ResolutionMode.Manual; ctx.RepackPerMesh = true;
            typeof(UvTransferWorkflow).GetField("stageRunAnalyzeUv0", Private).SetValue(hub.DiagnosticWorkflow, false);
            typeof(UvTransferWorkflow).GetField("stageRunWeldUv0", Private).SetValue(hub.DiagnosticWorkflow, false);
            typeof(UvTransferWorkflow).GetField("skipSymmetrySplitStep", Private).SetValue(hub.DiagnosticWorkflow, true);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("\\[Pipeline\\] Repack failed"));
            var completed = ((System.Threading.Tasks.Task<bool>)CallWorkflow("ExecFullPipelineCoreImpl", false)).GetAwaiter().GetResult();
            Assert.IsFalse(completed, "A partial/all failed repack is not a completed pipeline.");
            Assert.IsFalse(ctx.HasTransfer);
            Assert.IsNull(ctx.MeshEntries.Find(e => e.renderer == targetRenderer).transferredMesh);
            var stages = (System.Array)typeof(UvTransferWorkflow).GetField("stageOutcome", Private).GetValue(hub.DiagnosticWorkflow);
            Assert.AreEqual("Failed", stages.GetValue(4).ToString());
            Assert.AreEqual("Skipped", stages.GetValue(5).ToString());
            Assert.AreEqual(partialSuccess, ctx.HasRepack, "Successful source outputs remain available after a partial failure.");
        }

        [TestCase(".001")]
        [TestCase(".002")]
        public void NumericInstanceLodTransfersFromItsOwnSourceInsteadOfTheFirstRenderer(string instance)
        {
            var root = new GameObject("Train"); owned.Add(root);
            var door = Lod(root.transform, "Door_LOD0", Quad("Door_LOD0", true));
            var source = Lod(root.transform, "Seat_LOD0" + instance, Quad("Seat_LOD0" + instance, true));
            var target = Lod(root.transform, "Seat_LOD1" + instance, Quad("Seat_LOD1" + instance, false));
            var expected = ShiftedUvs(.5f); source.GetComponent<MeshFilter>().sharedMesh.uv2 = expected;
            var group = root.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(.5f, new Renderer[] { door, source }), new LOD(.1f, new Renderer[] { target }) });
            var ctx = Open(group); ctx.SourceLodIndex = 0;
            UvProgress.Begin("Instance source regression", cancelable: true);
            try { ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl", 1, false)).GetAwaiter().GetResult(); }
            finally { UvProgress.End(); }
            var output = ctx.MeshEntries.Find(e => e.renderer == target).transferredMesh;
            Assert.IsNotNull(output);
            for (int i = 0; i < expected.Length; ++i)
                Assert.That(Vector2.Distance(expected[i], output.uv2[i]), Is.LessThan(1e-5), "LOD instance must retain its matching source atlas.");
        }

        static Mesh SavedAutoTuneMesh(object best, string field, MeshEntry entry)
        {
            var dictionary = (System.Collections.IDictionary)best.GetType().GetField(field, Private).GetValue(best);
            var snapshot = dictionary[entry];
            return (Mesh)snapshot.GetType().GetField("Item1").GetValue(snapshot);
        }

        [TestCase("replacement")]
        [TestCase("winner")]
        [TestCase("in-place")]
        public void TransferAfterSourceReplacementUsesTheCurrentChartIndices(string change)
        {
            var ctx = Open(Group(true, out var sourceRenderer, out var targetRenderer));
            var source = ctx.MeshEntries.Find(e => e.renderer == sourceRenderer);
            var target = ctx.MeshEntries.Find(e => e.renderer == targetRenderer);
            var bestType = typeof(UvTransferWorkflow).GetNestedType("AutoTuneChoice", BindingFlags.NonPublic);
            var best = System.Activator.CreateInstance(bestType, true);
            source.repackedMesh = NearbyChartAtlas(false);
            source.repackedAtlasWidth = source.repackedAtlasHeight = 512;
            try {
                ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferAllImpl", false)).GetAwaiter().GetResult();
                Assert.AreEqual(0, target.shellTransferResult.targetShellToSourceShell[0]);
                var expected = target.transferredMesh.uv2;
                CallWorkflow("CaptureAutoTuneChoice", best);

                Object.DestroyImmediate(source.repackedMesh);
                source.repackedMesh = NearbyChartAtlas(true);
                ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferAllImpl", false)).GetAwaiter().GetResult();
                Assert.AreEqual(1, target.shellTransferResult.targetShellToSourceShell[0],
                    "The discarded atlas stores the same physical chart at a different index.");

                if (change == "winner") CallWorkflow("RestoreAutoTuneChoice", best);
                else if (change == "in-place") {
                    var replacement = NearbyChartAtlas(false);
                    source.repackedMesh.vertices = replacement.vertices;
                    source.repackedMesh.uv = replacement.uv;
                    source.repackedMesh.uv2 = replacement.uv2;
                    source.repackedMesh.triangles = replacement.triangles;
                    source.repackedMesh.RecalculateNormals();
                    VertexChannels.RaiseChanged(source.repackedMesh);
                }
                else {
                    Object.DestroyImmediate(source.repackedMesh);
                    source.repackedMesh = NearbyChartAtlas(false);
                }
                UvProgress.Begin("Source chart index regression", cancelable: true);
                try { ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl", 1, false)).GetAwaiter().GetResult(); }
                finally { UvProgress.End(); }
                Assert.AreEqual(0, target.shellTransferResult.targetShellToSourceShell[0],
                    "Hints from the discarded atlas must not select the nearby physical chart.");
                for (int i = 0; i < expected.Length; ++i)
                    Assert.That(Vector2.Distance(expected[i], target.transferredMesh.uv2[i]), Is.LessThan(.00001f));
            }
            finally { ((System.IDisposable)best).Dispose(); }
        }

        Mesh NearbyChartAtlas(bool reverseOrder)
        {
            var mesh = new Mesh { name = "Nearby charts with different atlas locations" };
            var positions = new Vector3[8]; var uv0 = new Vector2[8]; var uv2 = new Vector2[8];
            var quad = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up };
            var authored = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            for (int chart = 0; chart < 2; ++chart) {
                bool nearby = (chart == 1) != reverseOrder;
                var packed = ShiftedUvs(nearby ? .7f : .1f);
                for (int corner = 0; corner < 4; ++corner) {
                    int vertex = chart * 4 + corner;
                    positions[vertex] = quad[corner] + (nearby ? Vector3.forward * .01f : Vector3.zero);
                    uv0[vertex] = authored[corner]; uv2[vertex] = packed[corner];
                }
            }
            mesh.vertices = positions; mesh.uv = uv0; mesh.uv2 = uv2;
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            mesh.RecalculateNormals(); owned.Add(mesh); return mesh;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TransferReleasesSceneBindingsToItsPreviousWorkingOutput(bool checker)
        {
            var ctx = Open(Group(true, out var sourceRenderer, out var targetRenderer));
            var source = ctx.MeshEntries.Find(e => e.renderer == sourceRenderer);
            var target = ctx.MeshEntries.Find(e => e.renderer == targetRenderer);
            source.repackedMesh = Object.Instantiate(source.fbxMesh);
            source.repackedAtlasWidth = source.repackedAtlasHeight = 512;
            var previousOutput = target.transferredMesh = Object.Instantiate(target.fbxMesh);
            target.meshFilter.sharedMesh = previousOutput;
            SwitchLod(1);
            if (checker) Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            UvProgress.Begin("Working output binding regression", cancelable: true);
            try { ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl", 1, false)).GetAwaiter().GetResult(); }
            finally { UvProgress.End(); }
            Assert.IsTrue(previousOutput == null);
            Assert.AreSame(target.fbxMesh, target.meshFilter.sharedMesh,
                "Even a preview backup may refer to the previous working output.");
            Call(hub, "CollectCanvasEntries");
            if (checker) AssertFreshPreview(ctx, target, UvCanvasView.PreviewMode.Checker);
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
            Assert.AreSame(target.fbxMesh, target.meshFilter.sharedMesh);
        }

        [Test]
        public void PreviewRestoresPreferredChannelWhenTransferCreatesUv2()
        {
            var ctx = Open(Group(false, out var sourceRenderer, out var targetRenderer));
            var source = ctx.MeshEntries.Find(e => e.renderer == sourceRenderer);
            var target = ctx.MeshEntries.Find(e => e.renderer == targetRenderer);
            SwitchLod(1); Assert.AreEqual(0, ctx.PreviewUvChannel);
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            source.repackedMesh = Object.Instantiate(source.fbxMesh); owned.Add(source.repackedMesh);
            source.repackedMesh.uv2 = ShiftedUvs(.2f);
            UvProgress.Begin("Preview transfer channel regression", cancelable: true);
            try { ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl", 1, false)).GetAwaiter().GetResult(); }
            finally { UvProgress.End(); }
            Call(hub, "CollectCanvasEntries");
            Assert.AreEqual(1, ctx.PreviewUvChannel);
            AssertFreshPreview(ctx, target, UvCanvasView.PreviewMode.Checker);
        }

        [TestCase(false)] [TestCase(true)]
        public void ReverseTransferPublishesTheWholeChainAndKeepsCheckerAndCamera(bool prepareSeed)
        {
            var ctx = Open(Group(true, out var fineRenderer, out var coarseRenderer));
            var workflow = hub.DiagnosticWorkflow;
            typeof(UvTransferWorkflow).GetField("reversePrepareSeed",Private).SetValue(workflow,prepareSeed);
            var oldSource = ctx.SourceLodIndex;
            ctx.RepackResolutionMode = ResolutionMode.Manual; ctx.AtlasResolution = 128;
            var coarse = ctx.MeshEntries.Find(e => e.renderer == coarseRenderer);
            var fine = ctx.MeshEntries.Find(e => e.renderer == fineRenderer);
            // This fixture's third corner has Z=1: make it planar to establish an isotropic seed.
            coarse.originalMesh.vertices = fine.originalMesh.vertices = new[] {Vector3.zero, Vector3.right, new Vector3(1,1,0), Vector3.up};
            var originalCoarseIndices = coarse.originalMesh.triangles.Concat(new[] { 0, 0, 0 }).ToArray();
            var originalFineIndices = fine.originalMesh.triangles.Concat(new[] { 0, 0, 0 }).ToArray();
            coarse.originalMesh.triangles = originalCoarseIndices;
            fine.originalMesh.triangles = originalFineIndices;
            var prior = fine.transferredMesh = Object.Instantiate(fine.originalMesh);
            var previousDirectory = BenchmarkRecorder.OutputDirectoryOverride;
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),"MeshLabReversePreview_"+System.Guid.NewGuid().ToString("N"));
            try
            {
                BenchmarkRecorder.OutputDirectoryOverride = directory;
                Call(hub,"CollectCanvasEntries"); SetPreviewFrame(); Call(hub,"ApplyPreviewMode",UvCanvasView.PreviewMode.Checker);
                workflow.RunReverseTransfer(false).GetAwaiter().GetResult();
                Assert.IsTrue(prior == null); Assert.AreEqual(1,ctx.SourceLodIndex);
                Assert.IsTrue(ctx.HasRepack); Assert.IsTrue(ctx.HasTransfer);
                Assert.IsNotNull(coarse.repackedMesh); Assert.IsNotNull(fine.transferredMesh);
                Assert.AreEqual(coarse.repackedAtlasWidth,fine.repackedAtlasWidth);
                Assert.IsNotEmpty(fine.reverseTransferJson);
                CollectionAssert.AreEqual(originalCoarseIndices, coarse.originalMesh.triangles);
                CollectionAssert.AreEqual(originalFineIndices, fine.originalMesh.triangles);
                Assert.AreEqual(originalCoarseIndices.Length - 3, coarse.repackedMesh.triangles.Length);
                Assert.AreEqual(originalFineIndices.Length - 3, fine.transferredMesh.triangles.Length);
                Assert.AreEqual(1, JsonUtility.FromJson<ReverseUvAudit.ProvenanceEntry>(fine.reverseTransferJson).surface.removedSourceFaces.Length);
                Call(hub,"CollectCanvasEntries"); AssertFreshPreview(ctx,fine,UvCanvasView.PreviewMode.Checker); AssertPreviewFrame();
                Assert.AreEqual(1,System.IO.Directory.GetFiles(directory,"*.json").Length);
                var saved = SidecarStore.TryBuildEntry(fine,fine.transferredMesh,false,SidecarStore.AoUvTarget.None,out var sidecar);
                Assert.IsTrue(saved); Assert.AreEqual(fine.reverseTransferJson,sidecar.reverseTransferJson);
            }
            finally
            {
                BenchmarkRecorder.OutputDirectoryOverride = previousDirectory;
                if(System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory,true);
                ctx.SourceLodIndex = oldSource;
            }
        }

        [Test] public void FailedReverseTransferKeepsThePreviousChainAndProvenance()
        {
            var ctx = Open(Group(true,out var fineRenderer,out var coarseRenderer));
            var fine = ctx.MeshEntries.Find(e=>e.renderer==fineRenderer);
            var coarse = ctx.MeshEntries.Find(e=>e.renderer==coarseRenderer);
            fine.transferredMesh=Object.Instantiate(fine.originalMesh); coarse.repackedMesh=Object.Instantiate(coarse.originalMesh);
            fine.reverseTransferJson="prior"; var previousFine=fine.transferredMesh; var previousCoarse=coarse.repackedMesh;
            ctx.HasTransfer=ctx.HasRepack=true;
            typeof(UvTransferWorkflow).GetField("reverseReach",Private).SetValue(hub.DiagnosticWorkflow,float.NaN);
            ctx.RepackResolutionMode=ResolutionMode.Manual; ctx.AtlasResolution=128;
            Assert.Catch(()=>hub.DiagnosticWorkflow.RunReverseTransfer(false).GetAwaiter().GetResult());
            Assert.AreSame(previousFine,fine.transferredMesh); Assert.AreSame(previousCoarse,coarse.repackedMesh);
            Assert.AreEqual("prior",fine.reverseTransferJson); Assert.IsTrue(ctx.HasTransfer); Assert.AreEqual(0,ctx.SourceLodIndex);
        }

        [Test]
        public void PreviewSurvivesResetUndoAndContextRefresh(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode,
            [Values("reset", "working-reset", "undo", "refresh")] string change)
        {
            var group = Group(true, out var renderer, out _); var ctx = Open(group);
            var entry = ctx.MeshEntries.Find(e => e.renderer == renderer); var baseline = entry.fbxMesh;
            entry.repackedMesh = Object.Instantiate(baseline); owned.Add(entry.repackedMesh);
            entry.repackedMesh.uv2 = ShiftedUvs(.3f);
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                Call(hub, "CollectCanvasEntries"); SetPreviewFrame(); Call(hub, "ApplyPreviewMode", mode);
                if (change == "reset") CallWorkflow("ResetWorkingMeshesToFbx");
                else if (change == "working-reset") CallWorkflow("ResetWorkingCopies");
                else if (change == "undo") Call(hub, "OnUndoRedo");
                else ctx.Refresh(group);
                Call(hub, "CollectCanvasEntries");
                entry = ctx.MeshEntries.Find(e => e.renderer == renderer);
                Assert.AreSame(baseline, entry.fbxMesh, "Refresh must never capture a temporary preview as the imported baseline.");
                AssertFreshPreview(ctx, entry, mode); AssertPreviewFrame();
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreviewSurvivesFailedAssetPreparation(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode,
            [Values(false, true)] bool throwDuringPreparation)
        {
            var ctx = Open(Group(true, out var renderer, out _));
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                SetPreviewFrame(); Call(hub, "ApplyPreviewMode", mode);
                if (throwDuringPreparation) {
                    ctx.Assets.BeforeWrite += () => { throw new System.InvalidOperationException("Preparation regression"); };
                    Assert.Throws<System.InvalidOperationException>(() => ctx.Assets.ApplyUv2Public());
                } else ctx.Assets.ApplyUv2Public(); // no FBX path: exits before any file write
                Call(hub, "CollectCanvasEntries");
                AssertFreshPreview(ctx, ctx.MeshEntries.Find(e => e.renderer == renderer), mode); AssertPreviewFrame();
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreviewFollowsEveryUvChannelChange(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode,
            [Values(0, 1, 2)] int channel)
        {
            var ctx = Open(Group(true, out var renderer, out _));
            var entry = ctx.MeshEntries.Find(e => e.renderer == renderer);
            entry.repackedMesh = Object.Instantiate(entry.fbxMesh); owned.Add(entry.repackedMesh);
            entry.repackedMesh.uv2 = ShiftedUvs(.3f);
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                Call(hub, "CollectCanvasEntries"); SetPreviewFrame(); Call(hub, "ApplyPreviewMode", mode);
                Call(hub, "OnPreviewChannelChanged", channel); Call(hub, "CollectCanvasEntries");
                Assert.AreEqual(channel == 2 ? 0 : channel, ctx.PreviewUvChannel, "A missing channel uses the available fallback.");
                AssertFreshPreview(ctx, entry, mode); AssertPreviewFrame();
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [TestCase("texture")]
        [TestCase("index")]
        [TestCase("scale-offset")]
        [TestCase("pixels")]
        [TestCase("added")]
        public void PreviewFollowsLightmapMetadataAndTextureUpdates(string change)
        {
            var ctx = Open(Group(true, out var renderer, out _)); var entry = ctx.MeshEntries.Find(e => e.renderer == renderer);
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (change == "added") { renderer.lightmapIndex = -1; LightmapSettings.lightmaps = new LightmapData[0]; }
                else AssignLightmap(renderer);
                SetPreviewFrame(); Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Lightmap); Call(hub, "CollectCanvasEntries");
                var oldPreview = entry.meshFilter.sharedMesh;
                if (change == "added") AssignLightmap(renderer);
                else if (change == "scale-offset") renderer.lightmapScaleOffset = new Vector4(.25f, .5f, .6f, .1f);
                else if (change == "pixels") {
                    var texture = LightmapSettings.lightmaps[0].lightmapColor; texture.SetPixel(0, 0, Color.green); texture.Apply();
                } else {
                    var texture = new Texture2D(8, 8); owned.Add(texture);
                    if (change == "texture") LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = texture } };
                    else {
                        LightmapSettings.lightmaps = new[] { LightmapSettings.lightmaps[0], new LightmapData { lightmapColor = texture } };
                        renderer.lightmapIndex = 1;
                    }
                }
                Call(hub, "CollectCanvasEntries");
                AssertFreshPreview(ctx, entry, UvCanvasView.PreviewMode.Lightmap); AssertPreviewFrame();
                Assert.AreSame(LightmapSettings.lightmaps[renderer.lightmapIndex].lightmapColor, renderer.sharedMaterial.mainTexture);
                if (change != "added") Assert.IsTrue(oldPreview == null, "The old lightmap clone is released after metadata changes.");
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreviewDetectsLodRendererChangesWithUnchangedCounts(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode,
            [Values(false, true)] bool reorder)
        {
            var group = Group(true, out var first, out var second); var ctx = Open(group);
            var oldMaterial = first.sharedMaterial; var oldMesh = first.GetComponent<MeshFilter>().sharedMesh;
            var replacement = reorder ? second : Standalone("Replacement_LOD0");
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(first, second, replacement);
                SetPreviewFrame(); Call(hub, "ApplyPreviewMode", mode);
                group.SetLODs(new[] { new LOD(.5f, new Renderer[] { replacement }), new LOD(.1f, new Renderer[] { reorder ? first : second }) });
                Call(hub, "RefreshLodStructure"); Call(hub, "CollectCanvasEntries");
                var entry = ctx.MeshEntries.Find(e => e.renderer == replacement);
                Assert.NotNull(entry); Assert.AreEqual(0, entry.lodIndex);
                AssertFreshPreview(ctx, entry, mode); AssertPreviewFrame();
                Assert.AreSame(oldMaterial, first.sharedMaterial); Assert.AreSame(oldMesh, first.GetComponent<MeshFilter>().sharedMesh);
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreviewStaysSuspendedUntilNestedAssetWritesFinish(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode)
        {
            var ctx = Open(Group(true, out var renderer, out _)); var material = renderer.sharedMaterial;
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                Call(hub, "ApplyPreviewMode", mode);
                using (ctx.Assets.PreservePreviewDuringWrite()) {
                    ctx.Assets.BeforeWrite.Invoke();
                    using (ctx.Assets.PreservePreviewDuringWrite()) { ctx.Assets.BeforeWrite.Invoke(); Call(hub, "CollectCanvasEntries"); }
                    Call(hub, "CollectCanvasEntries");
                    Assert.AreSame(material, renderer.sharedMaterial, "Export must read authored materials even when the window repaints.");
                }
                Call(hub, "CollectCanvasEntries"); AssertFreshPreview(ctx, ctx.MeshEntries.Find(e => e.renderer == renderer), mode);
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreviewSurvivesSavingMeshAssets(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode)
        {
            var ctx = Open(Group(true, out var renderer, out _));
            var previousLightmaps = LightmapSettings.lightmaps;
            string folder = "Assets/PreviewWriteRegression_" + System.Guid.NewGuid().ToString("N");
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                ctx.PipeSettings.savePath = folder; SetPreviewFrame(); Call(hub, "ApplyPreviewMode", mode);
                ctx.Assets.SaveAllPublic();
                Assert.AreEqual(2, UnityEditor.AssetDatabase.FindAssets("t:Mesh", new[] { folder }).Length);
                Call(hub, "CollectCanvasEntries"); AssertFreshPreview(ctx, ctx.MeshEntries.Find(e => e.renderer == renderer), mode); AssertPreviewFrame();
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void SurfaceAreaDisplayUpdatesWhenGeometryChangesInPlace()
        {
            var ctx = Open(Group(true, out var renderer, out _));
            var mesh = ctx.MeshEntries.Find(e => e.renderer == renderer).originalMesh;
            double before = (double)CallWorkflow("GetSourceAreaPreview");
            var positions = mesh.vertices;
            for (int i = 0; i < positions.Length; ++i) positions[i] *= 2;
            mesh.vertices = positions; VertexChannels.RaiseChanged(mesh);
            Assert.AreEqual(before * 4, (double)CallWorkflow("GetSourceAreaPreview"), .00001);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LightmapOverlaySamplesTheRenderersAtlasRegion(bool rightHalf)
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            owned.Add(texture);
            var pixels = new Color[64];
            for (int i = 0; i < pixels.Length; ++i) pixels[i] = i % 8 < 4 ? Color.red : Color.green;
            texture.SetPixels(pixels); texture.Apply();
            var material = new Material(Shader.Find("Hidden/MeshLab/UvOverlay")); owned.Add(material);
            material.SetVector("_UvScaleOffset", new Vector4(.5f, 1, rightHalf ? .5f : 0, 0));
            var copy = GpuReadback.Read(texture, 16, 16, hdr: false, material: material);
            Assert.NotNull(copy); owned.Add(copy);
            foreach (var color in copy.GetPixels()) {
                Assert.Greater(rightHalf ? color.g : color.r, .8f);
                Assert.Less(rightHalf ? color.r : color.g, .1f);
            }
        }

        [UnityTest]
        public IEnumerator SharedMeshInstancesRenderTheirOwnLightmapLayers()
        {
            var first = Standalone("First"); var second = Standalone("Second");
            var mesh = first.GetComponent<MeshFilter>().sharedMesh; second.GetComponent<MeshFilter>().sharedMesh = mesh;
            mesh.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up };
            mesh.RecalculateNormals();
            var red = new Texture2D(8, 8); var green = new Texture2D(8, 8); owned.Add(red); owned.Add(green);
            var reds = new Color[64]; var greens = new Color[64];
            for (int i = 0; i < 64; ++i) { reds[i] = Color.red; greens[i] = Color.green; }
            red.SetPixels(reds); red.Apply(); green.SetPixels(greens); green.Apply();
            var previousLightmaps = LightmapSettings.lightmaps;
            var context = new UvToolContext();
            var entries = new List<MeshEntry> { new MeshEntry { originalMesh = mesh, renderer = first }, new MeshEntry { originalMesh = mesh, renderer = second } };
            var items = new List<MeshViewport3D.Item> { new MeshViewport3D.Item(mesh, Matrix4x4.Translate(Vector3.left * 1.5f)),
                new MeshViewport3D.Item(mesh, Matrix4x4.Translate(Vector3.right * .5f)) };
            var canvas = new UvCanvasView { CurrentPreviewMode = UvCanvasView.PreviewMode.Lightmap, FillHidden = true, ShowBorder = false, ShowWireframe = false };
            canvas.Init();
            using var layer = new UvLayer3D(); using var view = new MeshViewport3D { ViewProjection = MeshViewport3D.Projection.XY, ShowGrid = false, ShowAxes = false };
            var window = OpenSpotInputWindow();
            int redPixels = 0, greenPixels = 0;
            try {
                LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = red }, new LightmapData { lightmapColor = green } };
                first.lightmapIndex = 0; second.lightmapIndex = 1;
                first.lightmapScaleOffset = second.lightmapScaleOffset = new Vector4(1, 1, 0, 0);
                view.Frame(new Bounds(new Vector3(0, .5f, 0), new Vector3(3, 1, .1f)));
                window.Input = current => {
                    if (current.type != EventType.Repaint) return;
                    view.Draw(new Rect(0, 0, 256, 128), items, overlay: viewport => layer.Draw(viewport, canvas, context, items, entries));
                    var frame = Get<RenderTexture>(view, "offscreen");
                    var copy = GpuReadback.Read(frame, 256, 128, hdr: false);
                    if (!copy) return;
                    try {
                        redPixels = greenPixels = 0;
                        foreach (var pixel in copy.GetPixels()) {
                            if (pixel.r > .5f && pixel.g < .2f && pixel.b < .2f) ++redPixels;
                            if (pixel.g > .5f && pixel.r < .2f && pixel.b < .2f) ++greenPixels;
                        }
                    }
                    finally { Object.DestroyImmediate(copy); }
                };
                for (int i = 0; i < 60 && (redPixels < 30 || greenPixels < 30); ++i) { window.Repaint(); yield return null; }
                Assert.Greater(redPixels, 30);
                Assert.Greater(greenPixels, 30, "The second instance must not reuse the first instance's UV-layer texture.");
            }
            finally { window.Input = null; window.Close(); Object.DestroyImmediate(window); canvas.Cleanup(); LightmapSettings.lightmaps = previousLightmaps; }
        }

        object CallWorkflow(string method, params object[] args) => typeof(UvTransferWorkflow).GetMethod(method, Private).Invoke(hub.DiagnosticWorkflow, args);

        static Vector2[] ShiftedUvs(float offset) => new[] { new Vector2(offset, .1f), new Vector2(offset + .1f, .1f),
            new Vector2(offset + .1f, .2f), new Vector2(offset, .2f) };

        static void SetStageMesh(MeshEntry entry, string stage, Mesh mesh)
        {
            if (stage == "repack") entry.repackedMesh = mesh;
            else if (stage == "transfer") entry.transferredMesh = mesh;
            else entry.originalMesh = mesh;
        }

        void SetPreviewFrame()
        {
            var viewport = Get<MeshViewport3D>(hub, "viewport"); viewport.Frame(new Bounds(Vector3.zero, Vector3.one));
            ChooseCameraFrame(viewport);
            var canvas = Get<UvCanvasView>(hub, "canvas"); canvas.Zoom = 3; canvas.Pan = new Vector2(87, -31);
        }

        void AssertPreviewFrame()
        {
            AssertCameraFrame(Get<MeshViewport3D>(hub, "viewport"));
            var canvas = Get<UvCanvasView>(hub, "canvas"); Assert.AreEqual(3, canvas.Zoom); Assert.AreEqual(new Vector2(87, -31), canvas.Pan);
        }

        void AssertFreshPreview(UvToolContext ctx, MeshEntry entry, UvCanvasView.PreviewMode mode)
        {
            Assert.AreEqual(mode, Get<UvCanvasView>(hub, "canvas").CurrentPreviewMode);
            var mesh = ctx.DMesh(entry); var shown = entry.meshFilter.sharedMesh;
            CollectionAssert.AreEqual(mesh.uv2, shown.uv2, "The scene preview must use the current atlas.");
            if (mode == UvCanvasView.PreviewMode.Checker) Assert.AreSame(mesh, shown);
            if (mode == UvCanvasView.PreviewMode.Lightmap) {
                var so = entry.renderer.lightmapScaleOffset; var expected = mesh.uv2;
                for (int i = 0; i < expected.Length; ++i) expected[i] = new Vector2(expected[i].x * so.x + so.z, expected[i].y * so.y + so.w);
                CollectionAssert.AreEqual(expected, shown.uv);
            }
            var items = Get<List<MeshViewport3D.Item>>(hub, "viewportItems");
            var item = items.Find(value => value.mesh == mesh);
            Assert.AreSame(mesh, item.mesh, "The window preview and scene preview must use the same canonical data.");
            Assert.AreSame(entry.renderer.sharedMaterial, item.materials[0], "The same frame must not retain a destroyed preview material.");
            Assert.IsTrue(item.materials[0]);
        }

        [UnityTest]
        public IEnumerator ThreeDSpotUsesTheCurrentInputRect()
        {
            var group = Group(lod1HasUv1: true, out _, out _);
            group.transform.position = new Vector3(60, -13, 42);
            var ctx = Open(group);
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            var canvas = Get<UvCanvasView>(hub, "canvas");
            var items = Get<List<MeshViewport3D.Item>>(hub, "viewportItems");
            var entries = Get<List<MeshEntry>>(hub, "viewportEntries");
            var mesh = items[0].mesh;
            Call(hub, "SetCanvas3D", true);
            canvas.SpotMode = true;
            Set(viewport, "orbit", Vector2.zero);
            viewport.Frame(mesh.bounds);
            // A Layout pass or resize can leave a different rect from the input event.
            Set(viewport, "currentRect", new Rect(0, 0, 1, 1));
            long key = ((long)mesh.GetInstanceID() << 8) | (uint)ctx.PreviewUvChannel;
            ctx.PreviewShellDataCache[key] = UvTopology.BuildShellData(mesh.uv2, mesh.triangles);
            ctx.PreviewBvhCache[mesh.GetInstanceID()] = new TriangleBvh(mesh.vertices, mesh.triangles);

            var rect = new Rect(20, 30, 200, 100);
            var inputWindow = OpenSpotInputWindow();
            try {
                yield return null;
                foreach (var eventType in new[] { EventType.MouseMove, EventType.MouseDown }) {
                    Set(viewport, "currentRect", new Rect(0, 0, 1, 1));
                    bool handled = false;
                    inputWindow.Input = input => {
                        if (input.type != eventType) return;
                        handled = true;
                        Call(hub, "HandleViewportSpot", rect, input);
                        if (eventType == EventType.MouseDown)
                            Assert.That(input.type, Is.EqualTo(EventType.Used), "selection precedes camera orbit input");
                    };
                    inputWindow.SendEvent(new Event { type = eventType, mousePosition = rect.center, button = 0, clickCount = 1 });
                    Assert.That(handled, Is.True, "input must reach an actual OnGUI callback");
                    Assert.That(canvas.HasHoveredShell, Is.True, "Spot must use the rect of this input event");
                    Assert.That(canvas.HoverHitValid, Is.True);
                    Assert.That(canvas.HoveredShell.meshEntry, Is.SameAs(entries[0]));
                    Assert.That(canvas.HasSelectedShell, Is.EqualTo(eventType == EventType.MouseDown));
                }
            }
            finally { inputWindow.Close(); }
        }

        [Test]
        public void LayoutKeepsTheRectForPendingSpotPicking()
        {
            using (var viewport = new MeshViewport3D()) {
                var rect = new Rect(20, 30, 200, 100);
                viewport.PrepareRect(rect, EventType.Repaint);
                viewport.PrepareRect(new Rect(0, 0, 1, 1), EventType.Layout);
                Assert.That(viewport.LastRect, Is.EqualTo(rect));
                Assert.That(viewport.TryScreenRay(rect.center, out _, out _), Is.True);
            }
        }

        [UnityTest]
        public IEnumerator ThreeDSpotCompletesTheFirstClickAfterPreviewPreparation()
        {
            var ctx = Open(Group(lod1HasUv1: true, out _, out _));
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            var canvas = Get<UvCanvasView>(hub, "canvas");
            var items = Get<List<MeshViewport3D.Item>>(hub, "viewportItems");
            Call(hub, "SetCanvas3D", true);
            canvas.SpotMode = true;
            Set(viewport, "orbit", Vector2.zero);
            viewport.Frame(items[0].mesh.bounds);
            var rect = new Rect(20, 30, 200, 100);
            var inputWindow = OpenSpotInputWindow();
            try {
                yield return null;
                bool handled = false;
                inputWindow.Input = input => {
                    if (input.type != EventType.MouseDown) return;
                    handled = true; Call(hub, "HandleViewportSpot", rect, input);
                };
                inputWindow.SendEvent(new Event { type = EventType.MouseDown, mousePosition = rect.center, button = 0, clickCount = 1 });
                Assert.That(handled, Is.True, "input must reach an actual OnGUI callback");
                Assert.That(canvas.HasSelectedShell, Is.False, "the first click queues missing preview data");
                viewport.PrepareRect(new Rect(0, 0, 1, 1), EventType.Layout);

                double deadline = UnityEditor.EditorApplication.timeSinceStartup + 10;
                while (!canvas.HasSelectedShell && UnityEditor.EditorApplication.timeSinceStartup < deadline) {
                    canvas.PollPreviewJobs(); yield return null;
                }
                Assert.That(canvas.HasHoveredShell, Is.True);
                Assert.That(canvas.HasSelectedShell, Is.True, "worker completion must finish the click without another mouse event");
                Assert.That(canvas.SelectedShellDebug, Is.Not.Null);
                Assert.That(canvas.SelectedShellDebug.uvChannel, Is.EqualTo(ctx.PreviewUvChannel));
            }
            finally { inputWindow.Close(); }
        }

        [Test]
        public void PickedUvChannelReturnsWhenOnlyAGeneratedMeshCarriesIt()
        {
            // After a repack the source LOD's UV1 lives on repackedMesh, not on the original:
            // the return to UV1 must be judged on the mesh the context displays at UV1.
            var group = Group(lod1HasUv1: false, out var lod0, out var lod1);
            lod0.GetComponent<MeshFilter>().sharedMesh = Quad("Prop_LOD0_NoUv1", false);
            var ctx = Open(group);
            ctx.MeshEntries.Find(e => e.lodIndex == 0).repackedMesh = Quad("Prop_LOD0_Repacked", true);
            var canvas = Get<UvCanvasView>(hub, "canvas");
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            Assert.That(ShowsChecker(lod0), Is.True, "UV1 on the repacked mesh is enough for the checker");

            SwitchLod(1);
            Assert.That(ctx.PreviewUvChannel, Is.EqualTo(0));

            SwitchLod(0);
            Assert.That(ctx.PreviewUvChannel, Is.EqualTo(1), "the repacked mesh carries UV1, so the picked channel returns");
            Assert.That(canvas.CurrentPreviewMode, Is.EqualTo(UvCanvasView.PreviewMode.Checker));
            Assert.That(ShowsChecker(lod0), Is.True);
        }

        [Test]
        public void PickedUvChannelReturnsWhenTheLodHasIt()
        {
            var group = Group(lod1HasUv1: false, out var lod0, out var lod1);
            var ctx = Open(group);
            var canvas = Get<UvCanvasView>(hub, "canvas");
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);

            SwitchLod(1);
            Assert.That(ctx.PreviewUvChannel, Is.EqualTo(0), "LOD1 has no UV1: the canvas falls back to a channel it has");
            Assert.That(canvas.CurrentPreviewMode, Is.EqualTo(UvCanvasView.PreviewMode.Checker));
            Assert.That(ShowsChecker(lod1), Is.True);

            SwitchLod(0);
            Assert.That(ctx.PreviewUvChannel, Is.EqualTo(1), "back on LOD0 the channel the user picked returns");
            Assert.That(ShowsChecker(lod0), Is.True);
            Assert.That(ShowsChecker(lod1), Is.False);
        }
    }
}
