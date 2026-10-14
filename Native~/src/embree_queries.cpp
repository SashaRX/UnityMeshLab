#if defined(_WIN32)
#define MESHLAB_QUERY_API __declspec(dllexport)
#else
#define MESHLAB_QUERY_API __attribute__((visibility("default")))
#endif
#include <embree4/rtcore.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstddef>
#include <limits>
#include <memory>
#include <thread>
#include <vector>

struct Vec { float x,y,z; };
static Vec operator+(Vec a,Vec b){return {a.x+b.x,a.y+b.y,a.z+b.z};}
static Vec operator-(Vec a,Vec b){return {a.x-b.x,a.y-b.y,a.z-b.z};}
static Vec operator*(float s,Vec a){return {s*a.x,s*a.y,s*a.z};}
static float dot(Vec a,Vec b){return a.x*b.x+a.y*b.y+a.z*b.z;}
struct Query {Vec origin,direction;float reach,target,dotMin;int mode;};
struct Result {int tri;float distance;Vec bary,point;};
static_assert(sizeof(Query)==40 && sizeof(Result)==32,"managed query ABI");
struct State {RTCDevice device=nullptr;RTCScene scene=nullptr,broadScene=nullptr;float broadPadding=0;std::vector<Vec> vertices,normals;std::vector<int> indices;std::vector<uint8_t> either;
    ~State() {if(broadScene)rtcReleaseScene(broadScene);if(scene)rtcReleaseScene(scene);if(device)rtcReleaseDevice(device);}
};
struct RayContext {RTCRayQueryContext rtc;const Query* query;Result result;};
static_assert(offsetof(RayContext,rtc)==0,"Embree context prefix");
struct PointContext {const State* state;const Query* query;Result result;};

