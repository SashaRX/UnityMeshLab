// LodPipelineOps.cs — Shared LOD generation helper used by LodGenerationTool
// and PrefabBuilderTool's Build Pipeline. Encapsulates the meshoptimizer-driven
// simplification loop, prefab unpacking, LODGroup rebuild, scaleInLightmap
// propagation, and MeshEntry registration so both tools run identical logic.

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    internal static class LodPipelineOps
    {
        const string CancellationMessage = "LOD generation cancelled.";
        internal struct Options
        {
            public int count;
            public float[] ratios;
            public float targetError;
            public float uv2Weight;
            public float normalWeight;
            public float colorWeight;
            public LodReductionMode reductionMode;
            public float maxNormalAngle, maxColorError;
            public bool skipColorValidation;
            public int candidateCount;
            public bool lockBorder;
            public bool progressiveScaleInLightmap;
            public LevelQuality[] levelQuality;
            public LodSmallParts.Settings smallParts;
            public bool prioritizeTriangleBudget;
            public bool correctSurfaceAttributes;
            public bool preserveHardEdges;
        }

        internal struct LevelQuality
        {
            public float targetError, normalWeight, colorWeight, maxNormalAngle, maxColorError;
            internal bool IsValid => Positive(targetError) && Nonnegative(normalWeight) && Nonnegative(colorWeight) &&
                Nonnegative(maxNormalAngle) && maxNormalAngle <= 180 && Nonnegative(maxColorError);
            static bool Nonnegative(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0;
            static bool Positive(float value) => Nonnegative(value) && value > 0;
        }

        internal static Options ForLevel(Options options,int index)
        {
            if (options.levelQuality == null) return options;
            var quality = options.levelQuality[index];
            options.targetError = quality.targetError; options.normalWeight = quality.normalWeight;
            options.colorWeight = quality.colorWeight; options.maxNormalAngle = quality.maxNormalAngle;
            options.maxColorError = quality.maxColorError;
            return options;
        }

        internal static Options RelaxFarLods(Options baseline,int startLod,int firstRelaxedLod,LevelQuality far)
        {
            baseline.levelQuality = new LevelQuality[baseline.count];
            int lastLod = startLod+baseline.count-1;
            for (int i = 0; i < baseline.count; i++)
            {
                float t = Mathf.Clamp01((float)(startLod+i-firstRelaxedLod+1)/Mathf.Max(1,lastLod-firstRelaxedLod+1));
                baseline.levelQuality[i] = new LevelQuality {
                    targetError = Mathf.Lerp(baseline.targetError,Mathf.Max(baseline.targetError,far.targetError),t),
                    normalWeight = Mathf.Lerp(baseline.normalWeight,Mathf.Min(baseline.normalWeight,far.normalWeight),t),
                    colorWeight = Mathf.Lerp(baseline.colorWeight,Mathf.Min(baseline.colorWeight,far.colorWeight),t),
                    maxNormalAngle = Mathf.Lerp(baseline.maxNormalAngle,Mathf.Max(baseline.maxNormalAngle,far.maxNormalAngle),t),
                    maxColorError = Mathf.Lerp(baseline.maxColorError,Mathf.Max(baseline.maxColorError,far.maxColorError),t)
                };
            }
            return baseline;
        }

        internal struct LodInfo
        {
            public string meshName;
            public int simplifiedTris;
            public int lodLevel;
            public float targetRatio;
            public float actualRatio;
            public int targetTris;
            public bool targetNotReached;
            public int removedLoops, blockedLoops, sourceQuads;
            public float sourceDistance, normalError, colorError;
            public Vector4 colorMax, colorRms;
            public int evaluatedCandidates, selectedCandidate;
            public string reductionNote;
            public float allowedColorError, allowedNormalAngle, targetError, normalWeight, colorWeight;
            public int removedParts, removedPartTris;
            public float removedPartAreaFraction, removedPartMaxPixels;
            public bool budgetPriority, qualityLimitsExceeded;
            public int nativeProbes;
            public float sourceDistanceRms, normalRms, uvRms, silhouetteMean, silhouetteMax, selectionScore;
            public List<LodBudgetTriangleSimplifier.CandidateReport> budgetCandidates;
            public LodAttributeCorrection.Report attributeCorrection;
            public LodHardEdges.Report hardEdges;
        }

        internal class Result
        {
            public bool ok;
            public string error;
            public List<LodInfo> perLod = new List<LodInfo>();
            public List<GameObject> generatedObjects = new List<GameObject>();
        }

        internal static bool TryPrepareSources(UvToolContext ctx, LodReductionMode mode,
            out Dictionary<Mesh, LodSourceTopology> sources, out string error)
        {
            sources = new Dictionary<Mesh, LodSourceTopology>();
            error = null;
            if (mode == LodReductionMode.Triangles) return true;
            var imports = new Dictionary<string, object>();
            UvProgress.Begin("Read original LOD polygons", cancelable: true);
            try
            {
                foreach (var entry in ctx.MeshEntries)
                {
                    if (!entry.include || entry.lodIndex != ctx.SourceLodIndex) continue;
                    var mesh = entry.repackedMesh ?? entry.originalMesh;
                    if (mesh == null || sources.ContainsKey(mesh)) continue;
                    if (UvProgress.CancelRequested) { error = "Source topology extraction cancelled."; return false; }
                    UvProgress.Report(0, mesh.name);
                    if (!LodSourceTopology.TryLoad(entry, mesh, imports, out var source, out var reason))
                    { error = $"{mesh.name}: {reason}"; return false; }
                    sources.Add(mesh, source);
                }
                if (sources.Count == 0) { error = "No source meshes found."; return false; }
                return true;
            }
            finally { UvProgress.End(); }
        }

        internal static bool TryPrepareParts(UvToolContext ctx,LodSmallParts.Settings settings,
            out Dictionary<Mesh,LodSmallParts.Analysis> analyses,out string error)
        {
            analyses = new Dictionary<Mesh,LodSmallParts.Analysis>(); error = null;
            if (!settings.enabled) return true;
            if (!settings.IsValid) { error = "Invalid small-part settings"; return false; }
            var imports = new Dictionary<string,object>();
            UvProgress.Begin("Analyze small LOD parts",cancelable:true);
            try
            {
                foreach (var entry in ctx.MeshEntries)
                {
                    var mesh = entry.repackedMesh ?? entry.originalMesh;
                    if (!entry.include || entry.lodIndex != ctx.SourceLodIndex || !mesh || analyses.ContainsKey(mesh)) continue;
                    UvProgress.Report(0,mesh.name);
                    analyses.Add(mesh,LodSmallParts.Analyze(entry,mesh,imports,() => UvProgress.CancelRequested));
                }
                return true;
            }
            catch (System.Exception ex) { error = ex.Message; analyses.Clear(); return false; }
            finally { UvProgress.End(); }
        }

        internal static Result Generate(UvToolContext ctx, int startLod, Options opts, Dictionary<Mesh, LodSourceTopology> prepared = null,
            Dictionary<Mesh,LodSmallParts.Analysis> partAnalyses = null)
        {
            var result = new Result();
            if (ctx?.LodGroup == null) { result.error = "No LODGroup"; return result; }
            if (opts.ratios == null || opts.count <= 0) { result.error = "No ratios"; return result; }
            if (opts.count > opts.ratios.Length) { result.error = "Missing LOD ratios"; return result; }
            if (opts.smallParts.enabled && !opts.smallParts.IsValid) { result.error = "Invalid small-part settings"; return result; }
            if (opts.levelQuality != null)
            {
                if (opts.levelQuality.Length < opts.count) { result.error = "Missing per-LOD quality settings"; return result; }
                for (int i = 0; i < opts.count; i++)
                    if (!opts.levelQuality[i].IsValid) { result.error = $"Invalid quality settings for LOD{startLod+i}"; return result; }
            }
            if (prepared == null && !TryPrepareSources(ctx, opts.reductionMode, out prepared, out result.error)) return result;
            if (partAnalyses == null && !TryPrepareParts(ctx,opts.smallParts,out partAnalyses,out result.error)) return result;
            if (opts.smallParts.enabled)
                foreach (var analysis in partAnalyses.Values)
                    if (!analysis.Matches(analysis.source.data.source)) { result.error = "Small-part source geometry changed after preflight"; return result; }

            var lgGo = ctx.LodGroup.gameObject;
            if (PrefabUtility.IsPartOfPrefabInstance(lgGo))
            {
                var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(lgGo);
                if (outer != null)
                {
                    UvtLog.Info($"[LodPipelineOps] Unpacking prefab instance '{outer.name}' before LOD regeneration.");
                    PrefabUtility.UnpackPrefabInstance(outer, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                }
            }

            var sourceMeshes = new List<(MeshEntry entry, Mesh mesh)>();
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.lodIndex != ctx.SourceLodIndex) continue;
                Mesh src = e.repackedMesh ?? e.originalMesh;
                if (src != null) sourceMeshes.Add((e, src));
            }
            if (sourceMeshes.Count == 0) { result.error = "No source meshes found"; return result; }

            UvToolContext.CompactLodArray(ctx.LodGroup, removeEmptySlots: true);
            var lods = ctx.LodGroup.GetLODs();
            var newLods = new List<LOD>(lods);

            // A group created from unlabelled renderers uses a low value so its only
            // LOD is not culled early. Restore the normal LOD0 transition before
            // appending generated levels; otherwise they start at 0.005 and below.
            LodGroupUtility.NormalizeSingleLodTransitionForGeneration(newLods, startLod);

            UvProgress.Begin($"Generate LODs ({opts.count} levels)", cancelable: true);
            var validatedTriangles = new Dictionary<Mesh,(Mesh mesh,Options options)>(); // Borrowed meshes; Generate owns them.
            try
            {
                for (int lodIdx = 0; lodIdx < opts.count; lodIdx++)
                {
                    if (UvProgress.CancelRequested) break;
                    float ratio = opts.ratios[lodIdx];
                    var levelOptions = ForLevel(opts,lodIdx);
                    var settings = new MeshSimplifier.SimplifySettings
                    {
                        targetRatio  = ratio,
                        targetError  = levelOptions.targetError,
                        uv2Weight    = opts.uv2Weight,
                        normalWeight = levelOptions.normalWeight,
                        colorWeight  = levelOptions.colorWeight,
                        lockBorder   = opts.lockBorder,
                        preserveHardEdges = opts.preserveHardEdges,
                        uvChannel    = 1
                    };

                    float progress = (float)lodIdx / opts.count;
                    UvProgress.Report(progress,
                        $"LOD{startLod + lodIdx} (ratio {ratio:P0})");

                    var lodRenderers = new List<Renderer>();
                    int lodLevel = startLod + lodIdx;

                    var parentToContainer = new Dictionary<Transform, Transform>();

                    foreach (var (entry, srcMesh) in sourceMeshes)
                    {
                        if (UvProgress.CancelRequested) break;
                        validatedTriangles.TryGetValue(srcMesh,out var previous);
                        // A candidate accepted under weaker native constraints must
                        // not bypass a later, stricter profile via color-only reuse.
                        Mesh previousValidated = levelOptions.targetError >= previous.options.targetError &&
                            levelOptions.normalWeight <= previous.options.normalWeight && levelOptions.colorWeight <= previous.options.colorWeight
                            ? previous.mesh : null;
                        var partPlan = new LodSmallParts.Plan();
                        LodSourceTopology retained = null;
                        var reductionSources = prepared;
                        var reductionMesh = srcMesh;
                        var reductionSettings = settings;
                        if (partAnalyses.TryGetValue(srcMesh,out var analysis))
                        {
                            float entryHeight = lodLevel > 0 && lodLevel-1 < newLods.Count ? newLods[lodLevel-1].screenRelativeTransitionHeight : 1;
                            var scale = ctx.LodGroup.transform.lossyScale;
                            float worldSize = ctx.LodGroup.size*Mathf.Max(Mathf.Abs(scale.x),Mathf.Max(Mathf.Abs(scale.y),Mathf.Abs(scale.z)));
                            partPlan = LodSmallParts.Select(analysis,opts.smallParts,lodLevel,entryHeight,
                                entry.renderer ? entry.renderer.localToWorldMatrix : Matrix4x4.identity,worldSize);
                            if (partPlan.removed.Count > 0)
                            {
                                retained = LodSmallParts.Retain(analysis,partPlan);
                                reductionMesh = retained.data.source;
                                reductionSettings.targetRatio = Mathf.Min(1,ratio*analysis.triangles/LodMeshData.TriangleCount(reductionMesh));
                                reductionSources = new Dictionary<Mesh,LodSourceTopology>(prepared) { [reductionMesh] = retained };
                                // Reusing a candidate with a different retained surface can reintroduce deleted parts.
                                previousValidated = null;
                            }
                        }
                        MeshSimplifier.SimplifyResult r;
                        LodLoopSimplifier.Result loop;
                        string reductionNote;
                        try
                        {
                            r = Reduce(reductionMesh, reductionSettings, levelOptions, reductionSources, out loop, out reductionNote,previousValidated);
                            if (opts.prioritizeTriangleBudget && opts.reductionMode == LodReductionMode.LoopsThenTriangles &&
                                (!r.ok || r.simplifiedTriCount > Mathf.CeilToInt(TriCount(reductionMesh)*reductionSettings.targetRatio)))
                            {
                                if (r.simplifiedMesh) Object.DestroyImmediate(r.simplifiedMesh);
                                r = LodBudgetTriangleSimplifier.Simplify(reductionMesh,reductionSettings,levelOptions,out loop,out reductionNote,() => UvProgress.CancelRequested);
                                reductionNote = "Full-loop result missed the budget; triangle fallback from source. "+reductionNote;
                            }
                            if (r.ok && opts.preserveHardEdges)
                            {
                                var protection = new LodHardEdges(reductionMesh);
                                bool sourceFallback = r.hardEdges?.sourceFallback ?? false;
                                r.hardEdges = protection.Measure(r.simplifiedMesh);
                                r.hardEdges.sourceFallback = sourceFallback;
                                if (!r.hardEdges.Valid)
                                {
                                    LodSurfaceValidation.Metrics restoredMetrics;
                                    try { restoredMetrics = LodSurfaceValidation.MeasureMeshes(reductionMesh,reductionMesh,reductionSettings,() => UvProgress.CancelRequested,ignoreDegenerateFaces:true); }
                                    catch { Object.DestroyImmediate(r.simplifiedMesh); throw; }
                                    Object.DestroyImmediate(r.simplifiedMesh);
                                    r.simplifiedMesh = Object.Instantiate(reductionMesh);
                                    r.draftUv = MeshUvState.IsDraft(reductionMesh); MeshUvState.SetDraft(r.simplifiedMesh,r.draftUv);
                                    r.simplifiedTriCount = TriCount(reductionMesh); r.resultError = 0;
                                    r.hardEdges = protection.Measure(r.simplifiedMesh); r.hardEdges.sourceFallback = true;
                                    loop = new LodLoopSimplifier.Result { metrics = restoredMetrics };
                                    reductionNote = "Hard edge / protected face / patch-interface validation rejected reduction; kept source mesh. ";
                                }
                                reductionNote += " "+r.hardEdges.Note;
                            }
                        }
                        catch (System.OperationCanceledException) { r = new MeshSimplifier.SimplifyResult { error = CancellationMessage }; loop = null; reductionNote = null; }
                        finally { if (retained != null) Object.DestroyImmediate(retained.data.source); }
                        if (partPlan.removed.Count > 0)
                            reductionNote = $"Removed {partPlan.removed.Count} disconnected parts ({partPlan.triangles} source tris, " +
                                $"{partPlan.area/analysis.area:P2} area, estimated ≤{partPlan.maxPixels:F2} px). Surface errors use retained LOD0. "+reductionNote;
                        else if (partPlan.note != null) reductionNote = partPlan.note+" "+reductionNote;
                        if (!r.ok) { UvtLog.Error($"[LodPipelineOps] Failed on {srcMesh.name}: {r.error}"); continue; }
                        if (partPlan.removed.Count == 0 && opts.reductionMode == LodReductionMode.Triangles &&
                            ((opts.prioritizeTriangleBudget && opts.preserveHardEdges) ||
                             (!opts.prioritizeTriangleBudget && !opts.skipColorValidation && levelOptions.maxColorError > 0)))
                            validatedTriangles[srcMesh] = (r.simplifiedMesh,levelOptions);

                        int sourceTriCount = TriCount(srcMesh);
                        float actualRatio = sourceTriCount > 0
                            ? (float)r.simplifiedTriCount / sourceTriCount : 1f;
                        int targetTris = Mathf.Max(1, Mathf.CeilToInt(sourceTriCount * ratio));
                        bool targetNotReached = r.simplifiedTriCount > targetTris;
                        if (targetNotReached)
                            UvtLog.Warn($"[LodPipelineOps] LOD{lodLevel}: target ≈ {targetTris:N0} tris ({ratio:P1}), got {r.simplifiedTriCount:N0} ({actualRatio:P1}). " +
                                $"Target Error={levelOptions.targetError:G3}, achieved error={r.resultError:G3}, UV2 Weight={opts.uv2Weight:G3}, " +
                                $"Normal Weight={levelOptions.normalWeight:G3}, Max Color Error={levelOptions.maxColorError:G3}, Lock Border={opts.lockBorder}, Preserve Hard Edges={opts.preserveHardEdges}. Error / attribute constraints or topology prevent further reduction.");

                        string baseName = entry.fbxMesh != null ? entry.fbxMesh.name : srcMesh.name;
                        baseName = MeshNaming.StripPipelineSuffixes(baseName);
                        string meshName = baseName + "_LOD" + lodLevel;
                        r.simplifiedMesh.name = meshName;

                        UvtLog.Info($"[LodPipelineOps] {meshName}: {r.originalTriCount} → {r.simplifiedTriCount} tris ({actualRatio:P0})");

                        result.perLod.Add(new LodInfo
                        {
                            meshName = meshName,
                            simplifiedTris = r.simplifiedTriCount,
                            lodLevel = lodLevel,
                            targetRatio = ratio,
                            actualRatio = actualRatio,
                            targetTris = targetTris,
                            targetNotReached = targetNotReached,
                            removedLoops = loop?.removedLoops ?? 0,
                            blockedLoops = loop?.blockedLoops ?? 0,
                            sourceQuads = loop?.sourceQuads ?? 0,
                            sourceDistance = loop?.maxDistance ?? 0,
                            normalError = loop?.maxNormalAngle ?? 0,
                            colorError = loop?.maxColorError ?? 0,
                            colorMax = loop?.metrics.colorMax ?? Vector4.zero,
                            colorRms = loop?.metrics.ColorRms ?? Vector4.zero,
                            evaluatedCandidates = loop?.evaluatedCandidates ?? 1,
                            selectedCandidate = loop?.selectedCandidate ?? 1,
                            nativeProbes = loop?.nativeProbes ?? 0,
                            sourceDistanceRms = loop?.metrics.DistanceRms ?? 0,normalRms = loop?.metrics.NormalRms ?? 0,uvRms = loop?.metrics.UvRms ?? 0,
                            silhouetteMean = loop?.silhouetteMean ?? 0,silhouetteMax = loop?.silhouetteMax ?? 0,selectionScore = loop?.selectionScore ?? 0,
                            budgetCandidates = loop?.budgetCandidates,
                            attributeCorrection = loop?.attributeCorrection,
                            hardEdges = r.hardEdges,
                            reductionNote = reductionNote,
                            allowedColorError = opts.skipColorValidation ? 0 : levelOptions.maxColorError,
                            allowedNormalAngle = levelOptions.maxNormalAngle, targetError = levelOptions.targetError,
                            normalWeight = levelOptions.normalWeight, colorWeight = levelOptions.colorWeight,
                            removedParts = partPlan.removed.Count, removedPartTris = partPlan.triangles,
                            removedPartAreaFraction = analysis != null && analysis.area > 0 ? (float)(partPlan.area/analysis.area) : 0,
                            removedPartMaxPixels = partPlan.maxPixels,
                            budgetPriority = opts.prioritizeTriangleBudget && opts.reductionMode != LodReductionMode.FullLoops,
                            qualityLimitsExceeded = loop != null && ((!opts.skipColorValidation && levelOptions.maxColorError > 0 && loop.maxColorError > levelOptions.maxColorError) ||
                                (levelOptions.maxNormalAngle > 0 && loop.maxNormalAngle > levelOptions.maxNormalAngle))
                        });

                        if (entry.renderer != null)
                        {
                            var go = new GameObject(meshName);
                            Undo.RegisterCreatedObjectUndo(go, "Generate LOD");

                            Transform srcParent = entry.renderer.transform.parent;
                            Transform lodGroupTransform = ctx.LodGroup.transform;

                            if (srcParent != lodGroupTransform && srcParent != null)
                            {
                                if (parentToContainer.TryGetValue(srcParent, out var container))
                                    go.transform.SetParent(container, false);
                                else
                                    go.transform.SetParent(lodGroupTransform, false);
                            }
                            else
                            {
                                go.transform.SetParent(lodGroupTransform, false);
                            }

                            parentToContainer[entry.renderer.transform] = go.transform;
                            go.transform.localPosition = entry.renderer.transform.localPosition;
                            go.transform.localRotation = entry.renderer.transform.localRotation;
                            go.transform.localScale    = entry.renderer.transform.localScale;
                            Undo.RegisterCreatedObjectUndo(r.simplifiedMesh, "Generate LOD Mesh");
                            ctx.GeneratedLodMeshes[go] = r.simplifiedMesh;
                            var mf = go.AddComponent<MeshFilter>();
                            mf.sharedMesh = r.simplifiedMesh;
                            var mr = go.AddComponent<MeshRenderer>();
                            RendererSettings.Copy(entry.renderer, mr);

                            if (opts.progressiveScaleInLightmap && lodLevel > 0)
                            {
                                Undo.RecordObject(mr, "Set scaleInLightmap");
                                mr.scaleInLightmap = Mathf.Pow(0.5f, lodLevel);
                            }

                            GameObjectUtility.SetStaticEditorFlags(go,
                                GameObjectUtility.GetStaticEditorFlags(entry.renderer.gameObject));
                            result.generatedObjects.Add(go);
                            lodRenderers.Add(mr);
                        }
                    }

                    if (lodRenderers.Count > 0)
                    {
                        if (lodLevel < newLods.Count)
                        {
                            var oldRenderers = newLods[lodLevel].renderers;
                            if (oldRenderers != null)
                                foreach (var oldR in oldRenderers)
                                    if (oldR != null && oldR.gameObject != null)
                                        Undo.DestroyObjectImmediate(oldR.gameObject);
                            newLods[lodLevel] = new LOD(newLods[lodLevel].screenRelativeTransitionHeight, lodRenderers.ToArray());
                        }
                        else
                        {
                            float baseHeight = newLods.Count > 0 ? newLods[newLods.Count - 1].screenRelativeTransitionHeight : 0.5f;
                            newLods.Add(new LOD(baseHeight * 0.5f, lodRenderers.ToArray()));
                        }
                    }
                }

                ctx.LodGroup = LodGroupUtility.Rebuild(ctx.LodGroup.gameObject, newLods.ToArray());
                AssetDatabase.SaveAssets();
            }
            finally { UvProgress.End(); }

            foreach (var (entry, srcMesh) in sourceMeshes)
            {
                if (entry.renderer == null) continue;
                if (!MeshNaming.HasLodSuffix(entry.renderer.name))
                {
                    Undo.RecordObject(entry.renderer.gameObject, "Rename LOD0");
                    string newName = entry.renderer.gameObject.name + "_LOD0";
                    UvtLog.Info($"[LodPipelineOps] Renamed source: {entry.renderer.gameObject.name} → {newName}");
                    entry.renderer.gameObject.name = newName;
                }
            }

            RegisterNewLodEntries(ctx);
            ctx.GeneratedLodObjects.RemoveAll(go => go == null);
            ctx.GeneratedLodObjects.AddRange(result.generatedObjects);
            ctx.ClearAllCaches();
            result.ok = true;
            return result;
        }

        static MeshSimplifier.SimplifyResult Reduce(Mesh mesh, MeshSimplifier.SimplifySettings settings, Options options,
            Dictionary<Mesh, LodSourceTopology> sources, out LodLoopSimplifier.Result loop, out string note,Mesh previousValidated = null)
        {
            loop = null; note = null;
            if (options.reductionMode != LodReductionMode.LoopsThenTriangles)
            {
                try { return ReduceOne(mesh, settings, options, sources, 0, out loop, out note,previousValidated); }
                catch (System.OperationCanceledException) { return new MeshSimplifier.SimplifyResult { error = CancellationMessage }; }
            }
            var best = new MeshSimplifier.SimplifyResult();
            int target = Mathf.Max(1, Mathf.CeilToInt(LodMeshData.TriangleCount(mesh) * settings.targetRatio));
            int count = Mathf.Clamp(options.candidateCount, 1, 5);
            var single = options; single.candidateCount = 1;
            try
            {
                for (int variant = 0; variant < count; variant++)
                {
                    UvProgress.Report(UvProgress.Current.fraction, $"{mesh.name}: variant {variant + 1}/{count}");
                    if (UvProgress.CancelRequested) return new MeshSimplifier.SimplifyResult { error = CancellationMessage };
                    var candidate = ReduceOne(mesh, settings, single, sources, variant, out var candidateLoop, out var candidateNote);
                    if (!candidate.ok) return candidate;
                    // Compare complete hybrid results, not only the intermediate loop meshes.
                    try { candidateLoop.metrics = LodSurfaceValidation.MeasureMeshes(mesh, candidate.simplifiedMesh, settings, () => UvProgress.CancelRequested); }
                    catch { UnityEngine.Object.DestroyImmediate(candidate.simplifiedMesh); throw; }
                    if (!best.ok || LodLoopSimplifier.BetterResult(candidateLoop, loop, target))
                    {
                        if (best.simplifiedMesh != null) UnityEngine.Object.DestroyImmediate(best.simplifiedMesh);
                        best = candidate; loop = candidateLoop; note = candidateNote;
                    }
                    else UnityEngine.Object.DestroyImmediate(candidate.simplifiedMesh);
                }
                loop.evaluatedCandidates = count;
                var chosen = best;
                best.simplifiedMesh = null; // Transfer ownership to Generate.
                return chosen;
            }
            catch (System.OperationCanceledException) { return new MeshSimplifier.SimplifyResult { error = CancellationMessage }; }
            finally { if (best.simplifiedMesh != null) UnityEngine.Object.DestroyImmediate(best.simplifiedMesh); }
        }

        static MeshSimplifier.SimplifyResult ReduceOne(Mesh mesh, MeshSimplifier.SimplifySettings settings, Options options,
            Dictionary<Mesh, LodSourceTopology> sources, int strategy, out LodLoopSimplifier.Result loop, out string note,Mesh previousValidated = null)
        {
            loop = null; note = null;
            if (options.reductionMode == LodReductionMode.Triangles)
            {
                if (options.prioritizeTriangleBudget)
                    return LodBudgetTriangleSimplifier.Simplify(mesh,settings,options,out loop,out note,() => UvProgress.CancelRequested,previousValidated);
                return LodValidatedTriangleSimplifier.Simplify(mesh,settings,options,out loop,out note,() => UvProgress.CancelRequested,previousValidated);
            }
            loop = LodLoopSimplifier.Simplify(sources[mesh], new LodLoopSimplifier.Settings
            {
                simplify = settings, maxNormalAngle = options.maxNormalAngle, maxColorError = options.maxColorError,
                skipColorValidation = options.skipColorValidation, candidateCount = options.candidateCount, strategyOffset = strategy
            }, () => UvProgress.CancelRequested, (index, count) => UvProgress.Report(UvProgress.Current.fraction, $"{mesh.name}: loop variant {index}/{count}"));
            if (!loop.ok) return new MeshSimplifier.SimplifyResult { error = loop.error };
            int originalCount = LodMeshData.TriangleCount(mesh), loopCount = LodMeshData.TriangleCount(loop.mesh);
            var result = new MeshSimplifier.SimplifyResult
            {
                ok = true, simplifiedMesh = loop.mesh, originalTriCount = originalCount,
                simplifiedTriCount = loopCount, resultError = loop.maxDistance, draftUv = MeshUvState.IsDraft(loop.mesh)
            };
            if (options.reductionMode != LodReductionMode.LoopsThenTriangles || loopCount <= Mathf.CeilToInt(originalCount * settings.targetRatio)) return result;
            var triangleSettings = settings;
            triangleSettings.targetRatio = Mathf.Clamp01(originalCount * settings.targetRatio / loopCount);
            var reduced = MeshSimplifier.Simplify(loop.mesh, triangleSettings);
            if (!reduced.ok) { note = $"Triangle stage skipped: {reduced.error}"; return result; }
            try
            {
                var metrics = LodSurfaceValidation.MeasureMeshes(mesh, reduced.simplifiedMesh, settings, () => UvProgress.CancelRequested);
                if (metrics.weightedError > settings.targetError || metrics.normalAngle > options.maxNormalAngle ||
                    (!options.skipColorValidation && metrics.colorError > options.maxColorError))
                {
                    UnityEngine.Object.DestroyImmediate(reduced.simplifiedMesh);
                    note = "Triangle stage rejected by LOD0 shape / attribute validation; kept full-loop result.";
                    return result;
                }
                UnityEngine.Object.DestroyImmediate(loop.mesh);
                loop.mesh = reduced.simplifiedMesh;
                loop.maxDistance = metrics.distance; loop.maxNormalAngle = metrics.normalAngle; loop.maxColorError = metrics.colorError;
                loop.metrics = metrics;
                reduced.originalTriCount = originalCount;
                reduced.resultError = metrics.distance;
                note = "Triangle stage applied; complete-loop constraint relaxed.";
                return reduced;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(reduced.simplifiedMesh);
                UnityEngine.Object.DestroyImmediate(loop.mesh);
                throw;
            }
        }

        static void RegisterNewLodEntries(UvToolContext ctx)
        {
            var currentLods = ctx.LodGroup.GetLODs();
            for (int li = 0; li < currentLods.Length; li++)
            {
                bool alreadyRegistered = false;
                foreach (var e in ctx.MeshEntries)
                    if (e.lodIndex == li) { alreadyRegistered = true; break; }
                if (alreadyRegistered) continue;

                if (currentLods[li].renderers == null) continue;
                foreach (var r in currentLods[li].renderers)
                {
                    if (r == null) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var fbm = mf.sharedMesh;
                    var uv2Check = new List<Vector2>();
                    fbm.GetUVs(1, uv2Check);
                    ctx.MeshEntries.Add(new MeshEntry
                    {
                        lodIndex = li,
                        renderer = r,
                        meshFilter = mf,
                        originalMesh = fbm,
                        fbxMesh = fbm,
                        hasExistingUv2 = uv2Check.Count > 0,
                        meshGroupKey = UvToolContext.ExtractGroupKey(r.name)
                    });
                }
            }
        }

        static int TriCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            return LodMeshData.TriangleCount(mesh);
        }
    }
}
