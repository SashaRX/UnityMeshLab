using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshPreviewLightingTests
    {
        [Test]
        public void ResultMatchesSourcePbrAndRespondsToThePreviewLights()
        {
            RequireGraphics();
            var mesh = Plane();
            var maps = Maps(Vector3.forward, new Color(.01f, .02f, .015f, 1));
            var textures = SourceTextures(maps);
            var source = SourceMaterial(textures, false);
            using (var preview = new RemeshPreview())
            using (var render = new RenderFixture())
            try {
                var data = Data(mesh, maps, textures[0]);
                var result = ResultMaterial(preview, data);
                var front = Vector3.forward;
                var grazing = new Vector3(1, 0, .04f);
                var sourceFront = render.Sample(mesh, Matrix4x4.identity, source, front, "source-front");
                var resultFront = render.Sample(mesh, data.spaceToWorld, result, front, "result-front");
                var sourceGrazing = render.Sample(mesh, Matrix4x4.identity, source, grazing, "source-grazing");
                var resultGrazing = render.Sample(mesh, data.spaceToWorld, result, grazing, "result-grazing");
                AssertColor(resultFront, sourceFront, .025f, "Front lighting must match the source material path");
                AssertColor(resultGrazing, sourceGrazing, .025f, "Moving the preview light must affect both material paths alike");
                Assert.That(RgbDistance(sourceFront, sourceGrazing), Is.GreaterThan(.06f), "The fixture must distinguish lighting from a fixed camera headlight");
                Assert.That(RgbDistance(resultFront, resultGrazing), Is.GreaterThan(.06f), "Result must actually use the preview lights");
            }
            finally { Object.DestroyImmediate(mesh); Object.DestroyImmediate(source); DestroyTextures(textures); }
        }

        [TestCase(.45f, .2f, false)]
        [TestCase(-.45f, -.2f, false)]
        [TestCase(.45f, .2f, true)]
        [TestCase(-.45f, -.2f, true)]
        public void RawBakedNormalMatchesAnImportedNormalMapOnTheActiveTarget(float x, float y, bool mirrored)
        {
            RequireGraphics();
            // Keep the active target unchanged: an Android target in a Windows Editor
            // is exactly where platform normal-map packing needs an actual render test.
            string path = "Assets/RemeshPreviewNormal_" + Guid.NewGuid().ToString("N") + ".png";
            var mesh = Plane();
            var normal = new Vector3(x, y, Mathf.Sqrt(1 - x * x - y * y));
            var maps = Maps(normal, Color.black);
            var textures = SourceTextures(maps);
            Material source = null;
            using (var preview = new RemeshPreview())
            using (var render = new RenderFixture())
            try {
                File.WriteAllBytes(Path.GetFullPath(path), textures[1].EncodeToPNG());
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                Assert.IsNotNull(importer);
                importer.textureType = TextureImporterType.NormalMap;
                importer.sRGBTexture = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.mipmapEnabled = false;
                importer.filterMode = FilterMode.Point;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
                var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                Assert.IsNotNull(imported);
                var sourceMaps = (Texture2D[])textures.Clone();
                sourceMaps[1] = imported;
                source = SourceMaterial(sourceMaps, false);
                if (source.HasProperty("_Cull")) source.SetFloat("_Cull", (float)CullMode.Off);
                var data = Data(mesh, maps, textures[0]);
                data.twoSided = true;
                data.spaceToWorld = Matrix4x4.Scale(mirrored ? new Vector3(-1, 1, 1) : Vector3.one);
                var result = ResultMaterial(preview, data);
                var direction = new Vector3(-.7f, -.25f, 1);
                string capture = "normal-" + (x > 0 ? "positive" : "negative") + (mirrored ? "-mirrored" : "");
                var expected = render.Sample(mesh, data.spaceToWorld, source, direction, capture + "-imported");
                var actual = render.Sample(mesh, data.spaceToWorld, result, direction, capture + "-raw");
                Assert.That(Mathf.Max(expected.r, expected.g, expected.b), Is.GreaterThan(.025f), "A culled/blank fixture cannot prove normal packing parity");
                AssertColor(actual, expected, .035f, "Raw normals and imported NormalMap must produce the same lighting; target=" + EditorUserBuildSettings.activeBuildTarget + ", mirrored=" + mirrored);
                if (result.HasProperty("_Cull")) Assert.That(result.GetFloat("_Cull"), Is.EqualTo((float)CullMode.Off));
            }
            finally {
                AssetDatabase.DeleteAsset(path);
                Object.DestroyImmediate(mesh);
                if (source) Object.DestroyImmediate(source);
                DestroyTextures(textures);
            }
        }

        [Test]
        public void ResultPreservesHdrEmissionAndReleasesItsOwnedCaches()
        {
            var mesh = Plane();
            var firstMaps = Maps(Vector3.forward, new Color(3.5f, .375f, 1.25f, 1));
            var secondMaps = Maps(new Vector3(.3f, 0, Mathf.Sqrt(.91f)), new Color(.125f, 2.25f, .5f, 1));
            var externalBase = Pixels(firstMaps.color, false);
            var preview = new RemeshPreview();
            try {
                var data = Data(mesh, firstMaps, externalBase);
                var first = ResultMaterial(preview, data);
                var oldNormal = (Texture2D)first.GetTexture("_BumpMap");
                var oldMetal = (Texture2D)first.GetTexture("_MetallicGlossMap");
                var oldAo = (Texture2D)first.GetTexture("_OcclusionMap");
                var oldEmission = (Texture2D)first.GetTexture("_EmissionMap");
                Assert.IsTrue(oldNormal); Assert.IsTrue(oldMetal); Assert.IsTrue(oldAo); Assert.IsTrue(oldEmission);
                Assert.That(oldEmission.format, Is.EqualTo(TextureFormat.RGBAHalf).Or.EqualTo(TextureFormat.RGBAFloat));
                AssertColor(oldEmission.GetPixel(0, 0), firstMaps.emission[0], .003f, "Emission must retain linear HDR values");
                Assert.That(oldEmission.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
                Assert.That(first.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
                data.maps = secondMaps;
                var second = ResultMaterial(preview, data);
                Assert.IsFalse(oldNormal); Assert.IsFalse(oldMetal); Assert.IsFalse(oldAo); Assert.IsFalse(oldEmission);
                var normal = (Texture2D)second.GetTexture("_BumpMap");
                var emission = (Texture2D)second.GetTexture("_EmissionMap");
                AssertColor(normal.GetPixel(0, 0), secondMaps.normal[0], 1f / 255, "New maps must replace the normal cache");
                AssertColor(emission.GetPixel(0, 0), secondMaps.emission[0], .003f, "New maps must replace the HDR cache");
                preview.Invalidate();
                Assert.IsFalse(normal); Assert.IsFalse(emission); Assert.IsFalse(second);
                Assert.IsTrue(externalBase, "Preview does not own the pipeline's base-color texture");
                var afterInvalidation = ResultMaterial(preview, data);
                normal = (Texture2D)afterInvalidation.GetTexture("_BumpMap");
                emission = (Texture2D)afterInvalidation.GetTexture("_EmissionMap");
                preview.Dispose();
                Assert.IsFalse(afterInvalidation); Assert.IsFalse(normal); Assert.IsFalse(emission);
                Assert.IsTrue(externalBase);
            }
            finally { preview.Dispose(); Object.DestroyImmediate(mesh); Object.DestroyImmediate(externalBase); }
        }

        [Test]
        public void NormalToggleChangesTheRenderedSurfaceAndRestoresTheCachedMap()
        {
            RequireGraphics();
            var mesh = Plane();
            var maps = Maps(new Vector3(.7f, 0, Mathf.Sqrt(.51f)), Color.black);
            var externalBase = Pixels(maps.color, false);
            using (var preview = new RemeshPreview())
            using (var render = new RenderFixture())
            try {
                var data = Data(mesh, maps, externalBase);
                var material = ResultMaterial(preview, data);
                var cached = material.GetTexture("_BumpMap");
                var direction = new Vector3(.8f, 0, 1);
                var mapped = render.Sample(mesh, data.spaceToWorld, material, direction);
                var field = typeof(RemeshPreview).GetField("bumpMap", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(field);
                field.SetValue(preview, false);
                material = ResultMaterial(preview, data);
                Assert.IsFalse(material.IsKeywordEnabled("_NORMALMAP"));
                Assert.IsNull(material.GetTexture("_BumpMap"));
                var flat = render.Sample(mesh, data.spaceToWorld, material, direction);
                Assert.That(RgbDistance(mapped, flat), Is.GreaterThan(.04f), "The toggle must change shading, not only the material keyword");
                field.SetValue(preview, true);
                material = ResultMaterial(preview, data);
                Assert.IsTrue(material.IsKeywordEnabled("_NORMALMAP"));
                Assert.AreSame(cached, material.GetTexture("_BumpMap"));
                AssertColor(render.Sample(mesh, data.spaceToWorld, material, direction), mapped, .003f, "Re-enabling uses the original baked normal");
            }
            finally { Object.DestroyImmediate(mesh); Object.DestroyImmediate(externalBase); }
        }

        [Test]
        public void BeautyStaysUnlitAndSwitchingBakeModeReplacesTheMaterial()
        {
            RequireGraphics();
            var mesh = Plane();
            var maps = Maps(Vector3.forward, new Color(3, 2, 1, 1));
            maps.beauty = true;
            var externalBase = Pixels(maps.color, false);
            using (var preview = new RemeshPreview())
            using (var render = new RenderFixture())
            try {
                var data = Data(mesh, maps, externalBase);
                var beauty = ResultMaterial(preview, data);
                Assert.AreEqual(RemeshExporter.ResolveShader(true, out _), beauty.shader);
                var front = render.Sample(mesh, data.spaceToWorld, beauty, Vector3.forward);
                var grazing = render.Sample(mesh, data.spaceToWorld, beauty, new Vector3(1, 0, .01f));
                Assert.That(Mathf.Max(front.r, front.g, front.b), Is.GreaterThan(.025f));
                AssertColor(front, grazing, .003f, "Beauty already contains lighting and must not be lit twice");
                maps.beauty = false; // Also exercise a mode change without a new Maps reference.
                var pbr = ResultMaterial(preview, data);
                Assert.IsFalse(beauty);
                Assert.AreEqual(RemeshExporter.ResolveShader(false, out _), pbr.shader);
                Assert.IsTrue(pbr.IsKeywordEnabled("_NORMALMAP"));
                Assert.IsTrue(pbr.GetTexture("_EmissionMap"));
                maps.beauty = true;
                beauty = ResultMaterial(preview, data);
                Assert.IsFalse(pbr);
                Assert.IsFalse(beauty.IsKeywordEnabled("_NORMALMAP"));
            }
            finally { Object.DestroyImmediate(mesh); Object.DestroyImmediate(externalBase); }
        }

        [Test]
        public void UnbakedResultStillUsesThePreviewLights()
        {
            RequireGraphics();
            var mesh = Plane();
            using (var preview = new RemeshPreview())
            using (var render = new RenderFixture())
            try {
                var data = Data(mesh, null, null);
                var material = ResultMaterial(preview, data);
                Assert.AreEqual(RemeshExporter.ResolveShader(false, out _), material.shader);
                Assert.IsFalse(material.IsKeywordEnabled("_NORMALMAP"));
                var front = render.Sample(mesh, data.spaceToWorld, material, Vector3.forward);
                var grazing = render.Sample(mesh, data.spaceToWorld, material, new Vector3(1, 0, .01f));
                Assert.That(RgbDistance(front, grazing), Is.GreaterThan(.06f), "An unbaked Result must share the Source lighting path");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        static RemeshPreview.Data Data(Mesh mesh, RemeshBaker.Maps maps, Texture2D baseColor)
        {
            var data = new RemeshPreview.Data { maps = maps, baseColor = baseColor };
            data.meshes[(int)RemeshPreview.Stage.Result] = mesh;
            return data;
        }

        static Material ResultMaterial(RemeshPreview preview, RemeshPreview.Data data)
        {
            var items = new List<MeshViewport3D.Item>();
            Assert.IsTrue(preview.Fill3D(data, items));
            Assert.AreEqual(1, items.Count);
            Assert.AreEqual(data.spaceToWorld, items[0].matrix);
            Assert.IsNotNull(items[0].materials);
            Assert.IsTrue(items[0].materials[0]);
            return items[0].materials[0];
        }

        static RemeshBaker.Maps Maps(Vector3 normal, Color emission)
        {
            const int size = 16;
            var maps = new RemeshBaker.Maps { size = size, color = new Color32[size * size], normal = new Color32[size * size],
                metal = new Color32[size * size], ao = new Color32[size * size], emission = new Color[size * size] };
            Color32 packed = new Color(normal.x * .5f + .5f, normal.y * .5f + .5f, normal.z * .5f + .5f, 1);
            for (int i = 0; i < maps.color.Length; ++i) {
                maps.color[i] = new Color32(144, 119, 98, 255);
                maps.normal[i] = packed;
                maps.metal[i] = new Color32(51, 0, 0, 115);
                maps.ao[i] = new Color32(255, 255, 255, 255);
                maps.emission[i] = emission;
            }
            return maps;
        }

        static Texture2D Pixels(Color32[] pixels, bool linear)
        {
            return TextureAssets.FromPixels(pixels, 16, 16, linear);
        }

        static Texture2D[] SourceTextures(RemeshBaker.Maps maps)
        {
            var emission = new Texture2D(maps.size, maps.size, TextureFormat.RGBAHalf, false, true) { hideFlags = HideFlags.HideAndDontSave };
            emission.SetPixels(maps.emission); emission.Apply();
            return new[] { Pixels(maps.color, false), Pixels(maps.normal, true), Pixels(maps.metal, true), Pixels(maps.ao, true), emission };
        }

        static Material SourceMaterial(Texture2D[] maps, bool unlit)
        {
            // Independent material setup: do not call ConfigureMaterial under test.
            var shader = RemeshExporter.ResolveShader(unlit, out bool urp);
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetTexture(urp ? "_BaseMap" : "_MainTex", maps[0]);
            if (material.HasProperty(urp ? "_BaseColor" : "_Color")) material.SetColor(urp ? "_BaseColor" : "_Color", Color.white);
            if (unlit) return material;
            material.SetTexture("_BumpMap", maps[1]);
            material.SetTexture("_MetallicGlossMap", maps[2]);
            material.SetTexture("_OcclusionMap", maps[3]);
            material.SetTexture("_EmissionMap", maps[4]);
            material.SetColor("_EmissionColor", Color.white);
            material.SetFloat("_Metallic", 1);
            material.SetFloat(urp ? "_Smoothness" : "_GlossMapScale", 1);
            material.SetFloat("_BumpScale", 1);
            material.SetFloat("_OcclusionStrength", 1);
            material.EnableKeyword("_NORMALMAP"); material.EnableKeyword("_EMISSION");
            material.EnableKeyword(urp ? "_METALLICSPECGLOSSMAP" : "_METALLICGLOSSMAP");
            if (urp) material.EnableKeyword("_OCCLUSIONMAP");
            return material;
        }

        static Mesh Plane()
        {
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) };
            // Both windings keep the reflected fixture visible with a one-sided
            // Standard shader too; their vertex frames are deliberately identical.
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1, 0, 1, 2, 2, 1, 3 };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            var tangent = new Vector4(1, 0, 0, -1);
            mesh.tangents = new[] { tangent, tangent, tangent, tangent };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            mesh.RecalculateBounds();
            return mesh;
        }

        static float RgbDistance(Color a, Color b) => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b));

        static void AssertColor(Color actual, Color expected, float tolerance, string message)
        {
            Assert.That(RgbDistance(actual, expected), Is.LessThanOrEqualTo(tolerance), message + "; expected=" + expected + ", actual=" + actual);
        }

        static void DestroyTextures(Texture2D[] textures)
        {
            foreach (var texture in textures) if (texture) Object.DestroyImmediate(texture);
        }

        static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Lighting render tests need a graphics device; do not run with -nographics.");
        }

        sealed class RenderFixture : IDisposable
        {
            readonly PreviewRenderUtility utility;

            public RenderFixture()
            {
                utility = new PreviewRenderUtility();
                var camera = utility.camera;
                camera.transform.position = new Vector3(0, 0, -3);
                camera.transform.rotation = Quaternion.identity;
                camera.orthographic = true; camera.orthographicSize = 1.2f;
                camera.nearClipPlane = .1f; camera.farClipPlane = 10;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowHDR = true; camera.allowMSAA = false;
                utility.ambientColor = new Color(.025f, .025f, .025f, 1);
                foreach (var light in utility.lights) { light.type = LightType.Directional; light.color = Color.white; light.shadows = LightShadows.None; }
                utility.lights[0].intensity = 1.1f;
                utility.lights[1].intensity = .1f;
                utility.lights[1].transform.rotation = Quaternion.identity;
            }

            public Color Sample(Mesh mesh, Matrix4x4 matrix, Material material, Vector3 lightDirection, string captureName = null)
            {
                utility.lights[0].transform.rotation = Quaternion.LookRotation(lightDirection);
                utility.BeginPreview(new Rect(0, 0, 64, 64), GUIStyle.none);
                Texture image;
                try { utility.DrawMesh(mesh, matrix, material, 0); utility.Render(true); }
                finally { image = utility.EndPreview(); }
                var previous = RenderTexture.active;
                var target = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                var pixels = new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true);
                try {
                    Graphics.Blit(image, target);
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); pixels.Apply();
                    SaveCapture(pixels, captureName);
                    var sum = Color.clear;
                    for (int y = 24; y < 40; ++y) for (int x = 24; x < 40; ++x) sum += pixels.GetPixel(x, y);
                    return sum / 256;
                }
                finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); Object.DestroyImmediate(pixels); }
            }

            static void SaveCapture(Texture2D pixels, string name)
            {
                string output = Environment.GetEnvironmentVariable("MESHLAB_PREVIEW_RENDER_OUTPUT");
                if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(name)) return;
                Directory.CreateDirectory(output);
                var image = new Texture2D(pixels.width, pixels.height, TextureFormat.RGBA32, false);
                try {
                    var colors = pixels.GetPixels();
                    if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                        for (int i = 0; i < colors.Length; ++i) colors[i] = colors[i].gamma;
                    image.SetPixels(colors); image.Apply();
                    File.WriteAllBytes(Path.Combine(output, name + ".png"), image.EncodeToPNG());
                }
                finally { Object.DestroyImmediate(image); }
            }

            public void Dispose() => utility.Cleanup();
        }
    }
}
