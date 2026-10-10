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
    // Opt-in inspection of authored FBX LODs; never runs the simplifier.
    public sealed class LodOriginalProjectCaptures
    {
        [Serializable] sealed class Report
        {
            public string asset, sourceSha256, preview;
            public List<Row> lods = new List<Row>();
        }
        [Serializable] sealed class Row
        {
            public string name;
            public int triangles, vertices, uv2Vertices, colorVertices;
            public Bounds bounds;
        }

        [Test]
        public void CaptureAuthoredLods()
        {
            var args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args,"-meshlabOriginalLods");
            if (flag < 0 || flag+1 >= args.Length) Assert.Ignore("Requires -meshlabOriginalLods with a copied-model dataset.");
            var dataset = JsonUtility.FromJson<LodProjectVisualQualityTests.Dataset>(File.ReadAllText(args[flag+1]));
            int bakeFlag = Array.IndexOf(args,"-meshlabLodBakedFixture");
            var fixture = bakeFlag >= 0 ? JsonUtility.FromJson<LodSharedLightmapChecks.Fixture>(File.ReadAllText(args[bakeFlag+1])) : null;
            Assert.That(LodVisualQualityTests.OutputDirectory(),Is.Not.Null.And.Not.Empty);
            Directory.CreateDirectory(LodVisualQualityTests.OutputDirectory());
            foreach (var model in dataset.cases) Capture(model,fixture);
        }

        static void Capture(LodProjectVisualQualityTests.Case model,LodSharedLightmapChecks.Fixture fixture)
        {
            Assert.That(model.asset,Does.StartWith("Assets/LodProjectCopies/"));
            using var hash = System.Security.Cryptography.SHA256.Create();
            var actual = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(model.asset))).Replace("-","").ToLowerInvariant();
            Assert.That(actual,Is.EqualTo(model.sourceSha256));
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(model.asset);
            var filters = root.GetComponentsInChildren<MeshFilter>(true)
                .Where(f => f.sharedMesh && System.Text.RegularExpressions.Regex.IsMatch(f.sharedMesh.name,@"_LOD[012]$")).OrderBy(f => f.sharedMesh.name).ToArray();
            Assert.That(filters.Length,Is.EqualTo(3));
            var source = filters.Single(f => f.sharedMesh.name.EndsWith("_LOD0"));
            var sourceToRoot = root.transform.worldToLocalMatrix*source.transform.localToWorldMatrix;
            var bounds = source.sharedMesh.bounds;
            float scale = 1/Mathf.Max(bounds.size.x,Mathf.Max(bounds.size.y,bounds.size.z));
            var display = Matrix4x4.Rotate(Quaternion.Euler(model.rotation))*Matrix4x4.Scale(Vector3.one*scale)*Matrix4x4.Translate(-bounds.center);
            var report = new Report { asset=model.asset,sourceSha256=actual,
                preview="Authored FBX LODs from a readable, uncompressed copy. Gray diagnostic headlight/wire and actual shared Bakery irradiance; original PBR material is not evaluated." };
            Material baked = null;
            try
            {
                if (fixture != null && fixture.asset == model.asset)
                {
                    baked = new Material(Shader.Find("Hidden/MeshLab/Tests/SharedLightmap")) { hideFlags=HideFlags.HideAndDontSave };
                    baked.SetTexture("_BakedLight",AssetDatabase.LoadAssetAtPath<Texture2D>(fixture.color));
                    baked.SetTexture("_BakedDirection",AssetDatabase.LoadAssetAtPath<Texture2D>(fixture.direction));
                    baked.SetVector("_BakedST",fixture.scaleOffset); baked.SetFloat("_Directional",1);
                }
                using var renderer = new LodVisualQualityTests.Renderer();
                foreach (var filter in filters)
                {
                    var mesh = filter.sharedMesh;
                    Assert.That(mesh.isReadable,Is.True);
                    string variant = "original-lod"+mesh.name.Last();
                    report.lods.Add(new Row { name=mesh.name,triangles=LodMeshData.TriangleCount(mesh),vertices=mesh.vertexCount,
                        uv2Vertices=mesh.uv2.Length,colorVertices=mesh.colors.Length,bounds=mesh.bounds });
                    var meshToRoot = root.transform.worldToLocalMatrix*filter.transform.localToWorldMatrix;
                    var transform = display*sourceToRoot.inverse*meshToRoot;
                    if (baked) baked.SetMatrix("_BakedNormalToWorld",(Matrix4x4.Rotate(fixture.rootRotation)*meshToRoot).inverse.transpose);
                    foreach (string view in new[] { "front","oblique" })
                    {
                        renderer.SetView(view);
                        foreach (var mode in new[] { LodVisualQualityTests.Mode.Coverage,LodVisualQualityTests.Mode.Shaded,LodVisualQualityTests.Mode.Wire })
                            LodVisualQualityTests.Save(model.name,variant,view,mode.ToString().ToLowerInvariant(),renderer.Draw(mesh,mode,transform));
                        if (baked) LodVisualQualityTests.Save(model.name,variant,view,"baked-directional",renderer.Draw(mesh,LodVisualQualityTests.Mode.Shaded,transform,overrideMaterial:baked));
                    }
                }
                File.WriteAllText(Path.Combine(LodVisualQualityTests.OutputDirectory(),model.name+"-original-lods.json"),JsonUtility.ToJson(report,true));
            }
            finally { if (baked) Object.DestroyImmediate(baked); }
        }
    }
}
