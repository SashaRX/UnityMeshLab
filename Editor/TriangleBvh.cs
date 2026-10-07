// TriangleBvh.cs — the package's triangle BVH: binned-SAH build (Wald 2007, 8 bins
// per axis, SAH-terminated leaves), ordered ray traversal with the watertight
// two-sided ray–triangle test (Woop, Benthin, Wald 2013), nearest-point queries with
// optional normal / facing / either-side filters. GetGPUData hands the same tree to
// Shaders/BvhTraversal.hlsl, which mirrors every test here.

using System;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public class TriangleBvh
    {
        struct Node
        {
            public Vector3 bMin, bMax;
            public int left;   // child index or -1
            public int right;  // child index or -1
            public int triStart;
            public int triCount;
        }

        Node[] nodes;
        int[] triIndices;
        Vector3[] verts;
        int[] tris;
        int nodeCount;

        // Leaves: the SAH decides (a split must beat C_INT × count), with a hard cap so
        // piles of coincident triangles still terminate; below MIN_SPLIT nothing is tried.
        const int MAX_LEAF = 8, MIN_SPLIT = 3, BINS = 8;
        const float C_TRAV = 1f, C_INT = 1f;

        // Per face, for the build only: bounds and centroid; plus the SAH bin scratch.
        readonly Vector3[] faceMin, faceMax, faceCentroid;
        readonly int[] binCount = new int[BINS], leftCount = new int[BINS - 1], rightCount = new int[BINS - 1];
        readonly float[] leftArea = new float[BINS - 1], rightArea = new float[BINS - 1];
        readonly Vector3[] binMin = new Vector3[BINS], binMax = new Vector3[BINS];

        public TriangleBvh(Vector3[] vertices, int[] triangles)
        {
            verts = vertices;
            tris = triangles;
            int faceCount = triangles.Length / 3;

            triIndices = new int[faceCount];
            faceMin = new Vector3[faceCount]; faceMax = new Vector3[faceCount]; faceCentroid = new Vector3[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                triIndices[f] = f;
                Vector3 a = verts[tris[f * 3]], b = verts[tris[f * 3 + 1]], c = verts[tris[f * 3 + 2]];
                faceMin[f] = Vector3.Min(a, Vector3.Min(b, c));
                faceMax[f] = Vector3.Max(a, Vector3.Max(b, c));
                faceCentroid[f] = (a + b + c) * (1f / 3f);
            }

            nodes = new Node[Math.Max(1, faceCount * 2)];
            nodeCount = 0;
            if (faceCount == 0) { nodes[nodeCount++] = new Node { left = -1, right = -1, triStart = 0, triCount = 0 }; }
            else BuildRecursive(0, faceCount);
            faceMin = faceMax = faceCentroid = null;
        }
        // ─── Nearest point on any triangle ───
        public struct HitResult
        {
            public int triangleIndex;
            public Vector3 point;
            public Vector3 barycentric;
            public float distSq;
        }

        public HitResult FindNearest(Vector3 queryPoint)
        {
            var best = new HitResult { triangleIndex = -1, distSq = float.MaxValue };
            FindNearestRecursive(0, queryPoint, ref best);
            return best;
        }

        public HitResult FindNearest(Vector3 queryPoint, float maxDist)
        {
            var best = new HitResult { triangleIndex = -1, distSq = maxDist * maxDist };
            FindNearestRecursive(0, queryPoint, ref best);
            return best;
        }

        /// <summary>
        /// Find nearest triangle whose face normal has dot >= normalDotMin with queryNormal.
        /// faceNormals is indexed by local face index (same as returned triangleIndex).
        /// </summary>
        public HitResult FindNearestNormalFiltered(Vector3 queryPoint, Vector3 queryNormal,
            Vector3[] faceNormals, float normalDotMin)
            => FindNearestNormalFiltered(queryPoint, queryNormal, faceNormals, normalDotMin, float.MaxValue);

        /// <summary>Normal-filtered nearest triangle within maxDist; triangleIndex -1 when none qualifies.
        /// eitherSide (optional, per face): a two-sided face passes the test with |dot|,
        /// its back counting as front.</summary>
        public HitResult FindNearestNormalFiltered(Vector3 queryPoint, Vector3 queryNormal,
            Vector3[] faceNormals, float normalDotMin, float maxDist, bool[] eitherSide = null)
        {
            var best = new HitResult { triangleIndex = -1, distSq = maxDist >= float.MaxValue ? float.MaxValue : maxDist * maxDist };
            FindNearestNormFiltRecursive(0, queryPoint, queryNormal, faceNormals, normalDotMin, eitherSide, ref best);
            return best;
        }

        // ─── Raycast ───

        public struct RayHit
        {
            public int triangleIndex;
            public float t;            // distance along ray
            public Vector3 barycentric; // (u, v, w) where u = 1-v-w
        }

        /// <summary>
        /// Cast a ray and find the closest triangle intersection.
        /// Returns RayHit with triangleIndex = -1 if no hit.
        /// </summary>
        public RayHit Raycast(Vector3 origin, Vector3 direction, float maxDist)
        {
            var best = new RayHit { triangleIndex = -1, t = maxDist };
            var frame = new RayFrame(direction);
            RaycastRecursive(0, origin, direction, in frame, ref best);
            return best;
        }

        /// <summary>
        /// Closest intersection with a triangle whose face normal OPPOSES the ray
        /// direction (the surfaces the ray can "see"): dot(faceNormal, direction) &lt;= 0.
        /// Bake-style projection through thin geometry needs this — an unfiltered ray
        /// pierces the wall and samples the far side's texture.
        /// faceNormals is indexed by local face index (same as returned triangleIndex).
        /// </summary>
        public RayHit RaycastFacingFiltered(Vector3 origin, Vector3 direction, float maxDist, Vector3[] faceNormals, bool[] eitherSide = null)
        {
            var best = new RayHit { triangleIndex = -1, t = maxDist };
            var frame = new RayFrame(direction);
            RaycastFacingRecursive(0, origin, direction, in frame, faceNormals, eitherSide, ref best);
            return best;
        }

        /// <summary>
        /// Intersection on [0, maxDist) closest to origin + direction * preferredT.
        /// Facing eligibility is unchanged: faceNormals, when supplied, must oppose
        /// direction, except either-side faces. The returned t is the actual ray
        /// parameter, not the distance from preferredT. Nonpositive preferredT keeps
        /// the ordinary first-hit behavior used by proxy and visibility queries.
        /// </summary>
        public RayHit RaycastClosestToTarget(Vector3 origin, Vector3 direction, float maxDist, float preferredT,
            Vector3[] faceNormals = null, bool[] eitherSide = null)
        {
            if (!(preferredT > 0f))
                return faceNormals == null ? Raycast(origin, direction, maxDist)
                    : RaycastFacingFiltered(origin, direction, maxDist, faceNormals, eitherSide);
            var best = new RayHit { triangleIndex = -1, t = maxDist };
            if (!(maxDist > 0f)) return best;
            preferredT = Mathf.Min(preferredT, maxDist);
            float distance = Mathf.Max(preferredT, maxDist - preferredT);
            var frame = new RayFrame(direction);
            RaycastTargetRecursive(0, origin, direction, in frame, maxDist, preferredT,
                faceNormals, eitherSide, ref distance, ref best);
            return best;
        }

        /// <summary>
        /// Ray-along-normal projection: shoots ray in both directions (+normal, -normal).
        /// Always prefers forward hit (along normal = same side of thin geometry).
        /// Backward hit is only used when forward misses entirely.
        /// </summary>
        public RayHit RaycastBidirectional(Vector3 origin, Vector3 normal, float maxDist)
        {
            var fwd = Raycast(origin, normal, maxDist);
            if (fwd.triangleIndex >= 0) return fwd;
            return Raycast(origin, -normal, maxDist);
        }

        // ─── Build ───
        // Binned SAH (Wald 2007): BINS bins along each axis over the centroid bounds,
        // the cheapest plane by surface-area heuristic; the split only happens when it
        // beats testing the whole node. A midpoint split keeps the tree balanced where
        // the SAH finds nothing (coincident centroids).
        int BuildRecursive(int start, int count)
        {
            int idx = nodeCount++;
            ref Node node = ref nodes[idx];
            ComputeBounds(start, count, out node.bMin, out node.bMax, out Vector3 cMin, out Vector3 cMax);
            node.left = -1; node.right = -1;
            if (count < MIN_SPLIT) { node.triStart = start; node.triCount = count; return idx; }

            int bestAxis = -1, bestBin = -1; float bestCost = float.MaxValue;
            Vector3 cExtent = cMax - cMin;
            for (int axis = 0; axis < 3; axis++)
            {
                float extent = GetComponent(cExtent, axis);
                if (extent <= 0f) continue;
                float scale = BINS / extent, origin = GetComponent(cMin, axis);
                for (int b = 0; b < BINS; b++) { binCount[b] = 0; binMin[b] = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue); binMax[b] = new Vector3(float.MinValue, float.MinValue, float.MinValue); }
                for (int i = start; i < start + count; i++)
                {
                    int f = triIndices[i];
                    int b = Math.Min(BINS - 1, (int)((GetComponent(faceCentroid[f], axis) - origin) * scale));
                    binCount[b]++;
                    binMin[b] = Vector3.Min(binMin[b], faceMin[f]); binMax[b] = Vector3.Max(binMax[b], faceMax[f]);
                }
                // Sweep from both ends: area and count of everything left / right of each plane.
                Vector3 lMin = binMin[0], lMax = binMax[0]; int lCount = 0;
                for (int b = 0; b < BINS - 1; b++)
                {
                    lCount += binCount[b];
                    if (b > 0) { lMin = Vector3.Min(lMin, binMin[b]); lMax = Vector3.Max(lMax, binMax[b]); }
                    leftCount[b] = lCount; leftArea[b] = lCount > 0 ? HalfArea(lMin, lMax) : 0f;
                }
                Vector3 rMin = binMin[BINS - 1], rMax = binMax[BINS - 1]; int rCount = 0;
                for (int b = BINS - 1; b > 0; b--)
                {
                    rCount += binCount[b];
                    if (b < BINS - 1) { rMin = Vector3.Min(rMin, binMin[b]); rMax = Vector3.Max(rMax, binMax[b]); }
                    rightCount[b - 1] = rCount; rightArea[b - 1] = rCount > 0 ? HalfArea(rMin, rMax) : 0f;
                }
                for (int b = 0; b < BINS - 1; b++)
                {
                    if (leftCount[b] == 0 || rightCount[b] == 0) continue;
                    float cost = leftArea[b] * leftCount[b] + rightArea[b] * rightCount[b];
                    if (cost < bestCost) { bestCost = cost; bestAxis = axis; bestBin = b; }
                }
            }

            float nodeArea = HalfArea(node.bMin, node.bMax);
            bool split = bestAxis >= 0 && nodeArea > 0f && C_TRAV + C_INT * bestCost / nodeArea < C_INT * count;
            int mid = start;
            if (split)
            {
                float plane = GetComponent(cMin, bestAxis) + (bestBin + 1) * GetComponent(cExtent, bestAxis) / BINS;
                mid = Partition(start, count, bestAxis, plane);
                if (mid == start || mid == start + count) split = false;
            }
            if (!split)
            {
                if (count <= MAX_LEAF) { node.triStart = start; node.triCount = count; return idx; }
                // Forced split for oversize leaves the SAH would not cut: the middle of
                // the longest centroid axis, or the middle of the range when all coincide.
                int axis = 0;
                if (cExtent.y > cExtent.x) axis = 1;
                if (cExtent.z > GetComponent(cExtent, axis)) axis = 2;
                mid = Partition(start, count, axis, GetComponent(cMin, axis) + GetComponent(cExtent, axis) * 0.5f);
                if (mid == start || mid == start + count) mid = start + count / 2;
            }
            node.triStart = -1; node.triCount = 0;
            int left = BuildRecursive(start, mid - start);
            int right = BuildRecursive(mid, start + count - mid);
            nodes[idx].left = left; nodes[idx].right = right;   // `node` may be stale after recursion (no realloc, but be explicit)
            return idx;
        }

        static float HalfArea(Vector3 min, Vector3 max)
        {
            Vector3 e = max - min;
            return e.x * e.y + e.y * e.z + e.z * e.x;
        }

        int Partition(int start, int count, int axis, float splitVal)
        {
            int lo = start, hi = start + count - 1;
            while (lo <= hi)
            {
                float c = GetComponent(faceCentroid[triIndices[lo]], axis);
                if (c < splitVal)
                    lo++;
                else
                {
                    int tmp = triIndices[lo];
                    triIndices[lo] = triIndices[hi];
                    triIndices[hi] = tmp;
                    hi--;
                }
            }
            return lo;
        }

        void ComputeBounds(int start, int count, out Vector3 bMin, out Vector3 bMax, out Vector3 cMin, out Vector3 cMax)
        {
            bMin = cMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            bMax = cMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = start; i < start + count; i++)
            {
                int f = triIndices[i];
                bMin = Vector3.Min(bMin, faceMin[f]); bMax = Vector3.Max(bMax, faceMax[f]);
                cMin = Vector3.Min(cMin, faceCentroid[f]); cMax = Vector3.Max(cMax, faceCentroid[f]);
            }
        }

        void FindNearestRecursive(int nodeIdx, Vector3 q, ref HitResult best)
        {
            ref Node node = ref nodes[nodeIdx];

            // AABB distance check — prune if box is farther than current best
            float boxDistSq = AabbDistSq(node.bMin, node.bMax, q);
            if (boxDistSq >= best.distSq) return;

            // Leaf
            if (node.left == -1)
            {
                for (int i = node.triStart; i < node.triStart + node.triCount; i++)
                {
                    int f = triIndices[i];
                    int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];

                    Vector3 closest = ClosestPointOnTriangle(q, verts[i0], verts[i1], verts[i2],
                                                              out Vector3 bary);
                    float dSq = (closest - q).sqrMagnitude;
                    if (dSq < best.distSq)
                    {
                        best.distSq = dSq;
                        best.triangleIndex = f;
                        best.point = closest;
                        best.barycentric = bary;
                    }
                }
                return;
            }

            // Traverse closer child first
            float dL = AabbDistSq(nodes[node.left].bMin, nodes[node.left].bMax, q);
            float dR = AabbDistSq(nodes[node.right].bMin, nodes[node.right].bMax, q);

            if (dL < dR)
            {
                FindNearestRecursive(node.left, q, ref best);
                FindNearestRecursive(node.right, q, ref best);
            }
            else
            {
                FindNearestRecursive(node.right, q, ref best);
                FindNearestRecursive(node.left, q, ref best);
            }
        }

        // ─── Normal-filtered nearest-point query ───
        void FindNearestNormFiltRecursive(int nodeIdx, Vector3 q, Vector3 qNrm,
            Vector3[] fNrm, float dotMin, bool[] eitherSide, ref HitResult best)
        {
            ref Node node = ref nodes[nodeIdx];

            float boxDistSq = AabbDistSq(node.bMin, node.bMax, q);
            if (boxDistSq >= best.distSq) return;

            if (node.left == -1)
            {
                for (int i = node.triStart; i < node.triStart + node.triCount; i++)
                {
                    int f = triIndices[i];
                    if (f < fNrm.Length) {
                        float dot = Vector3.Dot(fNrm[f], qNrm);
                        if (eitherSide != null && f < eitherSide.Length && eitherSide[f]) dot = Mathf.Abs(dot);
                        if (dot < dotMin) continue;
                    }

                    int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                    Vector3 closest = ClosestPointOnTriangle(q, verts[i0], verts[i1], verts[i2],
                                                              out Vector3 bary);
                    float dSq = (closest - q).sqrMagnitude;
                    if (dSq < best.distSq)
                    {
                        best.distSq = dSq;
                        best.triangleIndex = f;
                        best.point = closest;
                        best.barycentric = bary;
                    }
                }
                return;
            }

            float dL = AabbDistSq(nodes[node.left].bMin, nodes[node.left].bMax, q);
            float dR = AabbDistSq(nodes[node.right].bMin, nodes[node.right].bMax, q);

            if (dL < dR)
            {
                FindNearestNormFiltRecursive(node.left, q, qNrm, fNrm, dotMin, eitherSide, ref best);
                FindNearestNormFiltRecursive(node.right, q, qNrm, fNrm, dotMin, eitherSide, ref best);
            }
            else
            {
                FindNearestNormFiltRecursive(node.right, q, qNrm, fNrm, dotMin, eitherSide, ref best);
                FindNearestNormFiltRecursive(node.left, q, qNrm, fNrm, dotMin, eitherSide, ref best);
            }
        }

        // ─── Raycast query ───
        // Both traversals visit the nearer child first: once a hit shrinks best.t the far
        // child is culled by the slab test, which matters for long rays (a proxy texel
        // looking through a whole building, a shadow ray across a scene).
        void RaycastRecursive(int nodeIdx, Vector3 origin, Vector3 dir, in RayFrame frame, ref RayHit best)
        {
            ref Node node = ref nodes[nodeIdx];
            if (!RayEntersAabb(origin, dir, node.bMin, node.bMax, best.t, out _))
                return;

            // Leaf: test triangles
            if (node.left == -1)
            {
                for (int i = node.triStart; i < node.triStart + node.triCount; i++)
                {
                    int f = triIndices[i];
                    int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                    if (Watertight(in frame, origin, verts[i0], verts[i1], verts[i2], best.t, out float t, out Vector3 bary))
                    {
                        best.t = t;
                        best.triangleIndex = f;
                        best.barycentric = bary;
                    }
                }
                return;
            }

            OrderChildren(node, origin, dir, best.t, out int first, out int second);
            if (first >= 0) RaycastRecursive(first, origin, dir, in frame, ref best);
            if (second >= 0) RaycastRecursive(second, origin, dir, in frame, ref best);
        }

        void RaycastFacingRecursive(int nodeIdx, Vector3 origin, Vector3 dir, in RayFrame frame, Vector3[] fNrm, bool[] eitherSide, ref RayHit best)
        {
            ref Node node = ref nodes[nodeIdx];
            if (!RayEntersAabb(origin, dir, node.bMin, node.bMax, best.t, out _))
                return;

            if (node.left == -1)
            {
                for (int i = node.triStart; i < node.triStart + node.triCount; i++)
                {
                    int f = triIndices[i];
                    if (Vector3.Dot(fNrm[f], dir) > 0f && !(eitherSide != null && f < eitherSide.Length && eitherSide[f])) continue; // facing away from the ray origin
                    int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                    if (Watertight(in frame, origin, verts[i0], verts[i1], verts[i2], best.t, out float t, out Vector3 bary))
                    {
                        best.t = t;
                        best.triangleIndex = f;
                        best.barycentric = bary;
                    }
                }
                return;
            }

            OrderChildren(node, origin, dir, best.t, out int first, out int second);
            if (first >= 0) RaycastFacingRecursive(first, origin, dir, in frame, fNrm, eitherSide, ref best);
            if (second >= 0) RaycastFacingRecursive(second, origin, dir, in frame, fNrm, eitherSide, ref best);
        }

        void RaycastTargetRecursive(int nodeIdx, Vector3 origin, Vector3 dir, in RayFrame frame,
            float maxT, float preferredT, Vector3[] fNrm, bool[] eitherSide, ref float distance, ref RayHit best)
        {
            ref Node node = ref nodes[nodeIdx];
            if (!RayTargetAabb(origin, dir, node.bMin, node.bMax, maxT, preferredT, out float bound)
                || bound > distance) return;
            if (node.left == -1) {
                for (int i = node.triStart; i < node.triStart + node.triCount; ++i) {
                    int f = triIndices[i];
                    if (fNrm != null && Vector3.Dot(fNrm[f], dir) > 0f
                        && !(eitherSide != null && f < eitherSide.Length && eitherSide[f])) continue;
                    int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                    // best.t cannot bound this test: a later intersection may be
                    // closer to the target than the current outer-layer hit.
                    if (!Watertight(in frame, origin, verts[a], verts[b], verts[c], maxT, out float t, out Vector3 bary)) continue;
                    float candidate = Mathf.Abs(t - preferredT);
                    int distanceOrder = candidate.CompareTo(distance);
                    int hitOrder = t.CompareTo(best.t);
                    if (distanceOrder > 0 || (distanceOrder == 0 && best.triangleIndex >= 0
                        && (hitOrder > 0 || (hitOrder == 0 && f >= best.triangleIndex)))) continue;
                    distance = candidate;
                    best = new RayHit { triangleIndex = f, t = t, barycentric = bary };
                }
                return;
            }
            ref Node left = ref nodes[node.left]; ref Node right = ref nodes[node.right];
            bool hitL = RayTargetAabb(origin, dir, left.bMin, left.bMax, maxT, preferredT, out float dl);
            bool hitR = RayTargetAabb(origin, dir, right.bMin, right.bMax, maxT, preferredT, out float dr);
            int first = node.left, second = node.right;
            if ((!hitL && hitR) || (hitL && hitR && dr < dl)) { first = node.right; second = node.left; }
            if (hitL || hitR) RaycastTargetRecursive(first, origin, dir, in frame, maxT, preferredT, fNrm, eitherSide, ref distance, ref best);
            if (hitL && hitR) RaycastTargetRecursive(second, origin, dir, in frame, maxT, preferredT, fNrm, eitherSide, ref distance, ref best);
        }

        // A box covers an interval on the segment; its lower bound is the distance
        // from preferredT to that interval, rather than distance from the ray origin.
        // Strict '>' pruning retains exact target hits and deterministic equal-distance
        // ties (outer hit, then face index). Only an exactly zero component is
        // parallel: a small nonzero direction can enter a thin/nearby slab.
        static bool RayTargetAabb(Vector3 origin, Vector3 dir, Vector3 bMin, Vector3 bMax,
            float maxT, float preferredT, out float distance)
        {
            float loT = 0f, hiT = maxT;
            distance = 0f;
            for (int axis = 0; axis < 3; ++axis) {
                float o = GetComponent(origin, axis), d = GetComponent(dir, axis);
                float lo = GetComponent(bMin, axis), hi = GetComponent(bMax, axis);
                if (d == 0f) { if (o < lo || o > hi) return false; }
                else {
                    float a = (lo - o) / d, b = (hi - o) / d;
                    loT = Mathf.Max(loT, Mathf.Min(a, b)); hiT = Mathf.Min(hiT, Mathf.Max(a, b));
                    if (loT > hiT) return false;
                }
            }
            distance = Mathf.Max(0f, Mathf.Max(loT - preferredT, preferredT - hiT));
            return true;
        }

        // The children the ray enters, nearer entry first; -1 for a child the ray misses
        // or that lies beyond the current best hit.
        void OrderChildren(in Node node, Vector3 origin, Vector3 dir, float maxT, out int first, out int second)
        {
            ref Node l = ref nodes[node.left];
            ref Node r = ref nodes[node.right];
            bool hitL = RayEntersAabb(origin, dir, l.bMin, l.bMax, maxT, out float tL);
            bool hitR = RayEntersAabb(origin, dir, r.bMin, r.bMax, maxT, out float tR);
            if (hitL && hitR) { if (tL <= tR) { first = node.left; second = node.right; } else { first = node.right; second = node.left; } }
            else if (hitL) { first = node.left; second = -1; }
            else if (hitR) { first = node.right; second = -1; }
            else { first = second = -1; }
        }

        // ─── Geometry helpers ───

        static float AabbDistSq(Vector3 bMin, Vector3 bMax, Vector3 p)
        {
            float dx = Mathf.Max(0, Mathf.Max(bMin.x - p.x, p.x - bMax.x));
            float dy = Mathf.Max(0, Mathf.Max(bMin.y - p.y, p.y - bMax.y));
            float dz = Mathf.Max(0, Mathf.Max(bMin.z - p.z, p.z - bMax.z));
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>
        /// Ray-AABB intersection test (slab method). Returns true if ray hits the box
        /// within [0, maxT].
        /// </summary>
        static bool RayIntersectsAabb(Vector3 origin, Vector3 dir, Vector3 bMin, Vector3 bMax, float maxT)
            => RayEntersAabb(origin, dir, bMin, bMax, maxT, out _);

        // Slab test that also reports the entry distance (0 when the origin is inside).
        static bool RayEntersAabb(Vector3 origin, Vector3 dir, Vector3 bMin, Vector3 bMax, float maxT, out float tEnter)
        {
            float tmin = 0f;
            float tmax = maxT;
            tEnter = 0f;

            for (int i = 0; i < 3; i++)
            {
                float o = GetComponent(origin, i);
                float d = GetComponent(dir, i);
                float lo = GetComponent(bMin, i);
                float hi = GetComponent(bMax, i);

                if (Mathf.Abs(d) < 1e-8f)
                {
                    // Ray parallel to slab — check if origin is within
                    if (o < lo || o > hi) return false;
                }
                else
                {
                    float invD = 1f / d;
                    float t1 = (lo - o) * invD;
                    float t2 = (hi - o) * invD;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    tmin = Mathf.Max(tmin, t1);
                    tmax = Mathf.Min(tmax, t2);
                    if (tmin > tmax) return false;
                }
            }
            tEnter = tmin;
            return true;
        }

        /// <summary>
        /// Möller–Trumbore ray-triangle intersection.
        /// Returns true if hit, with t (distance), u, v (barycentric of B, C).
        /// </summary>
        /// <summary>
        /// Per-ray part of the watertight ray–triangle test (Woop, Benthin, Wald, JCGT
        /// 2013): the ray's dominant axis kz, the other two in winding-preserving order
        /// (swapped when the ray points down kz), and the shear that maps the ray onto
        /// the unit ray (0,0,1).
        /// </summary>
        public readonly struct RayFrame
        {
            public readonly int kx, ky, kz;
            public readonly float sx, sy, sz;

            public RayFrame(Vector3 dir)
            {
                Vector3 a = new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z));
                kz = a.x > a.y ? (a.x > a.z ? 0 : 2) : (a.y > a.z ? 1 : 2);
                kx = kz + 1; if (kx == 3) kx = 0;
                ky = kx + 1; if (ky == 3) ky = 0;
                if (GetComponent(dir, kz) < 0f) { int t = kx; kx = ky; ky = t; }
                float dz = GetComponent(dir, kz);
                if (dz == 0f) dz = 1e-30f;   // a zero direction hits nothing anyway
                sx = GetComponent(dir, kx) / dz;
                sy = GetComponent(dir, ky) / dz;
                sz = 1f / dz;
            }
        }

        /// <summary>
        /// Watertight two-sided ray–triangle test (Woop, Benthin, Wald 2013). Vertices
        /// are translated to the ray origin, sheared so the ray becomes (0,0,1), and
        /// tested with three 2D edge functions; an edge that evaluates to exactly 0 is
        /// re-evaluated in double precision, and a 0 that survives counts as inside, so
        /// adjacent triangles never leak a ray between them (no cracks along shared
        /// edges or at shared vertices, which Möller–Trumbore does not guarantee). The
        /// hit is accepted when 0 ≤ t &lt; maxT; bary holds the weights of a, b, c.
        /// </summary>
        public static bool Watertight(in RayFrame k, Vector3 origin, Vector3 a, Vector3 b, Vector3 c, float maxT, out float t, out Vector3 bary)
        {
            t = 0f; bary = default;
            a -= origin; b -= origin; c -= origin;
            float az = GetComponent(a, k.kz), bz = GetComponent(b, k.kz), cz = GetComponent(c, k.kz);
            float ax = GetComponent(a, k.kx) - k.sx * az, ay = GetComponent(a, k.ky) - k.sy * az;
            float bx = GetComponent(b, k.kx) - k.sx * bz, by = GetComponent(b, k.ky) - k.sy * bz;
            float cx = GetComponent(c, k.kx) - k.sx * cz, cy = GetComponent(c, k.ky) - k.sy * cz;
            float U = cx * by - cy * bx;
            float V = ax * cy - ay * cx;
            float W = bx * ay - by * ax;
            if (U == 0f || V == 0f || W == 0f)
            {
                U = (float)((double)cx * by - (double)cy * bx);
                V = (float)((double)ax * cy - (double)ay * cx);
                W = (float)((double)bx * ay - (double)by * ax);
            }
            if ((U < 0f || V < 0f || W < 0f) && (U > 0f || V > 0f || W > 0f)) return false;
            float det = U + V + W;
            if (det == 0f) return false;   // ray in the triangle's plane, or a degenerate triangle
            float T = U * (k.sz * az) + V * (k.sz * bz) + W * (k.sz * cz);
            // Scaled depth test with the determinant's sign folded in (two-sided).
            float sign = det < 0f ? -1f : 1f;
            float Ts = T * sign, dets = det * sign;
            if (Ts < 0f || Ts >= maxT * dets) return false;
            float rcp = 1f / det;
            t = T * rcp;
            bary = new Vector3(U * rcp, V * rcp, W * rcp);
            return true;
        }

        /// <summary>
        /// Closest point on triangle ABC to point P, returns barycentric coords.
        /// Standard Ericson/Real-Time Collision Detection algorithm.
        /// </summary>
        public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c,
                                                      out Vector3 bary)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { bary = new Vector3(1, 0, 0); return a; }

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { bary = new Vector3(0, 1, 0); return b; }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v0 = d1 / (d1 - d3);
                bary = new Vector3(1 - v0, v0, 0);
                return a + v0 * ab;
            }

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { bary = new Vector3(0, 0, 1); return c; }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w0 = d2 / (d2 - d6);
                bary = new Vector3(1 - w0, 0, w0);
                return a + w0 * ac;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w0 = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                bary = new Vector3(0, 1 - w0, w0);
                return b + w0 * (c - b);
            }

            float denomSum = va + vb + vc;
            // This denominator scales with the fourth power of edge length. An
            // absolute cutoff classified valid millimetre triangles as degenerate,
            // returning a vertex instead of the surface and exceeding the trim reach.
            if (!(denomSum > 0f))
            {
                // Degenerate triangle — return nearest vertex
                float da = (p - a).sqrMagnitude;
                float db = (p - b).sqrMagnitude;
                float dc = (p - c).sqrMagnitude;
                if (da <= db && da <= dc) { bary = new Vector3(1, 0, 0); return a; }
                if (db <= dc) { bary = new Vector3(0, 1, 0); return b; }
                bary = new Vector3(0, 0, 1); return c;
            }
            float denom = 1f / denomSum;
            float sv = vb * denom;
            float sw = vc * denom;
            bary = new Vector3(1 - sv - sw, sv, sw);
            return a + sv * ab + sw * ac;
        }

        static float GetComponent(Vector3 v, int axis)
        {
            if (axis == 0) return v.x;
            if (axis == 1) return v.y;
            return v.z;
        }

        // ── GPU Serialization ──

        /// <summary>
        /// GPU-friendly BVH node (matches compute shader BVHNode struct).
        /// 10 floats + 4 ints = 56 bytes per node.
        /// </summary>
        public struct GPUNode
        {
            public Vector3 bMin;
            public Vector3 bMax;
            public int left;
            public int right;
            public int triStart;
            public int triCount;
        }

        /// <summary>
        /// Serialize BVH data for GPU compute shader.
        /// Returns: nodes array, triangle index remapping, vertices, triangle indices.
        /// </summary>
        public void GetGPUData(out GPUNode[] gpuNodes, out int[] gpuTriIndices,
            out Vector3[] gpuVerts, out int[] gpuTris)
        {
            gpuNodes = new GPUNode[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                gpuNodes[i] = new GPUNode
                {
                    bMin = nodes[i].bMin,
                    bMax = nodes[i].bMax,
                    left = nodes[i].left,
                    right = nodes[i].right,
                    triStart = nodes[i].triStart,
                    triCount = nodes[i].triCount
                };
            }
            gpuTriIndices = (int[])triIndices.Clone();
            gpuVerts = verts;  // already world-space
            gpuTris = tris;
        }
    }
}
