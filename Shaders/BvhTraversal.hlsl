// BvhTraversal.hlsl — the GPU side of TriangleBvh: the same node layout
// (TriangleBvh.GetGPUData), the same ray–triangle and closest-point tests, the
// same filters (facing, normal, either-side), the same hit records. Every
// compute kernel that traces or projects includes this; none carries its own
// traversal. Keep it in step with Editor/TriangleBvh.cs.
#ifndef MESHLAB_BVH_TRAVERSAL_INCLUDED
#define MESHLAB_BVH_TRAVERSAL_INCLUDED

#define BVH_MAX_STACK 48
#define BVH_DIR_EPSILON 1e-7

struct BVHNode
{
    float3 bMin;
    float3 bMax;
    int left;
    int right;
    int triStart;
    int triCount;
};

StructuredBuffer<BVHNode> _BVHNodes;
StructuredBuffer<float3>  _TriVerts;      // vertices in the BVH's space
StructuredBuffer<int>     _TriIndices;    // leaf slot → face index
StructuredBuffer<int>     _Tris;          // face → 3 vertex indices
StructuredBuffer<float3>  _FaceNormals;   // per face, unit, oriented as the caller wants (may be unused)
StructuredBuffer<uint>    _EitherSide;    // per face, 1 = both sides count as front (1-element dummy when unused)
uint _EitherSideCount;                    // faces covered by _EitherSide; 0 = none

struct BvhRayHit
{
    int   tri;     // face index, -1 = miss
    float t;       // distance along the ray
    float u;       // barycentric of vertex 1 (vertex 0 weight = 1 - u - v)
    float v;       // barycentric of vertex 2
};

struct BvhNearestHit
{
    int    tri;    // face index, -1 = none within range
    float  distSq;
    float3 closestPoint;  // closest point on the face ("point" is an HLSL keyword)
    float3 bary;   // weights of vertices 0, 1, 2
};

bool BvhEitherSide(int f)
{
    return _EitherSideCount > (uint)f && _EitherSide[f] != 0;
}

// Slab test; tEnter is the parametric entry (0 when the origin is inside).
bool BvhRayAabb(float3 origin, float3 invDir, float3 bMin, float3 bMax, float maxT, out float tEnter)
{
    float3 t1 = (bMin - origin) * invDir;
    float3 t2 = (bMax - origin) * invDir;
    float3 tmin3 = min(t1, t2);
    float3 tmax3 = max(t1, t2);
    float tmin = max(max(tmin3.x, tmin3.y), max(tmin3.z, 0.0));
    float tmax = min(min(tmax3.x, tmax3.y), tmax3.z);
    tEnter = tmin;
    return tmin <= min(tmax, maxT);
}

// Watertight two-sided ray–triangle test (Woop, Benthin, Wald, JCGT 2013), the
// same as TriangleBvh.Watertight: vertices translated to the ray origin and sheared
// so the ray is (0,0,1), three 2D edge functions, an edge at exactly 0 counts as
// inside, so adjacent triangles never leak a ray between them. The CPU re-evaluates
// a zero edge in double precision; GPUs without doubles keep the conservative hit
// (the paper: a false positive once per ~million tests, on tiny far triangles only).
struct BvhRayFrame { int kx; int ky; int kz; float sx; float sy; float sz; };

BvhRayFrame BvhFrame(float3 dir)
{
    BvhRayFrame k;
    float3 a = abs(dir);
    k.kz = a.x > a.y ? (a.x > a.z ? 0 : 2) : (a.y > a.z ? 1 : 2);
    k.kx = k.kz + 1; if (k.kx == 3) k.kx = 0;
    k.ky = k.kx + 1; if (k.ky == 3) k.ky = 0;
    if (dir[k.kz] < 0.0) { int t = k.kx; k.kx = k.ky; k.ky = t; }
    float dz = dir[k.kz];
    if (dz == 0.0) dz = 1e-30;
    k.sx = dir[k.kx] / dz; k.sy = dir[k.ky] / dz; k.sz = 1.0 / dz;
    return k;
}

