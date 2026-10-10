using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class LodBudgetTriangleSimplifier
    {
        sealed class ProbeEvaluation
        {
            internal Mesh source;
            internal MeshSimplifier.SimplifySettings settings;
            internal LodPipelineOps.Options options;
            internal LodSilhouetteValidation silhouette;
            internal LodScreenValidation screen;
        }
        internal struct CandidateReport
        {
            internal int variant, triangles, nativeProbes;
            internal string name;
            internal float score, distanceRms, normalRms, colorRms, uvRms, silhouetteMean, silhouetteMax;
            internal LodScreenValidation.Report screenQuality;
        }
        // Budget-first generation still uses edge collapse. It never drops arbitrary
        // triangles to manufacture a count, and never simplifies a preceding LOD.
        internal static MeshSimplifier.SimplifyResult Simplify(Mesh source,MeshSimplifier.SimplifySettings settings,
            LodPipelineOps.Options options,out LodLoopSimplifier.Result diagnostics,out string note,Func<bool> cancelled,Mesh previousProtected = null)
        {
            diagnostics = null; note = null;
            if (!source || source.vertexCount == 0) return new MeshSimplifier.SimplifyResult { error = "Source mesh has no vertices." };
            if (options.screenBudget)
            {
                settings.preserveHardEdges = settings.nativeHardEdgeConstraints = false;
                options.coarsenHardEdgeChains = false; options.screenGuidedSelection = true;
            }
            var chains = options.coarsenHardEdgeChains && settings.preserveHardEdges && options.reductionMode == LodReductionMode.Triangles
                ? LodFeatureChains.Coarsen(source,cancelled,new LodFeatureChains.Settings {
                    relativeDeviation = Mathf.Clamp(options.featureChainError,0,.01f),
                    normalAngle = options.featureChainError > 0 ? Mathf.Min(5,Mathf.Max(0,options.maxNormalAngle)) : 0,
                    colorError = options.featureChainError > 0 ? Mathf.Max(0,options.maxColorError) : 0,
                    uvError = options.featureChainError > 0 ? .001f : 0 }) : null;
            try
            {
                if (chains?.mesh && !new LodHardEdges(source).MeasureCoarsened(chains.mesh,chains.Configure(new LodHardEdges(chains.mesh).Measure(chains.mesh))).Valid)
                {
                    UnityEngine.Object.DestroyImmediate(chains.mesh); chains.mesh = null;
                    chains.removedPoints = chains.removedTriangles = 0; chains.refusal = "Original feature coverage rejected the prepass; retained source.";
                }
                return SimplifyPrepared(source,chains,settings,options,out diagnostics,out note,cancelled,previousProtected);
            }
            finally { if (chains?.mesh) UnityEngine.Object.DestroyImmediate(chains.mesh); }
        }

        static MeshSimplifier.SimplifyResult SimplifyPrepared(Mesh source,LodFeatureChains.Result chains,MeshSimplifier.SimplifySettings settings,
            LodPipelineOps.Options options,out LodLoopSimplifier.Result diagnostics,out string note,Func<bool> cancelled,Mesh previousProtected)
        {
            diagnostics = null; note = null;
            Mesh reductionSource = chains?.mesh ? chains.mesh : source;
            var reductionSettings = settings;
            var best = new MeshSimplifier.SimplifyResult();
            var selectedSettings = settings;
            var bestMetrics = new LodSurfaceValidation.Metrics();
            var bestSilhouette = new LodSilhouetteValidation.Metrics();
            LodScreenValidation.Report bestScreen = null;
            float bestScore = float.PositiveInfinity;
            int selected = 0, probes = 0;
            int target = Mathf.Max(1,Mathf.CeilToInt(LodMeshData.TriangleCount(source)*settings.targetRatio));
            if (chains?.mesh) reductionSettings.targetRatio = Mathf.Min(1,(float)target/LodMeshData.TriangleCount(reductionSource));
            var protection = settings.preserveHardEdges ? new LodHardEdges(reductionSource) : null;
            var originalProtection = settings.preserveHardEdges ? new LodHardEdges(source) : null;
            bool native = settings.nativeHardEdgeConstraints && protection != null;
            bool protectedFloorExceedsBudget = protection != null && (native ? protection.NativeProtectedTriangles : protection.protectedTriangles) >= target;
            int previousCount = previousProtected ? LodMeshData.TriangleCount(previousProtected) : 0;
            int densityLimit = settings.preserveHardEdges && previousCount > target && (native ? protection.MeasureNative(previousProtected) : protection.Measure(previousProtected)).Valid
                ? previousCount : int.MaxValue;
            bool reusedPrevious = false;
            int count = Mathf.Clamp(options.candidateCount,1,5);
            var reports = new List<CandidateReport>();
            var silhouette = options.screenBudget ? null : new LodSilhouetteValidation(source,cancelled);
            int screenPixels = options.screenObjectPixels > 0 ? options.screenObjectPixels : options.screenBudget ? 64 : 248;
            var screen = options.screenGuidedSelection ? new LodScreenValidation(source,!options.skipColorValidation,cancelled,
                resolution:Mathf.Max(32,screenPixels+8),objectPixels:screenPixels,measureFields:options.screenBudget) : null;
            var probeEvaluation = chains?.mesh || native || screen != null ? new ProbeEvaluation { source = source,settings = settings,options = options,silhouette = silhouette,screen = screen } : null;
            bool rankOverBudgetQuality = protectedFloorExceedsBudget || probeEvaluation != null;
            try
            {
                for (int variant = 0; variant < count; variant++)
                {
                    UvProgress.Report(UvProgress.Current.fraction,$"{source.name}: quality variant {variant+1}/{count}");
                    var candidate = SimplifyOne(reductionSource,Variant(reductionSettings,variant),out var nativeSettings,out int attempts,cancelled,protectedFloorExceedsBudget,probeEvaluation);
                    probes += attempts;
                    try
                    {
                        if (!candidate.ok) { note = candidate.error; continue; }
                        var metrics = LodSurfaceValidation.MeasureMeshes(source,candidate.simplifiedMesh,settings,cancelled,ignoreDegenerateFaces:true);
                        var local = screen?.Measure(candidate.simplifiedMesh,cancelled);
                        var outline = Outline(silhouette,candidate.simplifiedMesh,local,cancelled);
                        float score = CandidateScore(metrics,outline,local,settings,options);
                        reports.Add(new CandidateReport { variant = variant+1,name = VariantName(variant),triangles = candidate.simplifiedTriCount,
                            nativeProbes = attempts,score = score,distanceRms = metrics.DistanceRms,normalRms = metrics.NormalRms,
                            colorRms = Maximum(metrics.ColorRms),uvRms = metrics.UvRms,silhouetteMean = outline.mean,silhouetteMax = outline.maximum,screenQuality = local });
                        if (!best.ok || Better(candidate.simplifiedTriCount,score,best.simplifiedTriCount,bestScore,target,rankOverBudgetQuality,densityLimit))
                        {
                            if (best.simplifiedMesh) UnityEngine.Object.DestroyImmediate(best.simplifiedMesh);
                            best = candidate; candidate.simplifiedMesh = null;
                            bestMetrics = metrics; bestSilhouette = outline; bestScore = score; bestScreen = local;
                            selected = variant+1; selectedSettings = nativeSettings;
                        }
                    }
                    finally { if (candidate.simplifiedMesh) UnityEngine.Object.DestroyImmediate(candidate.simplifiedMesh); }
                }
                if (!best.ok) return new MeshSimplifier.SimplifyResult { error = note ?? "No nonempty triangle-budget candidate." };
                if (best.simplifiedTriCount > densityLimit)
                {
                    // Geometry remains a source-derived candidate, never input to
                    // another collapse pass. Re-evaluate fields under this level.
                    var metrics = LodSurfaceValidation.MeasureMeshes(source,previousProtected,settings,cancelled,ignoreDegenerateFaces:true);
                    var outline = silhouette.Measure(previousProtected,cancelled);
                    bool draft = MeshUvState.IsDraft(previousProtected);
                    var copy = UnityEngine.Object.Instantiate(previousProtected);
                    UnityEngine.Object.DestroyImmediate(best.simplifiedMesh);
                    best = new MeshSimplifier.SimplifyResult { ok = true,simplifiedMesh = copy,draftUv = draft,
                        originalTriCount = LodMeshData.TriangleCount(source),simplifiedTriCount = previousCount,resultError = metrics.weightedError };
                    bestScreen = screen?.Measure(copy,cancelled);
                    bestMetrics = metrics; bestSilhouette = outline; bestScore = Score(metrics,outline,settings,options)+(bestScreen?.Penalty(settings.colorWeight) ?? 0);
                    selected = count+1; reusedPrevious = true;
                    reports.Add(new CandidateReport { variant = selected,name = "previous protected source candidate",triangles = previousCount,
                        score = bestScore,distanceRms = metrics.DistanceRms,normalRms = metrics.NormalRms,colorRms = Maximum(metrics.ColorRms),
                        uvRms = metrics.UvRms,silhouetteMean = outline.mean,silhouetteMax = outline.maximum,screenQuality = bestScreen });
                }
                LodAttributeCorrection.Report correction = null;
                if (options.correctSurfaceAttributes)
                {
                    UvProgress.Report(UvProgress.Current.fraction,$"{source.name}: verify surface attribute correction");
                    var corrected = LodAttributeCorrection.Correct(source,best.simplifiedMesh,settings,options,
                        out correction,out var correctedMetrics,cancelled,bestMetrics);
                    try
                    {
                        if (corrected)
                        {
                            var correctedScreen = screen?.Measure(corrected,cancelled);
                            if (correctedScreen == null || correctedScreen.DoesNotWorsen(bestScreen,options.screenBudget))
                            {
                                UnityEngine.Object.DestroyImmediate(best.simplifiedMesh); best.simplifiedMesh = corrected; corrected = null;
                                bestMetrics = correctedMetrics; bestScreen = correctedScreen;
                                bestScore = CandidateScore(bestMetrics,bestSilhouette,bestScreen,settings,options);
                            }
                            else
                            {
                                correction.normalsAccepted = correction.colorsAccepted = false;
                                correction.normalRmsAfter = bestMetrics.NormalRms; correction.colorRmsAfter = bestMetrics.ColorRms;
                                correction.normalMaxAfter = bestMetrics.authoredNormalAngle; correction.colorMaxAfter = bestMetrics.colorMax;
                                correction.normalBlend = correction.colorBlend = 0; correction.regionNormalsAfter = correction.regionNormalsBefore;
                                correction.note += " CPU screen guide rejected the attribute replacement: local detail/paint loss worsened; kept original candidate attributes.";
                            }
                        }
                    }
                    finally { if (corrected) UnityEngine.Object.DestroyImmediate(corrected); }
                }
                MeshUvState.SetDraft(best.simplifiedMesh,best.draftUv);
                diagnostics = new LodLoopSimplifier.Result { metrics = bestMetrics,maxDistance = bestMetrics.distance,
                    maxNormalAngle = bestMetrics.normalAngle,maxColorError = bestMetrics.colorError,
                    evaluatedCandidates = reports.Count,selectedCandidate = selected,nativeProbes = probes,
                    silhouetteMean = bestSilhouette.mean,silhouetteMax = bestSilhouette.maximum,selectionScore = bestScore,budgetCandidates = reports,
                    attributeCorrection = correction,screenQuality = bestScreen };
                string selectedName = reusedPrevious ? "previous protected source candidate" : VariantName(selected-1);
                note = $"Triangle budget prioritized; selected {selectedName} from {reports.Count} quality candidates ({probes} native probes). " +
                "Selection measures six-view silhouette, area RMS geometry/normals/UV/RGBA against source; score is a relative ranking heuristic.";
                if (screen != null) note += $" CPU screen guide adds worst visible detail/RGBA boundary loss ({bestScreen.detailLoss:P1}/{bestScreen.colorLoss:P1}) at a {bestScreen.objectPixels}px object extent; budget and density rules still take precedence.";
                if (options.screenBudget) note += $" Far screen budget ranks silhouette, visible normal RMS {bestScreen.normalRms:F1}° and RGBA RMS {bestScreen.rgbaRms:G3}; exact crease/face belts are relaxed. Lock Border setting and material slots are retained.";
                if (protectedFloorExceedsBudget) note += " Protected face floor already reaches/exceeds the requested budget; no error/weight relaxation. Over-budget candidates ranked by measured quality.";
                else if (probeEvaluation != null) note += " Over-budget native probes and strategies ranked by measured source quality; lower triangle counts alone cannot displace a better field/shape.";
                if (reusedPrevious) note += " All new candidates exceeded the preceding protected density; cloned and remeasured that source-derived geometry. No recursive collapse; reported result error is sampled weighted error, not a new native bound.";
                else
                {
                    if (selectedSettings.targetError > settings.targetError) note += " Native costs/error relaxed; Lock Border retained.";
                    if (selectedSettings.allowAttributeSeamCollapse) note += " Attribute seam collapses allowed.";
                    note += $" Selected native Target Error={selectedSettings.targetError:G3}, UV/Normal/Color weights=" +
                        $"{selectedSettings.uv2Weight:G3}/{selectedSettings.normalWeight:G3}/{selectedSettings.colorWeight:G3}.";
                }
                if (correction != null) note += " "+correction.note;
                if (protection != null)
                {
                    bool sourceFallback = best.hardEdges?.sourceFallback ?? false;
                    bool lockedRetry = best.hardEdges?.lockedChainRetry ?? false;
                    bool beltFallback = best.hardEdges?.beltFallback ?? false;
                    best.hardEdges = native && !beltFallback ? protection.MeasureNative(best.simplifiedMesh,lockedRetry) : protection.Measure(best.simplifiedMesh);
                    best.hardEdges.beltFallback = beltFallback;
                    if (chains?.mesh)
                    {
                        best.hardEdges = originalProtection.MeasureCoarsened(best.simplifiedMesh,chains.Configure(best.hardEdges));
                        best.resultError = bestMetrics.weightedError;
                        note += " Coarsened-source result error is sampled against LOD0; the native bound applies only to the prepared mesh.";
                    }
                    best.hardEdges.sourceFallback = sourceFallback;
                    if (chains != null)
                    {
                        best.hardEdges.coarsenedPoints = chains.removedPoints; best.hardEdges.coarsenedTriangles = chains.removedTriangles;
                        note += " " + chains.Note;
                    }
                }
                best.originalTriCount = LodMeshData.TriangleCount(source);
                if (!options.skipColorValidation && options.maxColorError > 0 && bestMetrics.colorError > options.maxColorError)
                    note += $" RGBA error {bestMetrics.colorError:G3} exceeds requested {options.maxColorError:G3}.";
                if (options.maxNormalAngle > 0 && bestMetrics.normalAngle > options.maxNormalAngle)
                    note += $" Normal error {bestMetrics.normalAngle:F1}° exceeds requested {options.maxNormalAngle:F1}°.";
                var result = best; best.simplifiedMesh = null;
                return result;
            }
            finally { if (best.simplifiedMesh) UnityEngine.Object.DestroyImmediate(best.simplifiedMesh); }
        }

        // A reached budget wins; protected sequences also cap increasing density.
        // Known unreachable floors rank quality within that preceding density cap.
        internal static bool Better(int triangles,float score,int bestTriangles,float bestScore,int target,bool protectedFloorExceedsBudget = false,int densityLimit = int.MaxValue)
        {
            if ((triangles <= densityLimit) != (bestTriangles <= densityLimit)) return triangles <= densityLimit;
            if ((triangles <= target) != (bestTriangles <= target)) return triangles <= target;
            int scoreOrder = score.CompareTo(bestScore);
            if (triangles > target)
                return protectedFloorExceedsBudget ? scoreOrder < 0 || (scoreOrder == 0 && triangles < bestTriangles) :
                    triangles < bestTriangles || (triangles == bestTriangles && scoreOrder < 0);
            int minimum = Mathf.Max(1,target-Mathf.Max(2,Mathf.CeilToInt(target*.05f)));
            if ((triangles >= minimum) != (bestTriangles >= minimum)) return triangles >= minimum;
            if (scoreOrder != 0) return scoreOrder < 0;
            return triangles > bestTriangles;
        }

        internal static float Score(LodSurfaceValidation.Metrics metrics,LodSilhouetteValidation.Metrics silhouette,
            MeshSimplifier.SimplifySettings settings,LodPipelineOps.Options options)
        {
            if (float.IsInfinity(metrics.weightedError) || float.IsNaN(metrics.weightedError)) return float.PositiveInfinity;
            float color = options.skipColorValidation ? 0 : Maximum(metrics.ColorRms)/Mathf.Max(.02f,options.maxColorError)*Mathf.Max(0,settings.colorWeight);
            float score = metrics.DistanceRms/.01f + silhouette.mean/.05f + silhouette.maximum/.2f +
                metrics.NormalRms/Mathf.Max(15,options.maxNormalAngle)*Mathf.Max(0,settings.normalWeight) + color +
                metrics.UvRms/.1f*Mathf.Max(0,settings.uv2Weight);
            return float.IsNaN(score) ? float.PositiveInfinity : score;
        }
        static LodSilhouetteValidation.Metrics Outline(LodSilhouetteValidation silhouette,Mesh mesh,LodScreenValidation.Report screen,Func<bool> cancelled)
            => silhouette != null ? silhouette.Measure(mesh,cancelled) : new LodSilhouetteValidation.Metrics { mean = screen.silhouetteMean,maximum = screen.silhouetteMax };
        static float CandidateScore(LodSurfaceValidation.Metrics metrics,LodSilhouetteValidation.Metrics silhouette,LodScreenValidation.Report screen,
            MeshSimplifier.SimplifySettings settings,LodPipelineOps.Options options)
        {
            if (float.IsInfinity(metrics.weightedError) || float.IsNaN(metrics.weightedError)) return float.PositiveInfinity;
            return options.screenBudget ? screen.ScreenScore(settings.normalWeight,options.skipColorValidation ? 0 : settings.colorWeight)+metrics.DistanceRms*16 :
                Score(metrics,silhouette,settings,options)+(screen?.Penalty(settings.colorWeight) ?? 0);
        }
        static float Maximum(Vector4 value) => Mathf.Max(Mathf.Max(value.x,value.y),Mathf.Max(value.z,value.w));
        static string VariantName(int variant) => variant == 0 ? "balanced" : variant == 1 ? "shape" : variant == 2 ? "normals" : variant == 3 ? "RGBA" : "UV";
        static MeshSimplifier.SimplifySettings Variant(MeshSimplifier.SimplifySettings settings,int variant)
        {
            if (variant == 1) { settings.normalWeight *= .1f; settings.uv2Weight *= .1f; settings.colorWeight *= .1f; }
            if (variant == 2) { settings.normalWeight *= 4; settings.uv2Weight *= .25f; settings.colorWeight *= .5f; }
            if (variant == 3) { settings.colorWeight *= 4; settings.normalWeight *= .5f; settings.uv2Weight *= .25f; }
            if (variant == 4) { settings.uv2Weight *= 2; settings.normalWeight *= .5f; settings.colorWeight *= .5f; }
            return settings;
        }

        static MeshSimplifier.SimplifyResult SimplifyOne(Mesh source,MeshSimplifier.SimplifySettings settings,
            out MeshSimplifier.SimplifySettings selectedSettings,out int count,Func<bool> cancelled,bool protectedFloorExceedsBudget,ProbeEvaluation evaluation = null)
        {
            var best = new MeshSimplifier.SimplifyResult();
            selectedSettings = settings; count = 0; string error = null;
            float bestScore = float.PositiveInfinity;
            int target = Mathf.Max(1,Mathf.CeilToInt(LodMeshData.TriangleCount(source)*settings.targetRatio));
            try
            {
                for (int probe = 0; probe < (protectedFloorExceedsBudget ? 1 : 4); probe++)
                {
                    if (cancelled?.Invoke() == true) throw new OperationCanceledException("LOD generation cancelled.");
                    var attempt = settings;
                    if (probe > 0)
                    {
                        float weightScale = probe == 1 ? .1f : probe == 2 ? .001f : 0;
                        attempt.uv2Weight *= weightScale; attempt.normalWeight *= weightScale; attempt.colorWeight *= weightScale;
                        attempt.targetError = Mathf.Max(settings.targetError,probe == 1 ? .5f : 1);
                        attempt.allowAttributeSeamCollapse = probe >= 2;
                    }
                    UvProgress.Report(UvProgress.Current.fraction,$"{source.name}: triangle budget probe {probe+1}/4");
                    var candidate = MeshSimplifier.Simplify(source,attempt); count += 1+candidate.nativeRetries;
                    bool beltFloor = candidate.hardEdges?.beltFallback == true && candidate.hardEdges.protectedTriangles >= target;
                    try
                    {
                        if (!candidate.ok) { error = candidate.error; continue; }
                        if (candidate.simplifiedTriCount <= 0 || candidate.simplifiedMesh.vertexCount == 0) continue;
                        float score = 0;
                        if (evaluation != null)
                        {
                            var metrics = LodSurfaceValidation.MeasureMeshes(evaluation.source,candidate.simplifiedMesh,evaluation.settings,cancelled,ignoreDegenerateFaces:true);
                            var local = evaluation.screen?.Measure(candidate.simplifiedMesh,cancelled);
                            score = CandidateScore(metrics,Outline(evaluation.silhouette,candidate.simplifiedMesh,local,cancelled),local,evaluation.settings,evaluation.options);
                        }
                        bool preferable = evaluation == null ? candidate.simplifiedTriCount < best.simplifiedTriCount :
                            Better(candidate.simplifiedTriCount,score,best.simplifiedTriCount,bestScore,target,true);
                        if (!best.ok || preferable)
                        {
                            if (best.simplifiedMesh) UnityEngine.Object.DestroyImmediate(best.simplifiedMesh);
                            best = candidate; candidate.simplifiedMesh = null; selectedSettings = attempt; bestScore = score;
                        }
                    }
                    finally { if (candidate.simplifiedMesh) UnityEngine.Object.DestroyImmediate(candidate.simplifiedMesh); }
                    if (beltFloor) break; // Strict fallback has a proved floor; further budget relaxation cannot lower it.
                    if (best.ok && best.simplifiedTriCount <= target) break;
                }
                if (!best.ok) return new MeshSimplifier.SimplifyResult { error = error ?? "No nonempty triangle-budget candidate." };
                var result = best; best.simplifiedMesh = null;
                return result;
            }
            finally { if (best.simplifiedMesh) UnityEngine.Object.DestroyImmediate(best.simplifiedMesh); }
        }
    }
}
