#include "../third_party/xatlas/parallel_chart_search.h"
#include <cstdlib>
#include <iostream>
#include <random>

using xatlas::internal::ParallelChartLocation;

// Original brute-force placement order and tie/early-return rules. Do not use
// the optimized reducer here: this reference pins the semantic difference
// between first-inside and globally closest placements.
template<class CanBlit>
static ParallelChartLocation reference(int sx, int sy, int w, int h, int cw, int ch,
    int step, bool rotate, uint32_t resolution, CanBlit canBlit)
{
    ParallelChartLocation best;best.handled = true;
    for (int r = 0; r < (rotate ? 2 : 1); ++r) {
        const int rw = r ? ch : cw, rh = r ? cw : ch;
        for (int y = sy; y <= h + step; y += step) {
            if (resolution && y > int(resolution) - rh) break;
            for (int x = y == sy ? sx : 0; x <= w + step; x += step) {
                if (resolution && x > int(resolution) - rw) break;
                const int ex = std::max(w, x + rw), ey = std::max(h, y + rh);
                const int area = ex * ey, extent = std::max(ex, ey), metric = extent * extent + area;
                if (metric > best.metric || (metric == best.metric &&
                    std::max(x, y) >= std::max(best.x, best.y))) continue;
                if (!canBlit(r, x, y)) continue;
                best.found = true;best.x = x;best.y = y;best.width = rw;best.height = rh;
                best.rotation = r;best.metric = metric;best.inside = area == w * h;
                if (best.inside) return best;
            }
        }
    }
    return best;
}

template<class CanBlit>
static void verify(int sx, int sy, int w, int h, int cw, int ch, int step, bool rotate,
    uint32_t resolution, CanBlit canBlit)
{
    const auto expected = reference(sx, sy, w, h, cw, ch, step, rotate, resolution, canBlit);
    const auto actual = xatlas::internal::parallelChartLocation(sx, sy, w, h, cw, ch, step,
        rotate, resolution, canBlit, 0);
    if (!actual.handled || expected.found != actual.found || (expected.found &&
        (expected.x != actual.x || expected.y != actual.y || expected.width != actual.width ||
        expected.height != actual.height || expected.rotation != actual.rotation ||
        expected.metric != actual.metric || expected.inside != actual.inside))) {
        std::cerr << "parallel placement differs: window " << w << 'x' << h << " chart " << cw << 'x' << ch
            << " start " << sx << ',' << sy << " expected " << expected.x << ',' << expected.y << ',' << expected.rotation
            << " actual " << actual.x << ',' << actual.y << ',' << actual.rotation << '\n';
        std::exit(1);
    }
}

int main()
{
    std::mt19937 random(0x6bc214);
    for (int iteration = 0; iteration < 600; ++iteration) {
        const int w = 128 + random() % 385, h = 64 + random() % 193;
        const int cw = 1 + random() % (w + 20), ch = 1 + random() % (h + 20);
        const int sx = random() % (w + 2), sy = random() % (h / 2);
        const int step = iteration & 1 ? 4 : 1;
        const uint32_t resolution = iteration % 3 ? 0 : 128 + random() % 512;
        const uint32_t seed = random();
        const auto free = [=](int r, int x, int y) {
            if (iteration % 4 == 0 && y < sy + 32 * step) return false;
            if (iteration % 7 == 0 && r == 0) return false;
            if (iteration % 11 == 0) return false;
            uint32_t bits = uint32_t(x) * 747796405u + uint32_t(y) * 2891336453u + seed + uint32_t(r) * 277803737u;
            bits ^= bits >> 16;
            return bits % (1 + seed % 503) == 0;
        };
        verify(sx, sy, w, h, cw, ch, step, iteration & 2, resolution, free);
    }
    // First fit inside the existing canvas wins even if later fits are closer.
    verify(200, 0, 512, 256, 16, 16, 1, true, 512, [](int, int, int) {return true;});
    // Force the parallel region, first row and rotation ties, and canvas growth.
    for (int step : {1, 4}) {
        verify(0, 0, 512, 256, 32, 16, step, true, 512,
            [](int, int x, int y) {return y == 80 && (x == 100 || x == 104);});
        verify(0, 0, 256, 128, 257, 129, step, true, 0,
            [](int, int x, int y) {return x % 31 == 0 && y >= 64;});
        verify(0, 0, 256, 128, 40, 30, step, true, 512,
            [](int r, int x, int y) {return r == 1 && x == 32 && y == 80;});
    }
    if (xatlas::internal::parallelPackingBatches.load() == 0) {
        std::cerr << "no parallel packing batch was executed\n";
        return 1;
    }
    const auto scalar = xatlas::internal::parallelChartLocation(0, 0, 16, 16, 4, 4, 1, true, 16,
        [](int, int, int) {return true;});
    if (scalar.handled) return 1;
    std::cout << "607 scalar/oneTBB searches match, " << xatlas::internal::parallelPackingBatches.load()
        << " parallel batches; first-inside and tie order unchanged\n";
}
