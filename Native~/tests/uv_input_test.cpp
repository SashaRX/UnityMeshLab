#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <limits>

extern "C" void xatlasCreate();
extern "C" void xatlasDestroy();
extern "C" int xatlasAddUvMesh(const float*, uint32_t, const uint32_t*, uint32_t, const uint32_t*, uint32_t);
extern "C" void xatlasComputeCharts();
extern "C" void xatlasPackCharts(int, uint32_t, float, uint32_t, int, int, int, int, int);
extern "C" void xatlasPackChartsPreserveShape(int, uint32_t, float, uint32_t, int, int, int, int, int);
extern "C" uint32_t xatlasGetChartCount();
extern "C" uint32_t xatlasGetAtlasWidth();
extern "C" uint32_t xatlasGetAtlasHeight();
extern "C" int xatlasGetOutputVertexCount(int);
extern "C" int xatlasGetOutputVertexData(int, uint32_t*, float*, uint32_t*, int);

static void check(bool value, const char* message)
{
    if (!value) { std::cerr << message << '\n'; std::exit(1); }
}

static void smallQuad(float side, float offset, bool mirrored)
{
    const float width = mirrored ? -side : side;
    const float uv[] = {offset, offset, offset + width, offset,
                        offset + width, offset + side, offset, offset + side};
    const uint32_t indices[] = {0, 1, 2, 0, 2, 3};
    const uint32_t materials[] = {0, 0};
    xatlasCreate();
    check(xatlasAddUvMesh(uv, 4, indices, 6, materials, 2) == 0, "small UV: add mesh");
    xatlasComputeCharts();
    xatlasPackCharts(0, 2, 0, 128, 1, 0, 1, 1, 0);
    check(xatlasGetChartCount() == 1, "small UV: nondegenerate quad must become a chart");
    check(xatlasGetOutputVertexCount(0) == 4, "small UV: vertex count");
    uint32_t refs[4], charts[4]; float output[8];
    check(xatlasGetOutputVertexData(0, refs, output, charts, 4) == 4, "small UV: output");
    for (int i = 0; i < 4; ++i) {
        check(charts[i] != UINT32_MAX, "small UV: vertex must be packed");
        check(std::isfinite(output[i * 2]) && std::isfinite(output[i * 2 + 1]), "small UV: finite coordinates");
    }
    const double ux = double(output[2]) - output[0], uy = double(output[3]) - output[1];
    const double vx = double(output[4]) - output[0], vy = double(output[5]) - output[1];
    check(std::abs(ux * vy - uy * vx) > 1e-8, "small UV: positive packed triangle area");
    xatlasDestroy();
}

static void invalidTriangle(float side, bool nonfinite)
{
    const float uv[] = {0, 0, side, side, side * 2,
        nonfinite ? std::numeric_limits<float>::infinity() : side * 2};
    const uint32_t indices[] = {0, 1, 2}, material[] = {0};
    xatlasCreate();
    check(xatlasAddUvMesh(uv, 3, indices, 3, material, 1) == 0, "invalid UV: add mesh");
    xatlasComputeCharts();
    xatlasPackCharts(0, 2, 0, 128, 1, 0, 1, 1, 0);
    check(xatlasGetChartCount() == 0, "invalid UV: collinear and non-finite faces must remain ignored");
    xatlasDestroy();
}

static void skinnyShape(bool blocks, bool rotated)
{
    const float uv[] = {0,0,1,0,1,.0001f,0,.0001f};
    const uint32_t indices[] = {0,1,2,0,2,3}, materials[] = {0,0};
    xatlasCreate();
    check(xatlasAddUvMesh(uv,4,indices,6,materials,2)==0,"shape: add");
    xatlasComputeCharts();
    xatlasPackChartsPreserveShape(0,2,0,128,1,blocks,0,rotated,rotated);
    uint32_t refs[4],charts[4]; float packed[8],output[8];
    check(xatlasGetOutputVertexData(0,refs,packed,charts,4)==4,"shape: output");
    const float width=float(xatlasGetAtlasWidth()),height=float(xatlasGetAtlasHeight());
    for(int i=0;i<4;++i) { check(refs[i]<4 && charts[i]!=UINT32_MAX,"shape: references"); output[refs[i]*2]=packed[i*2]*width; output[refs[i]*2+1]=packed[i*2+1]*height; }
    const double a=std::hypot(double(output[2])-output[0],double(output[3])-output[1]);
    const double b=std::hypot(double(output[6])-output[0],double(output[7])-output[1]);
    check(b>0 && std::abs((a/b)/10000-1)<.002,"shape: subpixel short edge retains chart aspect ratio");
    xatlasDestroy();
}

int main()
{
    for (bool mirrored : {false, true}) {
        smallQuad(1e-4f, 0, mirrored);
        smallQuad(1e-8f, 0, mirrored);
        smallQuad(1e-4f, .75f, mirrored);
    }
    invalidTriangle(1, false); invalidTriangle(1e-8f, false); invalidTriangle(1, true);
    for(bool blocks:{false,true}) for(bool rotated:{false,true}) skinnyShape(blocks,rotated);
    std::cout << "UV input: small and mirrored faces pack, degenerate and non-finite faces remain ignored\n";
}
