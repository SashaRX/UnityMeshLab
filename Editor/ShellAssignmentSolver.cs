// ShellAssignmentSolver.cs — Stage 2: Transfer shell ID from source to target
// For each target triangle, determine which source UV shell it belongs to.
// Uses 7 sample points per triangle, BVH nearest-surface projection, majority vote.
// Does NOT transfer UV — only shell membership.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class ShellAssignmentSolver
    {
        public struct Settings
        {
            public float maxProjectionDistance;
            public float maxNormalAngle;       // degrees
            public bool filterBySubmesh;

            public static Settings Default => new Settings
            {
                maxProjectionDistance = 0.5f,
                maxNormalAngle = 80f,
                filterBySubmesh = true
            };
        }

        /// <summary>
        /// Assign source shell ID to each target triangle via surface projection.
        /// Populates: triangleShellAssignments, pointBindingsPerFace, triangleStatus (Ambiguous if needed).
        /// </summary>
        public static void Solve(SourceMeshData source, TargetTransferState target, Settings settings)
        {
            float cosLimit = Mathf.Cos(settings.maxNormalAngle * Mathf.Deg2Rad);

            for (int f = 0; f < target.faceCount; f++)
            {
                int ti0 = target.triangles[f * 3];
                int ti1 = target.triangles[f * 3 + 1];
                int ti2 = target.triangles[f * 3 + 2];

                Vector3 v0 = target.vertices[ti0];
                Vector3 v1 = target.vertices[ti1];
                Vector3 v2 = target.vertices[ti2];
                Vector3 n0 = target.normals[ti0];
                Vector3 n1 = target.normals[ti1];
                Vector3 n2 = target.normals[ti2];

                // 7 sample points: 3 vertices, 3 midpoints, 1 centroid
                var samples = new (Vector3 pos, Vector3 nrm, SampleType type, Vector3 bary)[]
                {
                    (v0, n0, SampleType.Vertex,   new Vector3(1,0,0)),
                    (v1, n1, SampleType.Vertex,   new Vector3(0,1,0)),
                    (v2, n2, SampleType.Vertex,   new Vector3(0,0,1)),
                    ((v0+v1)*0.5f, (n0+n1).normalized, SampleType.MidEdge, new Vector3(0.5f,0.5f,0)),
                    ((v1+v2)*0.5f, (n1+n2).normalized, SampleType.MidEdge, new Vector3(0,0.5f,0.5f)),
                    ((v2+v0)*0.5f, (n2+n0).normalized, SampleType.MidEdge, new Vector3(0.5f,0,0.5f)),
                    ((v0+v1+v2)/3f, (n0+n1+n2).normalized, SampleType.Centroid, new Vector3(1f/3,1f/3,1f/3)),
                };

                var bindings = target.pointBindingsPerFace[f];
                bindings.Clear();

                // Vote counters: shellId → count
                var votes = new Dictionary<int, int>();

                for (int s = 0; s < samples.Length; s++)
                {
                    var sample = samples[s];
                    var hit = source.bvh.FindNearest(sample.pos, settings.maxProjectionDistance);

                    if (hit.triangleIndex < 0) continue;

                    // Normal check
                    int si0 = source.triangles[hit.triangleIndex * 3];
                    int si1 = source.triangles[hit.triangleIndex * 3 + 1];
                    int si2 = source.triangles[hit.triangleIndex * 3 + 2];
                    Vector3 srcNormal = (source.normals[si0] * hit.barycentric.x +
                                         source.normals[si1] * hit.barycentric.y +
                                         source.normals[si2] * hit.barycentric.z).normalized;
                    float dot = Vector3.Dot(sample.nrm, srcNormal);
                    if (dot < cosLimit) continue;

                    // Submesh filter
                    if (settings.filterBySubmesh)
                    {
                        int targetSub = target.submeshIds[f];
                        int sourceSub = source.submeshIds[hit.triangleIndex];
                        if (targetSub != sourceSub) continue;
                    }

                    float dist = Mathf.Sqrt(hit.distSq);
                    int shellId = source.triangleToShellId[hit.triangleIndex];

                    // Interpolate source UV
                    Vector2 srcUv = source.uvSource[si0] * hit.barycentric.x +
                                    source.uvSource[si1] * hit.barycentric.y +
                                    source.uvSource[si2] * hit.barycentric.z;

                    // Confidence: closer + aligned = higher
                    float distConf = 1f - Mathf.Clamp01(dist / settings.maxProjectionDistance);
                    float normConf = Mathf.Clamp01((dot - cosLimit) / (1f - cosLimit));
                    float confidence = distConf * 0.6f + normConf * 0.4f;

                    var binding = new PointBinding
                    {
                        targetTriangleId = f,
                        sampleType = sample.type,
                        targetPosition = sample.pos,
                        targetBarycentric = sample.bary,
                        sourceTriangleId = hit.triangleIndex,
                        sourceBarycentric = hit.barycentric,
                        sourcePosition = hit.point,
                        sourceUV = srcUv,
                        sourceShellId = shellId,
                        sourcePrimId = hit.triangleIndex,
                        materialRegionId = source.triangleSignatures[hit.triangleIndex].materialRegionId,
                        distance3D = dist,
                        normalDot = dot,
                        confidence = confidence,
                        isAmbiguous = false
                    };

                    bindings.Add(binding);

                    if (!votes.ContainsKey(shellId))
                        votes[shellId] = 0;
                    votes[shellId]++;
                }

                // ── Majority vote ──
                if (votes.Count == 0)
                {
                    target.triangleShellAssignments[f] = -1;
                    target.triangleStatus[f] = TriangleStatus.Rejected;
                    continue;
                }

                int bestShell = -1;
                int bestCount = 0;
                int totalVotes = 0;
                foreach (var kv in votes)
                {
                    totalVotes += kv.Value;
                    if (kv.Value > bestCount)
                    {
                        bestCount = kv.Value;
                        bestShell = kv.Key;
                    }
                }

                target.triangleShellAssignments[f] = bestShell;

                // Ambiguity check: if dominant shell has < 60% of votes
                float dominance = (float)bestCount / totalVotes;
                if (dominance < 0.6f && votes.Count > 1)
                    target.triangleStatus[f] = TriangleStatus.Ambiguous;
            }

            // ── Neighbor stabilization pass ──
            StabilizeByNeighbors(target);
        }

        /// <summary>
        /// If a triangle's shell differs from all its neighbors, and neighbors agree,
        /// consider it a false outlier and re-assign.
        /// Only applies to non-Ambiguous triangles.
        /// </summary>
        static void StabilizeByNeighbors(TargetTransferState target)
        {
            // Build adjacency: face → neighbor faces (by shared edge)
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

            // Build face → neighbors
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

            // Single pass: re-assign outliers
            int reassigned = 0;
            for (int f = 0; f < target.faceCount; f++)
            {
                if (target.triangleShellAssignments[f] < 0) continue;
                if (target.triangleStatus[f] == TriangleStatus.Ambiguous) continue;

                int myShell = target.triangleShellAssignments[f];
                var nbrs = neighbors[f];
                if (nbrs.Count < 2) continue;

                // Count how many neighbors have same shell
                int sameCount = 0;
                int diffShell = -1;
                int diffCount = 0;

                foreach (int n in nbrs)
                {
                    int nShell = target.triangleShellAssignments[n];
                    if (nShell < 0) continue;
                    if (nShell == myShell)
                        sameCount++;
                    else
                    {
                        diffCount++;
                        diffShell = nShell;
                    }
                }

                // If I'm the only one with this shell among neighbors, and all
                // neighbors agree on a different shell → re-assign
                if (sameCount == 0 && diffCount >= 2 && diffShell >= 0)
                {
                    // Verify all diff neighbors agree on same shell
                    bool allAgree = true;
                    foreach (int n in nbrs)
                    {
                        int nShell = target.triangleShellAssignments[n];
                        if (nShell >= 0 && nShell != diffShell)
                        {
                            allAgree = false;
                            break;
                        }
                    }

                    if (allAgree)
                    {
                        target.triangleShellAssignments[f] = diffShell;
                        reassigned++;
                    }
                }
            }

            if (reassigned > 0)
                UvtLog.Verbose($"[ShellAssignment] Stabilized {reassigned} outlier triangle(s) by neighbor consensus");
        }
    }
}
