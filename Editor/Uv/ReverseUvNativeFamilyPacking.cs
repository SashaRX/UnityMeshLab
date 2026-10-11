using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Connected UV footprints make one native packing item
    /// per ancestry family. Only their common rigid transform is applied to the
    /// actual triangles; xatlas never unwraps or rescales individual descendants.</summary>
    internal static class ReverseUvNativeFamilyPacking
    {
        internal static Vector2[][] TryPack(Vector2[][] source,int side,ReverseUvTransfer.Options options,
            CancellationToken token,Func<int,Vector2[],int,bool> validate,out float scale,out int packedSide,bool allowLargerCanvas=false)
        {
            var direct=TryPackCore(source,side,options,token,validate,out scale,out packedSide,allowLargerCanvas);
            if(direct!=null) return direct;
            var fragile=Enumerable.Range(0,source.Length).Where(g=>ReverseUvFamilyPacking.MinimumAltitude(source[g])<.001).ToArray();
            var regular=Enumerable.Range(0,source.Length).Except(fragile).ToArray();
            if(fragile.Length==0 || regular.Length==0)
                return null;
            // Sub-texel altitudes need the finer Float32 spacing near an atlas
            // edge. Pack those few complete families in a header first; native
            // area ordering would otherwise put them in the canvas interior.
            var native=TryPackCore(regular.Select(g=>source[g]).ToArray(),side,options,token,null,out scale,out packedSide,allowLargerCanvas);
            if(native==null) return null;
            float commonScale=scale;
            var small=fragile.Select(g=>source[g]).ToArray();
            int nativeCanvas=packedSide;
            Vector2[][] header;
            try
            {
                header=ReverseUvFamilyPacking.Pack(small,new ReverseUvTransfer.Options {padding=options.padding,
                    maxAtlasSize=allowLargerCanvas?options.maxAtlasSize:side,rotateCharts=options.rotateCharts},token,
                    out _,out _,(g,p,s)=>validate==null || validate(fragile[g],p,Math.Max(nativeCanvas,s)),initialScale:commonScale);
            }
            catch(InvalidOperationException error) {return Refused($"precision header ({fragile.Length} families): {error.Message}");}
            double shift=header.SelectMany(p=>p).Max(p=>p.y)+options.padding*2-native.SelectMany(p=>p).Min(p=>p.y);
            var output=new Vector2[source.Length][];
            for(int g=0;g<header.Length;++g) output[fragile[g]]=header[g];
            double required=Math.Max(header.SelectMany(p=>p).Max(p=>p.x),
                Math.Max(native.SelectMany(p=>p).Max(p=>p.x),native.SelectMany(p=>p).Max(p=>p.y)+shift))+options.padding;
            int canvas=Math.Max(side,Mathf.NextPowerOfTwo((int)Math.Ceiling(required)));
            if(canvas>(allowLargerCanvas?options.maxAtlasSize:side)) return Refused("precision header canvas");
            for(int i=0;i<regular.Length;++i)
            {
                token.ThrowIfCancellationRequested();int g=regular[i];
                var min=source[g].Aggregate(Vector2.Min);
                var targetMin=native[i].Aggregate(Vector2.Min);
                int edge=Enumerable.Range(1,source[g].Length-1).OrderByDescending(k=>(source[g][k]-source[g][0]).sqrMagnitude).First();
                int turn=Enumerable.Range(0,4).OrderBy(t=>(Transform(source[g][edge]-source[g][0],t,commonScale,Vector2.zero)
                    -(native[i][edge]-native[i][0])).sqrMagnitude).First();
                var local=source[g].Select(p=>Transform(p-min,turn,1,Vector2.zero)).ToArray();
                var localMin=local.Aggregate(Vector2.Min);
                Vector2[] Place(double dx,double dy)=>local.Select(p=>new Vector2(
                    (float)(targetMin.x+((double)p.x-localMin.x)*commonScale+dx),
                    (float)(targetMin.y+shift+((double)p.y-localMin.y)*commonScale+dy))).ToArray();
                bool Fits(Vector2[] p)=>validate==null || validate(g,p,canvas);
                output[g]=Place(0,0);if(Fits(output[g])) continue;
                double magnitude=output[g].Max(p=>Math.Max(Math.Abs(p.x),Math.Abs(p.y)));
                double spacing=Math.Pow(2,Math.Floor(Math.Log(Math.Max(1,magnitude),2))-23);bool rescued=false;
                for(int x=0;x<8 && !rescued;++x) for(int y=0;y<8;++y)
                {
                    token.ThrowIfCancellationRequested();var candidate=Place(x*spacing/8,y*spacing/8);
                    if(!Fits(candidate)) continue;output[g]=candidate;rescued=true;break;
                }
                if(!rescued) return Refused($"family {g} after precision header");
            }
            if(output.Any(p=>p.Any(v=>v.x<0 || v.y<0 || v.x>canvas || v.y>canvas))) return Refused("header output bounds");
            packedSide=canvas;return output;
        }

        static Vector2[][] TryPackCore(Vector2[][] source,int side,ReverseUvTransfer.Options options,
            CancellationToken token,Func<int,Vector2[],int,bool> validate,out float scale,out int packedSide,bool allowLargerCanvas)
        {
            scale=1;packedSide=side;
            if(!XatlasRepack.TryAcquireNativeSession()) return null;
            bool created=false;
            try
            {
                token.ThrowIfCancellationRequested();
                var hulls=source.Select(p=>Footprint(p,token)).ToArray();
                if(hulls.Any(h=>h.Length<3)) return Refused("collapsed family envelope");
                var points=new List<Vector2>();var indices=new List<uint>();var materials=new List<uint>();
                var starts=new int[source.Length];
                for(int g=0;g<hulls.Length;++g)
                {
                    token.ThrowIfCancellationRequested();starts[g]=points.Count;
                    // xatlas's native "rotation" swaps U/V (a reflection).
                    // Choose proper quarter turns in the input instead, so
                    // winding and the ancestry motion contract are preserved.
                    var oriented=options.rotateCharts && g%2==1
                        ?hulls[g].Select(p=>new Vector2(-p.y,p.x)).ToArray():hulls[g];
                    var min=oriented.Aggregate(Vector2.Min);points.AddRange(oriented.Select(p=>p-min));
                    for(int k=0;k<hulls[g].Length;k+=3)
                    {indices.Add((uint)(starts[g]+k));indices.Add((uint)(starts[g]+k+1));indices.Add((uint)(starts[g]+k+2));materials.Add((uint)g);}
                }
                XatlasNative.xatlasCreate();
                created=true;
                var flat=points.SelectMany(p=>new[] {p.x,p.y}).ToArray();
                if(XatlasNative.xatlasAddUvMesh(flat,(uint)points.Count,indices.ToArray(),(uint)indices.Count,
                    materials.ToArray(),(uint)materials.Count)!=0) return Refused("native input");
                XatlasNative.xatlasComputeCharts();
                // Resolution chooses one common density. PreserveShape disables
                // native per-axis extent rounding, which would stretch charts.
                XatlasNative.xatlasPackChartsPreserveShape(0,(uint)options.padding,0,(uint)(side-options.padding*2),
                    1,0,0,0,0);
                token.ThrowIfCancellationRequested();
                uint width=XatlasNative.xatlasGetAtlasWidth(),height=XatlasNative.xatlasGetAtlasHeight();
                int canvas=Math.Max(side,Mathf.NextPowerOfTwo((int)Math.Max(width,height)));
                if(XatlasNative.xatlasGetChartCount()!=source.Length || canvas>(allowLargerCanvas?options.maxAtlasSize:side))
                    return Refused($"canvas {width}x{height}, charts {XatlasNative.xatlasGetChartCount()}/{source.Length}");
                int count=XatlasNative.xatlasGetOutputVertexCount(0);
                var xrefs=new uint[count];var packed=new float[count*2];var charts=new uint[count];
                if(XatlasNative.xatlasGetOutputVertexData(0,xrefs,packed,charts,count)!=count) return Refused("incomplete native output");
                var mapped=new Vector2[points.Count];var seen=new bool[points.Count];
                for(int v=0;v<count;++v)
                {
                    if(xrefs[v]>=points.Count || charts[v]==uint.MaxValue) return Refused("unmapped envelope corner");
                    mapped[xrefs[v]]=new Vector2(packed[v*2]*width,packed[v*2+1]*height);seen[xrefs[v]]=true;
                }
                if(seen.Any(v=>!v)) return Refused("missing envelope corner");
                var envelope=new RemeshNative.Geometry {uv=mapped.Select(p=>p/canvas).ToArray(),
                    indices=indices.Select(i=>(int)i).ToArray(),charts=new int[points.Count]};
                var scan=UvAtlasDiagnostics.Measure(envelope,token,comparisonBudget:options.comparisonBudget,collectConflicts:true);
                // Ancestors and their descendants intentionally share pixels in
                // the packing proxy. Different families must remain disjoint.
                if(!scan.complete || scan.conflicts.Any(p=>materials[p.a]!=materials[p.b]) || scan.invalidFaces>0)
                    return Refused($"envelope audit: overlap {scan.pairs}, invalid {scan.invalidFaces}, collapsed {scan.degenerateFaces}, complete {scan.complete}");
                // Use a long, well-conditioned edge to recover the shared scale.
                int reference=Enumerable.Range(0,hulls.Length).OrderByDescending(g=>hulls[g].Max(p=>p.sqrMagnitude)).First();
                int edge=Enumerable.Range(1,hulls[reference].Length-1).OrderByDescending(k=>hulls[reference][k].sqrMagnitude).First();
                var originalEdge=hulls[reference][edge]-hulls[reference][0];
                var movedEdge=mapped[starts[reference]+edge]-mapped[starts[reference]];
                double density=Math.Sqrt(((double)movedEdge.x*movedEdge.x+(double)movedEdge.y*movedEdge.y)
                    /((double)originalEdge.x*originalEdge.x+(double)originalEdge.y*originalEdge.y));
                if(double.IsNaN(density) || double.IsInfinity(density) || density<=0) return Refused("invalid common scale");
                var output=new Vector2[source.Length][];
                for(int g=0;g<source.Length;++g)
                {
                    token.ThrowIfCancellationRequested();int first=starts[g];
                    var origin=source[g].Aggregate(Vector2.Min);
                    int turn=Enumerable.Range(0,options.rotateCharts?4:1).OrderBy(t=>Enumerable.Range(0,hulls[g].Length)
                        .Sum(k=>(Transform(hulls[g][k]-hulls[g][0],t,density,mapped[first])-mapped[first+k]).sqrMagnitude)).First();
                    if(Enumerable.Range(0,hulls[g].Length).Any(k=>(Transform(hulls[g][k]-hulls[g][0],turn,density,mapped[first])
                        -mapped[first+k]).sqrMagnitude>1e-5)) return Refused($"family {g} changes shape");
                    Vector2[] Place(double x,double y)=>source[g].Select(p=>TransformFrom(p,origin,hulls[g][0],turn,density,mapped[first],x,y)).ToArray();
                    bool Fits(Vector2[] p)=>p.All(v=>v.x>=0 && v.y>=0 && v.x<=canvas && v.y<=canvas)
                        && (validate==null || validate(g,p,canvas));
                    output[g]=Place(0,0);
                    if(Fits(output[g])) continue;
                    // Evaluate sub-ULP phases before rounding, rather than add
                    // an already rounded Float32 offset. This is one common
                    // translation of the complete family, below 0.01 texel,
                    // inside the native padded/bilinear envelope reservation.
                    double magnitude=output[g].Max(p=>Math.Max(Math.Abs(p.x),Math.Abs(p.y)));
                    double spacing=Math.Pow(2,Math.Floor(Math.Log(Math.Max(1,magnitude),2))-23);
                    bool rescued=false;
                    for(int x=0;x<8 && !rescued;++x) for(int y=0;y<8;++y)
                    {
                        token.ThrowIfCancellationRequested();var candidate=Place(x*spacing/8,y*spacing/8);
                        if(!Fits(candidate)) continue;
                        output[g]=candidate;rescued=true;break;
                    }
                    if(!rescued) return Refused($"family {g} metric/contact audit");
                }
                scale=(float)density;packedSide=canvas;return output;
            }
            catch(DllNotFoundException) {return null;}
            catch(EntryPointNotFoundException) {return null;}
            finally {try {if(created) XatlasNative.xatlasDestroy();} finally {XatlasRepack.ReleaseNativeSession();}}
        }

        static Vector2[][] Refused(string reason)
        {
            UvtLog.Info(UvtLog.Category.Repack,"[ReverseUV] Native family packing trial refused: "+reason);
            return null;
        }

        static Vector2 Transform(Vector2 p,int turn,double scale,Vector2 origin,double phaseX=0,double phaseY=0)
        {
            double x=p.x,y=p.y;
            if(turn==1) {x=-p.y;y=p.x;}
            else if(turn==2) {x=-p.x;y=-p.y;}
            else if(turn==3) {x=p.y;y=-p.x;}
            return new Vector2((float)(origin.x+x*scale+phaseX),(float)(origin.y+y*scale+phaseY));
        }

        static Vector2 TransformFrom(Vector2 p,Vector2 sourceMin,Vector2 first,int turn,double scale,Vector2 origin,double phaseX,double phaseY)
        {
            double x=(double)p.x-sourceMin.x-first.x,y=(double)p.y-sourceMin.y-first.y;
            if(turn==1) (x,y)=(-y,x);
            else if(turn==2) (x,y)=(-x,-y);
            else if(turn==3) (x,y)=(y,-x);
            return new Vector2((float)(origin.x+x*scale+phaseX),(float)(origin.y+y*scale+phaseY));
        }

        // Native UV chart discovery joins equal corners. Keep each family's
        // actual concave union, joining disconnected ancestry pieces only with
        // short proxy triangles. These triangles never enter an exported mesh.
        static Vector2[] Footprint(Vector2[] source,CancellationToken token)
        {
            var origin=source.Aggregate(Vector2.Min);
            var points=source.Select(p=>p-origin).ToList();
            var connected=new DisjointSet(source.Length/3);
            var first=new Dictionary<Vector2,int>();
            for(int f=0;f<source.Length/3;++f)
                for(int k=0;k<3;++k)
                {
                    var p=points[f*3+k];
                    if(first.TryGetValue(p,out int other)) connected.Union(f,other);
                    else first.Add(p,f);
                }
            var parts=Enumerable.Range(0,source.Length/3).GroupBy(connected.Find)
                .Select(g=>g.SelectMany(f=>Enumerable.Range(f*3,3)).ToArray()).OrderByDescending(p=>p.Length).ToList();
            var joined=new HashSet<int>(parts[0]);parts.RemoveAt(0);
            long work=0;
            Vector2[] Conservative()
            {
                var hull=Hull(source,.01f);
                return Enumerable.Range(1,Math.Max(0,hull.Length-2)).SelectMany(k=>new[] {hull[0],hull[k],hull[k+1]}).ToArray();
            }
            while(parts.Count>0)
            {
                token.ThrowIfCancellationRequested();
                int selected=0,a=-1,b=-1,c=-1;double best=double.MaxValue;
                for(int p=0;p<parts.Count;++p)
                    foreach(int child in parts[p])
                        foreach(int parent in joined)
                        {
                            if((++work & 1023)==0) token.ThrowIfCancellationRequested();
                            if(work>2000000) return Conservative();
                            double distance=(points[child]-points[parent]).sqrMagnitude;
                            if(distance>=best) continue;
                            int face=parent/3*3;
                            foreach(int adjacent in Enumerable.Range(face,3).Where(i=>i!=parent))
                            {
                                if(Altitude(points[parent],points[adjacent],points[child])<=.00001) continue;
                                best=distance;selected=p;a=parent;b=adjacent;c=child;break;
                            }
                        }
                if(a<0) return Conservative();
                points.Add(points[a]);points.Add(points[b]);points.Add(points[c]);
                foreach(int corner in parts[selected]) joined.Add(corner);
                parts.RemoveAt(selected);
            }
            return points.ToArray();
        }

        static Vector2[] Hull(Vector2[] source,float minimumAltitude)
        {
            var origin=source.Aggregate(Vector2.Min);
            var points=source.Select(p=>p-origin).Distinct().OrderBy(p=>p.x).ThenBy(p=>p.y).ToArray();
            var lower=new List<Vector2>();var upper=new List<Vector2>();
            foreach(var p in points) Add(lower,p);
            foreach(var p in points.Reverse()) Add(upper,p);
            if(lower.Count>0) lower.RemoveAt(lower.Count-1);
            if(upper.Count>0) upper.RemoveAt(upper.Count-1);
            var hull=lower.Concat(upper).ToArray();
            // Almost collinear hull corners can fold when native output is
            // normalized and read back as Float32. Reserve a conservative box
            // for that proxy; the actual family keeps every original triangle.
            if(hull.Length>=3 && Enumerable.Range(1,hull.Length-2).Any(k=>Altitude(hull[0],hull[k],hull[k+1])<minimumAltitude))
            {
                var max=points.Aggregate(Vector2.Max);float width=Math.Max(minimumAltitude,max.x),height=Math.Max(minimumAltitude,max.y);
                return new[] {Vector2.zero,new Vector2(width,0),new Vector2(width,height),new Vector2(0,height)};
            }
            return hull;
            void Add(List<Vector2> chain,Vector2 p)
            {
                while(chain.Count>=2)
                {
                    var a=chain[chain.Count-2];var b=chain[chain.Count-1];
                    if(((double)b.x-a.x)*((double)p.y-a.y)-((double)b.y-a.y)*((double)p.x-a.x)>0) break;
                    chain.RemoveAt(chain.Count-1);
                }
                chain.Add(p);
            }
        }

        static double Altitude(Vector2 a,Vector2 b,Vector2 c)
        {
            double ex=(double)b.x-a.x,ey=(double)b.y-a.y,dx=(double)c.x-a.x,dy=(double)c.y-a.y;
            double longest=Math.Sqrt(Math.Max(ex*ex+ey*ey,Math.Max(dx*dx+dy*dy,(dx-ex)*(dx-ex)+(dy-ey)*(dy-ey))));
            return longest>0?Math.Abs(ex*dy-ey*dx)/longest:0;
        }
    }
}
