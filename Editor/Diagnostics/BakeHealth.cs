// BakeHealth.cs — the health of one Remesh & Bake node as data: the counters the bake
// collected (cage welding, one-sided normals, nearest fallbacks, misses, normal-map
// tilt), the source/target size ratio, the summary line and the warnings the numbers
// earn. The pipeline used to format and log these inline; building the report first
// lets the Diagnostics tab, a test or a benchmark read the same judgement.
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class BakeHealth
    {
        internal sealed class Report
        {
            public string node;
            public RemeshBaker.Maps maps;
            public float sourceDiagonal, targetDiagonal, scaleRatio;
            public int sourceFaces, targetFaces;
            /// <summary>A proxy shape or a decimation past 5×: a strongly tilted normal map is the intended result, not a defect.</summary>
            public bool heavyReduction;
            public string Summary;
            public readonly List<string> Warnings = new List<string>();
        }

        /// <summary>
        /// The report for one node: <paramref name="baked"/> are its maps and counters,
        /// the source is described by its bounds diagonal and face count, the target by
        /// its positions and face count (both in the capture space, so the diagonal ratio
        /// proves the scale), <paramref name="shape"/> tells a proxy from a remesh.
        /// </summary>
        internal static Report Build(string node, RemeshBaker.Maps baked, float sourceDiagonal, int sourceFaces,
            Vector3[] targetPositions, int targetFaces, RemeshShape shape)
        {
            var r = new Report { node = node, maps = baked, sourceDiagonal = sourceDiagonal, sourceFaces = sourceFaces, targetFaces = targetFaces };
            if (targetPositions != null && targetPositions.Length > 0)
            {
                Vector3 mn = targetPositions[0], mx = targetPositions[0];
                foreach (var p in targetPositions) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                r.targetDiagonal = (mx - mn).magnitude;
            }
            r.scaleRatio = r.targetDiagonal / Mathf.Max(1e-8f, sourceDiagonal);
            r.heavyReduction = shape != RemeshShape.LOD0 || targetFaces * 5 < sourceFaces;

            r.Summary =
                $"cage: {baked.weldedPositions:N0} welded positions ({baked.splitCopies:N0} split copies), {baked.cageSides:N0} sides " +
                $"({baked.foldedPositions:N0} double-sided positions), {baked.oneSidedNormals:N0} vertices off their cage by >30° (max {baked.maxOneSidedDeg:F0}°), " +
                $"reach up to {baked.maxReachRatio:F1}× the projection distance, {baked.zeroNormals:N0} zero normals; " +
                $"projection: {baked.rayFallbacks:N0} nearest-fallback samples, {baked.misses:N0} missed texels, front-face filter {(baked.facingFilter ? "on" : "off")}, " +
                $"{baked.twoSidedFaces:N0} two-sided source faces, {baked.partialMisses:N0} partially projected texels (missing area {baked.missedSampleArea:F2} texels); " +
                $"surface filtering: {baked.gutterTexels:N0} gutter texels, {baked.surfaceSamples:N0} adjacent-face samples, " +
                $"{baked.boundarySamples:N0} boundary-clamped samples, {baked.surfaceEdges:N0} trusted surface edges, " +
                $"{baked.stoppedWalks:N0} stopped walks ({baked.walkLimitHits:N0} walk limits), {baked.patchLimitHits:N0} patch limits, " +
                $"{baked.unfoldOverlapTexels:N0} overlapping local unfoldings, {baked.gutterMisses:N0} gutter projection fallbacks; " +
                $"normal map tilt: mean {baked.meanTiltDeg:F1}° / max {baked.maxTiltDeg:F0}°, {baked.loudTexels:N0} texels >45°, " +
                $"{baked.negativeNormalTexels:N0} covered / {baked.negativeGutterNormals:N0} gutter normals with negative tangent Z, " +
                $"{baked.invalidNormalFrames:N0} invalid normal frames; " +
                $"bounds diagonal: source {sourceDiagonal:F3} / target {r.targetDiagonal:F3} (ratio {r.scaleRatio:F2})";

            if (baked.invalidNormalFrames > 0)
                r.Warnings.Add($"{baked.invalidNormalFrames:N0} texels used a neutral normal because their interpolated tangent frame was singular. Inspect degenerate faces and re-run Unwrap.");
            if (baked.negativeNormalTexels > 0 || baked.negativeGutterNormals > 0)
                r.Warnings.Add($"{baked.negativeNormalTexels:N0} covered and {baked.negativeGutterNormals:N0} gutter normals point behind the result's tangent surface. " +
                    "Stock Lit RG/AG normal decoding cannot represent negative Z and reflects these normals. Check projection, surface folds and result smoothing; this map requires correction before export.");
            if (baked.zeroNormals > 0)
                r.Warnings.Add(baked.zeroNormals + " result vertices have zero normals — projection rays use the face-based cage, " +
                    "but their shading/tangent frames remain invalid. Re-run the UV stage; if it repeats, inspect degenerate faces " +
                    "(lower simplification error or raise voxel resolution).");
            else if (baked.rayFallbacks > baked.covered * 4 / 5 && baked.covered > 0)
                r.Warnings.Add(baked.rayFallbacks.ToString("N0") + " of the projection samples fell back to nearest-point search — the rays " +
                    "are not hitting the source. Check the projection distance and the hard-edge mode, and rebake.");
            if (Mathf.Abs(r.scaleRatio - 1f) > 0.1f)
                r.Warnings.Add($"target/source bounds diagonal ratio is {r.scaleRatio:F2} — the remeshed mesh no longer matches the source size. " +
                    "Check voxel resolution, small-part pruning and simplification settings, and rebake.");
            if (baked.partialMisses > 0)
                r.Warnings.Add($"{baked.partialMisses:N0} covered texels lost part of their surface samples during projection " +
                    $"(missing area {baked.missedSampleArea:F2} texels). The surviving samples were renormalized; check projection distance and cage fit.");
            if (baked.gutterMisses > 0)
                r.Warnings.Add($"{baked.gutterMisses:N0} gutter texels could not project the continued surface; used a boundary fill with normal-frame transport. Check projection distance and cage fit.");
            if (baked.patchLimitHits > 0)
                r.Warnings.Add($"{baked.patchLimitHits:N0} texel footprints exceeded the local surface traversal limit; unreachable parts were clamped. Increase texture resolution or reduce dilation radius.");
            if (baked.walkLimitHits > 0)
                r.Warnings.Add($"{baked.walkLimitHits:N0} gutter texels exceeded the surface walk limit; continuation stopped at the last reached face. Increase texture resolution or reduce dilation radius.");
            if (baked.unfoldOverlapTexels > 0)
                r.Warnings.Add($"{baked.unfoldOverlapTexels:N0} texel footprints unfolded over themselves on the curved surface; overlapping contributions were area-normalized. Increase texture resolution or reduce dilation radius.");
            if (!r.heavyReduction && baked.loudTexels > baked.covered / 20 && baked.meanTiltDeg > 30f)
                r.Warnings.Add($"{100.0 * baked.loudTexels / Mathf.Max(1, baked.covered):F1}% of texels lean >45° with a {baked.meanTiltDeg:F0}° mean tilt — " +
                    "the map is dominated by extreme normals. Check the hard-edge mode, projection distance and cage fit, " +
                    "and compare against the source: fine detail should tilt a map, not saturate it.");
            return r;
        }

        /// <summary>The summary (when the RemeshDiag category is on) and every warning, prefixed with the node.</summary>
        internal static void Log(Report r)
        {
            string prefix = "[" + r.node + "] ";
            if (UvtLog.IsCategoryEnabled(UvtLog.Category.RemeshDiag)) {
                UvtLog.Info(UvtLog.Category.RemeshDiag, prefix + r.Summary);
                var maps = r.maps;
                if (maps.gpu)
                    UvtLog.Info(UvtLog.Category.RemeshDiag, prefix +
                        $"GPU pipeline: {maps.gpuBands:N0} bands, {maps.gpuQueries:N0} queries, " +
                        $"{maps.gpuRayBatches:N0} ray / {maps.gpuNearestBatches:N0} nearest dispatches; " +
                        $"prepare {maps.gpuPrepareMs:F1} ms, setup {maps.gpuSetupMs:F1} ms, " +
                        $"build requests {maps.gpuBuildRequestsMs:F1} ms, resolve/readback {maps.gpuResolveMs:F1} ms, " +
                        $"evaluate {maps.gpuEvaluateMs:F1} ms, AO {maps.gpuAoMs:F1} ms, finish {maps.gpuFinishMs:F1} ms " +
                        "(includes Editor scheduling waits).");
            }
            foreach (var w in r.Warnings)
                UvtLog.Warn(UvtLog.Category.RemeshDiag, prefix + w);
        }
    }
}
