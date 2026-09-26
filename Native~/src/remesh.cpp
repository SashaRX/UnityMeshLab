// Isolated, owned remesh jobs. Do not touch the legacy bridge's global xatlas.
//
// Two entry styles share the helpers below:
//   * meshLabRemeshBuild: the original one-shot voxel remesh -> simplify -> unwrap.
//   * staged (ABI 2): meshLabVoxelRemesh, meshLabSimplify, meshLabUnwrap, so the
//     editor can preview and re-run each stage without repeating the earlier ones.
#include "meshoptimizer.h"
#include "xatlas.h"
#include <algorithm>
#include <cfloat>
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
struct Result { std::vector<Vertex> vertices; std::vector<unsigned int> indices; std::vector<int32_t> charts; };
struct PosMesh { std::vector<float> pos; std::vector<unsigned int> idx; };
struct AtlasDelete { void operator()(xatlas::Atlas* a) const { xatlas::Destroy(a); } };
constexpr size_t MaxTriangles = 5000000;

// Return codes shared by every entry point.
enum : int { Ok = 0, Invalid = 1, Budget = 2, Empty = 3, AtlasRejected = 4, MultipleAtlases = 5,
    Internal = 6, NoAtlas = 7, BadMapping = 8 };

int ValidateMesh(const float* positions, uint32_t vertexCount, const uint32_t* indices, uint32_t indexCount)
{
    if (!positions || !indices || vertexCount < 3 || indexCount < 3 || indexCount % 3 ||
        indexCount / 3 > MaxTriangles) return Invalid;
    for (size_t i = 0; i < size_t(vertexCount) * 3; ++i)
        if (!std::isfinite(positions[i])) return Invalid;
    for (size_t i = 0; i < indexCount; ++i)
        if (indices[i] >= vertexCount) return Invalid;
    float lo[3] = {positions[0], positions[1], positions[2]};
    float hi[3] = {positions[0], positions[1], positions[2]};
    for (size_t i = 0; i < vertexCount; ++i)
        for (int k = 0; k < 3; ++k) {
            lo[k] = std::min(lo[k], positions[i * 3 + k]);
            hi[k] = std::max(hi[k], positions[i * 3 + k]);
        }
    float extent = std::max(hi[0]-lo[0], std::max(hi[1]-lo[1], hi[2]-lo[2]));
    return std::isfinite(extent) && extent > 0 ? Ok : Invalid;
}

// Voxel remesh, then weld the per-corner output into an indexed mesh.
int Voxelize(const float* positions, uint32_t vertexCount, const uint32_t* indices, uint32_t indexCount,
    int resolution, unsigned int opts, PosMesh& out)
{
    size_t count = meshopt_remesh(nullptr, 0, indices, indexCount, positions, vertexCount, 12, resolution, opts);
    if (!count) return Empty;
    if (count > MaxTriangles) return Budget;
    std::vector<float> corners(count * 9);
    count = meshopt_remesh(corners.data(), count, indices, indexCount, positions, vertexCount, 12, resolution, opts);
    if (!count) return Empty;
    corners.resize(count * 9);
    std::vector<unsigned int> remap(count * 3);
    size_t unique = meshopt_generateVertexRemap(remap.data(), nullptr, count * 3, corners.data(), count * 3, 12);
    out.pos.resize(unique * 3);
    out.idx.resize(count * 3);
    meshopt_remapVertexBuffer(out.pos.data(), corners.data(), count * 3, 12, remap.data());
    meshopt_remapIndexBuffer(out.idx.data(), nullptr, count * 3, remap.data());
    return Ok;
}

