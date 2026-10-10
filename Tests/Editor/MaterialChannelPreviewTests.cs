using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class MaterialChannelPreviewTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            foreach (var item in owned) if (item) Object.DestroyImmediate(item);
            owned.Clear();
        }

        Material Standard()
        {
            var material = new Material(Shader.Find("Standard")); owned.Add(material);
            return material;
        }

        Texture2D Pixels(Color color)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            texture.SetPixels(new[] { color, color, color, color }); texture.Apply(); owned.Add(texture);
            return texture;
        }

        [Test]
        public void MissingMapsUseMaterialScalarsAndNeutralNormals()
        {
            var material = Standard(); material.SetFloat("_Metallic", .23f); material.SetFloat("_Glossiness", .67f);
            var metal = MaterialChannelPreview.Read(material, MeshViewport3D.Shading.Metalness);
            var gloss = MaterialChannelPreview.Read(material, MeshViewport3D.Shading.Gloss);
            Assert.IsNull(metal.texture); Assert.That(metal.scalar, Is.EqualTo(.23f).Within(1e-6f));
            Assert.IsNull(gloss.texture); Assert.That(gloss.scalar, Is.EqualTo(.67f).Within(1e-6f));
            Assert.IsNull(MaterialChannelPreview.Read(material, MeshViewport3D.Shading.NormalMap).texture);
            Assert.That(MaterialChannelPreview.Read(material, MeshViewport3D.Shading.AO).scalar, Is.EqualTo(1));
        }

        [Test]
        public void PackedMapsRespectKeywordsBaseTransformAndAlbedoAlphaGloss()
        {
            var material = Standard(); var packed = Pixels(new Color(.2f, .6f, .9f, .35f));
            material.SetTexture("_MetallicGlossMap", packed); material.SetTexture("_OcclusionMap", packed);
            material.SetFloat("_GlossMapScale", .8f); material.SetFloat("_OcclusionStrength", .5f);
            material.SetTextureScale("_MainTex", new Vector2(2, 3)); material.SetTextureOffset("_MainTex", new Vector2(.2f, .3f));
            material.SetTextureScale("_MetallicGlossMap", Vector2.one * 7);
            Assert.IsNull(MaterialChannelPreview.Read(material, MeshViewport3D.Shading.Metalness).texture, "Disabled metallic map uses scalar");
            material.EnableKeyword("_METALLICGLOSSMAP");
            var gloss = MaterialChannelPreview.Read(material, MeshViewport3D.Shading.Gloss);
            Assert.AreSame(packed, gloss.texture); Assert.AreEqual(3, gloss.channel); Assert.AreEqual(.8f, gloss.scalar);
            Assert.AreEqual(new Vector4(2, 3, .2f, .3f), gloss.transform);
            var ao = MaterialChannelPreview.Read(material, MeshViewport3D.Shading.AO);
            Assert.AreEqual(1, ao.channel); Assert.AreEqual(.5f, ao.scalar); Assert.AreEqual(.5f, ao.bias);
            var albedo = Pixels(new Color(.9f, .1f, .7f, .4f)); material.SetTexture("_MainTex", albedo);
            material.EnableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
            gloss = MaterialChannelPreview.Read(material, MeshViewport3D.Shading.Gloss);
            Assert.AreSame(albedo, gloss.texture); Assert.AreEqual(3, gloss.channel);
            Assert.AreSame(packed, material.GetTexture("_MetallicGlossMap"), "Inspection never edits authored material");
        }

        [UnityTest]
        public IEnumerator EveryChannelRendersUnlitAndUpdatesAcrossSubmeshesAndMaterials()
        {
            RequireGraphics();
            var mesh = Quad(); var material = Standard();
            material.SetTexture("_MainTex", Pixels(new Color(.8f, .4f, .2f, .1f)));
            material.SetTexture("_MetallicGlossMap", Pixels(new Color(.2f, .6f, .9f, .35f)));
            material.SetTexture("_OcclusionMap", Pixels(new Color(.9f, .6f, .1f, .2f)));
            material.SetFloat("_GlossMapScale", .8f); material.SetFloat("_OcclusionStrength", .5f);
            material.EnableKeyword("_METALLICGLOSSMAP");
            using var view = new MeshViewport3D { ViewProjection = MeshViewport3D.Projection.XY, ShowAxes = false, ShowGrid = false };
            view.Frame(mesh.bounds);
            var items = new[] { new MeshViewport3D.Item(mesh, Matrix4x4.identity, new[] { material }) };
            var window = ScriptableObject.CreateInstance<ViewportSpotInputWindow>(); window.Show();
            try {
                foreach (var mode in new[] { MeshViewport3D.Shading.Albedo, MeshViewport3D.Shading.NormalMap, MeshViewport3D.Shading.Gloss,
                    MeshViewport3D.Shading.Metalness, MeshViewport3D.Shading.AO }) {
                    view.Mode = mode; Color first = Color.clear, second = Color.clear;
                    yield return Capture(window, view, items, value => first = value);
                    view.Lit = !view.Lit;
                    yield return Capture(window, view, items, value => second = value);
                    AssertColor(second, first, .01f, mode + " must ignore lighting");
                    Color expected = mode == MeshViewport3D.Shading.Albedo ? new Color(.8f, .4f, .2f) :
                        mode == MeshViewport3D.Shading.NormalMap ? new Color(.5f, .5f, 1) :
                        mode == MeshViewport3D.Shading.Gloss ? Color.white * (.35f * .8f) :
                        mode == MeshViewport3D.Shading.Metalness ? Color.white * .2f : Color.white * .8f;
                    // Albedo texture is linear; data channels are deliberately displayed as numeric sRGB.
                    if (mode != MeshViewport3D.Shading.Albedo && QualitySettings.activeColorSpace == ColorSpace.Linear) expected = expected.linear;
                    AssertColor(first, expected, .018f, mode + " selects the correct component");
                }
                view.Mode = MeshViewport3D.Shading.Metalness; view.SurfaceOpacity = .5f; view.Background = Color.black;
                Color translucent = Color.clear;
                yield return Capture(window, view, items, value => translucent = value);
                float metalValue = QualitySettings.activeColorSpace == ColorSpace.Linear ? Mathf.GammaToLinearSpace(.2f) : .2f;
                AssertColor(translucent, Color.white * (metalValue * .5f), .015f, "Opacity must retain the selected material channel");
                view.SurfaceOpacity = 1;
                view.Mode = MeshViewport3D.Shading.Albedo;
                var secondMaterial = Standard(); secondMaterial.SetTexture("_MainTex", Pixels(Color.green));
                mesh.subMeshCount = 2; mesh.SetTriangles(new[] { 0, 1, 2 }, 0); mesh.SetTriangles(new[] { 0, 2, 3 }, 1);
                items[0] = new MeshViewport3D.Item(mesh, Matrix4x4.identity, new[] { material, secondMaterial });
                bool red = false, green = false;
                window.Input = current => {
                    if (current.type != EventType.Repaint) return;
                    view.Draw(new Rect(0, 0, 128, 128), items, null);
                    var pixels = Read(view);
                    try { foreach (var pixel in pixels.GetPixels()) { red |= pixel.r > .6f && pixel.g < .5f; green |= pixel.g > .8f && pixel.r < .1f; } }
                    finally { Object.DestroyImmediate(pixels); }
                };
                for (int i = 0; i < 30 && !(red && green); ++i) { window.Repaint(); yield return null; }
                Assert.IsTrue(red && green, "Queued submeshes must retain independent material maps");
                Assert.AreEqual(MeshViewport3D.Shading.Albedo, view.Mode);
            }
            finally { window.Input = null; window.Close(); Object.DestroyImmediate(window); }
        }

        [UnityTest]
        public IEnumerator UrpAlbedoMatchesUnlitAndPackedChannelsUseSmoothness()
        {
            RequireGraphics();
            var litShader = Shader.Find("Universal Render Pipeline/Lit");
            if (!litShader || GraphicsSettings.currentRenderPipeline == null) Assert.Ignore("Requires an active URP project");
            var material = new Material(litShader); owned.Add(material);
            var control = new Material(Shader.Find("Universal Render Pipeline/Unlit")); owned.Add(control);
            var albedo = Pixels(new Color(.8f, .4f, .2f, .1f));
            var tint = new Color(.5f, .75f, .25f, 1);
            foreach (var item in new[] { material, control }) { item.SetTexture("_BaseMap", albedo); item.SetColor("_BaseColor", tint); item.SetFloat("_Cull", (float)CullMode.Off); }
            material.SetTexture("_MetallicGlossMap", Pixels(new Color(.2f, .6f, .9f, .35f)));
            material.SetFloat("_Smoothness", .8f); material.EnableKeyword("_METALLICSPECGLOSSMAP");
            var mesh = Quad();
            using var view = new MeshViewport3D { ViewProjection = MeshViewport3D.Projection.XY, ShowGrid = false, ShowAxes = false };
            view.Frame(mesh.bounds);
            var items = new[] { new MeshViewport3D.Item(mesh, Matrix4x4.identity, new[] { control }) };
            var window = ScriptableObject.CreateInstance<ViewportSpotInputWindow>(); window.Show();
            try {
                Color expected = Color.clear, actual = Color.clear;
                yield return Capture(window, view, items, value => expected = value);
                Assert.Greater(expected.r, .03f, "Control must render under URP");
                items[0] = new MeshViewport3D.Item(mesh, Matrix4x4.identity, new[] { material });
                view.Mode = MeshViewport3D.Shading.Albedo;
                yield return Capture(window, view, items, value => actual = value);
                AssertColor(actual, expected, .015f, "Albedo tint must match URP Unlit in the active colour space");
                foreach (var mode in new[] { MeshViewport3D.Shading.Gloss, MeshViewport3D.Shading.Metalness }) {
                    view.Mode = mode; yield return Capture(window, view, items, value => actual = value);
                    expected = Color.white * (mode == MeshViewport3D.Shading.Gloss ? .35f * .8f : .2f);
                    if (QualitySettings.activeColorSpace == ColorSpace.Linear) expected = expected.linear;
                    AssertColor(actual, expected, .015f, "URP " + mode);
                }
                material.SetFloat("_WorkflowMode", 0); material.SetTexture("_SpecGlossMap", material.GetTexture("_MetallicGlossMap"));
                Assert.AreEqual(0, MaterialChannelPreview.Read(material, MeshViewport3D.Shading.Metalness).scalar, "Specular workflow is nonmetallic");
                Assert.AreSame(material.GetTexture("_SpecGlossMap"), MaterialChannelPreview.Read(material, MeshViewport3D.Shading.Gloss).texture);
            }
            finally { window.Input = null; window.Close(); Object.DestroyImmediate(window); }
        }

        [UnityTest]
        public IEnumerator ImportedNormalMapIsDecodedToCanonicalRgb()
        {
            RequireGraphics();
            string path = "Assets/ViewerNormal_" + Guid.NewGuid().ToString("N") + ".png";
            var canonical = new Color(.75f, .3f, Mathf.Sqrt(.59f) * .5f + .5f, 1);
            var texture = Pixels(canonical);
            System.IO.File.WriteAllBytes(System.IO.Path.GetFullPath(path), texture.EncodeToPNG());
            try {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.NormalMap; importer.sRGBTexture = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed; importer.mipmapEnabled = false;
                importer.SaveAndReimport();
                var material = Standard(); material.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(path)); material.EnableKeyword("_NORMALMAP");
                var mesh = Quad(); var items = new[] { new MeshViewport3D.Item(mesh, Matrix4x4.identity, new[] { material }) };
                using var view = new MeshViewport3D { Mode = MeshViewport3D.Shading.NormalMap, ViewProjection = MeshViewport3D.Projection.XY, ShowGrid = false, ShowAxes = false };
                view.Frame(mesh.bounds);
                var window = ScriptableObject.CreateInstance<ViewportSpotInputWindow>(); window.Show();
                try {
                    Color color = Color.clear; yield return Capture(window, view, items, value => color = value);
                    AssertColor(color, QualitySettings.activeColorSpace == ColorSpace.Linear ? canonical.linear : canonical, .02f, "Imported packed normal must display canonical RGB");
                    var urpShader = Shader.Find("Universal Render Pipeline/Lit");
                    if (urpShader) {
                        var urp = new Material(urpShader); owned.Add(urp);
                        urp.SetTexture("_BumpMap", material.GetTexture("_BumpMap")); urp.EnableKeyword("_NORMALMAP");
                        items[0] = new MeshViewport3D.Item(mesh, Matrix4x4.identity, new[] { urp });
                        yield return Capture(window, view, items, value => color = value);
                        AssertColor(color, QualitySettings.activeColorSpace == ColorSpace.Linear ? canonical.linear : canonical, .02f, "URP imported packed normal must display canonical RGB");
                    }
                }
                finally { window.Input = null; window.Close(); Object.DestroyImmediate(window); }
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        [TestCase(MeshViewport3D.Shading.Albedo)]
        [TestCase(MeshViewport3D.Shading.NormalMap)]
        [TestCase(MeshViewport3D.Shading.Gloss)]
        [TestCase(MeshViewport3D.Shading.Metalness)]
        [TestCase(MeshViewport3D.Shading.AO)]
        public void UvLayoutSamplesOriginalUv0ThroughTheSelectedLayout(MeshViewport3D.Shading mode)
        {
            RequireGraphics();
            var mesh = Quad(); mesh.uv = new[] { Vector2.one * .75f, Vector2.one * .75f, Vector2.one * .75f, Vector2.one * .75f };
            mesh.uv2 = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            var material = Standard(); var texture = Pixels(Color.red); texture.SetPixel(1, 1, Color.green); texture.Apply();
            material.SetTexture("_MainTex", texture);
            var packed = Pixels(Color.red); packed.SetPixel(1, 1, new Color(.2f, .6f, .8f, .35f)); packed.Apply();
            material.SetTexture("_MetallicGlossMap", packed); material.SetTexture("_OcclusionMap", packed);
            material.EnableKeyword("_METALLICGLOSSMAP"); material.SetFloat("_GlossMapScale", .8f); material.SetFloat("_OcclusionStrength", .5f);
            var entry = new MeshEntry { originalMesh = mesh, include = true };
            var canvas = new UvCanvasView { InspectionShading = mode, FillHidden = true, ShowBorder = false };
            canvas.DisplayMaterials[entry] = new[] { material };
            RenderTexture layer = null;
            try {
                canvas.Init(); layer = canvas.RenderUvLayer(new UvToolContext { PreviewUvChannel = 1 }, mesh, entry, null, 64, drawBorders: false);
                var pixels = Read(layer);
                try {
                    Color expected = mode == MeshViewport3D.Shading.Albedo ? Color.green : mode == MeshViewport3D.Shading.NormalMap ? new Color(.5f, .5f, 1) :
                        Color.white * (mode == MeshViewport3D.Shading.Gloss ? .35f * .8f : mode == MeshViewport3D.Shading.Metalness ? .2f : .8f);
                    if (mode != MeshViewport3D.Shading.Albedo && QualitySettings.activeColorSpace == ColorSpace.Linear) expected = expected.linear;
                    AssertColor(pixels.GetPixel(32, 32), expected, .015f, "UV1 layout must sample the material's UV0, not UV1: " + mode);
                }
                finally { Object.DestroyImmediate(pixels); }
            }
            finally { canvas.Cleanup(); if (layer) { layer.Release(); Object.DestroyImmediate(layer); } }
        }

        Mesh Quad()
        {
            var mesh = new Mesh { name = "Viewer channels", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.RecalculateNormals(); owned.Add(mesh); return mesh;
        }

        static IEnumerator Capture(ViewportSpotInputWindow window, MeshViewport3D view, IReadOnlyList<MeshViewport3D.Item> items, Action<Color> result)
        {
            bool captured = false;
            window.Input = current => {
                if (current.type != EventType.Repaint) return;
                view.Draw(new Rect(0, 0, 128, 128), items, null);
                var pixels = Read(view);
                try { result(pixels.GetPixel(64, 64)); captured = true; }
                finally { Object.DestroyImmediate(pixels); }
            };
            for (int i = 0; i < 30 && !captured; ++i) { window.Repaint(); yield return null; }
            window.Input = null; Assert.IsTrue(captured, "Viewer must repaint");
        }

        static Texture2D Read(MeshViewport3D view) => Read((RenderTexture)typeof(MeshViewport3D).GetField("offscreen", Private).GetValue(view));

        static Texture2D Read(RenderTexture image)
        {
            Assert.IsTrue(image);
            var target = RenderTexture.GetTemporary(image.width, image.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var pixels = new Texture2D(image.width, image.height, TextureFormat.RGBAFloat, false, true);
            try { Graphics.Blit(image, target); RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, image.width, image.height), 0, 0); pixels.Apply(); return pixels; }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
        }

        static void AssertColor(Color actual, Color expected, float tolerance, string reason)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(tolerance), reason + " R");
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(tolerance), reason + " G");
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(tolerance), reason + " B");
        }

        static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires a graphics-enabled Editor");
        }
    }
}
