#include "../src/footprint_search.h"
#include <iostream>
#include <random>
#include <cstdlib>

#ifdef MESHLAB_FOOTPRINT_STATIC_TEST
#define API extern "C"
#elif defined(_WIN32)
#define API extern "C" __declspec(dllimport)
#else
#define API extern "C"
#endif
API int meshLabFootprintSearchVersion();
API void* meshLabFootprintSearchCreate(int, int, int, const int*, int, const int*, int, const int*, int);
API int meshLabFootprintSearchAdvance(void*, int*, int*, int, int64_t*, int);
API void meshLabFootprintSearchDestroy(void*);
API void* meshLabFootprintSnapshotCreate(int,const int*,int,const int*,int);
API void meshLabFootprintSnapshotDestroy(void*);
API void* meshLabFootprintSearchFromSnapshot(void*,int,int,const int*,int);

static void require(bool pass, const char* reason) {
    if (!pass) { std::cerr << reason << '\n'; std::exit(1); }
}

static int reference(int side, int width, int height, const std::vector<int>& spans,
    const std::vector<int>& offsets, const std::vector<int>& occupied,
    int& x, int& y, int best, int64_t& work) {
    while (y + height <= side && y + height < best) {
        while (x + width <= side) {
            int next = 0;
            for (size_t s = 0; s < spans.size(); s += 3) {
                if (++work > 128000000) return -1;
                int row = y + spans[s];
                for (int i = offsets[row]; i < offsets[row + 1]; i += 2) {
                    if (occupied[i + 1] <= x + spans[s + 1]) continue;
                    if (occupied[i] < x + spans[s + 2])
                        next = std::max(x + 1, occupied[i + 1] - spans[s + 1]);
                    break;
                }
                if (next) break;
            }
            if (!next) {
                if (std::max(x + width, y + height) < best) return 1;
                break;
            }
            x = next;
        }
        ++y; x = 0;
    }
    return 0;
}

int main() {
    require(meshLabFootprintSearchVersion() == 2, "ABI version");
    std::mt19937 random(0x6bc2); int dummy = 0; int64_t candidates = 0;
    for (int trial = 0; trial < 6000; ++trial) {
        int side = 4 + int(random() % 125), width = 1 + int(random() % side), height = 1 + int(random() % side);
        std::vector<int> offsets(side + 1), occupied, spans;
        for (int row = 0; row < side; ++row) {
            offsets[row] = int(occupied.size());
            for (int x = int(random() % 3); x < side;) {
                int end = std::min(side, x + 1 + int(random() % 6));
                if (trial % 11) { occupied.push_back(x); occupied.push_back(end); }
                x = end + 1 + int(random() % 12);
            }
        }
        offsets[side] = int(occupied.size());
        for (int row = 0; row < height; ++row)
            for (int x = int(random() % 3); x < width;) {
                int end = std::min(width, x + 1 + int(random() % 5));
                if (trial % 19) { spans.push_back(row); spans.push_back(x); spans.push_back(end); }
                x = end + 1 + int(random() % 4);
            }
        void* handle = meshLabFootprintSearchCreate(side, width, height, spans.empty() ? &dummy : spans.data(),
            int(spans.size()), offsets.data(), int(offsets.size()), occupied.empty() ? &dummy : occupied.data(), int(occupied.size()));
        require(handle != nullptr, "valid snapshot refused");
        void* snapshot = meshLabFootprintSnapshotCreate(side, offsets.data(), int(offsets.size()),
            occupied.empty() ? &dummy : occupied.data(), int(occupied.size()));
        require(snapshot != nullptr, "shared snapshot refused");
        void* shared = meshLabFootprintSearchFromSnapshot(snapshot, width, height,
            spans.empty() ? &dummy : spans.data(), int(spans.size()));
        require(shared != nullptr, "shared mask refused");
        // Searches retain immutable rows even after their owner is released.
        meshLabFootprintSnapshotDestroy(snapshot);
        int x = 0, y = 0, expectedX = 0, expectedY = 0, best = INT32_MAX;
        int sharedX = 0, sharedY = 0;
        int64_t work = trial % 3 ? 0 : 127999990 + trial % 20, expectedWork = work, sharedWork = work;
        for (int step = 0; step < side * side + 1; ++step) {
            int expected = reference(side, width, height, spans, offsets, occupied, expectedX, expectedY, best, expectedWork);
            int status;
            do { status = meshLabFootprintSearchAdvance(handle, &x, &y, best, &work, 1 + trial % 100); } while (status == 2);
            require(status == expected && x == expectedX && y == expectedY && work == expectedWork, "candidate/work trace changed");
            int sharedStatus;
            do { sharedStatus = meshLabFootprintSearchAdvance(shared, &sharedX, &sharedY, best, &sharedWork, 1 + trial % 113); } while (sharedStatus == 2);
            require(sharedStatus == expected && sharedX == expectedX && sharedY == expectedY && sharedWork == expectedWork,
                "shared occupancy candidate/work trace changed");
            if (status != 1) break;
            ++candidates;
            if (random() % 5 == 0) { best = std::max(x + width, y + height); ++y; x = 0; }
            else ++x;
            expectedX = x; expectedY = y;
            sharedX = x; sharedY = y;
        }
        meshLabFootprintSearchDestroy(handle);
        meshLabFootprintSearchDestroy(shared);
    }
    int offsets[] = {0, 2}, occupied[] = {0, 1}, span[] = {0, 0, 1};
    require(!meshLabFootprintSearchCreate(8193, 1, 1, span, 3, offsets, 2, occupied, 2), "oversized canvas accepted");
    require(!meshLabFootprintSearchCreate(1, 1, 1, span, 3, offsets, 1, occupied, 2), "short row offsets accepted");
    span[0] = -1;
    require(!meshLabFootprintSearchCreate(1, 1, 1, span, 3, offsets, 2, occupied, 2), "negative row accepted");
    span[0] = 0; offsets[1] = 3;
    require(!meshLabFootprintSearchCreate(1, 1, 1, span, 3, offsets, 2, occupied, 2), "invalid interval length accepted");
    int x = 0, y = 0; int64_t work = 0;
    require(meshLabFootprintSearchAdvance(nullptr, &x, &y, 2, &work, 1) == -2, "null handle accepted");
    meshLabFootprintSearchDestroy(nullptr);
    std::cout << "6000 native ABI differential searches; " << candidates << " exact candidates; invalid buffers refused\n";
}
