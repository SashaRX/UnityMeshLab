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
    check(meshLabRemeshVersion() == 3, "ABI version");
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
        std::vector<float> vertices(v*16); std::vector<uint32_t> indices(n);
        check(meshLabRemeshCopy(h,vertices.data(),0,indices.data(),n)==1, "copy capacity guard");
        check(meshLabRemeshCopy(h,vertices.data(),v,indices.data(),n)==0, "copy result");
        for (auto i:indices) check(i<v, "valid index");
        for (uint32_t i=0;i<v;++i) {
            for(int k=0;k<16;++k) check(std::isfinite(vertices[i*16+k]), "finite vertex data");
            float norm=0; for(int k=3;k<6;++k) norm+=vertices[i*16+k]*vertices[i*16+k];
            check(std::abs(norm-1)<0.01f, "unit normals");
            for(int k=6;k<8;++k) check(vertices[i*16+k]>=0 && vertices[i*16+k]<=1, "normalized UV");
            float tangent=0; for(int k=8;k<11;++k) tangent+=vertices[i*16+k]*vertices[i*16+k];
            check(std::abs(tangent-1)<0.01f, "unit tangents");
            check(std::abs(vertices[i*16+11])==1, "tangent handedness");
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
        std::vector<float> vertices(v*16);
        std::vector<uint32_t> indices(n);
        check(meshLabRemeshCopy(h,vertices.data(),v,indices.data(),n)==0, "tiny cube copy");
        for (uint32_t i=0;i<v;++i) {
            for (int k=0;k<16;++k) check(std::isfinite(vertices[i*16+k]), "tiny cube finite output");
            for (int k=6;k<8;++k) check(vertices[i*16+k]>=0 && vertices[i*16+k]<=1, "tiny cube normalized UV");
        }
    }
    meshLabRemeshDestroy(h); h=nullptr;

    // The editor's island hard-edge modes pass crease = pi (the chart borders carry the
    // hard edges; the native call makes no crease splits of its own), and real captures
    // are ~5 mm models remeshed densely — a path the unit-scale cube tests never exercise.
    // Pin every field of the wire layout on it: all-zero normals shipped here once under
    // ABI 3 and the bake projected through zeroed ray directions.
    {
        std::vector<float> sp; std::vector<uint32_t> si;
        const int grid = 12;
        const float radius = 2.5e-3f;
        const int quads[6][4] = {
            {0,1,2,3}, {4,5,6,7}, {0,1,5,4}, {3,2,6,7}, {0,3,7,4}, {1,2,6,5},
        };
        auto point = [&](int q0, int q1, int q2, int q3, float u, float v, float* out) {
            for (int k = 0; k < 3; ++k) {
                float a = p[q0 * 3 + k] * (1 - u) + p[q1 * 3 + k] * u;
                float b = p[q3 * 3 + k] * (1 - u) + p[q2 * 3 + k] * u;
                out[k] = (a * (1 - v) + b * v);
            }
            float len = std::sqrt(out[0] * out[0] + out[1] * out[1] + out[2] * out[2]);
            for (int k = 0; k < 3; ++k) out[k] = out[k] / len * radius;
        };
        for (auto& q : quads)
            for (int i = 0; i < grid; ++i)
                for (int j = 0; j < grid; ++j) {
                    float a[3], b[3], c[3], d[3];
                    point(q[0], q[1], q[2], q[3], float(i) / grid, float(j) / grid, a);
                    point(q[0], q[1], q[2], q[3], float(i + 1) / grid, float(j) / grid, b);
                    point(q[0], q[1], q[2], q[3], float(i + 1) / grid, float(j + 1) / grid, c);
                    point(q[0], q[1], q[2], q[3], float(i) / grid, float(j + 1) / grid, d);
                    // Outward winding is not uniform across the hand-listed quads; the
                    // sphere is centred at the origin, so cross.centroid signs it.
                    uint32_t base = uint32_t(sp.size() / 3);
                    for (float* pv : {a, b, c, d}) { sp.push_back(pv[0]); sp.push_back(pv[1]); sp.push_back(pv[2]); }
                    float cx = (b[1]-a[1])*(c[2]-a[2]) - (b[2]-a[2])*(c[1]-a[1]);
                    float cy = (b[2]-a[2])*(c[0]-a[0]) - (b[0]-a[0])*(c[2]-a[2]);
                    float cz = (b[0]-a[0])*(c[1]-a[1]) - (b[1]-a[1])*(c[0]-a[0]);
                    float centroid[3] = {a[0]+b[0]+c[0], a[1]+b[1]+c[1], a[2]+b[2]+c[2]};
                    if (cx * centroid[0] + cy * centroid[1] + cz * centroid[2] >= 0) {
                si.push_back(base); si.push_back(base + 2); si.push_back(base + 1);
                si.push_back(base); si.push_back(base + 3); si.push_back(base + 2);
            } else {
                si.push_back(base); si.push_back(base + 1); si.push_back(base + 2);
                si.push_back(base); si.push_back(base + 2); si.push_back(base + 3);
            }
                }
        check(meshLabRemeshBuild(sp.data(), uint32_t(sp.size() / 3), si.data(), uint32_t(si.size()),
            64, 1400, 0.01f, 3.141593f, 0, 1024, 4, 1, &h, &v, &n) == 0 && h, "tiny dense sphere pipeline");
        check(v > 0 && n > 0 && n % 3 == 0, "tiny dense sphere counts");
        std::vector<float> vertices(v * 16); std::vector<uint32_t> indices(n);
        check(meshLabRemeshCopy(h, vertices.data(), v, indices.data(), n) == 0, "tiny dense sphere copy");
        for (uint32_t i = 0; i < v; ++i) {
            for (int k = 0; k < 16; ++k) check(std::isfinite(vertices[i * 16 + k]), "tiny dense sphere finite");
            float norm = 0; for (int k = 3; k < 6; ++k) norm += vertices[i * 16 + k] * vertices[i * 16 + k];
            check(std::abs(norm - 1) < 0.01f, "tiny dense sphere unit normals");
            for (int k = 6; k < 8; ++k) check(vertices[i * 16 + k] >= 0 && vertices[i * 16 + k] <= 1, "tiny dense sphere normalized UV");
            float tangent = 0; for (int k = 8; k < 11; ++k) tangent += vertices[i * 16 + k] * vertices[i * 16 + k];
            check(std::abs(tangent - 1) < 0.01f, "tiny dense sphere unit tangents");
            check(std::abs(vertices[i * 16 + 11]) == 1, "tiny dense sphere tangent handedness");
        }
        meshLabRemeshDestroy(h); h = nullptr;
        std::cout << "tiny dense sphere: " << v << " vertices, " << n / 3 << " triangles\n";
    }

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
        std::vector<float> uv(v*16); std::vector<uint32_t> ui(n); std::vector<int32_t> uc(v);
        check(meshLabUnwrapCopy(h,uv.data(),v,ui.data(),n,uc.data())==0, "staged: unwrap copy");
        for (auto i:ui) check(i<v, "staged: unwrap index range");
        for (auto c:uc) check(c>=0 && uint32_t(c)<charts, "staged: chart index range");
        for (uint32_t i=0;i<v;++i) {
            for(int k=6;k<8;++k) check(uv[i*16+k]>=0 && uv[i*16+k]<=1, "staged: normalized UV");
            float tangent=0; for(int k=8;k<11;++k) tangent+=uv[i*16+k]*uv[i*16+k];
            check(std::abs(tangent-1)<0.01f, "staged: unit tangents");
            check(std::abs(uv[i*16+11])==1, "staged: tangent handedness");
        }
        meshLabRemeshDestroy(h); h=nullptr;
        std::cout << "staged: " << ic/3 << " voxel -> " << si/3 << " simplified triangles, " << charts << " charts\n";
    }
    meshLabMeshDestroy(nullptr);
    meshLabRemeshDestroy(nullptr);
}
