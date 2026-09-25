// Isolated, owned remesh job. Does not touch the legacy bridge's global xatlas.
#include "meshoptimizer.h"
#include "xatlas.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <memory>
#include <vector>

#ifdef _WIN32
#define EXPORT extern "C" __declspec(dllexport)
#else
#define EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {
struct Vertex { float p[3], n[3], uv[2]; };
struct Result { std::vector<Vertex> vertices; std::vector<unsigned int> indices; };
struct AtlasDelete { void operator()(xatlas::Atlas* a) const { xatlas::Destroy(a); } };
constexpr size_t MaxTriangles = 5000000;
}

EXPORT int meshLabRemeshVersion() { return 1; }
EXPORT void meshLabRemeshDestroy(void* handle) { delete static_cast<Result*>(handle); }

// Returns 0 on success; 1 invalid input, 2 output budget, 3 empty result,
// 4 unwrap failed, 5 multiple atlases, 6 allocation/internal failure.
// Output vertex layout is eight float32 values: position, normal, UV0.
// flags: 1 = meshopt_RemeshSolve, 2 = meshopt_RemeshShell. These bits are this
// bridge's ABI and are mapped by name; meshoptimizer renumbered its enum in v1.3.
EXPORT int meshLabRemeshBuild(const float* positions, uint32_t vertexCount,
    const uint32_t* indices, uint32_t indexCount, int resolution,
    uint32_t targetTriangles, float error, float crease, float smoothing,
    uint32_t textureSize, uint32_t padding, uint32_t flags,
    void** handle, uint32_t* outVertices, uint32_t* outIndices)
{
    if (!handle || !outVertices || !outIndices) return 1;
    *handle = nullptr; *outVertices = 0; *outIndices = 0;
    if (!positions || !indices || vertexCount < 3 || indexCount < 3 || indexCount % 3 ||
        resolution < 4 || resolution > 256 || targetTriangles < 1 || targetTriangles > MaxTriangles ||
        !std::isfinite(error) || error < 0 || error > 1 ||
        !std::isfinite(crease) || crease < 0 || crease > 3.141593f ||
        !std::isfinite(smoothing) || smoothing < 0 || smoothing > 10 ||
        textureSize < 64 || textureSize > 8192 || padding > 32 || (flags & ~3u)) return 1;
    for (size_t i = 0; i < size_t(vertexCount) * 3; ++i)
        if (!std::isfinite(positions[i])) return 1;
    for (size_t i = 0; i < indexCount; ++i)
        if (indices[i] >= vertexCount) return 1;
    float lo[3] = {positions[0], positions[1], positions[2]};
    float hi[3] = {positions[0], positions[1], positions[2]};
    for (size_t i = 0; i < vertexCount; ++i)
        for (int k = 0; k < 3; ++k) {
            lo[k] = std::min(lo[k], positions[i * 3 + k]);
            hi[k] = std::max(hi[k], positions[i * 3 + k]);
        }
    float extent = std::max(hi[0]-lo[0], std::max(hi[1]-lo[1], hi[2]-lo[2]));
    if (!std::isfinite(extent) || extent <= 0) return 1;
    try {
        unsigned int opts = (flags & 1 ? meshopt_RemeshSolve : 0) | (flags & 2 ? meshopt_RemeshShell : 0);
        size_t count = meshopt_remesh(nullptr, 0, indices, indexCount, positions, vertexCount, 12, resolution, opts);
        if (!count) return 3;
        if (count > MaxTriangles) return 2;
        std::vector<float> corners(count * 9);
        count = meshopt_remesh(corners.data(), count, indices, indexCount, positions, vertexCount, 12, resolution, opts);
        if (!count) return 3;
        corners.resize(count * 9);
        std::vector<unsigned int> remap(count * 3), idx(count * 3);
        size_t unique = meshopt_generateVertexRemap(remap.data(), nullptr, count * 3, corners.data(), count * 3, 12);
        std::vector<float> pos(unique * 3);
        meshopt_remapVertexBuffer(pos.data(), corners.data(), count * 3, 12, remap.data());
        meshopt_remapIndexBuffer(idx.data(), nullptr, count * 3, remap.data());
        size_t simplified = meshopt_simplifyWithUpdate(idx.data(), idx.size(), pos.data(), unique, 12,
            nullptr, 0, nullptr, 0, nullptr, std::min(idx.size(), size_t(targetTriangles) * 3), error,
            meshopt_SimplifyPreserveFolds | meshopt_SimplifyRegularizeLight, nullptr);
        if (!simplified) return 3;
        idx.resize(simplified);
        std::vector<float> normals(simplified * 3);
        meshopt_generateNormals(normals.data(), idx.data(), idx.size(), pos.data(), unique, 12, crease, smoothing);
        std::vector<Vertex> split(simplified);
        for (size_t i = 0; i < simplified; ++i) {
            std::memcpy(split[i].p, &pos[size_t(idx[i]) * 3], 12);
            std::memcpy(split[i].n, &normals[i * 3], 12);
            split[i].uv[0] = split[i].uv[1] = 0;
        }
        remap.resize(simplified);
        unique = meshopt_generateVertexRemap(remap.data(), nullptr, simplified, split.data(), simplified, sizeof(Vertex));
        std::vector<Vertex> verts(unique);
        meshopt_remapVertexBuffer(verts.data(), split.data(), simplified, sizeof(Vertex), remap.data());
        meshopt_remapIndexBuffer(idx.data(), nullptr, simplified, remap.data());
        std::unique_ptr<xatlas::Atlas, AtlasDelete> atlas(xatlas::Create());
        if (!atlas) return 6;
        xatlas::MeshDecl decl;
        decl.vertexPositionData = verts.data(); decl.vertexPositionStride = sizeof(Vertex);
        decl.vertexNormalData = verts[0].n; decl.vertexNormalStride = sizeof(Vertex);
        decl.vertexCount = uint32_t(unique);
        decl.indexData = idx.data(); decl.indexCount = uint32_t(idx.size());
        decl.indexFormat = xatlas::IndexFormat::UInt32;
        if (xatlas::AddMesh(atlas.get(), decl) != xatlas::AddMeshError::Success) return 4;
        xatlas::ChartOptions charts;
        xatlas::PackOptions pack;
        pack.resolution = textureSize; pack.padding = padding; pack.bilinear = true;
        xatlas::Generate(atlas.get(), charts, pack);
        if (!atlas->meshCount || !atlas->width || !atlas->height) return 4;
        if (atlas->atlasCount != 1) return 5;
        const xatlas::Mesh& mesh = atlas->meshes[0];
        auto result = std::make_unique<Result>();
        result->vertices.resize(mesh.vertexCount);
        for (uint32_t i = 0; i < mesh.vertexCount; ++i) {
            const auto& v = mesh.vertexArray[i];
            if (v.atlasIndex != 0 || v.xref >= verts.size()) return 4;
            result->vertices[i] = verts[v.xref];
            result->vertices[i].uv[0] = v.uv[0] / atlas->width;
            result->vertices[i].uv[1] = v.uv[1] / atlas->height;
        }
        result->indices.assign(mesh.indexArray, mesh.indexArray + mesh.indexCount);
        *outVertices = mesh.vertexCount; *outIndices = mesh.indexCount;
        *handle = result.release();
        return 0;
    } catch (...) { return 6; }
}

EXPORT int meshLabRemeshCopy(void* handle, float* vertices, uint32_t vertexCapacity,
    uint32_t* indices, uint32_t indexCapacity)
{
    const auto* r = static_cast<const Result*>(handle);
    if (!r || !vertices || !indices || vertexCapacity < r->vertices.size() || indexCapacity < r->indices.size()) return 1;
    std::memcpy(vertices, r->vertices.data(), r->vertices.size() * sizeof(Vertex));
    std::memcpy(indices, r->indices.data(), r->indices.size() * sizeof(uint32_t));
    return 0;
}
