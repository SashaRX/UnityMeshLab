using System.Threading;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Exact squared Euclidean distance transform for texture edge extension.
    /// Array-only work: safe on bake workers, linear in the atlas pixel count.</summary>
    internal static class TextureDilation
    {
        // Consumes the scratch mask: >= 0 means filled. Returns the closest filled
        // pixel's index, with the smaller index winning ties; -1 without any seeds.
        internal static int[] NearestFilled(int[] filled, int size, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var nearest = new int[filled.Length];
            // Nearest seed in each row. Remembering seeds as their own indices lets
            // the reverse sweep distinguish original seeds from forward assignments.
            for (int y = 0; y < size; ++y) {
                token.ThrowIfCancellationRequested();
                int row = y * size, left = -1;
                for (int x = 0; x < size; ++x) {
                    int i = row + x;
                    if (filled[i] >= 0) left = i;
                    filled[i] = left;
                }
                int right = -1;
                for (int x = size - 1; x >= 0; --x) {
                    int i = row + x;
                    if (filled[i] == i) right = i;
                    else if (right >= 0 && (filled[i] < 0 || right - i < i - filled[i])) filled[i] = right;
                }
            }
            // Per column: the lower envelope of parabolas (y - seedY)^2 + rowDistance^2.
            var sites = new int[size]; var starts = new int[size];
            for (int x = 0; x < size; ++x) {
                token.ThrowIfCancellationRequested();
                int end = -1;
                for (int y = 0; y < size; ++y) {
                    int seed = filled[y * size + x];
                    if (seed < 0) continue;
                    int begin = 0;
                    long dx = x - seed % size;
                    long cost = dx * dx + (long)y * y;
                    while (end >= 0) {
                        int previous = sites[end];
                        long previousDx = x - filled[previous * size + x] % size;
                        long numerator = cost - previousDx * previousDx - (long)previous * previous;
                        long denominator = 2L * (y - previous);
                        // The new (higher-index) seed must be strictly closer. Integer
                        // floor handles negative intersections and leaves ties with the old seed.
                        long boundary = numerator / denominator;
                        if (numerator < 0 && numerator % denominator != 0) --boundary;
                        begin = (int)(boundary + 1);
                        if (begin > starts[end]) break;
                        --end;
                    }
                    sites[++end] = y; starts[end] = end == 0 ? 0 : begin;
                }
                int segment = 0;
                for (int y = 0; y < size; ++y) {
                    while (segment < end && starts[segment + 1] <= y) ++segment;
                    nearest[y * size + x] = end < 0 ? -1 : filled[sites[segment] * size + x];
                }
            }
            return nearest;
        }
    }
}
