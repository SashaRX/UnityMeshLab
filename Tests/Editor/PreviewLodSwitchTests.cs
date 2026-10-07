// PreviewLodSwitchTests.cs — the 3D preview follows the preview LOD switched from the
// canvas toolbar: the checker moves to the renderers now shown, the previous LOD gets
// its materials back, and the UV channel the user picked returns when the LOD has it.
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
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
        static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private).Invoke(target, args);
        static bool ShowsChecker(Renderer renderer) => CheckerTexturePreview.IsPreviewShader(renderer.sharedMaterial.shader.name);

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
