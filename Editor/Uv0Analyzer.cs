// Uv0Analyzer.cs — UV0 quality analysis and false seam welding
// Detects: false seams (weld candidates), degenerate UV triangles,
// flipped UV triangles, overlapping shells.
// Fix: welds false seams by merging duplicate vertex indices.

using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
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
            falseSeamPairs > 0 || degenerateTriangles > 0;
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
                UvtLog.Warn($"[UV0Analyze] '{mesh.name}': no UV0");
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

        /// <summary>
        /// Returns the set of vertex indices that participate in false seams
        /// (duplicate position+UV0+normal). Used by Model Builder problem preview.
        /// Returns null if no false seams found or mesh has no UV0.
        /// </summary>
        internal static HashSet<int> GetFalseSeamVertices(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) return null;
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var uv0 = mesh.uv;
            if (uv0 == null || uv0.Length == 0) return null;
            bool hasNormals = normals != null && normals.Length == mesh.vertexCount;
            var weldMap = BuildWeldMap(verts, uv0, normals, hasNormals);
            if (weldMap.Count == 0) return null;
            // Both keys (source) and values (target) are seam vertices
            var result = new HashSet<int>();
            foreach (var kv in weldMap)
            {
                result.Add(kv.Key);
                result.Add(kv.Value);
            }
            return result;
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
                UvtLog.Verbose($"[UV0Fix] '{source.name}': no false seams found");
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
            UvtLog.Verbose($"[UV0Fix] '{source.name}': welded {weldMap.Count} pairs, " +
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

            UvtLog.Verbose($"[UV0Fix] WeldInPlace '{mesh.name}': " +
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
                UvtLog.Warn("[UV0Fix] Source mesh has no UV0, cannot guide weld");
                return Object.Instantiate(target);
            }

            var sourceShells = UvShellExtractor.Extract(sUv0, sTris);
            int[] srcVertShellId = new int[sVertCount];
            for (int i = 0; i < sVertCount; i++) srcVertShellId[i] = -1;
            foreach (var shell in sourceShells)
                foreach (int vi in shell.vertexIndices)
                    srcVertShellId[vi] = shell.shellId;

            // ── Spatial hash grid for source vertices (replaces O(n*m) brute force) ──
            // Technique from UnityMeshSimplifier: spatial hashing for efficient
            // nearest-neighbor queries. Cell size based on mesh AABB diagonal.
            Vector3 srcMin = sVerts[0], srcMax = sVerts[0];
            for (int s = 1; s < sVertCount; s++)
            {
                srcMin = Vector3.Min(srcMin, sVerts[s]);
                srcMax = Vector3.Max(srcMax, sVerts[s]);
            }
            float srcDiag = (srcMax - srcMin).magnitude;
            float srcCellSize = Mathf.Max(srcDiag / Mathf.Max(Mathf.Pow(sVertCount, 0.333f), 1f), 1e-5f);

            var srcGrid = new Dictionary<long, List<int>>();
            for (int s = 0; s < sVertCount; s++)
            {
                long key = SpatialKey(sVerts[s], srcCellSize);
                if (!srcGrid.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    srcGrid[key] = list;
                }
                list.Add(s);
            }

            // For each target vertex, find nearest source vertex via grid lookup (27 cells)
            int[] tVertShellId = new int[tVertCount];
            int gridMisses = 0;
            for (int i = 0; i < tVertCount; i++)
            {
                Vector3 tp = tVerts[i];
                int cx = Mathf.FloorToInt(tp.x / srcCellSize);
                int cy = Mathf.FloorToInt(tp.y / srcCellSize);
                int cz = Mathf.FloorToInt(tp.z / srcCellSize);

                float bestDist = float.MaxValue;
                int bestSrc = -1;

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    long nKey = (long)(cx + dx) * 73856093L ^
                                (long)(cy + dy) * 19349663L ^
                                (long)(cz + dz) * 83492791L;
                    if (!srcGrid.TryGetValue(nKey, out var bucket)) continue;
                    for (int bi = 0; bi < bucket.Count; bi++)
                    {
                        int s = bucket[bi];
                        float d = (tp - sVerts[s]).sqrMagnitude;
                        if (d < bestDist) { bestDist = d; bestSrc = s; }
                    }
                }

                // Fallback: if grid miss (target vertex far from all source),
                // expand search to all source vertices for this vertex only.
                if (bestSrc < 0)
                {
                    gridMisses++;
                    for (int s = 0; s < sVertCount; s++)
                    {
                        float d = (tp - sVerts[s]).sqrMagnitude;
                        if (d < bestDist) { bestDist = d; bestSrc = s; }
                    }
                }

                tVertShellId[i] = bestSrc >= 0 ? srcVertShellId[bestSrc] : -1;
            }

            if (gridMisses > 0)
                UvtLog.Verbose($"[UV0Fix] SourceGuidedWeld grid fallback for {gridMisses} verts");

            // ── Find position+normal duplicates, weld if conditions met ──
            // Two weld modes (from UnityMeshSimplifier seam/foldover distinction):
            //   Foldover (same UV0): weld unconditionally — genuinely duplicate verts
            //   Seam (different UV0): weld only if same source shell
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
            int foldoverWelds = 0, seamWelds = 0;
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

                        // Foldover path: same UV0 → weld unconditionally
                        if (vi < tUv0.Length && vj < tUv0.Length &&
                            Vec2Equal(tUv0[vi], tUv0[vj], UV_EPS))
                        {
                            weldMap[vj] = vi;
                            foldoverWelds++;
                            continue;
                        }

                        // Seam path: different UV0 → require same source shell
                        if (tVertShellId[vi] != tVertShellId[vj]) continue;
                        if (tVertShellId[vi] < 0) continue;

                        weldMap[vj] = vi;
                        seamWelds++;
                    }
                }
            }

            if (weldMap.Count == 0)
            {
                UvtLog.Verbose($"[UV0Fix] SourceGuidedWeld '{target.name}': nothing to weld");
                return target;
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
            UvtLog.Verbose($"[UV0Fix] SourceGuidedWeld '{target.name}': " +
                      $"welded {weldMap.Count} pairs (foldover:{foldoverWelds} seam:{seamWelds}), " +
                      $"removed {removed} verts ({tVertCount} → {newVertCount}), " +
                      $"shells {shellsBefore} → {shellsAfter}");

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  UV Edge Weld — merge UV seam vertices by edge adjacency
        //  For each mesh edge shared by 2 triangles, if UV0 values
        //  on both endpoints are within threshold → merge vertices.
        //  No source mesh needed. Works purely in UV space.
        // ═══════════════════════════════════════════════════════════

        public static Mesh UvEdgeWeld(Mesh mesh, float uvThreshold = 0.002f)
        {
            if (mesh == null) return null;

            var verts   = mesh.vertices;
            var uv0List = new List<Vector2>();
            mesh.GetUVs(0, uv0List);
            if (uv0List.Count != verts.Length) return mesh;
            var uv0 = uv0List.ToArray();

            int vertCount = verts.Length;

            // Flatten all submesh triangles into one array
            int[] tris;
            {
                var allTris = new List<int>();
                for (int s = 0; s < mesh.subMeshCount; s++)
                    allTris.AddRange(mesh.GetTriangles(s));
                tris = allTris.ToArray();
            }
            int triCount = tris.Length / 3;
            if (triCount == 0) return mesh;

            // ── 1. Build position groups ──
            // Vertices at same 3D position → same group ID.
            // SpatialKey uses XOR hash which can have collisions, so we must verify
            // actual position distance within each hash bucket to avoid merging
            // vertices at different positions (which causes geometric stretching).
            const float posEps = 1e-5f;
            int[] posGroup = new int[vertCount];
            int nextGroup = 0;

            // Hash buckets: SpatialKey → list of vertex indices
            var hashBuckets = new Dictionary<long, List<int>>();
            for (int i = 0; i < vertCount; i++)
            {
                long h = SpatialKey(verts[i], posEps);
                if (!hashBuckets.TryGetValue(h, out var bucket))
                {
                    bucket = new List<int>();
                    hashBuckets[h] = bucket;
                }
                bucket.Add(i);
            }

            // Within each bucket, split into sub-groups by actual position distance
            foreach (var bucket in hashBuckets.Values)
            {
                // Track which sub-group each bucket member belongs to
                // subGroupLeaders[k] = first vertex index of sub-group k
                var subGroupLeaders = new List<int>();
                var subGroupIds = new List<int>(); // parallel: group ID for each sub-group

                for (int bi = 0; bi < bucket.Count; bi++)
                {
                    int vi = bucket[bi];
                    // Find a sub-group whose leader is within posEps
                    int found = -1;
                    for (int sg = 0; sg < subGroupLeaders.Count; sg++)
                    {
                        if (Vector3.Distance(verts[vi], verts[subGroupLeaders[sg]]) <= posEps)
                        {
                            found = sg;
                            break;
                        }
                    }
                    if (found >= 0)
                    {
                        posGroup[vi] = subGroupIds[found];
                    }
                    else
                    {
                        int gid = nextGroup++;
                        subGroupLeaders.Add(vi);
                        subGroupIds.Add(gid);
                        posGroup[vi] = gid;
                    }
                }
            }

            // ── 2. Build edge adjacency ──
            // EdgeKey = sorted pair of position group IDs
            // Value = list of (vertA, vertB) where vertA is the vertex with lower group
            var edgeMap = new Dictionary<long, List<(int vA, int vB)>>();

            for (int t = 0; t < triCount; t++)
            {
                int i0 = tris[t * 3 + 0];
                int i1 = tris[t * 3 + 1];
                int i2 = tris[t * 3 + 2];

                AddEdge(edgeMap, posGroup, i0, i1);
                AddEdge(edgeMap, posGroup, i1, i2);
                AddEdge(edgeMap, posGroup, i2, i0);
            }

            // ── 3. Union-Find for vertex merging ──
            int[] parent = new int[vertCount];
            int[] rank   = new int[vertCount];
            for (int i = 0; i < vertCount; i++) parent[i] = i;

            int weldCount = 0;

            foreach (var kv in edgeMap)
            {
                var edges = kv.Value;
                if (edges.Count < 2) continue;

                // Compare all pairs of triangle-edges sharing this geometric edge
                for (int a = 0; a < edges.Count; a++)
                {
                    for (int b = a + 1; b < edges.Count; b++)
                    {
                        var eA = edges[a];
                        var eB = edges[b];

                        // Already same vertices? Skip
                        if (Find(parent, eA.vA) == Find(parent, eB.vA) &&
                            Find(parent, eA.vB) == Find(parent, eB.vB))
                            continue;

                        // Check UV distance at both endpoints
                        float dA = Vector2.Distance(uv0[eA.vA], uv0[eB.vA]);
                        float dB = Vector2.Distance(uv0[eA.vB], uv0[eB.vB]);

                        if (dA <= uvThreshold && dB <= uvThreshold)
                        {
                            if (Find(parent, eA.vA) != Find(parent, eB.vA))
                            {
                                Union(parent, rank, eA.vA, eB.vA);
                                weldCount++;
                            }
                            if (Find(parent, eA.vB) != Find(parent, eB.vB))
                            {
                                Union(parent, rank, eA.vB, eB.vB);
                                weldCount++;
                            }
                        }
                    }
                }
            }

            if (weldCount == 0) return mesh;

            // ── 3b. VALIDATE: ensure merged vertices are actually co-located ──
            // SpatialKey uses XOR hash which can have collisions — two vertices at
            // DIFFERENT positions could end up in the same position group, causing
            // incorrect merges that produce geometric stretching.
            {
                int badMerges = 0;
                int undone = 0;
                for (int i = 0; i < vertCount; i++)
                {
                    int root = Find(parent, i);
                    if (root == i) continue;
                    float dist = Vector3.Distance(verts[i], verts[root]);
                    if (dist > posEps * 10f) // generous threshold: 10x posEps
                    {
                        badMerges++;
                        // Undo this merge by making vertex its own root
                        parent[i] = i;
                        undone++;
                    }
                }
                if (badMerges > 0)
                {
                    UvtLog.Warn($"[UV0Fix] UvEdgeWeld '{mesh.name}': detected {badMerges} bad merges " +
                                $"(vertices at different positions merged due to hash collision), " +
                                $"undone {undone} merges to prevent stretching");
                    // Re-run Find with path compression to fix up parent chains
                    for (int i = 0; i < vertCount; i++)
                        Find(parent, i);

                    // Recount actual welds after undo
                    int actualWelds = 0;
                    for (int i = 0; i < vertCount; i++)
                        if (Find(parent, i) != i) actualWelds++;
                    if (actualWelds == 0) return mesh;
                }
            }

            // ── 4. Rebuild mesh with merged vertices ──
            // Remap indices
            int[] newTris = new int[tris.Length];
            for (int i = 0; i < tris.Length; i++)
                newTris[i] = Find(parent, tris[i]);

            // Compact
            var used = new HashSet<int>();
            for (int i = 0; i < newTris.Length; i++) used.Add(newTris[i]);

            int[] compactMap = new int[vertCount];
            for (int i = 0; i < vertCount; i++) compactMap[i] = -1;
            int newVertCount = 0;
            for (int i = 0; i < vertCount; i++)
                if (used.Contains(i)) compactMap[i] = newVertCount++;

            for (int i = 0; i < newTris.Length; i++)
                newTris[i] = compactMap[newTris[i]];

            // Copy attributes
            var normals = mesh.normals;
            bool hasNormals = normals != null && normals.Length == vertCount;
            var tangents = mesh.tangents;
            bool hasTangents = tangents != null && tangents.Length == vertCount;
            var uv1List = new List<Vector2>();
            mesh.GetUVs(1, uv1List);
            bool hasUv1 = uv1List.Count == vertCount;
            Vector2[] uv1 = hasUv1 ? uv1List.ToArray() : null;
            var colors = mesh.colors;
            bool hasColors = colors != null && colors.Length == vertCount;
            var boneWeights = mesh.boneWeights;
            bool hasBW = boneWeights != null && boneWeights.Length == vertCount;

            // Read UV channels 2-7 for interpolation during weld
            const int UV_INTERP_START = 2;
            const int UV_INTERP_END = 8;
            var extraUvData = new List<Vector4>[UV_INTERP_END];
            bool[] hasExtraUv = new bool[UV_INTERP_END];
            for (int ch = UV_INTERP_START; ch < UV_INTERP_END; ch++)
            {
                var uvList = new List<Vector4>();
                mesh.GetUVs(ch, uvList);
                hasExtraUv[ch] = uvList.Count == vertCount;
                extraUvData[ch] = hasExtraUv[ch] ? uvList : null;
            }

            // Build Union-Find group membership for UV2+ interpolation.
            // groupMembers[root] = list of all original vertex indices in that group.
            var groupMembers = new Dictionary<int, List<int>>();
            for (int i = 0; i < vertCount; i++)
            {
                int root = Find(parent, i);
                if (!groupMembers.TryGetValue(root, out var members))
                {
                    members = new List<int>();
                    groupMembers[root] = members;
                }
                members.Add(i);
            }

            var newVerts   = new Vector3[newVertCount];
            var newUv0     = new Vector2[newVertCount];
            Vector3[] newNormals  = hasNormals  ? new Vector3[newVertCount] : null;
            Vector4[] newTangents = hasTangents ? new Vector4[newVertCount] : null;
            Vector2[] newUv1      = hasUv1      ? new Vector2[newVertCount] : null;
            Color[]   newColors   = hasColors   ? new Color[newVertCount]   : null;
            BoneWeight[] newBW    = hasBW       ? new BoneWeight[newVertCount] : null;
            var newExtraUv = new List<Vector4>[UV_INTERP_END];
            for (int ch = UV_INTERP_START; ch < UV_INTERP_END; ch++)
                if (hasExtraUv[ch]) newExtraUv[ch] = new List<Vector4>(new Vector4[newVertCount]);

            // Track which compact slots have been written (for averaging)
            var slotWritten = new bool[newVertCount];

            for (int i = 0; i < vertCount; i++)
            {
                int ni = compactMap[i];
                if (ni < 0) continue;
                if (!slotWritten[ni])
                {
                    // First vertex in this slot — copy UV0, UV1, and base attributes directly
                    newVerts[ni] = verts[i];
                    newUv0[ni]   = uv0[i];
                    if (hasNormals)  newNormals[ni]  = normals[i];
                    if (hasTangents) newTangents[ni] = tangents[i];
                    if (hasUv1)      newUv1[ni]      = uv1[i];
                    if (hasColors)   newColors[ni]   = colors[i];
                    if (hasBW)       newBW[ni]       = boneWeights[i];

                    // For UV2+: compute average across all members of the Union-Find group
                    int root = Find(parent, i);
                    var members = groupMembers[root];
                    for (int ch = UV_INTERP_START; ch < UV_INTERP_END; ch++)
                    {
                        if (!hasExtraUv[ch]) continue;
                        var srcUv = extraUvData[ch];
                        Vector4 sum = Vector4.zero;
                        int count = 0;
                        for (int m = 0; m < members.Count; m++)
                        {
                            int mi = members[m];
                            sum += srcUv[mi];
                            count++;
                        }
                        newExtraUv[ch][ni] = count > 0 ? sum / count : srcUv[i];
                    }

                    slotWritten[ni] = true;
                }
            }

            var result = new Mesh();
            result.name = mesh.name;
            result.vertices = newVerts;
            result.uv = newUv0;
            if (hasNormals)  result.normals  = newNormals;
            if (hasTangents) result.tangents = newTangents;
            if (hasUv1) result.SetUVs(1, newUv1);
            if (hasColors) result.colors = newColors;
            if (hasBW) result.boneWeights = newBW;
            for (int ch = UV_INTERP_START; ch < UV_INTERP_END; ch++)
                if (hasExtraUv[ch]) result.SetUVs(ch, newExtraUv[ch]);

            int subCount = mesh.subMeshCount;
            result.subMeshCount = subCount;
            int triOffset = 0;
            for (int s = 0; s < subCount; s++)
            {
                var desc = mesh.GetSubMesh(s);
                int idxCount = desc.indexCount;
                int[] subTris = new int[idxCount];
                System.Array.Copy(newTris, triOffset, subTris, 0, idxCount);
                result.SetTriangles(subTris, s);
                triOffset += idxCount;
            }

            if (mesh.bindposes != null && mesh.bindposes.Length > 0)
                result.bindposes = mesh.bindposes;

            result.RecalculateBounds();

            int shellsBefore = UvShellExtractor.Extract(uv0, tris).Count;
            int shellsAfter  = UvShellExtractor.Extract(newUv0, newTris).Count;
            int removed = vertCount - newVertCount;
            UvtLog.Verbose($"[UV0Fix] UvEdgeWeld '{mesh.name}': " +
                      $"welded {weldCount} pairs, removed {removed} verts " +
                      $"({vertCount} → {newVertCount}), " +
                      $"shells {shellsBefore} → {shellsAfter}");

            return result;
        }

        static void AddEdge(Dictionary<long, List<(int vA, int vB)>> edgeMap,
                            int[] posGroup, int i0, int i1)
        {
            int gA = posGroup[i0], gB = posGroup[i1];
            // Normalize: lower group first
            int vLow, vHigh;
            if (gA <= gB) { vLow = i0; vHigh = i1; }
            else           { vLow = i1; vHigh = i0; gA = posGroup[i1]; gB = posGroup[i0]; }

            long key = ((long)gA << 32) | (uint)gB;
            if (!edgeMap.ContainsKey(key))
                edgeMap[key] = new List<(int, int)>();
            edgeMap[key].Add((vLow, vHigh));
        }

        static int Find(int[] parent, int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        static void Union(int[] parent, int[] rank, int a, int b)
        {
            a = Find(parent, a); b = Find(parent, b);
            if (a == b) return;
            if (rank[a] < rank[b]) { int t = a; a = b; b = t; }
            parent[b] = a;
            if (rank[a] == rank[b]) rank[a]++;
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
