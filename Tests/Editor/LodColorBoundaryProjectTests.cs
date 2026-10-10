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
    // Explicit real-model cost study: copied FBX inputs only, no source writes.
    public sealed class LodColorBoundaryProjectTests
    {
        [Serializable] sealed class Report { public string fixture,sourceSha256; public int sourceTriangles,targetTriangles; public List<Row> rows = new List<Row>(); }
        [Serializable] sealed class Row { public string variant; public int triangles; public float colorWeight; public LodScreenValidation.Report screen; public LodAttributeCorrection.VisibleColorBoundaryReport fit; }

        [Test]
        public void CaptureSourceColorCostCandidates()
        {
            var args = Environment.GetCommandLineArgs(); int flag = Array.IndexOf(args,"-meshlabLodColorCostProbes");
            if (flag < 0 || flag+1 >= args.Length) Assert.Ignore("Requires a copied-model dataset through -meshlabLodColorCostProbes.");
            var dataset = JsonUtility.FromJson<LodProjectVisualQualityTests.Dataset>(File.ReadAllText(args[flag+1]));
            Assert.That(LodVisualQualityTests.OutputDirectory(),Is.Not.Null.And.Not.Empty);
            foreach (var model in dataset.cases)
            {
                Assert.That(model.asset,Does.StartWith("Assets/LodProjectCopies/"));
                using var hash = System.Security.Cryptography.SHA256.Create();
                var actual = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(model.asset))).Replace("-","").ToLowerInvariant();
                Assert.That(actual,Is.EqualTo(model.sourceSha256));
                var source = AssetDatabase.LoadAllAssetsAtPath(model.asset).OfType<Mesh>()
                    .Where(m => !m.name.Contains("_COL") && !System.Text.RegularExpressions.Regex.IsMatch(m.name,@"_LOD[1-9]"))
                    .OrderByDescending(LodMeshData.TriangleCount).First();
                Assert.That(source.isReadable,Is.True);
                var sourceColors = source.colors; var positions = source.vertices; var faces = source.triangles;
                var report = new Report { fixture=model.name,sourceSha256=actual,sourceTriangles=LodMeshData.TriangleCount(source),targetTriangles=Mathf.CeilToInt(LodMeshData.TriangleCount(source)/9f) };
                var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
                float scale = 1/Mathf.Max(source.bounds.size.x,Mathf.Max(source.bounds.size.y,source.bounds.size.z));
                var display = Matrix4x4.Rotate(Quaternion.Euler(model.rotation))*Matrix4x4.Scale(Vector3.one*scale)*Matrix4x4.Translate(-source.bounds.center);
                using var renderer = new LodVisualQualityTests.Renderer();
                Capture(source,"source");
                foreach (float weight in new[] { .25f,1,4,16,64 })
                {
                    var result = MeshSimplifier.Simplify(source,new MeshSimplifier.SimplifySettings { targetRatio=1f/9,targetError=1,
                        colorWeight=weight,uvChannel=1,allowAttributeSeamCollapse=true });
                    try
                    {
                        Assert.That(result.ok,Is.True,result.error);
                        string variant = "color-cost-"+weight.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        report.rows.Add(new Row { variant=variant,colorWeight=weight,triangles=result.simplifiedTriCount,screen=screen.Measure(result.simplifiedMesh) });
                        Capture(result.simplifiedMesh,variant);
                        var fitted = LodAttributeCorrection.CorrectVisibleColorBoundaries(source,result.simplifiedMesh,screen,out var fit);
                        try
                        {
                            if (fitted)
                            {
                                report.rows.Add(new Row { variant=variant+"-corners",colorWeight=weight,triangles=LodMeshData.TriangleCount(fitted),screen=screen.Measure(fitted),fit=fit });
                                Capture(fitted,variant+"-corners");
                            }
                        }
                        finally { if (fitted) Object.DestroyImmediate(fitted); }
                    }
                    finally { if (result.simplifiedMesh) Object.DestroyImmediate(result.simplifiedMesh); }
                }
                CollectionAssert.AreEqual(sourceColors,source.colors); CollectionAssert.AreEqual(positions,source.vertices); CollectionAssert.AreEqual(faces,source.triangles);
                File.WriteAllText(Path.Combine(LodVisualQualityTests.OutputDirectory(),model.name+"-color-costs.json"),JsonUtility.ToJson(report,true));
                void Capture(Mesh mesh,string variant)
                {
                    foreach (string view in new[] { "front","oblique","paint-surface" })
                    {
                        renderer.SetView(view == "paint-surface" ? "front" : view);
                        var matrix = view == "paint-surface" ? Matrix4x4.Rotate(Quaternion.Euler(0,180,0))*display : display;
                        foreach (var mode in new[] { LodVisualQualityTests.Mode.Colors,LodVisualQualityTests.Mode.Coverage })
                            LodVisualQualityTests.Save(model.name,variant,view,mode == LodVisualQualityTests.Mode.Colors ? "color" : "coverage",renderer.Draw(mesh,mode,matrix));
                    }
                }
            }
        }
    }
}
