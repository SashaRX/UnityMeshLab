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
using RenderMode = SashaRX.UnityMeshLab.Tests.LodVisualQualityTests.Mode;

namespace SashaRX.UnityMeshLab.Tests
{
    // Opt-in evaluation of copied project assets. No procedural geometry or colors.
    // -meshlabLodProjectCases points to a JSON Dataset containing imported Assets paths.
    public sealed class LodProjectVisualQualityTests
    {
        [Serializable] public sealed class Case
        {
            public string name, asset, sourcePath, sourceSha256, albedo;
            public Vector3 rotation;
        }
        [Serializable] public sealed class Dataset { public Case[] cases; }
        [Serializable] public sealed class Report
        {
            public string fixture, sourcePath, sourceSha256, importedAsset, meshName, loopStatus, unity, gpu, graphicsApi, preview;
            public int resolution, sourceTriangles, sourceVertices, sourceQuads, submeshes;
            public bool hasVertexColors, varyingVertexColors;
            public Vector4 colorMin, colorMax;
            public float targetError = .2f, uv2Weight = 20, normalWeight = 1, colorWeight = 1, maxColorError = .02f;
            public List<LodVisualQualityTests.Capture> captures = new List<LodVisualQualityTests.Capture>();
            public string partConnectivity;
            public string featureChainProbe;
            public List<PartReport> parts = new List<PartReport>();
            public float partPixelLimit;
            public int screenPartProposed, screenPartTris;
            public bool screenPartAccepted;
        }
        [Serializable] public sealed class PartReport
        {
            public int id, triangles;
            public float areaFraction, pixelsLod2, pixelsLod4;
            public string protectedReason;
            public Bounds bounds;
        }

        [Test]
        [Timeout(1800000)] // Extended native + matched-count matrix includes full-surface fits and GPU captures.
        public void CopiedProjectModelsProduceActualGenerateCaptures()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-meshlabLodProjectCases");
            if (index < 0 || index + 1 >= args.Length) Assert.Ignore("Specify copied project models with -meshlabLodProjectCases.");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires a graphics device; omit -nographics.");
            Assert.That(LodVisualQualityTests.OutputDirectory(), Is.Not.Null.And.Not.Empty);
            Directory.CreateDirectory(LodVisualQualityTests.OutputDirectory());
            var dataset = JsonUtility.FromJson<Dataset>(File.ReadAllText(args[index + 1]));
            Assert.That(dataset.cases, Is.Not.Empty);
            foreach (var model in dataset.cases) Evaluate(model);
        }

