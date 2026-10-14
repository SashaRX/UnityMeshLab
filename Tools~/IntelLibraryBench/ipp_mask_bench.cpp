#include <ippcv.h>
#include <ippcore.h>
#include "../../Native~/third_party/xatlas/bit_image_overlap.h"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <memory>
#include <new>
#include <random>
#include <stdexcept>
#include <string>
#include <vector>

static void check(IppStatus status){if(status<ippStsNoErr)throw std::runtime_error(ippGetStatusString(status));}
static uint8_t reverse(uint8_t value){value=((value&0x55)<<1)|((value>>1)&0x55);value=((value&0x33)<<2)|((value>>2)&0x33);return uint8_t((value<<4)|(value>>4));}
struct Morph
{
    struct Free {void operator()(Ipp8u* p)const {ippFree(p);}};
    int width,height,stride;bool bits;std::unique_ptr<Ipp8u,Free> spec,buffer;std::vector<Ipp8u> source,destination;
    IppiSize roi()const{return bits?IppiSize{stride*8,height+2}:IppiSize{width,height};}
    Morph(int w,int h,bool bitImage):width(w),height(h),stride(bitImage?((w+63)/64)*8+2:w),bits(bitImage){
        int stateSize,bufferSize;IppiSize roi=this->roi(),maskSize{3,3};
        check(bits?ippiMorphologyBorderGetSize_1u_C1R(roi,maskSize,&stateSize,&bufferSize):ippiMorphologyBorderGetSize_8u_C1R(roi,maskSize,&stateSize,&bufferSize));
        spec.reset(static_cast<Ipp8u*>(ippMalloc(stateSize)));buffer.reset(static_cast<Ipp8u*>(ippMalloc(std::max(1,bufferSize))));if(!spec || !buffer)throw std::bad_alloc();source.resize(stride*(bits?h+2:h));destination.resize(source.size());
        Ipp8u mask[9]={1,1,1,1,1,1,1,1,1};
        check(bits?ippiMorphologyBorderInit_1u_C1R(roi,mask,maskSize,state(),buffer.get()):ippiMorphologyBorderInit_8u_C1R(roi,mask,maskSize,state(),buffer.get()));
    }
    Morph(const Morph&)=delete;Morph& operator=(const Morph&)=delete;
    IppiMorphState* state(){return reinterpret_cast<IppiMorphState*>(spec.get());}
    void kernel(){
        check(bits?ippiDilateBorder_1u_C1R(source.data(),stride,0,destination.data(),stride,0,roi(),ippBorderConst,0,state(),buffer.get()):
            ippiDilateBorder_8u_C1R(source.data(),stride,destination.data(),stride,roi(),ippBorderConst,0,state(),buffer.get()));
    }
    void run(const std::vector<uint64_t>& input,std::vector<uint64_t>& output){
        const int words=(width+63)/64;
        if(bits){const auto* bytes=reinterpret_cast<const uint8_t*>(input.data());for(int y=0;y<height;++y)for(int x=0;x<words*8;++x){
            uint8_t value=x*8>=width?0:bytes[y*words*8+x];if(x==width/8 && width%8)value&=uint8_t((1u<<(width%8))-1);source[(y+1)*stride+x+1]=reverse(value);}}
        else for(int y=0;y<height;++y)for(int x=0;x<width;++x)source[y*stride+x]=Ipp8u((input[y*words+x/64]>>(x%64))&1);
        kernel();
        if(bits){auto* bytes=reinterpret_cast<uint8_t*>(output.data());for(int y=0;y<height;++y)for(int x=0;x<words*8;++x)bytes[y*words*8+x]=reverse(destination[(y+1)*stride+x+1]);
            if(width%64)for(int y=0;y<height;++y)output[y*words+words-1]&=(UINT64_C(1)<<(width%64))-1;}
        else {std::fill(output.begin(),output.end(),0);for(int y=0;y<height;++y)for(int x=0;x<width;++x)
            if(destination[y*stride+x])output[y*words+x/64]|=UINT64_C(1)<<(x%64);}
    }
};
template<class F>static double measure(F action,int count){auto start=std::chrono::steady_clock::now();for(int i=0;i<count;++i){action();std::atomic_signal_fence(std::memory_order_seq_cst);}return std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count()/count;}
int main(int argc,char** argv)
{
    bool byteOnly=argc==3 && std::string(argv[2])=="--8u-only";
    if(argc!=2 && !byteOnly)return 2;
    try {
        std::ofstream report(argv[1]);report<<(byteOnly?"width,height,word_ms,ipp_8u_total_ms,ipp_8u_kernel_ms\n":"width,height,word_ms,ipp_1u_total_ms,ipp_8u_total_ms,ipp_1u_kernel_ms,ipp_8u_kernel_ms\n");
        std::mt19937_64 random(0x6bc2);int exact=0;
        for(int width:{7,63,64,65,129,257,511,512,513,1025,2048})for(int height:{3,32,257}){
            int stride=(width+63)/64;std::vector<uint64_t> input(stride*height),reference(input.size()),one(input.size()),eight(input.size());
            std::unique_ptr<Morph> bit=byteOnly?nullptr:std::make_unique<Morph>(width,height,true);Morph byte(width,height,false);
            for(int pattern=0;pattern<12;++pattern){
                for(auto& v:input)v=pattern==0?0:pattern<4?random():UINT64_C(1)<<(random()%64);
                xatlas::internal::dilateWords(input.data(),reference.data(),width,height,stride);if(bit)bit->run(input,one);byte.run(input,eight);
                if((bit && one!=reference) || eight!=reference){std::cerr<<"pattern "<<pattern<<" one="<<(one==reference)<<" eight="<<(eight==reference)<<'\n';for(size_t i=0;i<std::min<size_t>(input.size(),5);++i)std::cerr<<std::hex<<input[i]<<" -> "<<reference[i]<<" / "<<one[i]<<" / "<<eight[i]<<std::dec<<'\n';throw std::runtime_error("IPP dilation bits differ at "+std::to_string(width)+"x"+std::to_string(height));}
                exact+=bit?2:1;
            }
            const int repeats=200;
            for(int pair=0;pair<3;++pair){double scalar,packed,pixels;
                if(pair%2==0){scalar=measure([&]{xatlas::internal::dilateWords(input.data(),reference.data(),width,height,stride);},repeats);packed=bit?measure([&]{bit->run(input,one);},repeats):0;pixels=measure([&]{byte.run(input,eight);},repeats);}
                else {pixels=measure([&]{byte.run(input,eight);},repeats);packed=bit?measure([&]{bit->run(input,one);},repeats):0;scalar=measure([&]{xatlas::internal::dilateWords(input.data(),reference.data(),width,height,stride);},repeats);}
                double packedKernel=bit?measure([&]{bit->kernel();},repeats):0,pixelKernel=measure([&]{byte.kernel();},repeats);
                report<<width<<','<<height<<','<<scalar<<',';if(bit)report<<packed<<',';report<<pixels<<',';if(bit)report<<packedKernel<<',';report<<pixelKernel<<'\n';
            }
        }
        std::cout<<exact<<(byteOnly?" IPP 8-bit":" IPP 1-bit and 8-bit")<<" dilation results match every storage bit; cached setup, conversion included in total timings\n";
    }catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
}
