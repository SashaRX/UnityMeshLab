using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class LodBudgetTriangleSimplifier
    {
        internal struct CandidateReport
        {
            internal int variant, triangles, nativeProbes;
            internal string name;
            internal float score, distanceRms, normalRms, colorRms, uvRms, silhouetteMean, silhouetteMax;
        }
        // Budget-first generation still uses edge collapse. It never drops arbitrary
        // triangles to manufacture a count, and never simplifies a preceding LOD.
        internal static MeshSimplifier.SimplifyResult Simplify(Mesh source,MeshSimplifier.SimplifySettings settings,
            LodPipelineOps.Options options,out LodLoopSimplifier.Result diagnostics,out string note,Func<bool> cancelled,Mesh previousProtected = null)
        {
            diagnostics = null; note = null;
            if (!source || source.vertexCount == 0) return new MeshSimplifier.SimplifyResult { error = "Source mesh has no vertices." };
            var best = new MeshSimplifier.SimplifyResult();
            var selectedSettings = settings;
            var bestMetrics = new LodSurfaceValidation.Metrics();
            var bestSilhouette = new LodSilhouetteValidation.Metrics();
            float bestScore = float.PositiveInfinity;
            int selected = 0, probes = 0;
            int target = Mathf.Max(1,Mathf.CeilToInt(LodMeshData.TriangleCount(source)*settings.targetRatio));
            bool protectedFloorExceedsBudget = settings.preserveHardEdges && new LodHardEdges(source).protectedTriangles >= target;
            int previousCount = previousProtected ? LodMeshData.TriangleCount(previousProtected) : 0;
            int densityLimit = settings.preserveHardEdges && previousCount > target && new LodHardEdges(source).Measure(previousProtected).Valid
                ? previousCount : int.MaxValue;
            bool reusedPrevious = false;
            int count = Mathf.Clamp(options.candidateCount,1,5);
            var reports = new List<CandidateReport>();
            var silhouette = new LodSilhouetteValidation(source,cancelled);
            try
            {
                for (int variant = 0; variant < count; variant++)
                {
                    UvProgress.Report(UvProgress.Current.fraction,$"{source.name}: quality variant {variant+1}/{count}");
                    var candidate = SimplifyOne(source,Variant(settings,variant),out var nativeSettings,out int attempts,cancelled,protectedFloorExceedsBudget);
                    probes += attempts;
                    try
                    {
                        if (!candidate.ok) { note = candidate.error; continue; }
                        var metrics = LodSurfaceValidation.MeasureMeshes(source,candidate.simplifiedMesh,settings,cancelled,ignoreDegenerateFaces:true);
                        var outline = silhouette.Measure(candidate.simplifiedMesh,cancelled);
                        float score = Score(metrics,outline,settings,options);
                        reports.Add(new CandidateReport { variant = variant+1,name = VariantName(variant),triangles = candidate.simplifiedTriCount,
                            nativeProbes = attempts,score = score,distanceRms = metrics.DistanceRms,normalRms = metrics.NormalRms,
                            colorRms = Maximum(metrics.ColorRms),uvRms = metrics.UvRms,silhouetteMean = outline.mean,silhouetteMax = outline.maximum });
                        if (!best.ok || Better(candidate.simplifiedTriCount,score,best.simplifiedTriCount,bestScore,target,protectedFloorExceedsBudget,densityLimit))
                        {
                            if (best.simplifiedMesh) UnityEngine.Object.DestroyImmediate(best.simplifiedMesh);
                            best = candidate; candidate.simplifiedMesh = null;
                            bestMetrics = metrics; bestSilhouette = outline; bestScore = score;
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
                    bestMetrics = metrics; bestSilhouette = outline; bestScore = Score(metrics,outline,settings,options);
                    selected = count+1; reusedPrevious = true;
                    reports.Add(new CandidateReport { variant = selected,name = "previous protected source candidate",triangles = previousCount,
                        score = bestScore,distanceRms = metrics.DistanceRms,normalRms = metrics.NormalRms,colorRms = Maximum(metrics.ColorRms),
                        uvRms = metrics.UvRms,silhouetteMean = outline.mean,silhouetteMax = outline.maximum });
                }
                LodAttributeCorrection.Report correction = null;
                if (options.correctSurfaceAttributes)
                {
                    UvProgress.Report(UvProgress.Current.fraction,$"{source.name}: verify surface attribute correction");
                    var corrected = LodAttributeCorrection.Correct(source,best.simplifiedMesh,settings,options,
                        out correction,out bestMetrics,cancelled,bestMetrics);
                    if (corrected)
                    {
                        UnityEngine.Object.DestroyImmediate(best.simplifiedMesh); best.simplifiedMesh = corrected;
                        bestScore = Score(bestMetrics,bestSilhouette,settings,options);
                    }
                }
                MeshUvState.SetDraft(best.simplifiedMesh,best.draftUv);
                diagnostics = new LodLoopSimplifier.Result { metrics = bestMetrics,maxDistance = bestMetrics.distance,
                    maxNormalAngle = bestMetrics.normalAngle,maxColorError = bestMetrics.colorError,
                    evaluatedCandidates = reports.Count,selectedCandidate = selected,nativeProbes = probes,
                    silhouetteMean = bestSilhouette.mean,silhouetteMax = bestSilhouette.maximum,selectionScore = bestScore,budgetCandidates = reports,
                    attributeCorrection = correction };
                string selectedName = reusedPrevious ? "previous protected source candidate" : VariantName(selected-1);
                note = $"Triangle budget prioritized; selected {selectedName} from {reports.Count} quality candidates ({probes} native probes). " +
                "Selection measures six-view silhouette, area RMS geometry/normals/UV/RGBA against source; score is a relative ranking heuristic.";
                if (protectedFloorExceedsBudget) note += " Protected face floor already reaches/exceeds the requested budget; no error/weight relaxation. Over-budget candidates ranked by measured quality.";
                if (reusedPrevious) note += " All new candidates exceeded the preceding protected density; cloned and remeasured that source-derived geometry. No recursive collapse; reported result error is sampled weighted error, not a new native bound.";
                else
                {
                    if (selectedSettings.targetError > settings.targetError) note += " Native costs/error relaxed; Lock Border retained.";
                    if (selectedSettings.allowAttributeSeamCollapse) note += " Attribute seam collapses allowed.";
                    note += $" Selected native Target Error={selectedSettings.targetError:G3}, UV/Normal/Color weights=" +
                        $"{selectedSettings.uv2Weight:G3}/{selectedSettings.normalWeight:G3}/{selectedSettings.colorWeight:G3}.";
                }
                if (correction != null) note += " "+correction.note;
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
            if (triangles > target)
                return protectedFloorExceedsBudget ? score < bestScore || (score == bestScore && triangles < bestTriangles) :
                    triangles < bestTriangles || (triangles == bestTriangles && score < bestScore);
            int minimum = Mathf.Max(1,target-Mathf.Max(2,Mathf.CeilToInt(target*.05f)));
            if ((triangles >= minimum) != (bestTriangles >= minimum)) return triangles >= minimum;
            if (score != bestScore) return score < bestScore;
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
            out MeshSimplifier.SimplifySettings selectedSettings,out int count,Func<bool> cancelled,bool protectedFloorExceedsBudget)
        {
            var best = new MeshSimplifier.SimplifyResult();
            selectedSettings = settings; count = 0; string error = null;
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
                    var candidate = MeshSimplifier.Simplify(source,attempt); count++;
                    try
                    {
                        if (!candidate.ok) { error = candidate.error; continue; }
                        if (candidate.simplifiedTriCount <= 0 || candidate.simplifiedMesh.vertexCount == 0) continue;
                        if (!best.ok || candidate.simplifiedTriCount < best.simplifiedTriCount)
                        {
                            if (best.simplifiedMesh) UnityEngine.Object.DestroyImmediate(best.simplifiedMesh);
                            best = candidate; candidate.simplifiedMesh = null; selectedSettings = attempt;
                        }
                    }
                    finally { if (candidate.simplifiedMesh) UnityEngine.Object.DestroyImmediate(candidate.simplifiedMesh); }
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