// simplifyWithUpdate is allowed to collapse/move vertices. On real meshes this can
// leave duplicate-index, zero-edge or near-zero-area triangles. xatlas keeps such
// faces as "invalid geometry" vertices with atlasIndex == -1; treating that as a
// generic unwrap failure makes one collapsed triangle abort the whole remesh.
// Filter them using a relative area scale, then drop unreferenced vertices so every
// later xref points at geometry that can own UVs.
int Clean(PosMesh& m)
{
    const size_t unique = m.pos.size() / 3;
    float lo[3] = {0, 0, 0}, hi[3] = {0, 0, 0};
    bool haveBounds = false;
    for (unsigned int v : m.idx) {
        if (v >= unique) return Internal;
        const float* p = &m.pos[size_t(v) * 3];
        if (!std::isfinite(p[0]) || !std::isfinite(p[1]) || !std::isfinite(p[2])) return Internal;
        for (int k = 0; k < 3; ++k) {
            lo[k] = haveBounds ? std::min(lo[k], p[k]) : p[k];
            hi[k] = haveBounds ? std::max(hi[k], p[k]) : p[k];
        }
        haveBounds = true;
    }
    if (!haveBounds) return Empty;
    const double extent = std::max(double(hi[0] - lo[0]), std::max(double(hi[1] - lo[1]), double(hi[2] - lo[2])));
    if (!std::isfinite(extent) || extent <= 0.0) return Empty;
    const double minTriangleArea = extent * extent * double(FLT_EPSILON);

    std::vector<unsigned int> cleaned;
    cleaned.reserve(m.idx.size());
    for (size_t i = 0; i < m.idx.size(); i += 3) {
        const unsigned int ia = m.idx[i + 0], ib = m.idx[i + 1], ic = m.idx[i + 2];
        if (ia == ib || ib == ic || ic == ia) continue;
        const float* a = &m.pos[size_t(ia) * 3];
        const float* b = &m.pos[size_t(ib) * 3];
        const float* c = &m.pos[size_t(ic) * 3];
        const double abx = double(b[0]) - a[0], aby = double(b[1]) - a[1], abz = double(b[2]) - a[2];
        const double acx = double(c[0]) - a[0], acy = double(c[1]) - a[1], acz = double(c[2]) - a[2];
        const double cx = aby * acz - abz * acy;
        const double cy = abz * acx - abx * acz;
        const double cz = abx * acy - aby * acx;
        const double area = 0.5 * std::sqrt(cx * cx + cy * cy + cz * cz);
        if (!std::isfinite(area)) return Internal;
        if (area <= minTriangleArea) continue;
        cleaned.push_back(ia); cleaned.push_back(ib); cleaned.push_back(ic);
    }
    if (cleaned.empty()) return Empty;
    m.idx.swap(cleaned);

    std::vector<unsigned int> compactRemap(unique, ~0u);
    std::vector<float> compactPos;
    compactPos.reserve(std::min(unique, m.idx.size()) * size_t(3));
    for (unsigned int& v : m.idx) {
        unsigned int mapped = compactRemap[v];
        if (mapped == ~0u) {
            mapped = unsigned(compactPos.size() / 3);
            compactRemap[v] = mapped;
            const float* p = &m.pos[size_t(v) * 3];
            compactPos.push_back(p[0]); compactPos.push_back(p[1]); compactPos.push_back(p[2]);
        }
        v = mapped;
    }
    m.pos.swap(compactPos);
    return Ok;
}

// targetTriangles == 0 simplifies until the error limit alone stops it.
int Simplify(PosMesh& m, uint32_t targetTriangles, float error, unsigned int opts, float* resultError)
{
    const size_t target = std::min(m.idx.size(), size_t(targetTriangles) * 3);
    float achieved = 0;
    size_t simplified = meshopt_simplifyWithUpdate(m.idx.data(), m.idx.size(), m.pos.data(), m.pos.size() / 3, 12,
        nullptr, 0, nullptr, 0, nullptr, target, error, opts, &achieved);
    if (!simplified) return Empty;
    m.idx.resize(simplified);
    if (resultError) *resultError = achieved;
    return Clean(m);
}

struct UnwrapOptions {
    xatlas::ChartOptions charts;
    xatlas::PackOptions pack;
};

