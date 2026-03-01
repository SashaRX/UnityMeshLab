// xatlas-unity-bridge.cpp
// Native bridge: wraps xatlas for Unity Editor tool.
// Compile: cl /O2 /LD /EHsc xatlas-unity-bridge.cpp xatlas.cpp /Fe:xatlas-unity.dll
// Place DLL in Assets/Plugins/x86_64/

#include "xatlas.h"
#include "meshoptimizer.h"
#include <cstring>
#include <cstdint>
#include <vector>

#ifdef _WIN32
#define EXPORT extern "C" __declspec(dllexport)
#else
#define EXPORT extern "C" __attribute__((visibility("default")))
#endif

static xatlas::Atlas* s_atlas = nullptr;

// ── Lifecycle ──

EXPORT void xatlasCreate()
{
    if (s_atlas)
        xatlas::Destroy(s_atlas);
    s_atlas = xatlas::Create();
}

EXPORT void xatlasDestroy()
{
    if (s_atlas) {
        xatlas::Destroy(s_atlas);
        s_atlas = nullptr;
    }
}

// ── Input ──

EXPORT int xatlasAddUvMesh(
    const float*    uvData,
    uint32_t        vertexCount,
    const uint32_t* indexData,
    uint32_t        indexCount,
    const uint32_t* faceMaterialData,
    uint32_t        faceCount)
{
    if (!s_atlas) return -1;

    xatlas::UvMeshDecl decl;
    memset(&decl, 0, sizeof(decl));
    decl.vertexUvData    = uvData;
    decl.vertexCount     = vertexCount;
    decl.vertexStride    = sizeof(float) * 2;
    decl.indexData       = indexData;
    decl.indexCount       = indexCount;
    decl.indexFormat      = xatlas::IndexFormat::UInt32;
    decl.faceMaterialData = faceMaterialData;

    xatlas::AddMeshError err = xatlas::AddUvMesh(s_atlas, decl);
    return (int)err;
}

// ── Processing ──

EXPORT void xatlasComputeCharts()
{
    if (!s_atlas) return;
    xatlas::ChartOptions opts;
    xatlas::ComputeCharts(s_atlas, opts);
}

EXPORT void xatlasPackCharts(
    int      maxChartSize,
    uint32_t padding,
    float    texelsPerUnit,
    uint32_t resolution,
    int      bilinear,
    int      blockAlign,
    int      bruteForce)
{
    if (!s_atlas) return;

    xatlas::PackOptions opts;
    opts.maxChartSize  = maxChartSize;
    opts.padding       = padding;
    opts.texelsPerUnit = texelsPerUnit;
    opts.resolution    = resolution;
    opts.bilinear      = (bilinear  != 0);
    opts.blockAlign    = (blockAlign != 0);
    opts.bruteForce    = (bruteForce != 0);

    xatlas::PackCharts(s_atlas, opts);
}

// ── Queries ──

EXPORT int      xatlasGetMeshCount()   { return s_atlas ? (int)s_atlas->meshCount : 0; }
EXPORT uint32_t xatlasGetAtlasWidth()  { return s_atlas ? s_atlas->width  : 0; }
EXPORT uint32_t xatlasGetAtlasHeight() { return s_atlas ? s_atlas->height : 0; }
EXPORT uint32_t xatlasGetChartCount()  { return s_atlas ? s_atlas->chartCount : 0; }

// ── Raw output data — C# handles all mapping ──

EXPORT int xatlasGetOutputVertexCount(int meshIndex)
{
    if (!s_atlas || meshIndex < 0 || meshIndex >= (int)s_atlas->meshCount)
        return 0;
    return (int)s_atlas->meshes[meshIndex].vertexCount;
}

EXPORT int xatlasGetOutputIndexCount(int meshIndex)
{
    if (!s_atlas || meshIndex < 0 || meshIndex >= (int)s_atlas->meshCount)
        return 0;
    return (int)s_atlas->meshes[meshIndex].indexCount;
}

