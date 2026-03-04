// SpatialPartitioner.cs — Spatial partition for overlapping UV0 shells
//
// Approach A: Flood-fill mesh adjacency → connected components = partitions.
//             Works when overlapping UV0 faces belong to different connected
//             components (front/back of belt are separate polygon groups).
//
// Approach B: When flood-fill returns one partition for a shell with overlap,
//             force-split by 3D proximity (K-means k=2 on overlapping face
//             centroids, propagate to all faces via adjacency).

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class SpatialPartitioner
    {
        public class ShellPartitionResult
        {
            public int shellId;
            public bool hasOverlap;
            public int partitionCount; // 1 = no split, >1 = partitions available
            public Dictionary<int, int> facePartitionId; // globalFaceIndex → partitionId (only shell faces)
            public Vector3[] partitionCentroid; // 3D centroid per partition
        }

        const int kMinFacesForPartition = 4;
        const int kGridResolution = 128;

        /// <summary>
        /// Partition source shells by detecting UV0 overlap and splitting via
        /// flood-fill (Approach A) then forced 3D split (Approach B) if needed.
        /// </summary>
        public static ShellPartitionResult[] PartitionShells(
            List<UvShell> shells,
            Vector2[] uv0, int[] triangles, Vector3[] vertices)
        {
            var results = new ShellPartitionResult[shells.Count];

            for (int si = 0; si < shells.Count; si++)
            {
                var shell = shells[si];
                var r = new ShellPartitionResult
                {
                    shellId = si,
                    hasOverlap = false,
                    partitionCount = 1,
                    facePartitionId = new Dictionary<int, int>()
                };

                // Default: all faces in partition 0
                foreach (int f in shell.faceIndices)
                    r.facePartitionId[f] = 0;

                // Skip tiny shells
                if (shell.faceIndices.Count < kMinFacesForPartition)
                {
                    r.partitionCentroid = new[] { ComputePartitionCentroid(shell.faceIndices, triangles, vertices) };
                    results[si] = r;
                    continue;
                }

                // Step 1: Build face adjacency (needed for both overlap detection and flood-fill)
                var faceVerts = BuildFaceVertexSets(shell.faceIndices, triangles);
                var adjacency = BuildFaceAdjacency(shell.faceIndices, triangles);

                // Step 2: Overlap detection — only flag faces sharing a grid cell
                // with a NON-ADJACENT face (no shared vertex)
                var overlappingFaces = DetectOverlap(shell, uv0, triangles, faceVerts);
                r.hasOverlap = overlappingFaces.Count > 0;

                if (!r.hasOverlap)
                {
                    r.partitionCentroid = new[] { ComputePartitionCentroid(shell.faceIndices, triangles, vertices) };
                    results[si] = r;
                    continue;
                }

                UvtLog.Verbose($"[SpatialPartitioner] Shell {si}: overlap detected " +
                    $"({overlappingFaces.Count} faces in overlap zones)");

                // Step 3: Flood-fill → connected components (Approach A)
                var components = FloodFillComponents(shell.faceIndices, adjacency);

                if (components.Count > 1)
                {
                    r.partitionCount = components.Count;
                    r.partitionCentroid = new Vector3[components.Count];
                    for (int ci = 0; ci < components.Count; ci++)
                    {
                        foreach (int f in components[ci])
                            r.facePartitionId[f] = ci;
                        r.partitionCentroid[ci] = ComputePartitionCentroid(
                            components[ci], triangles, vertices);
                    }

                    UvtLog.Verbose($"[SpatialPartitioner] Shell {si}: flood-fill → " +
                        $"{components.Count} partitions ({PartitionSizes(components)} faces)");
                }
                else
                {
                    UvtLog.Verbose($"[SpatialPartitioner] Shell {si}: flood-fill → " +
                        $"1 partition (connected mesh)");

                    // Step 4: Approach B — forced split by 3D proximity
                    var split = ForceSplitByProximity(
                        shell, overlappingFaces, adjacency, triangles, vertices);

                    if (split != null && split.Count == 2)
                    {
                        r.partitionCount = 2;
                        r.partitionCentroid = new Vector3[2];
                        for (int ci = 0; ci < 2; ci++)
                        {
                            foreach (int f in split[ci])
                                r.facePartitionId[f] = ci;
                            r.partitionCentroid[ci] = ComputePartitionCentroid(
                                split[ci], triangles, vertices);
                        }

                        UvtLog.Verbose($"[SpatialPartitioner] Shell {si}: forced 3D split → " +
                            $"2 partitions ({split[0].Count} + {split[1].Count} faces)");
                    }
                    else
                    {
                        r.partitionCentroid = new[] {
                            ComputePartitionCentroid(shell.faceIndices, triangles, vertices) };
                        UvtLog.Verbose($"[SpatialPartitioner] Shell {si}: " +
                            $"forced split failed, staying as 1 partition");
                    }
                }

                results[si] = r;
            }

            return results;
        }

        /// <summary>
        /// Find the best-matching source partition for a target shell by 3D centroid.
        /// Returns partition index, or -1 if no partitions available.
        /// </summary>
        public static int MatchPartition(ShellPartitionResult srcPartition, Vector3 targetCentroid3D)
        {
            if (srcPartition == null || srcPartition.partitionCount <= 1)
                return -1;

            int bestPart = 0;
            float bestDistSq = float.MaxValue;
            for (int pi = 0; pi < srcPartition.partitionCount; pi++)
            {
                float dSq = (srcPartition.partitionCentroid[pi] - targetCentroid3D).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestPart = pi;
                }
            }
            return bestPart;
        }

        /// <summary>
        /// Collect global face indices belonging to a specific partition.
        /// </summary>
        public static int[] GetPartitionFaces(
            UvShell shell, ShellPartitionResult partResult, int partitionId)
        {
            var faces = new List<int>();
            foreach (int f in shell.faceIndices)
            {
                if (partResult.facePartitionId.TryGetValue(f, out int pid) && pid == partitionId)
                    faces.Add(f);
            }
            return faces.ToArray();
        }

        // ════════════════════════════════════════════════════════════
        //  Per-face vertex set (for fast adjacency check in overlap detection)
        // ════════════════════════════════════════════════════════════

        static Dictionary<int, HashSet<int>> BuildFaceVertexSets(List<int> faceIndices, int[] triangles)
        {
            var result = new Dictionary<int, HashSet<int>>(faceIndices.Count);
            foreach (int f in faceIndices)
            {
                var set = new HashSet<int>();
                set.Add(triangles[f * 3]);
                set.Add(triangles[f * 3 + 1]);
                set.Add(triangles[f * 3 + 2]);
                result[f] = set;
            }
            return result;
        }

        static bool FacesShareVertex(Dictionary<int, HashSet<int>> faceVerts, int fA, int fB)
        {
            if (!faceVerts.TryGetValue(fA, out var setA)) return false;
            if (!faceVerts.TryGetValue(fB, out var setB)) return false;
            foreach (int v in setA)
                if (setB.Contains(v)) return true;
            return false;
        }

        // ════════════════════════════════════════════════════════════
        //  Overlap detection: grid rasterization + vertex-sharing filter
        //  Only flags faces sharing a cell with a NON-ADJACENT face.
        // ════════════════════════════════════════════════════════════

        static HashSet<int> DetectOverlap(
            UvShell shell, Vector2[] uv0, int[] triangles,
            Dictionary<int, HashSet<int>> faceVerts)
        {
            var overlapping = new HashSet<int>();

            Vector2 bMin = shell.boundsMin;
            Vector2 bMax = shell.boundsMax;
            float rangeX = bMax.x - bMin.x;
            float rangeY = bMax.y - bMin.y;
            if (rangeX < 1e-8f || rangeY < 1e-8f) return overlapping;

            int gridRes = kGridResolution;
            float invX = gridRes / rangeX;
            float invY = gridRes / rangeY;

            // Rasterize face AABBs onto grid (sparse dictionary)
            var cellFaces = new Dictionary<long, List<int>>();

            foreach (int f in shell.faceIndices)
            {
                int i0 = triangles[f * 3], i1 = triangles[f * 3 + 1], i2 = triangles[f * 3 + 2];
                if (i0 >= uv0.Length || i1 >= uv0.Length || i2 >= uv0.Length) continue;

                Vector2 a = uv0[i0], b = uv0[i1], c = uv0[i2];

                int gxMin = Mathf.Clamp((int)((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - bMin.x) * invX), 0, gridRes - 1);
                int gxMax = Mathf.Clamp((int)((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - bMin.x) * invX), 0, gridRes - 1);
                int gyMin = Mathf.Clamp((int)((Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - bMin.y) * invY), 0, gridRes - 1);
                int gyMax = Mathf.Clamp((int)((Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - bMin.y) * invY), 0, gridRes - 1);

                for (int gy = gyMin; gy <= gyMax; gy++)
                {
                    for (int gx = gxMin; gx <= gxMax; gx++)
                    {
                        long key = (long)gy * gridRes + gx;
                        if (!cellFaces.TryGetValue(key, out var list))
                        {
                            list = new List<int>(2);
                            cellFaces[key] = list;
                        }
                        list.Add(f);
                    }
                }
            }

            // For each cell with >1 face, check if any pair shares NO vertex.
            // Only those non-adjacent pairs indicate true UV0 overlap.
            foreach (var kv in cellFaces)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;

                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (!FacesShareVertex(faceVerts, list[i], list[j]))
                        {
                            overlapping.Add(list[i]);
                            overlapping.Add(list[j]);
                        }
                    }
                }
            }

            return overlapping;
        }

        // ════════════════════════════════════════════════════════════
        //  Face adjacency graph: two faces adjacent if they share an edge
        // ════════════════════════════════════════════════════════════

        static Dictionary<int, List<int>> BuildFaceAdjacency(
            List<int> faceIndices, int[] triangles)
        {
            var adjacency = new Dictionary<int, List<int>>(faceIndices.Count);
            var edgeToFace = new Dictionary<long, int>(faceIndices.Count * 3);

            foreach (int f in faceIndices)
            {
                adjacency[f] = new List<int>(3);

                int i0 = triangles[f * 3], i1 = triangles[f * 3 + 1], i2 = triangles[f * 3 + 2];
                TryAddEdge(edgeToFace, adjacency, i0, i1, f);
                TryAddEdge(edgeToFace, adjacency, i1, i2, f);
                TryAddEdge(edgeToFace, adjacency, i2, i0, f);
            }

            return adjacency;
        }

        static void TryAddEdge(Dictionary<long, int> edgeToFace,
            Dictionary<int, List<int>> adjacency, int v0, int v1, int face)
        {
            long key = v0 < v1 ? ((long)v0 << 32) | (uint)v1 : ((long)v1 << 32) | (uint)v0;

            if (edgeToFace.TryGetValue(key, out int otherFace))
            {
                adjacency[face].Add(otherFace);
                if (adjacency.ContainsKey(otherFace))
                    adjacency[otherFace].Add(face);
            }
            else
            {
                edgeToFace[key] = face;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Flood-fill → connected components (Approach A)
        // ════════════════════════════════════════════════════════════

        static List<List<int>> FloodFillComponents(
            List<int> faceIndices, Dictionary<int, List<int>> adjacency)
        {
            var visited = new HashSet<int>(faceIndices.Count);
            var components = new List<List<int>>();

            foreach (int startFace in faceIndices)
            {
                if (visited.Contains(startFace)) continue;

                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(startFace);
                visited.Add(startFace);

                while (queue.Count > 0)
                {
                    int f = queue.Dequeue();
                    component.Add(f);

                    if (adjacency.TryGetValue(f, out var neighbors))
                    {
                        foreach (int n in neighbors)
                        {
                            if (!visited.Contains(n))
                            {
                                visited.Add(n);
                                queue.Enqueue(n);
                            }
                        }
                    }
                }

                components.Add(component);
            }

            return components;
        }

        // ════════════════════════════════════════════════════════════
        //  Forced split by 3D proximity (Approach B)
        //  K-means (k=2) on overlapping face 3D centroids, then
        //  propagate to non-overlapping faces via adjacency.
        // ════════════════════════════════════════════════════════════

        static List<List<int>> ForceSplitByProximity(
            UvShell shell, HashSet<int> overlappingFaces,
            Dictionary<int, List<int>> adjacency,
            int[] triangles, Vector3[] vertices)
        {
            if (overlappingFaces.Count < 2) return null;

            // Compute 3D centroid for each overlapping face
            var faceCentroids = new Dictionary<int, Vector3>();
            foreach (int f in overlappingFaces)
            {
                int i0 = triangles[f * 3], i1 = triangles[f * 3 + 1], i2 = triangles[f * 3 + 2];
                if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length)
                    continue;
                faceCentroids[f] = (vertices[i0] + vertices[i1] + vertices[i2]) / 3f;
            }

            if (faceCentroids.Count < 2) return null;

            // K-means k=2: initialize with most-distant pair
            var faceList = new List<int>(faceCentroids.Keys);
            Vector3 c0 = faceCentroids[faceList[0]];
            Vector3 c1 = c0;
            float maxDist = -1f;
            foreach (int f in faceList)
            {
                float d = (faceCentroids[f] - c0).sqrMagnitude;
                if (d > maxDist) { maxDist = d; c1 = faceCentroids[f]; }
            }

            if (maxDist < 1e-10f) return null;

            // Iterate
            var assignment = new Dictionary<int, int>(faceList.Count);
            for (int iter = 0; iter < 20; iter++)
            {
                bool changed = false;
                foreach (int f in faceList)
                {
                    int cl = (faceCentroids[f] - c0).sqrMagnitude
                          <= (faceCentroids[f] - c1).sqrMagnitude ? 0 : 1;
                    if (!assignment.TryGetValue(f, out int old) || old != cl)
                    {
                        assignment[f] = cl;
                        changed = true;
                    }
                }

                if (!changed && iter > 0) break;

                Vector3 s0 = Vector3.zero, s1 = Vector3.zero;
                int n0 = 0, n1 = 0;
                foreach (int f in faceList)
                {
                    if (assignment[f] == 0) { s0 += faceCentroids[f]; n0++; }
                    else { s1 += faceCentroids[f]; n1++; }
                }

                if (n0 > 0) c0 = s0 / n0;
                if (n1 > 0) c1 = s1 / n1;
            }

            // Check both clusters non-empty
            int cnt0 = 0, cnt1 = 0;
            foreach (var kv in assignment)
            {
                if (kv.Value == 0) cnt0++;
                else cnt1++;
            }
            if (cnt0 == 0 || cnt1 == 0) return null;

            // Propagate via adjacency BFS
            var faceCluster = new Dictionary<int, int>(assignment);
            var queue = new Queue<int>();
            foreach (var kv in assignment)
                queue.Enqueue(kv.Key);

            while (queue.Count > 0)
            {
                int f = queue.Dequeue();
                int cl = faceCluster[f];
                if (adjacency.TryGetValue(f, out var neighbors))
                {
                    foreach (int n in neighbors)
                    {
                        if (!faceCluster.ContainsKey(n))
                        {
                            faceCluster[n] = cl;
                            queue.Enqueue(n);
                        }
                    }
                }
            }

            // Build partition lists
            var part0 = new List<int>();
            var part1 = new List<int>();
            foreach (int f in shell.faceIndices)
            {
                if (faceCluster.TryGetValue(f, out int cl))
                {
                    if (cl == 0) part0.Add(f);
                    else part1.Add(f);
                }
                else
                {
                    int i0 = triangles[f * 3], i1 = triangles[f * 3 + 1], i2 = triangles[f * 3 + 2];
                    if (i0 < vertices.Length && i1 < vertices.Length && i2 < vertices.Length)
                    {
                        Vector3 fc = (vertices[i0] + vertices[i1] + vertices[i2]) / 3f;
                        if ((fc - c0).sqrMagnitude <= (fc - c1).sqrMagnitude)
                            part0.Add(f);
                        else
                            part1.Add(f);
                    }
                    else
                    {
                        part0.Add(f);
                    }
                }
            }

            if (part0.Count == 0 || part1.Count == 0) return null;
            return new List<List<int>> { part0, part1 };
        }

        // ════════════════════════════════════════════════════════════
        //  Helpers
        // ════════════════════════════════════════════════════════════

        static Vector3 ComputePartitionCentroid(
            List<int> faceIndices, int[] triangles, Vector3[] vertices)
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (int f in faceIndices)
            {
                int i0 = triangles[f * 3], i1 = triangles[f * 3 + 1], i2 = triangles[f * 3 + 2];
                if (i0 < vertices.Length && i1 < vertices.Length && i2 < vertices.Length)
                {
                    sum += (vertices[i0] + vertices[i1] + vertices[i2]) / 3f;
                    count++;
                }
            }
            return count > 0 ? sum / count : Vector3.zero;
        }

        static string PartitionSizes(List<List<int>> components)
        {
            var parts = new string[components.Count];
            for (int i = 0; i < components.Count; i++)
                parts[i] = components[i].Count.ToString();
            return string.Join(" + ", parts);
        }
    }
}
