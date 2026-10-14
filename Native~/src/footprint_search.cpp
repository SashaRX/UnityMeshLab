#include "footprint_search.h"
#include <new>

#ifdef _WIN32
#define EXPORT extern "C" __declspec(dllexport)
#else
#define EXPORT extern "C" __attribute__((visibility("default")))
#endif

using OccupancySnapshot = std::shared_ptr<const meshlab::FootprintOccupancy>;
EXPORT int meshLabFootprintSearchVersion() { return 2; }

EXPORT void* meshLabFootprintSnapshotCreate(int side, const int* offsets, int offsetInts,
    const int* occupied, int occupiedInts) {
    if (side < 1 || side > 8192 || offsetInts != side + 1 ||
        !meshlab::FootprintOccupancy::valid(side, offsets, occupied, occupiedInts)) return nullptr;
    try { return new OccupancySnapshot(std::make_shared<meshlab::FootprintOccupancy>(side, offsets, occupied, occupiedInts)); }
    catch (const std::bad_alloc&) { return nullptr; }
}

EXPORT void meshLabFootprintSnapshotDestroy(void* handle) {
    delete static_cast<OccupancySnapshot*>(handle);
}

EXPORT void* meshLabFootprintSearchFromSnapshot(void* snapshot, int width, int height, const int* spans, int spanInts) {
    if (!snapshot) return nullptr;
    const auto& occupancy = *static_cast<OccupancySnapshot*>(snapshot);
    if (!meshlab::FootprintSearch::validMask(occupancy->side, width, height, spans, spanInts)) return nullptr;
    try { return new meshlab::FootprintSearch(occupancy, width, height, spans, spanInts); }
    catch (const std::bad_alloc&) { return nullptr; }
}

EXPORT void* meshLabFootprintSearchCreate(int side, int width, int height,
    const int* spans, int spanInts, const int* offsets, int offsetInts,
    const int* occupied, int occupiedInts) {
    if (side < 1 || side > 8192 || offsetInts != side + 1 || !meshlab::FootprintSearch::valid(side, width, height,
        spans, spanInts, offsets, occupied, occupiedInts)) return nullptr;
    try { return new meshlab::FootprintSearch(side, width, height, spans, spanInts, offsets, occupied, occupiedInts); }
    catch (const std::bad_alloc&) { return nullptr; }
}

EXPORT int meshLabFootprintSearchAdvance(void* handle, int* x, int* y, int best, int64_t* work, int chunk) {
    if (!handle || !x || !y || !work) return -2;
    return static_cast<meshlab::FootprintSearch*>(handle)->advance(*x, *y, best, *work, chunk);
}

EXPORT void meshLabFootprintSearchDestroy(void* handle) {
    delete static_cast<meshlab::FootprintSearch*>(handle);
}