// The same Ericson branches and Float32 operation order as TriangleBvh.
static Vec closest(Vec p,Vec a,Vec b,Vec c,Vec& bary)
{
    Vec ab=b-a,ac=c-a,ap=p-a;float d1=dot(ab,ap),d2=dot(ac,ap);
    if(d1<=0 && d2<=0){bary={1,0,0};return a;}
    Vec bp=p-b;float d3=dot(ab,bp),d4=dot(ac,bp);
    if(d3>=0 && d4<=d3){bary={0,1,0};return b;}
    float vc=d1*d4-d3*d2;
    if(vc<=0 && d1>=0 && d3<=0){float v=d1/(d1-d3);bary={1-v,v,0};return a+v*ab;}
    Vec cp=p-c;float d5=dot(ab,cp),d6=dot(ac,cp);
    if(d6>=0 && d5<=d6){bary={0,0,1};return c;}
    float vb=d5*d2-d1*d6;
    if(vb<=0 && d2>=0 && d6<=0){float w=d2/(d2-d6);bary={1-w,0,w};return a+w*ac;}
    float va=d3*d6-d5*d4;
    if(va<=0 && d4-d3>=0 && d5-d6>=0){float w=(d4-d3)/((d4-d3)+(d5-d6));bary={0,1-w,w};return b+w*(c-b);}
    float sum=va+vb+vc;
    if(!(sum>0)){float da=dot(p-a,p-a),db=dot(p-b,p-b),dc=dot(p-c,p-c);
        if(da<=db && da<=dc){bary={1,0,0};return a;}if(db<=dc){bary={0,1,0};return b;}bary={0,0,1};return c;}
    float denom=1/sum,v=vb*denom,w=vc*denom;bary={1-v-w,v,w};return a+v*ab+w*ac;
}
static void filter(const RTCFilterFunctionNArguments* args)
{
    auto& context=*reinterpret_cast<RayContext*>(args->context);
    const auto& state=*static_cast<const State*>(args->geometryUserPtr);
    const auto& q=*context.query;
    for(unsigned lane=0;lane<args->N;++lane){
        if(args->valid[lane]!=-1) continue;
        unsigned tri=RTCHitN_primID(args->hit,args->N,lane);
        float t=RTCRayN_tfar(args->ray,args->N,lane);
        if(!(t<q.reach) || (q.mode!=0 && !state.either[tri] && dot(state.normals[tri],q.direction)>0)) {
            args->valid[lane]=0;continue;
        }
        if(q.mode==2){
            float delta=std::abs(t-q.target),best=std::abs(context.result.distance-q.target);
            if(context.result.tri<0 || delta<best || (delta==best && (t<context.result.distance || (t==context.result.distance && int(tri)<context.result.tri))))
                context.result={int(tri),t,{1-RTCHitN_u(args->hit,args->N,lane)-RTCHitN_v(args->hit,args->N,lane),
                    RTCHitN_u(args->hit,args->N,lane),RTCHitN_v(args->hit,args->N,lane)},q.origin+t*q.direction};
            // Collect all qualifying intersections for target-relative selection.
            args->valid[lane]=0;
        }
    }
}
static bool pointQuery(RTCPointQueryFunctionArguments* args)
{
    auto& context=*static_cast<PointContext*>(args->userPtr);
    const auto& state=*context.state;const auto& q=*context.query;unsigned tri=args->primID;
    if(q.mode==4){float d=dot(state.normals[tri],q.direction);if(state.either[tri])d=std::abs(d);if(d<q.dotMin)return false;}
    Vec bary;Vec p=closest(q.origin,state.vertices[state.indices[tri*3]],state.vertices[state.indices[tri*3+1]],state.vertices[state.indices[tri*3+2]],bary);
    float distance=dot(p-q.origin,p-q.origin);
    if(!(distance<context.result.distance))return false;
    context.result={int(tri),distance,bary,p};
    args->query->radius=std::min(args->query->radius,std::nextafter(std::sqrt(distance),std::numeric_limits<float>::infinity()));
    return true;
}
static void broadBounds(const RTCBoundsFunctionArguments* args)
{
    const auto& s=*static_cast<const State*>(args->geometryUserPtr);int face=int(args->primID);
    Vec a=s.vertices[s.indices[face*3]],b=s.vertices[s.indices[face*3+1]],c=s.vertices[s.indices[face*3+2]];
    auto& box=*args->bounds_o;float pad=s.broadPadding;
    box.lower_x=std::min(a.x,std::min(b.x,c.x))-pad;box.lower_y=std::min(a.y,std::min(b.y,c.y))-pad;box.lower_z=std::min(a.z,std::min(b.z,c.z))-pad;
    box.upper_x=std::max(a.x,std::max(b.x,c.x))+pad;box.upper_y=std::max(a.y,std::max(b.y,c.y))+pad;box.upper_z=std::max(a.z,std::max(b.z,c.z))+pad;
}
struct Candidates {const State* state;const Query* query;Vec center;double radiusSquared;int* output;int count=0,capacity;};
static bool collectCandidates(RTCPointQueryFunctionArguments* args)
{
    auto& candidates=*static_cast<Candidates*>(args->userPtr);
    const auto& s=*candidates.state;const auto& q=*candidates.query;int face=int(args->primID);
    Vec a=s.vertices[s.indices[face*3]],b=s.vertices[s.indices[face*3+1]],c=s.vertices[s.indices[face*3+2]];
    double pad=s.broadPadding;
    double dx=std::max(0.0,std::max(double(std::min(a.x,std::min(b.x,c.x)))-pad-candidates.center.x,double(candidates.center.x)-std::max(a.x,std::max(b.x,c.x))-pad));
    double dy=std::max(0.0,std::max(double(std::min(a.y,std::min(b.y,c.y)))-pad-candidates.center.y,double(candidates.center.y)-std::max(a.y,std::max(b.y,c.y))-pad));
    double dz=std::max(0.0,std::max(double(std::min(a.z,std::min(b.z,c.z)))-pad-candidates.center.z,double(candidates.center.z)-std::max(a.z,std::max(b.z,c.z))-pad));
    if(dx*dx+dy*dy+dz*dz>candidates.radiusSquared)return false;
    // Exclude only normals separated from the managed threshold by a generous
    // arithmetic margin. Borderline eligibility stays for exact C# evaluation.
    if(q.mode==1 || q.mode==2 || q.mode==4){Vec n=s.normals[face];float d=dot(n,q.direction);
        float error=4e-6f*std::max(1.0f,std::abs(n.x*q.direction.x)+std::abs(n.y*q.direction.y)+std::abs(n.z*q.direction.z));
        if(q.mode==4){if(s.either[face])d=std::abs(d);if(d<q.dotMin-error)return false;}
        else if(!s.either[face] && d>error)return false;
    }
    if(candidates.count<=candidates.capacity){
        if(candidates.count<candidates.capacity)candidates.output[candidates.count]=int(args->primID);
        ++candidates.count;
    }
    return false;
}
static void* create(const Vec* vertices,int vertexCount,
    const int* indices,int indexCount,const Vec* normals,const uint8_t* either)
{
    if(!vertices || !indices || !normals || !either || vertexCount<=0 || indexCount<=0 || indexCount%3)return nullptr;
    for(int i=0;i<vertexCount;++i) if(!std::isfinite(vertices[i].x) || !std::isfinite(vertices[i].y) || !std::isfinite(vertices[i].z)) return nullptr;
    for(int i=0;i<indexCount;++i) if(indices[i]<0 || indices[i]>=vertexCount) return nullptr;
    auto owner=std::make_unique<State>();auto* s=owner.get();
    s->vertices.assign(vertices,vertices+vertexCount);s->indices.assign(indices,indices+indexCount);
    s->normals.assign(normals,normals+indexCount/3);s->either.assign(either,either+indexCount/3);
    s->device=rtcNewDevice("threads=8");if(!s->device)return nullptr;
    float magnitude=1;for(auto v:s->vertices)magnitude=std::max(magnitude,std::max(std::abs(v.x),std::max(std::abs(v.y),std::abs(v.z))));
    s->broadPadding=magnitude*1e-5f;
    s->broadScene=rtcNewScene(s->device);if(!s->broadScene)return nullptr;
    auto broad=rtcNewGeometry(s->device,RTC_GEOMETRY_TYPE_USER);if(!broad)return nullptr;
    rtcSetGeometryUserPrimitiveCount(broad,indexCount/3);rtcSetGeometryUserData(broad,s);rtcSetGeometryBoundsFunction(broad,broadBounds,s);
    rtcCommitGeometry(broad);rtcAttachGeometry(s->broadScene,broad);rtcReleaseGeometry(broad);rtcCommitScene(s->broadScene);
    s->scene=rtcNewScene(s->device);if(!s->scene)return nullptr;rtcSetSceneFlags(s->scene,RTC_SCENE_FLAG_ROBUST);
    auto geometry=rtcNewGeometry(s->device,RTC_GEOMETRY_TYPE_TRIANGLE);if(!geometry)return nullptr;
    auto* v=static_cast<Vec*>(rtcSetNewGeometryBuffer(geometry,RTC_BUFFER_TYPE_VERTEX,0,RTC_FORMAT_FLOAT3,sizeof(Vec),vertexCount));
    if(!v){rtcReleaseGeometry(geometry);return nullptr;}std::copy(s->vertices.begin(),s->vertices.end(),v);
    auto* i=static_cast<int*>(rtcSetNewGeometryBuffer(geometry,RTC_BUFFER_TYPE_INDEX,0,RTC_FORMAT_UINT3,3*sizeof(int),indexCount/3));
    if(!i){rtcReleaseGeometry(geometry);return nullptr;}std::copy(s->indices.begin(),s->indices.end(),i);
    rtcSetGeometryUserData(geometry,s);rtcSetGeometryIntersectFilterFunction(geometry,filter);
    rtcCommitGeometry(geometry);rtcAttachGeometry(s->scene,geometry);rtcReleaseGeometry(geometry);rtcCommitScene(s->scene);
    if(rtcGetDeviceError(s->device)!=RTC_ERROR_NONE)return nullptr;
    return owner.release();
}
extern "C" MESHLAB_QUERY_API void* meshLabEmbreeCreate(const Vec* vertices,int vertexCount,
    const int* indices,int indexCount,const Vec* normals,const uint8_t* either)
{
    try {return create(vertices,vertexCount,indices,indexCount,normals,either);}catch(...){return nullptr;}
}
extern "C" MESHLAB_QUERY_API void meshLabEmbreeDestroy(void* handle)
{
    delete static_cast<State*>(handle);
}
static Result query(const State& s,const Query& q)
{
    if(!(q.reach>0) || !std::isfinite(q.reach))return {-1,q.mode>=3?q.reach*q.reach:q.reach,{},{}};
    if(q.mode>=3){
        RTCPointQuery point{};point.x=q.origin.x;point.y=q.origin.y;point.z=q.origin.z;point.radius=q.reach;point.time=0;
        RTCPointQueryContext rtc;rtcInitPointQueryContext(&rtc);
        PointContext context{&s,&q,{-1,q.reach*q.reach,{},{}}};rtcPointQuery(s.scene,&point,&rtc,pointQuery,&context);return context.result;
    }
    RTCRayHit ray{};ray.ray.org_x=q.origin.x;ray.ray.org_y=q.origin.y;ray.ray.org_z=q.origin.z;
    ray.ray.dir_x=q.direction.x;ray.ray.dir_y=q.direction.y;ray.ray.dir_z=q.direction.z;
    ray.ray.tnear=0;ray.ray.tfar=q.reach;ray.ray.mask=UINT32_MAX;ray.hit.geomID=RTC_INVALID_GEOMETRY_ID;
    RayContext context{};rtcInitRayQueryContext(&context.rtc);context.query=&q;context.result={-1,q.reach,{},{}};
    RTCIntersectArguments args;rtcInitIntersectArguments(&args);args.context=&context.rtc;rtcIntersect1(s.scene,&ray,&args);
    if(q.mode==2)return context.result;
    if(ray.hit.geomID==RTC_INVALID_GEOMETRY_ID)return {-1,q.reach,{},{}};
    return {int(ray.hit.primID),ray.ray.tfar,{1-ray.hit.u-ray.hit.v,ray.hit.u,ray.hit.v},q.origin+ray.ray.tfar*q.direction};
}
template<class Run> static int batch(Run run,int threads)
{
    std::vector<std::thread> workers;std::atomic<bool> failed{false};
    auto safe=[&](int worker){try{run(worker);}catch(...){failed=true;}};
    try {workers.reserve(threads-1);for(int worker=1;worker<threads;++worker)workers.emplace_back(safe,worker);}
    catch(...) {for(auto& worker:workers)worker.join();return 2;}
    safe(0);for(auto& worker:workers)worker.join();return failed?2:0;
}
extern "C" MESHLAB_QUERY_API int meshLabEmbreeQueries(void* handle,const Query* queries,Result* results,int count,int threads)
{
    if(!handle || !queries || !results || count<0 || count>4096 || threads<1 || threads>8)return 1;
    const auto& s=*static_cast<State*>(handle);
    auto run=[&](int worker){for(int i=worker;i<count;i+=threads)results[i]=query(s,queries[i]);};
    return batch(run,threads);
}

