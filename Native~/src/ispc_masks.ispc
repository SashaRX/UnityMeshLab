// Experimental integer-only kernels. No floating point packing decisions change.
export uniform bool meshLabCanBlitIspc(uniform const uint64 atlas[], uniform uint32 stride,
    uniform const uint64 image[], uniform uint32 imageStride, uniform uint32 first,
    uniform uint32 offsetY, uniform uint32 bitShift, uniform uint32 width, uniform uint32 rows)
{
    uniform uint32 words = (width + 63) >> 6;
    for (uniform uint32 y = 0; y < rows; ++y) {
        for (uniform uint32 base = 0; base < words; base += programCount) {
            uint32 word = base + programIndex;
            bool collision = false;
            if (word < words) {
                uint64 bits = image[y * imageStride + word];
                uint64 destination = atlas[(y + offsetY) * stride + first + word];
                collision = ((destination >> bitShift) & bits) != 0;
                if (bitShift != 0 && word * 64 + (64 - bitShift) < width) {
                    uint64 next = atlas[(y + offsetY) * stride + first + word + 1];
                    collision |= ((next << (64 - bitShift)) & bits) != 0;
                }
            }
            if (any(collision)) return false;
        }
    }
    return true;
}

export void meshLabDilateIspc(uniform const uint64 source[], uniform uint64 destination[],
    uniform uint32 width, uniform uint32 height, uniform uint32 stride)
{
    uniform uint32 tail = width & 63;
    uniform uint64 tailMask = tail == 0 ? (uint64)-1 : ((uint64)1 << tail) - 1;
    for (uniform uint32 y = 0; y < height; ++y) {
      foreach (word = 0 ... stride) {
        uint32 index = y * stride + word;
        uint64 current = source[index], previous = 0, next = 0;
        if (y > 0) current |= source[index - stride];
        if (y + 1 < height) current |= source[index + stride];
        if (word + 1 == stride) current &= tailMask;
        if (word > 0) {
            previous = source[index - 1];
            if (y > 0) previous |= source[index - stride - 1];
            if (y + 1 < height) previous |= source[index + stride - 1];
        }
        if (word + 1 < stride) {
            next = source[index + 1];
            if (y > 0) next |= source[index - stride + 1];
            if (y + 1 < height) next |= source[index + stride + 1];
            if (word + 2 == stride) next &= tailMask;
        }
        uint64 bits = current | (current << 1) | (current >> 1) | (previous >> 63) | (next << 63);
        if (word + 1 == stride) bits &= tailMask;
        destination[index] = bits;
      }
    }
}
