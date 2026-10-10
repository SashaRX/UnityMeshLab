#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <vector>

extern "C" int meshoptConstraintVersion();
extern "C" int meshoptSimplifyConstrained(const unsigned char*, uint32_t, uint32_t,
    const uint32_t*, uint32_t, const float*, uint32_t, const float*, uint32_t,
    const unsigned char*, uint32_t, float, float, uint32_t, uint32_t*, uint32_t*, float*);

static void check(bool condition, const char* message) {
    if (!condition) { std::cerr << message << '\n'; std::exit(1); }
}

int main() {
    check(meshoptConstraintVersion() == 1, "constraint ABI");
    // Two perpendicular grids with independent render wedges on their crease.
    const uint32_t n = 8, panel = (n + 1) * (n + 1), count = panel * 2;
    std::vector<float> positions;
    std::vector<uint32_t> triangles;
    std::vector<unsigned char> locks(count);
    for (uint32_t side = 0; side < 2; ++side) {
        for (uint32_t y = 0; y <= n; ++y)
            for (uint32_t x = 0; x <= n; ++x) {
                positions.insert(positions.end(), {side ? 0.f : float(x) / n, float(y) / n, side ? float(x) / n : 0.f});
                if (x == 0) locks[side * panel + y * (n + 1)] = y == 0 || y == n ? 1 : 2;
            }
        for (uint32_t y = 0; y < n; ++y)
            for (uint32_t x = 0; x < n; ++x) {
                uint32_t a = side * panel + y * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
                if (side) triangles.insert(triangles.end(), {a, c, b, b, c, d});
                else triangles.insert(triangles.end(), {a, b, c, b, d, c});
            }
    }
    std::vector<uint32_t> output(triangles.size());
    uint32_t outputCount = 0;
    float error = 0;
    auto run = [&](const std::vector<unsigned char>& flags) {
        return meshoptSimplifyConstrained(reinterpret_cast<const unsigned char*>(positions.data()), count, 12,
            triangles.data(), uint32_t(triangles.size()), nullptr, 0, nullptr, 0,
            flags.data(), uint32_t(flags.size()), .02f, 1.f, 32, output.data(), &outputCount, &error);
    };
    check(run(locks) == 0, "zero-attribute protected seam");
    check(outputCount > 0 && outputCount < triangles.size(), "protected seam reduces triangles");
    for (uint32_t side = 0; side < 2; ++side)
        for (uint32_t y : {0u, n})
            check(std::find(output.begin(), output.begin() + outputCount, side * panel + y * (n + 1)) != output.begin() + outputCount,
                "both shading-side endpoints survive zero attribute costs");
    for (auto& flag : locks) if (flag) flag = 1;
    check(run(locks) == 0, "locked crease retry");
    for (uint32_t side = 0; side < 2; ++side)
        for (uint32_t y = 0; y < n; ++y) {
            uint32_t a = side * panel + y * (n + 1), b = a + n + 1;
            if (!side) std::swap(a, b);
            bool edge = false;
            for (uint32_t t = 0; t < outputCount; t += 3)
                for (uint32_t k = 0; k < 3; ++k)
                    edge |= output[t + k] == a && output[t + (k + 1) % 3] == b;
            check(edge, "every locked oriented crease segment survives");
        }
    auto invalid = locks; invalid.pop_back();
    check(run(invalid) == 5, "lock count rejected before native read");
    invalid = locks; invalid[0] = 4;
    check(run(invalid) == 5, "unknown vertex flag rejected");
    auto saved = triangles[0]; triangles[0] = count;
    check(run(locks) == 6, "invalid index rejected before native read"); triangles[0] = saved;
    std::cout << "native crease constraints: zero attributes, oriented seams, lock validation passed\n";
}
