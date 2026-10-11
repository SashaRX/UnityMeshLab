using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    // Opt-in, actual Bakery atlas binding supplied by the read-only scene extractor.
    internal static class LodSharedLightmapChecks
    {
        [Serializable] internal sealed class Fixture
        {
            public string asset, color, direction, colorSha256, directionSha256, scene, hierarchy, sceneSha256;
            public int atlas;
            public Vector4 scaleOffset;
            public Quaternion rootRotation;
        }
        [Serializable] sealed class Report
        {
            public Fixture binding;
            public int sourceTriangles, targetTriangles, atlasWidth, atlasHeight;
            public string textureFormat, preview;
            public LodSharedUvTransfer.Report transfer;
            public List<LodSharedUvTransfer.Report> restrictedTransfers = new List<LodSharedUvTransfer.Report>();
            public LodSharedUvTransfer.Report integratedTransfer;
            public List<View> views = new List<View>();
        }
        [Serializable] sealed class View
        {
            public string name, variant, mode;
            public float rmsShadingError, silhouetteMismatch;
        }
        internal static void Evaluate(string asset,Mesh source,
            List<(string name,Mesh mesh,LodPipelineOps.LodInfo info,double ms)> variants,Matrix4x4 display)
        {
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args,"-meshlabLodBakedFixture");
            if (index < 0 || index+1 >= args.Length) return;
            var fixture = JsonUtility.FromJson<Fixture>(File.ReadAllText(args[index+1]));
            if (fixture.asset != asset) return;
            var light = Texture(fixture.color,fixture.colorSha256);
            var direction = Texture(fixture.direction,fixture.directionSha256);
            // The Windows desktop binding is full HDR. Other encodings need their
            // actual decode parameters instead of silently sampling packed RGBM.
            Assert.That(light.format,Is.EqualTo(TextureFormat.BC6H).Or.EqualTo(TextureFormat.RGBAHalf).Or.EqualTo(TextureFormat.RGBAFloat));
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            var filter = root.GetComponentsInChildren<MeshFilter>(true).Single(f => f.sharedMesh == source);
            var meshToRoot = root.transform.worldToLocalMatrix*filter.transform.localToWorldMatrix;
            var normalToWorld = (Matrix4x4.Rotate(fixture.rootRotation)*meshToRoot).inverse.transpose;
            var shader = Shader.Find("Hidden/MeshLab/Tests/SharedLightmap");
            Assert.That(shader,Is.Not.Null); Assert.That(shader.isSupported,Is.True);
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var report = new Report { binding = fixture,sourceTriangles = LodMeshData.TriangleCount(source),
                targetTriangles = LodMeshData.TriangleCount(variants.Single(v => v.name == "far-lod2").mesh),
                atlasWidth = light.width,atlasHeight = light.height,textureFormat = light.format.ToString(),
                preview = "Actual shared Bakery atlas and ST; linear baked irradiance only, with/without dominant direction. Original PBR material, normal maps, reflection probes and shadowmask direct lights are not evaluated." };
            var far = variants.Single(v => v.name == "far-lod2");
            Mesh transferred = null;
            var restricted = new List<Mesh>();
            try
            {
                var fields = LodLightmapFields.Capture(light,direction,fixture.scaleOffset,normalToWorld);
                Assert.That(fields,Is.Not.Null);
                var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true,allSides:true);
                transferred = LodSharedUvTransfer.Transfer(source,far.mesh,new MeshSimplifier.SimplifySettings { uvChannel = 1 },out report.transfer,screen:screen,bakedFields:fields.fields);
                if (transferred) variants = variants.Concat(new[] { ("uv2-chart",transferred,far.info,0d) }).ToList();
                foreach (int minimum in new[] {4,16})
                {
                    var limited = LodSharedUvTransfer.Transfer(source,far.mesh,new MeshSimplifier.SimplifySettings { uvChannel = 1 },out var fit,screen:screen,bakedFields:fields.fields,minVisiblePixels:minimum);
                    report.restrictedTransfers.Add(fit);
                    if (limited) { restricted.Add(limited); variants = variants.Concat(new[] { ("uv2-min"+minimum,limited,far.info,0d) }).ToList(); }
                }
                var previousMaps = LightmapSettings.lightmaps; var previousMode = LightmapSettings.lightmapsMode;
                var referenceObject = new GameObject("CopiedBakedRendererBinding");
                var generated = new List<Mesh>();
                try
                {
                    var maps = Enumerable.Range(0,fixture.atlas+1).Select(_ => new LightmapData()).ToArray();
                    maps[fixture.atlas] = new LightmapData { lightmapColor = light,lightmapDir = direction };
                    LightmapSettings.lightmaps = maps; LightmapSettings.lightmapsMode = LightmapsMode.CombinedDirectional;
                    var referenceRenderer = referenceObject.AddComponent<MeshRenderer>();
                    referenceObject.transform.rotation = normalToWorld.rotation;
                    referenceRenderer.lightmapIndex = fixture.atlas; referenceRenderer.lightmapScaleOffset = fixture.scaleOffset;
                    var bound = new List<(string name,Mesh mesh,LodPipelineOps.LodInfo info,double ms)>();
                    LodProjectVisualQualityTests.Generate(source,LodReductionMode.Triangles,3,"baked",bound,generated,
                        relaxedFar:true,correctAttributes:true,preserveHardEdges:true,coarsenHardEdgeChains:true,nativeHardEdgeConstraints:true,
                        screenGuided:true,farScreenBudget:true,bakedReference:referenceRenderer);
                    var boundFar = bound.Single(v => v.name == "baked-lod2");
                    report.integratedTransfer = boundFar.info.attributeCorrection.sharedLightmapUv;
                    Assert.That(boundFar.info.attributeCorrection.sharedLightmapUv.accepted,Is.True);
                    var expected = variants.Single(v => v.name == "uv2-min16").mesh;
                    Assert.That(LodMeshData.TriangleCount(boundFar.mesh),Is.EqualTo(LodMeshData.TriangleCount(expected)));
                    Assert.That(LodMeshData.TriangleCount(boundFar.mesh),Is.LessThanOrEqualTo(Mathf.CeilToInt(LodMeshData.TriangleCount(source)/9f)));
                    CollectionAssert.AreEqual(expected.vertices,boundFar.mesh.vertices);
                    CollectionAssert.AreEqual(expected.uv2,boundFar.mesh.uv2);
                    foreach (var variant in bound) Object.DestroyImmediate(variant.mesh);
                    generated.Clear();
                }
                finally
                {
                    foreach (var mesh in generated) if (mesh) Object.DestroyImmediate(mesh);
                    LightmapSettings.lightmaps = previousMaps; LightmapSettings.lightmapsMode = previousMode;
                    Object.DestroyImmediate(referenceObject);
                }
                material.SetTexture("_BakedLight",light); material.SetTexture("_BakedDirection",direction);
                material.SetVector("_BakedST",fixture.scaleOffset); material.SetMatrix("_BakedNormalToWorld",normalToWorld);
                using var renderer = new LodVisualQualityTests.Renderer();
                foreach (string view in new[] { "front","oblique" })
                {
                    renderer.SetView(view);
                    var mask = renderer.Draw(source,LodVisualQualityTests.Mode.Coverage,display);
                    foreach (bool directional in new[] { false,true })
                    {
                        material.SetFloat("_Directional",directional ? 1 : 0);
                        string mode = directional ? "baked-directional" : "baked-color";
                        var reference = renderer.Draw(source,LodVisualQualityTests.Mode.Shaded,display,overrideMaterial:material);
                        foreach (var variant in variants.Where(v => v.name == "source" || v.name == "far-lod2" || v.name.StartsWith("uv2-")))
                        {
                            var pixels = renderer.Draw(variant.mesh,LodVisualQualityTests.Mode.Shaded,display,overrideMaterial:material);
                            var coverage = renderer.Draw(variant.mesh,LodVisualQualityTests.Mode.Coverage,display);
                            var comparison = LodVisualQualityTests.Compare(reference,pixels,mask,coverage,reference,pixels);
                            Assert.That(float.IsNaN(comparison.rmsShadingError) || float.IsInfinity(comparison.rmsShadingError),Is.False);
                            if (variant.name == "source") Assert.That(comparison.rmsShadingError,Is.Zero);
                            report.views.Add(new View { name = view,variant = variant.name,mode = mode,
                                rmsShadingError = comparison.rmsShadingError,silhouetteMismatch = comparison.silhouetteMismatch });
                            LodVisualQualityTests.Save("fire-shield",variant.name,view,mode,pixels);
                            if (variant.name.StartsWith("uv2-")) LodVisualQualityTests.Save("fire-shield",variant.name,view,"coverage",coverage);
                            if (variant.name == "uv2-min16")
                            {
                                LodVisualQualityTests.Save("fire-shield",variant.name,view,"color",renderer.Draw(variant.mesh,LodVisualQualityTests.Mode.Colors,display));
                                LodVisualQualityTests.Save("fire-shield",variant.name,view,"shaded",renderer.Draw(variant.mesh,LodVisualQualityTests.Mode.Shaded,display));
                            }
                        }
                    }
                }
                File.WriteAllText(Path.Combine(LodVisualQualityTests.OutputDirectory(),"fire-shield-shared-lightmap.json"),JsonUtility.ToJson(report,true));
            }
            finally { Object.DestroyImmediate(material); if (transferred) Object.DestroyImmediate(transferred); foreach (var mesh in restricted) Object.DestroyImmediate(mesh); }
        }
        static Texture2D Texture(string path,string expected)
        {
            Assert.That(path,Does.StartWith("Assets/LodProjectCopies/"));
            using var hash = System.Security.Cryptography.SHA256.Create();
            string actual = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant();
            Assert.That(actual,Is.EqualTo(expected),"Copied bake differs from the supplied scene atlas");
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.That(texture,Is.Not.Null,path); return texture;
        }
    }
}
