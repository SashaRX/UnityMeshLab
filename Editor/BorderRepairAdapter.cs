// BorderRepairAdapter.cs — Bridges GroupedShellTransfer output to BorderRepairSolver
//
// GroupedShellTransfer produces Vector2[] uv2 per-vertex but does NOT track
// per-face source triangle IDs. This adapter:
//   1. Builds lightweight source data (BVH, UV2, metrics)
//   2. Recovers per-target-face source prim ID via 3D centroid projection
//   3. Runs BorderRepairSolver on the provisional UV2
//   4. Returns repaired UV2 in-place + repair report
//
// Does NOT modify GroupedShellTransfer code — purely additive.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class BorderRepairAdapter
    {
        public struct Settings
        {
            public float perimeterTolerance;
            public float borderFuseTolerance;
            public float maxNormalAngle;

            public static Settings Default => new Settings
            {
                perimeterTolerance = 0.05f,
                borderFuseTolerance = 0.02f,
                maxNormalAngle = 90f
            };
        }

        public struct AdapterReport
        {
            public BorderRepairSolver.RepairReport repairReport;
            public int sourcePrimsRecovered;
            public int sourcePrimsFailed;
        }

        /// <summary>
        /// Run border repair on GroupedShellTransfer output.
        /// Modifies uv2 array in-place for border primitives only.
        /// </summary>
        public static AdapterReport Repair(
            Mesh targetMesh, Mesh sourceMesh,
            Vector2[] uv2,
            Settings settings)
        {
            var report = new AdapterReport();

            if (uv2 == null || uv2.Length == 0)
                return report;

            // ── Build lightweight source data ──
            var sourceData = BuildSourceData(sourceMesh);
            if (sourceData == null)
                return report;

            // ── Build target state from provisional UV2 ──
            var targetState = BuildTargetState(targetMesh, uv2, sourceData,
                settings.maxNormalAngle,
                out report.sourcePrimsRecovered,
                out report.sourcePrimsFailed);

            // ── Run border repair ──
            var repairSettings = new BorderRepairSolver.Settings
            {
                perimeterTolerance = settings.perimeterTolerance,
                borderFuseTolerance = settings.borderFuseTolerance,
                enableBorderRepair = true
            };

            report.repairReport = BorderRepairSolver.Solve(sourceData, targetState, repairSettings);

            // ── Write repaired UV back to the uv2 array ──
            // BorderRepairSolver writes directly to targetState.targetUv,
            // which shares the reference with our uv2 array (set in BuildTargetState).
            // No extra copy needed.

            return report;
        }

        /// <summary>
        /// Build SourceMeshData from source mesh (reuses SourceMeshAnalyzer).
        /// </summary>
        static SourceMeshData BuildSourceData(Mesh sourceMesh)
        {
            return SourceMeshAnalyzer.Analyze(sourceMesh, 1);
        }

        /// <summary>
        /// Build a TargetTransferState from provisional UV2 + recover sourcePrimIds
        /// via BVH centroid projection.
        /// </summary>
        static TargetTransferState BuildTargetState(
            Mesh targetMesh,
            Vector2[] uv2,
            SourceMeshData source,
            float maxNormalAngle,
            out int primsRecovered,
            out int primsFailed)
        {
            var state = new TargetTransferState();
            state.mesh = targetMesh;
            state.vertices = targetMesh.vertices;
            state.normals = targetMesh.normals;
            if (state.normals == null || state.normals.Length == 0)
            {
                targetMesh.RecalculateNormals();
                state.normals = targetMesh.normals;
            }
            state.triangles = targetMesh.triangles;
            state.vertCount = targetMesh.vertexCount;
            state.faceCount = state.triangles.Length / 3;

            // Share the UV2 array — BorderRepairSolver writes into this directly
            state.targetUv = uv2;

            // Allocate per-face arrays
            state.triangleShellAssignments = new int[state.faceCount];
            state.triangleStatus = new TriangleStatus[state.faceCount];
            state.triangleSourcePrimId = new int[state.faceCount];
            state.triangleBorderFlags = new bool[state.faceCount];
            state.perimeterUV = new float[state.faceCount];
            state.borderPrimitiveIds = new HashSet<int>();
            state.results = new TriangleTransferResult[state.faceCount];

            // pointBindingsPerFace — not used by BorderRepairSolver, but needed by
            // TransferQualityEvaluator. Allocate empty lists.
            state.pointBindingsPerFace = new List<PointBinding>[state.faceCount];
            for (int f = 0; f < state.faceCount; f++)
                state.pointBindingsPerFace[f] = new List<PointBinding>();

            // ── Recover sourcePrimId per target face via BVH projection ──
            float cosLimit = Mathf.Cos(maxNormalAngle * Mathf.Deg2Rad);
            primsRecovered = 0;
            primsFailed = 0;

            for (int f = 0; f < state.faceCount; f++)
            {
                state.triangleShellAssignments[f] = -1;
                state.triangleSourcePrimId[f] = -1;
                state.triangleStatus[f] = TriangleStatus.Accepted;

                int ti0 = state.triangles[f * 3];
                int ti1 = state.triangles[f * 3 + 1];
                int ti2 = state.triangles[f * 3 + 2];

                Vector3 centroid = (state.vertices[ti0] + state.vertices[ti1] + state.vertices[ti2]) / 3f;
                Vector3 normal = Vector3.zero;
                if (state.normals != null && ti0 < state.normals.Length)
                    normal = ((state.normals[ti0] + state.normals[ti1] + state.normals[ti2]) / 3f).normalized;

                var hit = source.bvh.FindNearest(centroid);
                if (hit.triangleIndex < 0)
                {
                    primsFailed++;
                    continue;
                }

                // Normal check
                if (normal.sqrMagnitude > 0.5f && source.normals != null)
                {
                    int si0 = source.triangles[hit.triangleIndex * 3];
                    int si1 = source.triangles[hit.triangleIndex * 3 + 1];
                    int si2 = source.triangles[hit.triangleIndex * 3 + 2];
                    Vector3 srcN = (source.normals[si0] * hit.barycentric.x +
                                    source.normals[si1] * hit.barycentric.y +
                                    source.normals[si2] * hit.barycentric.z).normalized;
                    float dot = Vector3.Dot(normal, srcN);
                    if (dot < cosLimit)
                    {
                        primsFailed++;
                        continue;
                    }
                }

                state.triangleSourcePrimId[f] = hit.triangleIndex;
                state.triangleShellAssignments[f] = source.triangleToShellId[hit.triangleIndex];
                state.triangleBorderFlags[f] = source.borderPrimitiveIds.Contains(hit.triangleIndex);
                primsRecovered++;
            }

            UvtLog.Info($"[BorderRepairAdapter] Source prim recovery: {primsRecovered} ok, {primsFailed} failed " +
                      $"(of {state.faceCount} target faces)");

            return state;
        }
    }
}
