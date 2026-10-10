using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    // GPU tests compare actual rasterized LODs. Optional captures stay outside the
    // package: MESHLAB_LOD_VISUAL_OUTPUT enables reproducible PNG + float readbacks.
    public sealed class LodVisualQualityTests
    {
        internal const int Size = 384;
        readonly List<Mesh> meshes = new List<Mesh>();

        [Serializable]
        public sealed class Capture
        {
            public string variant, view, reductionNote;
            public int triangles, selectedCandidate, candidateCount, coveredPixels;
            public int removedLoops, targetTriangles;
            public bool budgetReached;
            public double simplifyMs;
            public float targetRatio, maxPixelRgbaError, rmsPixelRgbaError, silhouetteMismatch, maxShadingError, rmsShadingError;
            public Vector4 surfaceColorMax, surfaceColorRms;
            public float allowedColorError, targetError, normalWeight, colorWeight;
            public bool budgetPriority, qualityLimitsExceeded;
            public float sourceDistance, normalError;
            public float sourceDistanceRms, normalRms, uvRms, silhouetteMean, silhouetteMax, selectionScore;
            public int nativeProbes;
            public int hardEdges, missingHardEdges, protectedTriangles, missingProtectedTriangles;
            public int patchInterfaces, missingPatchInterfaces, ambiguousFeatureEdges;
            public int coarsenedFeaturePoints, coarsenedFeatureTriangles;
            public bool hardEdgeSourceFallback;
            public bool normalsCorrected, colorsCorrected;
            public string correctionNote;
            public float normalRmsBefore, authoredNormalMaxBefore, authoredNormalMaxAfter;
            public Vector4 colorRmsBefore, colorMaxBefore;
            public int smoothingRegions, linkedNormalDuplicates, mixedSmoothingFaces, missingSmoothingRegions, incompleteSmoothingRegions;
            public List<RegionNormalError> regionNormalsBefore, regionNormalsAfter;
            public List<BudgetCandidate> budgetCandidates;
            public int removedParts, removedPartTris;
            public float removedPartAreaFraction, removedPartMaxPixels;
        }
        [Serializable] public sealed class RegionNormalError
        {
            public int region, samples, unresolved;
            public float rms, maximum;
        }
        [Serializable] public sealed class BudgetCandidate
        {
            public int variant, triangles, nativeProbes;
            public string name;
            public float score, distanceRms, normalRms, colorRms, uvRms, silhouetteMean, silhouetteMax;
        }
        [Serializable]
        public sealed class Report
        {
            public string fixture, unity, gpu, graphicsApi;
            public int resolution;
            public float targetError = .05f, maxColorError = .03f, maxNormalAngle = 15;
            public List<Capture> captures = new List<Capture>();
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var mesh in meshes) if (mesh) Object.DestroyImmediate(mesh);
            meshes.Clear();
        }
        Mesh Track(Mesh mesh) { meshes.Add(mesh); return mesh; }

        [TestCase("gradient")]
        [TestCase("mask")]
        [TestCase("curved")]
        [TestCase("curved-soft")]
        public void ActualLodRendersPreserveFieldsAndProduceReviewableCaptures(string fixture)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("LOD visual tests need a graphics device; omit -nographics.");
            var source = Grid(fixture);
            Assert.That(LodSourceTopology.TryLoad(new MeshEntry { fbxMesh = source }, source,
                new Dictionary<string, object>(), out var topology, out var error), Is.True, error);
            var report = new Report { fixture = fixture, unity = Application.unityVersion, gpu = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), resolution = Size };
            var variants = new List<(string name, Mesh mesh, LodLoopSimplifier.Result result, float ratio, double ms)>
                { ("source", source, null, 1, 0) };
            foreach (int candidates in new[] {1,5}) foreach (int level in new[] {1,2})
            {
                float ratio = level == 1 ? .5f : .25f;
                var watch = Stopwatch.StartNew();
                var reduced = LodLoopSimplifier.Simplify(topology, Settings(ratio,candidates));
                watch.Stop(); Track(reduced.mesh);
                Assert.That(reduced.ok, Is.True,reduced.error);
                variants.Add(($"{(candidates == 1 ? "fast" : "high")}-lod{level}",reduced.mesh,reduced,ratio,watch.Elapsed.TotalMilliseconds));
            }
            if (fixture == "mask")
            {
                var watch = Stopwatch.StartNew();
                var unprotected = MeshSimplifier.Simplify(source,new MeshSimplifier.SimplifySettings { targetRatio = .25f, targetError = .05f });
                watch.Stop(); Track(unprotected.simplifiedMesh);
                Assert.That(unprotected.ok, Is.True,unprotected.error);
                variants.Add(("unchecked-triangles",unprotected.simplifiedMesh,null,.25f,watch.Elapsed.TotalMilliseconds));
            }
            using var renderer = new Renderer();
            foreach (string view in new[] { "front", "oblique" })
            {
                renderer.SetView(view);
                var original = renderer.Draw(source,Mode.Colors);
                var coverage = renderer.Draw(source,Mode.Coverage);
                var shading = renderer.Draw(source,Mode.Shaded);
                foreach (var variant in variants)
                {
                    var colors = renderer.Draw(variant.mesh,Mode.Colors);
                    var mask = renderer.Draw(variant.mesh,Mode.Coverage);
                    var lit = renderer.Draw(variant.mesh,Mode.Shaded);
                    var capture = Compare(original,colors,coverage,mask,shading,lit);
                    capture.variant = variant.name; capture.view = view; capture.triangles = LodMeshData.TriangleCount(variant.mesh);
                    capture.targetRatio = variant.ratio; capture.simplifyMs = variant.ms;
                    capture.targetTriangles = Mathf.CeilToInt(LodMeshData.TriangleCount(source)*variant.ratio);
                    capture.budgetReached = capture.triangles <= capture.targetTriangles;
                    capture.removedLoops = variant.result?.removedLoops ?? 0;
                    capture.selectedCandidate = variant.result?.selectedCandidate ?? 0;
                    capture.candidateCount = variant.result?.evaluatedCandidates ?? 0;
                    capture.surfaceColorMax = variant.result?.metrics.colorMax ?? Vector4.zero;
                    capture.surfaceColorRms = variant.result?.metrics.ColorRms ?? Vector4.zero;
                    report.captures.Add(capture);
                    Save(fixture,variant.name,view,"color",colors);
                    Save(fixture,variant.name,view,"coverage",mask);
                    Save(fixture,variant.name,view,"shaded",lit);
                    Save(fixture,variant.name,view,"alpha",renderer.Draw(variant.mesh,Mode.Alpha));
                    var wire = renderer.Draw(variant.mesh,Mode.Wire);
                    Save(fixture,variant.name,view,"wire",wire);
                    Assert.That(wire.Where((c,index) => Mathf.Abs(c.r-lit[index].r) > .05f).Count(), Is.GreaterThan(250),
                        "The wire capture must actually display mesh edges: " + fixture + "/" + variant.name + "/" + view);
                }
            }
            SaveReport(report);
            foreach (var capture in report.captures.Where(c => c.variant != "unchecked-triangles"))
            {
                string detail = fixture + "/" + capture.variant + "/" + capture.view;
                Assert.That(capture.coveredPixels, Is.GreaterThan(Size*Size/10), "Blank renders cannot prove fidelity: " + detail);
                Assert.That(capture.silhouetteMismatch, Is.LessThan(.03f), "Silhouette changed: " + detail);
                Assert.That(capture.maxPixelRgbaError, Is.LessThan(fixture.StartsWith("curved",StringComparison.Ordinal) ? .10f : .04f), "Rendered field changed: " + detail);
                Assert.That(capture.rmsPixelRgbaError, Is.LessThan(.025f), "Rendered field RMS: " + detail);
                Assert.That(capture.rmsShadingError, Is.LessThan(.06f), "Shading changed: " + detail);
            }
            if (fixture != "curved")
                Assert.That(report.captures.Where(c => c.variant.StartsWith("fast-",StringComparison.Ordinal) || c.variant.StartsWith("high-",StringComparison.Ordinal))
                    .All(c => c.triangles < LodMeshData.TriangleCount(source)), Is.True, "A reduced-fixture test cannot pass merely by keeping LOD0.");
            if (fixture == "mask")
                Assert.That(report.captures.Where(c => c.variant == "unchecked-triangles").Max(c => c.maxPixelRgbaError),
                    Is.GreaterThan(.3f), "The positive control must expose visible mask loss, not merely save matching frames.");
        }

        static LodLoopSimplifier.Settings Settings(float ratio, int candidates) => new LodLoopSimplifier.Settings
        {
            simplify = new MeshSimplifier.SimplifySettings { targetRatio = ratio, targetError = .05f, normalWeight = .15f,
                colorWeight = 1, uv2Weight = .15f, uvChannel = 1 },
            maxNormalAngle = 15, maxColorError = .03f, candidateCount = candidates
        };

        Mesh Grid(string fixture)
        {
            const int n = 6;
            var p = new List<Vector3>(); var uv = new List<Vector2>(); var colors = new List<Color>(); var quads = new List<int>();
            for (int y = 0; y <= n; y++) for (int x = 0; x <= n; x++)
            {
                float u = (float)x/n, v = (float)y/n;
                float amplitude = fixture == "curved" ? .18f : fixture == "curved-soft" ? .05f : 0;
                float z = amplitude*Mathf.Sin(u*Mathf.PI)*Mathf.Sin(v*2*Mathf.PI);
                p.Add(new Vector3(u-.5f,v-.5f,z)); uv.Add(new Vector2(u,v));
                float band = y == n/2 ? 1 : 0;
                colors.Add(fixture == "mask" ? new Color(.08f+.85f*band,.12f+.6f*u,.15f+.7f*(1-band),band) :
                    new Color(.1f+.65f*u,.1f+.65f*v,.1f+.2f*u*v,1));
            }
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            { int a = y*(n+1)+x; quads.AddRange(new[] {a,a+1,a+n+2,a+n+1}); }
            var mesh = Track(new Mesh { name = "LodVisual_"+fixture, vertices = p.ToArray(), uv = uv.ToArray(), uv2 = uv.ToArray(), colors = colors.ToArray() });
            mesh.SetIndices(quads.ToArray(),MeshTopology.Quads,0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        internal static Capture Compare(Color[] source, Color[] target, Color[] sourceMask, Color[] targetMask, Color[] sourceLit, Color[] targetLit)
        {
            var capture = new Capture(); int union = 0, mismatch = 0, interior = 0; double sum = 0, litSum = 0;
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
            {
                int i = y*Size+x; bool a = sourceMask[i].r > .5f, b = targetMask[i].r > .5f;
                if (b) capture.coveredPixels++;
                if (a || b) union++;
                if (a != b) mismatch++;
                // Ignore the 2px rasterization fringe for field/shading metrics.
                if (x < 2 || y < 2 || x >= Size-2 || y >= Size-2 || !a || !b) continue;
                bool inside = true;
                for (int dy = -2; dy <= 2 && inside; dy++) for (int dx = -2; dx <= 2; dx++)
                    if (sourceMask[(y+dy)*Size+x+dx].r < .5f || targetMask[(y+dy)*Size+x+dx].r < .5f) { inside = false; break; }
                if (!inside) continue;
                var delta = (Vector4)source[i]-(Vector4)target[i];
                float max = Mathf.Max(Mathf.Max(Mathf.Abs(delta.x),Mathf.Abs(delta.y)),Mathf.Max(Mathf.Abs(delta.z),Mathf.Abs(delta.w)));
                capture.maxPixelRgbaError = Mathf.Max(capture.maxPixelRgbaError,max); sum += delta.sqrMagnitude/4;
                var ld = (Vector4)sourceLit[i]-(Vector4)targetLit[i]; ld.w = 0;
                capture.maxShadingError = Mathf.Max(capture.maxShadingError,Mathf.Max(Mathf.Abs(ld.x),Mathf.Max(Mathf.Abs(ld.y),Mathf.Abs(ld.z))));
                litSum += ld.sqrMagnitude/3; interior++;
            }
            capture.silhouetteMismatch = union > 0 ? (float)mismatch/union : 1;
            capture.rmsPixelRgbaError = interior > 0 ? (float)Math.Sqrt(sum/interior) : 1;
            capture.rmsShadingError = interior > 0 ? (float)Math.Sqrt(litSum/interior) : 1;
            return capture;
        }

        static void SaveReport(Report report)
        {
            string output = OutputDirectory();
            if (string.IsNullOrEmpty(output)) return;
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output,report.fixture+"-metrics.json"),JsonUtility.ToJson(report,true));
        }
        internal static void Save(string fixture, string variant, string view, string mode, Color[] colors)
        {
            string output = OutputDirectory();
            if (string.IsNullOrEmpty(output)) return;
            Directory.CreateDirectory(output);
            string path = Path.Combine(output,$"{fixture}-{variant}-{view}-{mode}");
            using (var writer = new BinaryWriter(File.Create(path+".rgba")))
            {
                writer.Write(Size); writer.Write(Size);
                foreach (var c in colors) { writer.Write(c.r); writer.Write(c.g); writer.Write(c.b); writer.Write(c.a); }
            }
            var image = new Texture2D(Size,Size,TextureFormat.RGBA32,false);
            try
            {
                // Only the review PNG is display-encoded and opaque. Metrics/raw
                // readbacks remain linear RGBA, including the actual alpha mask.
                image.SetPixels(colors.Select(c => { var display = c.gamma; display.a = 1; return display; }).ToArray()); image.Apply();
                File.WriteAllBytes(path+".png",image.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(image); }
        }

        internal static string OutputDirectory()
        {
            string output = Environment.GetEnvironmentVariable("MESHLAB_LOD_VISUAL_OUTPUT");
            if (!string.IsNullOrEmpty(output)) return output;
            // Support explicit capture destinations in command-line test runs.
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args,"-meshlabLodVisualOutput");
            return index >= 0 && index+1 < args.Length ? args[index+1] : null;
        }

        internal enum Mode { Colors, Coverage, Shaded, Alpha, Wire, Textured }
        internal sealed class Renderer : IDisposable
        {
            readonly PreviewRenderUtility utility;
            readonly Material material, lines;
            internal Renderer()
            {
                var shader = Shader.Find("Hidden/MeshLab/RemeshPreview"); var lineShader = Shader.Find("Hidden/MeshLab/PreviewLines");
                Assert.That(shader, Is.Not.Null); Assert.That(shader.isSupported, Is.True);
                Assert.That(lineShader, Is.Not.Null); Assert.That(lineShader.isSupported, Is.True);
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                lines = new Material(lineShader) { hideFlags = HideFlags.HideAndDontSave };
                lines.SetColor("_Color",new Color(.02f,.07f,.11f,1)); lines.SetFloat("_LineWidth",1);
                lines.SetVector("_ViewportSize",new Vector4(Size,Size,0,0));
                utility = new PreviewRenderUtility();
                utility.camera.orthographic = true; utility.camera.orthographicSize = .72f;
                utility.camera.nearClipPlane = .1f; utility.camera.farClipPlane = 10;
                utility.camera.clearFlags = CameraClearFlags.SolidColor; utility.camera.backgroundColor = new Color(.012f,.016f,.024f,0);
                utility.camera.allowHDR = true; utility.camera.allowMSAA = false;
            }
            internal void SetView(string view)
            {
                utility.camera.transform.position = view == "front" ? new Vector3(0,0,-3) : new Vector3(1.5f,.8f,-3);
                utility.camera.transform.LookAt(Vector3.zero,Vector3.up);
            }
            internal Color[] Draw(Mesh mesh, Mode mode, Matrix4x4? displayTransform = null, Texture albedo = null)
            {
                var matrix = displayTransform ?? Matrix4x4.identity;
                material.SetColor("_Color",mode == Mode.Wire || mode == Mode.Shaded ? new Color(.65f,.72f,.8f,1) : Color.white);
                material.SetFloat("_Lit",mode == Mode.Wire || mode == Mode.Shaded || mode == Mode.Textured ? 1 : 0);
                material.SetFloat("_UseVertexColor",mode == Mode.Colors || mode == Mode.Alpha ? 1 : 0);
                material.SetFloat("_UseTexture",mode == Mode.Textured && albedo ? 1 : 0);
                material.SetTexture("_MainTex",albedo ? albedo : Texture2D.whiteTexture);
                Mesh alphaMesh = null, wire = null;
                var previous = RenderTexture.active;
                var rt = RenderTexture.GetTemporary(Size,Size,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
                var pixels = new Texture2D(Size,Size,TextureFormat.RGBAFloat,false,true);
                try
                {
                    if (mode == Mode.Alpha)
                    {
                        alphaMesh = Object.Instantiate(mesh);
                        alphaMesh.colors = mesh.colors.Select(c => new Color(c.a,c.a,c.a,1)).ToArray();
                    }
                    if (mode == Mode.Wire)
                    {
                        var edges = new List<int>();
                        for (int s = 0; s < mesh.subMeshCount; s++)
                        {
                            var t = LodMeshData.Triangles(mesh,s);
                            for (int i = 0; i < t.Length; i += 3) edges.AddRange(new[] {t[i],t[i+1],t[i+1],t[i+2],t[i+2],t[i]});
                        }
                        wire = PreviewLines.Build(mesh.vertices,edges.ToArray(),null).Upload();
                    }
                    float previewPoints = Size/EditorGUIUtility.pixelsPerPoint;
                    utility.BeginPreview(new Rect(0,0,previewPoints,previewPoints),GUIStyle.none);
                    // PreviewRenderUtility scales its target with editor DPI. The
                    // ribbon shader works in actual target pixels, not GUI points.
                    var previewTarget = utility.camera.targetTexture;
                    Assert.That(previewTarget.width,Is.EqualTo(Size),"GPU measurement width must be independent of editor DPI.");
                    Assert.That(previewTarget.height,Is.EqualTo(Size),"GPU measurement height must be independent of editor DPI.");
                    lines.SetVector("_ViewportSize",new Vector4(previewTarget.width,previewTarget.height,0,0));
                    lines.SetFloat("_LineWidth",1.25f*previewTarget.width/Size);
                    Texture image;
                    try
                    {
                        for (int s = 0; s < mesh.subMeshCount; s++) utility.DrawMesh(alphaMesh ? alphaMesh : mesh,matrix,material,s);
                        // Only the diagnostic overlay is offset to avoid coplanar
                        // depth rejection; measurements use untouched geometry.
                        if (wire) utility.DrawMesh(wire,Matrix4x4.Translate(-utility.camera.transform.forward*.002f)*matrix,lines,0);
                        utility.Render(true);
                    }
                    finally { image = utility.EndPreview(); }
                    Graphics.Blit(image,rt); RenderTexture.active = rt;
                    pixels.ReadPixels(new Rect(0,0,Size,Size),0,0); pixels.Apply();
                    return pixels.GetPixels();
                }
                finally
                {
                    RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); Object.DestroyImmediate(pixels);
                    if (alphaMesh) Object.DestroyImmediate(alphaMesh); if (wire) Object.DestroyImmediate(wire);
                }
            }
            public void Dispose() { utility.Cleanup(); Object.DestroyImmediate(material); Object.DestroyImmediate(lines); }
        }
    }
}
