// TransferQualityEvaluator.cs — Stage 7: Validate transfer, classify triangles, report
// Computes per-triangle error, collects statistics, outputs structured report.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class TransferQualityEvaluator
    {
        public struct TransferReport
        {
            // Per-target LOD stats
            public int totalTriangles;
            public int accepted;
            public int ambiguous;
            public int borderRisk;
            public int unavoidableMismatch;
            public int rejected;
            public float meanError;
            public float maxError;
            public float meanConfidence;

            // Source stats
            public int sourceShellCount;
            public int sourceBorderPrimCount;

            // Border repair stats
            public BorderRepairSolver.RepairReport borderReport;

            public override string ToString()
            {
                return $"Transfer Report:\n" +
                       $"  Triangles: {totalTriangles}\n" +
                       $"  Accepted: {accepted} ({Pct(accepted)}%)\n" +
                       $"  Ambiguous: {ambiguous} ({Pct(ambiguous)}%)\n" +
                       $"  BorderRisk: {borderRisk} ({Pct(borderRisk)}%)\n" +
                       $"  UnavoidableMismatch: {unavoidableMismatch} ({Pct(unavoidableMismatch)}%)\n" +
                       $"  Rejected: {rejected} ({Pct(rejected)}%)\n" +
                       $"  Mean Error: {meanError:F6}\n" +
                       $"  Max Error: {maxError:F6}\n" +
                       $"  Mean Confidence: {meanConfidence:F3}\n" +
                       $"  Source Shells: {sourceShellCount}\n" +
                       $"  Source Border Prims: {sourceBorderPrimCount}\n" +
                       $"  Border Repaired: {borderReport.repairedCount}\n" +
                       $"  Border Skipped (OK): {borderReport.skippedAlreadyMatching}\n" +
                       $"  Border Risk: {borderReport.markedBorderRisk}";

                string Pct(int v) => totalTriangles > 0
                    ? (v * 100f / totalTriangles).ToString("F1") : "0";
            }
        }

        /// <summary>
        /// Evaluate transfer quality and build report.
        /// Call after all stages (including border repair) are complete.
        /// </summary>
        public static TransferReport Evaluate(
            SourceMeshData source,
            TargetTransferState target,
            BorderRepairSolver.RepairReport borderReport)
        {
            var report = new TransferReport
            {
                totalTriangles = target.faceCount,
                borderReport = borderReport,
                sourceShellCount = source.uvShells.Count,
                sourceBorderPrimCount = source.borderPrimitiveIds.Count
            };

            float sumError = 0;
            float sumConf = 0;
            int confCount = 0;

            for (int f = 0; f < target.faceCount; f++)
            {
                // Compute per-triangle error from bindings
                float fMeanErr = 0;
                float fMaxErr = 0;
                float fMeanConf = 0;
                var bindings = target.pointBindingsPerFace[f];

                if (bindings != null && bindings.Count > 0)
                {
                    foreach (var b in bindings)
                    {
                        fMeanErr += b.distance3D;
                        if (b.distance3D > fMaxErr)
                            fMaxErr = b.distance3D;
                        fMeanConf += b.confidence;
                    }
                    fMeanErr /= bindings.Count;
                    fMeanConf /= bindings.Count;
                }

                // Store in results
                target.results[f] = new TriangleTransferResult
                {
                    triangleId = f,
                    dominantRegion = source.triangleSignatures.Length > 0 && target.triangleSourcePrimId[f] >= 0
                        ? source.triangleSignatures[target.triangleSourcePrimId[f]]
                        : default,
                    status = target.triangleStatus[f],
                    meanError = fMeanErr,
                    maxError = fMaxErr,
                    sourcePrimId = target.triangleSourcePrimId[f],
                    isBorder = target.triangleBorderFlags[f]
                };

                sumError += fMeanErr;
                if (fMaxErr > report.maxError)
                    report.maxError = fMaxErr;
                sumConf += fMeanConf;
                confCount++;

                // Count by status
                switch (target.triangleStatus[f])
                {
                    case TriangleStatus.Accepted: report.accepted++; break;
                    case TriangleStatus.Ambiguous: report.ambiguous++; break;
                    case TriangleStatus.BorderRisk: report.borderRisk++; break;
                    case TriangleStatus.UnavoidableMismatch: report.unavoidableMismatch++; break;
                    case TriangleStatus.Rejected: report.rejected++; break;
                }
            }

            report.meanError = confCount > 0 ? sumError / confCount : 0;
            report.meanConfidence = confCount > 0 ? sumConf / confCount : 0;

            return report;
        }
    }
}
