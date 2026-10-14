#pragma once

#include <algorithm>
#include <cstdint>
#if defined(MESHLAB_ISPC_PACKING)
#include "ispc_masks.h"
#endif

// Mesh Lab: compare each source word once, without changing xatlas' clipped
// word/tail semantics. Padding bits are deliberately not masked here: the
// original overlap test also reads the complete final storage word.
namespace xatlas { namespace internal {
#if defined(_MSC_VER)
#define MESHLAB_BIT_INLINE __forceinline
#else
#define MESHLAB_BIT_INLINE inline
#endif
MESHLAB_BIT_INLINE bool canBlitWords(const uint64_t *atlas, uint32_t width, uint32_t height, uint32_t stride,
    const uint64_t *image, uint32_t imageWidth, uint32_t imageHeight, uint32_t imageStride,
    uint32_t offsetX, uint32_t offsetY)
{
    if (offsetX >= width || offsetY >= height || imageWidth == 0 || imageHeight == 0)
        return true;
    // Most rejected candidates collide in the first fragment. Keep that
    // original cheap rejection ahead of word counts and row-loop setup.
    const uint32_t first = offsetX >> 6, shift = offsetX & 63;
    if (((atlas[offsetY * stride + first] >> shift) & image[0]) != 0)
        return false;
    const uint32_t clippedWidth = std::min(imageWidth, width - offsetX);
    const uint32_t rows = std::min(imageHeight, height - offsetY);
    const uint32_t words = (clippedWidth + 63) >> 6;
#if defined(MESHLAB_ISPC_PACKING)
    // Tiny masks and immediate collisions stay on the inexpensive scalar path.
    if (words >= 8 && uint64_t(words) * rows >= 128)
        return ispc::meshLabCanBlitIspc(atlas,stride,image,imageStride,first,offsetY,shift,clippedWidth,rows);
#endif
    if (shift == 0) {
        for (uint32_t y = 0; y < rows; ++y) {
            const uint64_t *destination = atlas + (y + offsetY) * stride + first;
            const uint64_t *source = image + y * imageStride;
            for (uint32_t word = 0; word < words; ++word)
                if ((destination[word] & source[word]) != 0)
                    return false;
        }
        return true;
    }
    const uint32_t tailShift = 64 - shift;
    for (uint32_t y = 0; y < rows; ++y) {
        const uint64_t *destination = atlas + (y + offsetY) * stride + first;
        const uint64_t *source = image + y * imageStride;
        for (uint32_t word = 0; word < words; ++word) {
            const uint64_t bits = source[word];
            if (bits == 0)
                continue;
            if (((destination[word] >> shift) & bits) != 0)
                return false;
            // The second destination word is consulted only if the original
            // fragment traversal would reach it. This also bounds the read at
            // both right edges, including a source narrower than one word.
            if (word * 64 + tailShift < clippedWidth &&
                ((destination[word + 1] << tailShift) & bits) != 0)
                return false;
        }
    }
    return true;
}

// One original eight-neighbour dilation pass. Unlike overlap lookup, dilation
// must clear storage tail bits: xatlas visits only valid pixels when writing
// the temporary image. Mask each source tail before propagating neighbours.
inline void dilateWords(const uint64_t *source, uint64_t *destination,
    uint32_t width, uint32_t height, uint32_t stride)
{
    if (stride == 0 || height == 0) return;
#if defined(MESHLAB_ISPC_PACKING)
    if (uint64_t(stride) * height >= 256) {
        ispc::meshLabDilateIspc(source,destination,width,height,stride);
        return;
    }
#endif
    const uint32_t tail = width & 63;
    const uint64_t tailMask = tail == 0 ? UINT64_MAX : (UINT64_C(1) << tail) - 1;
    for (uint32_t y = 0; y < height; ++y) {
        const uint64_t *row = source + y * stride;
        const uint64_t *above = y > 0 ? row - stride : nullptr;
        const uint64_t *below = y + 1 < height ? row + stride : nullptr;
        const auto vertical = [&](uint32_t word) {
            uint64_t bits = row[word];
            if (above) bits |= above[word];
            if (below) bits |= below[word];
            return word + 1 == stride ? bits & tailMask : bits;
        };
        uint64_t previous = 0, current = vertical(0);
        for (uint32_t word = 0; word < stride; ++word) {
            const uint64_t next = word + 1 < stride ? vertical(word + 1) : 0;
            uint64_t bits = current | (current << 1) | (current >> 1) | (previous >> 63) | (next << 63);
            if (word + 1 == stride) bits &= tailMask;
            destination[y * stride + word] = bits;
            previous = current;current = next;
        }
    }
}
#undef MESHLAB_BIT_INLINE
}}