extern "C" MESHLAB_QUERY_API int meshLabEmbreeCandidates(void* handle,const Query* queries,int* candidates,int* counts,int count,int capacity,int threads)
{
    if(!handle || !queries || !candidates || !counts || count<0 || count>4096 || capacity<=0 || capacity>256 || threads<1 || threads>8)return 1;
    const auto& s=*static_cast<State*>(handle);
    auto run=[&](int worker){for(int i=worker;i<count;i+=threads){const auto& q=queries[i];Vec center=q.origin;
        float radius=q.reach+s.broadPadding;
        if(q.mode<3){center=q.origin+(q.reach*.5f)*q.direction;radius=q.reach*.5f*std::sqrt(dot(q.direction,q.direction))+s.broadPadding;}
        if(!(q.reach>0) || !std::isfinite(radius) || !std::isfinite(center.x) || !std::isfinite(center.y) || !std::isfinite(center.z)) {counts[i]=-1;continue;}
        RTCPointQuery point{};point.x=center.x;point.y=center.y;point.z=center.z;
        point.radius=std::nextafter(radius,std::numeric_limits<float>::infinity());point.time=0;
        RTCPointQueryContext context;rtcInitPointQueryContext(&context);Candidates output{&s,&q,center,double(point.radius)*point.radius,candidates+size_t(i)*capacity,0,capacity};
        rtcPointQuery(s.broadScene,&point,&context,collectCandidates,&output);
        counts[i]=output.count>capacity?-1:output.count;
    }};
    return batch(run,threads);
}

extern "C" MESHLAB_QUERY_API int meshLabEmbreeVersion() { return 1; }
