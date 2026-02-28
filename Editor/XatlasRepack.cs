// XatlasRepack.cs — High-level xatlas repack with C#-side UV2 write-back
// Place in Assets/Editor/

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public struct RepackOptions
    {
        public uint padding;
        public uint resolution;
        public float texelsPerUnit;
        public bool bilinear;
        public bool blockAlign;
        public bool bruteForce;

        public static RepackOptions Default => new RepackOptions
        {
            padding    = 4,
            resolution = 0,
            texelsPerUnit = 0f,
            bilinear   = true,
            blockAlign = false,
            bruteForce = false,
        };
    }

    public struct RepackResult
    {
        public bool ok;
        public uint atlasWidth;
        public uint atlasHeight;
        public uint chartCount;
        public int  shellCount;
        public int  overlapGroupCount;
        public int  conflictVertices;
        public int  orphanVertices;
        public int  orphanTriangles;
        public int  snappedVertices;
        public string error;
    }

    public static class XatlasRepack
    {
        const uint ORPHAN_CHART = uint.MaxValue;

        /// <summary>
        /// Convenience wrapper: repack UV0 shells into UV2, return packed UV2 array.
        /// Does NOT modify the original mesh.
        /// </summary>
        public static Vector2[] RepackUv(Mesh mesh, Vector2[] uv0, uint[] faceShellIds,
            int resolution, int padding, bool rotate)
        {
            var opts = new RepackOptions
            {
                resolution = (uint)resolution,
                padding = (uint)padding,
                texelsPerUnit = 0f,
                bilinear = true,
                blockAlign = false,
                bruteForce = false,
            };
            // Work on a temporary copy so original mesh is untouched
            var tmp = Object.Instantiate(mesh);
            tmp.name = mesh.name + "_repack_tmp";
            var result = RepackSingle(tmp, opts);
            if (!result.ok)
            {
                Object.DestroyImmediate(tmp);
                return null;
            }
            var uvOut = new List<Vector2>();
            tmp.GetUVs(2, uvOut);
            Object.DestroyImmediate(tmp);
            return uvOut.ToArray();
        }

        public static RepackResult RepackSingle(Mesh mesh, RepackOptions opts)
        {
            var result = new RepackResult();

            // ── Read mesh data ──
            Vector2[] uv0 = mesh.uv;
            if (uv0 == null || uv0.Length == 0)
            {
                result.error = "Mesh has no UV0";
                return result;
            }

            int[] tris = mesh.triangles;
            int vertCount = mesh.vertexCount;
            int faceCount = tris.Length / 3;

            // ── Extract shells + build per-face shell IDs ──
            List<UvShell> shells;
            List<List<int>> overlapGroups;
            uint[] faceShellIds = UvShellExtractor.BuildPerFaceShellIds(
                uv0, tris, out shells, out overlapGroups);

            result.shellCount = shells.Count;
            result.overlapGroupCount = overlapGroups.Count;

            Debug.Log($"[xatlas] Input: '{mesh.name}', verts={vertCount}, faces={faceCount}, " +
                      $"shells={shells.Count}, overlap_groups={overlapGroups.Count}");

            foreach (var group in overlapGroups)
            {
                string s = "";
                foreach (int si in group)
                    s += $"shell{si}({shells[si].faceIndices.Count}f) ";
                Debug.Log($"[xatlas]   Overlap group: [{s.Trim()}]");
            }

            // ── Flatten UV0 ──
            float[] uvFlat = new float[vertCount * 2];
            for (int i = 0; i < vertCount; i++)
            {
                uvFlat[i * 2]     = uv0[i].x;
                uvFlat[i * 2 + 1] = uv0[i].y;
            }

            // ── Flatten indices ──
            uint[] indices = new uint[tris.Length];
            for (int i = 0; i < tris.Length; i++)
                indices[i] = (uint)tris[i];

            // ── xatlas pipeline ──
            XatlasNative.xatlasCreate();

            try
            {
                int addErr = XatlasNative.xatlasAddUvMesh(
                    uvFlat, (uint)vertCount,
                    indices, (uint)indices.Length,
                    faceShellIds, (uint)faceCount);

                if (addErr != 0)
                {
                    result.error = $"xatlasAddUvMesh error {addErr}";
                    return result;
                }

                XatlasNative.xatlasComputeCharts();

                XatlasNative.xatlasPackCharts(
                    0, opts.padding, opts.texelsPerUnit, opts.resolution,
                    opts.bilinear  ? 1 : 0,
                    opts.blockAlign ? 1 : 0,
                    opts.bruteForce ? 1 : 0);

                if (XatlasNative.xatlasGetMeshCount() == 0)
                {
                    result.error = "xatlas returned 0 meshes";
                    return result;
                }

                result.atlasWidth  = XatlasNative.xatlasGetAtlasWidth();
                result.atlasHeight = XatlasNative.xatlasGetAtlasHeight();
                result.chartCount  = XatlasNative.xatlasGetChartCount();

                // ── Get raw output data ──
                int outVertCount  = XatlasNative.xatlasGetOutputVertexCount(0);
                int outIndexCount = XatlasNative.xatlasGetOutputIndexCount(0);

                if (outVertCount == 0 || outIndexCount == 0)
                {
                    result.error = $"xatlas output empty: verts={outVertCount}, idx={outIndexCount}";
                    return result;
                }

                uint[]  outXref  = new uint[outVertCount];
                float[] outUV    = new float[outVertCount * 2];
                uint[]  outChart = new uint[outVertCount];
                uint[]  outIdx   = new uint[outIndexCount];

                XatlasNative.xatlasGetOutputVertexData(0, outXref, outUV, outChart, outVertCount);
                XatlasNative.xatlasGetOutputIndices(0, outIdx, outIndexCount);

                Debug.Log($"[xatlas] Output: verts={outVertCount}, idx={outIndexCount}, " +
                          $"charts={result.chartCount}");

                // ── C#-side UV2 assignment ──
                Vector2[] uv2;
                uint[] vertChartId;
                int conflicts;
                AssignUv2(vertCount, faceCount, tris,
                          outVertCount, outXref, outUV, outChart,
                          outIndexCount, outIdx,
                          out uv2, out vertChartId, out conflicts);

                result.conflictVertices = conflicts;

                // ── Post-process: fix orphan vertices ──
                int orphanVerts, orphanTris, snapped;
                FixOrphanVertices(uv2, tris, vertChartId, out orphanVerts, out orphanTris, out snapped);
                result.orphanVertices = orphanVerts;
                result.orphanTriangles = orphanTris;
                result.snappedVertices = snapped;

                // ── Diagnostic: top longest UV2 edges (after fix) ──
                DiagnoseLongestEdges(uv2, tris, faceShellIds, vertChartId, 10);

                // ── Apply UV2 (channel 2 — matches SourceMeshAnalyzer.GetUVs(2)) ──
                mesh.SetUVs(2, uv2);
                result.ok = true;

                // ── Stats ──
                int nonZero = 0;
                float minU = float.MaxValue, maxU = float.MinValue;
                float minV = float.MaxValue, maxV = float.MinValue;
                for (int i = 0; i < vertCount; i++)
                {
                    if (uv2[i].sqrMagnitude > 1e-12f)
                    {
                        nonZero++;
                        if (uv2[i].x < minU) minU = uv2[i].x;
                        if (uv2[i].x > maxU) maxU = uv2[i].x;
                        if (uv2[i].y < minV) minV = uv2[i].y;
                        if (uv2[i].y > maxV) maxV = uv2[i].y;
                    }
                }

                Debug.Log($"[xatlas] OK — atlas={result.atlasWidth}x{result.atlasHeight}, " +
                          $"charts={result.chartCount}, shells={shells.Count}, " +
                          $"conflicts={conflicts}, orphan_verts={orphanVerts}, " +
                          $"orphan_tris={orphanTris}, snapped={snapped}, " +
                          $"uv2_range=[{minU:F4}..{maxU:F4}]x[{minV:F4}..{maxV:F4}], " +
                          $"verts_with_uv2={nonZero}/{vertCount}");
            }
            finally
            {
                XatlasNative.xatlasDestroy();
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────
        // Fix orphan vertices: xatlas assigned chartIndex=0xFFFFFFFF to
        // vertices it couldn't place in any chart. These vertices get
        // near-zero UV2, creating diagonal stretches across the atlas.
        //
        // For each triangle containing an orphan vertex:
        //   - If 1 orphan: snap it to midpoint of the other 2 (valid) verts
        //   - If 2 orphans: snap both to the 1 valid vert
        //   - If 3 orphans: collapse to centroid (all near-zero anyway)
        //
        // Only snap if vertex is used in MORE orphan-tris than valid-tris,
        // to avoid breaking vertices that are mostly correct.
        // ─────────────────────────────────────────────────────────────────
        static void FixOrphanVertices(
            Vector2[] uv2, int[] tris, uint[] vertChartId,
            out int orphanVertCount, out int orphanTriCount, out int snappedCount)
        {
            orphanVertCount = 0;
            orphanTriCount = 0;
            snappedCount = 0;

            int vertCount = uv2.Length;
            int faceCount = tris.Length / 3;

            // Count orphan vertices
            bool[] isOrphan = new bool[vertCount];
            for (int v = 0; v < vertCount; v++)
            {
                if (vertChartId[v] == ORPHAN_CHART)
                {
                    isOrphan[v] = true;
                    orphanVertCount++;
                }
            }

            if (orphanVertCount == 0)
            {
                Debug.Log("[xatlas] Post-process: 0 orphan vertices");
                return;
            }

            // Log orphan details
            for (int v = 0; v < vertCount; v++)
            {
                if (isOrphan[v])
                    Debug.Log($"[xatlas]   Orphan vertex {v}: uv2=({uv2[v].x:F4},{uv2[v].y:F4})");
            }

            // Find triangles with orphan vertices, track per-vertex usage
            var orphanFaces = new List<int>();
            int[] orphanTriUse = new int[vertCount]; // how many orphan-tris use this vert
            int[] validTriUse  = new int[vertCount]; // how many valid-tris use this vert

            for (int f = 0; f < faceCount; f++)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                bool o0 = isOrphan[i0], o1 = isOrphan[i1], o2 = isOrphan[i2];

                if (o0 || o1 || o2)
                {
                    orphanFaces.Add(f);
                    orphanTriUse[i0]++; orphanTriUse[i1]++; orphanTriUse[i2]++;
                }
                else
                {
                    validTriUse[i0]++; validTriUse[i1]++; validTriUse[i2]++;
                }
            }

            orphanTriCount = orphanFaces.Count;
            Debug.Log($"[xatlas] Post-process: {orphanVertCount} orphan vertices, " +
                      $"{orphanTriCount} affected triangles");

            // Snap orphan vertices
            // Collect proposed snap targets (there may be multiple per vertex from different faces)
            var snapTargets = new Dictionary<int, List<Vector2>>();

            foreach (int f in orphanFaces)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                bool o0 = isOrphan[i0], o1 = isOrphan[i1], o2 = isOrphan[i2];

                int orphans = (o0 ? 1 : 0) + (o1 ? 1 : 0) + (o2 ? 1 : 0);

                if (orphans == 1)
                {
                    // 1 orphan → snap to midpoint of 2 valid
                    int ov = o0 ? i0 : (o1 ? i1 : i2);
                    Vector2 anchor;
                    if (o0) anchor = (uv2[i1] + uv2[i2]) * 0.5f;
                    else if (o1) anchor = (uv2[i0] + uv2[i2]) * 0.5f;
                    else anchor = (uv2[i0] + uv2[i1]) * 0.5f;

                    AddSnapTarget(snapTargets, ov, anchor);
                }
                else if (orphans == 2)
                {
                    // 2 orphans → snap both to the 1 valid vertex
                    if (!o0) { AddSnapTarget(snapTargets, i1, uv2[i0]); AddSnapTarget(snapTargets, i2, uv2[i0]); }
                    else if (!o1) { AddSnapTarget(snapTargets, i0, uv2[i1]); AddSnapTarget(snapTargets, i2, uv2[i1]); }
                    else { AddSnapTarget(snapTargets, i0, uv2[i2]); AddSnapTarget(snapTargets, i1, uv2[i2]); }
                }
                else // 3 orphans
                {
                    Vector2 centroid = (uv2[i0] + uv2[i1] + uv2[i2]) / 3f;
                    AddSnapTarget(snapTargets, i0, centroid);
                    AddSnapTarget(snapTargets, i1, centroid);
                    AddSnapTarget(snapTargets, i2, centroid);
                }
            }

            // Apply snaps: average all proposed targets for each vertex
            foreach (var kv in snapTargets)
            {
                int v = kv.Key;

                // Only snap if vertex appears more in orphan tris than valid tris
                if (orphanTriUse[v] < validTriUse[v])
                {
                    Debug.Log($"[xatlas]   Orphan vertex {v}: skipping snap " +
                              $"(orphan_tris={orphanTriUse[v]} < valid_tris={validTriUse[v]})");
                    continue;
                }

                var targets = kv.Value;
                Vector2 avg = Vector2.zero;
                for (int i = 0; i < targets.Count; i++)
                    avg += targets[i];
                avg /= targets.Count;

                Debug.Log($"[xatlas]   Orphan vertex {v}: snap ({uv2[v].x:F4},{uv2[v].y:F4}) " +
                          $"-> ({avg.x:F4},{avg.y:F4}) [{targets.Count} proposals]");

                uv2[v] = avg;
                snappedCount++;
            }

            Debug.Log($"[xatlas] Post-process done: {snappedCount}/{orphanVertCount} vertices snapped");
        }

        static void AddSnapTarget(Dictionary<int, List<Vector2>> dict, int vertIdx, Vector2 target)
        {
            if (!dict.TryGetValue(vertIdx, out var list))
            {
                list = new List<Vector2>(4);
                dict[vertIdx] = list;
            }
            list.Add(target);
        }

        // ─────────────────────────────────────────────────────────────────
        // Diagnostic: top longest UV2 edges
        // ─────────────────────────────────────────────────────────────────
        struct EdgeInfo
        {
            public int face;
            public int v0, v1;
            public uint shell;
            public uint chart0, chart1;
            public Vector2 uv2_0, uv2_1;
            public float length;
        }

        static void DiagnoseLongestEdges(Vector2[] uv2, int[] tris, uint[] faceShellIds,
                                          uint[] vertChartId, int topN)
        {
            int faceCount = tris.Length / 3;
            var longest = new List<EdgeInfo>(topN + 1);
            float minKeep = 0f;

            for (int f = 0; f < faceCount; f++)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                uint shell = faceShellIds[f];
                CheckEdge(longest, ref minKeep, topN, f, i0, i1, shell, uv2, vertChartId);
                CheckEdge(longest, ref minKeep, topN, f, i1, i2, shell, uv2, vertChartId);
                CheckEdge(longest, ref minKeep, topN, f, i2, i0, shell, uv2, vertChartId);
            }

            longest.Sort((a, b) => b.length.CompareTo(a.length));

            Debug.Log($"[xatlas] ── TOP {longest.Count} longest UV2 edges (after fix) ──");
            for (int i = 0; i < longest.Count; i++)
            {
                var e = longest[i];
                string chartStr = e.chart0 == ORPHAN_CHART ? "ORPHAN" : e.chart0.ToString();
                string chartStr1 = e.chart1 == ORPHAN_CHART ? "ORPHAN" : e.chart1.ToString();
                string tag = (e.chart0 != e.chart1) ? " ← PROBLEM" : "";
                Debug.Log($"[xatlas]   #{i}: face={e.face}, v=[{e.v0},{e.v1}], " +
                          $"shell={e.shell}, chart={chartStr}/{chartStr1}, " +
                          $"len={e.length:F4}, " +
                          $"uv2=({e.uv2_0.x:F4},{e.uv2_0.y:F4})->({e.uv2_1.x:F4},{e.uv2_1.y:F4}){tag}");
            }
            Debug.Log("[xatlas] ── end ──");
        }

        static void CheckEdge(List<EdgeInfo> list, ref float minKeep, int topN,
                               int face, int v0, int v1, uint shell,
                               Vector2[] uv2, uint[] vertChartId)
        {
            float len = (uv2[v0] - uv2[v1]).magnitude;
            if (len <= minKeep && list.Count >= topN) return;

            list.Add(new EdgeInfo
            {
                face = face, v0 = v0, v1 = v1, shell = shell,
                chart0 = vertChartId != null && v0 < vertChartId.Length ? vertChartId[v0] : ORPHAN_CHART,
                chart1 = vertChartId != null && v1 < vertChartId.Length ? vertChartId[v1] : ORPHAN_CHART,
                uv2_0 = uv2[v0], uv2_1 = uv2[v1], length = len,
            });

            if (list.Count > topN)
            {
                list.Sort((a, b) => b.length.CompareTo(a.length));
                list.RemoveAt(list.Count - 1);
                minKeep = list[list.Count - 1].length;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // UV2 assignment: majority vote per original vertex
        // ─────────────────────────────────────────────────────────────────
        static void AssignUv2(
            int vertCount, int faceCount, int[] tris,
            int outVertCount, uint[] outXref, float[] outUV, uint[] outChart,
            int outIndexCount, uint[] outIdx,
            out Vector2[] uv2, out uint[] vertChartId, out int conflictCount)
        {
            uv2 = new Vector2[vertCount];
            vertChartId = new uint[vertCount];
            conflictCount = 0;

            for (int i = 0; i < vertCount; i++)
                vertChartId[i] = ORPHAN_CHART;

            var vertEntries = new List<ChartUv2Entry>[vertCount];

            for (int i = 0; i < outVertCount; i++)
            {
                uint orig = outXref[i];
                if (orig >= (uint)vertCount) continue;

                var entry = new ChartUv2Entry
                {
                    chartId = outChart[i],
                    uv = new Vector2(outUV[i * 2], outUV[i * 2 + 1]),
                    triCount = 0
                };

                if (vertEntries[orig] == null)
                    vertEntries[orig] = new List<ChartUv2Entry>(2);

                bool found = false;
                var list = vertEntries[orig];
                for (int j = 0; j < list.Count; j++)
                {
                    if (list[j].chartId == entry.chartId)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    list.Add(entry);
            }

            int outFaceCount = outIndexCount / 3;
            for (int f = 0; f < outFaceCount; f++)
            {
                uint chart = outChart[outIdx[f * 3]];
                IncrementChartTriCount(vertEntries, outXref[outIdx[f * 3 + 0]], chart);
                IncrementChartTriCount(vertEntries, outXref[outIdx[f * 3 + 1]], chart);
                IncrementChartTriCount(vertEntries, outXref[outIdx[f * 3 + 2]], chart);
            }

            for (int v = 0; v < vertCount; v++)
            {
                var list = vertEntries[v];
                if (list == null || list.Count == 0) continue;

                if (list.Count == 1)
                {
                    uv2[v] = list[0].uv;
                    vertChartId[v] = list[0].chartId;
                    continue;
                }

                conflictCount++;
                int bestIdx = 0;
                int bestCount = list[0].triCount;
                for (int j = 1; j < list.Count; j++)
                {
                    if (list[j].triCount > bestCount)
                    {
                        bestCount = list[j].triCount;
                        bestIdx = j;
                    }
                }
                uv2[v] = list[bestIdx].uv;
                vertChartId[v] = list[bestIdx].chartId;
            }
        }

        struct ChartUv2Entry
        {
            public uint chartId;
            public Vector2 uv;
            public int triCount;
        }

        static void IncrementChartTriCount(List<ChartUv2Entry>[] entries, uint origVert, uint chart)
        {
            if (origVert >= (uint)entries.Length) return;
            var list = entries[origVert];
            if (list == null) return;
            for (int j = 0; j < list.Count; j++)
            {
                if (list[j].chartId == chart)
                {
                    var e = list[j]; e.triCount++; list[j] = e;
                    return;
                }
            }
        }
    }
}
