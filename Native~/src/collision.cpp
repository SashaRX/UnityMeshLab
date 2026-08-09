// collision.cpp
// Native bridge: V-HACD convex decomposition for Unity collision mesh generation.
// Part of xatlas-unity shared library.

#define ENABLE_VHACD_IMPLEMENTATION 1
#include "VHACD.h"

#include <vector>
#include <cstring>
#include <cstdint>

#ifdef _WIN32
#define EXPORT extern "C" __declspec(dllexport)
#else
#define EXPORT extern "C" __attribute__((visibility("default")))
#endif

struct ConvexHullData
{
    std::vector<float> vertices;  // x,y,z,x,y,z,...
    std::vector<int>   indices;   // i0,i1,i2,...
    int vertexCount;
    int indexCount;
};

struct DecompResult
{
    std::vector<ConvexHullData> hulls;
};

// ══════════════════════════════════════════════════════════════════
// Convex Decomposition — V-HACD 4.x handle-based API
//
// Workflow:
//   1. ConvexDecomp_Compute   → returns handle (IntPtr in C#)
//   2. ConvexDecomp_GetHull*  → query hull count and data
//   3. ConvexDecomp_Destroy   → free memory
// ══════════════════════════════════════════════════════════════════

EXPORT void* ConvexDecomp_Compute(
    const float* vertices,
    int          vertexCount,
    const int*   indices,
    int          indexCount,
    int          maxHulls,
    int          resolution,
    int          maxVertsPerHull,
    float        minVolumePerHull,
    int          maxRecursionDepth,
    int          shrinkWrap,
    int          fillMode,
    int          minEdgeLength,
    int          findBestPlane)
{
    if (!vertices || !indices || vertexCount <= 0 || indexCount < 3 || indexCount % 3 != 0)
        return nullptr;

    // V-HACD treats indices as unsigned and dereferences them without bounds checks.
    // Validate at the native API boundary so malformed meshes cannot cause OOB reads.
    for (int i = 0; i < indexCount; i++)
    {
        if (indices[i] < 0 || indices[i] >= vertexCount)
            return nullptr;
    }

    // The remaining tunables are handed to V-HACD as uint32_t or as an enum.
    // A negative int wraps to a huge unsigned value there — a negative
    // resolution becomes a multi-billion-voxel grid, a negative maxHulls an
    // effectively unbounded hull budget — and a fillMode outside the enum
    // selects no case at all. Reject them at the ABI boundary; the editor-side
    // ranges (maxHulls 1..64, resolution 10000..1000000, maxVertsPerHull
    // 8..255, minEdgeLength 1..8, fillMode 0..2) all stay valid.
    if (maxHulls <= 0 || resolution <= 0 || maxVertsPerHull <= 0 || minEdgeLength <= 0)
        return nullptr;
    if (fillMode < 0 || fillMode > 2)
        return nullptr;

    // Recursive splitting can create 2^depth intermediate hulls, followed by an
    // O(n²) merge-cost allocation. Treat the native ABI as a trust boundary so
    // callers cannot bypass the editor-side work-budget limit.
    constexpr int kMaxSafeRecursionDepth = 10;
    if (maxRecursionDepth < 1)
        maxRecursionDepth = 1;
    else if (maxRecursionDepth > kMaxSafeRecursionDepth)
        maxRecursionDepth = kMaxSafeRecursionDepth;

    VHACD::IVHACD* vhacd = VHACD::CreateVHACD();
    if (!vhacd)
        return nullptr;

    VHACD::IVHACD::Parameters params;
    params.m_maxConvexHulls                    = (uint32_t)maxHulls;
    params.m_resolution                        = (uint32_t)resolution;
    params.m_maxNumVerticesPerCH               = (uint32_t)maxVertsPerHull;
    params.m_minimumVolumePercentErrorAllowed   = (double)minVolumePerHull;
    params.m_maxRecursionDepth                 = (uint32_t)maxRecursionDepth;
    params.m_shrinkWrap                        = (shrinkWrap != 0);
    params.m_fillMode                          = (VHACD::FillMode)fillMode;
    params.m_minEdgeLength                     = (uint32_t)minEdgeLength;
    params.m_findBestPlane                     = (findBestPlane != 0);
    params.m_asyncACD                          = false; // synchronous — block until done

    uint32_t triangleCount = (uint32_t)(indexCount / 3);

    // V-HACD Compute accepts float* directly (x,y,z interleaved)
    // and uint32_t* for triangle indices
    bool ok = vhacd->Compute(
        vertices,
        (uint32_t)vertexCount,
        reinterpret_cast<const uint32_t*>(indices),
        triangleCount,
        params);

    if (!ok)
    {
        vhacd->Release();
        return nullptr;
    }

    uint32_t hullCount = vhacd->GetNConvexHulls();
    DecompResult* result = new DecompResult();
    result->hulls.resize(hullCount);

    for (uint32_t i = 0; i < hullCount; i++)
    {
        VHACD::IVHACD::ConvexHull hull;
        vhacd->GetConvexHull(i, hull);

        ConvexHullData& hd = result->hulls[i];
        hd.vertexCount = (int)hull.m_points.size();
        hd.indexCount  = (int)(hull.m_triangles.size() * 3);

        // Copy vertices: VHACD::Vertex has double mX,mY,mZ → convert to float
        hd.vertices.resize(hd.vertexCount * 3);
        for (int v = 0; v < hd.vertexCount; v++)
        {
            hd.vertices[v * 3 + 0] = (float)hull.m_points[v].mX;
            hd.vertices[v * 3 + 1] = (float)hull.m_points[v].mY;
            hd.vertices[v * 3 + 2] = (float)hull.m_points[v].mZ;
        }

        // Copy triangle indices
        hd.indices.resize(hd.indexCount);
        for (size_t t = 0; t < hull.m_triangles.size(); t++)
        {
            hd.indices[t * 3 + 0] = (int)hull.m_triangles[t].mI0;
            hd.indices[t * 3 + 1] = (int)hull.m_triangles[t].mI1;
            hd.indices[t * 3 + 2] = (int)hull.m_triangles[t].mI2;
        }
    }

    vhacd->Release();
    return result;
}