// options layout (missing trailing entries keep xatlas defaults):
//  0 maxChartArea  1 maxBoundaryLength  2 normalDeviationWeight  3 roundnessWeight
//  4 straightnessWeight  5 normalSeamWeight  6 textureSeamWeight  7 maxCost
//  8 maxIterations  9 resolution  10 padding  11 texelsPerUnit  12 bilinear
// 13 blockAlign  14 bruteForce  15 rotateCharts  16 rotateChartsToAxis
int ParseUnwrapOptions(const float* options, uint32_t optionCount, UnwrapOptions& o)
{
    if (optionCount > 17 || (optionCount && !options)) return Invalid;
    for (uint32_t i = 0; i < optionCount; ++i)
        if (!std::isfinite(options[i]) || options[i] < 0) return Invalid;
    auto get = [&](uint32_t i, float fallback) { return i < optionCount ? options[i] : fallback; };
    o.charts.maxChartArea = get(0, o.charts.maxChartArea);
    o.charts.maxBoundaryLength = get(1, o.charts.maxBoundaryLength);
    o.charts.normalDeviationWeight = get(2, o.charts.normalDeviationWeight);
    o.charts.roundnessWeight = get(3, o.charts.roundnessWeight);
    o.charts.straightnessWeight = get(4, o.charts.straightnessWeight);
    o.charts.normalSeamWeight = get(5, o.charts.normalSeamWeight);
    o.charts.textureSeamWeight = get(6, o.charts.textureSeamWeight);
    o.charts.maxCost = get(7, o.charts.maxCost);
    float iterations = get(8, float(o.charts.maxIterations));
    float resolution = get(9, 1024), padding = get(10, 4);
    if (iterations < 1 || iterations > 16 || resolution < 64 || resolution > 8192 || padding > 32 || o.charts.maxCost <= 0) return Invalid;
    o.charts.maxIterations = uint32_t(iterations);
    o.pack.resolution = uint32_t(resolution);
    o.pack.padding = uint32_t(padding);
    o.pack.texelsPerUnit = get(11, 0);
    o.pack.bilinear = get(12, 1) != 0;
    o.pack.blockAlign = get(13, 0) != 0;
    o.pack.bruteForce = get(14, 0) != 0;
    o.pack.rotateCharts = get(15, 1) != 0;
    o.pack.rotateChartsToAxis = get(16, 1) != 0;
    return Ok;
}