// Per-output-vertex: xref (original vertex index), packed UV [0..1], chartIndex
EXPORT int xatlasGetOutputVertexData(
    int       meshIndex,
    uint32_t* outXref,
    float*    outUV,
    uint32_t* outChartIndex,
    int       maxVerts)
{
    if (!s_atlas || meshIndex < 0 || meshIndex >= (int)s_atlas->meshCount)
        return 0;

    const xatlas::Mesh& mesh = s_atlas->meshes[meshIndex];
    int count = (int)mesh.vertexCount;
    if (count > maxVerts) count = maxVerts;

    float invW = s_atlas->width  > 0 ? 1.0f / (float)s_atlas->width  : 1.0f;
    float invH = s_atlas->height > 0 ? 1.0f / (float)s_atlas->height : 1.0f;

    for (int i = 0; i < count; i++) {
        outXref[i]        = mesh.vertexArray[i].xref;
        outUV[i * 2 + 0]  = mesh.vertexArray[i].uv[0] * invW;
        outUV[i * 2 + 1]  = mesh.vertexArray[i].uv[1] * invH;
        outChartIndex[i]   = mesh.vertexArray[i].chartIndex;
    }
    return count;
}

// Output index buffer (triangles referencing output vertices)
EXPORT int xatlasGetOutputIndices(
    int       meshIndex,
    uint32_t* outIndices,
    int       maxIndices)
{
    if (!s_atlas || meshIndex < 0 || meshIndex >= (int)s_atlas->meshCount)
        return 0;

    const xatlas::Mesh& mesh = s_atlas->meshes[meshIndex];
    int count = (int)mesh.indexCount;
    if (count > maxIndices) count = maxIndices;

    memcpy(outIndices, mesh.indexArray, count * sizeof(uint32_t));
    return count;
}

// ══════════════════════════════════════════════════════════════════
// meshoptimizer — full optimization pipeline (dedup + cache + overdraw + fetch)
//
// vertexData:          interleaved vertex buffer, position MUST be first 12 bytes (float3)
// vertexCount:         number of vertices in the input
// vertexStride:        bytes per vertex (must be >= 12)
// indices:             input index buffer (triangles)
// indexCount:          number of indices (must be multiple of 3)
// overdrawThreshold:   meshopt_optimizeOverdraw threshold (typically 1.05)
// outVertexData:       caller-allocated buffer, vertexCount * vertexStride bytes
// outIndices:          caller-allocated buffer, indexCount uint32s
// outVertexCount:      receives actual vertex count after deduplication
//
// Returns: 0 = OK, 1 = null pointer, 2 = stride < 12, 3 = indexCount not multiple of 3
// ══════════════════════════════════════════════════════════════════

EXPORT int meshoptOptimize(
    const unsigned char* vertexData,
    uint32_t             vertexCount,
    uint32_t             vertexStride,
    const uint32_t*      indices,
    uint32_t             indexCount,
    float                overdrawThreshold,
    unsigned char*       outVertexData,
    uint32_t*            outIndices,
    uint32_t*            outVertexCount)
{
    // ── Validation ──
    if (!vertexData || !indices || !outVertexData || !outIndices || !outVertexCount)
        return 1;
    if (vertexStride < 12)
        return 2;
    if (indexCount % 3 != 0)
        return 3;
    if (vertexCount == 0 || indexCount == 0) {
        *outVertexCount = 0;
        return 0;
    }

    // ── 1. Generate vertex remap (deduplication) ──
    std::vector<unsigned int> remap(vertexCount);
    size_t newVertexCount = meshopt_generateVertexRemap(
        remap.data(), indices, indexCount,
        vertexData, vertexCount, vertexStride);

    // ── 2. Apply remap to index and vertex buffers ──
    meshopt_remapIndexBuffer(outIndices, indices, indexCount, remap.data());
    meshopt_remapVertexBuffer(outVertexData, vertexData, vertexCount, vertexStride, remap.data());

    // ── 3. Optimize vertex cache (triangle reorder for GPU post-transform cache) ──
    meshopt_optimizeVertexCache(outIndices, outIndices, indexCount, newVertexCount);

    // ── 4. Optimize overdraw (needs position as first 12 bytes in stride) ──
    meshopt_optimizeOverdraw(outIndices, outIndices, indexCount,
        reinterpret_cast<const float*>(outVertexData),
        newVertexCount, vertexStride, overdrawThreshold);

    // ── 5. Optimize vertex fetch (reorder vertices for sequential access) ──
    meshopt_optimizeVertexFetch(outVertexData, outIndices, indexCount,
        outVertexData, newVertexCount, vertexStride);

    *outVertexCount = (uint32_t)newVertexCount;
    return 0;
}
