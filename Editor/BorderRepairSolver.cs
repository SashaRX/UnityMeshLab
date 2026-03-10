// BorderRepairSolver.cs — Stages 4-6: Border detection on target, perimeter measurement,
// quality-gated border repair.
// Stage 4: Detect border primitives on target's provisional UV
// Stage 5: Measure perimeter, build BorderPairs
// Stage 6: Quality gate → conditional local reproject/fuse

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class BorderRepairSolver
    {
        public struct Settings
        {
            public float perimeterTolerance;     // max perimeter delta to skip fuse
            public float borderFuseTolerance;    // max UV delta for fuse correction
            public bool enableBorderRepair;

            public static Settings Default => new Settings
            {
                perimeterTolerance = 0.05f,
                borderFuseTolerance = 0.02f,
                enableBorderRepair = true
            };
        }

        public struct RepairReport
        {
            public int totalBorderPrims;
            public int repairedCount;
            public int skippedAlreadyMatching;
            public int markedBorderRisk;
            public List<BorderPair> borderPairs;
        }

        /// <summary>
        /// Run full border repair pipeline: detect → measure → quality gate → repair.
        /// </summary>
        public static RepairReport Solve(
            SourceMeshData source, TargetTransferState target, Settings settings)
        {
            var report = new RepairReport { borderPairs = new List<BorderPair>() };

            if (!settings.enableBorderRepair)
                return report;

            // ── Stage 4: Detect border primitives on target ──
            BorderPrimitiveDetector.DetectByUvConnectivity(
                target.targetUv, target.triangles, target.faceCount,
                out _, out target.borderPrimitiveIds);

            report.totalBorderPrims = target.borderPrimitiveIds != null
                ? target.borderPrimitiveIds.Count : 0;

            if (report.totalBorderPrims == 0)
                return report;

            // ── Stage 5: Perimeter measurement + build pairs ──
            // Compute target perimeter from provisional UV
            for (int f = 0; f < target.faceCount; f++)
            {
                int i0 = target.triangles[f * 3];
                int i1 = target.triangles[f * 3 + 1];
                int i2 = target.triangles[f * 3 + 2];
                target.perimeterUV[f] = UvMetricCalculator.Perimeter(
                    target.targetUv[i0], target.targetUv[i1], target.targetUv[i2]);
            }

            foreach (int tf in target.borderPrimitiveIds)
            {
                int srcPrimId = target.triangleSourcePrimId[tf];
                if (srcPrimId < 0 || srcPrimId >= source.faceCount)
                    continue;

                float tPerim = target.perimeterUV[tf];
                float sPerim = source.triangleMetrics[srcPrimId].perimeterUV;

                var pair = new BorderPair
                {
                    targetBorderPrimId = tf,
                    sourceBorderPrimId = srcPrimId,
                    targetPerimeterUV = tPerim,
                    sourcePerimeterUV = sPerim,
                    perimeterDelta = Mathf.Abs(tPerim - sPerim),
                    qualityGatePassed = false
                };

                // ── Stage 6: Quality gate ──
                pair.qualityGatePassed = pair.perimeterDelta < settings.perimeterTolerance;

                report.borderPairs.Add(pair);
            }

            // ── Stage 6: Repair ──
            foreach (var pair in report.borderPairs)
            {
                if (pair.qualityGatePassed)
                {
                    // Already matching — no fuse needed
                    report.skippedAlreadyMatching++;
                    continue;
                }

                // Attempt local reproject for this border primitive
                bool repaired = LocalReproject(
                    pair.targetBorderPrimId, pair.sourceBorderPrimId,
                    source, target, settings.borderFuseTolerance);

                if (repaired)
                {
                    report.repairedCount++;
                }
                else
                {
                    // Mark as BorderRisk
                    if (target.triangleStatus[pair.targetBorderPrimId] == TriangleStatus.Accepted ||
                        target.triangleStatus[pair.targetBorderPrimId] == TriangleStatus.None)
                    {
                        target.triangleStatus[pair.targetBorderPrimId] = TriangleStatus.BorderRisk;
                    }
                    report.markedBorderRisk++;
                }
            }

            UvtLog.Info($"[BorderRepair] {report.totalBorderPrims} border prims: " +
                       $"{report.repairedCount} repaired, " +
                       $"{report.skippedAlreadyMatching} already OK, " +
                       $"{report.markedBorderRisk} risk");

            return report;
        }

        /// <summary>
        /// Local reproject: re-compute UV for target border prim's vertices
        /// by projecting onto the specific source primitive.
        /// Only applies correction if UV delta is within tolerance.
        /// Does NOT modify vertices shared with interior if delta exceeds tolerance.
        /// </summary>
        static bool LocalReproject(
            int targetFace, int sourceFace,
            SourceMeshData source, TargetTransferState target,
            float fuseTolerance)
        {
            int si0 = source.triangles[sourceFace * 3];
            int si1 = source.triangles[sourceFace * 3 + 1];
            int si2 = source.triangles[sourceFace * 3 + 2];

            Vector3 sa = source.vertices[si0];
            Vector3 sb = source.vertices[si1];
            Vector3 sc = source.vertices[si2];

            Vector2 suv0 = source.uvSource[si0];
            Vector2 suv1 = source.uvSource[si1];
            Vector2 suv2 = source.uvSource[si2];

            bool anyApplied = false;

            for (int j = 0; j < 3; j++)
            {
                int vi = target.triangles[targetFace * 3 + j];
                Vector3 vPos = target.vertices[vi];

                // Project target vertex onto source triangle
                TriangleBvh.ClosestPointOnTriangle(vPos, sa, sb, sc, out Vector3 bary);

                Vector2 newUv = suv0 * bary.x + suv1 * bary.y + suv2 * bary.z;
                Vector2 oldUv = target.targetUv[vi];

                float delta = (newUv - oldUv).magnitude;

                if (delta < fuseTolerance)
                {
                    // Apply correction — small enough to not disrupt neighbors
                    target.targetUv[vi] = newUv;
                    anyApplied = true;
                }
                // else: skip this vertex — delta too large, would jump through seam
            }

            return anyApplied;
        }
    }
}