// Crease-aware normals, then an xatlas full unwrap of an indexed, cleaned mesh.
int Unwrap(const PosMesh& m, float crease, float smoothing, const UnwrapOptions& o, Result& out)
{
    const size_t corners = m.idx.size();
    const size_t positions = m.pos.size() / 3;
    std::vector<float> normals(corners * 3);
    meshopt_generateNormals(normals.data(), m.idx.data(), corners, m.pos.data(), positions, 12, crease, smoothing);
    for (float n : normals) if (!std::isfinite(n)) return Internal;
    std::vector<Vertex> split(corners);
    for (size_t i = 0; i < corners; ++i) {
        std::memcpy(split[i].p, &m.pos[size_t(m.idx[i]) * 3], 12);
        std::memcpy(split[i].n, &normals[i * 3], 12);
        split[i].uv[0] = split[i].uv[1] = 0;
    }
    std::vector<unsigned int> remap(corners), idx(corners);
    size_t unique = meshopt_generateVertexRemap(remap.data(), nullptr, corners, split.data(), corners, sizeof(Vertex));
    std::vector<Vertex> verts(unique);
    meshopt_remapVertexBuffer(verts.data(), split.data(), corners, sizeof(Vertex), remap.data());
    meshopt_remapIndexBuffer(idx.data(), nullptr, corners, remap.data());
    std::unique_ptr<xatlas::Atlas, AtlasDelete> atlas(xatlas::Create());
    if (!atlas) return Internal;

    // xatlas uses absolute float epsilons for colocal and zero-area checks. Feed it a
    // uniformly normalized copy so those tests are scale-relative; the real output
    // positions remain in verts and are restored through xref after packing.
    std::vector<Vertex> atlasVerts = verts;
    float lo[3] = {atlasVerts[0].p[0], atlasVerts[0].p[1], atlasVerts[0].p[2]};
    float hi[3] = {lo[0], lo[1], lo[2]};
    for (const Vertex& v : atlasVerts)
        for (int k = 0; k < 3; ++k) {
            lo[k] = std::min(lo[k], v.p[k]);
            hi[k] = std::max(hi[k], v.p[k]);
        }
    const float extent = std::max(hi[0] - lo[0], std::max(hi[1] - lo[1], hi[2] - lo[2]));
    if (!std::isfinite(extent) || extent <= 0) return Empty;
    const float center[3] = {(lo[0] + hi[0]) * 0.5f, (lo[1] + hi[1]) * 0.5f, (lo[2] + hi[2]) * 0.5f};
    for (Vertex& v : atlasVerts)
        for (int k = 0; k < 3; ++k)
            v.p[k] = (v.p[k] - center[k]) / extent;
    UnwrapOptions scaled = o;
    // Chart limits are given in source units; the atlas input is normalized by extent.
    scaled.charts.maxChartArea = o.charts.maxChartArea / (extent * extent);
    scaled.charts.maxBoundaryLength = o.charts.maxBoundaryLength / extent;
    scaled.pack.texelsPerUnit = o.pack.texelsPerUnit * extent;

    xatlas::MeshDecl decl;
    decl.vertexPositionData = atlasVerts.data(); decl.vertexPositionStride = sizeof(Vertex);
    decl.vertexNormalData = atlasVerts[0].n; decl.vertexNormalStride = sizeof(Vertex);
    decl.vertexCount = uint32_t(unique);
    decl.indexData = idx.data(); decl.indexCount = uint32_t(idx.size());
    decl.indexFormat = xatlas::IndexFormat::UInt32;
    if (xatlas::AddMesh(atlas.get(), decl) != xatlas::AddMeshError::Success) return AtlasRejected;
    xatlas::Generate(atlas.get(), scaled.charts, scaled.pack);
    if (!atlas->meshCount || !atlas->width || !atlas->height || !atlas->atlasCount) return NoAtlas;
    if (atlas->atlasCount != 1) return MultipleAtlases;
    const xatlas::Mesh& mesh = atlas->meshes[0];
    out.vertices.resize(mesh.vertexCount);
    out.charts.resize(mesh.vertexCount);
    for (uint32_t i = 0; i < mesh.vertexCount; ++i) {
        const auto& v = mesh.vertexArray[i];
        if (v.atlasIndex != 0 || v.xref >= verts.size()) return BadMapping;
        out.vertices[i] = verts[v.xref];
        out.vertices[i].uv[0] = v.uv[0] / atlas->width;
        out.vertices[i].uv[1] = v.uv[1] / atlas->height;
        out.charts[i] = v.chartIndex;
    }
    out.indices.assign(mesh.indexArray, mesh.indexArray + mesh.indexCount);
    return Ok;
}

unsigned int RemeshOptions(uint32_t flags)
{
    return (flags & 1 ? meshopt_RemeshSolve : 0) | (flags & 2 ? meshopt_RemeshShell : 0);
}

// Bridge simplify bits: 1 RegularizeLight, 2 Regularize, 4 PreserveFolds, 8 LockBorder, 16 Prune.
unsigned int SimplifyOptions(uint32_t flags)
{
    return (flags & 1 ? meshopt_SimplifyRegularizeLight : 0) | (flags & 2 ? meshopt_SimplifyRegularize : 0) |
        (flags & 4 ? meshopt_SimplifyPreserveFolds : 0) | (flags & 8 ? meshopt_SimplifyLockBorder : 0) |
        (flags & 16 ? meshopt_SimplifyPrune : 0);
}

template <class T> void Publish(std::unique_ptr<T>& result, void** handle) { *handle = result.release(); }
}

EXPORT int meshLabRemeshVersion() { return 2; }
EXPORT void meshLabRemeshDestroy(void* handle) { delete static_cast<Result*>(handle); }
EXPORT void meshLabMeshDestroy(void* handle) { delete static_cast<PosMesh*>(handle); }

