// InitialUvTransferSolver.cs — Stage 3: Shell-isolated initial UV transfer
// For each target triangle, transfer UV from source using only bindings
// within the assigned shell. Also transfers sourcePrimId and isBorder flags.
// No cross-shell bleeding. No border repair here.
//
// Key design: per-shell vertex accumulation with UV0-based priority.
// When a vertex is shared between triangles in different shells,
// the shell with the best UV0 proximity wins — no cross-shell averaging.

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
            bool hasUv0 = target.targetUv0 != null && source.uv0 != null;

            // Per-vertex, per-shell accumulation.
            // vertexId → { shellId → ShellEntry }
            var vertAccum = new Dictionary<int, Dictionary<int, ShellVertexEntry>>();

            for (int f = 0; f < target.faceCount; f++)
            {
                int assignedShell = target.triangleShellAssignments[f];
                if (assignedShell < 0)
                {
                    target.triangleStatus[f] = TriangleStatus.Rejected;
                    continue;
                }

                var bindings = target.pointBindingsPerFace[f];

                // Filter to in-shell bindings only — find best for sourcePrimId
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

                    Vector2 uvResult;
                    float weight;
                    Vector2 sourceUv0Sample = Vector2.zero;
                    bool gotUv0 = false;

                    // Primary: BVH projection from actual vertex position (accurate for large target triangles)
                    if (FallbackVertexProject(vPos, assignedShell, source,
                                               out uvResult, out weight, out sourceUv0Sample))
                    {
                        gotUv0 = hasUv0;
                    }
                    else if (InterpolateVertexUv(vPos, assignedShell, source, bindings,
                                            out uvResult, out weight, out sourceUv0Sample))
                    {
                        // Fallback: use face-level bindings when BVH can't find shell match
                        gotUv0 = hasUv0;
                    }
                    else
                    {
                        continue;
                    }

                    // Compute UV0 proximity if available
                    float uv0Proximity = 0f;
                    if (gotUv0)
                    {
                        Vector2 targetUv0 = target.targetUv0[vi];
                        float uv0Dist = (targetUv0 - sourceUv0Sample).magnitude;
                        // Proximity: 1.0 = exact match, drops off with distance
                        uv0Proximity = 1f / (1f + uv0Dist * 10f);
                    }

                    AccumulateVertex(vertAccum, vi, uvResult, weight, assignedShell, uv0Proximity);
                }
            }

            // ── Resolve accumulated vertex UV ──
            ResolveVertexUv(vertAccum, target, hasUv0);

            // ── Detect UnavoidableMismatch: vertex shared by triangles in different shells ──
            DetectVertexConflicts(target);

            // ── Post-validation: detect anomalous triangles ──
            PostValidateTriangleUv(source, target);
        }

        // ─── Per-shell vertex entry ───

        struct ShellVertexEntry
        {
            public Vector2 sumUv;
            public float totalWeight;
            public float sumUv0Proximity;
            public int sampleCount;
        }

        static void AccumulateVertex(
            Dictionary<int, Dictionary<int, ShellVertexEntry>> dict,
            int vertId, Vector2 uv, float weight, int shellId, float uv0Proximity)
        {
            if (!dict.TryGetValue(vertId, out var shellMap))
            {
                shellMap = new Dictionary<int, ShellVertexEntry>();
                dict[vertId] = shellMap;
            }

            if (shellMap.TryGetValue(shellId, out var entry))
            {
                entry.sumUv += uv * weight;
                entry.totalWeight += weight;
                entry.sumUv0Proximity += uv0Proximity;
                entry.sampleCount++;
                shellMap[shellId] = entry;
            }
            else
            {
                shellMap[shellId] = new ShellVertexEntry
                {
                    sumUv = uv * weight,
                    totalWeight = weight,
                    sumUv0Proximity = uv0Proximity,
                    sampleCount = 1
                };
            }
        }

        /// <summary>
        /// Resolve final UV for each vertex.
        /// Non-conflicting vertices: straightforward weighted average within their single shell.
        /// Conflicting vertices (multiple shells): pick the shell with highest combined score
        /// (weight + UV0 proximity), use only that shell's UV data.
        /// </summary>
        static void ResolveVertexUv(
            Dictionary<int, Dictionary<int, ShellVertexEntry>> vertAccum,
            TargetTransferState target,
            bool hasUv0)
        {
            int conflictCount = 0;
            int uv0ResolvedCount = 0;

            foreach (var kv in vertAccum)
            {
                int vertId = kv.Key;
                var shellMap = kv.Value;

                if (shellMap.Count == 0) continue;

                if (shellMap.Count == 1)
                {
                    // Single shell — no conflict, simple resolve
                    foreach (var entry in shellMap.Values)
                    {
                        if (entry.totalWeight > 0)
                            target.targetUv[vertId] = entry.sumUv / entry.totalWeight;
                    }
                    continue;
                }

                // ── Multiple shells competing for this vertex ──
                conflictCount++;

                int bestShellId = -1;
                float bestScore = -1f;
                bool uv0WasDominant = false;

                foreach (var shellKv in shellMap)
                {
                    int shellId = shellKv.Key;
                    var entry = shellKv.Value;

                    if (entry.totalWeight <= 0) continue;

                    // Base score: total accumulated weight
                    float score = entry.totalWeight;

                    // UV0 proximity bonus: strong signal when available
                    if (hasUv0 && entry.sampleCount > 0)
                    {
                        float avgUv0Prox = entry.sumUv0Proximity / entry.sampleCount;
                        // UV0 proximity can double the score — very strong signal
                        score *= (1f + avgUv0Prox * 2f);
                    }

                    if (score > bestScore)
                    {
                        // Track whether UV0 flipped the decision
                        if (bestShellId >= 0 && hasUv0)
                        {
                            // Check: would weight alone have chosen differently?
                            float prevWeight = shellMap[bestShellId].totalWeight;
                            if (entry.totalWeight < prevWeight)
                                uv0WasDominant = true;
                        }

                        bestScore = score;
                        bestShellId = shellId;
                    }
                }

                if (uv0WasDominant)
                    uv0ResolvedCount++;

                // Use UV only from the winning shell
                if (bestShellId >= 0 && shellMap.TryGetValue(bestShellId, out var winner))
                {
                    if (winner.totalWeight > 0)
                        target.targetUv[vertId] = winner.sumUv / winner.totalWeight;
                }
            }

            if (conflictCount > 0)
            {
                UvtLog.Verbose($"[InitialUvTransfer] Resolved {conflictCount} vertex shell conflicts" +
                          (hasUv0 ? $" ({uv0ResolvedCount} decided by UV0 proximity)" : ""));
            }
        }

        // ─── Per-vertex interpolation from in-shell bindings ───

        static bool InterpolateVertexUv(
            Vector3 vertexPos, int shellId,
            SourceMeshData source, List<PointBinding> bindings,
            out Vector2 uv, out float weight, out Vector2 sourceUv0)
        {
            uv = Vector2.zero;
            weight = 0;
            sourceUv0 = Vector2.zero;

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

            // Interpolate source UV0 at the same barycentric position
            if (source.uv0 != null)
            {
                sourceUv0 = source.uv0[si0] * bary.x +
                            source.uv0[si1] * bary.y +
                            source.uv0[si2] * bary.z;
            }

            return true;
        }

        /// <summary>
        /// Direct BVH query fallback: find nearest source triangle in the assigned shell.
        /// </summary>
        static bool FallbackVertexProject(
            Vector3 vertexPos, int shellId,
            SourceMeshData source,
            out Vector2 uv, out float weight, out Vector2 sourceUv0)
        {
            uv = Vector2.zero;
            weight = 0;
            sourceUv0 = Vector2.zero;

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
                weight = 0.9f; // direct BVH hit in correct shell

                if (source.uv0 != null)
                {
                    sourceUv0 = source.uv0[si0] * hit.barycentric.x +
                                source.uv0[si1] * hit.barycentric.y +
                                source.uv0[si2] * hit.barycentric.z;
                }
                return true;
            }

            // Not in our shell — scan nearby triangles in shell
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
            weight = 0.6f; // brute-force shell scan, still reliable

            if (source.uv0 != null)
            {
                sourceUv0 = source.uv0[bi0] * bestBary.x +
                            source.uv0[bi1] * bestBary.y +
                            source.uv0[bi2] * bestBary.z;
            }

            return true;
        }

        /// <summary>
        /// Post-validation: detect triangles with anomalously large UV bounding box
        /// relative to their 3D size. These are vertices that still ended up with
        /// bad UV despite shell selection (e.g. all three vertices in different shells).
        /// Attempt re-projection from the triangle's assigned shell.
        /// </summary>
        static void PostValidateTriangleUv(SourceMeshData source, TargetTransferState target)
        {
            int fixedCount = 0;
            int rejectedSharedCount = 0;
            const bool topologySafeRepairOnly = true;

            var vertexUseCount = new int[target.vertices.Length];
            var vertexToFaces = new List<int>[target.vertices.Length];

            for (int f = 0; f < target.faceCount; f++)
            {
                for (int j = 0; j < 3; j++)
                {
                    int vi = target.triangles[f * 3 + j];
                    vertexUseCount[vi]++;

                    if (vertexToFaces[vi] == null)
                        vertexToFaces[vi] = new List<int>();

                    vertexToFaces[vi].Add(f);
                }
            }

            for (int f = 0; f < target.faceCount; f++)
            {
                int assignedShell = target.triangleShellAssignments[f];
                if (assignedShell < 0) continue;
                if (target.triangleStatus[f] == TriangleStatus.Rejected) continue;

                int vi0 = target.triangles[f * 3];
                int vi1 = target.triangles[f * 3 + 1];
                int vi2 = target.triangles[f * 3 + 2];

                Vector2 uv0 = target.targetUv[vi0];
                Vector2 uv1 = target.targetUv[vi1];
                Vector2 uv2 = target.targetUv[vi2];

                // Check if UV bounding box is anomalously large
                Vector2 uvMin = Vector2.Min(Vector2.Min(uv0, uv1), uv2);
                Vector2 uvMax = Vector2.Max(Vector2.Max(uv0, uv1), uv2);
                float uvSpan = Mathf.Max(uvMax.x - uvMin.x, uvMax.y - uvMin.y);

                // Compare UV span to the assigned shell's bounding box
                if (assignedShell < source.uvShells.Count)
                {
                    var shell = source.uvShells[assignedShell];
                    float shellSpan = Mathf.Max(
                        shell.uvBoundsMax.x - shell.uvBoundsMin.x,
                        shell.uvBoundsMax.y - shell.uvBoundsMin.y);

                    // If triangle's UV span exceeds shell's span, something is very wrong
                    if (shellSpan > 0 && uvSpan > shellSpan * 1.5f)
                    {
                        // Re-project all three vertices strictly from assigned shell
                        bool allFixed = true;
                        Vector2[] fixedUv = new Vector2[3];

                        for (int j = 0; j < 3; j++)
                        {
                            int vi = target.triangles[f * 3 + j];
                            Vector3 vPos = target.vertices[vi];

                            Vector2 reprojUv;
                            float reprojWeight;
                            Vector2 dummyUv0;

                            if (FallbackVertexProject(vPos, assignedShell, source,
                                                       out reprojUv, out reprojWeight, out dummyUv0))
                            {
                                fixedUv[j] = reprojUv;
                            }
                            else
                            {
                                allFixed = false;
                                break;
                            }
                        }

                        if (allFixed)
                        {
                            // Verify the fix is actually better
                            Vector2 newMin = Vector2.Min(Vector2.Min(fixedUv[0], fixedUv[1]), fixedUv[2]);
                            Vector2 newMax = Vector2.Max(Vector2.Max(fixedUv[0], fixedUv[1]), fixedUv[2]);
                            float newSpan = Mathf.Max(newMax.x - newMin.x, newMax.y - newMin.y);

                            if (newSpan < uvSpan * 0.5f)
                            {
                                bool hasSharedVertex =
                                    vertexUseCount[vi0] > 1 ||
                                    vertexUseCount[vi1] > 1 ||
                                    vertexUseCount[vi2] > 1;

                                bool neighborImproved = true;
                                if (hasSharedVertex)
                                {
                                    var proposal = new Dictionary<int, Vector2>(3)
                                    {
                                        [vi0] = fixedUv[0],
                                        [vi1] = fixedUv[1],
                                        [vi2] = fixedUv[2]
                                    };

                                    float metricBefore = EvaluateOneRingImpactMetric(
                                        source, target, vertexToFaces, vi0, vi1, vi2, null);
                                    float metricAfter = EvaluateOneRingImpactMetric(
                                        source, target, vertexToFaces, vi0, vi1, vi2, proposal);

                                    neighborImproved = metricAfter < metricBefore;
                                }

                                if (hasSharedVertex && topologySafeRepairOnly && !neighborImproved)
                                {
                                    rejectedSharedCount++;
                                    continue;
                                }

                                // Write fixed UV only for this triangle's vertices
                                // Note: this may affect neighboring triangles sharing these vertices,
                                // but in topology-safe mode we only allow it when one-ring metric improves
                                for (int j = 0; j < 3; j++)
                                {
                                    int vi = target.triangles[f * 3 + j];
                                    target.targetUv[vi] = fixedUv[j];
                                }
                                fixedCount++;
                            }
                        }
                    }
                }
            }

            if (fixedCount > 0)
            {
                UvtLog.Verbose($"[InitialUvTransfer] Post-validation fixed {fixedCount} anomalous triangles");
            }

            if (rejectedSharedCount > 0)
            {
                UvtLog.Verbose($"[InitialUvTransfer] Post-validation rejected {rejectedSharedCount} repairs due to shared vertices");
            }
        }

        static float EvaluateOneRingImpactMetric(
            SourceMeshData source,
            TargetTransferState target,
            List<int>[] vertexToFaces,
            int vi0,
            int vi1,
            int vi2,
            Dictionary<int, Vector2> overrideUv)
        {
            var affectedFaces = new HashSet<int>();
            AddAffectedFaces(vertexToFaces, vi0, affectedFaces);
            AddAffectedFaces(vertexToFaces, vi1, affectedFaces);
            AddAffectedFaces(vertexToFaces, vi2, affectedFaces);

            float metric = 0f;

            foreach (int f in affectedFaces)
            {
                if (target.triangleStatus[f] == TriangleStatus.Rejected) continue;

                int i0 = target.triangles[f * 3];
                int i1 = target.triangles[f * 3 + 1];
                int i2 = target.triangles[f * 3 + 2];

                Vector2 uv0 = overrideUv != null && overrideUv.TryGetValue(i0, out var o0) ? o0 : target.targetUv[i0];
                Vector2 uv1 = overrideUv != null && overrideUv.TryGetValue(i1, out var o1) ? o1 : target.targetUv[i1];
                Vector2 uv2 = overrideUv != null && overrideUv.TryGetValue(i2, out var o2) ? o2 : target.targetUv[i2];

                Vector2 uvMin = Vector2.Min(Vector2.Min(uv0, uv1), uv2);
                Vector2 uvMax = Vector2.Max(Vector2.Max(uv0, uv1), uv2);
                float uvSpan = Mathf.Max(uvMax.x - uvMin.x, uvMax.y - uvMin.y);

                float spanPenalty = uvSpan;

                int shellId = target.triangleShellAssignments[f];
                if (shellId >= 0 && shellId < source.uvShells.Count)
                {
                    var shell = source.uvShells[shellId];
                    float shellSpan = Mathf.Max(
                        shell.uvBoundsMax.x - shell.uvBoundsMin.x,
                        shell.uvBoundsMax.y - shell.uvBoundsMin.y);

                    if (shellSpan > 1e-6f)
                        spanPenalty = uvSpan / shellSpan;
                }

                float orientationPenalty = 0f;
                float signedAreaBefore = ComputeSignedArea(
                    target.targetUv[i0],
                    target.targetUv[i1],
                    target.targetUv[i2]);
                float signedAreaAfter = ComputeSignedArea(uv0, uv1, uv2);
                if (Mathf.Abs(signedAreaBefore) > 1e-9f && Mathf.Abs(signedAreaAfter) > 1e-9f &&
                    Mathf.Sign(signedAreaBefore) != Mathf.Sign(signedAreaAfter))
                {
                    orientationPenalty = 2f;
                }

                metric += spanPenalty + orientationPenalty;
            }

            return metric;
        }

        static void AddAffectedFaces(List<int>[] vertexToFaces, int vi, HashSet<int> affectedFaces)
        {
            var faces = vertexToFaces[vi];
            if (faces == null) return;

            foreach (int faceId in faces)
                affectedFaces.Add(faceId);
        }

        static float ComputeSignedArea(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        /// <summary>
        /// Mark triangles whose vertices are shared across different shells
        /// as UnavoidableMismatch — we can't express this without new topology.
        /// </summary>
        static void DetectVertexConflicts(TargetTransferState target)
        {
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
