using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ViewportOverlayTests
    {
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
        {
            window.SendEvent(new Event { type = EventType.Layout });
            window.SendEvent(new Event { type = EventType.Repaint });
            var target = (RenderTexture)typeof(MeshViewport3D).GetField("offscreen", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window.view);
            Assert.IsNotNull(target);
            var previous = RenderTexture.active;
            var pixels = new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);
            var resolved = RenderTexture.GetTemporary(target.width,target.height,0,RenderTextureFormat.ARGB32);
            try {
                MeshViewport3D.ResolveFrame(target,resolved); RenderTexture.active=resolved;
                pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0); pixels.Apply();
                string output = System.Environment.GetEnvironmentVariable("MESHLAB_VIEWPORT_OUTPUT");
                if (output != null && file != null) { Directory.CreateDirectory(output); File.WriteAllBytes(Path.Combine(output,file),pixels.EncodeToPNG()); }
                return pixels.GetPixel(target.width/2,target.height/2);
            }
            finally { RenderTexture.active=previous; RenderTexture.ReleaseTemporary(resolved); Object.DestroyImmediate(pixels); }
        }

        public sealed class OverlayWindow : EditorWindow
        {
            internal readonly MeshViewport3D view = new MeshViewport3D { ViewProjection = MeshViewport3D.Projection.XY, ShowGrid = false, ShowAxes = false, Lit = false };
            internal readonly MeshTopologyPreview topology = new MeshTopologyPreview { ShowHoles = true };
            internal bool controls;
            internal bool instances;
            internal Mesh original;
            internal MeshViewport3D.Item[] Items => instances ? new[] {
                new MeshViewport3D.Item(original,Matrix4x4.Translate(Vector3.left*2)),
                new MeshViewport3D.Item(original,Matrix4x4.Translate(Vector3.right*2))
            } : new[] {new MeshViewport3D.Item(original,Matrix4x4.identity)};
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
                    v => { v.DrawHighlightMesh(patch,Matrix4x4.identity,new Color(1,1,1,.65f),true); if (controls) topology.Draw(v,items); });
            }
            void OnDisable() { topology.Dispose(); view.Dispose(); if (original) Object.DestroyImmediate(original); if (patch) Object.DestroyImmediate(patch); }
        }
    }
}
