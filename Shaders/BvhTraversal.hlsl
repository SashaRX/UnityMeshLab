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
    float3 point;  // closest point on the face
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

// Möller–Trumbore. The parallel test is relative to the triangle's own scale
// (|det| <= |e1|·|e2|), so millimetre meshes register hits like metre ones.
bool BvhRayTriangle(float3 origin, float3 dir, float3 a, float3 b, float3 c, out float t, out float u, out float v)
{
    t = 0; u = 0; v = 0;
    float3 edge1 = b - a;
    float3 edge2 = c - a;
    float3 h = cross(dir, edge2);
    float det = dot(edge1, h);
    if (abs(det) <= 1e-7 * sqrt(dot(edge1, edge1) * dot(edge2, edge2))) return false;
    float invDet = 1.0 / det;
    float3 s = origin - a;
    u = invDet * dot(s, h);
    if (u < 0.0 || u > 1.0) return false;
    float3 q = cross(s, edge1);
    v = invDet * dot(dir, q);
    if (v < 0.0 || u + v > 1.0) return false;
    t = invDet * dot(edge2, q);
    return true;
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
    float3 ab = b - a, ac = c - a, ap = p - a;
    float d1 = dot(ab, ap), d2 = dot(ac, ap);
    if (d1 <= 0.0 && d2 <= 0.0) { bary = float3(1, 0, 0); return a; }
    float3 bp = p - b;
    float d3 = dot(ab, bp), d4 = dot(ac, bp);
    if (d3 >= 0.0 && d4 <= d3) { bary = float3(0, 1, 0); return b; }
    float vc = d1 * d4 - d3 * d2;
    if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0) { float v = d1 / (d1 - d3); bary = float3(1 - v, v, 0); return a + ab * v; }
    float3 cp = p - c;
    float d5 = dot(ab, cp), d6 = dot(ac, cp);
    if (d6 >= 0.0 && d5 <= d6) { bary = float3(0, 0, 1); return c; }
    float vb = d5 * d2 - d1 * d6;
    if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0) { float w = d2 / (d2 - d6); bary = float3(1 - w, 0, w); return a + ac * w; }
    float va = d3 * d6 - d5 * d4;
    if (va <= 0.0 && (d4 - d3) >= 0.0 && (d5 - d6) >= 0.0) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); bary = float3(0, 1 - w, w); return b + (c - b) * w; }
    float denom = 1.0 / (va + vb + vc);
    float v2 = vb * denom, w2 = vc * denom;
    bary = float3(1 - v2 - w2, v2, w2);
    return a + ab * v2 + ac * w2;
}

// Closest hit along the ray within maxDist. facingFilter rejects faces whose
// normal points along the ray (the surfaces the ray cannot "see"), except
// either-side faces. Children are visited nearer first, so a hit prunes the rest.
BvhRayHit BvhRaycast(float3 origin, float3 dir, float maxDist, bool facingFilter)
{
    BvhRayHit best;
    best.tri = -1; best.t = maxDist; best.u = 0; best.v = 0;
    if (maxDist <= 0.0) return best;
    float3 invDir = float3(
        abs(dir.x) > BVH_DIR_EPSILON ? 1.0 / dir.x : (dir.x >= 0 ? 1e20 : -1e20),
        abs(dir.y) > BVH_DIR_EPSILON ? 1.0 / dir.y : (dir.y >= 0 ? 1e20 : -1e20),
        abs(dir.z) > BVH_DIR_EPSILON ? 1.0 / dir.z : (dir.z >= 0 ? 1e20 : -1e20));
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
                float t, u, v;
                if (BvhRayTriangle(origin, dir, a, b, c, t, u, v) && t >= 0.0 && t < best.t)
                {
                    best.t = t; best.tri = f; best.u = u; best.v = v;
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
        if (hitL && hitR)
        {
            if (tl <= tr) { stack[sp++] = node.right; stack[sp++] = node.left; }
            else          { stack[sp++] = node.left;  stack[sp++] = node.right; }
        }
        else if (hitL) stack[sp++] = node.left;
        else if (hitR) stack[sp++] = node.right;
    }
    return best;
}

// Nearest face to q within maxDistSq. normalFilter keeps only faces whose normal
// has dot >= dotMin with qNormal (|dot| for either-side faces).
BvhNearestHit BvhNearest(float3 q, float maxDistSq, bool normalFilter, float3 qNormal, float dotMin)
{
    BvhNearestHit best;
    best.tri = -1; best.distSq = maxDistSq; best.point = q; best.bary = float3(0, 0, 0);
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
                    best.distSq = dSq; best.tri = f; best.point = closest; best.bary = bary;
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
