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
        const float GRID_CELL = 0.01f; // spatial hash cell for UV0 centroids
        const int   MIN_FACES = 20;    // skip shells with fewer faces — splitting tiny shells
                                        // creates fragments too small for quality transfer

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
        public static int Split(Mesh mesh, List<UvShell> shells, float separationThreshold = 0.10f, bool allowNFold = true)
        {
            var verts = mesh.vertices;
            var uv0 = mesh.uv;
            var tris = mesh.triangles;

            if (uv0 == null || uv0.Length == 0 || tris.Length == 0)
                return 0;

            // Adaptive 3D separation threshold based on mesh size.
            // Fixed 0.5 was too large for small models (WateringCan diag=0.34).
            float meshDiag = mesh.bounds.size.magnitude;
            float POS_FAR = Mathf.Max(meshDiag * 0.1f, 0.05f);

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
                if (faces.Count < MIN_FACES) continue;

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
                float[] axisMidpointSum = new float[3]; // sum of midpoint[axis] per axis
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

                            // Symmetry pair — vote on axis
                            Vector3 mid = (posC[f] + posC[g]) * 0.5f;
                            float ax = Mathf.Abs(mid.x);
                            float ay = Mathf.Abs(mid.y);
                            float az = Mathf.Abs(mid.z);

                            if (ax <= ay && ax <= az)
                            {
                                axisVotes[0]++;
                                axisMidpointSum[0] += mid.x;
                            }
                            else if (ay <= az)
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

                // ── Secondary detection: UV0 overlap-based (V-shaped symmetry) ──
                // Catches symmetry where UV0 regions overlap but per-face centroids
                // don't coincide (e.g., V-shaped geometry with shared boundary edges).
                // Uses grid rasterization to find non-adjacent face pairs occupying
                // the same UV0 space, then checks if they lie on opposite sides of
                // a 3D symmetry plane.
                if (!found && faces.Count >= 6)
                {
                    var overlapPairs = DetectUv0OverlapPairs(shell, uv0, tris);
                    if (overlapPairs.Count > 0)
                    {
                        // Vote on symmetry axis using overlapping face pairs
                        foreach (var (fA, fB) in overlapPairs)
                        {
                            float posDist = Vector3.Distance(posC[fA], posC[fB]);
                            if (posDist <= POS_FAR * 0.5f) continue; // too close in 3D

                            Vector3 mid = (posC[fA] + posC[fB]) * 0.5f;
                            float ax = Mathf.Abs(mid.x);
                            float ay = Mathf.Abs(mid.y);
                            float az = Mathf.Abs(mid.z);

                            if (ax <= ay && ax <= az)
                            {
                                axisVotes[0]++;
                                axisMidpointSum[0] += mid.x;
                            }
                            else if (ay <= az)
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

                        if (found)
                            UvtLog.Verbose($"[SymSplit] Shell {si}: V-shape overlap detected " +
                                $"({overlapPairs.Count} pairs)");
                    }
                }

                // ── Tertiary detection: UV0 winding split ──
                // DISABLED: causes over-splitting on models with mixed winding
                // (inner/outer walls, belt front/back). POS_FAR threshold is
                // insufficient — needs mesh-scale-aware separation check.
                // TODO: re-enable with adaptive threshold after auto-tuning system.


                if (!found) continue;

                int bestAxis = 0;
                if (axisVotes[1] > axisVotes[bestAxis]) bestAxis = 1;
                if (axisVotes[2] > axisVotes[bestAxis]) bestAxis = 2;

                // Compute split threshold: average midpoint along the winning axis.
                // This is the symmetry plane position — faces on opposite sides
                // of this value belong to different symmetry halves.
                float threshold = axisVotes[bestAxis] > 0
                    ? axisMidpointSum[bestAxis] / axisVotes[bestAxis]
                    : 0f;

                splits.Add(new SplitInfo { shellIndex = si, axis = bestAxis, splitThreshold = threshold });
                UvtLog.Verbose($"[SymSplit] Shell {si}: symmetry on {AxisName(bestAxis)} " +
                    $"({axisVotes[0]}x/{axisVotes[1]}y/{axisVotes[2]}z votes, {faces.Count} faces, " +
                    $"threshold={threshold:F4})");
            }

            // ══════════════ Phase 1b: N-fold rotational split ══════════════
            // For shells not caught by bilateral detection, check for N-fold
            // rotational symmetry and split into N angular sectors.
            // Only on source LOD — target LODs use bilateral only to maintain
            // UV2 continuity with source.
            var nFoldSplits = new List<(int shellIndex, int nFold, int rotAxis, Vector3 center)>();
            if (!allowNFold) goto skipNFold;
            for (int si = 0; si < shells.Count; si++)
            {
                // Skip if already bilateral-split
                bool alreadySplit = false;
                foreach (var sp in splits)
                    if (sp.shellIndex == si) { alreadySplit = true; break; }
                if (alreadySplit) continue;

                var shell = shells[si];
                if (shell.faceIndices.Count < MIN_FACES) continue;

                int nFold = DetectRotationalSymmetry(shell, verts, uv0, tris);
                if (nFold < 3) continue;

                // Compute rotation axis and center
                Vector3 center = Vector3.zero;
                foreach (int f in shell.faceIndices)
                {
                    int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                    center += (verts[v0] + verts[v1] + verts[v2]) / 3f;
                }
                center /= shell.faceIndices.Count;

                float cxx = 0, cyy = 0, czz = 0;
                foreach (int f in shell.faceIndices)
                {
                    int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                    Vector3 fc = (verts[v0] + verts[v1] + verts[v2]) / 3f - center;
                    cxx += fc.x * fc.x; cyy += fc.y * fc.y; czz += fc.z * fc.z;
                }
                int rotAxis;
                if (cxx <= cyy && cxx <= czz) rotAxis = 0;
                else if (cyy <= czz) rotAxis = 1;
                else rotAxis = 2;

                nFoldSplits.Add((si, nFold, rotAxis, center));
                UvtLog.Info($"[SymSplit] Shell {si}: {nFold}-fold rotational on {AxisName(rotAxis)}, " +
                    $"{shell.faceIndices.Count} faces — will split into {nFold} sectors");
            }

            skipNFold:
            if (splits.Count == 0 && nFoldSplits.Count == 0) return 0;

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

                // Skip if groups are not well separated in 3D.
                // Compute 3D centroids of each group and the shell's extent.
                // Groups must be significantly separated relative to the shell size
                // to be a real symmetry split (not just noise from midpoint threshold).
                {
                    Vector3 centA = Vector3.zero, centB = Vector3.zero;
                    foreach (int f in groupA) centA += posC[f];
                    foreach (int f in groupB) centB += posC[f];
                    centA /= groupA.Count;
                    centB /= groupB.Count;
                    float groupSep = Vector3.Distance(centA, centB);

                    // Compute shell 3D extent (AABB diagonal)
                    Vector3 sMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    Vector3 sMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    foreach (int f in shell.faceIndices)
                    {
                        sMin = Vector3.Min(sMin, posC[f]);
                        sMax = Vector3.Max(sMax, posC[f]);
                    }
                    float shellExtent = (sMax - sMin).magnitude;

                    // Groups must be separated by at least N% of the shell's extent
                    if (shellExtent > 1e-6f && groupSep / shellExtent < separationThreshold)
                    {
                        UvtLog.Verbose($"[SymSplit] Shell {sp.shellIndex}: skip (3D separation too small: " +
                            $"{groupSep:F1}/{shellExtent:F1} = {groupSep / shellExtent:P0})");
                        continue;
                    }
                }

                // Skip if UV0 bounding boxes don't overlap — no symmetry overlap to fix.
                // SymSplit exists to separate overlapping UV0 regions so xatlas packs them
                // at different UV2 positions. Without UV0 overlap, the split is unnecessary.
                {
                    Vector2 mnA = new Vector2(float.MaxValue, float.MaxValue);
                    Vector2 mxA = new Vector2(float.MinValue, float.MinValue);
                    foreach (int f in groupA)
                        for (int j = 0; j < 3; j++)
                        {
                            int vi = tris[f * 3 + j];
                            if (vi < uv0.Length) { mnA = Vector2.Min(mnA, uv0[vi]); mxA = Vector2.Max(mxA, uv0[vi]); }
                        }
                    Vector2 mnB = new Vector2(float.MaxValue, float.MaxValue);
                    Vector2 mxB = new Vector2(float.MinValue, float.MinValue);
                    foreach (int f in groupB)
                        for (int j = 0; j < 3; j++)
                        {
                            int vi = tris[f * 3 + j];
                            if (vi < uv0.Length) { mnB = Vector2.Min(mnB, uv0[vi]); mxB = Vector2.Max(mxB, uv0[vi]); }
                        }
                    // Check bbox overlap
                    float oMinX = Mathf.Max(mnA.x, mnB.x);
                    float oMaxX = Mathf.Min(mxA.x, mxB.x);
                    float oMinY = Mathf.Max(mnA.y, mnB.y);
                    float oMaxY = Mathf.Min(mxA.y, mxB.y);
                    if (oMaxX <= oMinX || oMaxY <= oMinY)
                    {
                        UvtLog.Verbose($"[SymSplit] Shell {sp.shellIndex}: skip (UV0 bboxes don't overlap)");
                        continue;
                    }
                    // Also check overlap is significant (>10% of smaller bbox area)
                    float overlapArea = (oMaxX - oMinX) * (oMaxY - oMinY);
                    float areaA = (mxA.x - mnA.x) * (mxA.y - mnA.y);
                    float areaB = (mxB.x - mnB.x) * (mxB.y - mnB.y);
                    float smaller = Mathf.Min(areaA, areaB);
                    if (smaller > 1e-8f && overlapArea / smaller < 0.10f)
                    {
                        UvtLog.Verbose($"[SymSplit] Shell {sp.shellIndex}: skip (UV0 overlap too small: {overlapArea / smaller:P0})");
                        continue;
                    }
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

                // Skip if split is too expensive: boundary verts exceed smaller group's
                // face count. Such splits add more vertex bloat than separation benefit.
                int smallerGroup = Mathf.Min(groupA.Count, groupB.Count);
                if (boundary.Count > smallerGroup)
                {
                    UvtLog.Verbose($"[SymSplit] Shell {sp.shellIndex}: skip (boundary {boundary.Count} > smaller group {smallerGroup} faces)");
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
                origShell.symSplitAxis = info.axis;
                origShell.symSplitSide = +1; // groupA = positive side

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
                newShell.symSplitAxis = info.axis;
                newShell.symSplitSide = -1; // groupB = negative side
                shells.Add(newShell);
            }

            int bilateralCount = splitData.Count;
            UvtLog.Info($"[SymSplit] Split {bilateralCount} shell(s), " +
                $"added {totalNewVerts} boundary verts ({origVertCount} → {newVertCount})");

            // ══════════════ Phase 4: N-fold rotational splits ══════════════
            int nFoldCount = 0;
            if (nFoldSplits.Count > 0)
            {
                // Re-read mesh data (may have changed from bilateral splits)
                verts = mesh.vertices;
                uv0 = mesh.uv;
                tris = mesh.triangles;
                origVertCount = verts.Length;
                newVertOffset = 0;

                foreach (var (shellIdx, nFold, rotAxisIdx, center) in nFoldSplits)
                {
                    if (shellIdx >= shells.Count) continue;
                    var shell = shells[shellIdx];
                    var faces = shell.faceIndices;

                    // Compute angular position for each face
                    int projA = (rotAxisIdx + 1) % 3;
                    int projB = (rotAxisIdx + 2) % 3;

                    var faceAngles = new float[faces.Count];
                    for (int i = 0; i < faces.Count; i++)
                    {
                        int f = faces[i];
                        int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                        Vector3 fc = (verts[v0] + verts[v1] + verts[v2]) / 3f - center;
                        faceAngles[i] = Mathf.Atan2(fc[projB], fc[projA]);
                    }

                    // Assign faces to N sectors
                    float sectorSize = 2f * Mathf.PI / nFold;
                    var sectors = new List<int>[nFold];
                    for (int s = 0; s < nFold; s++) sectors[s] = new List<int>();

                    for (int i = 0; i < faces.Count; i++)
                    {
                        float angle = faceAngles[i] + Mathf.PI; // shift to [0, 2π]
                        int sector = Mathf.Clamp(Mathf.FloorToInt(angle / sectorSize), 0, nFold - 1);
                        sectors[sector].Add(faces[i]);
                    }

                    // Skip if any sector is empty
                    bool anyEmpty = false;
                    foreach (var sec in sectors) if (sec.Count == 0) anyEmpty = true;
                    if (anyEmpty)
                    {
                        UvtLog.Warn($"[SymSplit] Shell {shellIdx}: N-fold skip (empty sector)");
                        continue;
                    }

                    // Find boundary vertices between adjacent sectors
                    var sectorVerts = new HashSet<int>[nFold];
                    for (int s = 0; s < nFold; s++)
                    {
                        sectorVerts[s] = new HashSet<int>();
                        foreach (int f in sectors[s])
                            for (int j = 0; j < 3; j++)
                                sectorVerts[s].Add(tris[f * 3 + j]);
                    }

                    // Count total boundary verts needed
                    // Each sector >0 gets its own copy of boundary verts shared with other sectors
                    var sectorBoundaryRemap = new Dictionary<int, int>[nFold];
                    int totalBoundary = 0;
                    for (int s = 1; s < nFold; s++)
                    {
                        sectorBoundaryRemap[s] = new Dictionary<int, int>();
                        foreach (int vi in sectorVerts[s])
                        {
                            // Check if this vert is shared with any other sector
                            for (int t = 0; t < s; t++)
                            {
                                if (sectorVerts[t].Contains(vi) && !sectorBoundaryRemap[s].ContainsKey(vi))
                                {
                                    sectorBoundaryRemap[s][vi] = origVertCount + newVertOffset;
                                    newVertOffset++;
                                    totalBoundary++;
                                    break;
                                }
                            }
                        }
                    }

                    if (totalBoundary == 0)
                    {
                        UvtLog.Verbose($"[SymSplit] Shell {shellIdx}: N-fold no boundary (already separate)");
                        continue;
                    }

                    // Expand mesh arrays
                    newVertCount = origVertCount + newVertOffset;
                    var nfVerts = new Vector3[newVertCount];
                    System.Array.Copy(verts, nfVerts, origVertCount);
                    var nfUv0 = new Vector2[newVertCount];
                    System.Array.Copy(uv0, nfUv0, origVertCount);

                    var nfSrcNormals = mesh.normals;
                    Vector3[] nfNormals = null;
                    if (nfSrcNormals != null && nfSrcNormals.Length == origVertCount)
                    {
                        nfNormals = new Vector3[newVertCount];
                        System.Array.Copy(nfSrcNormals, nfNormals, origVertCount);
                    }

                    var nfSrcTangents = mesh.tangents;
                    Vector4[] nfTangents = null;
                    if (nfSrcTangents != null && nfSrcTangents.Length == origVertCount)
                    {
                        nfTangents = new Vector4[newVertCount];
                        System.Array.Copy(nfSrcTangents, nfTangents, origVertCount);
                    }

                    // Copy UV channels
                    var nfUvChannels = new List<Vector2>[8];
                    for (int ch = 0; ch < 8; ch++)
                    {
                        var chData = new List<Vector2>();
                        mesh.GetUVs(ch, chData);
                        if (chData.Count == origVertCount)
                        {
                            while (chData.Count < newVertCount) chData.Add(Vector2.zero);
                            nfUvChannels[ch] = chData;
                        }
                    }

                    // Duplicate boundary vertices
                    for (int s = 1; s < nFold; s++)
                    {
                        if (sectorBoundaryRemap[s] == null) continue;
                        foreach (var kv in sectorBoundaryRemap[s])
                        {
                            int src = kv.Key, dst = kv.Value;
                            if (dst < newVertCount)
                            {
                                nfVerts[dst] = verts[src];
                                nfUv0[dst] = uv0[src];
                                if (nfNormals != null && src < nfSrcNormals.Length) nfNormals[dst] = nfSrcNormals[src];
                                if (nfTangents != null && src < nfSrcTangents.Length) nfTangents[dst] = nfSrcTangents[src];
                                for (int ch = 0; ch < 8; ch++)
                                    if (nfUvChannels[ch] != null && src < nfUvChannels[ch].Count)
                                        nfUvChannels[ch][dst] = nfUvChannels[ch][src];
                            }
                        }
                    }

                    // Remap triangle indices for sectors 1..N-1
                    var nfTris = (int[])tris.Clone();
                    for (int s = 1; s < nFold; s++)
                    {
                        if (sectorBoundaryRemap[s] == null) continue;
                        foreach (int f in sectors[s])
                        {
                            for (int j = 0; j < 3; j++)
                            {
                                int vi = nfTris[f * 3 + j];
                                if (sectorBoundaryRemap[s].TryGetValue(vi, out int newVi))
                                    nfTris[f * 3 + j] = newVi;
                            }
                        }
                    }

                    // Apply to mesh
                    mesh.SetVertices(nfVerts);
                    mesh.SetNormals(nfNormals);
                    if (nfTangents != null) mesh.SetTangents(nfTangents);
                    for (int ch = 0; ch < 8; ch++)
                        if (nfUvChannels[ch] != null)
                            mesh.SetUVs(ch, nfUvChannels[ch]);
                    mesh.SetTriangles(nfTris, 0);
                    mesh.RecalculateBounds();

                    // Update shell list: original shell keeps sector 0, add new shells for 1..N-1
                    shell.faceIndices = sectors[0];
                    shell.vertexIndices = new HashSet<int>();
                    foreach (int f in sectors[0])
                        for (int j = 0; j < 3; j++)
                            shell.vertexIndices.Add(nfTris[f * 3 + j]);
                    // Recompute bounds for sector 0
                    Vector2 mn0 = new Vector2(float.MaxValue, float.MaxValue);
                    Vector2 mx0 = new Vector2(float.MinValue, float.MinValue);
                    foreach (int f in sectors[0])
                        for (int j = 0; j < 3; j++)
                        {
                            int vi = nfTris[f * 3 + j];
                            if (vi < nfUv0.Length) { mn0 = Vector2.Min(mn0, nfUv0[vi]); mx0 = Vector2.Max(mx0, nfUv0[vi]); }
                        }
                    shell.boundsMin = mn0; shell.boundsMax = mx0;
                    shell.bboxArea = Mathf.Max(0f, (mx0.x - mn0.x) * (mx0.y - mn0.y));

                    for (int s = 1; s < nFold; s++)
                    {
                        var ns = new UvShell { faceIndices = sectors[s], vertexIndices = new HashSet<int>() };
                        foreach (int f in sectors[s])
                            for (int j = 0; j < 3; j++)
                                ns.vertexIndices.Add(nfTris[f * 3 + j]);
                        Vector2 mn = new Vector2(float.MaxValue, float.MaxValue);
                        Vector2 mx = new Vector2(float.MinValue, float.MinValue);
                        foreach (int vi in ns.vertexIndices)
                            if (vi < nfUv0.Length) { mn = Vector2.Min(mn, nfUv0[vi]); mx = Vector2.Max(mx, nfUv0[vi]); }
                        ns.boundsMin = mn; ns.boundsMax = mx;
                        ns.bboxArea = Mathf.Max(0f, (mx.x - mn.x) * (mx.y - mn.y));
                        shells.Add(ns);
                    }

                    // Update for next iteration
                    verts = mesh.vertices;
                    uv0 = mesh.uv;
                    tris = mesh.triangles;
                    origVertCount = verts.Length;

                    nFoldCount++;
                    UvtLog.Info($"[SymSplit] Shell {shellIdx}: N-fold split into {nFold} sectors, " +
                        $"{totalBoundary} boundary verts duplicated");
                }
            }

            return bilateralCount + nFoldCount;
        }

        // ── UV0 overlap detection for V-shaped symmetry ──

        /// <summary>
        /// Find face pairs within a shell that overlap in UV0 space but don't share
        /// vertices. Uses grid rasterization similar to SpatialPartitioner.DetectOverlap.
        /// Returns list of (faceA, faceB) pairs.
        /// </summary>
        static List<(int, int)> DetectUv0OverlapPairs(UvShell shell, Vector2[] uv0, int[] tris)
        {
            var pairs = new List<(int, int)>();
            var faces = shell.faceIndices;

            // Build face vertex sets for adjacency check
            var faceVerts = new Dictionary<int, HashSet<int>>(faces.Count);
            foreach (int f in faces)
            {
                var set = new HashSet<int>();
                set.Add(tris[f * 3]);
                set.Add(tris[f * 3 + 1]);
                set.Add(tris[f * 3 + 2]);
                faceVerts[f] = set;
            }

            // Grid rasterization of face UV0 bboxes
            const int kGridRes = 64;
            float rangeX = shell.boundsMax.x - shell.boundsMin.x;
            float rangeY = shell.boundsMax.y - shell.boundsMin.y;
            if (rangeX < 1e-8f || rangeY < 1e-8f) return pairs;

            float invX = kGridRes / rangeX;
            float invY = kGridRes / rangeY;
            float bMinX = shell.boundsMin.x;
            float bMinY = shell.boundsMin.y;

            var cellFaces = new Dictionary<long, List<int>>();

            foreach (int f in faces)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                if (i0 >= uv0.Length || i1 >= uv0.Length || i2 >= uv0.Length) continue;
                Vector2 a = uv0[i0], b = uv0[i1], c = uv0[i2];

                int gxMin = Mathf.Clamp((int)((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - bMinX) * invX), 0, kGridRes - 1);
                int gxMax = Mathf.Clamp((int)((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - bMinX) * invX), 0, kGridRes - 1);
                int gyMin = Mathf.Clamp((int)((Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - bMinY) * invY), 0, kGridRes - 1);
                int gyMax = Mathf.Clamp((int)((Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - bMinY) * invY), 0, kGridRes - 1);

                for (int gy = gyMin; gy <= gyMax; gy++)
                for (int gx = gxMin; gx <= gxMax; gx++)
                {
                    long key = (long)gy * kGridRes + gx;
                    if (!cellFaces.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<int>(2);
                        cellFaces[key] = bucket;
                    }
                    bucket.Add(f);
                }
            }

            // Find non-adjacent face pairs sharing a grid cell,
            // then confirm with actual 2D triangle-triangle overlap test.
            var seen = new HashSet<long>(); // dedup pairs
            foreach (var kv in cellFaces)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;

                for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    int fA = list[i], fB = list[j];
                    // Skip adjacent faces (they share at least one vertex)
                    bool adjacent = false;
                    foreach (int v in faceVerts[fA])
                        if (faceVerts[fB].Contains(v)) { adjacent = true; break; }
                    if (adjacent) continue;

                    long pairKey = fA < fB ? ((long)fA << 32) | (uint)fB : ((long)fB << 32) | (uint)fA;
                    if (!seen.Add(pairKey)) continue;

                    // Actual 2D triangle overlap test to avoid false positives
                    // from bbox-only grid co-location on dense meshes.
                    Vector2 a0 = uv0[tris[fA * 3]], a1 = uv0[tris[fA * 3 + 1]], a2 = uv0[tris[fA * 3 + 2]];
                    Vector2 b0 = uv0[tris[fB * 3]], b1 = uv0[tris[fB * 3 + 1]], b2 = uv0[tris[fB * 3 + 2]];
                    if (TrianglesOverlap2D(a0, a1, a2, b0, b1, b2))
                        pairs.Add((fA, fB));
                }
            }

            return pairs;
        }

        // ── 2D triangle overlap test ──

        /// <summary>
        /// Returns true if two 2D triangles overlap (share interior area).
        /// Uses separating-axis theorem (SAT) on the 6 edge normals.
        /// </summary>
        static bool TrianglesOverlap2D(Vector2 a0, Vector2 a1, Vector2 a2,
                                        Vector2 b0, Vector2 b1, Vector2 b2)
        {
            // Test all 6 edge normals as separating axes (3 per triangle).
            // If any axis separates the projections, triangles don't overlap.
            if (SeparatedOnAxis(a0, a1, a2, b0, b1, b2, a1 - a0)) return false;
            if (SeparatedOnAxis(a0, a1, a2, b0, b1, b2, a2 - a1)) return false;
            if (SeparatedOnAxis(a0, a1, a2, b0, b1, b2, a0 - a2)) return false;
            if (SeparatedOnAxis(a0, a1, a2, b0, b1, b2, b1 - b0)) return false;
            if (SeparatedOnAxis(a0, a1, a2, b0, b1, b2, b2 - b1)) return false;
            if (SeparatedOnAxis(a0, a1, a2, b0, b1, b2, b0 - b2)) return false;
            return true;
        }

        /// <summary>
        /// Check if projections of two triangles onto the perpendicular of 'edge' are separated.
        /// </summary>
        static bool SeparatedOnAxis(Vector2 a0, Vector2 a1, Vector2 a2,
                                     Vector2 b0, Vector2 b1, Vector2 b2,
                                     Vector2 edge)
        {
            // Perpendicular (normal) of the edge
            float nx = -edge.y, ny = edge.x;

            // Project all 6 vertices onto the axis
            float pa0 = a0.x * nx + a0.y * ny;
            float pa1 = a1.x * nx + a1.y * ny;
            float pa2 = a2.x * nx + a2.y * ny;
            float pb0 = b0.x * nx + b0.y * ny;
            float pb1 = b1.x * nx + b1.y * ny;
            float pb2 = b2.x * nx + b2.y * ny;

            float aMin = Mathf.Min(pa0, Mathf.Min(pa1, pa2));
            float aMax = Mathf.Max(pa0, Mathf.Max(pa1, pa2));
            float bMin = Mathf.Min(pb0, Mathf.Min(pb1, pb2));
            float bMax = Mathf.Max(pb0, Mathf.Max(pb1, pb2));

            // Separated if intervals don't overlap (with small epsilon for edge-touching)
            const float eps = 1e-6f;
            return aMax <= bMin + eps || bMax <= aMin + eps;
        }

        // ── N-fold rotational symmetry detection (diagnostic) ──

        internal static int DetectRotationalSymmetry(
            UvShell shell, Vector3[] verts, Vector2[] uv0, int[] tris)
        {
            var faces = shell.faceIndices;
            if (faces.Count < 6) return 0;

            Vector3 center = Vector3.zero;
            foreach (int f in faces)
            {
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                center += (verts[v0] + verts[v1] + verts[v2]) / 3f;
            }
            center /= faces.Count;

            float cxx = 0, cyy = 0, czz = 0;
            foreach (int f in faces)
            {
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                Vector3 fc = (verts[v0] + verts[v1] + verts[v2]) / 3f - center;
                cxx += fc.x * fc.x; cyy += fc.y * fc.y; czz += fc.z * fc.z;
            }

            int rotAxis;
            if (cxx <= cyy && cxx <= czz) rotAxis = 0;
            else if (cyy <= czz) rotAxis = 1;
            else rotAxis = 2;

            const int kSamples = 16;
            float uvW = shell.boundsMax.x - shell.boundsMin.x;
            float uvH = shell.boundsMax.y - shell.boundsMin.y;
            if (uvW < 1e-6f || uvH < 1e-6f) return 0;

            var uvCentroids = new Vector2[faces.Count];
            for (int i = 0; i < faces.Count; i++)
            {
                int f = faces[i];
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                uvCentroids[i] = (uv0[v0] + uv0[v1] + uv0[v2]) / 3f;
            }

            float cellW = uvW / kSamples, cellH = uvH / kSamples;
            int maxLayers = 0, layerSum = 0, layerCells = 0;
            for (int sy = 0; sy < kSamples; sy++)
            for (int sx = 0; sx < kSamples; sx++)
            {
                float cx = shell.boundsMin.x + (sx + 0.5f) * cellW;
                float cy = shell.boundsMin.y + (sy + 0.5f) * cellH;
                int count = 0;
                for (int i = 0; i < uvCentroids.Length; i++)
                    if (Mathf.Abs(uvCentroids[i].x - cx) < cellW &&
                        Mathf.Abs(uvCentroids[i].y - cy) < cellH)
                        count++;
                if (count > 1) { if (count > maxLayers) maxLayers = count; layerSum += count; layerCells++; }
            }

            if (layerCells < kSamples) return 0;
            int nFold = Mathf.RoundToInt((float)layerSum / layerCells);
            if (nFold > 2)
            {
                UvtLog.Info($"[SymSplit] Shell: detected {nFold}-fold rotational symmetry " +
                    $"(axis={AxisName(rotAxis)}, maxLayers={maxLayers}, {faces.Count} faces)");
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
