#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <limits>
#include <vector>
extern "C" int meshLabRemeshVersion();
extern "C" int meshLabRemeshBuild(const float*, uint32_t, const uint32_t*, uint32_t,
    int, uint32_t, float, float, float, uint32_t, uint32_t, uint32_t, void**, uint32_t*, uint32_t*);
extern "C" int meshLabRemeshCopy(void*, float*, uint32_t, uint32_t*, uint32_t);
extern "C" void meshLabRemeshDestroy(void*);
static void check(bool condition, const char* message) {
    if (!condition) { std::cerr << message << '\n'; std::exit(1); }
}
int main() {
    float p[] = {-1,-1,-1, 1,-1,-1, 1,1,-1, -1,1,-1, -1,-1,1, 1,-1,1, 1,1,1, -1,1,1};
    uint32_t t[] = {0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5};
    check(meshLabRemeshVersion() == 1, "ABI version");
    void* h = nullptr; uint32_t v = 0, n = 0;
    check(meshLabRemeshBuild(p,8,t,36,3,100,0.01f,1,0,256,4,1,&h,&v,&n)==1 && !h, "reject grid resolution");
    t[0]=99;
    check(meshLabRemeshBuild(p,8,t,36,16,100,0.01f,1,0,256,4,1,&h,&v,&n)==1 && !h, "reject bad index");
    t[0]=0;
    p[0]=std::numeric_limits<float>::quiet_NaN();
    check(meshLabRemeshBuild(p,8,t,36,16,100,0.01f,1,0,256,4,1,&h,&v,&n)==1 && !h, "reject NaN");
    p[0]=-1;
    for (int pass=0; pass<2; ++pass) {
        check(meshLabRemeshBuild(p,8,t,36,16,100,pass ? 1.0f : 0.01f,1,0,256,4,1,&h,&v,&n)==0 && h, "cube pipeline");
        check(v>0 && n>0 && n%3==0, "valid counts");
        if (pass) check(n/3 <= 100, "target budget with relaxed error");
        std::vector<float> vertices(v*8); std::vector<uint32_t> indices(n);
        check(meshLabRemeshCopy(h,vertices.data(),0,indices.data(),n)==1, "copy capacity guard");
        check(meshLabRemeshCopy(h,vertices.data(),v,indices.data(),n)==0, "copy result");
        for (auto i:indices) check(i<v, "valid index");
        for (uint32_t i=0;i<v;++i) {
            for(int k=0;k<8;++k) check(std::isfinite(vertices[i*8+k]), "finite vertex data");
            float norm=0; for(int k=3;k<6;++k) norm+=vertices[i*8+k]*vertices[i*8+k];
            check(std::abs(norm-1)<0.01f, "unit normals");
            for(int k=6;k<8;++k) check(vertices[i*8+k]>=0 && vertices[i*8+k]<=1, "normalized UV");
        }
        meshLabRemeshDestroy(h); h=nullptr;
        std::cout << "cube: " << v << " vertices, " << n/3 << " triangles\n";
    }
    meshLabRemeshDestroy(nullptr);
}
