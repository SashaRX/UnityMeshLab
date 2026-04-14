// SymmetrySplitShells.cs — Split UV0 shells with symmetry overlap
// Detects mirrored geometry sharing the same UV0 space, duplicates boundary
// vertices to break topological connection so xatlas repacks them as separate charts.
// Place in Assets/Editor/

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class SymmetrySplitShells
    {
        const float UV_NEAR = 0.01f;   // UV0 centroid distance threshold
        const float POS_FAR = 0.5f;    // 3D centroid distance threshold
        const float GRID_CELL = 0.01f; // spatial hash cell for UV0 centroids

        /// <summary>
        /// Parameters describing how a shell was split. Stored so that
        /// the same split can be applied to other LOD levels.
        /// </summary>
        public struct SplitParams
        {
            public int foldCount;        // N (2 for binary mirror, >2 for rotational)
            public int axis;             // split axis for binary, rotation axis for N-fold
            public float splitThreshold; // for binary: value along axis
            public Vector3 center;       // rotation center for N-fold
        }

        struct SplitInfo
        {
            public int shellIndex;
            public int axis; // 0=X, 1=Y, 2=Z
            public float splitThreshold; // 3D position along axis to split at
        }

        /// <summary>
        /// Detect and split shells with symmetry overlap.
        /// Modifies mesh in-place, adds new shells to the list.
        /// Returns number of shells split.
        /// </summary>
        public static int Split(Mesh mesh, List<UvShell> shells)
        {
            var verts = mesh.vertices;
            var uv0 = mesh.uv;
            var tris = mesh.triangles;

            if (uv0 == null || uv0.Length == 0 || tris.Length == 0)
                return 0;

            int faceCount = tris.Length / 3;

            // ── Precompute per-face centroids ──
            var uv0C = new Vector2[faceCount];
            var posC = new Vector3[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                uv0C[f] = (uv0[v0] + uv0[v1] + uv0[v2]) / 3f;
                posC[f] = (verts[v0] + verts[v1] + verts[v2]) / 3f;
            }

            // ══════════════ Phase 1: Detection ══════════════

            var splits = new List<SplitInfo>();

            for (int si = 0; si < shells.Count; si++)
            {
                var shell = shells[si];
                var faces = shell.faceIndices;
                if (faces.Count < 2) continue;

                // Build UV0 centroid spatial hash for this shell
                var grid = new Dictionary<long, List<int>>();
                foreach (int f in faces)
                {
                    long key = UvGridKey(uv0C[f]);
                    if (!grid.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<int>();
                        grid[key] = bucket;
                    }
                    bucket.Add(f);
                }

                // Find symmetry pairs via grid neighbor search
                int[] axisVotes = new int[3];
                float[] axisMidpointSum = new float[3]; // sum of pair midpoints along each axis
                bool found = false;

                foreach (int f in faces)
                {
                    var c = uv0C[f];
                    int cx = Mathf.FloorToInt(c.x / GRID_CELL);
                    int cy = Mathf.FloorToInt(c.y / GRID_CELL);

                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        long nk = GridKey(cx + dx, cy + dy);
                        if (!grid.TryGetValue(nk, out var bucket)) continue;

                        foreach (int g in bucket)
                        {
                            if (g <= f) continue; // avoid duplicate pairs + self
                            float uvDist = Vector2.Distance(uv0C[f], uv0C[g]);
                            if (uvDist >= UV_NEAR) continue;
                            float posDist = Vector3.Distance(posC[f], posC[g]);
                            if (posDist <= POS_FAR) continue;

                            // Symmetry pair — vote on split axis
                            // Use separation direction, not midpoint position.
                            // The axis of maximum separation between a mirrored pair
                            // is the correct split axis. The old midpoint heuristic
                            // fails on flat models (e.g. Carousel in XZ plane) where
                            // Y midpoint ≈ 0 for all pairs, incorrectly picking Y.
                            Vector3 sep = posC[f] - posC[g];
                            float sx = Mathf.Abs(sep.x);
                            float sy = Mathf.Abs(sep.y);
                            float sz = Mathf.Abs(sep.z);

                            Vector3 mid = (posC[f] + posC[g]) * 0.5f;
                            if (sx >= sy && sx >= sz)
                            {
                                axisVotes[0]++;
                                axisMidpointSum[0] += mid.x;
                            }
                            else if (sy >= sz)
                            {
                                axisVotes[1]++;
                                axisMidpointSum[1] += mid.y;
                            }
                            else
                            {
                                axisVotes[2]++;
                                axisMidpointSum[2] += mid.z;
                            }
                            found = true;
                        }
                    }
                }

                if (!found) continue;

                int bestAxis = 0;
                if (axisVotes[1] > axisVotes[bestAxis]) bestAxis = 1;
                if (axisVotes[2] > axisVotes[bestAxis]) bestAxis = 2;

                float threshold = axisVotes[bestAxis] > 0
                    ? axisMidpointSum[bestAxis] / axisVotes[bestAxis]
                    : 0f;
                splits.Add(new SplitInfo { shellIndex = si, axis = bestAxis, splitThreshold = threshold });
                UvtLog.Verbose($"[SymSplit] Shell {si}: symmetry on {AxisName(bestAxis)} " +
                    $"(threshold={threshold:F3}, {axisVotes[0]}x/{axisVotes[1]}y/{axisVotes[2]}z votes, {faces.Count} faces)");
            }

            if (splits.Count == 0) return 0;

            // ══════════════ Phase 2: Split classification ══════════════

            // Collect all boundary vertex duplications needed
            int origVertCount = verts.Length;
            int newVertOffset = 0;

            // Per split: groupA faces, groupB faces, boundary remap
            var splitData = new List<(
                SplitInfo info,
                List<int> groupA,
                List<int> groupB,
                Dictionary<int, int> boundaryRemap)>();

            foreach (var sp in splits)
            {
                var shell = shells[sp.shellIndex];
                var groupA = new List<int>();
                var groupB = new List<int>();

                foreach (int f in shell.faceIndices)
                {
                    float val = posC[f][sp.axis];
                    if (val >= sp.splitThreshold)
                        groupA.Add(f);
                    else
                        groupB.Add(f);
                }

                // Skip if one group is empty — no actual split
                if (groupA.Count == 0 || groupB.Count == 0)
                {
                    UvtLog.Verbose($"[SymSplit] Shell {sp.shellIndex}: skip (all faces on one side)");
                    continue;
                }

                // Find vertices used by each group
                var vertsA = new HashSet<int>();
                var vertsB = new HashSet<int>();
                foreach (int f in groupA)
                    for (int j = 0; j < 3; j++)
                        vertsA.Add(tris[f * 3 + j]);
                foreach (int f in groupB)
                    for (int j = 0; j < 3; j++)
                        vertsB.Add(tris[f * 3 + j]);

                // Boundary = intersection
                var boundary = new HashSet<int>(vertsA);
                boundary.IntersectWith(vertsB);

                if (boundary.Count == 0)
                {
                    UvtLog.Verbose($"[SymSplit] Shell {sp.shellIndex}: no boundary vertices (already separate)");
                    continue;
                }

                // Assign new indices for boundary vertices (for group B)
                var remap = new Dictionary<int, int>();
                foreach (int bv in boundary)
                {
                    remap[bv] = origVertCount + newVertOffset;
                    newVertOffset++;
                }

                splitData.Add((sp, groupA, groupB, remap));
                UvtLog.Verbose($"[SymSplit] Shell {sp.shellIndex}: A={groupA.Count} B={groupB.Count} boundary={boundary.Count}");
            }

            if (splitData.Count == 0) return 0;

            // ══════════════ Phase 3: Apply ══════════════

            int totalNewVerts = newVertOffset;
            int newVertCount = origVertCount + totalNewVerts;

            // Read all vertex attributes
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var colors = mesh.colors;
            var boneWeights = mesh.boneWeights;

            bool hasNormals = normals != null && normals.Length == origVertCount;
            bool hasTangents = tangents != null && tangents.Length == origVertCount;
            bool hasColors = colors != null && colors.Length == origVertCount;
            bool hasBW = boneWeights != null && boneWeights.Length == origVertCount;

            // Read all UV channels (0-7)
            var uvLists = new List<Vector4>[8];
            var hasUv = new bool[8];
            for (int ch = 0; ch < 8; ch++)
            {
                uvLists[ch] = new List<Vector4>();
                mesh.GetUVs(ch, uvLists[ch]);
                hasUv[ch] = uvLists[ch].Count == origVertCount;
            }

            // Expand arrays
            var newVerts = new Vector3[newVertCount];
            System.Array.Copy(verts, newVerts, origVertCount);

            Vector3[] newNormals = null;
            if (hasNormals)
            {
                newNormals = new Vector3[newVertCount];
                System.Array.Copy(normals, newNormals, origVertCount);
            }

            Vector4[] newTangents = null;
            if (hasTangents)
            {
                newTangents = new Vector4[newVertCount];
                System.Array.Copy(tangents, newTangents, origVertCount);
            }

            Color[] newColors = null;
            if (hasColors)
            {
                newColors = new Color[newVertCount];
                System.Array.Copy(colors, newColors, origVertCount);
            }

            BoneWeight[] newBW = null;
            if (hasBW)
            {
                newBW = new BoneWeight[newVertCount];
                System.Array.Copy(boneWeights, newBW, origVertCount);
            }

            // Expand UV channels
            var newUvs = new List<Vector4>[8];
            for (int ch = 0; ch < 8; ch++)
            {
                if (!hasUv[ch]) { newUvs[ch] = null; continue; }
                newUvs[ch] = new List<Vector4>(newVertCount);
                newUvs[ch].AddRange(uvLists[ch]);
                // Pad to newVertCount — will fill duplicates below
                while (newUvs[ch].Count < newVertCount)
                    newUvs[ch].Add(Vector4.zero);
            }

            // Copy boundary vertex attributes to new slots
            foreach (var (info, groupA, groupB, remap) in splitData)
            {
                foreach (var kv in remap)
                {
                    int src = kv.Key;
                    int dst = kv.Value;
                    newVerts[dst] = verts[src];
                    if (hasNormals) newNormals[dst] = normals[src];
                    if (hasTangents) newTangents[dst] = tangents[src];
                    if (hasColors) newColors[dst] = colors[src];
                    if (hasBW) newBW[dst] = boneWeights[src];
                    for (int ch = 0; ch < 8; ch++)
                        if (hasUv[ch]) newUvs[ch][dst] = uvLists[ch][src];
                }
            }

            // Update triangle indices for group B faces
            int[] newTris = (int[])tris.Clone();
            foreach (var (info, groupA, groupB, remap) in splitData)
            {
                foreach (int f in groupB)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int vi = newTris[f * 3 + j];
                        if (remap.TryGetValue(vi, out int ni))
                            newTris[f * 3 + j] = ni;
                    }
                }
            }

            // Capture submesh layout before Clear
            int subCount = mesh.subMeshCount;
            var subDescs = new UnityEngine.Rendering.SubMeshDescriptor[subCount];
            for (int s = 0; s < subCount; s++)
                subDescs[s] = mesh.GetSubMesh(s);

            var bindPoses = mesh.bindposes;

            // Apply to mesh
            mesh.Clear();
            mesh.vertices = newVerts;
            if (hasNormals) mesh.normals = newNormals;
            if (hasTangents) mesh.tangents = newTangents;
            if (hasColors) mesh.colors = newColors;
            if (hasBW) mesh.boneWeights = newBW;
            if (bindPoses != null && bindPoses.Length > 0)
                mesh.bindposes = bindPoses;

            for (int ch = 0; ch < 8; ch++)
            {
                if (!hasUv[ch]) continue;
                mesh.SetUVs(ch, newUvs[ch]);
            }

            // Restore submeshes
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

            // Update shell list — need fresh UV0 for bounds
            var finalUv0 = mesh.uv;

            foreach (var (info, groupA, groupB, remap) in splitData)
            {
                var origShell = shells[info.shellIndex];

                // Rebuild original shell as group A
                origShell.symSplitAxis = info.axis;
                origShell.symSplitSide = 1;
                origShell.faceIndices = groupA;
                origShell.vertexIndices.Clear();
                Vector2 mnA = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 mxA = new Vector2(float.MinValue, float.MinValue);
                foreach (int f in groupA)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int vi = newTris[f * 3 + j];
                        origShell.vertexIndices.Add(vi);
                        if (vi < finalUv0.Length)
                        {
                            mnA = Vector2.Min(mnA, finalUv0[vi]);
                            mxA = Vector2.Max(mxA, finalUv0[vi]);
                        }
                    }
                }
                origShell.boundsMin = mnA;
                origShell.boundsMax = mxA;
                origShell.bboxArea = Mathf.Max(0f, (mxA.x - mnA.x) * (mxA.y - mnA.y));

                // Create new shell for group B
                var newShell = new UvShell { shellId = shells.Count, symSplitAxis = info.axis, symSplitSide = -1 };
                newShell.faceIndices = groupB;
                Vector2 mnB = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 mxB = new Vector2(float.MinValue, float.MinValue);
                foreach (int f in groupB)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int vi = newTris[f * 3 + j];
                        newShell.vertexIndices.Add(vi);
                        if (vi < finalUv0.Length)
                        {
                            mnB = Vector2.Min(mnB, finalUv0[vi]);
                            mxB = Vector2.Max(mxB, finalUv0[vi]);
                        }
                    }
                }
                newShell.boundsMin = mnB;
                newShell.boundsMax = mxB;
                newShell.bboxArea = Mathf.Max(0f, (mxB.x - mnB.x) * (mxB.y - mnB.y));
                shells.Add(newShell);
            }

            UvtLog.Info($"[SymSplit] Split {splitData.Count} shell(s), " +
                $"added {totalNewVerts} boundary verts ({origVertCount} → {newVertCount})");

            return splitData.Count;
        }

        // ═══════════════════════════════════════════════════════════════
        //  Overload: Split with parameter output (for source LOD)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Split shells and output parameters so target LODs can replicate
        /// the same split pattern. Call this on the source LOD first.
        /// </summary>
        public static int Split(Mesh mesh, List<UvShell> shells, out List<SplitParams> outParams)
        {
            outParams = new List<SplitParams>();
            var verts = mesh.vertices;
            var uv0 = mesh.uv;
            var tris = mesh.triangles;

            if (uv0 == null || uv0.Length == 0 || tris.Length == 0)
                return 0;

            int faceCount = tris.Length / 3;
            var uv0C = new Vector2[faceCount];
            var posC = new Vector3[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                uv0C[f] = (uv0[v0] + uv0[v1] + uv0[v2]) / 3f;
                posC[f] = (verts[v0] + verts[v1] + verts[v2]) / 3f;
            }

            // Detect N-fold rotational symmetry per shell
            int totalSplit = 0;
            int shellCountBefore = shells.Count;

            for (int si = 0; si < shellCountBefore; si++)
            {
                var shell = shells[si];
                if (shell.faceIndices.Count < 4) continue;

                int N = DetectFoldCount(shell, uv0C, posC, tris, verts, out int rotAxis, out Vector3 center);

                if (N >= 3)
                {
                    // N-fold rotational split
                    int splitCount = ApplyNFoldSplit(mesh, shells, si, N, rotAxis, center, posC);
                    if (splitCount > 0)
                    {
                        outParams.Add(new SplitParams
                        {
                            foldCount = N,
                            axis = rotAxis,
                            center = center,
                            splitThreshold = 0f
                        });
                        totalSplit += splitCount;
                        UvtLog.Info($"[SymSplit] Shell {si}: N-fold rotational N={N} axis={AxisName(rotAxis)} center=({center.x:F2},{center.y:F2},{center.z:F2})");
                        // Reread mesh data after modification
                        verts = mesh.vertices;
                        uv0 = mesh.uv;
                        tris = mesh.triangles;
                        faceCount = tris.Length / 3;
                        uv0C = new Vector2[faceCount];
                        posC = new Vector3[faceCount];
                        for (int f = 0; f < faceCount; f++)
                        {
                            int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                            uv0C[f] = (uv0[v0] + uv0[v1] + uv0[v2]) / 3f;
                            posC[f] = (verts[v0] + verts[v1] + verts[v2]) / 3f;
                        }
                        continue;
                    }
                }

                // Fall back to binary detection — already handled by Split(mesh, shells)
            }

            // Run standard binary split on remaining shells (those not N-fold split)
            if (totalSplit == 0)
            {
                // No N-fold detected, run the standard binary split
                int binarySplit = Split(mesh, shells);
                // Record binary params for each split
                for (int i = shellCountBefore; i < shells.Count; i++)
                {
                    var s = shells[i];
                    if (s.symSplitAxis >= 0)
                    {
                        outParams.Add(new SplitParams
                        {
                            foldCount = 2,
                            axis = s.symSplitAxis,
                            splitThreshold = 0f, // threshold was used internally
                            center = Vector3.zero
                        });
                    }
                }
                return binarySplit;
            }

            return totalSplit;
        }

        // ═══════════════════════════════════════════════════════════════
        //  SplitWithParams: apply prescribed split (for target LODs)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Apply the same split pattern from source LOD to a target LOD mesh.
        /// Guarantees identical shell count regardless of LOD simplification.
        /// </summary>
        public static int SplitWithParams(Mesh mesh, List<UvShell> shells, List<SplitParams> prescribed)
        {
            if (prescribed == null || prescribed.Count == 0)
                return 0;

            var verts = mesh.vertices;
            var uv0 = mesh.uv;
            var tris = mesh.triangles;
            if (uv0 == null || uv0.Length == 0 || tris.Length == 0)
                return 0;

            int faceCount = tris.Length / 3;
            var uv0C = new Vector2[faceCount];
            var posC = new Vector3[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                uv0C[f] = (uv0[v0] + uv0[v1] + uv0[v2]) / 3f;
                posC[f] = (verts[v0] + verts[v1] + verts[v2]) / 3f;
            }

            int totalSplit = 0;
            int shellCountBefore = shells.Count;

            foreach (var p in prescribed)
            {
                if (p.foldCount < 2) continue;

                // Find the target shell that overlaps in UV0 with the source split.
                // For N-fold, match by finding the shell with overlapping UV0 faces.
                int bestShell = -1;
                int bestFaceCount = 0;

                for (int si = 0; si < shellCountBefore; si++)
                {
                    var shell = shells[si];
                    if (shell.faceIndices.Count <= bestFaceCount) continue;

                    // Check if this shell has UV0 overlap (multiple faces sharing UV0 space)
                    bool hasOverlap = HasUv0Overlap(shell, uv0C);
                    if (!hasOverlap) continue;

                    bestShell = si;
                    bestFaceCount = shell.faceIndices.Count;
                }

                if (bestShell < 0)
                {
                    UvtLog.Verbose($"[SymSplit] SplitWithParams: no matching target shell for N={p.foldCount}");
                    continue;
                }

                if (p.foldCount >= 3)
                {
                    int splitCount = ApplyNFoldSplit(mesh, shells, bestShell, p.foldCount, p.axis, p.center, posC);
                    if (splitCount > 0)
                    {
                        totalSplit += splitCount;
                        UvtLog.Info($"[SymSplit] SplitWithParams: shell {bestShell} → {p.foldCount} sectors (prescribed)");
                        // Reread after mesh modification
                        verts = mesh.vertices;
                        uv0 = mesh.uv;
                        tris = mesh.triangles;
                        faceCount = tris.Length / 3;
                        uv0C = new Vector2[faceCount];
                        posC = new Vector3[faceCount];
                        for (int f2 = 0; f2 < faceCount; f2++)
                        {
                            int v0 = tris[f2 * 3], v1 = tris[f2 * 3 + 1], v2 = tris[f2 * 3 + 2];
                            uv0C[f2] = (uv0[v0] + uv0[v1] + uv0[v2]) / 3f;
                            posC[f2] = (verts[v0] + verts[v1] + verts[v2]) / 3f;
                        }
                    }
                }
                else
                {
                    // Binary: just run the standard split (it will detect the same axis)
                    totalSplit += Split(mesh, shells);
                }
            }

            return totalSplit;
        }

        // ═══════════════════════════════════════════════════════════════
        //  N-fold rotational symmetry detection
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Detect if a shell has N-fold rotational symmetry by counting how
        /// many UV0-duplicate face groups exist at distinct angular positions.
        /// Returns N (fold count), rotation axis, and center.
        /// Returns N=1 if no rotational symmetry is found.
        /// </summary>
        static int DetectFoldCount(UvShell shell, Vector2[] uv0C, Vector3[] posC,
            int[] tris, Vector3[] verts, out int rotAxis, out Vector3 center)
        {
            rotAxis = 1; // default Y
            center = Vector3.zero;

            var faces = shell.faceIndices;
            if (faces.Count < 6) return 1;

            // Build UV0 centroid spatial hash for this shell
            var grid = new Dictionary<long, List<int>>();
            foreach (int f in faces)
            {
                long key = UvGridKey(uv0C[f]);
                if (!grid.TryGetValue(key, out var bucket))
                {
                    bucket = new List<int>();
                    grid[key] = bucket;
                }
                bucket.Add(f);
            }

            // For a sample of faces, count how many "copies" each has
            // (faces with same UV0 centroid but different 3D position)
            var copyCounts = new Dictionary<int, int>(); // N → vote count
            int[] axisMinVotes = new int[3]; // votes for rotation axis (axis of MINIMUM separation)
            Vector3 centerSum = Vector3.zero;
            int centerN = 0;

            int sampleCount = 0;
            const int maxSample = 50;

            foreach (int f in faces)
            {
                if (sampleCount >= maxSample) break;

                var c = uv0C[f];
                int cx = Mathf.FloorToInt(c.x / GRID_CELL);
                int cy = Mathf.FloorToInt(c.y / GRID_CELL);

                int copies = 0;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    long nk = GridKey(cx + dx, cy + dy);
                    if (!grid.TryGetValue(nk, out var bucket)) continue;
                    foreach (int g in bucket)
                    {
                        if (g == f) continue;
                        if (Vector2.Distance(uv0C[f], uv0C[g]) >= UV_NEAR) continue;
                        if (Vector3.Distance(posC[f], posC[g]) <= POS_FAR) continue;
                        copies++;

                        // Vote on rotation axis: axis of MINIMUM separation
                        Vector3 sep = posC[f] - posC[g];
                        float sx = Mathf.Abs(sep.x), sy = Mathf.Abs(sep.y), sz = Mathf.Abs(sep.z);
                        if (sx <= sy && sx <= sz) axisMinVotes[0]++;
                        else if (sy <= sz) axisMinVotes[1]++;
                        else axisMinVotes[2]++;
                    }
                }

                if (copies > 0)
                {
                    int N = copies + 1;
                    if (!copyCounts.ContainsKey(N)) copyCounts[N] = 0;
                    copyCounts[N]++;
                    centerSum += posC[f];
                    centerN++;
                }
                sampleCount++;
            }

            if (copyCounts.Count == 0) return 1;

            // Find modal N (most common copy count)
            int bestN = 1, bestVotes = 0;
            foreach (var kv in copyCounts)
            {
                if (kv.Value > bestVotes)
                {
                    bestN = kv.Key;
                    bestVotes = kv.Value;
                }
            }

            if (bestN <= 2) return bestN; // binary or none

            // Rotation axis = axis with MOST votes for minimum separation
            rotAxis = 0;
            if (axisMinVotes[1] > axisMinVotes[rotAxis]) rotAxis = 1;
            if (axisMinVotes[2] > axisMinVotes[rotAxis]) rotAxis = 2;

            // Center = mean of all face centroids in the shell
            center = Vector3.zero;
            foreach (int f in faces) center += posC[f];
            center /= faces.Count;

            UvtLog.Verbose($"[SymSplit] DetectFoldCount: N={bestN} (votes: {bestVotes}/{sampleCount}), " +
                $"rotAxis={AxisName(rotAxis)} ({axisMinVotes[0]}x/{axisMinVotes[1]}y/{axisMinVotes[2]}z)");

            return bestN;
        }

        // ═══════════════════════════════════════════════════════════════
        //  N-fold angular sector split
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Split a shell into N angular sectors around the given rotation axis.
        /// Modifies mesh in-place, updates shell list. Returns 1 on success, 0 on failure.
        /// </summary>
        static int ApplyNFoldSplit(Mesh mesh, List<UvShell> shells, int shellIndex,
            int N, int rotAxis, Vector3 center, Vector3[] posC)
        {
            var shell = shells[shellIndex];
            var faces = shell.faceIndices;
            var tris = mesh.triangles;
            var verts = mesh.vertices;

            // Assign faces to angular sectors
            var sectors = new List<int>[N];
            for (int k = 0; k < N; k++) sectors[k] = new List<int>();

            float sectorAngle = 2f * Mathf.PI / N;

            foreach (int f in faces)
            {
                float angle = ComputeAngle(posC[f], center, rotAxis);
                int sector = Mathf.FloorToInt((angle + Mathf.PI) / sectorAngle) % N;
                if (sector < 0) sector += N;
                sectors[sector].Add(f);
            }

            // Validate: all sectors should have faces
            int emptySectors = 0;
            for (int k = 0; k < N; k++)
                if (sectors[k].Count == 0) emptySectors++;

            if (emptySectors > 0)
            {
                UvtLog.Verbose($"[SymSplit] NFold: {emptySectors}/{N} empty sectors, skipping");
                return 0;
            }

            // Find vertices per sector and compute boundary remaps
            var sectorVerts = new HashSet<int>[N];
            for (int k = 0; k < N; k++)
            {
                sectorVerts[k] = new HashSet<int>();
                foreach (int f in sectors[k])
                    for (int j = 0; j < 3; j++)
                        sectorVerts[k].Add(tris[f * 3 + j]);
            }

            // For each vertex, find which sectors use it. If used by multiple
            // sectors, keep original index for the lowest-numbered sector and
            // create new indices for higher-numbered sectors.
            int origVertCount = verts.Length;
            int newVertOffset = 0;
            var sectorRemaps = new Dictionary<int, int>[N];
            for (int k = 0; k < N; k++) sectorRemaps[k] = new Dictionary<int, int>();

            var vertexSectorSet = new Dictionary<int, List<int>>();
            for (int k = 0; k < N; k++)
                foreach (int vi in sectorVerts[k])
                {
                    if (!vertexSectorSet.TryGetValue(vi, out var list))
                    {
                        list = new List<int>();
                        vertexSectorSet[vi] = list;
                    }
                    list.Add(k);
                }

            foreach (var kv in vertexSectorSet)
            {
                int vi = kv.Key;
                var sects = kv.Value;
                if (sects.Count <= 1) continue;
                sects.Sort();
                // Sector sects[0] keeps original, others get new indices
                for (int i = 1; i < sects.Count; i++)
                {
                    sectorRemaps[sects[i]][vi] = origVertCount + newVertOffset;
                    newVertOffset++;
                }
            }

            if (newVertOffset == 0)
            {
                UvtLog.Verbose($"[SymSplit] NFold: no boundary vertices to duplicate");
                return 0;
            }

            // Apply mesh modification
            int totalNewVerts = newVertOffset;
            int newVertCount = origVertCount + totalNewVerts;

            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var colors = mesh.colors;
            var boneWeights = mesh.boneWeights;

            bool hasNormals = normals != null && normals.Length == origVertCount;
            bool hasTangents = tangents != null && tangents.Length == origVertCount;
            bool hasColors = colors != null && colors.Length == origVertCount;
            bool hasBW = boneWeights != null && boneWeights.Length == origVertCount;

            var uvLists = new List<Vector4>[8];
            var hasUv = new bool[8];
            for (int ch = 0; ch < 8; ch++)
            {
                uvLists[ch] = new List<Vector4>();
                mesh.GetUVs(ch, uvLists[ch]);
                hasUv[ch] = uvLists[ch].Count == origVertCount;
            }

            // Expand arrays
            var newVerts = new Vector3[newVertCount];
            System.Array.Copy(verts, newVerts, origVertCount);
            Vector3[] newNormals = hasNormals ? new Vector3[newVertCount] : null;
            if (hasNormals) System.Array.Copy(normals, newNormals, origVertCount);
            Vector4[] newTangents = hasTangents ? new Vector4[newVertCount] : null;
            if (hasTangents) System.Array.Copy(tangents, newTangents, origVertCount);
            Color[] newColors = hasColors ? new Color[newVertCount] : null;
            if (hasColors) System.Array.Copy(colors, newColors, origVertCount);
            BoneWeight[] newBW = hasBW ? new BoneWeight[newVertCount] : null;
            if (hasBW) System.Array.Copy(boneWeights, newBW, origVertCount);

            var newUvs = new List<Vector4>[8];
            for (int ch = 0; ch < 8; ch++)
            {
                if (!hasUv[ch]) { newUvs[ch] = null; continue; }
                newUvs[ch] = new List<Vector4>(newVertCount);
                newUvs[ch].AddRange(uvLists[ch]);
                while (newUvs[ch].Count < newVertCount)
                    newUvs[ch].Add(Vector4.zero);
            }

            // Copy boundary vertex attributes to new slots
            for (int k = 0; k < N; k++)
            {
                foreach (var kv in sectorRemaps[k])
                {
                    int src = kv.Key, dst = kv.Value;
                    newVerts[dst] = verts[src];
                    if (hasNormals) newNormals[dst] = normals[src];
                    if (hasTangents) newTangents[dst] = tangents[src];
                    if (hasColors) newColors[dst] = colors[src];
                    if (hasBW) newBW[dst] = boneWeights[src];
                    for (int ch = 0; ch < 8; ch++)
                        if (hasUv[ch]) newUvs[ch][dst] = uvLists[ch][src];
                }
            }

            // Remap triangle indices per sector
            int[] newTris = (int[])tris.Clone();
            for (int k = 0; k < N; k++)
            {
                if (sectorRemaps[k].Count == 0) continue;
                foreach (int f in sectors[k])
                    for (int j = 0; j < 3; j++)
                    {
                        int vi = newTris[f * 3 + j];
                        if (sectorRemaps[k].TryGetValue(vi, out int ni))
                            newTris[f * 3 + j] = ni;
                    }
            }

            // Capture submesh layout
            int subCount = mesh.subMeshCount;
            var subDescs = new UnityEngine.Rendering.SubMeshDescriptor[subCount];
            for (int s = 0; s < subCount; s++)
                subDescs[s] = mesh.GetSubMesh(s);
            var bindPoses = mesh.bindposes;

            // Apply to mesh
            mesh.Clear();
            mesh.vertices = newVerts;
            if (hasNormals) mesh.normals = newNormals;
            if (hasTangents) mesh.tangents = newTangents;
            if (hasColors) mesh.colors = newColors;
            if (hasBW) mesh.boneWeights = newBW;
            if (bindPoses != null && bindPoses.Length > 0)
                mesh.bindposes = bindPoses;
            for (int ch = 0; ch < 8; ch++)
            {
                if (!hasUv[ch]) continue;
                mesh.SetUVs(ch, newUvs[ch]);
            }

            // Restore submeshes
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

            // Update shell list — sector 0 replaces original, sectors 1..N-1 are new
            var finalUv0 = mesh.uv;

            for (int k = 0; k < N; k++)
            {
                UvShell target;
                if (k == 0)
                {
                    target = shells[shellIndex];
                    target.faceIndices = sectors[0];
                    target.vertexIndices.Clear();
                }
                else
                {
                    target = new UvShell { shellId = shells.Count };
                    target.faceIndices = sectors[k];
                    shells.Add(target);
                }

                target.symSplitAxis = rotAxis;
                target.symSplitSide = k; // sector index

                Vector2 mn = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 mx = new Vector2(float.MinValue, float.MinValue);
                foreach (int f in sectors[k])
                    for (int j = 0; j < 3; j++)
                    {
                        int vi = newTris[f * 3 + j];
                        target.vertexIndices.Add(vi);
                        if (vi < finalUv0.Length)
                        {
                            mn = Vector2.Min(mn, finalUv0[vi]);
                            mx = Vector2.Max(mx, finalUv0[vi]);
                        }
                    }
                target.boundsMin = mn;
                target.boundsMax = mx;
                target.bboxArea = Mathf.Max(0f, (mx.x - mn.x) * (mx.y - mn.y));
            }

            UvtLog.Info($"[SymSplit] NFold: shell {shellIndex} → {N} sectors, " +
                $"{totalNewVerts} boundary verts duplicated ({origVertCount} → {newVertCount})");

            return 1;
        }

        // ═══════════════════════════════════════════════════════════════
        //  Helpers
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Check if a shell has UV0 overlap (multiple faces sharing the same UV0 space
        /// but at different 3D positions). Quick check using spatial hash.
        /// </summary>
        static bool HasUv0Overlap(UvShell shell, Vector2[] uv0C)
        {
            var grid = new Dictionary<long, List<int>>();
            foreach (int f in shell.faceIndices)
            {
                long key = UvGridKey(uv0C[f]);
                if (!grid.TryGetValue(key, out var bucket))
                {
                    bucket = new List<int>();
                    grid[key] = bucket;
                }
                bucket.Add(f);
            }
            // If any bucket has more than 1 face, there's potential overlap
            foreach (var kv in grid)
                if (kv.Value.Count > 1) return true;
            return false;
        }

        /// <summary>
        /// Compute angle of a position around a rotation axis relative to a center.
        /// Returns angle in [-PI, PI].
        /// </summary>
        static float ComputeAngle(Vector3 pos, Vector3 center, int rotAxis)
        {
            Vector3 d = pos - center;
            // Project onto the plane perpendicular to rotation axis
            float u, v;
            switch (rotAxis)
            {
                case 0: u = d.z; v = d.y; break; // X axis rotation → YZ plane
                case 1: u = d.x; v = d.z; break; // Y axis rotation → XZ plane
                default: u = d.x; v = d.y; break; // Z axis rotation → XY plane
            }
            return Mathf.Atan2(v, u);
        }

        static long UvGridKey(Vector2 uv)
        {
            int cx = Mathf.FloorToInt(uv.x / GRID_CELL);
            int cy = Mathf.FloorToInt(uv.y / GRID_CELL);
            return GridKey(cx, cy);
        }

        static long GridKey(int cx, int cy)
        {
            return (long)cx * 73856093L ^ (long)cy * 19349663L;
        }

        static string AxisName(int axis)
        {
            return axis == 0 ? "X" : axis == 1 ? "Y" : "Z";
        }
    }
}
