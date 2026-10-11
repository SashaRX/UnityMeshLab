using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Pack chart families once for the whole completed chain. Every
    /// descendant and intentional overlay receives its parent's rigid move.</summary>
    internal static class ReverseUvFinalPacking
    {
        internal static int Pack(List<ReverseUvTransfer.Surface[]> levels, ReverseUvTransfer.Options options,
            ReverseUvTransfer.Report report, int previousSide, CancellationToken token)
        {
            report.finalPacking=null;report.finalPackingRefusal=null;report.finalPackingRectangleRefusal=null;
            report.finalPackingFootprints=false;report.finalPackingNative=false;
            report.finalPackingFamilies=0;report.finalPackingScale=0;
            var surfaces = levels.SelectMany(l => l).ToArray();
            // A region label can survive independent placement/rescue of its
            // disconnected pieces. It is not a rigid packing item: using that
            // label reserves the empty space between unrelated UV islands.
            // First find actual UV components, then join explicit ancestry and
            // ordered overlays across the chain.
            int count=0;
            var offsets=new Dictionary<ReverseUvTransfer.Surface,int>();
            foreach(var surface in surfaces) {offsets.Add(surface,count);count+=surface.faces.Length;}
            var families = new DisjointSet(count);
            foreach(var surface in surfaces)
            {
                if(options.regroupPreparedAtlas)
                    foreach(var group in Enumerable.Range(0,surface.faces.Length).GroupBy(f=>surface.preparedRegions[f]))
                        foreach(int f in group) families.Union(offsets[surface]+group.First(),offsets[surface]+f);
                // Retain corner-connected pieces of a region too (including
                // T-junction boundaries). Separate placements have no shared
                // geometric/UV corner and remain separate packing components.
                var vertices=new Dictionary<(int chart,Vector3 position,Vector2 uv),int>();
                for(int f=0;f<surface.faces.Length;++f)
                {
                    token.ThrowIfCancellationRequested();
                    int face=offsets[surface]+f,chart=surface.faces[f].chart;
                    for(int k=0;k<3;++k)
                    {
                        int corner=f*3+k;
                        var vertex=(chart,surface.positions[surface.indices[corner]],surface.pixels[corner]);
                        if(vertices.TryGetValue(vertex,out int other))
                            families.Union(face,other);
                        else vertices.Add(vertex,face);
                    }
                }
            }
            if(options.regroupPreparedAtlas)
                JoinPreparedCorrespondence(surfaces,offsets,families,previousSide,options,token);
            var byLod=levels.ToDictionary(l=>l[0].lod);
            foreach(var surface in surfaces)
                for(int f=0;f<surface.faces.Length;++f)
                {
                    var face=surface.faces[f];
                    if(face.inherited && face.parentLod>surface.lod)
                    {
                        token.ThrowIfCancellationRequested();
                        var parent=byLod[face.parentLod][face.parentMesh];
                        families.Union(offsets[surface]+f,offsets[parent]+face.parentFace);
                    }
                }
            foreach (var level in levels)
                foreach (var relation in report.overlaps.Where(r => r.lod == level[0].lod))
                    families.Union(offsets[level[relation.underMesh]]+relation.underFace,
                        offsets[level[relation.overMesh]]+relation.overFace);
            var corners = new SortedDictionary<int,List<(ReverseUvTransfer.Surface surface,int corner)>>();
            foreach (var surface in surfaces)
                for (int f = 0; f < surface.faces.Length; ++f)
                {
                    token.ThrowIfCancellationRequested();
                    int family = families.Find(offsets[surface]+f);
                    if (!corners.TryGetValue(family,out var list))
                    { list = new List<(ReverseUvTransfer.Surface,int)>(); corners.Add(family,list); }
                    for (int k = 0; k < 3; ++k) list.Add((surface,f*3+k));
                }
            try
            {
                var groups = corners.Values.ToArray();
                var timing=System.Diagnostics.Stopwatch.StartNew();
                bool logTiming=surfaces.Sum(s=>s.faces.Length)>2048;
                if(logTiming) UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Pack {groups.Length} shared chart families.");
                var coordinates = groups.Select(g => g.Select(c => c.surface.pixels[c.corner]).ToArray()).ToArray();
                var metrics=groups.Select(g=>Enumerable.Range(0,g.Count/3).Select(f=>{
                    var corner=g[f*3];var s=corner.surface;int t=corner.corner;
                    return Metric(s,t,s.pixels[t],s.pixels[t+1],s.pixels[t+2],previousSide);
                }).ToArray()).ToArray();
                var allowed=new HashSet<(ReverseUvTransfer.Face,ReverseUvTransfer.Face)>();
                foreach(var relation in report.overlaps)
                {
                    var level=byLod[relation.lod];
                    var a=level[relation.underMesh].faces[relation.underFace];var b=level[relation.overMesh].faces[relation.overFace];
                    allowed.Add((a,b));allowed.Add((b,a));
                }
                var scans=groups.Select(g=>Enumerable.Range(0,g.Count/3)
                    .GroupBy(f=>g[f*3].surface.lod)
                    .Select(part=>{
                        var faceIds=part.ToArray();
                        return (faceIds,geometry:new RemeshNative.Geometry {uv=new Vector2[faceIds.Length*3],
                            indices=Enumerable.Range(0,faceIds.Length*3).ToArray(),charts=new int[faceIds.Length*3]});
                    }).ToArray()).ToArray();
                var intersection=new UvAtlasDiagnostics.IntersectionTest();
                bool Valid(int group,Vector2[] uv,int side)
                {
                    var entries=groups[group];
                    for(int f=0;f<metrics[group].Length;++f)
                    {
                        var corner=entries[f*3];var s=corner.surface;int t=corner.corner;
                        double metric=Metric(s,t,uv[f*3],uv[f*3+1],uv[f*3+2],side);
                        if(!MetricFits(metric,metrics[group][f],
                            Altitude(s.pixels[t],s.pixels[t+1],s.pixels[t+2]),
                            Altitude(uv[f*3],uv[f*3+1],uv[f*3+2]),options)) return false;
                    }
                    foreach(var scanInput in scans[group])
                    {
                        var faceIds=scanInput.faceIds;var geometry=scanInput.geometry;
                        for(int f=0;f<faceIds.Length;++f)
                            for(int k=0;k<3;++k) geometry.uv[f*3+k]=uv[faceIds[f]*3+k]/side;
                        if(faceIds.Length<=24)
                        {
                            long comparisons=0;
                            for(int a=0;a<faceIds.Length;++a)
                            {
                                if(UvAtlasDiagnostics.IntersectionTest.Degenerate(geometry,a)) return false;
                                for(int b=a+1;b<faceIds.Length;++b)
                                {
                                    if(options.comparisonBudget>=0 && ++comparisons>options.comparisonBudget) return false;
                                    if(!intersection.OverlapsInAtlas(geometry,a,b)) continue;
                                    var ca=entries[faceIds[a]*3];var cb=entries[faceIds[b]*3];
                                    if(!allowed.Contains((ca.surface.faces[ca.corner/3],cb.surface.faces[cb.corner/3]))) return false;
                                }
                            }
                            continue;
                        }
                        var scan=UvAtlasDiagnostics.Measure(geometry,token,comparisonBudget:options.comparisonBudget,collectConflicts:true);
                        if(!scan.complete || scan.invalidFaces>0 || scan.degenerateFaces>0) return false;
                        foreach(var pair in scan.conflicts)
                        {
                            var a=entries[faceIds[pair.a]*3];var b=entries[faceIds[pair.b]*3];
                            if(!allowed.Contains((a.surface.faces[a.corner/3],b.surface.faces[b.corner/3]))) return false;
                        }
                    }
                    return true;
                }
                int side;float scale;
                Vector2[][] packed;bool freshFootprints=false;
                try {packed=ReverseUvFamilyPacking.Pack(coordinates,options,token,out side,out scale,Valid);}
                catch(InvalidOperationException error)
                {
                    report.finalPackingRectangleRefusal=error.Message;
                    packed=ReverseUvFootprintPacking.PackFresh(coordinates,previousSide,options,token,Valid,out side);
                    scale=1;freshFootprints=true;
                    UvtLog.Info(UvtLog.Category.Repack,"[ReverseUV] Rectangle trial refused; packed family footprints on an empty canvas: "+error.Message);
                }
                // Thousands of short charts can spend most of a small canvas
                // on two-pixel gutters. Test higher common pixel density in a
                // larger canvas; never squeeze charts or reduce their gutter.
                if(options.fillFinalAtlas && coordinates.Length>256 && side<=options.maxAtlasSize/2)
                    try
                    {
                        var larger=ReverseUvFamilyPacking.Pack(coordinates,options,token,out int largerSide,
                            out float largerScale,Valid,initialScale:scale*2);
                        if(BoundsCost(larger,largerScale)<BoundsCost(packed,scale))
                        {packed=larger;side=largerSide;scale=largerScale;}
                    }
                    catch(InvalidOperationException) { /* Retain the already audited placement. */ }
                if(logTiming) UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Family rectangles: {timing.ElapsedMilliseconds} ms. Test triangle footprints.");
                var search=new ReverseUvFootprintPacking.Diagnostics();
                int completedSide=side;
                packed = ReverseUvFootprintPacking.Improve(coordinates,packed,side,scale,options,token,Valid,
                    out scale,out bool footprintImproved,search,resized=>completedSide=resized);
                side=completedSide;
                if(logTiming) UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Final family packing: {timing.ElapsedMilliseconds} ms, footprint improvement {footprintImproved}; native {search.nativePacked}; moved {search.tightenedFamilies}; completed orders {search.completedOrders}; search work {search.tighteningWork}/{search.repackingWork}; validations {search.validations}.");
                if(logTiming && search.refusedOrders.Count>0) UvtLog.Info(UvtLog.Category.Repack,"[ReverseUV] Footprint trials: "+string.Join("; ",search.refusedOrders));
                var replacements = surfaces.ToDictionary(s => s,s => new Vector2[s.pixels.Length]);
                for (int g = 0; g < groups.Length; ++g)
                    for (int c = 0; c < groups[g].Count; ++c)
                    {
                        var corner = groups[g][c];
                        replacements[corner.surface][corner.corner] = packed[g][c];
                    }
                foreach (var surface in surfaces)
                    for (int f = 0; f < surface.faces.Length; ++f)
                    {
                        token.ThrowIfCancellationRequested();
                        int t = f*3; var uv = replacements[surface];
                        double oldMetric = Metric(surface,t,surface.pixels[t],surface.pixels[t+1],surface.pixels[t+2],previousSide);
                        double newMetric = Metric(surface,t,uv[t],uv[t+1],uv[t+2],side);
                        if (!MetricFits(newMetric,oldMetric,
                            Altitude(surface.pixels[t],surface.pixels[t+1],surface.pixels[t+2]),Altitude(uv[t],uv[t+1],uv[t+2]),options))
                            throw new InvalidOperationException($"Final packing cannot preserve LOD{surface.lod} '{surface.input?.key}' face {f} at float precision (anisotropy {oldMetric:G6} -> {newMetric:G6}).");
                    }
                foreach (var surface in surfaces) surface.pixels = replacements[surface];
                report.texelsPerUnit *= scale;
                report.finalPacking = "shared-chart-families";
                report.finalPackingFamilies=groups.Length;
                report.finalPackingScale=scale;
                report.finalPackingFootprints=freshFootprints || footprintImproved;
                report.finalPackingNative=search.nativePacked;
                return side;
            }
            catch (InvalidOperationException error)
            {
                report.finalPackingRefusal = error.Message;
                UvtLog.Warn(UvtLog.Category.Repack,"[ReverseUV] Final chart packing failed; no final atlas was published: "+error.Message);
                return previousSide;
            }
        }

        // Ordinary Transfer already placed every LOD in one common UV frame.
        // Projection acceptance describes individual triangles, not all of that
        // existing island correspondence. Retain cross-LOD footprint matches in
        // the same mesh group even when the reverse proposal was ambiguous.
        static void JoinPreparedCorrespondence(ReverseUvTransfer.Surface[] surfaces,
            Dictionary<ReverseUvTransfer.Surface,int> offsets,DisjointSet families,int side,
            ReverseUvTransfer.Options options,CancellationToken token)
        {
            foreach(var part in surfaces.GroupBy(s=>UvToolContext.ExtractGroupKey(s.input.key)))
            {
                if(part.Select(s=>s.lod).Distinct().Count()<2) continue;
                var faces=part.SelectMany(s=>Enumerable.Range(0,s.faces.Length).Select(f=>(s,f))).ToArray();
                var geometry=new RemeshNative.Geometry {
                    uv=faces.SelectMany(x=>Enumerable.Range(0,3).Select(k=>x.s.pixels[x.f*3+k]/side)).ToArray(),
                    indices=Enumerable.Range(0,faces.Length*3).ToArray(),charts=new int[faces.Length*3]};
                var scan=UvAtlasDiagnostics.Measure(geometry,token,comparisonBudget:options.comparisonBudget,collectConflicts:true);
                if(!scan.complete) throw new InvalidOperationException("Prepared cross-LOD footprint audit exceeded its budget.");
                foreach(var (a,b) in scan.conflicts)
                {
                    var x=faces[a];var y=faces[b];
                    if(x.s.lod!=y.s.lod) families.Union(offsets[x.s]+x.f,offsets[y.s]+y.f);
                }
            }
        }

        static double Metric(ReverseUvTransfer.Surface surface,int corner,Vector2 a,Vector2 b,Vector2 c,int side)
            => surface.packingFrames!=null
                ?ReverseUvTransfer.TriangleAnisotropy(surface.packingFrames[corner/3],a/side,b/side,c/side)
                :ReverseUvTransfer.TriangleAnisotropy(surface.positions[surface.indices[corner]],
                    surface.positions[surface.indices[corner+1]],surface.positions[surface.indices[corner+2]],a,b,c);

        static double BoundsCost(Vector2[][] groups,float scale)
        {
            var points=groups.SelectMany(p=>p).ToArray();var min=points.Aggregate(Vector2.Min);var max=points.Aggregate(Vector2.Max);
            return ((double)max.x-min.x)*((double)max.y-min.y)/(scale*(double)scale);
        }

        // These candidates are rigid family moves or one common scale. A
        // subpixel altitude makes relative derivatives sensitive to Float32
        // rounding, although the moved UV corners remain faithful at bake
        // resolution. Keep the absolute stretch cap and every overlap audit;
        // only the 1% no-regression derivative gate is resolution dependent.
        internal static bool MetricFits(double current,double baseline,double oldAltitude,double newAltitude,
            ReverseUvTransfer.Options options)
            => !double.IsNaN(current) && !double.IsInfinity(current) && current<=options.maxAnisotropy
                && (UvTransferTolerance.MetricFits(current,baseline,options.relativeStretchTolerance)
                    || (oldAltitude>0 && newAltitude>0
                        && Math.Max(oldAltitude,newAltitude)<=options.boundaryToleranceTexels));

        static double Altitude(Vector2 a,Vector2 b,Vector2 c)
        {
            double ex=(double)b.x-a.x,ey=(double)b.y-a.y,dx=(double)c.x-a.x,dy=(double)c.y-a.y;
            double longest=Math.Sqrt(Math.Max(ex*ex+ey*ey,Math.Max(dx*dx+dy*dy,(dx-ex)*(dx-ex)+(dy-ey)*(dy-ey))));
            return longest>0?Math.Abs(ex*dy-ey*dx)/longest:0;
        }
    }
}
