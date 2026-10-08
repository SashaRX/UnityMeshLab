// PreviewLodSwitchTests.cs — the 3D preview follows the preview LOD switched from the
// canvas toolbar: the checker moves to the renderers now shown, the previous LOD gets
// its materials back, and the UV channel the user picked returns when the LOD has it.
using System.Collections;
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

        [TearDown]
        public void Cleanup()
        {
            // Closing the hub restores an active preview; the materials go after it.
            if (hub != null) Object.DestroyImmediate(hub);
            hub = null;
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
            Assert.That(Get<float>(viewport, "radius"), Is.EqualTo(.75f));
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
