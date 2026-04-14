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
        const float UV_NEAR_FACTOR = 0.05f;  // UV0 centroid threshold = shell UV diagonal * factor
        const float UV_NEAR_FLOOR  = 0.005f; // minimum UV0 centroid distance threshold
        const float POS_FAR_FACTOR = 0.10f;  // 3D centroid threshold = mesh diagonal * 10%
        const float POS_FAR_FLOOR  = 0.1f;   // minimum 3D centroid distance threshold
        const float GRID_CELL = 0.01f;       // spatial hash cell for UV0 centroids

        struct SplitInfo
        {
            public int shellIndex;
            public int axis; // 0=X, 1=Y, 2=Z
            public float splitThreshold; // computed from pair midpoints
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

            // ── Adaptive thresholds from mesh bounds ──
            float meshDiag = (mesh.bounds.max - mesh.bounds.min).magnitude;
            float posFar = Mathf.Max(meshDiag * POS_FAR_FACTOR, POS_FAR_FLOOR);
            UvtLog.Verbose($"[SymSplit] Adaptive: meshDiag={meshDiag:F3} posFar={posFar:F3}");

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

                // Per-shell adaptive UV0 threshold from shell UV bounding box
                float shellUvDiag = (shell.boundsMax - shell.boundsMin).magnitude;
                float uvNear = Mathf.Max(shellUvDiag * UV_NEAR_FACTOR, UV_NEAR_FLOOR);

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
                float[] midpointSum = new float[3]; // accumulate midpoint per axis
                int[] midpointCount = new int[3];
                bool found = false;

                // Search radius in grid cells must cover uvNear distance
                int gridRadius = Mathf.Max(1, Mathf.CeilToInt(uvNear / GRID_CELL));

                foreach (int f in faces)
                {
                    var c = uv0C[f];
                    int cx = Mathf.FloorToInt(c.x / GRID_CELL);
                    int cy = Mathf.FloorToInt(c.y / GRID_CELL);

                    for (int dx = -gridRadius; dx <= gridRadius; dx++)
                    for (int dy = -gridRadius; dy <= gridRadius; dy++)
                    {
                        long nk = GridKey(cx + dx, cy + dy);
                        if (!grid.TryGetValue(nk, out var bucket)) continue;

                        foreach (int g in bucket)
                        {
                            if (g <= f) continue; // avoid duplicate pairs + self
                            float uvDist = Vector2.Distance(uv0C[f], uv0C[g]);
                            if (uvDist >= uvNear) continue;
                            float posDist = Vector3.Distance(posC[f], posC[g]);
                            if (posDist <= posFar) continue;

                            // Symmetry pair — vote on axis and accumulate midpoint
                            Vector3 mid = (posC[f] + posC[g]) * 0.5f;
                            float ax = Mathf.Abs(mid.x);
                            float ay = Mathf.Abs(mid.y);
                            float az = Mathf.Abs(mid.z);

                            int votedAxis;
                            if (ax <= ay && ax <= az) votedAxis = 0;
                            else if (ay <= az) votedAxis = 1;
                            else votedAxis = 2;

                            axisVotes[votedAxis]++;
                            midpointSum[votedAxis] += mid[votedAxis];
                            midpointCount[votedAxis]++;
                            found = true;
                        }
                    }
                }

                if (!found) continue;

                int bestAxis = 0;
                if (axisVotes[1] > axisVotes[bestAxis]) bestAxis = 1;
                if (axisVotes[2] > axisVotes[bestAxis]) bestAxis = 2;

                // Compute split threshold from average midpoint of detected pairs.
                // This handles models not centered at origin.
                float splitThreshold = 0f;
                if (midpointCount[bestAxis] > 0)
                    splitThreshold = midpointSum[bestAxis] / midpointCount[bestAxis];

                splits.Add(new SplitInfo { shellIndex = si, axis = bestAxis,
                    splitThreshold = splitThreshold });
                UvtLog.Verbose($"[SymSplit] Shell {si}: symmetry on {AxisName(bestAxis)} " +
                    $"({axisVotes[0]}x/{axisVotes[1]}y/{axisVotes[2]}z votes, {faces.Count} faces) " +
                    $"uvNear={uvNear:F4} posFar={posFar:F3} splitAt={splitThreshold:F4}");
            }

            // ── Diagnostic: detect N-fold rotational symmetry in unsplit shells ──
            for (int si = 0; si < shells.Count; si++)
            {
                bool wasSplit = false;
                foreach (var sp in splits)
                    if (sp.shellIndex == si) { wasSplit = true; break; }
                if (wasSplit) continue;
                if (shells[si].faceIndices.Count < 20) continue;
                DetectRotationalSymmetry(shells[si], verts, uv0, tris);
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

                // Try splitting at computed midpoint threshold first,
                // fall back to origin (0) if midpoint puts all faces on one side
                float threshold = sp.splitThreshold;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    groupA.Clear();
                    groupB.Clear();
                    foreach (int f in shell.faceIndices)
                    {
                        float val = posC[f][sp.axis];
                        if (val >= threshold)
                            groupA.Add(f);
                        else
                            groupB.Add(f);
                    }
                    if (groupA.Count > 0 && groupB.Count > 0) break;

                    // First attempt failed — try origin as fallback
                    if (attempt == 0 && Mathf.Abs(threshold) > 1e-6f)
                    {
                        UvtLog.Verbose($"[SymSplit] Shell {sp.shellIndex}: " +
                            $"midpoint threshold {threshold:F4} failed, trying origin");
                        threshold = 0f;
                    }
                }

                // Skip if both thresholds fail
                if (groupA.Count == 0 || groupB.Count == 0)
                {
                    UvtLog.Verbose($"[SymSplit] Shell {sp.shellIndex}: skip " +
                        $"(all faces on one side, tried midpoint={sp.splitThreshold:F4} and origin)");
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
                var newShell = new UvShell { shellId = shells.Count };
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

        // ── N-fold rotational symmetry detection (research/diagnostic) ──

        /// <summary>
        /// Detect N-fold rotational symmetry in a shell's UV0 overlap pattern.
        /// Returns the number of segments (N) if rotational symmetry is found, 0 otherwise.
        /// Does NOT modify the mesh — diagnostic only.
        /// </summary>
        internal static int DetectRotationalSymmetry(
            UvShell shell, Vector3[] verts, Vector2[] uv0, int[] tris)
        {
            var faces = shell.faceIndices;
            if (faces.Count < 6) return 0;

            // Compute shell 3D centroid
            Vector3 center = Vector3.zero;
            foreach (int f in faces)
            {
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                center += (verts[v0] + verts[v1] + verts[v2]) / 3f;
            }
            center /= faces.Count;

            // PCA to find rotation axis (smallest variance axis = rotation axis for a ring)
            float cxx = 0, cyy = 0, czz = 0, cxy = 0, cxz = 0, cyz = 0;
            foreach (int f in faces)
            {
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                Vector3 fc = (verts[v0] + verts[v1] + verts[v2]) / 3f - center;
                cxx += fc.x * fc.x; cyy += fc.y * fc.y; czz += fc.z * fc.z;
                cxy += fc.x * fc.y; cxz += fc.x * fc.z; cyz += fc.y * fc.z;
            }

            // Determine the axis with maximum variance spread (rotation axis = min variance)
            // Simplified: pick axis with minimum diagonal covariance
            int rotAxis;
            if (cxx <= cyy && cxx <= czz) rotAxis = 0;      // X has min variance → rotate around X
            else if (cyy <= czz) rotAxis = 1;                // Y has min variance → rotate around Y
            else rotAxis = 2;                                 // Z has min variance → rotate around Z

            // Compute angular positions of face centroids projected onto rotation plane
            int projA = (rotAxis + 1) % 3;
            int projB = (rotAxis + 2) % 3;

            var angles = new List<float>(faces.Count);
            foreach (int f in faces)
            {
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                Vector3 fc = (verts[v0] + verts[v1] + verts[v2]) / 3f - center;
                float a = fc[projA];
                float b = fc[projB];
                angles.Add(Mathf.Atan2(b, a));
            }

            // Count UV0 overlap layers via grid sampling
            // Sample a small grid over the shell's UV0 bbox and count how many faces
            // have centroids near each sample point
            const int kSamples = 16;
            float uvMinX = shell.boundsMin.x, uvMaxX = shell.boundsMax.x;
            float uvMinY = shell.boundsMin.y, uvMaxY = shell.boundsMax.y;
            float uvW = uvMaxX - uvMinX, uvH = uvMaxY - uvMinY;
            if (uvW < 1e-6f || uvH < 1e-6f) return 0;

            // Build UV0 centroid list for this shell
            var uvCentroids = new Vector2[faces.Count];
            for (int i = 0; i < faces.Count; i++)
            {
                int f = faces[i];
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                uvCentroids[i] = (uv0[v0] + uv0[v1] + uv0[v2]) / 3f;
            }

            float cellW = uvW / kSamples;
            float cellH = uvH / kSamples;
            int maxLayers = 0;
            int layerSum = 0;
            int layerCells = 0;

            for (int sy = 0; sy < kSamples; sy++)
            {
                for (int sx = 0; sx < kSamples; sx++)
                {
                    float cx = uvMinX + (sx + 0.5f) * cellW;
                    float cy = uvMinY + (sy + 0.5f) * cellH;

                    int count = 0;
                    for (int i = 0; i < uvCentroids.Length; i++)
                    {
                        float dx = uvCentroids[i].x - cx;
                        float dy = uvCentroids[i].y - cy;
                        if (Mathf.Abs(dx) < cellW && Mathf.Abs(dy) < cellH)
                            count++;
                    }
                    if (count > 1)
                    {
                        if (count > maxLayers) maxLayers = count;
                        layerSum += count;
                        layerCells++;
                    }
                }
            }

            // If consistent multi-layer overlap detected, it's N-fold
            if (layerCells < kSamples) return 0;  // too few cells with overlap
            float avgLayers = (float)layerSum / layerCells;
            int nFold = Mathf.RoundToInt(avgLayers);

            if (nFold > 2)
            {
                UvtLog.Info($"[SymSplit] Shell: detected {nFold}-fold rotational symmetry " +
                    $"(axis={AxisName(rotAxis)}, maxLayers={maxLayers}, avgLayers={avgLayers:F1}, " +
                    $"{faces.Count} faces)");
                return nFold;
            }

            return 0;
        }

        // ── Spatial hash helpers ──

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
