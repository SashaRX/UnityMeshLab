#pragma once

// Float32 primitive operations and legacy traversal order are deliberate.
// Embree may provide an upper culling bound, never the chosen face/result.
static float component(Vec v,int axis){return axis==0?v.x:axis==1?v.y:v.z;}
struct LegacyFrame
{
    int x,y,z;float sx,sy,sz;
    explicit LegacyFrame(Vec d){
        Vec a{std::abs(d.x),std::abs(d.y),std::abs(d.z)};
        z=a.x>a.y?(a.x>a.z?0:2):(a.y>a.z?1:2);x=z+1;if(x==3)x=0;y=x+1;if(y==3)y=0;
        if(component(d,z)<0)std::swap(x,y);float dz=component(d,z);if(dz==0)dz=1e-30f;
        sx=component(d,x)/dz;sy=component(d,y)/dz;sz=1/dz;
    }
};
static bool watertight(const LegacyFrame& k,Vec origin,Vec a,Vec b,Vec c,float maxT,float& t,Vec& bary)
{
    a=a-origin;b=b-origin;c=c-origin;
    float az=component(a,k.z),bz=component(b,k.z),cz=component(c,k.z);
    float ax=component(a,k.x)-k.sx*az,ay=component(a,k.y)-k.sy*az;
    float bx=component(b,k.x)-k.sx*bz,by=component(b,k.y)-k.sy*bz;
    float cx=component(c,k.x)-k.sx*cz,cy=component(c,k.y)-k.sy*cz;
    float u=cx*by-cy*bx,v=ax*cy-ay*cx,w=bx*ay-by*ax;
    if(u==0 || v==0 || w==0){u=float(double(cx)*by-double(cy)*bx);v=float(double(ax)*cy-double(ay)*cx);w=float(double(bx)*ay-double(by)*ax);}
    if((u<0 || v<0 || w<0) && (u>0 || v>0 || w>0))return false;
    float det=u+v+w;if(det==0)return false;
    float depth=u*(k.sz*az)+v*(k.sz*bz)+w*(k.sz*cz);
    float sign=det<0?-1.0f:1.0f,ds=det*sign,ts=depth*sign;
    if(ts<0 || ts>=maxT*ds)return false;
    float inverse=1/det;t=depth*inverse;bary={u*inverse,v*inverse,w*inverse};return true;
}
static bool rayBox(const LegacyNode& n,const Query& q,float maxT,float& enter)
{
    float low=0,high=maxT;enter=0;
    for(int axis=0;axis<3;++axis){float o=component(q.origin,axis),d=component(q.direction,axis),lo=component(n.min,axis),hi=component(n.max,axis);
        if(std::abs(d)<1e-8f){if(o<lo || o>hi)return false;}
        else {float inverse=1/d,a=(lo-o)*inverse,b=(hi-o)*inverse;if(a>b)std::swap(a,b);
            low=std::max(low,a);high=std::min(high,b);if(low>high)return false;}}
    enter=low;return true;
}
static bool targetBox(const LegacyNode& n,const Query& q,float& distance)
{
    float low=0,high=q.reach;
    for(int axis=0;axis<3;++axis){float o=component(q.origin,axis),d=component(q.direction,axis),lo=component(n.min,axis),hi=component(n.max,axis);
        if(d==0){if(o<lo || o>hi)return false;}
        else {float a=(lo-o)/d,b=(hi-o)/d;low=std::max(low,std::min(a,b));high=std::min(high,std::max(a,b));if(low>high)return false;}}
    distance=std::max(0.0f,std::max(low-q.target,q.target-high));return true;
}
static float boxDistance(const LegacyNode& n,Vec p)
{
    float x=std::max(0.0f,std::max(n.min.x-p.x,p.x-n.max.x));
    float y=std::max(0.0f,std::max(n.min.y-p.y,p.y-n.max.y));
    float z=std::max(0.0f,std::max(n.min.z-p.z,p.z-n.max.z));return x*x+y*y+z*z;
}
struct LegacyQuery
{
    const State& s;const Query& q;LegacyFrame frame;Result best;float upper,targetDistance;
    LegacyQuery(const State& state,const Query& query):s(state),q(query),frame(q.direction),
        best{-1,q.mode>=3?q.reach*q.reach:q.reach,{},{}},upper(std::numeric_limits<float>::infinity()),
        targetDistance(std::max(q.target,q.reach-q.target)){}
    bool eligible(int face)const {
        if(q.mode==0 || q.mode==3)return true;
        float d=dot(s.normals[face],q.direction);
        if(q.mode==4){if(s.either[face])d=std::abs(d);return !(d<q.dotMin);}
        return !(d>0 && !s.either[face]);
    }
    bool primitive(int face,float maxT,Result& result)const {
        if(!eligible(face))return false;
        Vec a=s.vertices[s.indices[face*3]],b=s.vertices[s.indices[face*3+1]],c=s.vertices[s.indices[face*3+2]];
        if(q.mode>=3){Vec bary;Vec p=closest(q.origin,a,b,c,bary);float d=dot(p-q.origin,p-q.origin);result={face,d,bary,p};return d<q.reach*q.reach;}
        float t;Vec bary;if(!watertight(frame,q.origin,a,b,c,maxT,t,bary))return false;
        result={face,t,bary,q.origin+t*q.direction};return true;
    }
    void seed(const Result& proposal){
        if(proposal.tri<0)return;
        Result exact;if(!primitive(proposal.tri,q.reach,exact) || !std::isfinite(exact.distance))return;
        // A primitive that the legacy tree itself cannot reach must not prune it.
        for(int node=s.leaves[proposal.tri];node>=0;node=s.parents[node]){
            float bound;
            if(q.mode>=3){if(boxDistance(s.nodes[node],q.origin)>=q.reach*q.reach)return;}
            else if(q.mode==2){if(!targetBox(s.nodes[node],q,bound) || bound>targetDistance)return;}
            else if(!rayBox(s.nodes[node],q,q.reach,bound))return;
        }
        float value=q.mode==2?std::abs(exact.distance-q.target):exact.distance;
        // Culling has a conservative margin; this never changes primitive or
        // tie tolerances. No proposal face is installed as the initial winner.
        upper=std::nextafter(value+std::max(1e-7f,std::abs(value)*1e-5f),std::numeric_limits<float>::infinity());
    }
    void visit(int index){
        const auto& n=s.nodes[index];float bound;
        if(q.mode>=3){float d=boxDistance(n,q.origin);if(d>=best.distance || d>upper)return;}
        else if(q.mode==2){if(!targetBox(n,q,bound) || bound>targetDistance || bound>upper)return;}
        else {if(!rayBox(n,q,best.distance,bound) || bound>upper)return;}
        if(n.left<0){
            for(int i=n.start;i<n.start+n.count;++i){Result hit;
                if(!primitive(s.order[i],q.mode==2?q.reach:best.distance,hit))continue;
                if(q.mode>=3){if(hit.distance<best.distance)best=hit;}
                else if(q.mode==2){float d=std::abs(hit.distance-q.target);
                    if(d>targetDistance || (d==targetDistance && best.tri>=0 && (hit.distance>best.distance || (hit.distance==best.distance && hit.tri>=best.tri))))continue;
                    targetDistance=d;best=hit;
                }else best=hit;
            }return;
        }
        const auto& left=s.nodes[n.left];const auto& right=s.nodes[n.right];
        if(q.mode>=3){float l=boxDistance(left,q.origin),r=boxDistance(right,q.origin);
            if(l<r){visit(n.left);visit(n.right);}else{visit(n.right);visit(n.left);}return;}
        float l,r;bool hitL=q.mode==2?targetBox(left,q,l):rayBox(left,q,best.distance,l);
        bool hitR=q.mode==2?targetBox(right,q,r):rayBox(right,q,best.distance,r);
        int first=n.left,second=n.right;
        if(q.mode==2){if((!hitL && hitR) || (hitL && hitR && r<l)){first=n.right;second=n.left;}}
        else {if(hitL && hitR){if(l>r){first=n.right;second=n.left;}}
            else if(hitL){second=-1;}else if(hitR){first=n.right;second=-1;}else{first=second=-1;}}
        if(q.mode==2){if(hitL || hitR)visit(first);if(hitL && hitR)visit(second);}
        else {if(first>=0)visit(first);if(second>=0)visit(second);}
    }
};
static Result legacyQuery(const State& s,Query q,const Result& proposal)
{
    if(q.mode==2){if(!(q.target>0))q.mode=1;else q.target=std::min(q.target,q.reach);}
    LegacyQuery context(s,q);context.seed(proposal);context.visit(0);return context.best;
}