        static void Evaluate(Case model)
        {
            // The caller explicitly supplies copies in this isolated test project.
            Assert.That(model.asset, Does.StartWith("Assets/LodProjectCopies/"));
            string actualHash;
            using (var hash = System.Security.Cryptography.SHA256.Create())
                actualHash = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(model.asset))).Replace("-", "").ToLowerInvariant();
            Assert.That(actualHash,Is.EqualTo(model.sourceSha256),"Copied FBX differs from recorded project source");
            var importer = AssetImporter.GetAtPath(model.asset) as ModelImporter;
            Assert.That(importer, Is.Not.Null, model.asset);
            if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
            var source = AssetDatabase.LoadAllAssetsAtPath(model.asset).OfType<Mesh>()
                .Where(m => !m.name.Contains("_COL") && !System.Text.RegularExpressions.Regex.IsMatch(m.name, @"_LOD[1-9]"))
                .OrderByDescending(LodMeshData.TriangleCount).FirstOrDefault();
            Assert.That(source, Is.Not.Null, model.asset);
            var report = new Report { fixture = model.name, sourcePath = model.sourcePath, sourceSha256 = actualHash, importedAsset = model.asset,
                meshName = source.name, sourceTriangles = LodMeshData.TriangleCount(source), sourceVertices = source.vertexCount,
                submeshes = source.subMeshCount, resolution = LodVisualQualityTests.Size, unity = Application.unityVersion,
                gpu = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                preview = "Diagnostic headlight; original albedo if supplied. Original URP material, packed maps and normal maps are not evaluated." };
            Color[] sourceColors = source.colors;
            report.hasVertexColors = sourceColors.Length == source.vertexCount;
            if (report.hasVertexColors)
            {
                report.colorMin = report.colorMax = sourceColors[0];
                foreach (Color c in sourceColors)
                { report.colorMin = Vector4.Min(report.colorMin,c); report.colorMax = Vector4.Max(report.colorMax,c); }
                report.varyingVertexColors = (report.colorMax-report.colorMin).sqrMagnitude > 1e-8f;
            }
            var topologyImports = new Dictionary<string, object>();
            bool loops = LodSourceTopology.TryLoad(new MeshEntry { fbxMesh = source },source,
                topologyImports,out var topology,out string reason);
            report.loopStatus = loops ? "Original FBX polygons loaded" : reason;
            report.sourceQuads = topology?.QuadCount ?? 0;
            var parts = LodSmallParts.Analyze(new MeshEntry { fbxMesh = source },source,topologyImports);
            report.partConnectivity = parts.connectivity;
            if (Environment.GetCommandLineArgs().Contains("-meshlabLodSmallParts")) report.partPixelLimit = PartPixelLimit();
            var probe = new GameObject("PartScreenSizeProbe");
            try
            {
                probe.AddComponent<MeshFilter>().sharedMesh = source; probe.AddComponent<MeshRenderer>();
                var group = LodGenerationTool.CreateLodGroupFromRenderers(probe);
                foreach (var part in parts.parts)
                    report.parts.Add(new PartReport { id = part.id, triangles = part.triangles, bounds = part.bounds,
                        areaFraction = (float)(part.area/parts.area), protectedReason = part.protectedReason,
                        pixelsLod2 = LodSmallParts.Pixels(part,Matrix4x4.identity,group.size,.25f,1080),
                        pixelsLod4 = LodSmallParts.Pixels(part,Matrix4x4.identity,group.size,.0625f,1080) });
                if (argsHave("-meshlabLodScreenGuided"))
                {
                    var plan = LodSmallParts.Select(parts,new LodSmallParts.Settings { enabled = true,firstLod = 2,
                        screenHeight = 1080,maxPixels = 8,maxAreaFraction = .02f,maxTriangleFraction = .2f },2,.25f,Matrix4x4.identity,group.size);
                    report.screenPartProposed = plan.removed.Count; report.screenPartTris = plan.triangles;
                    if (plan.removed.Count > 0)
                    {
                        var retained = LodSmallParts.Retain(parts,plan);
                        try { report.screenPartAccepted = LodSmallParts.ScreenSafe(source,retained.data.source,true,Matrix4x4.identity,270/group.size); }
                        finally { Object.DestroyImmediate(retained.data.source); }
                    }
                }
            }
            finally { Object.DestroyImmediate(probe); }
            SaveReport(report); // Preserve provenance even if a later generation fails.
            UvtLog.Info($"[LOD project visuals] {model.name}/{source.name}: {report.sourceTriangles} tris, varying colors={report.varyingVertexColors}, loops={report.loopStatus}");
            if (argsHave("-meshlabLodFeatureProbe"))
            {
                var coarsened = LodFeatureChains.Coarsen(source,settings:new LodFeatureChains.Settings {
                    relativeDeviation = .005f,normalAngle = 5,colorError = .02f,uvError = .001f });
                try
                {
                    report.featureChainProbe = coarsened.Note;
                    if (coarsened.mesh) Assert.That(new LodHardEdges(source).MeasureCoarsened(coarsened.mesh,coarsened.Configure(new LodHardEdges(coarsened.mesh).Measure(coarsened.mesh))).Valid,Is.True,model.name+": "+coarsened.Note);
                    SaveReport(report); return;
                }
                finally { if (coarsened.mesh) Object.DestroyImmediate(coarsened.mesh); }
            }
            if (argsHave("-meshlabQslimExport"))
            {
                LodQslimComparison.Export(model,source,topologyImports);
                return;
            }

            var generated = new List<Mesh>();
            var variants = new List<(string name, Mesh mesh, LodPipelineOps.LodInfo info, double ms)>
                { ("source",source,default,0) };
            try
            {
                bool partsOnly = Environment.GetCommandLineArgs().Contains("-meshlabLodSmallParts");
                bool budgetOnly = Environment.GetCommandLineArgs().Contains("-meshlabLodBudget");
                if (budgetOnly)
                {
                    if (argsHave("-meshlabLodScreenGuided"))
                    {
                        Generate(source,LodReductionMode.Triangles,3,"native",variants,generated,relaxedFar:true,correctAttributes:true,preserveHardEdges:true,coarsenHardEdgeChains:true,nativeHardEdgeConstraints:true);
                        Generate(source,LodReductionMode.Triangles,3,"guided",variants,generated,relaxedFar:true,correctAttributes:true,preserveHardEdges:true,coarsenHardEdgeChains:true,nativeHardEdgeConstraints:true,screenGuided:true);
                    }
                    else if (argsHave("-meshlabLodHardEdges"))
                    {
                        Generate(source,LodReductionMode.Triangles,3,"unprotected",variants,generated,relaxedFar:true,correctAttributes:true);
                        Generate(source,LodReductionMode.Triangles,3,"hard",variants,generated,relaxedFar:true,correctAttributes:true,preserveHardEdges:true);
                        if (argsHave("-meshlabLodFeatureChains"))
                            Generate(source,LodReductionMode.Triangles,3,"chains",variants,generated,relaxedFar:true,correctAttributes:true,preserveHardEdges:true,coarsenHardEdgeChains:true);
                        if (argsHave("-meshlabLodNativeFeatures"))
                            Generate(source,LodReductionMode.Triangles,3,"native",variants,generated,relaxedFar:true,correctAttributes:true,preserveHardEdges:true,coarsenHardEdgeChains:true,nativeHardEdgeConstraints:true);
                        if (argsHave("-meshlabLodMatchedFeatures"))
                        {
                            var chainCounts = variants.Where(v => v.name.StartsWith("chains-")).Select(v => (v.info.simplifiedTris-.25f)/report.sourceTriangles).ToArray();
                            Assert.That(chainCounts.Length,Is.EqualTo(2),"Matched comparison requires the coarsened-belt controls.");
                            Generate(source,LodReductionMode.Triangles,3,"matched",variants,generated,relaxedFar:true,correctAttributes:true,preserveHardEdges:true,coarsenHardEdgeChains:true,nativeHardEdgeConstraints:true,requestedRatios:chainCounts);
                        }
                    }
                    else if (argsHave("-meshlabQslimCompare"))
                    {
                        Generate(source,LodReductionMode.Triangles,3,"meshopt",variants,generated,relaxedFar:true,correctAttributes:true);
                        LodQslimComparison.Append(model,source,variants,generated);
                    }
                    else if (Environment.GetCommandLineArgs().Contains("-meshlabLodAttributeCorrection"))
                    {
                        Generate(source,LodReductionMode.Triangles,3,"quality",variants,generated,relaxedFar:true);
                        Generate(source,LodReductionMode.Triangles,3,"corrected",variants,generated,relaxedFar:true,correctAttributes:true);
                        for (int level = 1; level <= 2; level++)
                        {
                            var baseline = variants.Single(v => v.name == $"quality-lod{level}");
                            var fitted = variants.Single(v => v.name == $"corrected-lod{level}");
                            CollectionAssert.AreEqual(baseline.mesh.vertices,fitted.mesh.vertices);
                            for (int slot = 0; slot < source.subMeshCount; slot++)
                                CollectionAssert.AreEqual(baseline.mesh.GetIndices(slot),fitted.mesh.GetIndices(slot));
                            CollectionAssert.AreEqual(baseline.mesh.uv2,fitted.mesh.uv2);
                            Assert.That(fitted.info.normalRms,Is.LessThanOrEqualTo(baseline.info.normalRms+1e-4));
                            for (int ch = 0; ch < 4; ch++)
                            {
                                Assert.That(fitted.info.colorRms[ch],Is.LessThanOrEqualTo(baseline.info.colorRms[ch]+1e-6));
                                Assert.That(fitted.info.colorMax[ch],Is.LessThanOrEqualTo(baseline.info.colorMax[ch]+1e-6));
                            }
                            Assert.That(fitted.mesh.GetVertexAttributes(),Is.EqualTo(baseline.mesh.GetVertexAttributes()));
                        }
                    }
                    else
                    {
                        Generate(source,LodReductionMode.Triangles,1,"budget",variants,generated,relaxedFar:true);
                        if (Environment.GetCommandLineArgs().Contains("-meshlabLodQualitySelection"))
                            Generate(source,LodReductionMode.Triangles,3,"quality",variants,generated,relaxedFar:true);
                    }
                }
                else if (partsOnly)
                {
                    Generate(source,LodReductionMode.Triangles,1,"far-relaxed",variants,generated,relaxedFar:true);
                    Generate(source,LodReductionMode.Triangles,1,"parts",variants,generated,relaxedFar:true,pruneParts:true);
                }
                else
                {
                    Generate(source,LodReductionMode.Triangles,1,"triangles",variants,generated);
                    Generate(source,LodReductionMode.Triangles,1,"far-relaxed",variants,generated,relaxedFar:true);
                    if (report.varyingVertexColors) Generate(source,LodReductionMode.Triangles,5,"triangles-high",variants,generated);
                    if (loops) Generate(source,LodReductionMode.FullLoops,5,"loops-high",variants,generated);
                    if (report.varyingVertexColors) Generate(source,LodReductionMode.Triangles,1,"unchecked",variants,generated,true);
                }
                float scale = 1f / Mathf.Max(source.bounds.size.x,Mathf.Max(source.bounds.size.y,source.bounds.size.z));
                Matrix4x4 matrix = Matrix4x4.Rotate(Quaternion.Euler(model.rotation)) * Matrix4x4.Scale(Vector3.one*scale)
                    * Matrix4x4.Translate(-source.bounds.center);
                Texture albedo = string.IsNullOrEmpty(model.albedo) ? null : AssetDatabase.LoadAssetAtPath<Texture>(model.albedo);
                using var renderer = new LodVisualQualityTests.Renderer();
                foreach (string view in new[] { "front", "oblique" })
                {
                    renderer.SetView(view);
                    var original = renderer.Draw(source,RenderMode.Colors,matrix);
                    var coverage = renderer.Draw(source,RenderMode.Coverage,matrix);
                    var shading = renderer.Draw(source,RenderMode.Shaded,matrix);
                    var screens = new List<LodScreenAcceptance>();
                    foreach (int divisor in new[] { 1,2,4 })
                    {
                        var screenSettings = LodScreenAcceptance.Settings.Default; screenSettings.divisor = divisor;
                        screenSettings.checkColorBoundaries = report.varyingVertexColors;
                        screens.Add(new LodScreenAcceptance(original,coverage,LodVisualQualityTests.Size,LodVisualQualityTests.Size,screenSettings));
                    }
                    foreach (var variant in variants)
                    {
                        var color = renderer.Draw(variant.mesh,RenderMode.Colors,matrix);
                        var mask = renderer.Draw(variant.mesh,RenderMode.Coverage,matrix);
                        var lit = renderer.Draw(variant.mesh,RenderMode.Shaded,matrix);
                        var capture = LodVisualQualityTests.Compare(original,color,coverage,mask,shading,lit);
                        capture.screenAcceptance = new List<LodScreenAcceptance.Report>();
                        foreach (var screen in screens) capture.screenAcceptance.Add(screen.Measure(color,mask));
                        capture.variant = variant.name; capture.view = view;
                        capture.triangles = LodMeshData.TriangleCount(variant.mesh);
                        capture.targetRatio = variant.name == "source" ? 1 : variant.info.targetRatio;
                        capture.targetTriangles = variant.name == "source" ? report.sourceTriangles : variant.info.targetTris;
                        capture.budgetReached = capture.triangles <= capture.targetTriangles;
                        capture.selectedCandidate = variant.info.selectedCandidate; capture.candidateCount = variant.info.evaluatedCandidates;
                        capture.removedLoops = variant.info.removedLoops; capture.simplifyMs = variant.ms;
                        capture.reductionNote = variant.info.reductionNote;
                        capture.surfaceColorMax = variant.info.colorMax; capture.surfaceColorRms = variant.info.colorRms;
                        capture.allowedColorError = variant.info.allowedColorError; capture.targetError = variant.info.targetError;
                        capture.normalWeight = variant.info.normalWeight; capture.colorWeight = variant.info.colorWeight;
                        capture.budgetPriority = variant.info.budgetPriority; capture.qualityLimitsExceeded = variant.info.qualityLimitsExceeded;
                        capture.sourceDistance = variant.info.sourceDistance; capture.normalError = variant.info.normalError;
                        capture.sourceDistanceRms = variant.info.sourceDistanceRms; capture.normalRms = variant.info.normalRms; capture.uvRms = variant.info.uvRms;
                        capture.silhouetteMean = variant.info.silhouetteMean; capture.silhouetteMax = variant.info.silhouetteMax;
                        capture.selectionScore = variant.info.selectionScore; capture.nativeProbes = variant.info.nativeProbes;
                        capture.screenQuality = variant.info.screenQuality;
                        if (argsHave("-meshlabLodHardEdges"))
                        {
                            var features = variant.name.StartsWith("chains-") || variant.name.StartsWith("native-") || variant.name.StartsWith("matched-") || variant.name.StartsWith("guided-") ? variant.info.hardEdges : new LodHardEdges(source).Measure(variant.mesh);
                            capture.hardEdges = features.edges; capture.missingHardEdges = features.missingEdges;
                            capture.protectedTriangles = features.protectedTriangles; capture.missingProtectedTriangles = features.missingFaces;
                            capture.patchInterfaces = features.interfaces; capture.missingPatchInterfaces = features.missingInterfaces;
                            capture.ambiguousFeatureEdges = features.ambiguousEdges;
                            capture.hardEdgeSourceFallback = variant.info.hardEdges?.sourceFallback ?? false;
                            capture.coarsenedFeaturePoints = features.coarsenedPoints; capture.coarsenedFeatureTriangles = features.coarsenedTriangles;
                            capture.nativeCreaseConstraints = features.nativeConstraints; capture.lockedChainRetry = features.lockedChainRetry;
                            capture.nativeBeltFallback = features.beltFallback;
                            if (variant.name.StartsWith("hard-") || variant.name.StartsWith("chains-") || variant.name.StartsWith("native-") || variant.name.StartsWith("matched-") || variant.name.StartsWith("guided-")) Assert.That(features.Valid,Is.True,model.name+"/"+variant.name+": hard features or patch interfaces changed");
                        }
                        var correction = variant.info.attributeCorrection;
                        if (correction != null)
                        {
                            capture.correctionNote = correction.note; capture.normalsCorrected = correction.normalsAccepted; capture.colorsCorrected = correction.colorsAccepted;
                            capture.normalRmsBefore = correction.normalRmsBefore; capture.authoredNormalMaxBefore = correction.normalMaxBefore;
                            capture.authoredNormalMaxAfter = correction.normalMaxAfter; capture.colorRmsBefore = correction.colorRmsBefore; capture.colorMaxBefore = correction.colorMaxBefore;
                            capture.smoothingRegions = correction.smoothingRegions; capture.linkedNormalDuplicates = correction.linkedNormalDuplicates;
                            capture.mixedSmoothingFaces = correction.mixedSmoothingFaces; capture.missingSmoothingRegions = correction.missingSmoothingRegions;
                            capture.incompleteSmoothingRegions = correction.incompleteSmoothingRegions;
                            capture.regionNormalsBefore = correction.regionNormalsBefore?.Select(RegionError).ToList();
                            capture.regionNormalsAfter = correction.regionNormalsAfter?.Select(RegionError).ToList();
                            if (correction.regionNormalsBefore != null)
                                Assert.That(LodSmoothingRegions.NoRegression(correction.regionNormalsAfter,correction.regionNormalsBefore),Is.True,
                                    model.name+"/"+variant.name+": a smoothing region's normal error increased");
                        }
                        capture.budgetCandidates = variant.info.budgetCandidates?.Select(c => new LodVisualQualityTests.BudgetCandidate {
                            variant = c.variant,name = c.name,triangles = c.triangles,nativeProbes = c.nativeProbes,score = c.score,
                            distanceRms = c.distanceRms,normalRms = c.normalRms,colorRms = c.colorRms,uvRms = c.uvRms,
                            silhouetteMean = c.silhouetteMean,silhouetteMax = c.silhouetteMax,screenQuality = c.screenQuality }).ToList();
                        capture.removedParts = variant.info.removedParts; capture.removedPartTris = variant.info.removedPartTris;
                        capture.removedPartAreaFraction = variant.info.removedPartAreaFraction; capture.removedPartMaxPixels = variant.info.removedPartMaxPixels;
                        report.captures.Add(capture);
                        LodVisualQualityTests.Save(model.name,variant.name,view,"color",color);
                        LodVisualQualityTests.Save(model.name,variant.name,view,"coverage",mask);
                        LodVisualQualityTests.Save(model.name,variant.name,view,"shaded",lit);
                        LodVisualQualityTests.Save(model.name,variant.name,view,"wire",renderer.Draw(variant.mesh,RenderMode.Wire,matrix));
                        if (report.hasVertexColors) LodVisualQualityTests.Save(model.name,variant.name,view,"alpha",renderer.Draw(variant.mesh,RenderMode.Alpha,matrix));
                        if (albedo) LodVisualQualityTests.Save(model.name,variant.name,view,"textured",renderer.Draw(variant.mesh,RenderMode.Textured,matrix,albedo));
                        Assert.That(capture.coveredPixels,Is.GreaterThan(100),"Blank project asset capture: "+model.name);
                    }
                }
                SaveReport(report);
                foreach (var capture in report.captures.Where(c => c.reductionNote != null && c.reductionNote.Contains("kept source mesh")))
                    Assert.That(capture.surfaceColorMax.magnitude,Is.LessThan(.001f),"An unchanged fallback must validate against LOD0: "+model.name);
                // Asset evaluation records fidelity, including regressions and missed budgets;
                // it does not claim all production models satisfy synthetic-fixture thresholds.
                Assert.That(report.captures.Count,Is.GreaterThanOrEqualTo(6));
                Assert.That(LodMeshData.TriangleCount(source),Is.EqualTo(report.sourceTriangles),"Source asset changed");
            }
            finally { foreach (Mesh mesh in generated) if (mesh) Object.DestroyImmediate(mesh); }
        }

        static void Generate(Mesh source,LodReductionMode mode,int candidates,string prefix,
            List<(string name, Mesh mesh, LodPipelineOps.LodInfo info, double ms)> variants,List<Mesh> generated,bool uncheckedColors = false,bool relaxedFar = false,bool pruneParts = false,bool correctAttributes = false,bool preserveHardEdges = false,bool coarsenHardEdgeChains = false,bool nativeHardEdgeConstraints = false,float[] requestedRatios = null,bool screenGuided = false)
        {
            var root = new GameObject("ProjectLODPreview");
            try
            {
                root.AddComponent<MeshFilter>().sharedMesh = source; root.AddComponent<MeshRenderer>();
                var context = new UvToolContext(); context.Refresh(LodGenerationTool.CreateLodGroupFromRenderers(root));
                var options = new LodPipelineOps.Options { count = 2, ratios = new[] {.5f,.25f}, targetError = .2f,
                    uv2Weight = 20, normalWeight = 1, colorWeight = 1, maxNormalAngle = 15, maxColorError = .02f,
                    candidateCount = candidates, reductionMode = mode, skipColorValidation = uncheckedColors };
                bool fourLevels = Environment.GetCommandLineArgs().Contains("-meshlabLodPartsFourLevels");
                if (fourLevels) { options.count = 4; options.ratios = new[] {.5f,.25f,.125f,.0625f}; }
                bool budgetOnly = Environment.GetCommandLineArgs().Contains("-meshlabLodBudget");
                if (budgetOnly)
                {
                    options.count = 2; options.ratios = LodGenerationTool.SteppedRatios(1,3,2); options.prioritizeTriangleBudget = true;
                    if (requestedRatios != null) options.ratios = requestedRatios;
                    options.correctSurfaceAttributes = correctAttributes;
                    options.preserveHardEdges = preserveHardEdges;
                    options.coarsenHardEdgeChains = coarsenHardEdgeChains;
                    options.nativeHardEdgeConstraints = nativeHardEdgeConstraints;
                    options.screenGuidedSelection = screenGuided;
                    options.featureChainError = coarsenHardEdgeChains ? .005f : 0;
                }
                if (relaxedFar)
                    options = LodPipelineOps.RelaxFarLods(options,1,2,new LodPipelineOps.LevelQuality {
                        targetError = .3f, normalWeight = .5f, colorWeight = .25f, maxColorError = .1f, maxNormalAngle = 30
                    });
                if (pruneParts)
                {
                    options.smallParts = new LodSmallParts.Settings { enabled = true, firstLod = 2, screenHeight = 1080,
                        maxPixels = PartPixelLimit(), maxAreaFraction = .02f, maxTriangleFraction = .2f };
                }
                var timer = Stopwatch.StartNew();
                var result = LodPipelineOps.Generate(context,1,options); timer.Stop();
                foreach (var go in result.generatedObjects)
                { var mesh = go.GetComponent<MeshFilter>().sharedMesh; generated.Add(mesh); }
                Assert.That(result.ok,Is.True,result.error);
                if (relaxedFar)
                {
                    Assert.That(result.perLod[0].targetError,Is.EqualTo(.2f));
                    Assert.That(result.perLod[0].allowedColorError,Is.EqualTo(.02f));
                    Assert.That(result.perLod.Last().targetError,Is.EqualTo(.3f));
                    Assert.That(result.perLod.Last().allowedColorError,Is.EqualTo(.1f));
                    if (!budgetOnly)
                        foreach (var info in result.perLod)
                            Assert.That(info.colorError,Is.LessThanOrEqualTo(info.allowedColorError),"Relaxation must still enforce its finite color bound.");
                }
                if (budgetOnly)
                    foreach (var info in result.perLod)
                    {
                        Assert.That(info.budgetPriority,Is.True);
                        if (info.hardEdges?.sourceFallback != true)
                            Assert.That(info.evaluatedCandidates,Is.EqualTo(candidates+(info.budgetCandidates?.Any(c => c.name == "previous protected source candidate") == true ? 1 : 0)));
                        Assert.That(float.IsNaN(info.selectionScore) || float.IsInfinity(info.selectionScore),Is.False,
                            "Visible real project models require a finite quality ranking: "+source.name);
                        if (!preserveHardEdges) Assert.That(info.simplifiedTris,Is.LessThanOrEqualTo(info.targetTris),$"{source.name}/LOD{info.lodLevel}: failed requested 3x budget");
                        else
                        {
                            Assert.That(info.hardEdges.Valid,Is.True);
                            Assert.That(info.targetNotReached,Is.EqualTo(info.simplifiedTris > info.targetTris));
                        }
                    }
                for (int i = 0; i < result.perLod.Count; i++)
                {
                    if (preserveHardEdges && i > 0) Assert.That(result.perLod[i].simplifiedTris,Is.LessThanOrEqualTo(result.perLod[i-1].simplifiedTris));
                    if ((uncheckedColors || pruneParts) && i == 0) continue;
                    if (fourLevels && i != 3) continue; // The explicit four-level probe captures only the last level.
                    variants.Add(($"{prefix}-lod{i+1}",result.generatedObjects[i].GetComponent<MeshFilter>().sharedMesh,result.perLod[i],timer.Elapsed.TotalMilliseconds));
                }
            }
            finally { Object.DestroyImmediate(root); }
        }

        static void SaveReport(Report report) => File.WriteAllText(
            Path.Combine(LodVisualQualityTests.OutputDirectory(),report.fixture+"-metrics.json"),JsonUtility.ToJson(report,true));

        static bool argsHave(string argument) => Environment.GetCommandLineArgs().Contains(argument);
        static LodVisualQualityTests.RegionNormalError RegionError(LodSmoothingRegions.Error e)
            => new LodVisualQualityTests.RegionNormalError { region = e.region,samples = e.samples,unresolved = e.unresolved,rms = e.rms,maximum = e.maximum };

        static float PartPixelLimit()
        {
            var args = Environment.GetCommandLineArgs(); int arg = Array.IndexOf(args,"-meshlabLodPartPixels");
            return arg >= 0 && arg+1 < args.Length ? float.Parse(args[arg+1],System.Globalization.CultureInfo.InvariantCulture) : 2;
        }
    }
}
