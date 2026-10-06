using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshSourceNormalScaleTests
    {
        [TestCase(0f)]
        [TestCase(.5f)]
        [TestCase(2f)]
        public void CapturedStrengthMatchesTheActiveLitShader(float strength)
        {
            RequireGraphics();
            using (var fixture = new Fixture()) {
                var material = fixture.Material(ActiveShader(), strength);
                var source = fixture.Capture(material);
                var surface = source.materials[0];
                Assert.That(surface.normalScale, Is.EqualTo(strength));
                Assert.That(surface.normalFrameMode, Is.EqualTo(ActiveMode()));
                Assert.That(surface.normal.image.normalReadback, Is.True);
                var direction = RemeshBaker.SourceNormal(source, 0, new Vector3(.2f, .3f, .5f), true);
                if (strength == 0) Assert.That(Vector3.Angle(direction, Vector3.forward), Is.LessThan(1f));
                AssertLitDirection(fixture.mesh, material, direction, "constant-" + strength);
            }
        }

        [TestCase(0f)]
        [TestCase(.5f)]
        [TestCase(2f)]
        public void VaryingNormalMapIsFilteredBeforeZReconstructionAndStrength(float strength)
        {
            RequireGraphics();
            using (var fixture = new Fixture(true)) {
                var material = fixture.Material(ActiveShader(), strength);
                // Constant UV=.5 spans the boundary of two contrasting normal
                // texels. The Lit shader filters their packed XY before decoding;
                // a per-texel normalized XYZ snapshot would tilt this direction.
                string baseMap = ActiveMode() == RemeshNormalFrame.Mode.Urp ? "_BaseMap" : "_MainTex";
                material.SetTextureScale(baseMap, Vector2.zero);
                material.SetTextureOffset(baseMap, new Vector2(.5f, .5f));
                var source = fixture.Capture(material);
                Assert.That(source.materials[0].normal.image.normalReadback, Is.True);
                var direction = RemeshBaker.SourceNormal(source, 0, new Vector3(.2f, .3f, .5f), true);
                AssertLitDirection(fixture.mesh, material, direction, "filtered-" + strength);
            }
        }

        [Test]
        public void SharedNormalTextureSharesReadbackButPreservesMaterialStrength()
        {
            RequireGraphics();
            using (var fixture = new Fixture()) {
                var shader = ActiveShader();
                var source = fixture.Capture(fixture.Material(shader, .5f), fixture.Material(shader, 2f), fixture.Material(shader, 2f));
                var first = source.materials[0]; var second = source.materials[1]; var third = source.materials[2];
                Assert.That(first.normal.image, Is.SameAs(second.normal.image));
                Assert.That(second.normal.image, Is.SameAs(third.normal.image));
                Assert.That(first.color.image, Is.SameAs(second.color.image));
                Assert.That(second.color.image, Is.SameAs(third.color.image));
                Assert.That(first.normal.image.normalReadback && second.normal.image.normalReadback, Is.True);
                Assert.That(first.color.image.normalReadback, Is.False);
                Assert.That(first.normalScale, Is.EqualTo(.5f)); Assert.That(second.normalScale, Is.EqualTo(2f));
                var bary = new Vector3(.2f, .3f, .5f);
                Assert.That(Vector3.Angle(RemeshBaker.SourceNormal(source, 0, bary, true),
                    RemeshBaker.SourceNormal(source, 2, bary, true)), Is.GreaterThan(10f),
                    "Different strengths on a shared image must retain different source directions.");
            }
        }

        [Test]
        public void SharedNormalTextureKeepsStandardAndUrpConventionsSeparate()
        {
            RequireGraphics();
            var standard = Shader.Find("Standard");
            var urp = Shader.Find("Universal Render Pipeline/Lit");
            if (!standard || !urp) Assert.Ignore("Both source shaders are needed for the convention cache fixture.");
            using (var fixture = new Fixture()) {
                var source = fixture.Capture(fixture.Material(standard, 2f), fixture.Material(urp, 2f));
                Assert.That(source.materials[0].normalFrameMode, Is.EqualTo(RemeshNormalFrame.Mode.BuiltIn));
                Assert.That(source.materials[1].normalFrameMode, Is.EqualTo(RemeshNormalFrame.Mode.Urp));
                Assert.That(source.materials[0].normal.image, Is.Not.SameAs(source.materials[1].normal.image));
                Assert.That(source.materials[0].color.image, Is.SameAs(source.materials[1].color.image));
                // Some platform packing branches intentionally use the same Z
                // convention. Cache isolation must not assume their vectors differ.
            }
        }

        [UnityTest]
        public IEnumerator AsyncAndSyncCaptureApplyTheSameStrengthOnce()
        {
            RequireGraphics();
            if (!SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("Actual asynchronous GPU readback is required.");
            using (var fixture = new Fixture()) {
                var shader = ActiveShader();
                var materials = new[] { fixture.Material(shader, 0f), fixture.Material(shader, .5f), fixture.Material(shader, 2f) };
                var sync = fixture.Capture(materials);
                var asynchronous = fixture.Capture(true, materials);
                while (!asynchronous.TextureReadbacks.IsCompleted) yield return null;
                asynchronous.TextureReadbacks.GetAwaiter().GetResult();
                for (int i = 0; i < materials.Length; ++i) {
                    var a = sync.materials[i].normal.image;
                    var b = asynchronous.materials[i].normal.image;
                    Assert.That(a.normalReadback && b.normalReadback, Is.True);
                    Assert.That(b.width, Is.EqualTo(a.width)); Assert.That(b.height, Is.EqualTo(a.height));
                    CollectionAssert.AreEqual(a.pixels, b.pixels, "Sync/async raw normal snapshots must match.");
                    Assert.That(Vector3.Angle(RemeshBaker.SourceNormal(sync, i * 2, Vector3.one / 3f, true),
                        RemeshBaker.SourceNormal(asynchronous, i * 2, Vector3.one / 3f, true)), Is.LessThan(.1f));
                }
            }
        }

        static RemeshNormalFrame.Mode ActiveMode()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (!pipeline) return RemeshNormalFrame.Mode.BuiltIn;
            if (pipeline.GetType().FullName == "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset")
                return RemeshNormalFrame.Mode.Urp;
            Assert.Ignore("The Lit render oracle supports Built-in Standard and URP Lit.");
            return RemeshNormalFrame.Mode.BuiltIn;
        }

        static Shader ActiveShader()
        {
            string name = ActiveMode() == RemeshNormalFrame.Mode.Urp ? "Universal Render Pipeline/Lit" : "Standard";
            var shader = Shader.Find(name);
            if (!shader || !shader.isSupported) Assert.Ignore("The active Lit shader is unavailable: " + name);
            return shader;
        }

        static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("A graphics device is required for source normal-map readback.");
        }

        static float RgbDistance(Color a, Color b) => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b));

        static void AssertLitDirection(Mesh mesh, Material material, Vector3 direction, string captureName)
        {
            // Independent actual-Lit render versus a plain mesh carrying the
            // captured shading normal. No expected packing formula comes from
            // the readback implementation under test.
            var referenceMaterial = new Material(material) { hideFlags = HideFlags.HideAndDontSave };
            var referenceMesh = Object.Instantiate(mesh);
            try {
                referenceMaterial.DisableKeyword("_NORMALMAP");
                referenceMaterial.SetTexture("_BumpMap", null);
                referenceMesh.normals = new[] { direction, direction, direction, direction };
                int lightIndex = 0;
                foreach (var light in new[] { new Vector3(.8f, -.2f, 1), new Vector3(-.5f, .5f, 1) }) {
                    var actual = RemeshNormalFrameTests.RenderMean(mesh, material, light, "normal-strength-" + captureName + "-source-" + lightIndex);
                    var expected = RemeshNormalFrameTests.RenderMean(referenceMesh, referenceMaterial, light, "normal-strength-" + captureName + "-captured-" + lightIndex);
                    ++lightIndex;
                    Assert.That(Mathf.Max(actual.r, actual.g, actual.b), Is.GreaterThan(.025f), "A blank fixture cannot prove source normal strength.");
                    Assert.That(Mathf.Max(expected.r, expected.g, expected.b), Is.GreaterThan(.025f));
                    Assert.That(RgbDistance(actual, expected), Is.LessThan(.025f),
                        "Captured source direction must match actual Lit shading; target=" +
                        EditorUserBuildSettings.activeBuildTarget + ", fixture=" + captureName +
                        ", actual=" + actual + ", expected=" + expected);
                }
            }
            finally { Object.DestroyImmediate(referenceMaterial); Object.DestroyImmediate(referenceMesh); }
        }

        sealed class Fixture : IDisposable
        {
            readonly string path = "Assets/RemeshSourceNormalScale_" + Guid.NewGuid().ToString("N") + ".png";
            readonly List<GameObject> objects = new List<GameObject>();
            readonly List<Material> materials = new List<Material>();
            Texture2D imported, color;
            public Mesh mesh;

            public Fixture(bool varying = false)
            {
                Texture2D writer = null;
                try {
                    writer = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
                    var n = varying ? new Vector3(.99f, 0, Mathf.Sqrt(1 - .99f * .99f)) :
                        new Vector3(.35f, -.2f, Mathf.Sqrt(1 - .35f * .35f - .2f * .2f));
                    var pixels = new Color[16];
                    for (int i = 0; i < pixels.Length; ++i) {
                        var value = varying && i % 4 >= 2 ? Vector3.forward : n;
                        pixels[i] = new Color(value.x * .5f + .5f, value.y * .5f + .5f, value.z * .5f + .5f, 1);
                    }
                    writer.SetPixels(pixels); writer.Apply();
                    File.WriteAllBytes(Path.GetFullPath(path), writer.EncodeToPNG());
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    Assert.IsNotNull(importer);
                    importer.textureType = TextureImporterType.NormalMap;
                    importer.sRGBTexture = false;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.mipmapEnabled = false; importer.filterMode = varying ? FilterMode.Bilinear : FilterMode.Point;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.SaveAndReimport();
                    imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    Assert.IsNotNull(imported);
                    color = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
                    for (int i = 0; i < pixels.Length; ++i) pixels[i] = Color.white;
                    color.SetPixels(pixels); color.Apply();
                    mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                    mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) };
                    mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
                    mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
                    var tangent = new Vector4(1, 0, 0, 1);
                    mesh.tangents = new[] { tangent, tangent, tangent, tangent };
                    mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                    mesh.RecalculateBounds();
                }
                catch { Dispose(); throw; }
                finally { if (writer) Object.DestroyImmediate(writer); }
            }

            public Material Material(Shader shader, float strength)
            {
                bool urp = shader.name == "Universal Render Pipeline/Lit";
                var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                materials.Add(material);
                material.SetTexture(urp ? "_BaseMap" : "_MainTex", color);
                material.SetColor(urp ? "_BaseColor" : "_Color", Color.white);
                material.SetTexture("_BumpMap", imported); material.SetFloat("_BumpScale", strength);
                material.EnableKeyword("_NORMALMAP");
                material.SetFloat("_Metallic", 0); material.SetFloat(urp ? "_Smoothness" : "_Glossiness", 0);
                if (material.HasProperty("_Cull")) material.SetFloat("_Cull", (float)CullMode.Off);
                return material;
            }

            public RemeshSource Capture(params Material[] values) => Capture(false, values);

            public RemeshSource Capture(bool asynchronous, params Material[] values)
            {
                var renderers = new List<Renderer>();
                foreach (var material in values) {
                    var go = new GameObject("RemeshSourceNormalScale fixture") { hideFlags = HideFlags.HideAndDontSave };
                    objects.Add(go);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                    renderers.Add(renderer);
                }
                return RemeshSource.Capture(Matrix4x4.identity, renderers, asyncTextures: asynchronous);
            }

            public void Dispose()
            {
                foreach (var go in objects) if (go) Object.DestroyImmediate(go);
                foreach (var material in materials) if (material) Object.DestroyImmediate(material);
                if (mesh) Object.DestroyImmediate(mesh);
                if (color) Object.DestroyImmediate(color);
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
