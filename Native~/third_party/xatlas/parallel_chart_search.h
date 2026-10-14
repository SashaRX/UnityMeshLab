#pragma once

// Optional oneTBB experiment. Only read-only row searches run in parallel;
// committing a chart and selecting ties keep xatlas' original traversal order.
#ifdef MESHLAB_TBB_PACKING
#include <oneapi/tbb/blocked_range.h>
#include <oneapi/tbb/parallel_for.h>
#include <oneapi/tbb/task_arena.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <climits>
#include <cstdint>
#include <vector>

namespace xatlas { namespace internal {
inline std::atomic<uint64_t> parallelPackingBatches{0};

struct ParallelChartLocation {
    bool handled = false, found = false, inside = false;
    int x = 0, y = 0, width = 0, height = 0, rotation = 0, metric = INT_MAX;
};

inline bool betterChartLocation(const ParallelChartLocation& candidate, const ParallelChartLocation& best)
{
    return candidate.found && (!best.found || candidate.metric < best.metric ||
        (candidate.metric == best.metric && std::max(candidate.x, candidate.y) < std::max(best.x, best.y)));
}

template<class CanBlit>
ParallelChartLocation parallelChartLocation(int startX, int startY, int width, int height,
    int chartWidth, int chartHeight, int step, bool rotate, uint32_t resolution, CanBlit canBlit,
    int minimumCells = 65536)
{
    ParallelChartLocation best;
    // Bound allocations and integer metrics. Unsupported windows retain the
    // original scalar traversal, including all its boundary semantics.
    if (startX < 0 || startY < 0 || width < 1 || height < 1 || width > 8192 || height > 8192 ||
        chartWidth < 1 || chartHeight < 1 || chartWidth > 8192 || chartHeight > 8192 ||
        (step != 1 && step != 4) || resolution > 8192) return best;
    if (int64_t(width / step + 1) * (height / step + 1) < minimumCells) return best;
    best.handled = true;
    for (int rotation = 0; rotation < (rotate ? 2 : 1); ++rotation) {
        const int cw = rotation ? chartHeight : chartWidth, ch = rotation ? chartWidth : chartHeight;
        const int lastY = resolution ? std::min(height + step, int(resolution) - ch) : height + step;
        const int lastX = resolution ? std::min(width + step, int(resolution) - cw) : width + step;
        if (lastY < startY || lastX < 0) continue;
        const int rowCount = (lastY - startY) / step + 1;
        std::atomic<int> firstInside{INT_MAX};
        std::atomic<int> bestMetric{best.metric};
        const auto rowSearch = [&](int rowIndex, ParallelChartLocation& row) {
            if (rowIndex > firstInside.load(std::memory_order_relaxed)) return;
            const int y = startY + rowIndex * step;
            for (int x = rowIndex == 0 ? startX : 0; x <= lastX; x += step) {
                // Abandon in-flight late rows too, not just rows that have not
                // started. Joining their redundant scans can erase the gain.
                if (rowIndex > firstInside.load(std::memory_order_relaxed)) return;
                const int extentX = std::max(width, x + cw), extentY = std::max(height, y + ch);
                const int area = extentX * extentY;
                ParallelChartLocation candidate;
                candidate.found = true;candidate.x = x;candidate.y = y;candidate.width = cw;
                candidate.height = ch;candidate.rotation = rotation;
                candidate.metric = std::max(extentX, extentY) * std::max(extentX, extentY) + area;
                // A feasible lower metric from any row safely rules out a
                // higher metric. Equal metrics still need ordered reduction.
                if (candidate.metric > bestMetric.load(std::memory_order_relaxed)) continue;
                if (!betterChartLocation(candidate, row) || !canBlit(rotation, x, y)) continue;
                candidate.inside = area == width * height;row = candidate;
                int metric = bestMetric.load(std::memory_order_relaxed);
                while (candidate.metric < metric && !bestMetric.compare_exchange_weak(metric, candidate.metric,
                    std::memory_order_relaxed)) {}
                if (candidate.inside) {
                    int previous = firstInside.load(std::memory_order_relaxed);
                    while (rowIndex < previous && !firstInside.compare_exchange_weak(previous, rowIndex,
                        std::memory_order_relaxed)) {}
                    return;
                }
            }
        };
        // Most easy placements fit near the beginning; avoid scheduling jobs
        // for those. Later rows can only be skipped after an earlier fit exists.
        const int prefix = std::min(rowCount, 8);
        std::array<ParallelChartLocation, 8> prefixRows{};
        for (int row = 0; row < prefix; ++row) {
            rowSearch(row, prefixRows[row]);
            if (prefixRows[row].inside) {prefixRows[row].handled = true;return prefixRows[row];}
            if (betterChartLocation(prefixRows[row], best)) {best = prefixRows[row];best.handled = true;}
        }
        // Easy fits do not allocate a full row table. Deferred allocation also
        // avoids touching pages that no worker ever needs to read or write.
        std::vector<ParallelChartLocation> rows(rowCount - prefix);
        if (rowCount > prefix) {
            ++parallelPackingBatches;
            // This arena limits only our searches, not Unity/Burst or other TBB
            // clients. Do not change a process-wide global concurrency setting.
            static oneapi::tbb::task_arena arena(8);
            arena.execute([&] {
                oneapi::tbb::parallel_for(oneapi::tbb::blocked_range<int>(prefix, rowCount, 8),
                    [&](const oneapi::tbb::blocked_range<int>& range) {
                        for (int row = range.begin(); row != range.end(); ++row) rowSearch(row, rows[row - prefix]);
                    });
            });
        }
        // Preserve first-inside early return, rotation priority, and stable
        // same-metric ties. A global minimum alone would change existing UVs.
        for (const auto& row : rows) {
            if (row.inside) {auto result = row;result.handled = true;return result;}
            if (betterChartLocation(row, best)) {best = row;best.handled = true;}
        }
    }
    return best;
}
}}
#endif
