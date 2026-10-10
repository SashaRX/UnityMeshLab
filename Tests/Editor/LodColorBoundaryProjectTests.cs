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
        [Serializable] internal sealed class Snapshot
        {
            public Vector3[] positions,normals; public Vector4[] tangents; public Color[] colors;
            public Slot[] slots; public Uv[] uvs; public int colorFormat;
        }
        [Serializable] internal sealed class Slot { public int[] indices; }
        [Serializable] internal sealed class Uv { public int dimension; public Vector4[] values; }
        [Serializable] sealed class LostPairs { public List<LostPair> pairs = new List<LostPair>(); }
        [Serializable] sealed class LostPair { public int view,a,b,region; public Color ca,cb; }

        internal static Mesh ReadPaintMesh(string path)
        {
            var data = JsonUtility.FromJson<Snapshot>(File.ReadAllText(path)); var mesh = new Mesh { name="PaintReplay",vertices=data.positions,normals=data.normals,tangents=data.tangents };
            try
            {
                if (data.colorFormat == (int)UnityEngine.Rendering.VertexAttributeFormat.UNorm8) mesh.SetColors(data.colors.Select(c => (Color32)c).ToArray());
                else mesh.colors = data.colors;
                for (int c = 0; c < data.uvs.Length; c++)
                {
                    var uv = data.uvs[c]; if (uv.dimension == 0) continue;
                    if (uv.dimension == 2) mesh.SetUVs(c,uv.values.Select(v => (Vector2)v).ToList());
                    else if (uv.dimension == 3) mesh.SetUVs(c,uv.values.Select(v => (Vector3)v).ToList());
                    else mesh.SetUVs(c,uv.values.ToList());
                }
                mesh.subMeshCount = data.slots.Length;
                for (int s = 0; s < data.slots.Length; s++) mesh.SetTriangles(data.slots[s].indices,s);
                mesh.RecalculateBounds(); return mesh;
            }
            catch { Object.DestroyImmediate(mesh); throw; }
        }

        [Test]
        public void ReplayPaintDiagonals()
        {
            var args=Environment.GetCommandLineArgs(); int flag=Array.IndexOf(args,"-meshlabLodPaintDiagonals");
            if (flag<0 || flag+1>=args.Length) Assert.Ignore("Requires actual mesh snapshots through -meshlabLodPaintDiagonals.");
            var directory=args[flag+1]; var source=ReadPaintMesh(Path.Combine(directory,"tent-colored-source-mesh.json"));
            var reduced=ReadPaintMesh(Path.Combine(directory,"tent-colored-far-lod2-mesh.json")); Mesh result=null;
            try
            {
                var screen=new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
                var settings=new MeshSimplifier.SimplifySettings { uvChannel=1,uv2Weight=20,normalWeight=1,colorWeight=1 };
                result=LodPaintRetriangulation.Correct(source,reduced,screen,settings,out var report);
                var output=LodVisualQualityTests.OutputDirectory(); Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output,"paint-diagonals.json"),JsonUtility.ToJson(new DiagonalReport {
                    correction=report,before=screen.Measure(reduced),after=screen.Measure(result ? result : reduced)
                },true));
                ExportPaintDiagnostic("tent-colored",source,result ? result : reduced);
            }
            finally { if (result) Object.DestroyImmediate(result); Object.DestroyImmediate(reduced); Object.DestroyImmediate(source); }
        }
        [Serializable] sealed class DiagonalReport { public LodPaintRetriangulation.Report correction; public LodScreenValidation.Report before,after; }

        [Test]
        public void ReplayPaintGeometryCandidates()
        {
            var args = Environment.GetCommandLineArgs(); int flag = Array.IndexOf(args,"-meshlabLodPaintReplay");
            if (flag < 0 || flag+1 >= args.Length) Assert.Ignore("Requires cached actual Generate mesh snapshots through -meshlabLodPaintReplay.");
            var directory = args[flag+1]; var source = ReadPaintMesh(Path.Combine(directory,"tent-colored-source-mesh.json"));
            var reduced = ReadPaintMesh(Path.Combine(directory,"tent-colored-far-lod2-mesh.json"));
            var owned = new List<Mesh> { source,reduced };
            try
            {
                var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
                var report = new Report { fixture="tent-colored",sourceTriangles=LodMeshData.TriangleCount(source),targetTriangles=LodMeshData.TriangleCount(reduced) };
                var shape = screen.RefineSilhouette(reduced,out var moves,out var trials); if (shape) owned.Add(shape);
                var variants = new List<(string,Mesh)> { ("baseline",reduced),("silhouette",shape) };
                if (shape)
                    foreach (float blend in new[] { .25f,.5f,.75f })
                    {
                        var mixed = Object.Instantiate(reduced); owned.Add(mixed);
                        var a = reduced.vertices; var b = shape.vertices;
                        mixed.vertices = a.Select((p,i) => Vector3.Lerp(p,b[i],blend)).ToArray(); mixed.RecalculateBounds(); variants.Add(("shape-blend-"+blend,mixed));
                    }
                foreach (var item in variants)
                {
                    if (!item.Item2) continue;
                    var row = new Row { variant=item.Item1,triangles=LodMeshData.TriangleCount(item.Item2),screen=screen.Measure(item.Item2) }; report.rows.Add(row);
                    ExportPaintDiagnostic(item.Item1,source,item.Item2);
                    var fitted = LodAttributeCorrection.PaintFaceBoundaries(source,item.Item2,screen,out var fit);
                    row.fit = fit;
                    if (fitted)
                    {
                        owned.Add(fitted); report.rows.Add(new Row { variant=item.Item1+"-color",triangles=LodMeshData.TriangleCount(fitted),screen=screen.Measure(fitted),fit=fit });
                        ExportPaintDiagnostic(item.Item1+"-color",source,fitted);
                    }
                }
                File.WriteAllText(Path.Combine(LodVisualQualityTests.OutputDirectory(),"paint-replay.json"),JsonUtility.ToJson(report,true));
            }
            finally { foreach (var mesh in owned) if (mesh) Object.DestroyImmediate(mesh); }
        }

        internal static void ExportPaintDiagnostic(string fixture,Mesh source,Mesh reduced)
        {
            var output = LodVisualQualityTests.OutputDirectory();
            foreach (var item in new[] { ("source",source),("far-lod2",reduced) })
            {
                var data = new LodMeshData(item.Item2);
                File.WriteAllText(Path.Combine(output,fixture+"-"+item.Item1+"-mesh.json"),JsonUtility.ToJson(new Snapshot {
                    positions=data.positions,normals=data.normals,tangents=data.tangents,colors=data.colors,
                    colorFormat=(int)item.Item2.GetVertexAttributeFormat(UnityEngine.Rendering.VertexAttribute.Color),
                    slots=Enumerable.Range(0,item.Item2.subMeshCount).Select(s => new Slot { indices=LodMeshData.Triangles(item.Item2,s) }).ToArray(),
                    uvs=Enumerable.Range(0,8).Select(c => new Uv { dimension=data.uvDimensions[c],values=data.uvs[c]?.ToArray() }).ToArray()
                },true));
            }
            var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            var lost = new LostPairs();
            screen.VisitLostPaintPairs(reduced,(view,a,b,ca,cb,region) => lost.pairs.Add(new LostPair { view=view,a=a,b=b,ca=ca,cb=cb,region=region }));
            File.WriteAllText(Path.Combine(output,fixture+"-lost-pairs.json"),JsonUtility.ToJson(lost,true));
            screen.CaptureDiagnostic(reduced,(view,a,ac,b,bc) => {
                Save("source",a,ac); Save("far-lod2",b,bc);
                void Save(string label,Color[] colors,Color[] coverage)
                {
                    var picture = new Texture2D(72,72,TextureFormat.RGBA32,false);
                    try
                    {
                        picture.SetPixels(colors.Select((c,i) => Color.Lerp(Color.black,c,coverage[i].r)).ToArray()); picture.Apply();
                        File.WriteAllBytes(Path.Combine(output,$"{fixture}-{label}-cpu{view}.png"),picture.EncodeToPNG());
                    }
                    finally { Object.DestroyImmediate(picture); }
                }
            });
        }

        internal static void CapturePaintEdges(string fixture,Mesh source,Mesh reduced,LodVisualQualityTests.Renderer renderer)
        {
            if (fixture!="tent-colored") return;
            var args = Environment.GetCommandLineArgs(); int flag = Array.IndexOf(args,"-meshlabLodPaintBaseline");
            if (flag < 0 || flag+1 >= args.Length) return;
            var baseline = ReadPaintMesh(Path.Combine(args[flag+1],fixture+"-far-lod2-mesh.json"));
            var original = ReadPaintMesh(Path.Combine(args[flag+1],fixture+"-source-mesh.json"));
            try
            {
                CollectionAssert.AreEqual(original.vertices,source.vertices); CollectionAssert.AreEqual(original.colors,source.colors);
                CollectionAssert.AreEqual(original.triangles,source.triangles);
                if (fixture == "tent-colored")
                {
                    var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
                    Assert.That(screen.Measure(reduced).colorBoundaryMean,Is.LessThanOrEqualTo(screen.Measure(baseline).colorBoundaryMean-.04f),
                        "Retain the recorded actual tent improvement over the supplied preceding-stage snapshot.");
                }
                renderer.SetView("front"); var size = source.bounds.size; float scale = 1/Mathf.Max(size.x,Mathf.Max(size.y,size.z));
                foreach (var direction in new[] { ("paint-edge",Vector3.right),("paint-angle",new Vector3(-2,1,1).normalized) })
                {
                    var normal = direction.Item2; var horizontal = Vector3.Cross(normal,Vector3.up).normalized; var vertical = Vector3.Cross(horizontal,normal).normalized;
                    var rotation = Matrix4x4.identity; rotation.SetRow(0,horizontal); rotation.SetRow(1,vertical); rotation.SetRow(2,-normal);
                    var matrix = rotation*Matrix4x4.Scale(Vector3.one*scale)*Matrix4x4.Translate(-source.bounds.center);
                    foreach (var item in new[] { ("source",source),("paint-before",baseline),("far-lod2",reduced) })
                        foreach (var mode in new[] { LodVisualQualityTests.Mode.Colors,LodVisualQualityTests.Mode.Coverage,LodVisualQualityTests.Mode.Wire })
                            LodVisualQualityTests.Save(fixture,item.Item1,direction.Item1,mode == LodVisualQualityTests.Mode.Colors ? "color" : mode == LodVisualQualityTests.Mode.Coverage ? "coverage" : "wire",renderer.Draw(item.Item2,mode,matrix));
                }
            }
            finally { Object.DestroyImmediate(baseline); Object.DestroyImmediate(original); }
        }
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
