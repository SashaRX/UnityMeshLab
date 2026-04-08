// TriangleBvh.cs — AABB Bounding Volume Hierarchy for nearest-surface queries
// Used by SurfaceProjectionSolver to find closest source triangle for each target sample

using UnityEngine;

namespace LightmapUvTool
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

        const int MAX_LEAF = 4;

        public TriangleBvh(Vector3[] vertices, int[] triangles)
        {
            verts = vertices;
            tris = triangles;
            int faceCount = triangles.Length / 3;

            triIndices = new int[faceCount];
            for (int i = 0; i < faceCount; i++)
                triIndices[i] = i;

            nodes = new Node[faceCount * 2 + 1];
            nodeCount = 0;

            BuildRecursive(0, faceCount);
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
        {
            var best = new HitResult { triangleIndex = -1, distSq = float.MaxValue };
            FindNearestNormFiltRecursive(0, queryPoint, queryNormal, faceNormals, normalDotMin, ref best);
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
            RaycastRecursive(0, origin, direction, ref best);
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
        int BuildRecursive(int start, int count)
        {
            int idx = nodeCount++;
            ref Node node = ref nodes[idx];

            // Compute bounds
            ComputeBounds(start, count, out node.bMin, out node.bMax);
            node.left = -1;
            node.right = -1;

            if (count <= MAX_LEAF)
            {
                node.triStart = start;
                node.triCount = count;
                return idx;
            }

            // Split along longest axis
            Vector3 extent = node.bMax - node.bMin;
            int axis = 0;
            if (extent.y > extent.x) axis = 1;
            if (extent.z > (axis == 0 ? extent.x : extent.y)) axis = 2;

            float splitVal = GetComponent(node.bMin, axis) + GetComponent(extent, axis) * 0.5f;

            // Partition
            int mid = Partition(start, count, axis, splitVal);

            // Fallback: split in half if partition failed
            if (mid == start || mid == start + count)
                mid = start + count / 2;

            node.triStart = -1;
            node.triCount = 0;
            node.left = BuildRecursive(start, mid - start);
            node.right = BuildRecursive(mid, start + count - mid);

            return idx;
        }

        int Partition(int start, int count, int axis, float splitVal)
        {
            int lo = start, hi = start + count - 1;
            while (lo <= hi)
            {
                float c = TriangleCentroidAxis(triIndices[lo], axis);
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

        float TriangleCentroidAxis(int face, int axis)
        {
            int i0 = tris[face * 3], i1 = tris[face * 3 + 1], i2 = tris[face * 3 + 2];
            return (GetComponent(verts[i0], axis) +
                    GetComponent(verts[i1], axis) +
                    GetComponent(verts[i2], axis)) / 3f;
        }

        void ComputeBounds(int start, int count, out Vector3 bMin, out Vector3 bMax)
        {
            bMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            bMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = start; i < start + count; i++)
            {
                int f = triIndices[i];
                for (int j = 0; j < 3; j++)
                {
                    Vector3 v = verts[tris[f * 3 + j]];
                    bMin = Vector3.Min(bMin, v);
                    bMax = Vector3.Max(bMax, v);
                }
            }
        }

        // ─── Nearest-point query ───
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
            Vector3[] fNrm, float dotMin, ref HitResult best)
        {
            ref Node node = ref nodes[nodeIdx];

            float boxDistSq = AabbDistSq(node.bMin, node.bMax, q);
            if (boxDistSq >= best.distSq) return;

            if (node.left == -1)
            {
                for (int i = node.triStart; i < node.triStart + node.triCount; i++)
                {
                    int f = triIndices[i];
                    if (f < fNrm.Length && Vector3.Dot(fNrm[f], qNrm) < dotMin)
                        continue;

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
                FindNearestNormFiltRecursive(node.left, q, qNrm, fNrm, dotMin, ref best);
                FindNearestNormFiltRecursive(node.right, q, qNrm, fNrm, dotMin, ref best);
            }
            else
            {
                FindNearestNormFiltRecursive(node.right, q, qNrm, fNrm, dotMin, ref best);
                FindNearestNormFiltRecursive(node.left, q, qNrm, fNrm, dotMin, ref best);
            }
        }

        // ─── Raycast query ───
        void RaycastRecursive(int nodeIdx, Vector3 origin, Vector3 dir, ref RayHit best)
        {
            ref Node node = ref nodes[nodeIdx];

            // Ray-AABB intersection (slab method)
            if (!RayIntersectsAabb(origin, dir, node.bMin, node.bMax, best.t))
                return;

            // Leaf: test triangles
            if (node.left == -1)
            {
                for (int i = node.triStart; i < node.triStart + node.triCount; i++)
                {
                    int f = triIndices[i];
                    int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];

                    if (RayTriangleIntersect(origin, dir, verts[i0], verts[i1], verts[i2],
                            out float t, out float u, out float v)
                        && t >= 0f && t < best.t)
                    {
                        best.t = t;
                        best.triangleIndex = f;
                        best.barycentric = new Vector3(1f - u - v, u, v);
                    }
                }
                return;
            }

            // Traverse both children (order doesn't matter much for raycast, but
            // we could optimize by traversing closer first based on ray direction)
            RaycastRecursive(node.left, origin, dir, ref best);
            RaycastRecursive(node.right, origin, dir, ref best);
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
        {
            float tmin = 0f;
            float tmax = maxT;

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
            return true;
        }

        /// <summary>
        /// Möller–Trumbore ray-triangle intersection.
        /// Returns true if hit, with t (distance), u, v (barycentric of B, C).
        /// </summary>
        static bool RayTriangleIntersect(Vector3 origin, Vector3 dir,
            Vector3 a, Vector3 b, Vector3 c,
            out float t, out float u, out float v)
        {
            t = 0; u = 0; v = 0;
            const float EPSILON = 1e-7f;

            Vector3 edge1 = b - a;
            Vector3 edge2 = c - a;
            Vector3 h = Vector3.Cross(dir, edge2);
            float det = Vector3.Dot(edge1, h);

            if (det > -EPSILON && det < EPSILON) return false; // parallel

            float invDet = 1f / det;
            Vector3 s = origin - a;
            u = invDet * Vector3.Dot(s, h);
            if (u < 0f || u > 1f) return false;

            Vector3 q = Vector3.Cross(s, edge1);
            v = invDet * Vector3.Dot(dir, q);
            if (v < 0f || u + v > 1f) return false;

            t = invDet * Vector3.Dot(edge2, q);
            return true; // t can be negative — caller checks t >= 0
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
            if (denomSum < 1e-10f)
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
