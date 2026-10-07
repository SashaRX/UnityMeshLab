using System.Collections;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PreviewLinesTests
    {
        static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("This pixel test requires a graphics device; do not launch Unity with -nographics.");
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        public void RenderTargetReadbackPreservesConstantClear(int samples)
        {
            RequireGraphics();
            var previous = RenderTexture.active;
            var target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { antiAliasing = samples };
            var resolved = new RenderTexture(128, 128, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false, true);
            try {
                target.Create(); resolved.Create(); RenderTexture.active = target;
                GL.Clear(false, true, new Color(.25f, .5f, .75f, 1));
                RenderTexture.active = null;
                MeshViewport3D.ResolveFrame(target, resolved);
                RenderTexture.active = resolved;
                pixels.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); pixels.Apply();
                foreach (var pixel in pixels.GetPixels()) {
                    Assert.That(pixel.r, Is.EqualTo(.25f).Within(.01f));
                    Assert.That(pixel.g, Is.EqualTo(.5f).Within(.01f));
                    Assert.That(pixel.b, Is.EqualTo(.75f).Within(.01f));
                }
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(pixels); target.Release(); Object.DestroyImmediate(target); resolved.Release(); Object.DestroyImmediate(resolved); }
        }

        [Test]
        public void SplitVerticesAndReversedEdgesDoNotDoubleTheCoverage()
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.right, Vector3.zero };
            var data = PreviewLines.Build(p, new[] { 0, 1, 2, 3, 0, 0 }, null);
            Assert.AreEqual(6, data.indices.Length);
            Assert.IsTrue(data.bounds.Contains(Vector3.zero));
            Assert.IsTrue(data.bounds.Contains(Vector3.right), "bounds also contain the shader's other endpoint");
            var colors = new Color32[] { Color.red, Color.red, Color.blue, Color.blue };
            Assert.AreEqual(12, PreviewLines.Build(p, new[] { 0, 1, 2, 3 }, colors).indices.Length,
                "different-colour overlays must not be silently removed");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<System.OperationCanceledException>(() => PreviewLines.Build(p, new[] { 0, 1 }, null, cancellation.Token));
        }

        [UnityTest]
        public IEnumerator CachedRibbonsAreReusedAndReleasedWhenTheSourceDies()
        {
            using var cache = new PreviewLines();
            var source = new Mesh { vertices = new[] { Vector3.zero, Vector3.one } };
            try {
                source.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0);
                Assert.IsNull(cache.Get(source));
                Mesh ribbons = null; double deadline = EditorApplication.timeSinceStartup + 10;
                while (!ribbons && EditorApplication.timeSinceStartup < deadline) { yield return null; ribbons = cache.Get(source); }
                Assert.IsTrue(ribbons);
                Assert.AreSame(ribbons, cache.Get(source));
                Object.DestroyImmediate(source); cache.Prune();
                Assert.IsFalse(ribbons, "destroying the boundary/cage source must release its uploaded ribbons");
            }
            finally { if (source) Object.DestroyImmediate(source); }
        }

        static readonly int[] SampleCounts = { 1, 2, 4 };

        [UnityTest]
        public IEnumerator SlopedLineHasContinuousCoverageAndSubpixelEnergy([ValueSource(nameof(SampleCounts))] int samples)
        {
            using var render = new LineRender(samples);
            yield return null;
            var a = new Vector3(-.8f, -.13f, 2); var b = new Vector3(.8f, .13f, 2);
            var full = render.Draw(a, b, 1);
            yield return null;
            var thin = render.Draw(a, b, .5f);
            float total = 0, half = 0; int partial = 0;
            for (int i = 0; i < full.Length; i++) {
                total += full[i].r; half += thin[i].r;
                if (full[i].r > .05f && full[i].r < .95f) partial++;
            }
            Assert.Greater(partial, 60, "analytic edge coverage exists even without MSAA");
            Assert.That(half / total, Is.EqualTo(.5f).Within(.04f), "subpixel width reduces opacity, not raster coverage");
            for (int x = 17; x < 110; ++x) {
                float column = 0; for (int y = 0; y < 128; y++) column += full[y * 128 + x].r;
                Assert.Greater(column, .3f, "no stippled gaps at column " + x);
            }
        }

        [UnityTest]
        public IEnumerator NearPlaneAndOcclusionDoNotProduceScreenFillingStripes()
        {
            using var render = new LineRender(1);
            render.camera.orthographic = false;
            yield return null;
            var hidden = render.Draw(new Vector3(-1, 0, -.3f), new Vector3(1, 0, -.1f), 1);
            Assert.AreEqual(0, Lit(hidden));
            yield return null;
            var crossing = render.Draw(new Vector3(-.05f, -.1f, -.1f), new Vector3(.2f, .1f, 2), 1);
            Assert.That(Lit(crossing), Is.InRange(5, 1000));
            yield return null;
            var occluded = render.Draw(new Vector3(-.3f, 0, 2), new Vector3(.3f, 0, 2), 1, true);
            Assert.AreEqual(0, Lit(occluded), "depth test must hide lines behind the model");
        }

        static int Lit(Color[] pixels) { int n = 0; foreach (var c in pixels) if (c.r > .01f) n++; return n; }

        [UnityTest]
        public IEnumerator SrpPreviewDoesNotChangeTheAssetOrRequireAMultisampledResolve()
        {
            RequireGraphics();
            var pipeline = GraphicsSettings.currentRenderPipeline;
            var msaa = pipeline?.GetType().GetProperty("msaaSampleCount");
            if (msaa == null) Assert.Ignore("Run this integration test in a URP project.");
            int previous = (int)msaa.GetValue(pipeline);
            var window = ScriptableObject.CreateInstance<LineTestWindow>();
            try {
                window.Show(); window.position = new Rect(0, 0, 256, 256);
                foreach (int samples in new[] { 1, 2, 4 }) {
                    msaa.SetValue(pipeline, samples);
                    yield return null;
                    window.SendEvent(new Event { type = EventType.Layout });
                    window.SendEvent(new Event { type = EventType.Repaint });
                    yield return null;
                    Assert.AreEqual(samples, msaa.GetValue(pipeline), "rendering cannot modify the shared pipeline asset");
                    var target = (RenderTexture)typeof(MeshViewport3D).GetField("offscreen", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window.view);
                    Assert.IsTrue(target.IsCreated());
                    Assert.AreEqual(1, target.antiAliasing, "SRP preview must not import a multisampled depth resolve target");
                }
            }
            finally { msaa.SetValue(pipeline, previous); window.Close(); }
        }

        public sealed class LineTestWindow : EditorWindow
        {
            internal readonly MeshViewport3D view = new MeshViewport3D();
            Mesh mesh;
            void OnEnable()
            {
                mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
                mesh.RecalculateNormals(); view.Frame(mesh.bounds);
            }
            void OnGUI() => view.Draw(new Rect(0, 0, position.width, position.height),
                new[] { new MeshViewport3D.Item(mesh, Matrix4x4.identity) },
                v => v.DrawLines(new[] { Vector3.zero, Vector3.one }, Matrix4x4.identity, Color.red));
            void OnDisable() { view.Dispose(); if (mesh) Object.DestroyImmediate(mesh); }
        }

        sealed class LineRender : System.IDisposable
        {
            readonly GameObject owner;
            readonly Material material, solid;
            readonly RenderTexture target, resolved;
            readonly Texture2D pixels;
            readonly RenderPipelineAsset pipeline, qualityPipeline;
            internal readonly Camera camera;
            internal LineRender(int samples)
            {
                RequireGraphics();
                pipeline = GraphicsSettings.defaultRenderPipeline; qualityPipeline = QualitySettings.renderPipeline;
                GraphicsSettings.defaultRenderPipeline = null; QualitySettings.renderPipeline = null;
                owner = new GameObject("Line coverage camera") { hideFlags = HideFlags.HideAndDontSave };
                camera = owner.AddComponent<Camera>(); camera.enabled = false;
                camera.orthographic = true; camera.orthographicSize = 1;
                camera.nearClipPlane = .1f; camera.farClipPlane = 10; camera.fieldOfView = 60;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 31; camera.allowMSAA = true;
                target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { antiAliasing = samples };
                target.Create(); camera.targetTexture = target;
                resolved = new RenderTexture(128, 128, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear); resolved.Create();
                pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false, true);
                var shader = Shader.Find("Hidden/MeshLab/PreviewLines"); Assert.IsNotNull(shader); Assert.IsTrue(shader.isSupported);
                material = new Material(shader); material.SetVector("_ViewportSize", new Vector4(128, 128, 0, 0));
                solid = new Material(Shader.Find("Hidden/MeshLab/RemeshPreview")); solid.SetColor("_Color", Color.black); solid.SetFloat("_Lit", 0);
            }
            internal Color[] Draw(Vector3 a, Vector3 b, float width, bool occlude = false)
            {
                var mesh = PreviewLines.Build(new[] { a, b }, new[] { 0, 1 }, null).Upload();
                var previous = RenderTexture.active; Mesh plane = null;
                GameObject lineObject = null, planeObject = null;
                try {
                    material.SetFloat("_LineWidth", width);
                    lineObject = RenderObject(mesh, material);
                    if (occlude) {
                        plane = new Mesh { vertices = new[] { new Vector3(-2,-2,1), new Vector3(2,-2,1), new Vector3(-2,2,1), new Vector3(2,2,1) },
                            triangles = new[] { 0, 2, 1, 1, 2, 3 } };
                        plane.RecalculateNormals(); planeObject = RenderObject(plane, solid);
                    }
                    camera.Render();
                    MeshViewport3D.ResolveFrame(target, resolved);
                    RenderTexture.active = resolved;
                    pixels.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); pixels.Apply();
                    return pixels.GetPixels();
                }
                finally {
                    RenderTexture.active = previous;
                    if (lineObject) Object.DestroyImmediate(lineObject);
                    if (planeObject) Object.DestroyImmediate(planeObject);
                    Object.DestroyImmediate(mesh); if (plane) Object.DestroyImmediate(plane);
                }
            }
            static GameObject RenderObject(Mesh mesh, Material material)
            {
                var go = new GameObject("Line coverage object") { layer = 31, hideFlags = HideFlags.HideAndDontSave };
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                return go;
            }
            public void Dispose()
            {
                Object.DestroyImmediate(owner); Object.DestroyImmediate(material); Object.DestroyImmediate(solid);
                target.Release(); resolved.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(resolved); Object.DestroyImmediate(pixels);
                GraphicsSettings.defaultRenderPipeline = pipeline; QualitySettings.renderPipeline = qualityPipeline;
            }
        }
    }
}
