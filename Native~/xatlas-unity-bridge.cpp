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
// dedupStride:         bytes to compare per vertex for deduplication (0 = full vertexStride)
//                      Use this to exclude tangent/color from dedup comparison.
//                      Dedup-relevant channels (pos, normal, uvs) must be packed first in layout.
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
    uint32_t             dedupStride,
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
    // If dedupStride is set, build a key-only buffer for comparison
    uint32_t actualDedupStride = (dedupStride > 0 && dedupStride <= vertexStride) ? dedupStride : vertexStride;
    
    std::vector<unsigned int> remap(vertexCount);
    size_t newVertexCount;
    
    if (actualDedupStride < vertexStride) {
        // Build compacted key buffer (only dedup-relevant bytes per vertex)
        std::vector<unsigned char> keyBuffer(vertexCount * actualDedupStride);
        for (uint32_t i = 0; i < vertexCount; i++)
            memcpy(&keyBuffer[i * actualDedupStride], &vertexData[i * vertexStride], actualDedupStride);
        
        newVertexCount = meshopt_generateVertexRemap(
            remap.data(), indices, indexCount,
            keyBuffer.data(), vertexCount, actualDedupStride);
    } else {
        newVertexCount = meshopt_generateVertexRemap(
            remap.data(), indices, indexCount,
            vertexData, vertexCount, vertexStride);
    }

    // ── 2. Apply remap to FULL vertex buffer (all channels including tangent) ──
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

// ══════════════════════════════════════════════════════════════════
// meshoptimizer — mesh simplification with attribute preservation
//
// Uses meshopt_simplifyWithAttributes to reduce triangle count while
// preserving vertex attributes (normals, UVs) weighted by importance.
//
// vertexData:          interleaved vertex buffer, position MUST be first 12 bytes (float3)
// vertexCount:         number of vertices
// vertexStride:        bytes per vertex (must be >= 12)
// indices:             input index buffer (triangles)
// indexCount:          number of indices (must be multiple of 3)
// attributes:          float array of per-vertex attributes (e.g. normal xyz + uv2 xy)
// attributeStride:     bytes per vertex in the attribute array
// attributeWeights:    importance weight per scalar attribute
// attributeCount:      number of scalar attributes (max 16)
// targetRatio:         fraction of triangles to keep (0.0–1.0)
// targetError:         maximum allowed simplification error
// options:             meshopt simplify flags (LockBorder=1, ErrorAbsolute=2, Sparse=4, Prune=8)
// outIndices:          caller-allocated buffer, indexCount uint32s (only outIndexCount used)
// outIndexCount:       receives actual output index count
// outResultError:      receives achieved error (can be NULL)
//
// Returns: 0 = OK, 1 = null pointer, 2 = stride < 12, 3 = indexCount not multiple of 3,
//          4 = attributeCount too large
// ══════════════════════════════════════════════════════════════════

EXPORT int meshoptSimplify(
    const unsigned char* vertexData,
    uint32_t             vertexCount,
    uint32_t             vertexStride,
    const uint32_t*      indices,
    uint32_t             indexCount,
    const float*         attributes,
    uint32_t             attributeStride,
    const float*         attributeWeights,
    uint32_t             attributeCount,
    float                targetRatio,
    float                targetError,
    uint32_t             options,
    uint32_t*            outIndices,
    uint32_t*            outIndexCount,
    float*               outResultError)
{
    // ── Validation ──
    if (!vertexData || !indices || !outIndices || !outIndexCount)
        return 1;
    if (vertexStride < 12)
        return 2;
    if (indexCount % 3 != 0)
        return 3;
    if (attributeCount > 16)
        return 4;
    if (vertexCount == 0 || indexCount == 0) {
        *outIndexCount = 0;
        if (outResultError) *outResultError = 0.0f;
        return 0;
    }

    // Compute target index count from ratio
    size_t targetIndexCount = (size_t)(indexCount * targetRatio);
    // Round down to multiple of 3
    targetIndexCount = (targetIndexCount / 3) * 3;
    if (targetIndexCount < 3) targetIndexCount = 3;

    float resultError = 0.0f;

    size_t newIndexCount;
    if (attributes && attributeWeights && attributeCount > 0) {
        newIndexCount = meshopt_simplifyWithAttributes(
            outIndices,
            indices, indexCount,
            reinterpret_cast<const float*>(vertexData),
            vertexCount, vertexStride,
            attributes, attributeStride,
            attributeWeights, attributeCount,
            nullptr,  // vertex_lock — not used, border locking via options flag
            targetIndexCount, targetError,
            options,
            &resultError);
    } else {
        newIndexCount = meshopt_simplify(
            outIndices,
            indices, indexCount,
            reinterpret_cast<const float*>(vertexData),
            vertexCount, vertexStride,
            targetIndexCount, targetError,
            options,
            &resultError);
    }

    // ── Post-simplify optimization ──
    meshopt_optimizeVertexCache(outIndices, outIndices, newIndexCount, vertexCount);
    meshopt_optimizeOverdraw(outIndices, outIndices, newIndexCount,
        reinterpret_cast<const float*>(vertexData),
        vertexCount, vertexStride, 1.05f);

    *outIndexCount = (uint32_t)newIndexCount;
    if (outResultError) *outResultError = resultError;
    return 0;
}
