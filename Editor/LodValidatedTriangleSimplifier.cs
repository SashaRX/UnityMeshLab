using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    // Every probe starts from LOD0. Failed probes stop validation early; accepted
    // probes always complete both sampling directions and all material slots.
    internal static class LodValidatedTriangleSimplifier
    {
        internal static MeshSimplifier.SimplifyResult Simplify(Mesh source,MeshSimplifier.SimplifySettings settings,
            LodPipelineOps.Options options,out LodLoopSimplifier.Result diagnostics,out string note,Func<bool> cancelled = null,Mesh previousValidated = null)
        {
            diagnostics = null; note = null;
            bool colors = source.HasVertexAttribute(VertexAttribute.Color);
            if (!colors || options.maxColorError <= 0) return MeshSimplifier.Simplify(source,settings);
            var limits = LodSurfaceValidation.Limits.Unbounded;
            if (!options.skipColorValidation) limits.color = options.maxColorError;
            var best = new MeshSimplifier.SimplifyResult();
            var bestMetrics = new LodSurfaceValidation.Metrics();
            int evaluations = 0, selected = 0;
            try
            {
                int variants = Mathf.Clamp(options.candidateCount,1,5);
                for (int variant = 0; variant < variants; variant++)
                {
                    var probeSettings = settings;
                    if (variant > 0) probeSettings.colorWeight = Mathf.Max(settings.colorWeight,1)*Mathf.Pow(4,variant);
                    Evaluate(probeSettings);
                    if (best.ok && best.simplifiedTriCount <= Mathf.CeilToInt(best.originalTriCount*settings.targetRatio)) break;
                }
                // Reuse an already generated candidate, never simplify it again.
                // Revalidate against LOD0 so a farther level cannot become denser
                // merely because its independent budget search found another result.
                if (previousValidated != null && (!best.ok || LodMeshData.TriangleCount(previousValidated) < best.simplifiedTriCount))
                    EvaluateMesh(new MeshSimplifier.SimplifyResult { ok = true,simplifiedMesh = UnityEngine.Object.Instantiate(previousValidated),
                        originalTriCount = LodMeshData.TriangleCount(source),simplifiedTriCount = LodMeshData.TriangleCount(previousValidated),
                        draftUv = MeshUvState.IsDraft(previousValidated) });
                if ((!best.ok || best.simplifiedTriCount > Mathf.CeilToInt(best.originalTriCount*settings.targetRatio)) && !options.skipColorValidation)
                {
                    // The native error/attribute bound is not a maximum RGBA bound.
                    // Search safer requested budgets; do not assume measured error
                    // is monotonic or accept a candidate merely because it is denser.
                    float rejected = settings.targetRatio, accepted = best.ok ? (float)best.simplifiedTriCount/best.originalTriCount : 1;
                    for (int step = 0; step < 7; step++)
                    {
                        var probeSettings = settings;
                        probeSettings.targetRatio = (rejected+accepted)*.5f;
                        bool valid = Evaluate(probeSettings);
                        if (valid) accepted = probeSettings.targetRatio;
                        else rejected = probeSettings.targetRatio;
                    }
                }
                if (!best.ok)
                {
                    if (cancelled?.Invoke() == true) throw new OperationCanceledException();
                    best = new MeshSimplifier.SimplifyResult { ok = true,simplifiedMesh = UnityEngine.Object.Instantiate(source),
                        originalTriCount = LodMeshData.TriangleCount(source),simplifiedTriCount = LodMeshData.TriangleCount(source),
                        draftUv = MeshUvState.IsDraft(source) };
                    MeshUvState.SetDraft(best.simplifiedMesh,best.draftUv);
                    selected = ++evaluations; // LOD0 is the final evaluated fallback candidate.
                    bestMetrics = LodSurfaceValidation.MeasureMeshes(source,best.simplifiedMesh,settings,cancelled,colorsOnly:true);
                    if (bestMetrics.Rejected(limits)) throw new InvalidOperationException("LOD0 does not pass its own attribute validation.");
                    note = "Triangle candidates rejected by LOD0 vertex-color validation; kept source mesh.";
                }
                else if (best.simplifiedTriCount == best.originalTriCount)
                    note = "Triangle candidates rejected by LOD0 vertex-color validation; kept source mesh.";
                else if (best.simplifiedTriCount > Mathf.CeilToInt(best.originalTriCount*settings.targetRatio))
                    note = "Selected a safer triangle budget after LOD0 vertex-color validation.";
                diagnostics = new LodLoopSimplifier.Result { metrics = bestMetrics,maxDistance = bestMetrics.distance,
                    maxNormalAngle = bestMetrics.normalAngle,maxColorError = bestMetrics.colorError,
                    evaluatedCandidates = evaluations,selectedCandidate = selected };
                var result = best; best.simplifiedMesh = null;
                return result;
            }
            catch (InvalidOperationException ex) { return new MeshSimplifier.SimplifyResult { error = ex.Message }; }
            finally { if (best.simplifiedMesh) UnityEngine.Object.DestroyImmediate(best.simplifiedMesh); }

            bool Evaluate(MeshSimplifier.SimplifySettings probeSettings)
            {
                if (cancelled?.Invoke() == true) throw new OperationCanceledException("LOD generation cancelled.");
                return EvaluateMesh(MeshSimplifier.Simplify(source,probeSettings));
            }

            bool EvaluateMesh(MeshSimplifier.SimplifyResult probe)
            {
                evaluations++;
                UvProgress.Report(UvProgress.Current.fraction,$"{source.name}: validate triangle candidate {evaluations}");
                try
                {
                    MeshUvState.SetDraft(probe.simplifiedMesh,probe.draftUv);
                    if (!probe.ok) throw new InvalidOperationException(probe.error);
                    var metrics = LodSurfaceValidation.MeasureMeshes(source,probe.simplifiedMesh,settings,cancelled,colorsOnly:true,limits:limits);
                    if (metrics.Rejected(limits)) return false;
                    if (!best.ok || probe.simplifiedTriCount < best.simplifiedTriCount ||
                        (probe.simplifiedTriCount == best.simplifiedTriCount && metrics.ColorRms.sqrMagnitude < bestMetrics.ColorRms.sqrMagnitude))
                    {
                        if (best.simplifiedMesh) UnityEngine.Object.DestroyImmediate(best.simplifiedMesh);
                        best = probe; bestMetrics = metrics; selected = evaluations;
                        probe.simplifiedMesh = null;
                    }
                    return true;
                }
                finally { if (probe.simplifiedMesh) UnityEngine.Object.DestroyImmediate(probe.simplifiedMesh); }
            }
        }
    }
}
