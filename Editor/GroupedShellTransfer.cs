// GroupedShellTransfer.cs — UV2 transfer via 3D nearest-vertex matching
// Core idea: for each target LOD vertex, find nearest source (LOD0) vertex
// by 3D position and directly copy its UV2. No similarity transforms,
// no UV0 shell matching dependency. Works regardless of target UV0 layout.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class GroupedShellTransfer
    {
        // ─── Shell info for cross-LOD matching ───
        public class SourceShellInfo
        {
            public int shellId;
            public Vector2 uv0BoundsMin, uv0BoundsMax;
            public Vector2 uv0Centroid;
            public Vector3 worldCentroid;
            public float signedAreaUv0;              // positive = CCW, negative = CW
            public int vertexCount;

            // Per-vertex data for 3D nearest-vertex transfer
            public int[] vertexIndices;              // original mesh vertex indices
            public Vector3[] worldPositions;         // 3D positions
            public Vector2[] shellUv2;               // UV2 values to copy
        }

        // ─── Result of transfer for one target mesh ───
        public class TransferResult
        {
            public Vector2[] uv2;
            public int shellsMatched;
            public int shellsUnmatched;
            public int shellsMirrored;
            public int verticesTransferred;
            public int verticesTotal;
        }

        // ═══════════════════════════════════════════════════════════
        //  Step 1: Analyze source mesh — extract UV0 shells,
        //          store per-vertex 3D positions + UV2 for transfer
        // ═══════════════════════════════════════════════════════════

        public static SourceShellInfo[] AnalyzeSource(Mesh sourceMesh)
        {
            var uv0List = new List<Vector2>();
            var uv2List = new List<Vector2>();
            sourceMesh.GetUVs(0, uv0List);
            sourceMesh.GetUVs(2, uv2List);

            if (uv0List.Count == 0 || uv2List.Count == 0)
            {
                Debug.LogError("[GroupedTransfer] Source mesh missing UV0 or UV2");
                return null;
            }

            var uv0 = uv0List.ToArray();
            var uv2 = uv2List.ToArray();
            var tris = sourceMesh.triangles;
            var verts = sourceMesh.vertices;

            var shells = UvShellExtractor.Extract(uv0, tris);
            var infos = new SourceShellInfo[shells.Count];

            for (int si = 0; si < shells.Count; si++)
            {
                var shell = shells[si];

                // Collect per-vertex data
                var idxList = new List<int>();
                var posList = new List<Vector3>();
                var uv2sList = new List<Vector2>();
                Vector2 uv0Sum = Vector2.zero;
                Vector3 worldSum = Vector3.zero;
                int n = 0;

                foreach (int vi in shell.vertexIndices)
                {
                    if (vi >= uv0.Length || vi >= uv2.Length || vi >= verts.Length) continue;
                    idxList.Add(vi);
                    posList.Add(verts[vi]);
                    uv2sList.Add(uv2[vi]);
                    uv0Sum += uv0[vi];
                    worldSum += verts[vi];
                    n++;
                }

                float signedArea = ComputeSignedArea(tris, uv0, shell.faceIndices);

                infos[si] = new SourceShellInfo
                {
                    shellId = shell.shellId,
                    uv0BoundsMin = shell.boundsMin,
                    uv0BoundsMax = shell.boundsMax,
                    uv0Centroid = n > 0 ? uv0Sum / n : Vector2.zero,
                    worldCentroid = n > 0 ? worldSum / n : Vector3.zero,
                    signedAreaUv0 = signedArea,
                    vertexCount = n,
                    vertexIndices = idxList.ToArray(),
                    worldPositions = posList.ToArray(),
                    shellUv2 = uv2sList.ToArray()
                };
            }

            Debug.Log($"[GroupedTransfer] Source '{sourceMesh.name}': {infos.Length} shells");

            return infos;
        }

        // ═══════════════════════════════════════════════════════════
        //  Step 2: Transfer UV2 to target mesh via 3D nearest vertex
        //  - Match target vertex to source shell by 3D proximity
        //  - Copy UV2 directly from nearest source vertex in 3D
        //  - No similarity transforms, no UV0 dependency
        // ═══════════════════════════════════════════════════════════

        public static TransferResult Transfer(
            Mesh targetMesh, SourceShellInfo[] sourceInfos)
        {
            var result = new TransferResult();

            var tVerts = targetMesh.vertices;
            int vertCount = targetMesh.vertexCount;

            result.uv2 = new Vector2[vertCount];
            result.verticesTotal = vertCount;

            // Build flat arrays of all source positions + UV2 for global fallback
            int totalSrcVerts = 0;
            foreach (var si in sourceInfos) totalSrcVerts += si.worldPositions.Length;

            var allSrcPos = new Vector3[totalSrcVerts];
            var allSrcUv2 = new Vector2[totalSrcVerts];
            var allSrcShell = new int[totalSrcVerts];
            int offset = 0;
            for (int s = 0; s < sourceInfos.Length; s++)
            {
                var si = sourceInfos[s];
                for (int i = 0; i < si.worldPositions.Length; i++)
                {
                    allSrcPos[offset] = si.worldPositions[i];
                    allSrcUv2[offset] = si.shellUv2[i];
                    allSrcShell[offset] = s;
                    offset++;
                }
            }

            // For each target vertex: find nearest source vertex by 3D position
            // Copy UV2 directly
            int[] matchedShell = new int[vertCount];
            bool[] vertexDone = new bool[vertCount];

            for (int vi = 0; vi < vertCount; vi++)
            {
                Vector3 tPos = tVerts[vi];
                float bestDist = float.MaxValue;
                int bestIdx = 0;

                for (int si = 0; si < totalSrcVerts; si++)
                {
                    float d = (tPos - allSrcPos[si]).sqrMagnitude;
                    if (d < bestDist) { bestDist = d; bestIdx = si; }
                }

                result.uv2[vi] = allSrcUv2[bestIdx];
                matchedShell[vi] = allSrcShell[bestIdx];
                vertexDone[vi] = true;
                result.verticesTransferred++;
            }

            // Count shells matched (unique source shells used)
            var shellsUsed = new HashSet<int>();
            for (int i = 0; i < vertCount; i++)
                if (vertexDone[i]) shellsUsed.Add(matchedShell[i]);
            result.shellsMatched = shellsUsed.Count;

            Debug.Log($"[GroupedTransfer] '{targetMesh.name}': " +
                      $"{result.shellsMatched} source shells used, " +
                      $"{result.verticesTransferred}/{result.verticesTotal} verts");

            // UV2 bounds check
            int outOfBounds = 0;
            Vector2 uvMin = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 uvMax = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < result.uv2.Length; i++)
            {
                if (!vertexDone[i]) continue;
                var uv = result.uv2[i];
                if (uv.x < uvMin.x) uvMin.x = uv.x;
                if (uv.y < uvMin.y) uvMin.y = uv.y;
                if (uv.x > uvMax.x) uvMax.x = uv.x;
                if (uv.y > uvMax.y) uvMax.y = uv.y;
                if (uv.x < -0.01f || uv.x > 1.01f || uv.y < -0.01f || uv.y > 1.01f)
                    outOfBounds++;
            }
            if (outOfBounds > 0)
                Debug.LogWarning($"[GroupedTransfer] '{targetMesh.name}': {outOfBounds} verts " +
                    $"outside 0-1! UV2 bounds=[{uvMin.x:F3},{uvMin.y:F3}]-[{uvMax.x:F3},{uvMax.y:F3}]");

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  Signed area of shell in UV space (used by AnalyzeSource)
        // ═══════════════════════════════════════════════════════════

        static float ComputeSignedArea(int[] tris, Vector2[] uvs, List<int> faceIndices)
        {
            double area = 0;
            foreach (int f in faceIndices)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                var a = uvs[i0]; var b = uvs[i1]; var c = uvs[i2];
                area += (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            }
            return (float)(area * 0.5);
        }
    }
}
