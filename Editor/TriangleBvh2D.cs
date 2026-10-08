// TriangleBvh2D.cs — 2D AABB BVH for nearest-triangle queries in UV space
// Mirrors TriangleBvh but works in 2D (Vector2) for UV0/UV2 lookups.
// Supports both global builds (all triangles) and per-subset builds
// (only triangles from a specific shell).

using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public class TriangleBvh2D
    {
        struct Node
        {
            public Vector2 bMin, bMax;
            public int left;
            public int right;
            public int triStart;
            public int triCount;
        }

        Node[] nodes;
        int[] triIndices;
        Vector2[] uvA, uvB, uvC; // per-face UV corners (pre-extracted)
        int nodeCount;

        const int MAX_LEAF = 4;

        public struct HitResult2D
        {
            public int faceIndex; // original face index (not local)
            public float u, v, w; // barycentric
            public float distSq;
        }

        /// <summary>
        /// Build from pre-extracted per-face UV arrays.
        /// faceList: which face indices to include (null = all faces 0..faceCount-1).
        /// </summary>
        public TriangleBvh2D(Vector2[] triA, Vector2[] triB, Vector2[] triC,
                             int[] faceList = null)
        {
            uvA = triA; uvB = triB; uvC = triC;
            int totalFaces = triA.Length;

            if (faceList != null)
            {
                triIndices = new int[faceList.Length];
                System.Array.Copy(faceList, triIndices, faceList.Length);
            }
            else
            {
                triIndices = new int[totalFaces];
                for (int i = 0; i < totalFaces; i++)
                    triIndices[i] = i;
            }

            int count = triIndices.Length;
            nodes = new Node[count * 2 + 1];
            nodeCount = 0;

            if (count > 0)
                BuildRecursive(0, count);
        }

        /// <summary>
        /// Find nearest triangle to a 2D query point.
        /// Returns face index, barycentric coords, and squared distance.
        /// </summary>
        public HitResult2D FindNearest(Vector2 queryPoint)
        {
            var best = new HitResult2D { faceIndex = -1, distSq = float.MaxValue };
            if (nodeCount > 0)
                FindNearestRecursive(0, queryPoint, ref best);
            return best;
        }

        /// <summary>
        /// Find nearest triangle with a normal consistency filter.
        /// triNormals: per-face normals for the source mesh.
        /// queryNormal: normal of the query vertex.
        /// normalDotMin: minimum dot product to accept (e.g. 0.3 to reject backfaces).
        /// Falls back to unconstrained if no normal-consistent triangle is close enough.
        /// </summary>
        public HitResult2D FindNearestNormalFiltered(
            Vector2 queryPoint, Vector3 queryNormal,
            Vector3[] triNormals, float normalDotMin)
        {
            var bestFiltered = new HitResult2D { faceIndex = -1, distSq = float.MaxValue };
            var bestAny = new HitResult2D { faceIndex = -1, distSq = float.MaxValue };

            if (nodeCount > 0)
                FindNearestNormalRecursive(0, queryPoint, queryNormal, triNormals,
                                           normalDotMin, ref bestFiltered, ref bestAny);

            // Use filtered result if it found something, else fall back to any
            return bestFiltered.faceIndex >= 0 ? bestFiltered : bestAny;
        }

        // ─── Build ───

        int BuildRecursive(int start, int count)
        {
            int idx = nodeCount++;
            ref Node node = ref nodes[idx];

            ComputeBounds(start, count, out node.bMin, out node.bMax);
            node.left = -1;
            node.right = -1;

            if (count <= MAX_LEAF)
            {
                node.triStart = start;
                node.triCount = count;
                return idx;
            }

            Vector2 extent = node.bMax - node.bMin;
            int axis = extent.y > extent.x ? 1 : 0;
            float splitVal = (axis == 0 ? node.bMin.x : node.bMin.y)
                           + (axis == 0 ? extent.x : extent.y) * 0.5f;

            int mid = Partition(start, count, axis, splitVal);
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
                float c = TriCentroidAxis(triIndices[lo], axis);
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

        float TriCentroidAxis(int face, int axis)
        {
            if (axis == 0)
                return (uvA[face].x + uvB[face].x + uvC[face].x) / 3f;
            return (uvA[face].y + uvB[face].y + uvC[face].y) / 3f;
        }

        void ComputeBounds(int start, int count, out Vector2 bMin, out Vector2 bMax)
        {
            bMin = new Vector2(float.MaxValue, float.MaxValue);
            bMax = new Vector2(float.MinValue, float.MinValue);

            for (int i = start; i < start + count; i++)
            {
                int f = triIndices[i];
                bMin = Vector2.Min(bMin, uvA[f]);
                bMin = Vector2.Min(bMin, uvB[f]);
                bMin = Vector2.Min(bMin, uvC[f]);
                bMax = Vector2.Max(bMax, uvA[f]);
                bMax = Vector2.Max(bMax, uvB[f]);
                bMax = Vector2.Max(bMax, uvC[f]);
            }
        }

        // ─── Query: standard nearest ───

        void FindNearestRecursive(int nodeIdx, Vector2 q, ref HitResult2D best)
        {
            ref Node node = ref nodes[nodeIdx];

            float boxDistSq = AabbDistSq2D(node.bMin, node.bMax, q);
            if (boxDistSq >= best.distSq) return;

            if (node.left == -1)
            {
                for (int i = node.triStart; i < node.triStart + node.triCount; i++)
                {
                    int f = triIndices[i];
                    float dSq = PointToTri2D(q, uvA[f], uvB[f], uvC[f],
                                              out float u, out float v, out float w);
                    if (dSq < best.distSq)
                    {
                        best.distSq = dSq;
                        best.faceIndex = f;
                        best.u = u; best.v = v; best.w = w;
                    }
                }
                return;
            }

            float dL = AabbDistSq2D(nodes[node.left].bMin, nodes[node.left].bMax, q);
            float dR = AabbDistSq2D(nodes[node.right].bMin, nodes[node.right].bMax, q);

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

        // ─── Query: normal-filtered nearest ───

        void FindNearestNormalRecursive(
            int nodeIdx, Vector2 q, Vector3 qNormal,
            Vector3[] triNormals, float normalDotMin,
            ref HitResult2D bestFiltered, ref HitResult2D bestAny)
        {
            ref Node node = ref nodes[nodeIdx];

            float boxDistSq = AabbDistSq2D(node.bMin, node.bMax, q);
            if (boxDistSq >= bestAny.distSq && boxDistSq >= bestFiltered.distSq) return;

            if (node.left == -1)
            {
                for (int i = node.triStart; i < node.triStart + node.triCount; i++)
                {
                    int f = triIndices[i];
                    float dSq = PointToTri2D(q, uvA[f], uvB[f], uvC[f],
                                              out float u, out float v, out float w);

                    if (dSq < bestAny.distSq)
                    {
                        bestAny.distSq = dSq;
                        bestAny.faceIndex = f;
                        bestAny.u = u; bestAny.v = v; bestAny.w = w;
                    }

                    if (f < triNormals.Length &&
                        Vector3.Dot(triNormals[f], qNormal) >= normalDotMin &&
                        dSq < bestFiltered.distSq)
                    {
                        bestFiltered.distSq = dSq;
                        bestFiltered.faceIndex = f;
                        bestFiltered.u = u; bestFiltered.v = v; bestFiltered.w = w;
                    }
                }
                return;
            }

            float dL = AabbDistSq2D(nodes[node.left].bMin, nodes[node.left].bMax, q);
            float dR = AabbDistSq2D(nodes[node.right].bMin, nodes[node.right].bMax, q);

            if (dL < dR)
            {
                FindNearestNormalRecursive(node.left, q, qNormal, triNormals, normalDotMin,
                                            ref bestFiltered, ref bestAny);
                FindNearestNormalRecursive(node.right, q, qNormal, triNormals, normalDotMin,
                                            ref bestFiltered, ref bestAny);
            }
            else
            {
                FindNearestNormalRecursive(node.right, q, qNormal, triNormals, normalDotMin,
                                            ref bestFiltered, ref bestAny);
                FindNearestNormalRecursive(node.left, q, qNormal, triNormals, normalDotMin,
                                            ref bestFiltered, ref bestAny);
            }
        }

        // ─── Geometry ───

        static float AabbDistSq2D(Vector2 bMin, Vector2 bMax, Vector2 p)
        {
            float dx = Mathf.Max(0f, Mathf.Max(bMin.x - p.x, p.x - bMax.x));
            float dy = Mathf.Max(0f, Mathf.Max(bMin.y - p.y, p.y - bMax.y));
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// Closest point on 2D triangle, returns barycentric coords and squared distance.
        /// </summary>
        internal static float PointToTri2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c,
                                   out float u, out float v, out float w)
        {
            // The Gram determinant is area squared and a fixed epsilon collapsed
            // valid small UV charts to vertex A. Cross products in double precision
            // also avoid cancellation on long, thin triangles.
            double abx = (double)b.x - a.x, aby = (double)b.y - a.y;
            double acx = (double)c.x - a.x, acy = (double)c.y - a.y;
            double apx = (double)p.x - a.x, apy = (double)p.y - a.y;
            double det = abx * acy - aby * acx;
            if (det != 0)
            {
                double bv = (apx * acy - apy * acx) / det;
                double bw = (abx * apy - aby * apx) / det;
                double bu = 1 - bv - bw;
                if (bu >= 0 && bv >= 0 && bw >= 0) {
                    u = (float)bu; v = (float)bv; w = (float)bw;
                    return 0;
                }
            }
            double best = SegmentDistance(p, a, b, out double t);
            u = (float)(1 - t); v = (float)t; w = 0;
            double distance = SegmentDistance(p, a, c, out t);
            if (distance < best) { best = distance; u = (float)(1 - t); v = 0; w = (float)t; }
            distance = SegmentDistance(p, b, c, out t);
            if (distance < best) { best = distance; u = 0; v = (float)(1 - t); w = (float)t; }
            return (float)best;
        }

        static double SegmentDistance(Vector2 p, Vector2 a, Vector2 b, out double t)
        {
            double dx = (double)b.x - a.x, dy = (double)b.y - a.y;
            double px = (double)p.x - a.x, py = (double)p.y - a.y;
            double length = dx * dx + dy * dy;
            t = length > 0 ? System.Math.Clamp((px * dx + py * dy) / length, 0, 1) : 0;
            px -= t * dx; py -= t * dy;
            return px * px + py * py;
        }
    }
}
