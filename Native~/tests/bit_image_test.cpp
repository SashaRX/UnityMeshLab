#include "../third_party/xatlas/bit_image_overlap.h"
#include <cstdlib>
#include <iostream>
#include <random>
#include <vector>

// Original xatlas traversal, including storage tail bits and clipped reads.
static bool reference(const std::vector<uint64_t>& atlas, uint32_t width, uint32_t height,
    const std::vector<uint64_t>& image, uint32_t imageWidth, uint32_t imageHeight,
    uint32_t offsetX, uint32_t offsetY)
{
    const uint32_t stride = (width + 63) >> 6, imageStride = (imageWidth + 63) >> 6;
    for (uint32_t y = 0; y < imageHeight; ++y) {
        const uint32_t thisY = y + offsetY;
        if (thisY >= height) continue;
        uint32_t x = 0;
        for (;;) {
            const uint32_t thisX = x + offsetX;
            if (thisX >= width) break;
            const uint32_t thisShift = thisX % 64, shift = x % 64;
            const uint64_t destination = atlas[(thisX >> 6) + thisY * stride] >> thisShift;
            const uint64_t source = image[(x >> 6) + y * imageStride] >> shift;
            if ((destination & source) != 0) return false;
            x += 64 - std::max(thisShift, shift);
            if (x >= imageWidth) break;
        }
    }
    return true;
}

static void verify(const std::vector<uint64_t>& atlas, uint32_t width, uint32_t height,
    const std::vector<uint64_t>& image, uint32_t imageWidth, uint32_t imageHeight,
    uint32_t x, uint32_t y)
{
    const bool expected = reference(atlas,width,height,image,imageWidth,imageHeight,x,y);
    const bool actual = xatlas::internal::canBlitWords(atlas.data(),width,height,(width+63)>>6,
        image.data(),imageWidth,imageHeight,(imageWidth+63)>>6,x,y);
    if (actual != expected) {
        std::cerr << "overlap mismatch: atlas " << width << 'x' << height
            << ", mask " << imageWidth << 'x' << imageHeight << ", offset " << x << ',' << y << '\n';
        std::exit(1);
    }
}

static std::vector<uint64_t> referenceDilation(const std::vector<uint64_t>& source,uint32_t width,uint32_t height)
{
    const uint32_t stride=(width+63)>>6;
    std::vector<uint64_t> result(source.size());
    for(uint32_t y=0;y<height;++y) for(uint32_t x=0;x<width;++x) {
        bool occupied=false;
        for(int dy=-1;dy<=1;++dy) for(int dx=-1;dx<=1;++dx) {
            const int sx=int(x)+dx,sy=int(y)+dy;
            if(sx<0 || sy<0 || sx>=int(width) || sy>=int(height)) continue;
            occupied|=(source[uint32_t(sy)*stride+(uint32_t(sx)>>6)]&(UINT64_C(1)<<(sx&63)))!=0;
        }
        if(occupied) result[y*stride+(x>>6)]|=UINT64_C(1)<<(x&63);
    }
    return result;
}

int main()
{
    std::mt19937_64 random(0x6bc2);
    uint64_t comparisons = 0;
    // Every alignment, both word edges, clipped rows/columns, sparse rows and
    // dirty final words (possible after shrinking an atlas without clearing).
    for (uint32_t width : {1u,7u,63u,64u,65u,127u,128u,129u,257u})
        for (uint32_t imageWidth : {1u,7u,63u,64u,65u,127u,128u,129u,257u})
            for (int pattern = 0; pattern < 12; ++pattern) {
                const uint32_t height = 9, imageHeight = 7;
                std::vector<uint64_t> atlas(((width+63)>>6)*height), image(((imageWidth+63)>>6)*imageHeight);
                for (auto& word : atlas) word = pattern < 2 ? 0 : pattern < 6 ? random() : UINT64_C(1) << (random()%64);
                for (auto& word : image) word = pattern == 1 || pattern == 2 ? 0 : pattern < 6 ? random() : UINT64_C(1) << (random()%64);
                for (uint32_t y = 0; y <= height+1; ++y)
                    for (uint32_t x = 0; x <= width+65; ++x) {
                        verify(atlas,width,height,image,imageWidth,imageHeight,x,y);
                        ++comparisons;
                    }
            }
    // Isolate each bit across misaligned word splits. Dense random tests alone
    // would usually stop at the first collision and miss later fragments.
    for (uint32_t shift = 0; shift < 64; ++shift)
        for (uint32_t bit = 0; bit < 129; ++bit) {
            std::vector<uint64_t> atlas(5), image(3);
            image[bit>>6] = UINT64_C(1) << (bit&63);
            for (uint32_t candidate : {bit+shift,bit+shift+1}) {
                std::fill(atlas.begin(),atlas.end(),0);
                atlas[candidate>>6] = UINT64_C(1) << (candidate&63);
                verify(atlas,257,1,image,129,1,shift,0);
                ++comparisons;
            }
        }
    std::cout << comparisons << " exact reference overlap comparisons passed\n";
    // Wide sparse masks exercise SIMD past its scalar early-rejection prefix.
    for (uint32_t width : {511u,512u,513u,1023u,1024u,1025u})
        for (uint32_t shift = 0; shift < 64; ++shift)
            for (int pattern = 0; pattern < 20; ++pattern) {
                const uint32_t imageWidth=width-70, height=65;
                std::vector<uint64_t> atlas(((width+63)>>6)*height),image(((imageWidth+63)>>6)*height);
                for (auto& bits:image) bits=pattern<10?random():UINT64_C(1)<<(random()%64);
                if(pattern!=0) for(uint32_t index=10;index<atlas.size();++index)
                    atlas[index]=pattern<10?(random()%9==0?random():0):(random()%23==0?UINT64_C(1)<<(random()%64):0);
                verify(atlas,width,height,image,imageWidth,height,shift,0);
                verify(atlas,width,height,image,imageWidth,height,shift,63);
                comparisons+=2;
            }
    std::cout << comparisons << " total exact overlap comparisons including wide masks\n";
    uint32_t dilationPasses=0;
    for(uint32_t width:{0u,1u,7u,63u,64u,65u,127u,128u,129u,257u,511u,513u,1025u})
        for(uint32_t height:{0u,1u,2u,7u,32u})
            for(int pattern=0;pattern<12;++pattern) {
                const uint32_t stride=(width+63)>>6;
                std::vector<uint64_t> slow(stride*height),fast;
                for(auto& word:slow) word=pattern<2?0:pattern<6?random():UINT64_C(1)<<(random()%64);
                fast=slow;
                for(int padding=0;padding<8;++padding) {
                    slow=referenceDilation(slow,width,height);
                    // Deliberately dirty output storage verifies full overwrite.
                    std::vector<uint64_t> output(fast.size(),UINT64_MAX);
                    xatlas::internal::dilateWords(fast.data(),output.data(),width,height,stride);
                    if(slow!=output) {std::cerr << "dilation mismatch: " << width << 'x' << height << '\n';return 1;}
                    fast=output;++dilationPasses;
                }
            }
    std::cout << dilationPasses << " exact reference dilation passes matched all storage bits\n";
}
