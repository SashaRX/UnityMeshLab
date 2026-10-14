using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Connected UV footprints make one native packing item
    /// per ancestry family. Xatlas supplies one common rigid transform; the
    /// caller audits Float32 precision without unwrapping individual descendants.</summary>
    internal static class ReverseUvNativeFamilyPacking
    {
        /// <summary>Immutable proxy masks reused only inside one final pack.
        /// Density and orientation trials read these masks without modifying them.</summary>
        internal sealed class FootprintCache : IDisposable
        {
            const int MaxRetainedCorners = 1000000;
            readonly Dictionary<(Vector2[] source, bool simplify, bool concave), Vector2[]> entries = new Dictionary<(Vector2[], bool, bool), Vector2[]>();
            readonly bool enabled;
            readonly TransferPerformanceProfile performance;
            bool disposed;
            int retainedCorners;
            internal long hits, misses, constructionTicks;

            internal FootprintCache(bool enabled, TransferPerformanceProfile performance = null)
            { this.enabled = enabled; this.performance = performance; }

            internal Vector2[] Get(Vector2[] source, CancellationToken token, bool simplify, bool concave)
            {
                token.ThrowIfCancellationRequested();
                var key = (source, simplify, concave);
                if (enabled && entries.TryGetValue(key, out var cached)) { ++hits; return cached; }
                long started = performance == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
                var footprint = Footprint(source, token, simplify, concave);
                if (performance != null) constructionTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                ++misses;
                if (enabled && source.Length + (long)footprint.Length <= MaxRetainedCorners - retainedCorners)
                {
                    entries.Add(key, footprint);
                    retainedCorners += source.Length + footprint.Length;
                }
                return footprint;
            }

            public void Dispose()
            {
                if (disposed) return;
                performance?.Record("Packing footprint construction", constructionTicks, misses);
                performance?.Record("Packing footprint cache hits", 0, hits);
                entries.Clear();
                disposed = true;
            }
        }

        internal static Vector2[][] TryPack(Vector2[][] source,int side,ReverseUvTransfer.Options options,
            CancellationToken token,Func<int,Vector2[],int,bool> validate,out float scale,out int packedSide,bool allowLargerCanvas=false,float? fixedScale=null,Func<Vector2[][],int,HashSet<int>> validateAtlas=null,FootprintCache footprints=null)
        {
            using var packTiming = options.performance?.Measure("Native family packing trial");
            var result=TryPackAttempt(source,side,options,token,validate,out scale,out packedSide,
                allowLargerCanvas,fixedScale,validateAtlas,true,footprints);
            if(options.frozenShellCascade && !options.preserveProjectedOverlap && options.rotateChartsToAxis)
            {
                if(result==null) UvtLog.Info(UvtLog.Category.Repack,"[ReverseUV] Axis-aligned family trial refused; retry the same whole families with 90-degree placement rotations.");
                var orthogonal=TryPackAttempt(source,side,options,token,validate,out float orthogonalScale,out int orthogonalSide,
                    allowLargerCanvas,fixedScale,validateAtlas,false,footprints);
                double Cost(Vector2[][] uv,float density)
                {
                    var points=uv.SelectMany(p=>p).ToArray();var lo=points.Aggregate(Vector2.Min);var hi=points.Aggregate(Vector2.Max);
                    return ((double)hi.x-lo.x)*(hi.y-lo.y)/(density*(double)density);
                }
                if(orthogonal!=null && (result==null || Cost(orthogonal,orthogonalScale)<Cost(result,scale)))
                {result=orthogonal;scale=orthogonalScale;packedSide=orthogonalSide;}
            }
            return result;
        }

        static Vector2[][] TryPackAttempt(Vector2[][] source,int side,ReverseUvTransfer.Options options,
            CancellationToken token,Func<int,Vector2[],int,bool> validate,out float scale,out int packedSide,
            bool allowLargerCanvas,float? fixedScale,Func<Vector2[][],int,HashSet<int>> validateAtlas,bool alignFootprints,FootprintCache footprints)
        {
            scale=1;packedSide=side;
            var precision=new HashSet<int>();
            for(int attempt=0;attempt<8;++attempt)
            {
                token.ThrowIfCancellationRequested();
                var regular=Enumerable.Range(0,source.Length).Where(g=>!precision.Contains(g)).ToArray();
                if(regular.Length==0) return null;
                var refused=new List<int>();
                bool ValidRegular(int g,Vector2[] p,int canvas)=>validate==null || validate(regular[g],p,canvas);
                var native=TryPackCore(regular.Select(g=>source[g]).ToArray(),side,options,token,ValidRegular,
                    out scale,out packedSide,allowLargerCanvas,refused,fixedScale,alignFootprints:alignFootprints,footprints:footprints);
                if(native==null)
                    native=TryPackCore(regular.Select(g=>source[g]).ToArray(),side,options,token,ValidRegular,
                        out scale,out packedSide,allowLargerCanvas,refused,fixedScale,false,alignFootprints,footprints);
                if(native==null) return null;
                if(refused.Count>0)
                {
                    foreach(int g in refused) precision.Add(regular[g]);
                    continue;
                }
                if(precision.Count==0)
                {
                    var wholeRefusals=validateAtlas?.Invoke(native,packedSide);
                    if(wholeRefusals==null || wholeRefusals.Count==0) return native;
                    precision.UnionWith(wholeRefusals);continue;
                }
                // Only families that actually failed placement need a precision
                // strip. A blanket altitude threshold sent hundreds of otherwise
                // movable islands into a separate square and wasted its interior.
                var fragile=precision.OrderBy(g=>g).ToArray();
                float commonScale=scale;int nativeCanvas=packedSide;
                Vector2[][] header;
                try
                {
                    header=ReverseUvFamilyPacking.Pack(fragile.Select(g=>source[g]).ToArray(),
                        new ReverseUvTransfer.Options {padding=options.padding,
                            maxAtlasSize=allowLargerCanvas?options.maxAtlasSize:side,rotateCharts=options.rotateCharts},token,
                        out _,out _,(g,p,c)=>validate==null || validate(fragile[g],p,Math.Max(nativeCanvas,c)),initialScale:commonScale);
                }
                catch(InvalidOperationException error) {return Refused($"precision strip ({fragile.Length} families): {error.Message}");}
                // Keep precision-sensitive families near the origin, but let
                // classic xatlas fill the spaces between them. A separate full-
                // width header wasted all of those vacancies.
                var anchor=header.SelectMany(p=>p.Select(v=>v/commonScale)).ToList();
                int[] anchorStarts=new int[header.Length];int cursor=0;
                for(int i=0;i<header.Length;++i) {anchorStarts[i]=cursor;cursor+=header[i].Length;}
                float longest=regular.Max(g=>{var lo=source[g].Aggregate(Vector2.Min);var hi=source[g].Aggregate(Vector2.Max);
                    return hi.x-lo.x+hi.y-lo.y;});
                // Native sorts by perimeter. This narrow proxy stroke makes the
                // precision chart the first item, without filling its empty bbox.
                var anchorCorner=anchor[0];Stroke(anchor,anchorCorner,anchorCorner+Vector2.right*(longest+options.padding*4/commonScale+1));
                var anchored=regular.Select(g=>source[g]).Concat(new[] {anchor.ToArray()}).ToArray();
                var anchoredRefusals=new List<int>();
                bool ValidAnchored(int id,Vector2[] uv,int canvas)
                {
                    if(validate==null) return true;
                    if(id<regular.Length) return validate(regular[id],uv,canvas);
                    for(int i=0;i<fragile.Length;++i)
                        if(!validate(fragile[i],uv.Skip(anchorStarts[i]).Take(header[i].Length).ToArray(),canvas)) return false;
                    return true;
                }
                var completed=TryPackCore(anchored,side,options,token,ValidAnchored,out float anchoredScale,out int canvas,
                    allowLargerCanvas,anchoredRefusals,commonScale,alignFootprints:alignFootprints,footprints:footprints);
                if(completed==null)
                    completed=TryPackCore(anchored,side,options,token,ValidAnchored,out anchoredScale,out canvas,
                        allowLargerCanvas,anchoredRefusals,commonScale,false,alignFootprints,footprints);
                if(completed==null) return null;
                if(anchoredRefusals.Count>0)
                {
                    if(anchoredRefusals.Contains(regular.Length)) return Refused("precision anchor placement");
                    foreach(int id in anchoredRefusals) precision.Add(regular[id]);
                    continue;
                }
                var output=new Vector2[source.Length][];
                for(int i=0;i<regular.Length;++i) output[regular[i]]=completed[i];
                for(int i=0;i<fragile.Length;++i)
                    output[fragile[i]]=completed[regular.Length].Skip(anchorStarts[i]).Take(header[i].Length).ToArray();
                var atlasRefusals=validateAtlas?.Invoke(output,canvas);
                if(atlasRefusals!=null && atlasRefusals.Count>0) {precision.UnionWith(atlasRefusals);continue;}
                scale=anchoredScale;
                UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Native family packing retained {precision.Count} precision families; {regular.Length} packed by footprint.");
                packedSide=canvas;return output;
            }
            return Refused($"precision placement did not converge ({precision.Count} families)");
        }

        static Vector2[][] TryPackCore(Vector2[][] source,int side,ReverseUvTransfer.Options options,
            CancellationToken token,Func<int,Vector2[],int,bool> validate,out float scale,out int packedSide,bool allowLargerCanvas,List<int> refusedFamilies=null,float? fixedScale=null,bool simplifyFootprints=true,bool alignFootprints=true,FootprintCache footprints=null)
        {
            scale=1;packedSide=side;
            if(!XatlasRepack.TryAcquireNativeSession()) return null;
            bool created=false;
            try
            {
                token.ThrowIfCancellationRequested();
                RemeshNative.CheckAvailable();
                bool concave = options.frozenShellCascade && !options.preserveProjectedOverlap;
                var hulls=source.Select(p=>footprints == null ? Footprint(p,token,simplifyFootprints,concave)
                    : footprints.Get(p,token,simplifyFootprints,concave)).ToArray();
                if(hulls.Any(h=>h.Length<3)) return Refused("collapsed family envelope");
                var points=new List<Vector2>();var indices=new List<uint>();var materials=new List<uint>();
                var starts=new int[source.Length];
                for(int g=0;g<hulls.Length;++g)
                {
                    token.ThrowIfCancellationRequested();starts[g]=points.Count;
                    // Native packing chooses the same chart orientations as
                    // ordinary Repack. Its U/V swap is an isometry of UV2;
                    // recover it once and move every LOD/overlay together.
                    points.AddRange(hulls[g]);
                    for(int k=0;k<hulls[g].Length;k+=3)
                    {indices.Add((uint)(starts[g]+k));indices.Add((uint)(starts[g]+k+1));indices.Add((uint)(starts[g]+k+2));materials.Add((uint)g);}
                }
                long discoveryStarted = options.performance == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
                XatlasNative.xatlasCreate();
                created=true;
                var flat=points.SelectMany(p=>new[] {p.x,p.y}).ToArray();
                var indexArray=indices.ToArray();var materialArray=materials.ToArray();
                if(XatlasNative.xatlasAddUvMesh(flat,(uint)points.Count,indexArray,(uint)indices.Count,
                    materialArray,(uint)materials.Count)!=0) return Refused("native input");
                XatlasNative.xatlasComputeCharts();
                options.performance?.Record("Native chart discovery", System.Diagnostics.Stopwatch.GetTimestamp() - discoveryStarted);
                // Resolution chooses one common density. PreserveShape disables
                // native per-axis extent rounding, which would stretch charts.
                double area=0;
                foreach(var hull in hulls)
                {
                    for(int f=0;f<hull.Length;f+=3)
                        area+=Math.Abs(((double)hull[f+1].x-hull[f].x)*((double)hull[f+2].y-hull[f].y)
                            -((double)hull[f+1].y-hull[f].y)*((double)hull[f+2].x-hull[f].x))*.5;
                }
                int estimate=fixedScale.HasValue?Mathf.NextPowerOfTwo((int)Math.Ceiling(Math.Sqrt(Math.Max(1,area/.75))*fixedScale.Value)):side;
                bool bruteForce=(long)source.Length*estimate*estimate<=500000000L;
                bool align=alignFootprints && options.frozenShellCascade && !options.preserveProjectedOverlap && options.rotateChartsToAxis;
                using (options.performance?.Measure("Native chart placement"))
                    XatlasNative.xatlasPackChartsPreserveShape(0,(uint)options.padding,fixedScale ?? 0,
                        fixedScale.HasValue?0u:(uint)(side-options.padding*2),
                        1,0,bruteForce?1:0,options.rotateCharts?1:0,align?1:0);
                token.ThrowIfCancellationRequested();
                var replay=options.performance?.CaptureNativePacking(flat,indexArray,materialArray,(uint)options.padding,
                    fixedScale??0,fixedScale.HasValue?0u:(uint)(side-options.padding*2),bruteForce,options.rotateCharts,align);
                token.ThrowIfCancellationRequested();
                uint width=replay?.width??XatlasNative.xatlasGetAtlasWidth(),height=replay?.height??XatlasNative.xatlasGetAtlasHeight();
                uint chartCount=replay?.chartCount??XatlasNative.xatlasGetChartCount();
                int extent=(int)Math.Max(width,height);
                // Ordinary Repack retains native's actual rectangle. A forced
                // power-of-two normalization adds a blank quadrant even after
                // dense packing, and is not a second packing operation.
                int canvas=Math.Max(side,options.frozenShellCascade && !options.preserveProjectedOverlap
                    ?extent:Mathf.NextPowerOfTwo(extent));
                if(chartCount!=source.Length || canvas>(allowLargerCanvas?options.maxAtlasSize:side))
                    return Refused($"canvas {width}x{height}, charts {chartCount}/{source.Length}");
                int count=replay?.references.Length??XatlasNative.xatlasGetOutputVertexCount(0);
                var xrefs=replay?.references??new uint[count];var packed=replay?.uv??new float[count*2];var charts=replay?.charts??new uint[count];
                if(replay==null && XatlasNative.xatlasGetOutputVertexData(0,xrefs,packed,charts,count)!=count) return Refused("incomplete native output");
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
                if(fixedScale.HasValue) density=fixedScale.Value;
                if(double.IsNaN(density) || double.IsInfinity(density) || density<=0) return Refused("invalid common scale");
                var output=new Vector2[source.Length][];
                for(int g=0;g<source.Length;++g)
                {
                    token.ThrowIfCancellationRequested();int first=starts[g];
                    var origin=source[g].Aggregate(Vector2.Min);
                    int turn=Enumerable.Range(0,options.rotateCharts?8:1).OrderBy(t=>Enumerable.Range(0,hulls[g].Length)
                        .Sum(k=>(Transform(hulls[g][k]-hulls[g][0],t,density,mapped[first])-mapped[first+k]).sqrMagnitude)).First();
                    var rotation=align?RecoverRotation(hulls[g],mapped,first,density):default;
                    Vector2 MoveProxy(Vector2 p)=>align?TransformAligned(p,rotation,density,mapped[first],0,0)
                        :Transform(p,turn,density,mapped[first]);
                    double shapeError=Math.Sqrt(Enumerable.Range(0,hulls[g].Length).Max(k=>(MoveProxy(hulls[g][k]-hulls[g][0])
                        -mapped[first+k]).sqrMagnitude));
                    if(shapeError>.01) return Refused($"family {g} changes shape ({shapeError:G6} texels, scale {density:G6}, orientation {turn})");
                    Vector2[] Place(double x,double y)=>source[g].Select(p=>align
                        ?TransformAligned((double)p.x-origin.x-hulls[g][0].x,(double)p.y-origin.y-hulls[g][0].y,
                            rotation,density,mapped[first],x,y)
                        :TransformFrom(p,origin,hulls[g][0],turn,density,mapped[first],x,y)).ToArray();
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
                    if(!rescued)
                    {
                        if(refusedFamilies==null) return Refused($"family {g} metric/contact audit");
                        refusedFamilies.Add(g);
                    }
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
            if(turn>=4) { (x,y)=(y,x); turn-=4; }
            if(turn==1) {(x,y)=(-y,x);}
            else if(turn==2) {(x,y)=(-x,-y);}
            else if(turn==3) {(x,y)=(y,-x);}
            return new Vector2((float)(origin.x+x*scale+phaseX),(float)(origin.y+y*scale+phaseY));
        }

        static Vector2 TransformFrom(Vector2 p,Vector2 sourceMin,Vector2 first,int turn,double scale,Vector2 origin,double phaseX,double phaseY)
        {
            double x=(double)p.x-sourceMin.x-first.x,y=(double)p.y-sourceMin.y-first.y;
            if(turn>=4) { (x,y)=(y,x); turn-=4; }
            if(turn==1) (x,y)=(-y,x);
            else if(turn==2) (x,y)=(-x,-y);
            else if(turn==3) (x,y)=(y,-x);
            return new Vector2((float)(origin.x+x*scale+phaseX),(float)(origin.y+y*scale+phaseY));
        }

        static (double a,double b,int sign) RecoverRotation(Vector2[] source,Vector2[] packed,int first,double scale)
        {
            (double a,double b,int sign) best=default;double bestError=double.MaxValue;
            foreach(int sign in new[] {1,-1})
            {
                double dot=0,cross=0;
                for(int i=1;i<source.Length;++i)
                {
                    double x=(double)source[i].x-source[0].x,y=((double)source[i].y-source[0].y)*sign;
                    double u=(double)packed[first+i].x-packed[first].x,v=(double)packed[first+i].y-packed[first].y;
                    dot+=x*u+y*v;cross+=x*v-y*u;
                }
                double length=Math.Sqrt(dot*dot+cross*cross);if(!(length>0)) continue;
                var trial=(a:dot/length,b:cross/length,sign);
                double error=0;
                for(int i=1;i<source.Length;++i)
                {
                    double x=(double)source[i].x-source[0].x,y=((double)source[i].y-source[0].y)*sign;
                    double u=(double)packed[first+i].x-packed[first].x,v=(double)packed[first+i].y-packed[first].y;
                    double ex=(trial.a*x-trial.b*y)*scale-u,ey=(trial.b*x+trial.a*y)*scale-v;
                    error+=ex*ex+ey*ey;
                }
                if(error<bestError) {bestError=error;best=trial;}
            }
            return best;
        }

        static Vector2 TransformAligned(Vector2 p,(double a,double b,int sign) rotation,double scale,Vector2 origin,double phaseX,double phaseY)
            =>TransformAligned(p.x,p.y,rotation,scale,origin,phaseX,phaseY);

        static Vector2 TransformAligned(double x,double y,(double a,double b,int sign) rotation,double scale,Vector2 origin,double phaseX,double phaseY)
        {
            y*=rotation.sign;
            return new Vector2((float)(origin.x+(rotation.a*x-rotation.b*y)*scale+phaseX),
                (float)(origin.y+(rotation.b*x+rotation.a*y)*scale+phaseY));
        }

        // Native UV chart discovery joins equal corners. Keep each family's
        // actual concave union, joining disconnected ancestry pieces only with
        // short proxy triangles. These triangles never enter an exported mesh.
        internal static Vector2[] Footprint(Vector2[] source,CancellationToken token,bool simplify,bool preserveConcavities=false)
        {
            var origin=source.Aggregate(Vector2.Min);
            var shifted=source.Select(p=>p-origin).ToArray();
            var proxy=simplify?ReverseUvPackingFootprint.Union(shifted,token):shifted;
            var points=new List<Vector2>();
            for(int f=0;f<proxy.Length;f+=3)
            {
                var a=proxy[f];var b=proxy[f+1];var c=proxy[f+2];
                float area=Math.Abs((b.x-a.x)*(c.y-a.y)-(c.x-a.x)*(b.y-a.y))*.5f;
                if(!preserveConcavities || area>1.192092896e-7f) points.AddRange(new[] {a,b,c});
                else
                {
                    // Float32 union shards can fall below native's face-area
                    // threshold. Guard only this subpixel fragment; filling the
                    // complete family's convex hull erases rings and concavities.
                    if((c-a).sqrMagnitude>(b-a).sqrMagnitude) b=c;
                    if((c-proxy[f+1]).sqrMagnitude>(b-a).sqrMagnitude) {a=proxy[f+1];b=c;}
                    Stroke(points,a,b,true);
                }
            }
            // Include guards before connectivity discovery: a union can remove
            // their original corners, so guards also need proxy connections.
            for(int f=0;f<source.Length;f+=3)
            {
                token.ThrowIfCancellationRequested();
                var a=source[f]-origin;var b=source[f+1]-origin;var c=source[f+2]-origin;
                if(Altitude(a,b,c)>=.01) continue;
                if((c-a).sqrMagnitude>(b-a).sqrMagnitude) b=c;
                if((c-(source[f+1]-origin)).sqrMagnitude>(b-a).sqrMagnitude) {a=source[f+1]-origin;b=c;}
                Stroke(points,a,b,preserveConcavities);
            }
            int proxyFaces=points.Count/3;
            var connected=new DisjointSet(proxyFaces);
            var first=new Dictionary<Vector2,int>();
            for(int f=0;f<proxyFaces;++f)
                for(int k=0;k<3;++k)
                {
                    var p=points[f*3+k];
                    if(first.TryGetValue(p,out int other)) connected.Union(f,other);
                    else first.Add(p,f);
                }
            var parts=Enumerable.Range(0,proxyFaces).GroupBy(connected.Find)
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
                var tree=new NearestPoint(points,joined.Distinct().ToArray());
                int selected=0,a=-1,b=-1;double best=double.MaxValue;
                for(int p=0;p<parts.Count;++p)
                    foreach(int child in parts[p].GroupBy(i=>points[i]).Select(g=>g.First()))
                    {
                        if((++work & 1023)==0) token.ThrowIfCancellationRequested();
                        int parent=tree.Find(points[child],out double distance);
                        if(distance>=best) continue;
                        best=distance;selected=p;a=parent;b=child;
                    }
                if(a<0 || b<0) return Conservative();
                // A narrow connection keeps native chart discovery together
                // without filling a concavity with a fan from a long source edge.
                Stroke(points,points[a],points[b],preserveConcavities);
                foreach(int corner in parts[selected]) joined.Add(corner);
                parts.RemoveAt(selected);
            }
            // Validate the exact Float32 mask before passing it to native.
            // A discarded/rounded connector cannot define family connectivity.
            // Reserve a conservative envelope for this family only; original
            // UV corners and all LOD triangles still use one rigid transform.
            var audit=new DisjointSet(points.Count/3);
            var aliases=new Dictionary<Vector2,int>();
            for(int f=0;f<points.Count/3;++f)
            {
                var a=points[f*3];var b=points[f*3+1];var c=points[f*3+2];
                float area=Math.Abs((b.x-a.x)*(c.y-a.y)-(c.x-a.x)*(b.y-a.y))*.5f;
                if(!(area>1.192092896e-7f)) return Conservative();
                for(int k=0;k<3;++k)
                {
                    var point=points[f*3+k];
                    if(aliases.TryGetValue(point,out int other)) audit.Union(f,other);
                    else aliases.Add(point,f);
                }
            }
            if(Enumerable.Range(0,points.Count/3).Select(audit.Find).Distinct().Count()!=1) return Conservative();
            return points.ToArray();
        }

        static void Stroke(List<Vector2> points,Vector2 a,Vector2 b,bool preserveConcavities=false)
        {
            var edge=b-a;float length=edge.magnitude;
            if(preserveConcavities && length<.0001f)
            {
                // A microscopic connecting edge cannot form a native-valid
                // ribbon. Both endpoints share a small, well-conditioned guard.
                var u=a+Vector2.right*.01f;var v=a+Vector2.up*.01f;
                points.AddRange(new[] {a,u,v,b,u,v});return;
            }
            if(length<=0) return;
            var normal=Vector2.Perpendicular(edge)*(.01f/length);
            points.AddRange(new[] {a,b,b+normal,a,b+normal,a+normal,
                b,a,a-normal,b,a-normal,b-normal});
        }

        sealed class NearestPoint
        {
            readonly List<Vector2> points;readonly int[] order;
            internal NearestPoint(List<Vector2> points,int[] order)
            {this.points=points;this.order=order;Build(0,order.Length,0);}
            void Build(int start,int end,int axis)
            {
                if(start>=end) return;
                Array.Sort(order,start,end-start,Comparer<int>.Create((a,b)=>{
                    int c=points[a][axis].CompareTo(points[b][axis]);return c!=0?c:a.CompareTo(b);
                }));
                int middle=(start+end)/2;Build(start,middle,1-axis);Build(middle+1,end,1-axis);
            }
            internal int Find(Vector2 query,out double distance)
            {int found=-1;double best=double.MaxValue;Search(0,order.Length,0,query,ref found,ref best);distance=best;return found;}
            void Search(int start,int end,int axis,Vector2 query,ref int found,ref double best)
            {
                if(start>=end) return;
                int middle=(start+end)/2,index=order[middle];var point=points[index];
                double x=(double)point.x-query.x,y=(double)point.y-query.y,distance=x*x+y*y;
                int comparison=distance.CompareTo(best);
                if(comparison<0 || comparison==0 && index<found) {found=index;best=distance;}
                double difference=(double)query[axis]-point[axis];
                if(difference<=0) Search(start,middle,1-axis,query,ref found,ref best);
                else Search(middle+1,end,1-axis,query,ref found,ref best);
                if(difference*difference>best) return;
                if(difference<=0) Search(middle+1,end,1-axis,query,ref found,ref best);
                else Search(start,middle,1-axis,query,ref found,ref best);
            }
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
