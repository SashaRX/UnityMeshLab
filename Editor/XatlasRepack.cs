// XatlasRepack.cs — High-level xatlas repack with C#-side UV2 write-back
// Place in Assets/Editor/

using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public struct RepackOptions
    {
        public uint padding;        // inter-shell padding (pixels)
        public uint borderPadding;   // atlas edge padding (pixels), default 0
        public uint resolution;
        public float texelsPerUnit;
        public bool bilinear;
        public bool blockAlign;
        public bool bruteForce;

        public static RepackOptions Default => new RepackOptions
        {
            padding    = 4,
            borderPadding = 0,
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
        public int  flippedShells;
        public string error;
    }

    public static class XatlasRepack
    {
        const uint ORPHAN_CHART = uint.MaxValue;

        /// <summary>
        /// Flip UV0 shells with negative signed area (mirrored) so all charts
        /// have positive winding before xatlas packing.
        /// Modifies uv0 array in-place. Returns number of shells flipped.
        /// </summary>
        public static int NormalizeShellWinding(Vector2[] uv0, int[] tris, List<UvShell> shells)
        {
            int flipped = 0;
            foreach (var shell in shells)
            {
                // Compute signed area
                double area = 0;
                foreach (int f in shell.faceIndices)
                {
                    int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                    if (i0 >= uv0.Length || i1 >= uv0.Length || i2 >= uv0.Length) continue;
                    area += (uv0[i1].x - uv0[i0].x) * (uv0[i2].y - uv0[i0].y)
                          - (uv0[i2].x - uv0[i0].x) * (uv0[i1].y - uv0[i0].y);
                }
                if (area >= 0) continue; // positive winding — ok

                // Shell is mirrored → flip U around AABB center
                float centerU = (shell.boundsMin.x + shell.boundsMax.x) * 0.5f;
                float twoCenter = centerU * 2f;
                foreach (int vi in shell.vertexIndices)
                {
                    if (vi >= uv0.Length) continue;
                    uv0[vi] = new Vector2(twoCenter - uv0[vi].x, uv0[vi].y);
                }
                // Update shell bounds after flip
                float oldMinX = shell.boundsMin.x, oldMaxX = shell.boundsMax.x;
                shell.boundsMin = new Vector2(twoCenter - oldMaxX, shell.boundsMin.y);
                shell.boundsMax = new Vector2(twoCenter - oldMinX, shell.boundsMax.y);
                flipped++;
            }
            return flipped;
        }

        /// <summary>
        /// Pre-repack: apply tiny asymmetric UV0.x scale to overlap group members
        /// (except the first) so xatlas sees distinct chart shapes and avoids
        /// packing identical SymSplit halves at the same atlas position.
        /// Operates on the flat UV0 copy — does NOT modify the original mesh.
        /// </summary>
        internal static void PerturbOverlapShellsUv0(
            float[] uvFlat, List<UvShell> shells, List<List<int>> overlapGroups)
        {
            if (overlapGroups == null || overlapGroups.Count == 0)
                return;

            const float EPSILON_SCALE = 0.002f; // 0.2% per shell

            foreach (var group in overlapGroups)
            {
                if (group.Count < 2) continue;

                // Compute centroid U of first shell to use as scale pivot
                var firstShell = shells[group[0]];
                float pivotU = (firstShell.boundsMin.x + firstShell.boundsMax.x) * 0.5f;

                for (int g = 1; g < group.Count; g++)
                {
                    float scale = 1f + g * EPSILON_SCALE;
                    var shell = shells[group[g]];
                    foreach (int vi in shell.vertexIndices)
                    {
                        int idx = vi * 2;
                        if ((uint)idx + 1 < (uint)uvFlat.Length)
                        {
                            float u = uvFlat[idx];
                            uvFlat[idx] = pivotU + (u - pivotU) * scale;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Post-repack: detect overlapping UV2 bounding boxes among shells that
        /// shared UV0 space (overlap groups) and shift colliding shells apart.
        /// If any UV2 exceeds [0,1] after shifts, rescales all UV2 to fit.
        /// Returns number of shells shifted.
        /// </summary>
        internal static int FixOverlappingUv2Shells(
            Vector2[] uv2, List<UvShell> shells, List<List<int>> overlapGroups,
            uint padding, uint atlasWidth, uint atlasHeight,
            bool skipRescale = false)
        {
            if (overlapGroups == null || overlapGroups.Count == 0)
                return 0;

            float padU = atlasWidth  > 0 ? (float)padding / atlasWidth  : 0f;
            float padV = atlasHeight > 0 ? (float)padding / atlasHeight : 0f;
            int shifted = 0;

            foreach (var group in overlapGroups)
            {
                if (group.Count < 2) continue;

                int gc = group.Count;
                var mn = new Vector2[gc];
                var mx = new Vector2[gc];
                for (int i = 0; i < gc; i++)
                {
                    mn[i] = new Vector2(float.MaxValue, float.MaxValue);
                    mx[i] = new Vector2(float.MinValue, float.MinValue);
                    var shell = shells[group[i]];
                    foreach (int vi in shell.vertexIndices)
                    {
                        if ((uint)vi < (uint)uv2.Length)
                        {
                            mn[i] = Vector2.Min(mn[i], uv2[vi]);
                            mx[i] = Vector2.Max(mx[i], uv2[vi]);
                        }
                    }
                }

                for (int i = 0; i < gc; i++)
                {
                    for (int j = i + 1; j < gc; j++)
                    {
                        float oMinX = Mathf.Max(mn[i].x, mn[j].x);
                        float oMinY = Mathf.Max(mn[i].y, mn[j].y);
                        float oMaxX = Mathf.Min(mx[i].x, mx[j].x);
                        float oMaxY = Mathf.Min(mx[i].y, mx[j].y);
                        if (oMaxX <= oMinX || oMaxY <= oMinY) continue;

                        float overlapArea = (oMaxX - oMinX) * (oMaxY - oMinY);
                        float areaI = (mx[i].x - mn[i].x) * (mx[i].y - mn[i].y);
                        float areaJ = (mx[j].x - mn[j].x) * (mx[j].y - mn[j].y);
                        float smaller = Mathf.Min(areaI, areaJ);
                        if (smaller <= 0f || overlapArea / smaller < 0.01f) continue;

                        float overlapRatio = overlapArea / smaller;

                        // Choose shift axis: prefer the direction with less displacement
                        float shiftU = (mx[i].x - mn[j].x) + padU;
                        float shiftV = (mx[i].y - mn[j].y) + padV;
                        if (shiftU <= 0f) shiftU = padU;
                        if (shiftV <= 0f) shiftV = padV;

                        var shell = shells[group[j]];
                        string axisName;
                        float shiftMag;
                        if (shiftU <= shiftV)
                        {
                            foreach (int vi in shell.vertexIndices)
                                if ((uint)vi < (uint)uv2.Length)
                                    uv2[vi] = new Vector2(uv2[vi].x + shiftU, uv2[vi].y);
                            mn[j] = new Vector2(mn[j].x + shiftU, mn[j].y);
                            mx[j] = new Vector2(mx[j].x + shiftU, mx[j].y);
                            axisName = "U";
                            shiftMag = shiftU;
                        }
                        else
                        {
                            foreach (int vi in shell.vertexIndices)
                                if ((uint)vi < (uint)uv2.Length)
                                    uv2[vi] = new Vector2(uv2[vi].x, uv2[vi].y + shiftV);
                            mn[j] = new Vector2(mn[j].x, mn[j].y + shiftV);
                            mx[j] = new Vector2(mx[j].x, mx[j].y + shiftV);
                            axisName = "V";
                            shiftMag = shiftV;
                        }
                        UvtLog.Verbose($"[xatlas] Overlap fix: shell {group[i]}↔{group[j]} " +
                            $"ratio={overlapRatio:F3} shift={axisName}+{shiftMag:F4}");
                        shifted++;
                    }
                }
            }

            if (shifted > 0 && !skipRescale)
                RescaleUv2ToUnit(uv2);

            if (shifted > 0)
                UvtLog.Info($"[xatlas] Post-repack: fixed {shifted} overlapping UV2 shell(s)");

            return shifted;
        }

        /// <summary>
        /// Post-repack safety net: find shell pairs with nearly identical UV2 centroids
        /// (true SymSplit duplicates packed at the same position) and fix their overlap.
        /// Unlike the old global pass that checked ALL N² pairs (causing false positives
        /// on dense atlases), this only checks pairs within centroid proximity threshold.
        /// </summary>
        internal static int FixNearDuplicateUv2Shells(
            Vector2[] uv2, List<UvShell> shells,
            uint padding, uint atlasWidth, uint atlasHeight,
            bool skipRescale = false)
        {
            if (shells.Count < 2) return 0;

            float atlasDim = Mathf.Max(atlasWidth, atlasHeight);
            if (atlasDim <= 0f) return 0;

            // Centroid proximity threshold: 4 pixels in UV space.
            // True SymSplit duplicates are packed at essentially identical positions.
            float centroidThreshold = 4f / atlasDim;
            float centroidThresholdSq = centroidThreshold * centroidThreshold;

            // Compute UV2 centroid for each shell
            int sc = shells.Count;
            var centroids = new Vector2[sc];
            for (int i = 0; i < sc; i++)
            {
                Vector2 sum = Vector2.zero;
                int cnt = 0;
                foreach (int vi in shells[i].vertexIndices)
                {
                    if ((uint)vi < (uint)uv2.Length)
                    {
                        sum += uv2[vi];
                        cnt++;
                    }
                }
                centroids[i] = cnt > 0 ? sum / cnt : Vector2.zero;
            }

            // Build overlap groups using union-find so transitive chains
            // (A near B, B near C) are merged into one group.
            var parent = new int[sc];
            for (int i = 0; i < sc; i++) parent[i] = i;

            int FindRoot(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }

            for (int i = 0; i < sc; i++)
            for (int j = i + 1; j < sc; j++)
            {
                float dx = centroids[i].x - centroids[j].x;
                float dy = centroids[i].y - centroids[j].y;
                if (dx * dx + dy * dy < centroidThresholdSq)
                {
                    int ri = FindRoot(i), rj = FindRoot(j);
                    if (ri != rj) parent[ri] = rj;
                }
            }

            // Collect groups with more than one member
            var groupMap = new Dictionary<int, List<int>>();
            for (int i = 0; i < sc; i++)
            {
                int root = FindRoot(i);
                if (!groupMap.TryGetValue(root, out var g))
                {
                    g = new List<int>();
                    groupMap[root] = g;
                }
                g.Add(i);
            }

            var nearPairs = new List<List<int>>();
            foreach (var g in groupMap.Values)
                if (g.Count > 1)
                    nearPairs.Add(g);

            if (nearPairs.Count == 0) return 0;

            return FixOverlappingUv2Shells(uv2, shells, nearPairs,
                padding, atlasWidth, atlasHeight, skipRescale);
        }

        /// <summary>
        /// If any UV2 coordinate exceeds [0,1], uniformly rescale all UV2 to fit.
        /// </summary>
        static void RescaleUv2ToUnit(Vector2[] uv2)
        {
            float maxU = 0f, maxV = 0f;
            for (int i = 0; i < uv2.Length; i++)
            {
                if (uv2[i].x > maxU) maxU = uv2[i].x;
                if (uv2[i].y > maxV) maxV = uv2[i].y;
            }

            if (maxU > 1f || maxV > 1f)
            {
                float scale = 1f / Mathf.Max(maxU, maxV);
                UvtLog.Verbose($"[xatlas] Rescale UV2 to unit: maxU={maxU:F4} maxV={maxV:F4} scale={scale:F4}");
                for (int i = 0; i < uv2.Length; i++)
                    uv2[i] *= scale;
            }
        }

        /// <summary>
        /// Phase 2 overlap fix: relocate overlapping UV2 shells to free atlas space.
        /// Uses an occupancy grid to find unoccupied rectangles for displaced shells.
        /// Falls back to axis-shift if no free space is found.
        /// Returns number of shells relocated.
        /// </summary>
        internal static int RelocateToFreeSpace(
            Vector2[] uv2, List<UvShell> shells,
            uint padding, uint atlasWidth, uint atlasHeight)
        {
            if (shells.Count < 2) return 0;

            float padU = atlasWidth  > 0 ? (float)padding / atlasWidth  : 0f;
            float padV = atlasHeight > 0 ? (float)padding / atlasHeight : 0f;

            // Compute UV2 AABB per shell
            int n = shells.Count;
            var mn = new Vector2[n];
            var mx = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                mn[i] = new Vector2(float.MaxValue, float.MaxValue);
                mx[i] = new Vector2(float.MinValue, float.MinValue);
                foreach (int vi in shells[i].vertexIndices)
                {
                    if ((uint)vi < (uint)uv2.Length)
                    {
                        mn[i] = Vector2.Min(mn[i], uv2[vi]);
                        mx[i] = Vector2.Max(mx[i], uv2[vi]);
                    }
                }
            }

            // Detect overlapping pairs
            var overlapping = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    float oMinX = Mathf.Max(mn[i].x, mn[j].x);
                    float oMinY = Mathf.Max(mn[i].y, mn[j].y);
                    float oMaxX = Mathf.Min(mx[i].x, mx[j].x);
                    float oMaxY = Mathf.Min(mx[i].y, mx[j].y);
                    if (oMaxX <= oMinX || oMaxY <= oMinY) continue;

                    float overlapArea = (oMaxX - oMinX) * (oMaxY - oMinY);
                    float areaI = (mx[i].x - mn[i].x) * (mx[i].y - mn[i].y);
                    float areaJ = (mx[j].x - mn[j].x) * (mx[j].y - mn[j].y);
                    float smaller = Mathf.Min(areaI, areaJ);
                    if (smaller <= 0f || overlapArea / smaller < 0.01f) continue;

                    overlapping.Add(i);
                    overlapping.Add(j);
                }
            }

            if (overlapping.Count == 0) return 0;

            // Build occupancy grid from non-overlapping shells
            const int kGridRes = 128;
            int[,] grid = new int[kGridRes, kGridRes]; // 0 = free, 1 = occupied

            for (int i = 0; i < n; i++)
            {
                if (overlapping.Contains(i)) continue;
                int gMinX = Mathf.Clamp(Mathf.FloorToInt(mn[i].x * kGridRes), 0, kGridRes - 1);
                int gMinY = Mathf.Clamp(Mathf.FloorToInt(mn[i].y * kGridRes), 0, kGridRes - 1);
                int gMaxX = Mathf.Clamp(Mathf.CeilToInt(mx[i].x * kGridRes), 0, kGridRes - 1);
                int gMaxY = Mathf.Clamp(Mathf.CeilToInt(mx[i].y * kGridRes), 0, kGridRes - 1);
                for (int gy = gMinY; gy <= gMaxY; gy++)
                    for (int gx = gMinX; gx <= gMaxX; gx++)
                        grid[gx, gy] = 1;
            }

            // Build summed area table for O(1) rectangle occupancy queries.
            // sat[x,y] = sum of grid[0..x-1, 0..y-1].
            int[,] sat = new int[kGridRes + 1, kGridRes + 1];
            for (int y = 0; y < kGridRes; y++)
                for (int x = 0; x < kGridRes; x++)
                    sat[x + 1, y + 1] = grid[x, y] + sat[x, y + 1] + sat[x + 1, y] - sat[x, y];
            // Sort overlapping shells by area (largest first) for better packing
            var toRelocate = new List<int>(overlapping);
            toRelocate.Sort((a, b) =>
            {
                float areaA = (mx[a].x - mn[a].x) * (mx[a].y - mn[a].y);
                float areaB = (mx[b].x - mn[b].x) * (mx[b].y - mn[b].y);
                return areaB.CompareTo(areaA);
            });

            int relocated = 0;
            foreach (int si in toRelocate)
            {
                float w = mx[si].x - mn[si].x + padU * 2f;
                float h = mx[si].y - mn[si].y + padV * 2f;
                int gw = Mathf.Max(1, Mathf.CeilToInt(w * kGridRes));
                int gh = Mathf.Max(1, Mathf.CeilToInt(h * kGridRes));

                // Scan for free rectangle using summed area table (O(1) per query)
                bool placed = false;
                for (int gy = 0; gy <= kGridRes - gh && !placed; gy++)
                {
                    for (int gx = 0; gx <= kGridRes - gw && !placed; gx++)
                    {
                        int sum = sat[gx + gw, gy + gh] - sat[gx, gy + gh]
                                - sat[gx + gw, gy]      + sat[gx, gy];
                        if (sum != 0) continue;

                        // Place shell here
                        float newMinX = (float)gx / kGridRes + padU;
                        float newMinY = (float)gy / kGridRes + padV;
                        float offX = newMinX - mn[si].x;
                        float offY = newMinY - mn[si].y;

                        foreach (int vi in shells[si].vertexIndices)
                            if ((uint)vi < (uint)uv2.Length)
                                uv2[vi] = new Vector2(uv2[vi].x + offX, uv2[vi].y + offY);

                        // Mark occupied in grid and rebuild SAT incrementally
                        for (int dy = 0; dy < gh; dy++)
                            for (int dx = 0; dx < gw; dx++)
                                grid[gx + dx, gy + dy] = 1;
                        for (int y = gy; y < kGridRes; y++)
                            for (int x = gx; x < kGridRes; x++)
                                sat[x + 1, y + 1] = grid[x, y] + sat[x, y + 1] + sat[x + 1, y] - sat[x, y];

                        mn[si] = new Vector2(mn[si].x + offX, mn[si].y + offY);
                        mx[si] = new Vector2(mx[si].x + offX, mx[si].y + offY);

                        UvtLog.Verbose($"[xatlas] Free-space relocate: shell {si} → " +
                            $"({newMinX:F3},{newMinY:F3}) offset=({offX:F4},{offY:F4})");
                        placed = true;
                        relocated++;
                    }
                }

                if (!placed)
                {
                    UvtLog.Verbose($"[xatlas] Free-space fallback: shell {si} — no free space, " +
                        $"using axis shift");
                    // Mark this shell's current position as occupied anyway
                    int fgMinX = Mathf.Clamp(Mathf.FloorToInt(mn[si].x * kGridRes), 0, kGridRes - 1);
                    int fgMinY = Mathf.Clamp(Mathf.FloorToInt(mn[si].y * kGridRes), 0, kGridRes - 1);
                    int fgMaxX = Mathf.Clamp(Mathf.CeilToInt(mx[si].x * kGridRes), 0, kGridRes - 1);
                    int fgMaxY = Mathf.Clamp(Mathf.CeilToInt(mx[si].y * kGridRes), 0, kGridRes - 1);
                    for (int fy = fgMinY; fy <= fgMaxY; fy++)
                        for (int fx = fgMinX; fx <= fgMaxX; fx++)
                            grid[fx, fy] = 1;
                    for (int y = fgMinY; y < kGridRes; y++)
                        for (int x = fgMinX; x < kGridRes; x++)
                            sat[x + 1, y + 1] = grid[x, y] + sat[x, y + 1] + sat[x + 1, y] - sat[x, y];
                }
            }

            if (relocated > 0)
            {
                RescaleUv2ToUnit(uv2);
                UvtLog.Info($"[xatlas] Free-space relocator: placed {relocated}/{toRelocate.Count} overlapping shells");
            }

            return relocated;
        }

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
            tmp.GetUVs(1, uvOut);
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
            int overlapPairCount = UvShellExtractor.CountAabbOverlaps(shells);
            UvtLog.Verbose($"[xatlas] Pre-repack: {shells.Count} shells, " +
                $"{overlapGroups.Count} overlap groups, {overlapPairCount} overlapping pairs");

            // UV0 winding normalized by ExecWeldUv0.
            result.flippedShells = 0;

            // ── Flatten UV0 ──
            float[] uvFlat = new float[vertCount * 2];
            for (int i = 0; i < vertCount; i++)
            {
                uvFlat[i * 2]     = uv0[i].x;
                uvFlat[i * 2 + 1] = uv0[i].y;
            }

            // ── Perturb overlapping shells to break xatlas packing symmetry ──
            PerturbOverlapShellsUv0(uvFlat, shells, overlapGroups);

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

                // ── C#-side UV2 assignment ──
                Vector2[] uv2;
                uint[] vertChartId;
                int conflicts;
                AssignUv2(vertCount, faceCount, tris,
                          outVertCount, outXref, outUV, outChart,
                          outIndexCount, outIdx,
                          out uv2, out vertChartId, out conflicts);

                result.conflictVertices = conflicts;

                // ── Post-process: fix overlapping UV2 shells ──
                // Phase 1: known UV0 overlap groups (fast path, catches SymSplit halves in same group)
                FixOverlappingUv2Shells(uv2, shells, overlapGroups,
                    opts.padding, result.atlasWidth, result.atlasHeight, skipRescale: true);

                // Phase 2: centroid-proximity safety net — find shells packed at
                // nearly identical UV2 positions (true SymSplit near-duplicates).
                // Only checks pairs within 4px centroid distance, avoiding the
                // false positives of the old global N² pass on dense atlases.
                FixNearDuplicateUv2Shells(uv2, shells,
                    opts.padding, result.atlasWidth, result.atlasHeight);

                // Phase 3: free-space relocator for any remaining overlaps.
                if (shells.Count > 1)
                    RelocateToFreeSpace(uv2, shells,
                        opts.padding, result.atlasWidth, result.atlasHeight);

                // ── Post-process: fix orphan vertices ──
                int orphanVerts, orphanTris, snapped;
                FixOrphanVertices(uv2, tris, vertChartId, out orphanVerts, out orphanTris, out snapped);
                result.orphanVertices = orphanVerts;
                result.orphanTriangles = orphanTris;
                result.snappedVertices = snapped;

                // ── Diagnostic: top longest UV2 edges (after fix) ──
                DiagnoseLongestEdges(uv2, tris, faceShellIds, vertChartId, 10);

                // ── Border padding inset ──
                if (opts.borderPadding > 0 && result.atlasWidth > 0)
                    ApplyBorderInset(uv2, opts.borderPadding, result.atlasWidth, result.atlasHeight);

                // ── Apply UV2 (channel 1 — Unity lightmap channel, mesh.uv2) ──
                mesh.SetUVs(1, uv2);
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

                UvtLog.Verbose($"[xatlas] '{mesh.name}': atlas={result.atlasWidth}x{result.atlasHeight}, " +
                          $"charts={result.chartCount}, conflicts={conflicts}, orphans={orphanVerts}");
            }
            finally
            {
                XatlasNative.xatlasDestroy();
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────
        // Repack multiple meshes into a single shared atlas.
        // All meshes are added to one xatlas session so their UV2 islands
        // are packed together without overlapping.
        // ─────────────────────────────────────────────────────────────────
        public static RepackResult[] RepackMulti(Mesh[] meshes, RepackOptions opts)
        {
            int meshCount = meshes.Length;
            var results = new RepackResult[meshCount];

            // ── Per-mesh pre-processing data ──
            var allUv0        = new Vector2[meshCount][];
            var allTris       = new int[meshCount][];
            var allShells     = new List<UvShell>[meshCount];
            var allOverlap    = new List<List<int>>[meshCount];
            var allFaceShells = new uint[meshCount][];

            // Validate all meshes up-front
            for (int m = 0; m < meshCount; m++)
            {
                var mesh = meshes[m];
                allUv0[m] = mesh.uv;
                if (allUv0[m] == null || allUv0[m].Length == 0)
                {
                    results[m].error = "Mesh has no UV0";
                    return results;
                }
                allTris[m] = mesh.triangles;
                List<UvShell> shells;
                List<List<int>> overlapGroups;
                allFaceShells[m] = UvShellExtractor.BuildPerFaceShellIds(
                    allUv0[m], allTris[m], out shells, out overlapGroups);
                allShells[m]  = shells;
                allOverlap[m] = overlapGroups;

                results[m].shellCount        = shells.Count;
                results[m].overlapGroupCount  = overlapGroups.Count;
                int overlapPairs = UvShellExtractor.CountAabbOverlaps(shells);
                UvtLog.Verbose($"[xatlas] Pre-repack mesh {m}: {shells.Count} shells, " +
                    $"{overlapGroups.Count} overlap groups, {overlapPairs} overlapping pairs");

            }

            // UV0 winding normalized by ExecWeldUv0.
            for (int m = 0; m < meshCount; m++)
                results[m].flippedShells = 0;

            // ── Single xatlas session for all meshes ──
            XatlasNative.xatlasCreate();
            try
            {
                // Add all meshes
                for (int m = 0; m < meshCount; m++)
                {
                    var mesh = meshes[m];
                    int vertCount = mesh.vertexCount;
                    int faceCount = allTris[m].Length / 3;

                    float[] uvFlat = new float[vertCount * 2];
                    for (int i = 0; i < vertCount; i++)
                    {
                        uvFlat[i * 2]     = allUv0[m][i].x;
                        uvFlat[i * 2 + 1] = allUv0[m][i].y;
                    }

                    // Perturb overlapping shells to break xatlas packing symmetry
                    PerturbOverlapShellsUv0(uvFlat, allShells[m], allOverlap[m]);

                    uint[] indices = new uint[allTris[m].Length];
                    for (int i = 0; i < allTris[m].Length; i++)
                        indices[i] = (uint)allTris[m][i];

                    int addErr = XatlasNative.xatlasAddUvMesh(
                        uvFlat, (uint)vertCount,
                        indices, (uint)indices.Length,
                        allFaceShells[m], (uint)faceCount);

                    if (addErr != 0)
                    {
                        results[m].error = $"xatlasAddUvMesh error {addErr}";
                        return results;
                    }
                }

                // Pack all charts together into one atlas
                XatlasNative.xatlasComputeCharts();
                XatlasNative.xatlasPackCharts(
                    0, opts.padding, opts.texelsPerUnit, opts.resolution,
                    opts.bilinear  ? 1 : 0,
                    opts.blockAlign ? 1 : 0,
                    opts.bruteForce ? 1 : 0);

                int outMeshCount = XatlasNative.xatlasGetMeshCount();
                if (outMeshCount == 0)
                {
                    for (int m = 0; m < meshCount; m++)
                        results[m].error = "xatlas returned 0 meshes";
                    return results;
                }

                uint atlasW = XatlasNative.xatlasGetAtlasWidth();
                uint atlasH = XatlasNative.xatlasGetAtlasHeight();
                uint totalCharts = XatlasNative.xatlasGetChartCount();

                    UvtLog.Info($"[xatlas] Joint atlas: {atlasW}x{atlasH}, total_charts={totalCharts}, meshes={outMeshCount}");

                // ── Per-mesh output extraction ──
                var allUv2 = new Vector2[meshCount][];
                int totalShifted = 0;

                for (int m = 0; m < meshCount; m++)
                {
                    var mesh = meshes[m];
                    int vertCount = mesh.vertexCount;
                    int faceCount = allTris[m].Length / 3;

                    results[m].atlasWidth  = atlasW;
                    results[m].atlasHeight = atlasH;

                    int outVertCount  = XatlasNative.xatlasGetOutputVertexCount(m);
                    int outIndexCount = XatlasNative.xatlasGetOutputIndexCount(m);

                    if (outVertCount == 0 || outIndexCount == 0)
                    {
                        results[m].error = $"xatlas output empty for mesh {m}: verts={outVertCount}, idx={outIndexCount}";
                        continue;
                    }

                    uint[]  outXref  = new uint[outVertCount];
                    float[] outUV    = new float[outVertCount * 2];
                    uint[]  outChart = new uint[outVertCount];
                    uint[]  outIdx   = new uint[outIndexCount];

                    XatlasNative.xatlasGetOutputVertexData(m, outXref, outUV, outChart, outVertCount);
                    XatlasNative.xatlasGetOutputIndices(m, outIdx, outIndexCount);

                    results[m].chartCount = (uint)outVertCount; // per-mesh chart count approximation

                    // Assign UV2
                    Vector2[] uv2;
                    uint[] vertChartId;
                    int conflicts;
                    AssignUv2(vertCount, faceCount, allTris[m],
                              outVertCount, outXref, outUV, outChart,
                              outIndexCount, outIdx,
                              out uv2, out vertChartId, out conflicts);
                    results[m].conflictVertices = conflicts;

                    // Fix overlapping UV2 shells (skip per-mesh rescale — do global rescale below)
                    totalShifted += FixOverlappingUv2Shells(uv2, allShells[m], allOverlap[m],
                        opts.padding, atlasW, atlasH, skipRescale: true);

                    // Centroid-proximity safety net for near-duplicate SymSplit shells
                    totalShifted += FixNearDuplicateUv2Shells(uv2, allShells[m],
                        opts.padding, atlasW, atlasH, skipRescale: true);

                    // Free-space relocator for any remaining overlaps
                    if (allShells[m].Count > 1)
                        totalShifted += RelocateToFreeSpace(uv2, allShells[m],
                            opts.padding, atlasW, atlasH);

                    // Fix orphan vertices
                    int orphanVerts, orphanTris, snapped;
                    FixOrphanVertices(uv2, allTris[m], vertChartId, out orphanVerts, out orphanTris, out snapped);
                    results[m].orphanVertices  = orphanVerts;
                    results[m].orphanTriangles = orphanTris;
                    results[m].snappedVertices = snapped;

                    allUv2[m] = uv2;
                    results[m].ok = true;
                }

                // Global rescale across all meshes to maintain cross-mesh UV2 consistency
                if (totalShifted > 0)
                {
                    float maxU = 0f, maxV = 0f;
                    for (int m = 0; m < meshCount; m++)
                    {
                        if (allUv2[m] == null) continue;
                        for (int i = 0; i < allUv2[m].Length; i++)
                        {
                            if (allUv2[m][i].x > maxU) maxU = allUv2[m][i].x;
                            if (allUv2[m][i].y > maxV) maxV = allUv2[m][i].y;
                        }
                    }
                    if (maxU > 1f || maxV > 1f)
                    {
                        float scale = 1f / Mathf.Max(maxU, maxV);
                        for (int m = 0; m < meshCount; m++)
                        {
                            if (allUv2[m] == null) continue;
                            for (int i = 0; i < allUv2[m].Length; i++)
                                allUv2[m][i] *= scale;
                        }
                    }
                }

                // Apply UV2 and border padding
                for (int m = 0; m < meshCount; m++)
                {
                    if (allUv2[m] == null || !results[m].ok) continue;

                    if (opts.borderPadding > 0 && atlasW > 0)
                        ApplyBorderInset(allUv2[m], opts.borderPadding, atlasW, atlasH);

                    meshes[m].SetUVs(1, allUv2[m]);
                }
            }
            finally
            {
                XatlasNative.xatlasDestroy();
            }

            return results;
        }

        // ─────────────────────────────────────────────────────────────────
        // Apply border inset: shrink all UV2 toward center to leave
        // borderPadding pixels of margin at atlas edges.
        // uv2 = uv2 * (1 - 2*inset) + inset
        // ─────────────────────────────────────────────────────────────────
        public static void ApplyBorderInset(Mesh mesh, int borderPaddingPx, uint atlasSize)
        {
            if (borderPaddingPx <= 0 || atlasSize == 0) return;

            var uv2List = new List<Vector2>();
            mesh.GetUVs(1, uv2List);
            if (uv2List.Count == 0) return;

            float inset = (float)borderPaddingPx / atlasSize;
            float scale = 1f - 2f * inset;

            if (scale <= 0f)
            {
                UvtLog.Warn($"[xatlas] Border padding {borderPaddingPx}px too large " +
                                 $"for atlas {atlasSize}px — skipping inset.");
                return;
            }

            var uv2 = uv2List.ToArray();
            for (int i = 0; i < uv2.Length; i++)
                uv2[i] = uv2[i] * scale + new Vector2(inset, inset);

            mesh.SetUVs(1, uv2);
        }

        public static Vector2[] ApplyBorderInset(Vector2[] uv2, int borderPaddingPx, uint atlasSize)
        {
            if (borderPaddingPx <= 0 || atlasSize == 0 || uv2 == null) return uv2;

            float inset = (float)borderPaddingPx / atlasSize;
            float scale = 1f - 2f * inset;
            if (scale <= 0f) return uv2;

            var result = new Vector2[uv2.Length];
            for (int i = 0; i < uv2.Length; i++)
                result[i] = uv2[i] * scale + new Vector2(inset, inset);
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
                return;

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
                    continue;

                var targets = kv.Value;
                Vector2 avg = Vector2.zero;
                for (int i = 0; i < targets.Count; i++)
                    avg += targets[i];
                avg /= targets.Count;

                uv2[v] = avg;
                snappedCount++;
            }

            UvtLog.Verbose($"[xatlas] Post-process: snapped {snappedCount}/{orphanVertCount} orphan vertices");
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
        // Border padding inset: shrink UV layout away from atlas edges
        // uv = uv * (1 - 2*inset) + inset  where inset = borderPx / atlasSize
        // ─────────────────────────────────────────────────────────────────
        static void ApplyBorderInset(Vector2[] uv2, uint borderPx, uint atlasW, uint atlasH)
        {
            float insetX = (float)borderPx / atlasW;
            float insetY = (float)borderPx / atlasH;
            float scaleX = 1f - 2f * insetX;
            float scaleY = 1f - 2f * insetY;

            if (scaleX <= 0f || scaleY <= 0f)
            {
                UvtLog.Warn($"[xatlas] Border padding {borderPx}px too large for atlas {atlasW}x{atlasH}");
                return;
            }

            for (int i = 0; i < uv2.Length; i++)
            {
                uv2[i] = new Vector2(
                    uv2[i].x * scaleX + insetX,
                    uv2[i].y * scaleY + insetY);
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
