// Replay managed native packing captures with the exact bridge ABI used by
// Unity. Private model data is supplied locally; no fixtures enter the package.
#include <chrono>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#include <algorithm>

extern "C" void xatlasCreate();
extern "C" void xatlasDestroy();
extern "C" int xatlasAddUvMesh(const float*,uint32_t,const uint32_t*,uint32_t,const uint32_t*,uint32_t);
extern "C" void xatlasComputeCharts();
extern "C" void xatlasPackChartsPreserveShape(int,uint32_t,float,uint32_t,int,int,int,int,int);
extern "C" uint32_t xatlasGetChartCount();
extern "C" uint32_t xatlasGetAtlasWidth();
extern "C" uint32_t xatlasGetAtlasHeight();
extern "C" int xatlasGetOutputVertexCount(int);
extern "C" int xatlasGetOutputVertexData(int,uint32_t*,float*,uint32_t*,int);
extern "C" uint64_t meshLabParallelPackingBatches();

template<class T> static T read(std::istream& input)
{
    T value{};
    if (!input.read(reinterpret_cast<char*>(&value),sizeof(T)))
        throw std::runtime_error("truncated packing capture");
    return value;
}

template<class T> static std::vector<T> values(std::istream& input)
{
    const int32_t count=read<int32_t>(input);
    if (count<0 || count>20000000) throw std::runtime_error("capture array exceeds bound");
    std::vector<T> result(count);
    if (count && !input.read(reinterpret_cast<char*>(result.data()),count*sizeof(T)))
        throw std::runtime_error("truncated capture array");
    return result;
}

template<class T> static void equal(const std::vector<T>& expected,const std::vector<T>& actual,const char* label)
{
    if (expected.size()!=actual.size() || (expected.size() &&
        std::memcmp(expected.data(),actual.data(),expected.size()*sizeof(T))!=0))
        throw std::runtime_error(std::string("native output differs: ")+label);
}

template<class T> static void write(std::ostream& output,const T& value)
{
    output.write(reinterpret_cast<const char*>(&value),sizeof(T));
}

template<class T> static void writeValues(std::ostream& output,const std::vector<T>& values)
{
    write(output,int32_t(values.size()));
    if (!values.empty()) output.write(reinterpret_cast<const char*>(values.data()),values.size()*sizeof(T));
}

static double replay(const std::filesystem::path& path,const std::filesystem::path& reply={})
{
    std::ifstream input(path,std::ios::binary);
    if (read<uint32_t>(input)!=0x58525031) throw std::runtime_error("unsupported packing capture");
    const uint32_t padding=read<uint32_t>(input);
    const float density=read<float>(input);
    const uint32_t resolution=read<uint32_t>(input);
    const bool brute=read<uint8_t>(input)!=0,rotate=read<uint8_t>(input)!=0,axis=read<uint8_t>(input)!=0;
    const auto uv=values<float>(input);
    const auto indices=values<uint32_t>(input),materials=values<uint32_t>(input);
    const uint32_t width=read<uint32_t>(input),height=read<uint32_t>(input),charts=read<uint32_t>(input);
    const auto refs=values<uint32_t>(input);
    const auto packed=values<float>(input);
    const auto chartIds=values<uint32_t>(input);
    if (input.peek()!=std::char_traits<char>::eof() || uv.size()%2 || indices.size()%3 ||
        materials.size()!=indices.size()/3 || packed.size()!=refs.size()*2 || chartIds.size()!=refs.size())
        throw std::runtime_error("inconsistent packing capture");
    xatlasCreate();
    try {
        if (xatlasAddUvMesh(uv.data(),uint32_t(uv.size()/2),indices.data(),uint32_t(indices.size()),
            materials.data(),uint32_t(materials.size()))!=0) throw std::runtime_error("native input refused");
        xatlasComputeCharts();
        const auto started=std::chrono::steady_clock::now();
        xatlasPackChartsPreserveShape(0,padding,density,resolution,1,0,brute,rotate,axis);
        const double ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-started).count();
        if (xatlasGetAtlasWidth()!=width || xatlasGetAtlasHeight()!=height || xatlasGetChartCount()!=charts ||
            xatlasGetOutputVertexCount(0)!=int(refs.size())) throw std::runtime_error("native dimensions/counts differ");
        std::vector<uint32_t> newRefs(refs.size()),newCharts(chartIds.size());
        std::vector<float> newUv(packed.size());
        if (xatlasGetOutputVertexData(0,newRefs.data(),newUv.data(),newCharts.data(),int(refs.size()))!=int(refs.size()))
            throw std::runtime_error("native output incomplete");
        equal(refs,newRefs,"references");equal(packed,newUv,"UV Float32 bits");equal(chartIds,newCharts,"chart IDs");
        if (!reply.empty()) {
            std::ofstream output(reply,std::ios::binary);
            write(output,0x58524f31u);write(output,width);write(output,height);write(output,charts);write(output,ms);
            writeValues(output,newRefs);writeValues(output,newUv);writeValues(output,newCharts);
            if (!output) throw std::runtime_error("cannot write native replay output");
        }
        xatlasDestroy();return ms;
    } catch (...) {xatlasDestroy();throw;}
}

int main(int argc,char** argv)
{
    if (argc!=3 && !(argc==4 && std::string(argv[1])=="--request")) {
        std::cerr << "usage: meshlab-packing-replay capture-root results.csv | --request capture reply\n";return 2;
    }
    try {
        if (argc==4) {replay(argv[2],argv[3]);return 0;}
        std::vector<std::filesystem::path> files;
        const std::filesystem::path root(argv[1]);
        for (const auto& entry:std::filesystem::recursive_directory_iterator(root))
            if (entry.is_regular_file() && entry.path().extension()==".xatlaspack") files.push_back(entry.path());
        std::sort(files.begin(),files.end());
        if (files.empty()) throw std::runtime_error("no packing requests executed");
        std::ofstream results(argv[2]);
        if (!results) throw std::runtime_error("cannot write replay report");
        results << "request,milliseconds\n";double total=0;
        for (const auto& file:files) {
            double ms;
            try {ms=replay(file);} catch (const std::exception& error) {
                throw std::runtime_error(file.string()+": "+error.what());
            }
            total+=ms;results << std::filesystem::relative(file,root).generic_string() << ',' << ms << '\n';
        }
        std::cout << files.size() << " native requests match byte for byte; placement " << total
            << " ms; oneTBB batches " << meshLabParallelPackingBatches() << "\n";
    } catch (const std::exception& error) {std::cerr << error.what() << '\n';return 1;}
}
