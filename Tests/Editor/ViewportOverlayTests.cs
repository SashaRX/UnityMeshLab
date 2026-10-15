using System.Collections;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ViewportOverlayTests
    {
        [UnityTest]
        public IEnumerator InspectionColorsSurviveShellFillSelectionAndCacheInvalidation()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires GPU preview rendering.");
            var window = ScriptableObject.CreateInstance<ModelOverlayWindow>();
            var mesh = new Mesh { vertices = new[] { new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0) },
                uv = new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.up}, triangles = new[] {0,1,2,0,2,3} };
            mesh.RecalculateNormals();
            var material = new Material(Shader.Find("Unlit/Color")) {color = new Color(.2f,.7f,.3f)};
            var owner = new GameObject("Shell inspection fixture") {hideFlags = HideFlags.HideAndDontSave};
            var renderer = owner.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            var entry = new MeshEntry {originalMesh = mesh,renderer = renderer,include = true};
            try {
                window.context.PreviewUvChannel = 0; window.context.MeshEntries.Add(entry);
                window.items.Add(new MeshViewport3D.Item(mesh,Matrix4x4.identity,new[] {material})); window.entries.Add(entry);
                window.canvas.FillAlpha = 1; window.view.ViewProjection = MeshViewport3D.Projection.XY;
                window.view.Frame(mesh.bounds); window.Show(); window.position = new Rect(0,0,256,256);
                foreach (var shading in new[] {MeshViewport3D.Shading.Normals,MeshViewport3D.Shading.UV0,MeshViewport3D.Shading.Albedo}) {
                    // Exercise the layer's own priority, including the frame where the
                    // toolbar has changed viewport shading before the canvas syncs it.
                    window.canvas.InspectionShading = shading;
                    window.canvas.CurrentPreviewMode = UvCanvasView.PreviewMode.Off;
                    window.canvas.SetFillModes(new List<UvCanvasView.FillModeEntry> {new UvCanvasView.FillModeEntry {name = "None"}});
                    window.layer.Invalidate();
                    for (int frame = 0; frame < 40; ++frame) {window.Repaint(); yield return null;}
                    var expected = Capture(window,window.view,null);
                    foreach (var preview in new[] {UvCanvasView.PreviewMode.Off,UvCanvasView.PreviewMode.Shells3D}) {
                        window.canvas.SetFillModes(new List<UvCanvasView.FillModeEntry> {new UvCanvasView.FillModeEntry {
                            name = "Shells", shellColors = true,
                            drawCallback = (c,x,y,s,m,e) => c.GlFillSh(window.context,x,y,s,m,2,4,e,-1,-1)}});
                        window.canvas.CurrentPreviewMode = preview; window.layer.Invalidate();
                        for (int frame = 0; frame < 40; ++frame) {window.Repaint(); yield return null;}
                        var actual = Capture(window,window.view,null);
                        Assert.That(actual.r,Is.EqualTo(expected.r).Within(.035f),shading + " / " + preview);
                        Assert.That(actual.g,Is.EqualTo(expected.g).Within(.035f),shading + " / " + preview);
                        Assert.That(actual.b,Is.EqualTo(expected.b).Within(.035f),shading + " / " + preview);
                    }
                }
            }
            finally {window.Close(); Object.DestroyImmediate(owner); Object.DestroyImmediate(material); Object.DestroyImmediate(mesh);}
        }

        [UnityTest]
        public IEnumerator ImportedModelUvFillMatchesTheAuthoredSurfaceAcrossLods()
        {
            string path = System.Environment.GetEnvironmentVariable("MESHLAB_VIEWPORT_MODEL");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("Supply an imported model fixture through MESHLAB_VIEWPORT_MODEL.");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires GPU preview rendering.");
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(source);
            var shader = Shader.Find("_MSC_2035/Environment_Master");
            Assert.IsNotNull(shader, "The fixture must include its authored shader.");
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader));
            var root = Object.Instantiate(source);
            var window = ScriptableObject.CreateInstance<ModelOverlayWindow>();
            var material = new Material(shader);
            var meshes = new List<Mesh>();
            try {
                root.hideFlags = HideFlags.HideAndDontSave;
                material.SetFloat("_VPOOffsetFar", .02f);
                var group = root.GetComponentInChildren<LODGroup>();
                Assert.IsNotNull(group);
                var lods = group.GetLODs();
                Assert.Greater(lods.Length, 1);
                window.Show(); window.position = new Rect(0,0,800,600);
                for (int lod = 0; lod < lods.Length; ++lod) {
                    SetModelLod(window, lods[lod], lod, root.transform, material, meshes);
                    Assert.Greater(window.items.Count, 0);
                    window.context.PreviewLod = lod;
                    for (int frame = 0; frame < 60; ++frame) { window.Repaint(); yield return null; }
                    Capture(window, window.view, "bench-lod" + lod + ".png");
                    var target = (RenderTexture)typeof(MeshViewport3D).GetField("offscreen", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window.view);
                    var pixels = GpuReadback.Read(target, target.width, target.height, hdr: false);
                    Assert.IsNotNull(pixels);
                    try {
                        Assert.Greater(PaintedPixels(pixels), 2000, "UV fill must remain visible on LOD" + lod);
                    }
                    finally { Object.DestroyImmediate(pixels); }
                }
                Assert.AreEqual(.02f, material.GetFloat("_VPOOffsetFar"));
            }
            finally { window.Close(); foreach (var mesh in meshes) Object.DestroyImmediate(mesh); Object.DestroyImmediate(material); Object.DestroyImmediate(root); }
        }

        static void SetModelLod(ModelOverlayWindow window, LOD level, int lod, Transform root, Material material, List<Mesh> meshes)
        {
            window.items.Clear(); window.entries.Clear(); window.layer.Invalidate();
            foreach (var renderer in level.renderers) {
                var filter = renderer.GetComponent<MeshFilter>();
                if (!filter || !filter.sharedMesh) continue;
                var mesh = MeshAccess.ReadableCopy(filter.sharedMesh); meshes.Add(mesh);
                var materials = new Material[mesh.subMeshCount];
                for (int sub = 0; sub < materials.Length; ++sub) materials[sub] = material;
                window.items.Add(new MeshViewport3D.Item(mesh, PreviewSpace.LocalToRoot(renderer.transform, root), materials));
                window.entries.Add(new MeshEntry { originalMesh = mesh, renderer = renderer, include = true, lodIndex = lod });
            }
        }

        static int PaintedPixels(Texture2D pixels)
        {
            int painted = 0;
            foreach (var pixel in pixels.GetPixels())
                if (Mathf.Max(pixel.r, pixel.g, pixel.b) - Mathf.Min(pixel.r, pixel.g, pixel.b) > .18f) ++painted;
            return painted;
        }

        [UnityTest]
        public IEnumerator UvFillStaysOnTheSurfaceWithCameraDirectedMaterialOffsets()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires GPU preview rendering.");
            var window = ScriptableObject.CreateInstance<OverlayWindow>();
            var material = new Material(Shader.Find("Hidden/MeshLab/Tests/ViewerVertexOffset"));
            var overlay = new Material(Shader.Find("Hidden/MeshLab/UvOverlay"));
            try {
                material.SetFloat("_VPOOffsetFar", .02f);
                window.materials = new[] { material }; window.hidePatch = true;
                window.uvOverlay = overlay;
                window.Show(); window.position = new Rect(0,0,256,256);
                yield return null;
                var pixel = Capture(window, "uv-material-offset.png");
                Assert.Greater(pixel.r, .9f, "The UV fill must cover the surface, instead of being hidden behind the material's vertex offset.");
                Assert.Less(pixel.b, .1f);
                Assert.AreEqual(.02f, material.GetFloat("_VPOOffsetFar"), "The authored material must retain its scene offset.");
                window.uvOverlay = null;
                var surface = Capture(window);
                Assert.Greater(surface.b, .9f, "Returning to the surface must retain the original material colour.");
            }
            finally { window.Close(); Object.DestroyImmediate(material); Object.DestroyImmediate(overlay); }
        }

        [Test]
        public void TranslucentTextureBindingKeepsMaterialTilingAndOffset()
        {
            var material = new Material(Shader.Find("Unlit/Texture"));
            var texture = new Texture2D(2,2);
            try {
                material.mainTexture = texture; material.mainTextureScale = new Vector2(2,.5f);
                material.mainTextureOffset = new Vector2(.3f,-.2f);
                var block = new MaterialPropertyBlock();
                Assert.AreSame(texture,MeshViewport3D.BindSurfaceTexture(material,block));
                Assert.AreEqual(new Vector4(2,.5f,.3f,-.2f),block.GetVector("_UvScaleOffset"));
                Assert.AreSame(texture,block.GetTexture("_MainTex"));
                MeshViewport3D.BindSurfaceTexture(null,block);
                Assert.AreEqual(new Vector4(1,1,0,0),block.GetVector("_UvScaleOffset"));
            }
            finally { Object.DestroyImmediate(material); Object.DestroyImmediate(texture); }
        }

        [UnityTest]
        public IEnumerator ExcessTranslucentMaterialSlotsRenderInOrderWithoutChangingMaterials()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires GPU preview rendering.");
            var window = ScriptableObject.CreateInstance<OverlayWindow>();
            var colors = new[] { Color.red, Color.blue, Color.green };
            var materials = new Material[colors.Length];
            try {
                for (int i = 0; i < materials.Length; ++i) materials[i] = new Material(Shader.Find("Unlit/Color")) { color = colors[i] };
                window.materials = materials; window.hidePatch = true; window.view.SurfaceOpacity = .6f;
                window.Show(); window.position = new Rect(0,0,256,256); yield return null;
                for (int frame = 0; frame < 3; ++frame) {
                    var pixel = Capture(window,"excess-translucent-slots.png");
                    Assert.Greater(pixel.g, pixel.b + .1f); Assert.Greater(pixel.g, pixel.r + .1f);
                    yield return null;
                }
                for (int i = 0; i < materials.Length; ++i) {
                    Assert.AreEqual(colors[i], materials[i].color); Assert.AreEqual(materials[i].shader.renderQueue, materials[i].renderQueue);
                }
            }
            finally { window.Close(); foreach (var material in materials) if (material) Object.DestroyImmediate(material); }
        }

        [UnityTest]
        public IEnumerator PolygonPickingDistinguishesTwoInstancesOfTheSameMesh()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires GPU preview rendering.");
            var window = ScriptableObject.CreateInstance<OverlayWindow>();
            try {
                window.instances = true; window.topology.Mode = MeshTopologyPreview.Element.Polygon;
                window.Show(); window.position = new Rect(0,0,600,300);
                window.view.Frame(new Bounds(Vector3.zero,new Vector3(6,2,.1f)));
                var items = window.Items; window.topology.Prepare(items);
                double deadline = EditorApplication.timeSinceStartup + 10;
                while (window.topology.Data(window.original) == null && EditorApplication.timeSinceStartup < deadline) yield return null;
                Capture(window);
                var pick = typeof(MeshTopologyPreview).GetMethod("Pick",BindingFlags.Instance|BindingFlags.NonPublic);
                for (int item = 0; item < items.Length; ++item) {
                    var world = items[item].matrix.MultiplyPoint3x4(new Vector3(.25f,.2f,0));
                    Assert.IsTrue(window.view.TryProject(world,out var pointer,out _));
                    var hit = (MeshTopologyPreview.Hit)pick.Invoke(window.topology,new object[] {window.view,pointer,items});
                    Assert.AreEqual(item,hit.item); Assert.AreSame(window.original,hit.mesh);
                    Assert.IsTrue(hit.Matches(items[item],item)); Assert.IsFalse(hit.Matches(items[1-item],1-item));
                }
            }
            finally { window.Close(); }
        }

        [UnityTest]
        public IEnumerator SharedInspectionToolbarDrawsEveryElementModeWithoutLayoutErrors()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires GPU preview rendering.");
            var window = ScriptableObject.CreateInstance<OverlayWindow>();
            try {
                window.controls = true; window.Show(); window.position = new Rect(0,0,760,400);
                yield return null;
                foreach (MeshTopologyPreview.Element mode in System.Enum.GetValues(typeof(MeshTopologyPreview.Element))) {
                    window.topology.Mode = mode;
                    Capture(window);
                    for (int i = 0; i < 5; ++i) yield return null;
                    Capture(window, mode == MeshTopologyPreview.Element.Vertex ? "vertex-mode.png" : null);
                }
                Assert.IsNotNull(window.topology.Data(window.original));
                Assert.AreEqual(1, window.topology.Data(window.original).rims.Length);
            }
            finally { window.Close(); }
        }

        [UnityTest]
        public IEnumerator OccludedCapIsVisibleWithXRayOrTransparentSurface()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires GPU preview rendering.");
            var window = ScriptableObject.CreateInstance<OverlayWindow>();
            try {
                window.Show(); window.position = new Rect(0,0,256,256);
                yield return null;
                var hidden = Capture(window);
                Assert.Less(hidden.r - hidden.b, .08f, "Opaque original surface occludes the closing polygon.");
                window.view.XRay = true;
                yield return null;
                var xray = Capture(window, "cap-xray.png");
                Assert.Greater(xray.r - xray.b, .15f, "Closure vertex colours show through foreground polygons.");
                window.view.XRay = false; window.view.SurfaceOpacity = .2f;
                yield return null;
                var transparent = Capture(window, "cap-transparent.png");
                Assert.Greater(transparent.r - transparent.b, .15f);
                window.view.SurfaceOpacity = 1;
                yield return null;
                var solid = Capture(window);
                Assert.Less(solid.r - solid.b, .08f, "Transparent preview cannot change the original material or its depth state.");
            }
            finally { window.Close(); }
        }

        static Color Capture(OverlayWindow window, string file = null)
            => Capture(window, window.view, file);

        static Color Capture(EditorWindow window, MeshViewport3D view, string file)
        {
            window.SendEvent(new Event { type = EventType.Layout });
            window.SendEvent(new Event { type = EventType.Repaint });
            var target = (RenderTexture)typeof(MeshViewport3D).GetField("offscreen", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
            Assert.IsNotNull(target);
            var previous = RenderTexture.active;
            var pixels = new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);
            var descriptor = target.descriptor;
            descriptor.depthBufferBits = 0; descriptor.msaaSamples = 1;
            var resolved = RenderTexture.GetTemporary(descriptor);
            try {
                MeshViewport3D.ResolveFrame(target,resolved); RenderTexture.active=resolved;
                pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0); pixels.Apply();
                string output = System.Environment.GetEnvironmentVariable("MESHLAB_VIEWPORT_OUTPUT");
                if (output != null && file != null) { Directory.CreateDirectory(output); File.WriteAllBytes(Path.Combine(output,file),pixels.EncodeToPNG()); }
                return pixels.GetPixel(target.width/2,target.height/2);
            }
            finally { RenderTexture.active=previous; RenderTexture.ReleaseTemporary(resolved); Object.DestroyImmediate(pixels); }
        }

        public sealed class ModelOverlayWindow : EditorWindow
        {
            internal readonly MeshViewport3D view = new MeshViewport3D { ShowGrid = false, ShowAxes = false };
            internal readonly UvLayer3D layer = new UvLayer3D();
            internal readonly UvCanvasView canvas = new UvCanvasView { CurrentPreviewMode = UvCanvasView.PreviewMode.Shells3D, ShowBorder = false, ShowWireframe = false };
            internal readonly UvToolContext context = new UvToolContext { PreviewUvChannel = 1 };
            internal readonly List<MeshViewport3D.Item> items = new List<MeshViewport3D.Item>();
            internal readonly List<MeshEntry> entries = new List<MeshEntry>();
            void OnEnable() => canvas.Init();
            void OnGUI() => view.Draw(new Rect(0,0,position.width,position.height), items, v => layer.Draw(v,canvas,context,items,entries));
            void OnDisable() { layer.Dispose(); view.Dispose(); canvas.Cleanup(); }
        }

        public sealed class OverlayWindow : EditorWindow
        {
            internal readonly MeshViewport3D view = new MeshViewport3D { ViewProjection = MeshViewport3D.Projection.XY, ShowGrid = false, ShowAxes = false, Lit = false };
            internal readonly MeshTopologyPreview topology = new MeshTopologyPreview { ShowHoles = true };
            internal bool controls;
            internal bool instances;
            internal bool hidePatch;
            internal Material[] materials;
            internal Material uvOverlay;
            internal Mesh original;
            internal MeshViewport3D.Item[] Items => instances ? new[] {
                new MeshViewport3D.Item(original,Matrix4x4.Translate(Vector3.left*2)),
                new MeshViewport3D.Item(original,Matrix4x4.Translate(Vector3.right*2))
            } : new[] {new MeshViewport3D.Item(original,Matrix4x4.identity,materials)};
            Mesh patch;
            void OnEnable()
            {
                original = new Mesh { vertices = new[] { new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0) },triangles=new[] { 0,1,2,0,2,3 } };
                original.RecalculateNormals();
                patch = new Mesh { vertices=new[] { new Vector3(-.7f,-.6f,.4f),new Vector3(.7f,-.6f,.4f),new Vector3(0,.7f,.4f) },triangles=new[] { 0,1,2 },
                    colors=new[] { ViewportHighlight.Cap,ViewportHighlight.Cap,ViewportHighlight.Cap } };
                view.Frame(original.bounds);
            }
            void OnGUI()
            {
                var items = Items;
                if (controls) topology.Toolbar(items, view);
                view.Draw(controls ? GUILayoutUtility.GetRect(100,100,GUILayout.ExpandWidth(true),GUILayout.ExpandHeight(true)) : new Rect(0,0,position.width,position.height),items,
                    v => { if (uvOverlay) v.DrawTextured(original, Matrix4x4.identity, uvOverlay, Texture2D.whiteTexture, Color.red, 0);
                        if (!hidePatch) v.DrawHighlightMesh(patch,Matrix4x4.identity,new Color(1,1,1,.65f),true); if (controls) topology.Draw(v,items); });
            }
            void OnDisable() { topology.Dispose(); view.Dispose(); if (original) Object.DestroyImmediate(original); if (patch) Object.DestroyImmediate(patch); }
        }
    }
}