bool BvhWatertight(BvhRayFrame k, float3 origin, float3 a, float3 b, float3 c, float maxT, out float t, out float3 bary)
{
    t = 0; bary = float3(0, 0, 0);
    bool hit = false;
    a -= origin; b -= origin; c -= origin;
    float az = a[k.kz], bz = b[k.kz], cz = c[k.kz];
    float ax = a[k.kx] - k.sx * az, ay = a[k.ky] - k.sy * az;
    float bx = b[k.kx] - k.sx * bz, by = b[k.ky] - k.sy * bz;
    float cx = c[k.kx] - k.sx * cz, cy = c[k.ky] - k.sy * cz;
    float U = cx * by - cy * bx;
    float V = ax * cy - ay * cx;
    float W = bx * ay - by * ax;
    // A single initialized return also gives the Vulkan translator a defined
    // result on rejected intersections; retain the same watertight predicates.
    if (!((U < 0.0 || V < 0.0 || W < 0.0) && (U > 0.0 || V > 0.0 || W > 0.0)))
    {
        float det = U + V + W;
        if (det != 0.0)
        {
            float T = U * (k.sz * az) + V * (k.sz * bz) + W * (k.sz * cz);
            float sgn = det < 0.0 ? -1.0 : 1.0;
            float Ts = T * sgn, dets = det * sgn;
            if (!(Ts < 0.0 || Ts >= maxT * dets))
            {
                float rcp = 1.0 / det;
                t = T * rcp;
                bary = float3(U, V, W) * rcp;
                hit = true;
            }
        }
    }
    return hit;
}

float BvhAabbDistSq(float3 bMin, float3 bMax, float3 p)
{
    float3 d = max(max(bMin - p, p - bMax), 0.0);
    return dot(d, d);
}

// Closest point on triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5)
// with the weights of a, b, c — the same convention as TriangleBvh.ClosestPointOnTriangle.
float3 BvhClosestPointOnTriangle(float3 p, float3 a, float3 b, float3 c, out float3 bary)
{
    float3 closest = a;
    bary = float3(1, 0, 0);
    float3 ab = b - a, ac = c - a, ap = p - a;
    float d1 = dot(ab, ap), d2 = dot(ac, ap);
    if (!(d1 <= 0.0 && d2 <= 0.0))
    {
        float3 bp = p - b;
        float d3 = dot(ab, bp), d4 = dot(ac, bp);
        if (d3 >= 0.0 && d4 <= d3)
        {
            bary = float3(0, 1, 0);
            closest = b;
        }
        else
        {
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0)
            {
                float v = d1 / (d1 - d3);
                bary = float3(1 - v, v, 0);
                closest = a + ab * v;
            }
            else
            {
                float3 cp = p - c;
                float d5 = dot(ab, cp), d6 = dot(ac, cp);
                if (d6 >= 0.0 && d5 <= d6)
                {
                    bary = float3(0, 0, 1);
                    closest = c;
                }
                else
                {
                    float vb = d5 * d2 - d1 * d6;
                    if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0)
                    {
                        float w = d2 / (d2 - d6);
                        bary = float3(1 - w, 0, w);
                        closest = a + ac * w;
                    }
                    else
                    {
                        float va = d3 * d6 - d5 * d4;
                        if (va <= 0.0 && (d4 - d3) >= 0.0 && (d5 - d6) >= 0.0)
                        {
                            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                            bary = float3(0, 1 - w, w);
                            closest = b + (c - b) * w;
                        }
                        else
                        {
                            float denom = 1.0 / (va + vb + vc);
                            float v2 = vb * denom, w2 = vc * denom;
                            bary = float3(1 - v2 - w2, v2, w2);
                            closest = a + ab * v2 + ac * w2;
                        }
                    }
                }
            }
        }
    }
    return closest;
}