// Returns 0 on success; 1 invalid input, 2 output budget, 3 empty result,
// 4 xatlas rejected the mesh, 5 multiple atlases, 6 allocation/internal failure,
// 7 xatlas produced no usable atlas, 8 xatlas returned an invalid vertex mapping.
// Output vertex layout is eight float32 values: position, normal, UV0.
// flags: 1 = meshopt_RemeshSolve, 2 = meshopt_RemeshShell. These bits are this
// bridge's ABI and are mapped by name; meshoptimizer renumbered its enum in v1.3.
EXPORT int meshLabRemeshBuild(const float* positions, uint32_t vertexCount,
    const uint32_t* indices, uint32_t indexCount, int resolution,
    uint32_t targetTriangles, float error, float crease, float smoothing,
    uint32_t textureSize, uint32_t padding, uint32_t flags,
    void** handle, uint32_t* outVertices, uint32_t* outIndices)
{
    if (!handle || !outVertices || !outIndices) return Invalid;
    *handle = nullptr; *outVertices = 0; *outIndices = 0;
    if (resolution < 4 || resolution > 256 || targetTriangles < 1 || targetTriangles > MaxTriangles ||
        !std::isfinite(error) || error < 0 || error > 1 ||
        !std::isfinite(crease) || crease < 0 || crease > 3.141593f ||
        !std::isfinite(smoothing) || smoothing < 0 || smoothing > 10 ||
        textureSize < 64 || textureSize > 8192 || padding > 32 || (flags & ~3u)) return Invalid;
    if (int code = ValidateMesh(positions, vertexCount, indices, indexCount)) return code;
    try {
        PosMesh mesh;
        if (int code = Voxelize(positions, vertexCount, indices, indexCount, resolution, RemeshOptions(flags), mesh)) return code;
        if (int code = Simplify(mesh, targetTriangles, error, meshopt_SimplifyPreserveFolds | meshopt_SimplifyRegularizeLight, nullptr)) return code;
        UnwrapOptions options;
        options.pack.resolution = textureSize; options.pack.padding = padding; options.pack.bilinear = true;
        auto result = std::make_unique<Result>();
        if (int code = Unwrap(mesh, crease, smoothing, options, *result)) return code;
        *outVertices = uint32_t(result->vertices.size()); *outIndices = uint32_t(result->indices.size());
        Publish(result, handle);
        return Ok;
    } catch (...) { return Internal; }
}

EXPORT int meshLabRemeshCopy(void* handle, float* vertices, uint32_t vertexCapacity,
    uint32_t* indices, uint32_t indexCapacity)
{
    const auto* r = static_cast<const Result*>(handle);
    if (!r || !vertices || !indices || vertexCapacity < r->vertices.size() || indexCapacity < r->indices.size()) return Invalid;
    std::memcpy(vertices, r->vertices.data(), r->vertices.size() * sizeof(Vertex));
    std::memcpy(indices, r->indices.data(), r->indices.size() * sizeof(uint32_t));
    return Ok;
}

// ── Staged API (ABI 2) ──

// Stage 1: voxel remesh + weld. Output: indexed positions (float3) in a mesh handle.
EXPORT int meshLabVoxelRemesh(const float* positions, uint32_t vertexCount,
    const uint32_t* indices, uint32_t indexCount, int resolution, uint32_t flags,
    void** handle, uint32_t* outVertices, uint32_t* outIndices)
{
    if (!handle || !outVertices || !outIndices) return Invalid;
    *handle = nullptr; *outVertices = 0; *outIndices = 0;
    if (resolution < 4 || resolution > 256 || (flags & ~3u)) return Invalid;
    if (int code = ValidateMesh(positions, vertexCount, indices, indexCount)) return code;
    try {
        auto mesh = std::make_unique<PosMesh>();
        if (int code = Voxelize(positions, vertexCount, indices, indexCount, resolution, RemeshOptions(flags), *mesh)) return code;
        if (int code = Clean(*mesh)) return code;
        *outVertices = uint32_t(mesh->pos.size() / 3); *outIndices = uint32_t(mesh->idx.size());
        Publish(mesh, handle);
        return Ok;
    } catch (...) { return Internal; }
}