EXPORT int ConvexDecomp_GetHullCount(void* ctx)
{
    if (!ctx) return 0;
    return (int)static_cast<DecompResult*>(ctx)->hulls.size();
}

EXPORT int ConvexDecomp_GetHullVertexCount(void* ctx, int hullIndex)
{
    if (!ctx) return 0;
    DecompResult* r = static_cast<DecompResult*>(ctx);
    if (hullIndex < 0 || hullIndex >= (int)r->hulls.size()) return 0;
    return r->hulls[hullIndex].vertexCount;
}

EXPORT int ConvexDecomp_GetHullIndexCount(void* ctx, int hullIndex)
{
    if (!ctx) return 0;
    DecompResult* r = static_cast<DecompResult*>(ctx);
    if (hullIndex < 0 || hullIndex >= (int)r->hulls.size()) return 0;
    return r->hulls[hullIndex].indexCount;
}

EXPORT int ConvexDecomp_GetHullVertices(void* ctx, int hullIndex, float* outVertices, int maxFloats)
{
    if (!ctx || !outVertices) return 0;
    DecompResult* r = static_cast<DecompResult*>(ctx);
    if (hullIndex < 0 || hullIndex >= (int)r->hulls.size()) return 0;

    const ConvexHullData& hd = r->hulls[hullIndex];
    int count = hd.vertexCount * 3;
    if (count > maxFloats) count = maxFloats;
    memcpy(outVertices, hd.vertices.data(), count * sizeof(float));
    return count;
}

EXPORT int ConvexDecomp_GetHullIndices(void* ctx, int hullIndex, int* outIndices, int maxInts)
{
    if (!ctx || !outIndices) return 0;
    DecompResult* r = static_cast<DecompResult*>(ctx);
    if (hullIndex < 0 || hullIndex >= (int)r->hulls.size()) return 0;

    const ConvexHullData& hd = r->hulls[hullIndex];
    int count = hd.indexCount;
    if (count > maxInts) count = maxInts;
    memcpy(outIndices, hd.indices.data(), count * sizeof(int));
    return count;
}

EXPORT void ConvexDecomp_Destroy(void* ctx)
{
    if (ctx)
        delete static_cast<DecompResult*>(ctx);
}
