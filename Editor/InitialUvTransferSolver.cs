// InitialUvTransferSolver.cs — Stage 3: Shell-isolated initial UV transfer
// For each target triangle, transfer UV from source using only bindings
// within the assigned shell. Also transfers sourcePrimId and isBorder flags.
// No cross-shell bleeding. No border repair here.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class InitialUvTransferSolver
    {
        /// <summary>
        /// Perform initial UV transfer, strictly within assigned shells.
        /// Populates: targetUv, triangleSourcePrimId, triangleBorderFlags.
        /// </summary>
        public static void Solve(SourceMeshData source, TargetTransferState target)
        {
            // Per-vertex: collect UV candidates weighted by confidence
            // A vertex may be shared by multiple triangles possibly in different shells.
            // We accumulate UV per vertex from in-shell bindings, then average.

            // vertexId → (sumUV, sumWeight, assignedShell)
            var vertAccum = new Dictionary<int, VertexAccumulator>();

            for (int f = 0; f < target.faceCount; f++)
            {
                int assignedShell = target.triangleShellAssignments[f];
                if (assignedShell < 0)
                {
                    target.triangleStatus[f] = TriangleStatus.Rejected;
                    continue;
                }

                var bindings = target.pointBindingsPerFace[f];

                // Filter to in-shell bindings only
                PointBinding bestPrim = default;
                float bestPrimConf = -1f;

                foreach (var b in bindings)
                {
                    if (b.sourceShellId != assignedShell) continue;

                    if (b.confidence > bestPrimConf)
                    {
                        bestPrimConf = b.confidence;
                        bestPrim = b;
                    }
                }

                // Record sourcePrimId from best binding
                if (bestPrimConf >= 0)
                {
                    target.triangleSourcePrimId[f] = bestPrim.sourcePrimId;
                    target.triangleBorderFlags[f] = source.borderPrimitiveIds.Contains(bestPrim.sourcePrimId);
                }

                // Transfer UV to each vertex of this triangle
                for (int j = 0; j < 3; j++)
                {
                    int vi = target.triangles[f * 3 + j];
                    Vector3 vPos = target.vertices[vi];

                    // Find the best in-shell binding for this vertex position
                    Vector2 uvResult;
                    float weight;
                    if (InterpolateVertexUv(vPos, assignedShell, source, bindings,
                                            out uvResult, out weight))
                    {
                        AccumulateVertex(vertAccum, vi, uvResult, weight, assignedShell);
                    }
                    else
                    {
                        // Fallback: direct BVH query for this vertex, restricted to shell
                        if (FallbackVertexProject(vPos, assignedShell, source,
                                                   out uvResult, out weight))
                        {
                            AccumulateVertex(vertAccum, vi, uvResult, weight, assignedShell);
                        }
                    }
                }
            }

            // ── Resolve accumulated vertex UV ──
            foreach (var kv in vertAccum)
            {
                var acc = kv.Value;
                if (acc.totalWeight > 0)
                    target.targetUv[kv.Key] = acc.sumUv / acc.totalWeight;
            }

            // ── Detect UnavoidableMismatch: vertex shared by triangles in different shells ──
            DetectVertexConflicts(target);
        }

        // ─── Per-vertex interpolation from in-shell bindings ───

        static bool InterpolateVertexUv(
            Vector3 vertexPos, int shellId,
            SourceMeshData source, List<PointBinding> bindings,
            out Vector2 uv, out float weight)
        {
            uv = Vector2.zero;
            weight = 0;

            // Use vertex-type bindings first, then midedge, then centroid
            PointBinding best = default;
            float bestScore = -1f;

            foreach (var b in bindings)
            {
                if (b.sourceShellId != shellId) continue;

                // Score: closer sample to this vertex gets higher weight
                float distToVert = (b.targetPosition - vertexPos).sqrMagnitude;
                float proxScore = 1f / (1f + distToVert * 100f);
                float score = b.confidence * proxScore;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = b;
                }
            }

            if (bestScore < 0) return false;

            // Project vertex onto the source triangle found by best binding
            int si0 = source.triangles[best.sourceTriangleId * 3];
            int si1 = source.triangles[best.sourceTriangleId * 3 + 1];
            int si2 = source.triangles[best.sourceTriangleId * 3 + 2];

            Vector3 sa = source.vertices[si0];
            Vector3 sb = source.vertices[si1];
            Vector3 sc = source.vertices[si2];

            TriangleBvh.ClosestPointOnTriangle(vertexPos, sa, sb, sc, out Vector3 bary);

            uv = source.uvSource[si0] * bary.x +
                 source.uvSource[si1] * bary.y +
                 source.uvSource[si2] * bary.z;
            weight = best.confidence;
            return true;
        }

        /// <summary>
        /// Direct BVH query fallback: find nearest source triangle in the assigned shell.
        /// </summary>
        static bool FallbackVertexProject(
            Vector3 vertexPos, int shellId,
            SourceMeshData source,
            out Vector2 uv, out float weight)
        {
            uv = Vector2.zero;
            weight = 0;

            // BVH gives us the globally nearest triangle — check if it's in our shell
            var hit = source.bvh.FindNearest(vertexPos);
            if (hit.triangleIndex < 0) return false;

            if (source.triangleToShellId[hit.triangleIndex] == shellId)
            {
                int si0 = source.triangles[hit.triangleIndex * 3];
                int si1 = source.triangles[hit.triangleIndex * 3 + 1];
                int si2 = source.triangles[hit.triangleIndex * 3 + 2];

                uv = source.uvSource[si0] * hit.barycentric.x +
                     source.uvSource[si1] * hit.barycentric.y +
                     source.uvSource[si2] * hit.barycentric.z;
                weight = 0.3f; // lower confidence for fallback
                return true;
            }

            // Not in our shell — scan nearby triangles in shell
            // For now, brute-force within shell (TODO: spatial index per shell)
            float bestDistSq = float.MaxValue;
            int bestFace = -1;
            Vector3 bestBary = Vector3.zero;

            if (shellId >= 0 && shellId < source.uvShells.Count)
            {
                foreach (int f in source.uvShells[shellId].triangleIds)
                {
                    int i0 = source.triangles[f * 3];
                    int i1 = source.triangles[f * 3 + 1];
                    int i2 = source.triangles[f * 3 + 2];

                    var cp = TriangleBvh.ClosestPointOnTriangle(
                        vertexPos, source.vertices[i0], source.vertices[i1], source.vertices[i2],
                        out Vector3 bary);
                    float dSq = (cp - vertexPos).sqrMagnitude;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestFace = f;
                        bestBary = bary;
                    }
                }
            }

            if (bestFace < 0) return false;

            int bi0 = source.triangles[bestFace * 3];
            int bi1 = source.triangles[bestFace * 3 + 1];
            int bi2 = source.triangles[bestFace * 3 + 2];

            uv = source.uvSource[bi0] * bestBary.x +
                 source.uvSource[bi1] * bestBary.y +
                 source.uvSource[bi2] * bestBary.z;
            weight = 0.2f;
            return true;
        }

        // ─── Accumulator ───

        struct VertexAccumulator
        {
            public Vector2 sumUv;
            public float totalWeight;
            public int shellId;
            public bool conflict;
        }

        static void AccumulateVertex(Dictionary<int, VertexAccumulator> dict,
                                      int vertId, Vector2 uv, float weight, int shellId)
        {
            if (dict.TryGetValue(vertId, out var acc))
            {
                // Check for shell conflict at this vertex
                if (acc.shellId != shellId && acc.shellId >= 0)
                    acc.conflict = true;

                acc.sumUv += uv * weight;
                acc.totalWeight += weight;
                dict[vertId] = acc;
            }
            else
            {
                dict[vertId] = new VertexAccumulator
                {
                    sumUv = uv * weight,
                    totalWeight = weight,
                    shellId = shellId,
                    conflict = false
                };
            }
        }

        /// <summary>
        /// Mark triangles whose vertices are shared across different shells
        /// as UnavoidableMismatch — we can't express this without new topology.
        /// </summary>
        static void DetectVertexConflicts(TargetTransferState target)
        {
            // Detect: if a triangle has vertex with shell conflict,
            // and the triangle's assigned shell differs from what the vertex got
            // We need vertex→shell from the accumulator, but it's local to Solve().
            // Instead, check if adjacent triangles of same vertex have different shell assignments.

            // vertex → set of assigned shells
            var vertShells = new Dictionary<int, HashSet<int>>();

            for (int f = 0; f < target.faceCount; f++)
            {
                int shell = target.triangleShellAssignments[f];
                if (shell < 0) continue;

                for (int j = 0; j < 3; j++)
                {
                    int vi = target.triangles[f * 3 + j];
                    if (!vertShells.TryGetValue(vi, out var set))
                    {
                        set = new HashSet<int>();
                        vertShells[vi] = set;
                    }
                    set.Add(shell);
                }
            }

            // Mark triangles that have conflicted vertices
            for (int f = 0; f < target.faceCount; f++)
            {
                if (target.triangleStatus[f] == TriangleStatus.Rejected) continue;
                if (target.triangleStatus[f] == TriangleStatus.Ambiguous) continue;

                for (int j = 0; j < 3; j++)
                {
                    int vi = target.triangles[f * 3 + j];
                    if (vertShells.TryGetValue(vi, out var set) && set.Count > 1)
                    {
                        if (target.triangleStatus[f] == TriangleStatus.None)
                            target.triangleStatus[f] = TriangleStatus.UnavoidableMismatch;
                        break;
                    }
                }
            }

            // Everything else still None → Accepted
            for (int f = 0; f < target.faceCount; f++)
            {
                if (target.triangleStatus[f] == TriangleStatus.None)
                    target.triangleStatus[f] = TriangleStatus.Accepted;
            }
        }
    }
}