// Stage 2: quadric simplification of an indexed mesh (moved vertex positions are kept),
// degenerate cleanup and compaction. targetTriangles == 0 means error-limited only.
// error is relative to the mesh extent, as in meshoptimizer.
EXPORT int meshLabSimplify(const float* positions, uint32_t vertexCount,
    const uint32_t* indices, uint32_t indexCount, uint32_t targetTriangles, float error, uint32_t flags,
    void** handle, uint32_t* outVertices, uint32_t* outIndices, float* outError)
{
    if (!handle || !outVertices || !outIndices) return Invalid;
    *handle = nullptr; *outVertices = 0; *outIndices = 0;
    if (outError) *outError = 0;
    if (targetTriangles > MaxTriangles || !std::isfinite(error) || error < 0 || error > 1 || (flags & ~31u)) return Invalid;
    if (int code = ValidateMesh(positions, vertexCount, indices, indexCount)) return code;
    try {
        auto mesh = std::make_unique<PosMesh>();
        mesh->pos.assign(positions, positions + size_t(vertexCount) * 3);
        mesh->idx.assign(indices, indices + indexCount);
        if (int code = Simplify(*mesh, targetTriangles, error, SimplifyOptions(flags), outError)) return code;
        *outVertices = uint32_t(mesh->pos.size() / 3); *outIndices = uint32_t(mesh->idx.size());
        Publish(mesh, handle);
        return Ok;
    } catch (...) { return Internal; }
}

EXPORT int meshLabMeshCopy(void* handle, float* positions, uint32_t vertexCapacity,
    uint32_t* indices, uint32_t indexCapacity)
{
    const auto* m = static_cast<const PosMesh*>(handle);
    if (!m || !positions || !indices || vertexCapacity < m->pos.size() / 3 || indexCapacity < m->idx.size()) return Invalid;
    std::memcpy(positions, m->pos.data(), m->pos.size() * sizeof(float));
    std::memcpy(indices, m->idx.data(), m->idx.size() * sizeof(uint32_t));
    return Ok;
}

// Stage 3: crease-aware normals + xatlas unwrap with explicit chart/pack options (see
// ParseUnwrapOptions for the layout). Output is a Result handle: copy with
// meshLabUnwrapCopy, free with meshLabRemeshDestroy.
EXPORT int meshLabUnwrap(const float* positions, uint32_t vertexCount,
    const uint32_t* indices, uint32_t indexCount, float crease, float smoothing,
    const float* options, uint32_t optionCount,
    void** handle, uint32_t* outVertices, uint32_t* outIndices, uint32_t* outCharts)
{
    if (!handle || !outVertices || !outIndices) return Invalid;
    *handle = nullptr; *outVertices = 0; *outIndices = 0;
    if (outCharts) *outCharts = 0;
    if (!std::isfinite(crease) || crease < 0 || crease > 3.141593f ||
        !std::isfinite(smoothing) || smoothing < 0 || smoothing > 10) return Invalid;
    if (int code = ValidateMesh(positions, vertexCount, indices, indexCount)) return code;
    UnwrapOptions parsed;
    if (int code = ParseUnwrapOptions(options, optionCount, parsed)) return code;
    try {
        PosMesh mesh;
        mesh.pos.assign(positions, positions + size_t(vertexCount) * 3);
        mesh.idx.assign(indices, indices + indexCount);
        if (int code = Clean(mesh)) return code;
        auto result = std::make_unique<Result>();
        if (int code = Unwrap(mesh, crease, smoothing, parsed, *result)) return code;
        int32_t charts = 0;
        for (int32_t c : result->charts) charts = std::max(charts, c + 1);
        if (outCharts) *outCharts = uint32_t(charts);
        *outVertices = uint32_t(result->vertices.size()); *outIndices = uint32_t(result->indices.size());
        Publish(result, handle);
        return Ok;
    } catch (...) { return Internal; }
}

// charts may be null; otherwise it receives the xatlas chart index of every vertex.
EXPORT int meshLabUnwrapCopy(void* handle, float* vertices, uint32_t vertexCapacity,
    uint32_t* indices, uint32_t indexCapacity, int32_t* charts)
{
    const auto* r = static_cast<const Result*>(handle);
    if (int code = meshLabRemeshCopy(handle, vertices, vertexCapacity, indices, indexCapacity)) return code;
    if (charts) std::memcpy(charts, r->charts.data(), r->charts.size() * sizeof(int32_t));
    return Ok;
}
