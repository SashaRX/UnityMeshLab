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
extern "C" int meshLabVoxelRemesh(const float*, uint32_t, const uint32_t*, uint32_t, int, uint32_t, void**, uint32_t*, uint32_t*);
extern "C" int meshLabSimplify(const float*, uint32_t, const uint32_t*, uint32_t, uint32_t, float, uint32_t,
    void**, uint32_t*, uint32_t*, float*);
extern "C" int meshLabMeshCopy(void*, float*, uint32_t, uint32_t*, uint32_t);
extern "C" void meshLabMeshDestroy(void*);
extern "C" int meshLabUnwrap(const float*, uint32_t, const uint32_t*, uint32_t, float, float,
    const float*, uint32_t, void**, uint32_t*, uint32_t*, uint32_t*);
extern "C" int meshLabUnwrapCopy(void*, float*, uint32_t, uint32_t*, uint32_t, int32_t*);
static void check(bool condition, const char* message) {
    if (!condition) { std::cerr << message << '\n'; std::exit(1); }
}
int main() {
    float p[] = {-1,-1,-1, 1,-1,-1, 1,1,-1, -1,1,-1, -1,-1,1, 1,-1,1, 1,1,1, -1,1,1};
    uint32_t t[] = {0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5};
    check(meshLabRemeshVersion() == 2, "ABI version");
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
    // xatlas uses absolute float epsilons internally. Before the bridge normalized
    // unwrap input, a physically small but otherwise valid mesh had every face marked
    // zero-area and came back as the generic "UV unwrap failed".
    float tiny[24];
    for (size_t i = 0; i < 24; ++i) tiny[i] = p[i] * 1e-5f;
    check(meshLabRemeshBuild(tiny,8,t,36,16,100,0.01f,1,0,256,4,1,&h,&v,&n)==0 && h, "tiny cube unwrap");
    check(v>0 && n>0 && n%3==0, "tiny cube counts");
    {
        std::vector<float> vertices(v*8);
        std::vector<uint32_t> indices(n);
        check(meshLabRemeshCopy(h,vertices.data(),v,indices.data(),n)==0, "tiny cube copy");
        for (uint32_t i=0;i<v;++i) {
            for (int k=0;k<8;++k) check(std::isfinite(vertices[i*8+k]), "tiny cube finite output");
            for (int k=6;k<8;++k) check(vertices[i*8+k]>=0 && vertices[i*8+k]<=1, "tiny cube normalized UV");
        }
    }
    meshLabRemeshDestroy(h); h=nullptr;

    // Bridge flags: bit 0 = meshopt_RemeshSolve, bit 1 = meshopt_RemeshShell. meshoptimizer
    // v1.3 renumbered that enum, so pin the mapping: a closed cube remeshed as a two-sided
    // shell keeps its inner surface as well and must come out larger than the solid remesh.
    uint32_t tris[4] = {};
    for (uint32_t flags = 0; flags < 4; ++flags) {
        check(meshLabRemeshBuild(p,8,t,36,16,100000,0.001f,1,0,256,4,flags,&h,&v,&n)==0 && h, "flag combination");
        tris[flags] = n/3;
        meshLabRemeshDestroy(h); h=nullptr;
    }
    check(tris[2] > tris[0] && tris[3] > tris[1], "shell flag reaches meshopt");
    std::cout << "cube solid/shell: " << tris[1] << " / " << tris[3] << " triangles\n";
    // Staged API: voxel -> simplify -> unwrap, each on plain indexed buffers.
    {
        uint32_t vc = 0, ic = 0;
        check(meshLabVoxelRemesh(p,8,t,36,3,1,&h,&vc,&ic)==1 && !h, "staged: reject grid resolution");
        check(meshLabVoxelRemesh(p,8,t,36,32,1,&h,&vc,&ic)==0 && h && vc>0 && ic%3==0, "staged: voxel remesh");
        std::vector<float> vp(vc*3); std::vector<uint32_t> vi(ic);
        check(meshLabMeshCopy(h,vp.data(),vc-1,vi.data(),ic)==1, "staged: mesh copy capacity guard");
        check(meshLabMeshCopy(h,vp.data(),vc,vi.data(),ic)==0, "staged: mesh copy");
        meshLabMeshDestroy(h); h=nullptr;

        // Error-limited simplification (no triangle budget, no regularization) must collapse
        // the flat cube faces far below the uniform voxel density.
        uint32_t sc = 0, si = 0; float achieved = -1;
        check(meshLabSimplify(vp.data(),vc,vi.data(),ic,0,0.01f,4,&h,&sc,&si,&achieved)==0 && h, "staged: adaptive simplify");
        check(si>0 && si%3==0 && si*4 < ic && achieved>=0, "staged: flat faces collapse");
        std::vector<float> sp(sc*3); std::vector<uint32_t> sidx(si);
        check(meshLabMeshCopy(h,sp.data(),sc,sidx.data(),si)==0, "staged: simplified copy");
        for (auto i:sidx) check(i<sc, "staged: simplified index range");
        meshLabMeshDestroy(h); h=nullptr;
        uint32_t rv = 0, ri = 0;
        check(meshLabSimplify(vp.data(),vc,vi.data(),ic,10,0.01f,32,&h,&rv,&ri,nullptr)==1 && !h, "staged: reject unknown simplify flag");

        float bad[] = {2,0,2,0.01f,6,4,0.5f,2,1,1024,4,0,1,0,0,1,1, 7};
        check(meshLabUnwrap(sp.data(),sc,sidx.data(),si,1,0,bad,18,&h,&v,&n,nullptr)==1 && !h, "staged: reject option overflow");
        bad[8] = 0;
        check(meshLabUnwrap(sp.data(),sc,sidx.data(),si,1,0,bad,17,&h,&v,&n,nullptr)==1 && !h, "staged: reject zero iterations");
        float options[] = {0,0,2,0.01f,6,4,0.5f,1,2,512,2,0,1,0,1,1,1};
        uint32_t charts = 0;
        check(meshLabUnwrap(sp.data(),sc,sidx.data(),si,1,0,options,17,&h,&v,&n,&charts)==0 && h, "staged: unwrap");
        check(v>0 && n>0 && n%3==0 && charts>0, "staged: unwrap counts");
        std::vector<float> uv(v*8); std::vector<uint32_t> ui(n); std::vector<int32_t> uc(v);
        check(meshLabUnwrapCopy(h,uv.data(),v,ui.data(),n,uc.data())==0, "staged: unwrap copy");
        for (auto i:ui) check(i<v, "staged: unwrap index range");
        for (auto c:uc) check(c>=0 && uint32_t(c)<charts, "staged: chart index range");
        for (uint32_t i=0;i<v;++i) for(int k=6;k<8;++k) check(uv[i*8+k]>=0 && uv[i*8+k]<=1, "staged: normalized UV");
        meshLabRemeshDestroy(h); h=nullptr;
        std::cout << "staged: " << ic/3 << " voxel -> " << si/3 << " simplified triangles, " << charts << " charts\n";
    }
    meshLabMeshDestroy(nullptr);
    meshLabRemeshDestroy(nullptr);
}