// Closest hit along the ray within maxDist. facingFilter rejects faces whose
// normal points along the ray (the surfaces the ray cannot "see"), except
// either-side faces. Children are visited nearer first, so a hit prunes the rest.
BvhRayHit BvhRaycast(float3 origin, float3 dir, float maxDist, bool facingFilter)
{
    BvhRayHit best;
    best.tri = -1; best.t = maxDist; best.u = 0; best.v = 0;
    if (!(maxDist <= 0.0))
    {
        float3 invDir = float3(
            abs(dir.x) > BVH_DIR_EPSILON ? 1.0 / dir.x : (dir.x >= 0 ? 1e20 : -1e20),
            abs(dir.y) > BVH_DIR_EPSILON ? 1.0 / dir.y : (dir.y >= 0 ? 1e20 : -1e20),
            abs(dir.z) > BVH_DIR_EPSILON ? 1.0 / dir.z : (dir.z >= 0 ? 1e20 : -1e20));
        BvhRayFrame frame = BvhFrame(dir);
        int stack[BVH_MAX_STACK];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            int nodeIdx = stack[--sp];
            BVHNode node = _BVHNodes[nodeIdx];
            float tEnter;
            if (!BvhRayAabb(origin, invDir, node.bMin, node.bMax, best.t, tEnter)) continue;
            if (node.left == -1)
            {
                for (int i = node.triStart; i < node.triStart + node.triCount; i++)
                {
                    int f = _TriIndices[i];
                    if (facingFilter && dot(_FaceNormals[f], dir) > 0.0 && !BvhEitherSide(f)) continue;
                    float3 a = _TriVerts[_Tris[f * 3]];
                    float3 b = _TriVerts[_Tris[f * 3 + 1]];
                    float3 c = _TriVerts[_Tris[f * 3 + 2]];
                    float t; float3 bary;
                    if (BvhWatertight(frame, origin, a, b, c, best.t, t, bary))
                    {
                        best.t = t; best.tri = f; best.u = bary.y; best.v = bary.z;
                    }
                }
                continue;
            }
            // Nearer child on top of the stack.
            BVHNode l = _BVHNodes[node.left];
            BVHNode r = _BVHNodes[node.right];
            float tl, tr;
            bool hitL = BvhRayAabb(origin, invDir, l.bMin, l.bMax, best.t, tl);
            bool hitR = BvhRayAabb(origin, invDir, r.bMin, r.bMax, best.t, tr);
            if (sp + 2 > BVH_MAX_STACK) continue;
            // Push each hit child independently. The combined hitL && hitR branch with
            // two stack writes crashes FXC's optimizer (including Windows SDK 26100).
            // This keeps the same near-first order without that compiler failure.
            if (tl <= tr)
            {
                if (hitR) stack[sp++] = node.right;
                if (hitL) stack[sp++] = node.left;
            }
            else
            {
                if (hitL) stack[sp++] = node.left;
                if (hitR) stack[sp++] = node.right;
            }
        }
    }
    return best;
}

// Lower bound for a closest-to-target query: distance from preferredT to the
// box's intersection interval on the original segment. Do not prune by best.t.
bool BvhRayTargetAabb(float3 origin, float3 dir, float3 bMin, float3 bMax,
    float maxT, float preferredT, out float distance)
{
    float loT = 0.0, hiT = maxT;
    distance = 0.0;
    bool intersects = true;
    [unroll] for (int axis = 0; axis < 3; ++axis)
    {
        if (intersects)
        {
            float o = origin[axis], d = dir[axis];
            // A nonzero component can still enter a slab from just outside its edge.
            // Keep the ordinary first-hit traversal's epsilon policy unchanged.
            if (d == 0.0)
            {
                if (o < bMin[axis] || o > bMax[axis]) intersects = false;
            }
            else
            {
                float a = (bMin[axis] - o) / d, b = (bMax[axis] - o) / d;
                loT = max(loT, min(a, b)); hiT = min(hiT, max(a, b));
                if (loT > hiT) intersects = false;
            }
        }
    }
    if (intersects) distance = max(0.0, max(loT - preferredT, preferredT - hiT));
    return intersects;
}

