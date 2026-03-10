// UvTransferPipeline.cs — Orchestrates the full 7-stage UV transfer pipeline
// Stage 0: Collect → Stage 1: Analyze Source → Stage 2: Shell Assignment →
// Stage 3: Initial Transfer → Stage 4-6: Border Repair → Stage 7: Validate

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    public static class UvTransferPipeline
    {
        public struct PipelineSettings
        {
            // Source
            public int sourceUvChannel;    // UV channel to transfer FROM (default 2)
            public int targetUvChannel;    // UV channel to write TO (default 2)

            // Projection
            public float maxProjectionDistance;
            public float maxNormalAngle;
            public bool filterBySubmesh;

            // Coverage split (Stage 2c)
            public bool enableCoverageSplit;

            // Border repair
            public float perimeterTolerance;
            public float borderFuseTolerance;
            public bool enableBorderRepair;

            // Output
            public bool saveNewMeshAssets;
            public string savePath;

            public static PipelineSettings Default => new PipelineSettings
            {
                sourceUvChannel = 1,
                targetUvChannel = 1,
                maxProjectionDistance = 0.5f,
                maxNormalAngle = 80f,
                filterBySubmesh = true,
                enableCoverageSplit = true,
                perimeterTolerance = 0.05f,
                borderFuseTolerance = 0.02f,
                enableBorderRepair = false,
                saveNewMeshAssets = true,
                savePath = "Assets/LightmapUvTool_Output"
            };
        }

        public struct PipelineResult
        {
            public SourceMeshData sourceData;
            public List<TargetResult> targetResults;
        }

        public struct TargetResult
        {
            public int lodIndex;
            public Mesh originalMesh;
            public Mesh outputMesh;
            public TargetTransferState state;
            public TransferQualityEvaluator.TransferReport report;
        }

        /// <summary>
        /// Run the full pipeline on a LODGroup.
        /// sourceLodIndex: which LOD to use as source (usually 0).
        /// </summary>
        public static PipelineResult Run(
            LODGroup lodGroup,
            int sourceLodIndex,
            PipelineSettings settings)
        {
            var result = new PipelineResult { targetResults = new List<TargetResult>() };

            // ── Stage 0: Collect ──
            var lods = lodGroup.GetLODs();
            if (sourceLodIndex < 0 || sourceLodIndex >= lods.Length)
            {
                UvtLog.Error("[UvTransferPipeline] Invalid source LOD index");
                return result;
            }

            // Get source mesh
            Mesh sourceMesh = GetMeshFromLod(lods[sourceLodIndex]);
            if (sourceMesh == null)
            {
                UvtLog.Error("[UvTransferPipeline] No mesh found on source LOD");
                return result;
            }

            UvtLog.Info($"[Pipeline] Source: {sourceMesh.name}, " +
                      $"{sourceMesh.vertexCount} verts, {sourceMesh.triangles.Length / 3} tris");

            // ── Stage 1: Analyze Source ──
            EditorUtility.DisplayProgressBar("UV Transfer", "Stage 1: Analyzing source...", 0.1f);

            result.sourceData = SourceMeshAnalyzer.Analyze(sourceMesh, settings.sourceUvChannel);
            if (result.sourceData == null)
            {
                EditorUtility.ClearProgressBar();
                return result;
            }

            UvtLog.Verbose($"[Pipeline] Source analysis: {result.sourceData.uvShells.Count} shells, " +
                      $"{result.sourceData.borderPrimitiveIds.Count} border prims");

            // ── Process each target LOD ──
            for (int lodIdx = 0; lodIdx < lods.Length; lodIdx++)
            {
                if (lodIdx == sourceLodIndex) continue;

                Mesh targetMesh = GetMeshFromLod(lods[lodIdx]);
                if (targetMesh == null) continue;

                UvtLog.Verbose($"[Pipeline] Target LOD{lodIdx}: {targetMesh.name}, " +
                          $"{targetMesh.vertexCount} verts, {targetMesh.triangles.Length / 3} tris");

                float progress = 0.2f + 0.7f * ((float)(lodIdx) / lods.Length);
                EditorUtility.DisplayProgressBar("UV Transfer",
                    $"Processing LOD{lodIdx}...", progress);

                var targetResult = ProcessTargetLod(
                    result.sourceData, targetMesh, lodIdx, settings);

                result.targetResults.Add(targetResult);
            }

            // ── Save ──
            if (settings.saveNewMeshAssets)
            {
                EditorUtility.DisplayProgressBar("UV Transfer", "Saving assets...", 0.95f);
                SaveResults(result, lodGroup, settings);
            }

            EditorUtility.ClearProgressBar();

            // ── Print reports ──
            foreach (var tr in result.targetResults)
            {
                UvtLog.Verbose($"[Pipeline] LOD{tr.lodIndex} report:\n{tr.report}");
            }

            return result;
        }

        static TargetResult ProcessTargetLod(
            SourceMeshData source, Mesh targetMesh, int lodIndex,
            PipelineSettings settings)
        {
            var tr = new TargetResult
            {
                lodIndex = lodIndex,
                originalMesh = targetMesh
            };

            // ── Stage 2: Shell Assignment ──
            tr.state = SourceMeshAnalyzer.PrepareTarget(targetMesh, settings.targetUvChannel);

            var shellSettings = new ShellAssignmentSolver.Settings
            {
                maxProjectionDistance = settings.maxProjectionDistance,
                maxNormalAngle = settings.maxNormalAngle,
                filterBySubmesh = settings.filterBySubmesh
            };
            ShellAssignmentSolver.Solve(source, tr.state, shellSettings);

            // Count assignments
            int assigned = 0, unassigned = 0;
            for (int f = 0; f < tr.state.faceCount; f++)
            {
                if (tr.state.triangleShellAssignments[f] >= 0) assigned++;
                else unassigned++;
            }
            UvtLog.Verbose($"[Pipeline] LOD{lodIndex} shell assignment: " +
                      $"{assigned} assigned, {unassigned} unassigned");

            // ── Stage 2c: Coverage Split ──
            if (settings.enableCoverageSplit)
            {
                var coverageSettings = new CoverageSplitSolver.Settings
                {
                    maxReverseProjectionDistance = settings.maxProjectionDistance,
                    minNormalDot = Mathf.Cos(settings.maxNormalAngle * Mathf.Deg2Rad),
                    minUncoveredFraction = 0.05f,
                    minFragmentSize = 2
                };
                var splitReport = CoverageSplitSolver.Solve(source, tr.state, coverageSettings);
                if (splitReport.trianglesDetached > 0)
                {
                    UvtLog.Verbose($"[Pipeline] LOD{lodIndex} coverage split: " +
                        $"{splitReport.trianglesDetached} detached, " +
                        $"{splitReport.isolatesReassigned} reassigned");
                }
            }

            // ── Stage 3: Initial Transfer ──
            InitialUvTransferSolver.Solve(source, tr.state);

            // ── Stages 4-6: Border Repair ──
            var borderSettings = new BorderRepairSolver.Settings
            {
                perimeterTolerance = settings.perimeterTolerance,
                borderFuseTolerance = settings.borderFuseTolerance,
                enableBorderRepair = settings.enableBorderRepair
            };
            var borderReport = BorderRepairSolver.Solve(source, tr.state, borderSettings);

            // ── Stage 7: Validate ──
            tr.report = TransferQualityEvaluator.Evaluate(source, tr.state, borderReport);

            // ── Build output mesh ──
            tr.outputMesh = BuildOutputMesh(targetMesh, tr.state.targetUv, settings.targetUvChannel);

            return tr;
        }

        /// <summary>
        /// Create a copy of the mesh with the new UV channel written.
        /// </summary>
        static Mesh BuildOutputMesh(Mesh original, Vector2[] newUv, int uvChannel)
        {
            var copy = Object.Instantiate(original);
            copy.name = original.name + "_uvTransfer";

            var uvList = new List<Vector2>(newUv);
            copy.SetUVs(uvChannel, uvList);

            return copy;
        }

        /// <summary>
        /// Get first mesh from LOD renderers.
        /// </summary>
        static Mesh GetMeshFromLod(LOD lod)
        {
            if (lod.renderers == null) return null;
            foreach (var r in lod.renderers)
            {
                if (r == null) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                    return mf.sharedMesh;
            }
            return null;
        }

        /// <summary>
        /// Save output meshes as assets and update LODGroup.
        /// </summary>
        static void SaveResults(PipelineResult result, LODGroup lodGroup, PipelineSettings settings)
        {
            if (!AssetDatabase.IsValidFolder(settings.savePath))
            {
                // Create folder
                string parent = System.IO.Path.GetDirectoryName(settings.savePath);
                string folder = System.IO.Path.GetFileName(settings.savePath);
                if (parent != null && !AssetDatabase.IsValidFolder(settings.savePath))
                    AssetDatabase.CreateFolder(parent, folder);
            }

            var lods = lodGroup.GetLODs();

            foreach (var tr in result.targetResults)
            {
                string assetPath = $"{settings.savePath}/{tr.outputMesh.name}.asset";
                AssetDatabase.CreateAsset(tr.outputMesh, assetPath);
                UvtLog.Info($"[Pipeline] Saved: {assetPath}");

                // Update LODGroup renderer
                if (tr.lodIndex < lods.Length)
                {
                    var renderers = lods[tr.lodIndex].renderers;
                    if (renderers != null && renderers.Length > 0)
                    {
                        var mf = renderers[0].GetComponent<MeshFilter>();
                        if (mf != null)
                        {
                            Undo.RecordObject(mf, "UV Transfer - Update Mesh");
                            mf.sharedMesh = tr.outputMesh;
                        }
                    }
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }
}
