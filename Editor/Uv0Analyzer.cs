// Uv0Analyzer.cs — UV0 quality analysis and false seam welding
// Detects: false seams (weld candidates), degenerate UV triangles,
// flipped UV triangles, overlapping shells.
// Fix: welds false seams by merging duplicate vertex indices.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>Analysis report for one mesh's UV0 quality.</summary>
    public struct Uv0Report
    {
        public string meshName;
        public int totalVertices;
        public int totalTriangles;
        public int totalShells;

        // False seams: vertices with identical pos+uv0+normal but different indices
        public int falseSeamPairs;
        public int falseSeamVertices; // unique verts that would be removed by weld

        // Degenerate: near-zero UV-space area
        public int degenerateTriangles;

        // Flipped: negative signed area when shell majority is positive (or vice versa)
        public int flippedTriangles;

        // Overlap groups (shells with overlapping UV0 bounding boxes)
        public int overlapGroups;
        public int overlappingShells;

        public bool HasIssues =>
            falseSeamPairs > 0 || degenerateTriangles > 0 ||
            flippedTriangles > 0;
    }

    public static class Uv0Analyzer
    {
        const float POS_EPS = 1e-6f;
        const float UV_EPS = 1e-6f;
        const float NORMAL_EPS = 1e-4f;
        const float DEGEN_AREA_EPS = 1e-10f;

        // ═══════════════════════════════════════════════════════════
        //  Analyze: detect issues without modifying mesh
        // ═══════════════════════════════════════════════════════════

        public static Uv0Report Analyze(Mesh mesh)
        {
            var report = new Uv0Report { meshName = mesh.name };

            var verts = mesh.vertices;
            var normals = mesh.normals;
            var uv0 = mesh.uv;
            var tris = mesh.triangles;

            int vertCount = mesh.vertexCount;
            int faceCount = tris.Length / 3;

            report.totalVertices = vertCount;
            report.totalTriangles = faceCount;

            if (uv0 == null || uv0.Length == 0)
            {
                Debug.LogWarning($"[UV0Analyze] '{mesh.name}': no UV0");
                return report;
            }

            bool hasNormals = normals != null && normals.Length == vertCount;

            // ── 1. False seams ──
            var weldMap = BuildWeldMap(verts, uv0, normals, hasNormals);
            report.falseSeamPairs = weldMap.Count;

            // Count unique vertices that would be removed
            var removed = new HashSet<int>();
            foreach (var kv in weldMap)
                removed.Add(kv.Key);
            report.falseSeamVertices = removed.Count;

            // ── 2. Shell extraction for per-shell analysis ──
            var shells = UvShellExtractor.Extract(uv0, tris);
            report.totalShells = shells.Count;

            // ── 3. Degenerate + flipped triangles ──
            // Compute signed area per triangle, then per-shell majority winding
            float[] signedAreas = new float[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                if (i0 >= uv0.Length || i1 >= uv0.Length || i2 >= uv0.Length)
                {
                    signedAreas[f] = 0;
                    continue;
                }
                signedAreas[f] = SignedArea2D(uv0[i0], uv0[i1], uv0[i2]);
            }

            // Per-shell: determine majority winding direction
            foreach (var shell in shells)
            {
                float posArea = 0, negArea = 0;
                foreach (int f in shell.faceIndices)
                {
                    float a = signedAreas[f];
                    if (a > 0) posArea += a;
                    else negArea += -a;
                }
                bool majorityPositive = posArea >= negArea;

                foreach (int f in shell.faceIndices)
                {
                    float a = signedAreas[f];
                    float absA = Mathf.Abs(a);

                    if (absA < DEGEN_AREA_EPS)
                    {
                        report.degenerateTriangles++;
                    }
                    else if (majorityPositive && a < 0)
                    {
                        report.flippedTriangles++;
                    }
                    else if (!majorityPositive && a > 0)
                    {
                        report.flippedTriangles++;
                    }
                }
            }

            // ── 4. Overlap groups ──
            var overlapGroups = UvShellExtractor.FindOverlapGroups(shells);
            report.overlapGroups = overlapGroups.Count;
            int overlapping = 0;
            foreach (var g in overlapGroups) overlapping += g.Count;
            report.overlappingShells = overlapping;

            return report;
        }

        // ═══════════════════════════════════════════════════════════
        //  WeldUv0: merge false-seam vertices, return new mesh
        //  Only modifies index buffer + removes unused vertices.
        //  All vertex attributes preserved. Does NOT modify input.
        // ═══════════════════════════════════════════════════════════

        public static Mesh WeldUv0(Mesh source)
        {
            var verts = source.vertices;
            var normals = source.normals;
            var uv0 = source.uv;
            var tris = source.triangles;
            int vertCount = source.vertexCount;

            if (uv0 == null || uv0.Length == 0)
                return Object.Instantiate(source);

            bool hasNormals = normals != null && normals.Length == vertCount;
            var weldMap = BuildWeldMap(verts, uv0, normals, hasNormals);

            if (weldMap.Count == 0)
            {
                Debug.Log($"[UV0Fix] '{source.name}': no false seams found");
                return Object.Instantiate(source);
            }

            // Remap index buffer
            int[] newTris = new int[tris.Length];
            for (int i = 0; i < tris.Length; i++)
            {
                int v = tris[i];
                newTris[i] = weldMap.TryGetValue(v, out int target) ? target : v;
            }

            // Find used vertices
            var used = new HashSet<int>();
            for (int i = 0; i < newTris.Length; i++)
                used.Add(newTris[i]);

            // Build compaction map: old index → new index
            int[] compactMap = new int[vertCount];
            for (int i = 0; i < vertCount; i++) compactMap[i] = -1;

            int newVertCount = 0;
            for (int i = 0; i < vertCount; i++)
            {
                if (used.Contains(i))
                    compactMap[i] = newVertCount++;
            }

            // Remap triangles to compacted indices
            for (int i = 0; i < newTris.Length; i++)
                newTris[i] = compactMap[newTris[i]];

            // Build compacted vertex arrays
            var newVerts = new Vector3[newVertCount];
            Vector3[] newNormals = hasNormals ? new Vector3[newVertCount] : null;
            var newUv0 = new Vector2[newVertCount];

            // Also transfer tangents, uv1, colors if present
            var tangents = source.tangents;
            bool hasTangents = tangents != null && tangents.Length == vertCount;
            Vector4[] newTangents = hasTangents ? new Vector4[newVertCount] : null;

            var uv1List = new List<Vector2>();
            source.GetUVs(1, uv1List);
            bool hasUv1 = uv1List.Count == vertCount;
            Vector2[] uv1 = hasUv1 ? uv1List.ToArray() : null;
            Vector2[] newUv1 = hasUv1 ? new Vector2[newVertCount] : null;

            var colors = source.colors;
            bool hasColors = colors != null && colors.Length == vertCount;
            Color[] newColors = hasColors ? new Color[newVertCount] : null;

            var boneWeights = source.boneWeights;
            bool hasBW = boneWeights != null && boneWeights.Length == vertCount;
            BoneWeight[] newBW = hasBW ? new BoneWeight[newVertCount] : null;

            for (int i = 0; i < vertCount; i++)
            {
                int ni = compactMap[i];
                if (ni < 0) continue;
                newVerts[ni] = verts[i];
                newUv0[ni] = uv0[i];
                if (hasNormals) newNormals[ni] = normals[i];
                if (hasTangents) newTangents[ni] = tangents[i];
                if (hasUv1) newUv1[ni] = uv1[i];
                if (hasColors) newColors[ni] = colors[i];
                if (hasBW) newBW[ni] = boneWeights[i];
            }

            // Build new mesh
            var result = new Mesh();
            result.name = source.name + "_welded";
            result.vertices = newVerts;
            result.uv = newUv0;
            if (hasNormals) result.normals = newNormals;
            if (hasTangents) result.tangents = newTangents;
            if (hasUv1) result.SetUVs(1, newUv1);
            if (hasColors) result.colors = newColors;
            if (hasBW) result.boneWeights = newBW;

            // Handle submeshes
            int subCount = source.subMeshCount;
            result.subMeshCount = subCount;
            int triOffset = 0;
            for (int s = 0; s < subCount; s++)
            {
                var desc = source.GetSubMesh(s);
                int idxCount = desc.indexCount;
                int[] subTris = new int[idxCount];
                System.Array.Copy(newTris, triOffset, subTris, 0, idxCount);
                result.SetTriangles(subTris, s);
                triOffset += idxCount;
            }

            // Copy bind poses if skinned
            if (source.bindposes != null && source.bindposes.Length > 0)
                result.bindposes = source.bindposes;

            result.RecalculateBounds();

            int removed = vertCount - newVertCount;
            Debug.Log($"[UV0Fix] '{source.name}': welded {weldMap.Count} pairs, " +
                      $"removed {removed} vertices ({vertCount} → {newVertCount})");

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  WeldInPlace: apply weld directly to an existing mesh
        //  Used by postprocessor to weld FBX mesh before UV2 injection.
        //  Returns true if weld was applied (vertex count changed).
        // ═══════════════════════════════════════════════════════════

        public static bool WeldInPlace(Mesh mesh)
        {
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var uv0 = mesh.uv;
            var tris = mesh.triangles;
            int vertCount = mesh.vertexCount;

            if (uv0 == null || uv0.Length == 0) return false;

            bool hasNormals = normals != null && normals.Length == vertCount;
            var weldMap = BuildWeldMap(verts, uv0, normals, hasNormals);
            if (weldMap.Count == 0) return false;

            // Remap index buffer
            int[] newTris = new int[tris.Length];
            for (int i = 0; i < tris.Length; i++)
            {
                int v = tris[i];
                newTris[i] = weldMap.TryGetValue(v, out int target) ? target : v;
            }

            // Find used vertices
            var used = new HashSet<int>();
            for (int i = 0; i < newTris.Length; i++)
                used.Add(newTris[i]);

            // Build compaction map
            int[] compactMap = new int[vertCount];
            for (int i = 0; i < vertCount; i++) compactMap[i] = -1;
            int newVertCount = 0;
            for (int i = 0; i < vertCount; i++)
                if (used.Contains(i))
                    compactMap[i] = newVertCount++;

            // Remap triangles
            for (int i = 0; i < newTris.Length; i++)
                newTris[i] = compactMap[newTris[i]];

            // Compact all vertex attributes
            var newVerts = new Vector3[newVertCount];
            Vector3[] newNormals = hasNormals ? new Vector3[newVertCount] : null;
            var newUv0 = new Vector2[newVertCount];

            var tangents = mesh.tangents;
            bool hasTangents = tangents != null && tangents.Length == vertCount;
            Vector4[] newTangents = hasTangents ? new Vector4[newVertCount] : null;

            var uv1List = new List<Vector2>();
            mesh.GetUVs(1, uv1List);
            bool hasUv1 = uv1List.Count == vertCount;
            Vector2[] uv1 = hasUv1 ? uv1List.ToArray() : null;
            Vector2[] newUv1 = hasUv1 ? new Vector2[newVertCount] : null;

            var colors = mesh.colors;
            bool hasColors = colors != null && colors.Length == vertCount;
            Color[] newColors = hasColors ? new Color[newVertCount] : null;

            var boneWeights = mesh.boneWeights;
            bool hasBW = boneWeights != null && boneWeights.Length == vertCount;
            BoneWeight[] newBW = hasBW ? new BoneWeight[newVertCount] : null;

            for (int i = 0; i < vertCount; i++)
            {
                int ni = compactMap[i];
                if (ni < 0) continue;
                newVerts[ni] = verts[i];
                newUv0[ni] = uv0[i];
                if (hasNormals) newNormals[ni] = normals[i];
                if (hasTangents) newTangents[ni] = tangents[i];
                if (hasUv1) newUv1[ni] = uv1[i];
                if (hasColors) newColors[ni] = colors[i];
                if (hasBW) newBW[ni] = boneWeights[i];
            }

            // Capture submesh layout before Clear
            int subCount = mesh.subMeshCount;
            var subDescs = new UnityEngine.Rendering.SubMeshDescriptor[subCount];
            for (int s = 0; s < subCount; s++)
                subDescs[s] = mesh.GetSubMesh(s);

            // Copy bind poses if skinned
            var bindPoses = mesh.bindposes;

            // Apply to mesh in-place
            mesh.Clear();
            mesh.vertices = newVerts;
            mesh.uv = newUv0;
            if (hasNormals) mesh.normals = newNormals;
            if (hasTangents) mesh.tangents = newTangents;
            if (hasUv1) mesh.SetUVs(1, newUv1);
            if (hasColors) mesh.colors = newColors;
            if (hasBW) mesh.boneWeights = newBW;
            if (bindPoses != null && bindPoses.Length > 0)
                mesh.bindposes = bindPoses;

            // Restore submeshes with compacted triangles
            mesh.subMeshCount = subCount;
            int triOffset = 0;
            for (int s = 0; s < subCount; s++)
            {
                int idxCount = subDescs[s].indexCount;
                int[] subTris = new int[idxCount];
                System.Array.Copy(newTris, triOffset, subTris, 0, idxCount);
                mesh.SetTriangles(subTris, s);
                triOffset += idxCount;
            }

            mesh.RecalculateBounds();

            Debug.Log($"[UV0Fix] WeldInPlace '{mesh.name}': " +
                      $"welded {weldMap.Count} pairs, {vertCount} → {newVertCount} verts");
            return true;
        }

        // ═══════════════════════════════════════════════════════════
        //  Source-guided weld: merge target vertices by position+normal
        //  only when both map to the SAME source UV0 shell.
        //  This re-unifies islands that LOD simplification split apart
        //  while preserving intentional seams between different shells.
        //  UV0 of the kept vertex is preserved (lower index wins).
        // ═══════════════════════════════════════════════════════════

        public static Mesh SourceGuidedWeld(Mesh target, Mesh source)
        {
            var tVerts = target.vertices;
            var tNormals = target.normals;
            var tUv0 = target.uv;
            var tTris = target.triangles;
            int tVertCount = target.vertexCount;
            bool tHasNormals = tNormals != null && tNormals.Length == tVertCount;

            if (tUv0 == null || tUv0.Length == 0)
                return Object.Instantiate(target);

            // ── Build source: vertex → shell ID lookup ──
            var sUv0 = source.uv;
            var sVerts = source.vertices;
            var sTris = source.triangles;
            int sVertCount = source.vertexCount;

            if (sUv0 == null || sUv0.Length == 0)
            {
                Debug.LogWarning("[UV0Fix] Source mesh has no UV0, cannot guide weld");
                return Object.Instantiate(target);
            }

            var sourceShells = UvShellExtractor.Extract(sUv0, sTris);
            int[] srcVertShellId = new int[sVertCount];
            for (int i = 0; i < sVertCount; i++) srcVertShellId[i] = -1;
            foreach (var shell in sourceShells)
                foreach (int vi in shell.vertexIndices)
                    srcVertShellId[vi] = shell.shellId;

            // ── For each target vertex, find nearest source vertex by 3D ──
            // Then get its source shell ID
            int[] tVertShellId = new int[tVertCount];
            for (int i = 0; i < tVertCount; i++)
            {
                float bestDist = float.MaxValue;
                int bestSrc = 0;
                for (int s = 0; s < sVertCount; s++)
                {
                    float d = (tVerts[i] - sVerts[s]).sqrMagnitude;
                    if (d < bestDist) { bestDist = d; bestSrc = s; }
                }
                tVertShellId[i] = srcVertShellId[bestSrc];
            }

            // ── Find position+normal duplicates, weld if same source shell ──
            float cellSize = POS_EPS * 100f;
            var cells = new Dictionary<long, List<int>>();
            for (int i = 0; i < tVertCount; i++)
            {
                long key = SpatialKey(tVerts[i], cellSize);
                if (!cells.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    cells[key] = list;
                }
                list.Add(i);
            }

            var weldMap = new Dictionary<int, int>();
            foreach (var kv in cells)
            {
                var list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    int vi = list[i];
                    if (weldMap.ContainsKey(vi)) continue;

                    for (int j = i + 1; j < list.Count; j++)
                    {
                        int vj = list[j];
                        if (weldMap.ContainsKey(vj)) continue;

                        // Position must match
                        if (!VecEqual(tVerts[vi], tVerts[vj], POS_EPS)) continue;
                        // Normal must match
                        if (tHasNormals && !VecEqual(tNormals[vi], tNormals[vj], NORMAL_EPS)) continue;
                        // Both must map to the same source shell
                        if (tVertShellId[vi] != tVertShellId[vj]) continue;
                        if (tVertShellId[vi] < 0) continue; // no source match

                        weldMap[vj] = vi;
                    }
                }
            }

            if (weldMap.Count == 0)
            {
                Debug.Log($"[UV0Fix] SourceGuidedWeld '{target.name}': nothing to weld");
                return Object.Instantiate(target);
            }

            // ── Remap + compact (same as WeldUv0) ──
            int[] newTris = new int[tTris.Length];
            for (int i = 0; i < tTris.Length; i++)
                newTris[i] = weldMap.TryGetValue(tTris[i], out int w) ? w : tTris[i];

            var used = new HashSet<int>();
            for (int i = 0; i < newTris.Length; i++) used.Add(newTris[i]);

            int[] compactMap = new int[tVertCount];
            for (int i = 0; i < tVertCount; i++) compactMap[i] = -1;
            int newVertCount = 0;
            for (int i = 0; i < tVertCount; i++)
                if (used.Contains(i)) compactMap[i] = newVertCount++;

            for (int i = 0; i < newTris.Length; i++)
                newTris[i] = compactMap[newTris[i]];

            // Compact vertex attributes
            var newVerts = new Vector3[newVertCount];
            Vector3[] newNormals = tHasNormals ? new Vector3[newVertCount] : null;
            var newUv0 = new Vector2[newVertCount];

            var tangents = target.tangents;
            bool hasTangents = tangents != null && tangents.Length == tVertCount;
            Vector4[] newTangents = hasTangents ? new Vector4[newVertCount] : null;

            var uv1List = new List<Vector2>();
            target.GetUVs(1, uv1List);
            bool hasUv1 = uv1List.Count == tVertCount;
            Vector2[] uv1 = hasUv1 ? uv1List.ToArray() : null;
            Vector2[] newUv1 = hasUv1 ? new Vector2[newVertCount] : null;

            var colors = target.colors;
            bool hasColors = colors != null && colors.Length == tVertCount;
            Color[] newColors = hasColors ? new Color[newVertCount] : null;

            var boneWeights = target.boneWeights;
            bool hasBW = boneWeights != null && boneWeights.Length == tVertCount;
            BoneWeight[] newBW = hasBW ? new BoneWeight[newVertCount] : null;

            for (int i = 0; i < tVertCount; i++)
            {
                int ni = compactMap[i];
                if (ni < 0) continue;
                newVerts[ni] = tVerts[i];
                newUv0[ni] = tUv0[i];
                if (tHasNormals) newNormals[ni] = tNormals[i];
                if (hasTangents) newTangents[ni] = tangents[i];
                if (hasUv1) newUv1[ni] = uv1[i];
                if (hasColors) newColors[ni] = colors[i];
                if (hasBW) newBW[ni] = boneWeights[i];
            }

            var result = new Mesh();
            result.name = target.name + "_welded";
            result.vertices = newVerts;
            result.uv = newUv0;
            if (tHasNormals) result.normals = newNormals;
            if (hasTangents) result.tangents = newTangents;
            if (hasUv1) result.SetUVs(1, newUv1);
            if (hasColors) result.colors = newColors;
            if (hasBW) result.boneWeights = newBW;

            int subCount = target.subMeshCount;
            result.subMeshCount = subCount;
            int triOffset = 0;
            for (int s = 0; s < subCount; s++)
            {
                var desc = target.GetSubMesh(s);
                int idxCount = desc.indexCount;
                int[] subTris = new int[idxCount];
                System.Array.Copy(newTris, triOffset, subTris, 0, idxCount);
                result.SetTriangles(subTris, s);
                triOffset += idxCount;
            }

            if (target.bindposes != null && target.bindposes.Length > 0)
                result.bindposes = target.bindposes;

            result.RecalculateBounds();

            int removed = tVertCount - newVertCount;
            int shellsBefore = UvShellExtractor.Extract(tUv0, tTris).Count;
            int shellsAfter = UvShellExtractor.Extract(newUv0, newTris).Count;
            Debug.Log($"[UV0Fix] SourceGuidedWeld '{target.name}': " +
                      $"welded {weldMap.Count} pairs, removed {removed} verts " +
                      $"({tVertCount} → {newVertCount}), " +
                      $"shells {shellsBefore} → {shellsAfter}");

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  Build weld map: duplicate vertex → keep vertex
        //  Groups vertices by quantized position, then checks
        //  UV0 + normal within epsilon.
        // ═══════════════════════════════════════════════════════════

        static Dictionary<int, int> BuildWeldMap(
            Vector3[] verts, Vector2[] uv0, Vector3[] normals, bool hasNormals)
        {
            int vertCount = verts.Length;
            var weldMap = new Dictionary<int, int>();

            // Spatial hash: quantize position to grid cells
            float cellSize = POS_EPS * 100f; // generous cell to catch all candidates
            var cells = new Dictionary<long, List<int>>();

            for (int i = 0; i < vertCount; i++)
            {
                long key = SpatialKey(verts[i], cellSize);
                if (!cells.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    cells[key] = list;
                }
                list.Add(i);
            }

            // Check all pairs within each cell + neighbor cells
            foreach (var kv in cells)
            {
                var list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    int vi = list[i];
                    if (weldMap.ContainsKey(vi)) continue;

                    for (int j = i + 1; j < list.Count; j++)
                    {
                        int vj = list[j];
                        if (weldMap.ContainsKey(vj)) continue;

                        if (!VecEqual(verts[vi], verts[vj], POS_EPS)) continue;
                        if (vi < uv0.Length && vj < uv0.Length &&
                            !Vec2Equal(uv0[vi], uv0[vj], UV_EPS)) continue;
                        if (hasNormals &&
                            !VecEqual(normals[vi], normals[vj], NORMAL_EPS)) continue;

                        // vj is duplicate of vi — map vj → vi (keep lower index)
                        weldMap[vj] = vi;
                    }
                }
            }

            return weldMap;
        }

        // ═══════════════════════════════════════════════════════════
        //  Utilities
        // ═══════════════════════════════════════════════════════════

        static float SignedArea2D(Vector2 a, Vector2 b, Vector2 c)
        {
            return 0.5f * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));
        }

        static long SpatialKey(Vector3 v, float cellSize)
        {
            long x = (long)Mathf.FloorToInt(v.x / cellSize);
            long y = (long)Mathf.FloorToInt(v.y / cellSize);
            long z = (long)Mathf.FloorToInt(v.z / cellSize);
            // Simple hash combine
            return x * 73856093L ^ y * 19349663L ^ z * 83492791L;
        }

        static bool VecEqual(Vector3 a, Vector3 b, float eps)
        {
            return Mathf.Abs(a.x - b.x) <= eps &&
                   Mathf.Abs(a.y - b.y) <= eps &&
                   Mathf.Abs(a.z - b.z) <= eps;
        }

        static bool Vec2Equal(Vector2 a, Vector2 b, float eps)
        {
            return Mathf.Abs(a.x - b.x) <= eps &&
                   Mathf.Abs(a.y - b.y) <= eps;
        }
    }
}
