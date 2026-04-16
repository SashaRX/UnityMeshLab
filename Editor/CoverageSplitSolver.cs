// CoverageSplitSolver.cs — Stage 2c: Reverse-projection coverage split
//
// After shell assignment (Stage 2), project each target triangle BACK onto LOD0.
// If a contiguous region of a target shell has NO coverage on LOD0 (no source
// shell contains matching geometry), detach that region as a separate
// "uncovered fragment" — it gets shell assignment -1 (Rejected) so downstream
// stages can handle it via merged/3D fallback instead of wrong UV interpolation.
//
// This catches cases where LOD simplification adds geometry that doesn't exist
// on LOD0, or where a target shell partially overlaps multiple source shells
// with poor coverage on some parts.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class CoverageSplitSolver
    {
        public struct Settings
        {
            /// <summary>Max distance for reverse projection (target→source).</summary>
            public float maxReverseProjectionDistance;

            /// <summary>Min normal dot product for reverse projection hit.</summary>
            public float minNormalDot;

            /// <summary>
            /// Minimum fraction of a target shell's triangles that must be uncovered
            /// before we consider splitting. Prevents splitting over tiny noise.
            /// </summary>
            public float minUncoveredFraction;

            /// <summary>
            /// Minimum number of connected uncovered triangles to form a detached fragment.
            /// Isolated single triangles are reassigned to the nearest covered neighbor instead.
            /// </summary>
            public int minFragmentSize;

            public static Settings Default => new Settings
            {
                maxReverseProjectionDistance = 0.5f,
                minNormalDot = 0.5f,  // ~60 degrees
                minUncoveredFraction = 0.05f,
                minFragmentSize = 2
            };
        }

        public struct SplitReport
        {
            public int shellsAnalyzed;
            public int shellsSplit;
            public int trianglesDetached;
            public int isolatesReassigned;
        }

        /// <summary>
        /// Analyze coverage of each target triangle by reverse-projecting to LOD0.
        /// Triangles that have no coverage are detached (assignment set to -1).
        /// Small isolated uncovered triangles are reassigned to their neighbors.
        ///
        /// Call AFTER ShellAssignmentSolver.Solve, BEFORE InitialUvTransferSolver.Solve.
        /// </summary>
        public static SplitReport Solve(
            SourceMeshData source,
            TargetTransferState target,
            Settings settings)
        {
            var report = new SplitReport();
            float maxDistSq = settings.maxReverseProjectionDistance * settings.maxReverseProjectionDistance;

            // ── Step 1: Per-triangle reverse coverage check ──
            // For each assigned target triangle, project its centroid back to LOD0.
            // Check if the nearest source surface point belongs to the SAME shell
            // that was assigned. If not → uncovered.
            var isCovered = new bool[target.faceCount];

            for (int f = 0; f < target.faceCount; f++)
            {
                int assignedShell = target.triangleShellAssignments[f];
                if (assignedShell < 0)
                {
                    // Already unassigned — skip
                    continue;
                }

                int ti0 = target.triangles[f * 3];
                int ti1 = target.triangles[f * 3 + 1];
                int ti2 = target.triangles[f * 3 + 2];

                // Use 3 points: centroid + 2 vertices for better coverage
                Vector3 v0 = target.vertices[ti0];
                Vector3 v1 = target.vertices[ti1];
                Vector3 v2 = target.vertices[ti2];
                Vector3 centroid = (v0 + v1 + v2) / 3f;
                Vector3 nrm = ((target.normals[ti0] + target.normals[ti1] +
                                target.normals[ti2]) / 3f).normalized;

                // Check centroid + vertices: if ANY hit the assigned shell, it's covered
                bool anyCovered = CheckPointCoverage(
                    centroid, nrm, assignedShell, source, maxDistSq, settings.minNormalDot);

                if (!anyCovered)
                {
                    // Try vertices as well before declaring uncovered
                    anyCovered = CheckPointCoverage(
                        v0, target.normals[ti0], assignedShell, source,
                        maxDistSq, settings.minNormalDot);
                }

                if (!anyCovered)
                {
                    anyCovered = CheckPointCoverage(
                        v1, target.normals[ti1], assignedShell, source,
                        maxDistSq, settings.minNormalDot);
                }

                if (!anyCovered)
                {
                    anyCovered = CheckPointCoverage(
                        v2, target.normals[ti2], assignedShell, source,
                        maxDistSq, settings.minNormalDot);
                }

                isCovered[f] = anyCovered;
            }

            // ── Step 2: Group target triangles by assigned shell ──
            var shellToFaces = new Dictionary<int, List<int>>();
            for (int f = 0; f < target.faceCount; f++)
            {
                int shell = target.triangleShellAssignments[f];
                if (shell < 0) continue;
                if (!shellToFaces.TryGetValue(shell, out var list))
                {
                    list = new List<int>();
                    shellToFaces[shell] = list;
                }
                list.Add(f);
            }

            // ── Step 3: Build face adjacency (by shared edge in position space) ──
            var faceNeighbors = BuildFaceAdjacency(target);

            // ── Step 4: Per-shell analysis and split ──
            foreach (var kv in shellToFaces)
            {
                int shellId = kv.Key;
                var faces = kv.Value;
                report.shellsAnalyzed++;

                // Count uncovered
                int uncoveredCount = 0;
                foreach (int f in faces)
                    if (!isCovered[f]) uncoveredCount++;

                if (uncoveredCount == 0) continue;

                float uncoveredFraction = (float)uncoveredCount / faces.Count;

                // Skip if below threshold — too few uncovered triangles to matter
                if (uncoveredFraction < settings.minUncoveredFraction) continue;

                // If ALL triangles are uncovered, don't split — the whole shell
                // is unmatched and should be handled as-is
                if (uncoveredCount == faces.Count) continue;

                // ── Find connected components of uncovered triangles ──
                var uncoveredSet = new HashSet<int>();
                foreach (int f in faces)
                    if (!isCovered[f]) uncoveredSet.Add(f);

                var components = FindConnectedComponents(uncoveredSet, faceNeighbors);

                int detachedTotal = 0;
                int reassignedIsolates = 0;

                foreach (var component in components)
                {
                    if (component.Count < settings.minFragmentSize)
                    {
                        // Small isolated group → reassign to nearest covered neighbor
                        foreach (int f in component)
                        {
                            int neighborShell = FindCoveredNeighborShell(
                                f, faceNeighbors, target, isCovered);
                            if (neighborShell >= 0)
                            {
                                target.triangleShellAssignments[f] = neighborShell;
                                isCovered[f] = true; // treat as covered after reassignment
                                reassignedIsolates++;
                            }
                        }
                    }
                    else
                    {
                        // Large enough fragment → detach (set assignment to -1)
                        foreach (int f in component)
                        {
                            target.triangleShellAssignments[f] = -1;
                            target.triangleStatus[f] = TriangleStatus.Rejected;
                            detachedTotal++;
                        }
                    }
                }

                if (detachedTotal > 0 || reassignedIsolates > 0)
                {
                    report.shellsSplit++;
                    report.trianglesDetached += detachedTotal;
                    report.isolatesReassigned += reassignedIsolates;

                    UvtLog.Verbose($"[CoverageSplit] Shell {shellId}: " +
                        $"detached {detachedTotal} tris ({components.Count} components), " +
                        $"reassigned {reassignedIsolates} isolates");
                }
            }

            if (report.shellsSplit > 0)
            {
                UvtLog.Info($"[CoverageSplit] Split {report.shellsSplit} shells: " +
                    $"{report.trianglesDetached} tris detached, " +
                    $"{report.isolatesReassigned} isolates reassigned");
            }
            else
            {
                UvtLog.Verbose("[CoverageSplit] No shells needed splitting — all coverage OK");
            }

            return report;
        }

        // ─── Helpers ───

        /// <summary>
        /// Check if a 3D point projects back onto the assigned source shell.
        /// Returns true if the nearest source triangle is in the expected shell
        /// AND passes normal/distance filters.
        /// </summary>
        static bool CheckPointCoverage(
            Vector3 point, Vector3 normal, int expectedShellId,
            SourceMeshData source, float maxDistSq, float minNormalDot)
        {
            var hit = source.bvh.FindNearest(point);
            if (hit.triangleIndex < 0) return false;
            if (hit.distSq > maxDistSq) return false;

            // Normal check
            int si0 = source.triangles[hit.triangleIndex * 3];
            int si1 = source.triangles[hit.triangleIndex * 3 + 1];
            int si2 = source.triangles[hit.triangleIndex * 3 + 2];
            Vector3 srcNormal = (source.normals[si0] * hit.barycentric.x +
                                 source.normals[si1] * hit.barycentric.y +
                                 source.normals[si2] * hit.barycentric.z).normalized;
            float dot = Vector3.Dot(normal, srcNormal);
            if (dot < minNormalDot) return false;

            // Check shell match — the key test
            int hitShell = source.triangleToShellId[hit.triangleIndex];
            if (hitShell == expectedShellId) return true;

            // Not nearest shell — but maybe the assigned shell is close enough.
            // Do a targeted search: find nearest triangle IN the expected shell.
            if (expectedShellId >= 0 && expectedShellId < source.uvShells.Count)
            {
                float bestDistSq = float.MaxValue;
                foreach (int sf in source.uvShells[expectedShellId].triangleIds)
                {
                    int i0 = source.triangles[sf * 3];
                    int i1 = source.triangles[sf * 3 + 1];
                    int i2 = source.triangles[sf * 3 + 2];

                    var cp = TriangleBvh.ClosestPointOnTriangle(
                        point, source.vertices[i0], source.vertices[i1], source.vertices[i2],
                        out Vector3 bary);
                    float dSq = (cp - point).sqrMagnitude;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        if (dSq <= maxDistSq)
                        {
                            // Also check normal at this point
                            Vector3 sn = (source.normals[i0] * bary.x +
                                          source.normals[i1] * bary.y +
                                          source.normals[i2] * bary.z).normalized;
                            if (Vector3.Dot(normal, sn) >= minNormalDot)
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Build face adjacency map: face → list of neighbor faces (by shared position-space edge).
        /// Uses position-based edge keys so it works regardless of UV splits.
        /// </summary>
        static List<int>[] BuildFaceAdjacency(TargetTransferState target)
        {
            var edgeToFaces = new Dictionary<long, List<int>>();

            for (int f = 0; f < target.faceCount; f++)
            {
                for (int e = 0; e < 3; e++)
                {
                    int v0 = target.triangles[f * 3 + e];
                    int v1 = target.triangles[f * 3 + (e + 1) % 3];
                    long key = BorderPrimitiveDetector.PackEdge(v0, v1);
                    if (!edgeToFaces.TryGetValue(key, out var list))
                    {
                        list = new List<int>(2);
                        edgeToFaces[key] = list;
                    }
                    list.Add(f);
                }
            }

            var neighbors = new List<int>[target.faceCount];
            for (int f = 0; f < target.faceCount; f++)
                neighbors[f] = new List<int>();

            foreach (var kv in edgeToFaces)
            {
                var faces = kv.Value;
                for (int i = 0; i < faces.Count; i++)
                    for (int j = i + 1; j < faces.Count; j++)
                    {
                        neighbors[faces[i]].Add(faces[j]);
                        neighbors[faces[j]].Add(faces[i]);
                    }
            }

            return neighbors;
        }

        /// <summary>
        /// Find connected components within a set of face indices using flood fill.
        /// </summary>
        static List<List<int>> FindConnectedComponents(
            HashSet<int> faceSet, List<int>[] adjacency)
        {
            var components = new List<List<int>>();
            var visited = new HashSet<int>();

            foreach (int seed in faceSet)
            {
                if (visited.Contains(seed)) continue;

                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(seed);
                visited.Add(seed);

                while (queue.Count > 0)
                {
                    int f = queue.Dequeue();
                    component.Add(f);

                    foreach (int n in adjacency[f])
                    {
                        if (faceSet.Contains(n) && !visited.Contains(n))
                        {
                            visited.Add(n);
                            queue.Enqueue(n);
                        }
                    }
                }

                components.Add(component);
            }

            return components;
        }

        /// <summary>
        /// For a small uncovered triangle, find the shell assignment of its
        /// nearest covered neighbor. Returns -1 if no covered neighbor found.
        /// </summary>
        static int FindCoveredNeighborShell(
            int faceId, List<int>[] adjacency,
            TargetTransferState target, bool[] isCovered)
        {
            // BFS with depth limit of 2 to find nearest covered neighbor
            var visited = new HashSet<int> { faceId };
            var queue = new Queue<(int face, int depth)>();
            queue.Enqueue((faceId, 0));

            while (queue.Count > 0)
            {
                var (f, depth) = queue.Dequeue();
                if (depth > 0 && isCovered[f] && target.triangleShellAssignments[f] >= 0)
                    return target.triangleShellAssignments[f];

                if (depth >= 2) continue;

                foreach (int n in adjacency[f])
                {
                    if (!visited.Contains(n))
                    {
                        visited.Add(n);
                        queue.Enqueue((n, depth + 1));
                    }
                }
            }

            return -1;
        }

        // ═══════════════════════════════════════════════════════════════
        //  GroupedShellTransfer integration
        //  Checks 3D coverage per target shell against matched source.
        //  Shells with poor 3D coverage get upgraded to merged mode.
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// For GroupedShellTransfer: compute per-shell 3D coverage fraction.
        /// For each target shell, checks what fraction of its face centroids
        /// project back to the matched source shell's 3D surface.
        ///
        /// Returns per-shell coverage [0..1]. Shells with coverage below
        /// threshold should be upgraded to merged mode.
        /// </summary>
        public static float[] ComputeShellCoverage3D(
            List<UvShell> tgtShells,
            Vector3[] tgtVerts, Vector3[] tgtNormals, int[] tgtTris,
            List<UvShell> srcShells,
            Vector3[] srcVerts, Vector3[] srcNormals, int[] srcTris,
            int[] targetShellToSourceShell,
            float maxDist, float minNormalDot)
        {
            float maxDistSq = maxDist * maxDist;
            var coverage = new float[tgtShells.Count];

            // Build a global source BVH for back-projection
            var globalBvh = new TriangleBvh(srcVerts, srcTris);

            // Per-face source shell lookup
            int srcFaceCount = srcTris.Length / 3;
            var srcFaceToShell = new int[srcFaceCount];
            for (int si = 0; si < srcShells.Count; si++)
                foreach (int f in srcShells[si].faceIndices)
                    srcFaceToShell[f] = si;

            for (int tsi = 0; tsi < tgtShells.Count; tsi++)
            {
                int matchedSrc = targetShellToSourceShell[tsi];
                if (matchedSrc < 0)
                {
                    coverage[tsi] = 0f;
                    continue;
                }

                var tShell = tgtShells[tsi];
                int coveredFaces = 0;
                int totalFaces = 0;

                foreach (int fi in tShell.faceIndices)
                {
                    int ti0 = tgtTris[fi * 3];
                    int ti1 = tgtTris[fi * 3 + 1];
                    int ti2 = tgtTris[fi * 3 + 2];
                    if (ti0 >= tgtVerts.Length || ti1 >= tgtVerts.Length || ti2 >= tgtVerts.Length)
                        continue;

                    totalFaces++;

                    // Check face centroid
                    Vector3 centroid = (tgtVerts[ti0] + tgtVerts[ti1] + tgtVerts[ti2]) / 3f;
                    Vector3 nrm = Vector3.zero;
                    if (tgtNormals != null && ti0 < tgtNormals.Length)
                        nrm = ((tgtNormals[ti0] + tgtNormals[ti1] + tgtNormals[ti2]) / 3f).normalized;

                    var hit = globalBvh.FindNearest(centroid);
                    if (hit.triangleIndex < 0 || hit.distSq > maxDistSq)
                        continue;

                    // Check normal
                    if (nrm.sqrMagnitude > 0.5f && srcNormals != null)
                    {
                        int si0 = srcTris[hit.triangleIndex * 3];
                        int si1 = srcTris[hit.triangleIndex * 3 + 1];
                        int si2 = srcTris[hit.triangleIndex * 3 + 2];
                        Vector3 srcN = (srcNormals[si0] * hit.barycentric.x +
                                        srcNormals[si1] * hit.barycentric.y +
                                        srcNormals[si2] * hit.barycentric.z).normalized;
                        float dot = Vector3.Dot(nrm, srcN);
                        if (dot < minNormalDot) continue;
                    }

                    // Verify that the nearest triangle belongs to the matched source shell
                    int hitShell = srcFaceToShell[hit.triangleIndex];
                    if (hitShell == matchedSrc)
                    {
                        coveredFaces++;
                    }
                }

                coverage[tsi] = totalFaces > 0 ? (float)coveredFaces / totalFaces : 0f;
            }

            return coverage;
        }

        /// <summary>
        /// Convenience: upgrade shells with poor 3D coverage to merged.
        /// Returns number of shells upgraded.
        /// </summary>
        public static int UpgradePoorCoverageToMerged(
            float[] coverage3D, bool[] isMerged,
            bool[] isFragmentMerged,
            float coverageThreshold = 0.7f)
        {
            int upgraded = 0;
            for (int tsi = 0; tsi < coverage3D.Length; tsi++)
            {
                if (isMerged[tsi]) continue; // already merged
                if (isFragmentMerged != null && tsi < isFragmentMerged.Length
                    && isFragmentMerged[tsi]) continue; // fragment-merged → skip

                if (coverage3D[tsi] < coverageThreshold && coverage3D[tsi] > 0f)
                {
                    isMerged[tsi] = true;
                    upgraded++;
                    UvtLog.Verbose($"[CoverageSplit] Shell t{tsi}: 3D coverage " +
                        $"{coverage3D[tsi]:P0} < {coverageThreshold:P0} → upgraded to merged");
                }
            }

            if (upgraded > 0)
                UvtLog.Info($"[CoverageSplit] Upgraded {upgraded} shell(s) to merged due to poor 3D coverage");

            return upgraded;
        }
    }
}

