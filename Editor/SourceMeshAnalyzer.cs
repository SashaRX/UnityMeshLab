// SourceMeshAnalyzer.cs — Stage 1: Builds complete SourceMeshData from LOD0
// Ties together: UvShellExtractor, BorderPrimitiveDetector, UvMetricCalculator, BVH
// Input: Mesh + UV channel index (UV2 after repack)
// Output: SourceMeshData ready for projection

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class SourceMeshAnalyzer
    {
        /// <summary>
        /// Build full source analysis from LOD0 mesh.
        /// uvChannel: 0=UV0, 2=UV2 (default — the repacked lightmap channel)
        /// </summary>
        public static SourceMeshData Analyze(Mesh mesh, int uvChannel = 2)
        {
            var data = new SourceMeshData();
            data.mesh = mesh;
            data.vertices = mesh.vertices;
            data.normals = mesh.normals;
            if (data.normals == null || data.normals.Length == 0)
            {
                mesh.RecalculateNormals();
                data.normals = mesh.normals;
            }
            data.triangles = mesh.triangles;
            data.vertCount = data.vertices.Length;
            data.faceCount = data.triangles.Length / 3;

            // Read UVs
            var uvList = new List<Vector2>();
            mesh.GetUVs(uvChannel, uvList);
            if (uvList.Count == 0)
            {
                UvtLog.Error($"[SourceMeshAnalyzer] Mesh '{mesh.name}' has no UV{uvChannel}");
                return null;
            }
            data.uvSource = uvList.ToArray();

            var uv0List = new List<Vector2>();
            mesh.GetUVs(0, uv0List);
            data.uv0 = uv0List.Count > 0 ? uv0List.ToArray() : null;

            // ── Submesh IDs per face ──
            data.submeshIds = BuildSubmeshIds(mesh, data.faceCount);

            // ── Extract UV shells from source channel ──
            var rawShells = UvShellExtractor.Extract(data.uvSource, data.triangles);
            data.triangleToShellId = new int[data.faceCount];
            data.uvShells = new List<UvShellData>();

            foreach (var raw in rawShells)
            {
                var sd = new UvShellData
                {
                    shellId = raw.shellId,
                    uvBoundsMin = raw.boundsMin,
                    uvBoundsMax = raw.boundsMax,
                    submeshId = -1,
                    componentId = 0
                };

                foreach (int f in raw.faceIndices)
                {
                    sd.triangleIds.Add(f);
                    data.triangleToShellId[f] = raw.shellId;
                    if (sd.submeshId < 0)
                        sd.submeshId = data.submeshIds[f];
                }
                foreach (int v in raw.vertexIndices)
                    sd.vertexIds.Add(v);

                data.uvShells.Add(sd);
            }

            // ── Border detection ──
            BorderPrimitiveDetector.Detect(
                data.triangles, data.faceCount, data.triangleToShellId,
                out data.borderEdges, out data.borderPrimitiveIds);

            // Propagate border info to shells
            foreach (var shell in data.uvShells)
            {
                shell.borderPrimitiveIds.Clear();
                foreach (int f in shell.triangleIds)
                    if (data.borderPrimitiveIds.Contains(f))
                        shell.borderPrimitiveIds.Add(f);
            }

            // ── Triangle metrics ──
            data.triangleMetrics = UvMetricCalculator.ComputeAll(
                data.vertices, data.uvSource, data.triangles, data.faceCount);

            // Compute shell area sums
            foreach (var shell in data.uvShells)
            {
                float aUV = 0, aWorld = 0;
                foreach (int f in shell.triangleIds)
                {
                    aUV += data.triangleMetrics[f].areaUV;
                    aWorld += data.triangleMetrics[f].areaWorld;
                }
                shell.areaUV = aUV;
                shell.areaWorld = aWorld;
            }

            // ── Region signatures ──
            data.triangleSignatures = new RegionSignature[data.faceCount];
            for (int f = 0; f < data.faceCount; f++)
            {
                data.triangleSignatures[f] = new RegionSignature
                {
                    componentId = 0,
                    submeshId = data.submeshIds[f],
                    shellId = data.triangleToShellId[f],
                    materialRegionId = 0
                };
            }

            // ── BVH ──
            data.bvh = new TriangleBvh(data.vertices, data.triangles);

            return data;
        }

        /// <summary>
        /// Build per-face submesh index array.
        /// </summary>
        static int[] BuildSubmeshIds(Mesh mesh, int faceCount)
        {
            int[] ids = new int[faceCount];
            int subCount = mesh.subMeshCount;

            for (int sub = 0; sub < subCount; sub++)
            {
                var desc = mesh.GetSubMesh(sub);
                int startFace = desc.indexStart / 3;
                int count = desc.indexCount / 3;
                for (int i = 0; i < count; i++)
                {
                    int f = startFace + i;
                    if (f < faceCount)
                        ids[f] = sub;
                }
            }

            return ids;
        }

        /// <summary>
        /// Initialize a TargetTransferState for a target LOD mesh.
        /// </summary>
        public static TargetTransferState PrepareTarget(Mesh mesh, int targetUvChannel = 2)
        {
            var state = new TargetTransferState();
            state.mesh = mesh;
            state.vertices = mesh.vertices;
            state.normals = mesh.normals;
            if (state.normals == null || state.normals.Length == 0)
            {
                mesh.RecalculateNormals();
                state.normals = mesh.normals;
            }
            state.triangles = mesh.triangles;
            state.vertCount = state.vertices.Length;
            state.faceCount = state.triangles.Length / 3;

            // Submesh IDs
            state.submeshIds = BuildSubmeshIds(mesh, state.faceCount);

            // Read target UV0 for shell priority matching
            var uv0List = new List<Vector2>();
            mesh.GetUVs(0, uv0List);
            state.targetUv0 = uv0List.Count == state.vertCount ? uv0List.ToArray() : null;

            // Allocate arrays
            state.targetUv = new Vector2[state.vertCount];
            state.triangleShellAssignments = new int[state.faceCount];
            state.triangleStatus = new TriangleStatus[state.faceCount];
            state.triangleSourcePrimId = new int[state.faceCount];
            state.triangleBorderFlags = new bool[state.faceCount];
            state.pointBindingsPerFace = new List<PointBinding>[state.faceCount];
            state.perimeterUV = new float[state.faceCount];
            state.results = new TriangleTransferResult[state.faceCount];

            for (int f = 0; f < state.faceCount; f++)
            {
                state.triangleShellAssignments[f] = -1;
                state.triangleSourcePrimId[f] = -1;
                state.pointBindingsPerFace[f] = new List<PointBinding>();
            }

            return state;
        }
    }
}
