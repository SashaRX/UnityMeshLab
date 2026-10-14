using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        static void InitializePreparedChain(Surface[][] chain,Options options,ref int nextChart,
            ref float width,ref float height,CancellationToken token)
        {
            foreach(var surface in chain.SelectMany(l=>l))
            {
                token.ThrowIfCancellationRequested();
                var uv=surface.input.mesh.uv2;
                if(!surface.input.usePreparedUv2 || uv.Length!=surface.positions.Length)
                    throw new InvalidOperationException("Reverse regrouping requires the complete ordinary Transfer output.");
                surface.pixels=surface.indices.Select(i=>uv[i]*options.seedResolution).ToArray();
            }
            float density=Density(chain[0]);
            if(!(density>0)) density=options.seedDensity>0?options.seedDensity:options.seedResolution;
            foreach(var surface in chain.SelectMany(l=>l))
            {
                token.ThrowIfCancellationRequested();
                // Freeze ordinary shells before any projection. A repaired shell
                // supplies valid donor coordinates in an independent local frame;
                // the whole chain is placed together by the final pack.
                AssignCharts(surface,Enumerable.Range(0,surface.faces.Length).ToArray(),ref nextChart);
                surface.preparedRegions=surface.faces.Select(f=>f.chart).ToArray();
                surface.sourceUvCorners=surface.pixels.ToArray();
                var scan=UvAtlasDiagnostics.Measure(Atlas(new[] {surface}),token,
                    comparisonBudget:options.comparisonBudget,collectConflicts:true);
                if(!scan.complete) throw new InvalidOperationException("Ordinary Transfer input audit exceeded its budget.");
                var bad=new HashSet<int>(scan.conflicts.SelectMany(p=>new[] {p.a,p.b}));
                foreach(int f in Enumerable.Range(0,surface.faces.Length))
                    if(Anisotropy(surface,f)>options.maxAnisotropy) bad.Add(f);
                foreach(var shell in Enumerable.Range(0,surface.faces.Length).GroupBy(f=>surface.preparedRegions[f]))
                {
                    var faces=shell.ToArray();
                    if(!faces.Any(bad.Contains)) continue;
                    var patch=faces.SelectMany(f=>Enumerable.Range(f*3,3).Select(c=>surface.positions[surface.indices[c]])).ToArray();
                    var coordinates=faces.SelectMany(f=>Enumerable.Range(f*3,3).Select(c=>surface.pixels[c])).ToArray();
                    var frames=faces.Select(f=>surface.metricFrames[f]).ToArray();
                    var original=(Vector2[])coordinates.Clone();
                    coordinates=RepairPreparedShell(patch,coordinates,frames,options,token);
                    double worldArea=frames.Sum(f=>f.length*f.y),uvArea=0;
                    for(int t=0;t<coordinates.Length;t+=3)
                        uvArea+=Math.Abs(((double)coordinates[t+1].x-coordinates[t].x)*((double)coordinates[t+2].y-coordinates[t].y)
                            -((double)coordinates[t+1].y-coordinates[t].y)*((double)coordinates[t+2].x-coordinates[t].x));
                    var minimum=coordinates.Aggregate(Vector2.Min);
                    double scale=density*Math.Sqrt(worldArea/uvArea);
                    coordinates=coordinates.Select(p=>new Vector2((float)(((double)p.x-minimum.x)*scale),
                        (float)(((double)p.y-minimum.y)*scale))).ToArray();
                    coordinates=RepairPreparedSharedCorners(patch,coordinates,frames,options.maxAnisotropy,token);
                    var aligned=AlignPreparedRepair(coordinates,original,frames,options);
                    if(aligned!=null && !ReverseUvShellPrecision.Stabilize(coordinates,aligned,frames,options.maxAnisotropy,token)) aligned=null;
                    if(surface.packingCoordinates==null) surface.packingCoordinates=new Vector2[surface.pixels.Length];
                    for(int i=0;i<faces.Length;++i)
                    {
                        surface.faces[faces[i]].localFallback=true;
                        surface.faces[faces[i]].coordinateDomain=aligned==null?shell.Key+1:0;
                        for(int k=0;k<3;++k)
                        {
                            surface.packingCoordinates[faces[i]*3+k]=coordinates[i*3+k];
                            surface.pixels[faces[i]*3+k]=(aligned??coordinates)[i*3+k];
                        }
                    }
                    UvtLog.Info($"[ReverseUV] Whole-shell repair LOD{surface.lod} '{surface.input.key}': {faces.Length} faces; geometry and original shell membership retained.");
                }
            }
            // Retain a repaired shell in the common Forward frame when its
            // healthy faces anchor it uniquely. Contacts with another same-LOD
            // shell detach the complete repair, never its individual triangles.
            foreach(var level in chain)
            {
                var refs=level.SelectMany(s=>Enumerable.Range(0,s.faces.Length).Select(f=>(s,f))).ToArray();
                var active=refs.Where(p=>p.s.faces[p.f].coordinateDomain==0).ToArray();
                var scan=UvAtlasDiagnostics.Measure(CorrespondenceGeometry(active),token,comparisonBudget:options.comparisonBudget,collectConflicts:true);
                if(!scan.complete) throw new InvalidOperationException("Prepared shell repair contact audit exceeded its budget.");
                foreach(var (a,b) in scan.conflicts)
                    foreach(int index in new[] {a,b})
                    {
                        var (s,f)=active[index];
                        if(!s.faces[f].localFallback) continue;
                        int region=s.preparedRegions[f];
                        for(int t=0;t<s.faces.Length;++t) if(s.preparedRegions[t]==region)
                        {
                            s.faces[t].coordinateDomain=region+1;
                            for(int k=0;k<3;++k) s.pixels[t*3+k]=s.packingCoordinates[t*3+k];
                        }
                    }
            }
            foreach(var surface in chain.SelectMany(l=>l)) surface.sourceUvCorners=surface.pixels.ToArray();
            if(options.seedDensity>0)
            {
                // Forward xatlas dimensions may include oversampling. Return to
                // bake density before measuring correspondence tolerance/padding.
                // A binary scale preserves every Float32 coordinate exactly and
                // applies equally to every LOD, including repaired input faces.
                float scale=(float)Math.Pow(2,Math.Ceiling(Math.Log(options.seedDensity/density,2)));
                if(float.IsNaN(scale) || float.IsInfinity(scale) || scale<=0)
                    throw new InvalidOperationException("Prepared Transfer density is invalid.");
                foreach(var surface in chain.SelectMany(l=>l))
                {
                    for(int c=0;c<surface.pixels.Length;++c)
                    {
                        surface.pixels[c]*=scale;
                        if(surface.packingCoordinates!=null) surface.packingCoordinates[c]*=scale;
                    }
                    surface.sourceUvCorners=surface.pixels.ToArray();
                }
                width*=scale;height*=scale;options.seedDensity=density*scale;
            }
        }

        static Vector2[] AlignPreparedRepair(Vector2[] repaired,Vector2[] original,
            (double length,double x,double y)[] frames,Options options)
        {
            var healthy=Enumerable.Range(0,frames.Length).Where(f=>TriangleAnisotropy(frames[f],
                original[f*3],original[f*3+1],original[f*3+2])<=options.maxAnisotropy)
                .SelectMany(f=>Enumerable.Range(f*3,3)).ToArray();
            if(healthy.Length<3) return null;
            double sx=healthy.Average(i=>(double)repaired[i].x),sy=healthy.Average(i=>(double)repaired[i].y);
            double tx=healthy.Average(i=>(double)original[i].x),ty=healthy.Average(i=>(double)original[i].y);
            foreach(int sign in new[] {1,-1})
            {
                double length=0,dot=0,cross=0;
                foreach(int i in healthy)
                {
                    double x=repaired[i].x-sx,y=(repaired[i].y-sy)*sign;
                    double u=original[i].x-tx,v=original[i].y-ty;
                    length+=x*x+y*y;dot+=x*u+y*v;cross+=x*v-y*u;
                }
                if(length<=0) continue;
                double a=dot/length,b=cross/length;
                var output=repaired.Select(p=>{
                    double x=p.x-sx,y=(p.y-sy)*sign;
                    return new Vector2((float)(a*x-b*y+tx),(float)(b*x+a*y+ty));
                }).ToArray();
                if(healthy.All(i=>(output[i]-original[i]).magnitude<=options.boundaryToleranceTexels)) return output;
            }
            return null;
        }

        static Vector2[] RepairPreparedShell(Vector3[] corners,Vector2[] uv,
            (double length,double x,double y)[] frames,Options options,CancellationToken token)
        {
            string refusal=null;
            bool Valid(Vector2[] candidate)
            {
                for(int f=0;f<frames.Length;++f)
                {
                    double anisotropy=TriangleAnisotropy(frames[f],candidate[f*3],candidate[f*3+1],candidate[f*3+2]);
                    if(!(anisotropy<=options.maxAnisotropy))
                    {refusal=$"face {f}: anisotropy {anisotropy:G6}, intrinsic {frames[f]}, UV {candidate[f*3].ToString("G9")} / {candidate[f*3+1].ToString("G9")} / {candidate[f*3+2].ToString("G9")}";return false;}
                }
                var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=candidate,
                    indices=Enumerable.Range(0,candidate.Length).ToArray(),charts=new int[candidate.Length]},token,
                    comparisonBudget:options.comparisonBudget);
                refusal=$"contacts {scan.pairs}, collapsed {scan.degenerateFaces}, complete {scan.complete}";
                return scan.complete && scan.pairs==0 && scan.degenerateFaces==0;
            }
            var shared=RepairPreparedSharedCorners(corners,uv,frames,options.maxAnisotropy,token);
            if(Valid(shared)) return shared;
            // A flat connected patch has an exact whole-shell projection. The
            // acceptance check also rejects folds and near-edge-on faces.
            int largest=Enumerable.Range(0,frames.Length).OrderByDescending(f=>frames[f].length*frames[f].y).First()*3;
            var origin=corners[largest];
            var tangent=MeshGeometry.UnitDirection(corners[largest+1]-origin);
            var normal=MeshGeometry.UnitDirection(Vector3.Cross(corners[largest+1]-origin,corners[largest+2]-origin));
            var bitangent=Vector3.Cross(normal,tangent);
            double span=corners.Max(p=>(p-origin).magnitude);
            if(corners.All(p=>Math.Abs(Vector3.Dot(p-origin,normal))<=span*.001))
            {
                var planar=corners.Select(p=>{
                    double x=(double)p.x-origin.x,y=(double)p.y-origin.y,z=(double)p.z-origin.z;
                    return new Vector2((float)(x*tangent.x+y*tangent.y+z*tangent.z),
                        (float)(x*bitangent.x+y*bitangent.y+z*bitangent.z));
                }).ToArray();
                if(Valid(planar)) return planar;
            }
            var arap=ReverseUvSeed.RepairAuthoredCharts(corners,(Vector2[])uv.Clone(),new int[uv.Length],token,out _);
            if(Valid(arap)) return arap;
            // Rebuild only the failed shell, using native chart parameterization.
            // The original triangle count and corner identities must survive.
            if(!XatlasRepack.TryAcquireNativeSession()) throw new InvalidOperationException("An xatlas operation is already in progress.");
            bool created=false;
            try
            {
                token.ThrowIfCancellationRequested();
                var vertices=new List<Vector3>();var unique=new Dictionary<Vector3,uint>();var indices=new uint[corners.Length];
                for(int i=0;i<corners.Length;++i)
                {
                    if(!unique.TryGetValue(corners[i],out uint vertex)) {vertex=(uint)vertices.Count;vertices.Add(corners[i]);unique.Add(corners[i],vertex);}
                    indices[i]=vertex;
                }
                // xatlas has an absolute area epsilon. Use a detached,
                // recentered chart-sized metric so small genuine input faces
                // are not silently ignored as zero-area native faces.
                double nativeScale=1024/Math.Max(span,1e-20);
                var positions=vertices.SelectMany(p=>new[] {(float)(((double)p.x-origin.x)*nativeScale),
                    (float)(((double)p.y-origin.y)*nativeScale),(float)(((double)p.z-origin.z)*nativeScale)}).ToArray();
                XatlasNative.xatlasCreate();created=true;
                if(XatlasNative.xatlasAddMesh(positions,null,(uint)vertices.Count,indices,(uint)indices.Length)!=0)
                    throw new InvalidOperationException("Connected reverse input shell could not be parameterized.");
                XatlasNative.xatlasComputeCharts();
                XatlasNative.xatlasPackCharts(0,(uint)options.padding,0,512,1,0,0,0,0);
                int count=XatlasNative.xatlasGetOutputVertexCount(0);
                if(count<=0 || XatlasNative.xatlasGetOutputIndexCount(0)!=indices.Length)
                    throw new InvalidOperationException("Reverse shell parameterization changed the triangle count.");
                var xref=new uint[count];var coordinates=new float[count*2];var charts=new uint[count];var output=new uint[indices.Length];
                if(XatlasNative.xatlasGetOutputVertexData(0,xref,coordinates,charts,count)!=count
                    || XatlasNative.xatlasGetOutputIndices(0,output,output.Length)!=output.Length)
                    throw new InvalidOperationException("Incomplete reverse shell parameterization.");
                var candidate=new Vector2[corners.Length];
                uint width=XatlasNative.xatlasGetAtlasWidth(),height=XatlasNative.xatlasGetAtlasHeight();
                for(int i=0;i<candidate.Length;++i)
                {
                    if(output[i]>=count || xref[output[i]]!=indices[i])
                        throw new InvalidOperationException("Reverse shell parameterization changed source face identity.");
                    candidate[i]=new Vector2(coordinates[output[i]*2]*width,coordinates[output[i]*2+1]*height);
                }
                if(Valid(candidate)) return candidate;
                var stitched=RepairPreparedSharedCorners(corners,candidate,frames,options.maxAnisotropy,token);
                if(Valid(stitched)) return stitched;
                // Native parameterization may inflate a near-collinear face
                // for rasterization. Relax the connected output chart rather
                // than detach that face into an intrinsic triangle island.
                var relaxed=ReverseUvSeed.RepairAuthoredCharts(corners,candidate,new int[candidate.Length],token,out _);
                if(Valid(relaxed)) return relaxed;
                throw new InvalidOperationException("Connected reverse shell repair failed its metric/contact audit: "+refusal+"; no triangle-island fallback was published.");
            }
            finally {try {if(created) XatlasNative.xatlasDestroy();} finally {XatlasRepack.ReleaseNativeSession();}}
        }

        internal static Vector2[] RepairPreparedSharedCorners(Vector3[] corners,Vector2[] uv,
            (double length,double x,double y)[] frames,float limit,CancellationToken token)
        {
            var output=(Vector2[])uv.Clone();
            double Metric(int f,Vector2[] p)=>TriangleAnisotropy(frames[f],p[f*3],p[f*3+1],p[f*3+2]);
            for(int pass=0;pass<8;++pass)
            {
                bool changed=false;
                for(int f=0;f<frames.Length;++f)
                {
                    token.ThrowIfCancellationRequested();double old=Metric(f,output);
                    if(old<=limit) continue;
                    int t=f*3;var rest=new[] {(x:0d,y:0d),(x:frames[f].length,y:0d),(x:frames[f].x,y:frames[f].y)};
                    Vector2[] best=null;double bestMetric=old;
                    for(int third=0;third<3;++third)
                    {
                        int a=(third+1)%3,b=(third+2)%3;
                        double ex=rest[b].x-rest[a].x,ey=rest[b].y-rest[a].y;
                        double px=rest[third].x-rest[a].x,py=rest[third].y-rest[a].y;
                        double length=Math.Sqrt(ex*ex+ey*ey);
                        double ux=(double)output[t+b].x-output[t+a].x,uy=(double)output[t+b].y-output[t+a].y;
                        double uvLength=Math.Sqrt(ux*ux+uy*uy);
                        if(length<=0 || uvLength<=0) continue;
                        double along=(px*ex+py*ey)/(length*length),height=Math.Abs(ex*py-ey*px)/(length*length);
                        double cross=ux*((double)output[t+third].y-output[t+a].y)-uy*((double)output[t+third].x-output[t+a].x);
                        double sign=cross<0?-1:1;
                        var point=new Vector2((float)(output[t+a].x+ux*along-uy*height*sign),
                            (float)(output[t+a].y+uy*along+ux*height*sign));
                        var trial=(Vector2[])output.Clone();var oldPoint=output[t+third];
                        // One shared UV vertex moves everywhere in this chart;
                        // no incident face is detached and no edge is cut.
                        var affected=new HashSet<int>();
                        for(int i=0;i<trial.Length;++i)
                            if(corners[i]==corners[t+third] && output[i]==oldPoint)
                            {trial[i]=point;affected.Add(i/3);}
                        double metric=Metric(f,trial);
                        if(!(metric<bestMetric) || affected.Any(n=>n!=f && Metric(n,trial)>Math.Max(limit,Metric(n,output)))) continue;
                        best=trial;bestMetric=metric;
                    }
                    if(best==null) continue;
                    output=best;changed=true;
                }
                if(!changed) break;
            }
            return output;
        }

        static void ProjectPreparedChain(Surface[] donor,Surface[] targets,Options options,CancellationToken token)
        {
            var originals=targets.ToDictionary(s=>s,s=>s.pixels.ToArray());
            var originalFaces=targets.ToDictionary(s=>s,s=>s.faces.Select(f=>f.Copy()).ToArray());
            List<Chart> charts;
            using (options.performance?.Measure("Donor chart BVHs", targets[0].lod)) charts=Charts(donor);
            var parents=charts.ToDictionary(c=>c.id);long queries=0;
            ProjectFaces(donor,targets,charts,options,ref queries,token);
            using var shellTiming = options.performance?.Measure("Prepared-shell grouping and underlays", targets[0].lod);
            var moved=new HashSet<(Surface,int)>();
            foreach(var surface in targets)
                foreach(var group in Enumerable.Range(0,surface.faces.Length).GroupBy(f=>surface.preparedRegions[f]))
                {
                    token.ThrowIfCancellationRequested();
                    var faces=group.ToArray();
                    bool samePlace=faces.All(f=>surface.faces[f].inherited
                        && surface.faces[f].coordinateDomain==originalFaces[surface][f].coordinateDomain && Enumerable.Range(0,3).All(k=>
                        (surface.pixels[f*3+k]-originals[surface][f*3+k]).magnitude<=options.boundaryToleranceTexels));
                    if(!samePlace && TryKeepChartShape(surface,faces,parents,options,token))
                    {moved.Add((surface,group.Key));continue;}
                    foreach(int f in faces)
                    {
                        var face=surface.faces[f];
                        bool matching=face.inherited && face.coordinateDomain==originalFaces[surface][f].coordinateDomain && Enumerable.Range(0,3).All(k=>
                            (surface.pixels[f*3+k]-originals[surface][f*3+k]).magnitude<=options.boundaryToleranceTexels);
                        for(int k=0;k<3;++k) surface.pixels[f*3+k]=originals[surface][f*3+k];
                        if(!matching) surface.faces[f]=originalFaces[surface][f].Copy();
                    }
                }
            ResolveOverlaps(targets,options,token);
            // A moved shell must be accepted with every face of its prepared
            // island. All unchanged islands remain in their original shared frame.
            bool changed;
            do
            {
                changed=false;
                var refs=targets.SelectMany(s=>Enumerable.Range(0,s.faces.Length).Select(f=>(s,f))).ToArray();
                var reject=new HashSet<(Surface,int)>();
                foreach(var pair in moved)
                    if(Enumerable.Range(0,pair.Item1.faces.Length).Any(f=>pair.Item1.preparedRegions[f]==pair.Item2 && !pair.Item1.faces[f].inherited)) reject.Add(pair);
                foreach(var part in CorrespondenceDomains(refs))
                {
                    var scan=UvAtlasDiagnostics.Measure(part.geometry,token,comparisonBudget:options.comparisonBudget,collectConflicts:true);
                    if(!scan.complete) throw new InvalidOperationException("Prepared chart contact audit exceeded its budget.");
                    foreach(var (localA,localB) in scan.conflicts)
                    {
                        int a=part.faces[localA],b=part.faces[localB];
                        var x=refs[a];var y=refs[b];
                        if(IsOrderedOverlap(x.s.faces[x.f],y.s.faces[y.f])) continue;
                        if(moved.Contains((x.s,x.s.preparedRegions[x.f]))) reject.Add((x.s,x.s.preparedRegions[x.f]));
                        if(moved.Contains((y.s,y.s.preparedRegions[y.f]))) reject.Add((y.s,y.s.preparedRegions[y.f]));
                    }
                }
                foreach(var (surface,region) in reject)
                {
                    foreach(int f in Enumerable.Range(0,surface.faces.Length).Where(f=>surface.preparedRegions[f]==region))
                    {
                        surface.faces[f]=originalFaces[surface][f].Copy();
                        for(int k=0;k<3;++k) surface.pixels[f*3+k]=originals[surface][f*3+k];
                    }
                    moved.Remove((surface,region));changed=true;
                }
                if(changed) ResolveOverlaps(targets,options,token);
            } while(changed);
            UvtLog.Info(UvtLog.Category.General,$"[ReverseUV] Regroup prepared Transfer: {targets.Sum(s=>s.preparedRegions.Distinct().Count())} intact islands; {moved.Count} whole projected shells; no new projection cuts.");
        }
    }
}
