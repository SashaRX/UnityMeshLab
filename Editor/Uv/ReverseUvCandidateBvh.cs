using System;
using UnityEngine;
namespace SashaRX.UnityMeshLab
{

    // Compatible adapter: native broad phase, original managed primitive arithmetic.
    // Candidate slots follow the original BVH partition; dynamic box pruning and
    // child/face ordering retain TriangleBvh's tie behavior.
    internal sealed class ReverseUvCandidateBvh
    {
        readonly TriangleBvh original;
        readonly TriangleBvh.GPUNode[] nodes;
        readonly int[] order, slotByFace, start, end, parents, leafByFace, depth;
        readonly Vector3[] vertices, normals;
        readonly int[] indices;
        readonly bool[] either;
        readonly float padding;
        public ReverseUvCandidateBvh(TriangleBvh tree, Vector3[] faceNormals, bool[] eitherSide)
        {
            original = tree;
            normals = faceNormals;
            either = eitherSide;
            tree.GetGPUData(out nodes, out order, out vertices, out indices);
            slotByFace = new int[order.Length];
            for (int i = 0; i < order.Length; ++i)
                slotByFace[order[i]] = i;
            start = new int[nodes.Length];
            end = new int[nodes.Length];
            parents = new int[nodes.Length];
            Array.Fill(parents, -1);
            leafByFace = new int[order.Length];
            depth = new int[nodes.Length];
            float magnitude = 1;
            foreach (var v in vertices)
                magnitude = Mathf.Max(magnitude, Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z))));
            padding = magnitude * 4e-5f;
            for (int i = nodes.Length - 1; i >= 0; --i)
            {
                var n = nodes[i];
                if (n.left < 0)
                {
                    start[i] = n.triStart;
                    end[i] = n.triStart + n.triCount;
                }
                else
                {
                    start[i] = start[n.left];
                    end[i] = end[n.right];
                }
            }
            for (int i = 0; i < nodes.Length; ++i)
            {
                var n = nodes[i];
                if (n.left >= 0)
                {
                    parents[n.left] = parents[n.right] = i;
                    depth[n.left] = depth[n.right] = depth[i] + 1;
                }
                else
                for (int slot = n.triStart; slot < n.triStart + n.triCount; ++slot)
                    leafByFace[order[slot]] = i;
            }
        }
        public ReverseUvQueryBackend.Query Region(ReverseUvQueryBackend.Query q, ReverseUvQueryBackend.Answer proposal)
        {
            int face = proposal.tri;
            if (face < 0 || face >= order.Length || !(q.reach > 0) || !float.IsFinite(q.reach))
                return q;
            if (q.mode == 2 && !(q.target > 0))
                return q;
            if (q.mode == 4 && face < normals.Length)
            {
                float dot = Vector3.Dot(normals[face], q.direction);
                if (TwoSided(face))
                    dot = Mathf.Abs(dot);
                if (dot < q.dotMin)
                    return q;
            }
            else if ((q.mode == 1 || q.mode == 2) && Vector3.Dot(normals[face], q.direction) > 0 && !TwoSided(face))
                return q;
            var walk = new Walk(this, q, Array.Empty<int>());
            if (!walk.ReachableLeaf(leafByFace[face]))
                return q;
            Vector3 a = vertices[indices[face * 3]], b = vertices[indices[face * 3 + 1]], c = vertices[indices[face * 3 + 2]];
            float pad = padding + Mathf.Abs(q.reach) * 1e-5f;
            if (q.mode >= 3)
            {
                var p = TriangleBvh.ClosestPointOnTriangle(q.origin, a, b, c, out _);
                float distSq = (p - q.origin).sqrMagnitude;
                if (!float.IsFinite(distSq) || !(distSq < q.reach * q.reach))
                    return q;
                q.reach = Mathf.Min(q.reach, Mathf.Sqrt(distSq) + pad);
                return q;
            }
            var frame = new TriangleBvh.RayFrame(q.direction);
            if (!TriangleBvh.Watertight(in frame, q.origin, a, b, c, q.reach, out float t, out _) || !float.IsFinite(t))
                return q;
            if (q.mode != 2)
            {
                q.reach = Mathf.Min(q.reach, t + pad);
                return q;
            }
            float preferred = Mathf.Min(q.target, q.reach), delta = Mathf.Abs(t - preferred) + pad;
            float first = Mathf.Max(0, preferred - delta), last = Mathf.Min(q.reach, preferred + delta);
            q.origin = q.origin + first * q.direction;
            q.reach = last - first;
            return q;
        }
        bool TwoSided(int face) => either != null && face < either.Length && either[face];
        public ReverseUvQueryBackend.Answer Resolve(ReverseUvQueryBackend.Query q, int[] candidates, int offset, int count, int[] scratch)
        {
            if (count < 0)
                return Fallback(q);
            for (int i = 0; i < count; ++i)
            {
                int face = candidates[offset + i];
                if (face < 0 || face >= slotByFace.Length)
                    return Fallback(q);
                scratch[i] = slotByFace[face];
            }
            Array.Sort(scratch, 0, count);
            // Duplicate callbacks for one primitive are harmless; preserve identity
            // of distinct seam faces, including coincident triangles.
            int unique = 0;
            for (int i = 0; i < count; ++i)
            if (unique == 0 || scratch[i] != scratch[unique - 1])
                scratch[unique++] = scratch[i];
            if (q.mode == 2)
            {
                if (!(q.target > 0))
                    q.mode = 1;
                else
                    q.target = Mathf.Min(q.target, q.reach);
            }
            var state = new Walk(this, q, scratch);
            if (unique > 0)
            {
                // Only leaf visits can change the winner. Parent bounds contain the
                // leaf bounds; a passing leaf also passes its ancestors. Recover
                // relative leaf order at each pair's lowest common ancestor instead
                // of repeating all empty parent paths for a handful of candidates.
                int groups = 0;
                for (int first = 0; first < unique;)
                {
                    int leaf = leafByFace[order[scratch[first]]], limit = first + 1;
                    while (limit < unique && scratch[limit] < end[leaf])
                        ++limit;
                    int insert = groups;
                    while (insert > 0 && state.LeafBefore(leaf, leafByFace[order[scratch[scratch[unique + insert - 1]]]]))
                    {
                        scratch[unique + insert] = scratch[unique + insert - 1];
                        --insert;
                    }
                    scratch[unique + insert] = first;
                    ++groups;
                    first = limit;
                }
                for (int group = 0; group < groups; ++group)
                {
                    int first = scratch[unique + group], leaf = leafByFace[order[scratch[first]]], limit = first + 1;
                    while (limit < unique && scratch[limit] < end[leaf])
                        ++limit;
                    state.Visit(leaf, first, limit);
                }
            }
            return state.best;
        }
        ReverseUvQueryBackend.Answer Fallback(ReverseUvQueryBackend.Query q)
        {
            if (q.mode >= 3)
            {
                var h = q.mode == 3 ? original.FindNearest(q.origin, q.reach) : original.FindNearestNormalFiltered(q.origin, q.direction, normals, q.dotMin, q.reach, either);
                return new ReverseUvQueryBackend.Answer { tri = h.triangleIndex, distance = h.distSq, bary = h.barycentric, point = h.point };
            }
            var ray = q.mode == 0 ? original.Raycast(q.origin, q.direction, q.reach) : q.mode == 1 ? original.RaycastFacingFiltered(q.origin, q.direction, q.reach, normals, either) :
                original.RaycastClosestToTarget(q.origin, q.direction, q.reach, q.target, normals, either);
            return new ReverseUvQueryBackend.Answer { tri = ray.triangleIndex, distance = ray.t, bary = ray.barycentric, point = ray.triangleIndex >= 0 ? q.origin + ray.t * q.direction : Vector3.zero };
        }
        struct Walk
        {
            readonly ReverseUvCandidateBvh tree; readonly ReverseUvQueryBackend.Query q; readonly int[] slots;
            readonly TriangleBvh.RayFrame frame; float distance;
            public ReverseUvQueryBackend.Answer best;
            public Walk(ReverseUvCandidateBvh tree, ReverseUvQueryBackend.Query query, int[] candidates)
            {
                this.tree = tree;
                q = query;
                slots = candidates;
                frame = new TriangleBvh.RayFrame(q.direction);
                best = new ReverseUvQueryBackend.Answer { tri = -1, distance = q.mode >= 3 ? (q.reach >= float.MaxValue ? float.MaxValue : q.reach * q.reach) : q.reach };
                distance = Mathf.Max(q.target, q.reach - q.target);
            }
            public void Visit(int index, int first, int limit)
            {
                if (first == limit)
                    return;
                var n = tree.nodes[index];
                if (q.mode >= 3)
                {
                    if (BoxDistance(n, q.origin) >= best.distance)
                        return;
                }
                else if (q.mode == 2)
                {
                    if (!TargetBox(n, out float bound) || bound > distance)
                        return;
                }
                else if (!RayBox(n, best.distance, out _))
                    return;
                if (n.left < 0)
                {
                    for (int i = first; i < limit; ++i)
                        Face(tree.order[slots[i]]);
                    return;
                }
                int split = first, high = limit;
                while (split < high)
                {
                    int middle = split + (high - split) / 2;
                    if (slots[middle] < tree.end[n.left])
                        split = middle + 1;
                    else
                        high = middle;
                }
                var left = tree.nodes[n.left];
                var right = tree.nodes[n.right];
                if (q.mode >= 3)
                {
                    float l = BoxDistance(left, q.origin), r = BoxDistance(right, q.origin);
                    if (l < r)
                    {
                        Visit(n.left, first, split);
                        Visit(n.right, split, limit);
                    }
                    else
                    {
                        Visit(n.right, split, limit);
                        Visit(n.left, first, split);
                    }
                    return;
                }
                float dl, dr;
                bool hitL = q.mode == 2 ? TargetBox(left, out dl) : RayBox(left, best.distance, out dl);
                bool hitR = q.mode == 2 ? TargetBox(right, out dr) : RayBox(right, best.distance, out dr);
                bool rightFirst = q.mode == 2 ? ((!hitL && hitR) || (hitL && hitR && dr < dl)) : (hitR && (!hitL || dr < dl));
                if (rightFirst)
                {
                    if (hitR)
                        Visit(n.right, split, limit);
                    if (hitL)
                        Visit(n.left, first, split);
                }
                else
                {
                    if (hitL)
                        Visit(n.left, first, split);
                    if (hitR)
                        Visit(n.right, split, limit);
                }
            }
            public bool ReachableLeaf(int leaf)
            {
                if (q.mode >= 3)
                    return BoxDistance(tree.nodes[leaf], q.origin) < best.distance;
                if (q.mode == 2)
                    return TargetBox(tree.nodes[leaf], out float bound) && bound <= distance;
                return RayBox(tree.nodes[leaf], q.reach, out _);
            }
            public bool LeafBefore(int leftLeaf, int rightLeaf)
            {
                int a = leftLeaf, b = rightLeaf;
                while (tree.depth[a] > tree.depth[b])
                    a = tree.parents[a];
                while (tree.depth[b] > tree.depth[a])
                    b = tree.parents[b];
                while (tree.parents[a] != tree.parents[b])
                {
                    a = tree.parents[a];
                    b = tree.parents[b];
                }
                var parent = tree.nodes[tree.parents[a]];
                bool inLeft = tree.start[leftLeaf] < tree.end[parent.left];
                var left = tree.nodes[parent.left];
                var right = tree.nodes[parent.right];
                if (q.mode >= 3)
                    return inLeft == (BoxDistance(left, q.origin) < BoxDistance(right, q.origin));
                float dl, dr;
                bool hitL = q.mode == 2 ? TargetBox(left, out dl) : RayBox(left, q.reach, out dl);
                bool hitR = q.mode == 2 ? TargetBox(right, out dr) : RayBox(right, q.reach, out dr);
                bool rightFirst = q.mode == 2 ? ((!hitL && hitR) || (hitL && hitR && dr < dl)) : (hitR && (!hitL || dr < dl));
                return inLeft != rightFirst;
            }
            void Face(int f)
            {
                if (q.mode == 4 && f < tree.normals.Length)
                {
                    float dot = Vector3.Dot(tree.normals[f], q.direction);
                    if (tree.TwoSided(f))
                        dot = Mathf.Abs(dot);
                    if (dot < q.dotMin)
                        return;
                }
                else if ((q.mode == 1 || q.mode == 2)
                    && Vector3.Dot(tree.normals[f], q.direction) > 0 && !tree.TwoSided(f))
                    return;
                Vector3 a = tree.vertices[tree.indices[f * 3]], b = tree.vertices[tree.indices[f * 3 + 1]], c = tree.vertices[tree.indices[f * 3 + 2]];
                if (q.mode >= 3)
                {
                    Vector3 p = TriangleBvh.ClosestPointOnTriangle(q.origin, a, b, c, out var bary);
                    float d = (p - q.origin).sqrMagnitude;
                    if (d < best.distance)
                        best = new ReverseUvQueryBackend.Answer { tri = f, distance = d, bary = bary, point = p };
                    return;
                }
                if (!TriangleBvh.Watertight(in frame, q.origin, a, b, c, q.mode == 2 ? q.reach : best.distance, out float t, out var weights))
                    return;
                if (q.mode == 2)
                {
                    float candidate = Mathf.Abs(t - q.target);
                    int distanceOrder = candidate.CompareTo(distance), hitOrder = t.CompareTo(best.distance);
                    if (distanceOrder > 0 || (distanceOrder == 0 && best.tri >= 0 && (hitOrder > 0 || (hitOrder == 0 && f >= best.tri))))
                        return;
                    distance = candidate;
                }
                best = new ReverseUvQueryBackend.Answer { tri = f, distance = t, bary = weights, point = q.origin + t * q.direction };
            }
            static float Component(Vector3 p, int axis) => axis == 0 ? p.x : axis == 1 ? p.y : p.z;
            static float BoxDistance(TriangleBvh.GPUNode n, Vector3 p)
            {
                float x = Mathf.Max(0, Mathf.Max(n.bMin.x - p.x, p.x - n.bMax.x)), y = Mathf.Max(0, Mathf.Max(n.bMin.y - p.y, p.y - n.bMax.y)), z = Mathf.Max(0, Mathf.Max(n.bMin.z - p.z, p.z - n.bMax.z));
                return x * x + y * y + z * z;
            }
            bool RayBox(TriangleBvh.GPUNode n, float maxT, out float enter)
            {
                float min = 0, max = maxT;
                enter = 0;
                for (int axis = 0; axis < 3; ++axis)
                {
                    float o = Component(q.origin, axis), d = Component(q.direction, axis), lo = Component(n.bMin, axis), hi = Component(n.bMax, axis);
                    if (Mathf.Abs(d) < 1e-8f)
                    {
                        if (o < lo || o > hi)
                            return false;
                    }
                    else
                    {
                        float inverse = 1 / d, a = (lo - o) * inverse, b = (hi - o) * inverse;
                        if (a > b)
                        {
                            float tmp = a;
                            a = b;
                            b = tmp;
                        }
                        min = Mathf.Max(min, a);
                        max = Mathf.Min(max, b);
                        if (min > max)
                            return false;
                    }
                }
                enter = min;
                return true;
            }
            bool TargetBox(TriangleBvh.GPUNode n, out float bound)
            {
                float min = 0, max = q.reach;
                bound = 0;
                for (int axis = 0; axis < 3; ++axis)
                {
                    float o = Component(q.origin, axis), d = Component(q.direction, axis), lo = Component(n.bMin, axis), hi = Component(n.bMax, axis);
                    if (d == 0)
                    {
                        if (o < lo || o > hi)
                            return false;
                    }
                    else
                    {
                        float a = (lo - o) / d, b = (hi - o) / d;
                        min = Mathf.Max(min, Mathf.Min(a, b));
                        max = Mathf.Min(max, Mathf.Max(a, b));
                        if (min > max)
                            return false;
                    }
                }
                bound = Mathf.Max(0, Mathf.Max(min - q.target, q.target - max));
                return true;
            }
        }
    }

}
