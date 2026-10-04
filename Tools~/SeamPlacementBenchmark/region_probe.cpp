// Standalone research executable. Does not build or copy a Unity plugin.
// Inputs: exact capture, face-region uint32 stream (or '-'), output capture,
// maxCost, maxIterations, roundnessWeight. Optional region labels are inferred
// from geometry; no artist UVs/normals are passed to xatlas.
#include "xatlas.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

template<class T> void read(std::ifstream& f, std::vector<T>& v, size_t n) {
    v.resize(n);
    if (!f.read(reinterpret_cast<char*>(v.data()), n * sizeof(T)))
        throw std::runtime_error("Truncated input");
}
template<class T> void write(std::ofstream& f, const std::vector<T>& v) {
    f.write(reinterpret_cast<const char*>(v.data()), v.size() * sizeof(T));
}

int main(int argc, char** argv) try {
    if (argc != 7) throw std::runtime_error("capture regions output cost iterations roundness");
    std::ifstream input(argv[1], std::ios::binary);
    std::vector<int32_t> h, charts;
    std::vector<float> p, uv;
    std::vector<uint32_t> indices, regions;
    read(input, h, 3);
    if (h[0] <= 0 || h[0] > 5000000 || h[1] <= 0 || h[1] > 15000000 || h[1] % 3)
        throw std::runtime_error("Invalid capture dimensions");
    read(input, p, size_t(h[0]) * 3);
    read(input, uv, size_t(h[0]) * 2);
    read(input, indices, h[1]);
    read(input, charts, h[0]);
    if (input.peek() != EOF) throw std::runtime_error("Trailing capture bytes");
    for (uint32_t i : indices) if (i >= uint32_t(h[0])) throw std::runtime_error("Invalid vertex index");
    for (float value : p) if (!std::isfinite(value)) throw std::runtime_error("Invalid position");
    for (float value : uv) if (!std::isfinite(value)) throw std::runtime_error("Invalid UV");
    if (std::string(argv[2]) != "-") {
        std::ifstream labels(argv[2], std::ios::binary);
        read(labels, regions, indices.size() / 3);
        if (labels.peek() != EOF) throw std::runtime_error("Invalid label count");
    }
    auto normalized = p;
    float lo[3] = {p[0], p[1], p[2]}, hi[3] = {p[0], p[1], p[2]};
    for (size_t i = 0; i < p.size(); ++i) {
        lo[i % 3] = std::min(lo[i % 3], p[i]);
        hi[i % 3] = std::max(hi[i % 3], p[i]);
    }
    float extent = std::max({hi[0]-lo[0], hi[1]-lo[1], hi[2]-lo[2]});
    if (!(extent > 0) || !std::isfinite(extent)) throw std::runtime_error("Invalid extent");
    for (size_t i = 0; i < p.size(); ++i) normalized[i] = (p[i] - (lo[i % 3] + hi[i % 3]) * 0.5f) / extent;
    std::unique_ptr<xatlas::Atlas, decltype(&xatlas::Destroy)> atlas(xatlas::Create(), xatlas::Destroy);
    if (!atlas) throw std::runtime_error("Atlas allocation failed");
    const bool packOnly = std::string(argv[4]) == "pack";
    xatlas::MeshDecl mesh;
    mesh.vertexPositionData = normalized.data(); mesh.vertexPositionStride = 12;
    mesh.vertexCount = h[0]; mesh.indexData = indices.data(); mesh.indexCount = h[1];
    mesh.indexFormat = xatlas::IndexFormat::UInt32;
    mesh.faceMaterialData = regions.empty() ? nullptr : regions.data();
    xatlas::UvMeshDecl uvMesh;
    uvMesh.vertexUvData = uv.data(); uvMesh.vertexStride = 8;
    uvMesh.vertexCount = h[0]; uvMesh.indexData = indices.data(); uvMesh.indexCount = h[1];
    uvMesh.indexFormat = xatlas::IndexFormat::UInt32;
    uvMesh.faceMaterialData = regions.empty() ? nullptr : regions.data();
    const auto added = packOnly ? xatlas::AddUvMesh(atlas.get(), uvMesh) : xatlas::AddMesh(atlas.get(), mesh);
    if (added != xatlas::AddMeshError::Success)
        throw std::runtime_error("xatlas rejected mesh");
    xatlas::ChartOptions chart;
    chart.maxCost = packOnly ? 2 : std::stof(argv[4]); chart.maxIterations = std::stoul(argv[5]);
    chart.roundnessWeight = std::stof(argv[6]);
    if (!(chart.maxCost > 0) || !std::isfinite(chart.maxCost) || chart.maxIterations < 1 || chart.maxIterations > 16 ||
        chart.roundnessWeight < 0 || !std::isfinite(chart.roundnessWeight))
        throw std::runtime_error("Invalid chart settings");
    xatlas::PackOptions pack;
    pack.resolution = 512; pack.padding = 3;
    xatlas::Generate(atlas.get(), chart, pack);
    if (atlas->meshCount != 1 || atlas->atlasCount != 1 || !atlas->width || !atlas->height)
        throw std::runtime_error("Expected single valid atlas");
    const auto& result = atlas->meshes[0];
    if (result.indexCount != indices.size()) throw std::runtime_error("Face count changed");
    p.resize(result.vertexCount * 3); uv.resize(result.vertexCount * 2); charts.resize(result.vertexCount);
    // Retain the exact input positions instead of round-tripping normalization.
    std::ifstream original(argv[1], std::ios::binary);
    original.seekg(12);
    std::vector<float> source;
    read(original, source, size_t(h[0]) * 3);
    for (uint32_t i = 0; i < result.vertexCount; ++i) {
        const auto& v = result.vertexArray[i];
        if (v.atlasIndex != 0 || v.chartIndex < 0 || v.xref >= uint32_t(h[0]))
            throw std::runtime_error("Invalid output mapping");
        for (int k = 0; k < 3; ++k) p[i * 3 + k] = source[v.xref * 3 + k];
        uv[i * 2] = v.uv[0] / atlas->width; uv[i * 2 + 1] = v.uv[1] / atlas->height;
        charts[i] = v.chartIndex;
    }
    indices.assign(result.indexArray, result.indexArray + result.indexCount);
    h = {int32_t(result.vertexCount), int32_t(result.indexCount), int32_t(result.chartCount)};
    std::ofstream output(argv[3], std::ios::binary);
    write(output, h); write(output, p); write(output, uv); write(output, indices); write(output, charts);
    if (!output) throw std::runtime_error("Output write failed");
    std::cout << result.chartCount << " charts\n";
    return 0;
} catch (const std::exception& e) {
    std::cerr << e.what() << '\n';
    return 1;
}