// Positive-preference surface-transfer query. The query kernel selects the
// ordinary first-hit traversal separately for nonpositive preferences: combining
// the two traversal loops behind an early return confuses FXC's
// definite-initialization analysis. Preserve actual hit t in either path.
BvhRayHit BvhRaycastClosestToTarget(float3 origin, float3 dir, float maxDist, bool facingFilter, float preferredT)
{
    BvhRayHit best;
    best.tri = -1; best.t = maxDist; best.u = 0; best.v = 0;
    if (maxDist > 0.0)
    {
        preferredT = min(preferredT, maxDist);
        float distance = max(preferredT, maxDist - preferredT);
        BvhRayFrame frame = BvhFrame(dir);
        int stack[BVH_MAX_STACK];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            BVHNode node = _BVHNodes[stack[--sp]];
            float bound;
            if (!BvhRayTargetAabb(origin, dir, node.bMin, node.bMax, maxDist, preferredT, bound) || bound > distance) continue;
            if (node.left == -1)
            {
                for (int i = node.triStart; i < node.triStart + node.triCount; ++i)
                {
                    int f = _TriIndices[i];
                    if (facingFilter && dot(_FaceNormals[f], dir) > 0.0 && !BvhEitherSide(f)) continue;
                    float3 a = _TriVerts[_Tris[f * 3]], b = _TriVerts[_Tris[f * 3 + 1]], c = _TriVerts[_Tris[f * 3 + 2]];
                    float t; float3 bary;
                    if (!BvhWatertight(frame, origin, a, b, c, maxDist, t, bary)) continue;
                    float candidate = abs(t - preferredT);
                    if (candidate > distance || (candidate == distance && best.tri >= 0
                        && (t > best.t || (t == best.t && f >= best.tri)))) continue;
                    distance = candidate; best.t = t; best.tri = f; best.u = bary.y; best.v = bary.z;
                }
                continue;
            }
            BVHNode l = _BVHNodes[node.left], r = _BVHNodes[node.right];
            float dl, dr;
            bool hitL = BvhRayTargetAabb(origin, dir, l.bMin, l.bMax, maxDist, preferredT, dl) && dl <= distance;
            bool hitR = BvhRayTargetAabb(origin, dir, r.bMin, r.bMax, maxDist, preferredT, dr) && dr <= distance;
            if (sp + 2 > BVH_MAX_STACK) continue;
            // Separate stack writes preserve compatibility with FXC's optimizer.
            if (dl <= dr)
            {
                if (hitR) stack[sp++] = node.right;
                if (hitL) stack[sp++] = node.left;
            }
            else
            {
                if (hitL) stack[sp++] = node.left;
                if (hitR) stack[sp++] = node.right;
            }
        }
    }
    return best;
}

// Nearest face to q within maxDistSq. normalFilter keeps only faces whose normal
// has dot >= dotMin with qNormal (|dot| for either-side faces).
BvhNearestHit BvhNearest(float3 q, float maxDistSq, bool normalFilter, float3 qNormal, float dotMin)
{
    BvhNearestHit best;
    best.tri = -1; best.distSq = maxDistSq; best.closestPoint = q; best.bary = float3(0, 0, 0);
    int stack[BVH_MAX_STACK];
    int sp = 0;
    stack[sp++] = 0;
    while (sp > 0)
    {
        int nodeIdx = stack[--sp];
        BVHNode node = _BVHNodes[nodeIdx];
        if (BvhAabbDistSq(node.bMin, node.bMax, q) >= best.distSq) continue;
        if (node.left == -1)
        {
            for (int i = node.triStart; i < node.triStart + node.triCount; i++)
            {
                int f = _TriIndices[i];
                if (normalFilter)
                {
                    float d = dot(_FaceNormals[f], qNormal);
                    if (BvhEitherSide(f)) d = abs(d);
                    if (d < dotMin) continue;
                }
                float3 a = _TriVerts[_Tris[f * 3]];
                float3 b = _TriVerts[_Tris[f * 3 + 1]];
                float3 c = _TriVerts[_Tris[f * 3 + 2]];
                float3 bary;
                float3 closest = BvhClosestPointOnTriangle(q, a, b, c, bary);
                float dSq = dot(closest - q, closest - q);
                if (dSq < best.distSq)
                {
                    best.distSq = dSq; best.tri = f; best.closestPoint = closest; best.bary = bary;
                }
            }
            continue;
        }
        BVHNode l = _BVHNodes[node.left];
        BVHNode r = _BVHNodes[node.right];
        float dl = BvhAabbDistSq(l.bMin, l.bMax, q);
        float dr = BvhAabbDistSq(r.bMin, r.bMax, q);
        if (sp + 2 > BVH_MAX_STACK) continue;
        // Nearer child on top of the stack.
        if (dl <= dr) { if (dr < best.distSq) stack[sp++] = node.right; if (dl < best.distSq) stack[sp++] = node.left; }
        else          { if (dl < best.distSq) stack[sp++] = node.left;  if (dr < best.distSq) stack[sp++] = node.right; }
    }
    return best;
}

#endif
