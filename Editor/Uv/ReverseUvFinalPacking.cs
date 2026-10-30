using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Pack chart families once for the whole completed chain. Every
    /// descendant and intentional overlay receives its parent's rigid move.</summary>
    internal static partial class ReverseUvFinalPacking
    {
        /// <summary>
        /// The surfaces that must share one atlas when each mesh is packed on its own:
        /// the LODs of one mesh (by group key) and every surface an island ancestry or
        /// an ordered overlay links to them. No chart family spans two sets.
        /// </summary>
        internal static List<ReverseUvTransfer.Surface[]> MeshSets(List<ReverseUvTransfer.Surface[]> levels,ReverseUvTransfer.Report report)
        {
            var surfaces=levels.SelectMany(l=>l).ToArray();
            var index=new Dictionary<ReverseUvTransfer.Surface,int>();
            for(int i=0;i<surfaces.Length;++i) index.Add(surfaces[i],i);
            var sets=new DisjointSet(surfaces.Length);
            foreach(var mesh in surfaces.GroupBy(s=>UvToolContext.ExtractGroupKey(s.input.key??string.Empty)))
                foreach(var surface in mesh) sets.Union(index[mesh.First()],index[surface]);
            var byLod=levels.ToDictionary(l=>l[0].lod);
            foreach(var surface in surfaces)
                foreach(var face in surface.faces.Where(f=>f.inherited && f.parentLod>surface.lod))
                    sets.Union(index[surface],index[byLod[face.parentLod][face.parentMesh]]);
            foreach(var relation in report.overlaps)
                sets.Union(index[byLod[relation.lod][relation.underMesh]],index[byLod[relation.lod][relation.overMesh]]);
            return surfaces.GroupBy(s=>sets.Find(index[s])).Select(g=>g.ToArray()).ToList();
        }

        /// <param name="only">The mesh set to pack, from <see cref="MeshSets"/>; null packs the whole chain.</param>
        internal static int Pack(List<ReverseUvTransfer.Surface[]> levels, ReverseUvTransfer.Options options,
            ReverseUvTransfer.Report report, int previousSide, CancellationToken token, ReverseUvTransfer.Surface[] only = null)
        {
            report.finalPacking=null;report.finalPackingRefusal=null;report.finalPackingRectangleRefusal=null;
            report.finalPackingFootprints=false;report.finalPackingNative=false;
            report.finalPackingFamilies=0;report.finalPackingScale=0;
            report.resolutionLimitedFaces=0;
            // Every path packs at the requested density; Fill then tests denser
            // atlases whose gutter grows with the density (see the end).
            bool fill=options.fillFinalAtlas;
            options=options.AtDensity(1);
            var included=only==null?null:new HashSet<ReverseUvTransfer.Surface>(only);
            var surfaces = levels.SelectMany(l => l).Where(s=>included==null || included.Contains(s)).ToArray();
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
            if(options.regroupPreparedAtlas && !(options.frozenShellCascade && !options.preserveProjectedOverlap))
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
                        if(face.coordinateDomain!=parent.faces[face.parentFace].coordinateDomain)
                            throw new InvalidOperationException("Reverse shell ancestry uses different coordinate frames.");
                        families.Union(offsets[surface]+f,offsets[parent]+face.parentFace);
                    }
                }
            foreach (var level in levels)
                foreach (var relation in report.overlaps.Where(r => r.lod == level[0].lod && offsets.ContainsKey(level[r.underMesh])))
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
                using var footprints = new ReverseUvNativeFamilyPacking.FootprintCache(options.cachePackingFootprints, options.performance);
                using var chartSession=options.cacheNativeCharts && options.frozenShellCascade && !options.preserveProjectedOverlap
                    ?new ReverseUvNativeChartSession(options.performance):null;
                var groups = corners.Values.ToArray();
                var timing=System.Diagnostics.Stopwatch.StartNew();
                bool logTiming=surfaces.Sum(s=>s.faces.Length)>2048;
                if(logTiming) UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Pack {groups.Length} shared chart families.");
                var coordinates = groups.Select(g => g.Select(c => c.surface.pixels[c.corner]).ToArray()).ToArray();
                if(options.frozenShellCascade && !options.preserveProjectedOverlap)
                    for(int group=0;group<groups.Length;++group)
                    {
                        var entries=groups[group];int donorLod=entries.Max(c=>c.surface.lod);
                        var donor=Enumerable.Range(0,entries.Count/3).Where(f=>entries[f*3].surface.lod==donorLod).ToArray();
                        double world=0,area=0;
                        foreach(int f in donor)
                        {
                            var c=entries[f*3];var frame=c.surface.packingFrames[c.corner/3];world+=frame.length*frame.y;
                            var a=coordinates[group][f*3];var b=coordinates[group][f*3+1];var d=coordinates[group][f*3+2];
                            area+=Math.Abs(((double)b.x-a.x)*((double)d.y-a.y)-((double)b.y-a.y)*((double)d.x-a.x));
                        }
                        if(!(area>0) || !(world>0)) throw new InvalidOperationException("Frozen family has no measurable area.");
                        double density=options.seedDensity*Math.Sqrt(world/area);var origin=coordinates[group].Aggregate(Vector2.Min);
                        // Correspondence is complete. Fix the requested texel
                        // density once per family, then start classic Repack
                        // with fresh pixel padding and no reserved old slots.
                        coordinates[group]=coordinates[group].Select(p=>new Vector2((float)(((double)p.x-origin.x)*density),
                            (float)(((double)p.y-origin.y)*density))).ToArray();
                    }
                var metrics=groups.Select((g,id)=>Enumerable.Range(0,g.Count/3).Select(f=>{
                    var corner=g[f*3];var s=corner.surface;int t=corner.corner;
                    return Metric(s,t,coordinates[id][f*3],coordinates[id][f*3+1],coordinates[id][f*3+2],previousSide);
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
                var metricFrames=groups.Select(g=>Enumerable.Range(0,g.Count/3).Select(f=>{
                    var c=g[f*3];var surface=c.surface;int t=c.corner;
                    return surface.packingFrames!=null?surface.packingFrames[t/3]
                        :ReverseUvTransfer.TriangleFrame(surface.positions[surface.indices[t]],surface.positions[surface.indices[t+1]],surface.positions[surface.indices[t+2]]);
                }).ToArray()).ToArray();
                var intersection=new UvAtlasDiagnostics.IntersectionTest();
                var refusals=new Dictionary<int,string>();
                bool Refuse(int group,string reason)
                {
                    if(!refusals.ContainsKey(group)) refusals.Add(group,reason);
                    return false;
                }
                bool Valid(int group,Vector2[] uv,int side)
                {
                    using var familyTiming=options.performance?.Accumulated("Final family validation");
                    var entries=groups[group];
                    // Keep shared corners connected. Only physically subpixel
                    // faces may spend the existing 0.01-texel precision budget;
                    // visible faces cannot be changed by this repair.
                    if(options.regroupPreparedAtlas)
                    {
                        using var precisionTiming=options.performance?.Accumulated("Final family Float32 stabilization");
                        if(!ReverseUvShellPrecision.Stabilize(coordinates[group],uv,
                            metricFrames[group],options.maxAnisotropy,token)) return false;
                    }
                    for(int f=0;f<metrics[group].Length;++f)
                    {
                        var corner=entries[f*3];var s=corner.surface;int t=corner.corner;
                        double metric=Metric(s,t,uv[f*3],uv[f*3+1],uv[f*3+2],side);
                        if(options.frozenShellCascade && !options.preserveProjectedOverlap
                            && !ReverseUvShellPrecision.Fits(metricFrames[group][f],uv[f*3],uv[f*3+1],uv[f*3+2],options.maxAnisotropy))
                            return Refuse(group,$"face {f}: final texel precision/absolute metric {metric:G6}");
                        if(!(options.regroupPreparedAtlas && ReverseUvShellPrecision.ResolutionLimited(metricFrames[group][f],uv[f*3],uv[f*3+1],uv[f*3+2])) && !MetricFits(metric,metrics[group][f],
                            Altitude(s.pixels[t],s.pixels[t+1],s.pixels[t+2]),
                            Altitude(uv[f*3],uv[f*3+1],uv[f*3+2]),options)) return Refuse(group,$"face {f}: metric {metrics[group][f]:G6} -> {metric:G6}");
                    }
                    foreach(var scanInput in scans[group])
                    {
                        var faceIds=scanInput.faceIds;var geometry=scanInput.geometry;
                        for(int f=0;f<faceIds.Length;++f)
                            for(int k=0;k<3;++k) geometry.uv[f*3+k]=uv[faceIds[f]*3+k]/side;
                        if(faceIds.Length<=24 && !options.regroupPreparedAtlas)
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
                        var scan=UvAtlasDiagnostics.Measure(geometry,token,comparisonBudget:options.comparisonBudget,collectConflicts:true,
                            coordinateOrigin:geometry.uv.Aggregate(Vector2.Min));
                        if(!scan.complete || scan.invalidFaces>0 || scan.degenerateFaces>0)
                            return Refuse(group,$"LOD{entries[faceIds[0]*3].surface.lod}: invalid {scan.invalidFaces}, collapsed {scan.degenerateFaces}, complete {scan.complete}");
                        foreach(var pair in scan.conflicts)
                        {
                            var a=entries[faceIds[pair.a]*3];var b=entries[faceIds[pair.b]*3];
                            if(!allowed.Contains((a.surface.faces[a.corner/3],b.surface.faces[b.corner/3])))
                                return Refuse(group,$"LOD{a.surface.lod}: contact {pair.a}/{pair.b}");
                        }
                    }
                    return true;
                }
                // Level face lists depend only on the families; build them once per pack.
                var levelFaces=levels.Select(level=>groups.SelectMany((g,id)=>Enumerable.Range(0,g.Count/3)
                    .Where(f=>g[f*3].surface.lod==level[0].lod).Select(f=>(id,f,corner:g[f*3]))).ToArray()).ToArray();
                var levelIndices=levelFaces.Select(faces=>Enumerable.Range(0,faces.Length*3).ToArray()).ToArray();
                HashSet<int> AuditAtlas(Vector2[][] placement,int canvas)
                {
                    using var atlasTiming=options.performance?.Accumulated("Final whole-atlas audit");
                    var failed=new HashSet<int>();
                    for(int l=0;l<levelFaces.Length;++l)
                    {
                        var faces=levelFaces[l];
                        if(faces.Length==0) continue;
                        var uv=faces.SelectMany(x=>Enumerable.Range(0,3).Select(k=>placement[x.id][x.f*3+k]/canvas)).ToArray();
                        var min=uv.Aggregate(Vector2.Min);var max=uv.Aggregate(Vector2.Max);
                        double span=Math.Max((double)max.x-min.x,(double)max.y-min.y);
                        var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=uv,
                            indices=levelIndices[l],charts=new int[uv.Length]},token,
                            comparisonBudget:options.comparisonBudget,collectConflicts:true,coordinateOrigin:min,coordinateScale:1/span);
                        if(!scan.complete || scan.invalidFaces>0 || scan.degenerateFaces>0)
                            throw new InvalidOperationException("Classic Repack atlas audit is incomplete or contains invalid faces.");
                        int cross=0,same=0;
                        foreach(var pair in scan.conflicts)
                        {
                            var a=faces[pair.a];var b=faces[pair.b];
                            if(allowed.Contains((a.corner.surface.faces[a.corner.corner/3],b.corner.surface.faces[b.corner.corner/3]))) continue;
                            failed.Add(a.id);failed.Add(b.id);if(a.id==b.id) ++same;else ++cross;
                        }
                        if(same+cross>0) UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Classic placement audit LOD{levels[l][0].lod}: {same} internal / {cross} cross-family contacts; retry {failed.Count} families.");
                    }
                    return failed;
                }
                int side;float scale;
                Vector2[][] packed;bool freshFootprints=false;
                bool footprintImproved=false;var search=new ReverseUvFootprintPacking.Diagnostics();
                if(options.frozenShellCascade && !options.preserveProjectedOverlap)
                {
                    packed=ReverseUvNativeFamilyPacking.TryPack(coordinates,16,options,token,Valid,
                        out scale,out side,allowLargerCanvas:true,fixedScale:1,validateAtlas:AuditAtlas,footprints:footprints,chartSession:chartSession);
                    if(packed==null) throw new InvalidOperationException("Classic frozen-group Repack failed; no atlas or shell fragments were published. "
                        +string.Join("; ",refusals.Take(12).Select(p=>$"family {p.Key} '{groups[p.Key][0].surface.input.key}': {p.Value}")));
                    (packed,side)=AlignAndCompactFrozen(coordinates,groups,options,token,(packed,side,scale),Valid,AuditAtlas);
                    search.nativePacked=true;footprintImproved=true;
                    UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Frozen groups: classic Repack of {groups.Length} families, {side}px, {options.seedDensity*scale:G6} tex/m, final padding {options.padding}px.");
                }
                else if(options.regroupPreparedAtlas)
                {
                    // Every candidate packs all shell groups from scratch.
                    // The projection atlas supplies correspondence, never
                    // fixed slots or a list of vacancies.
                    packed=ReverseUvNativeFamilyPacking.TryPack(coordinates,previousSide,options,token,Valid,
                        out scale,out side,allowLargerCanvas:true,fixedScale:1,validateAtlas:AuditAtlas,footprints:footprints,chartSession:chartSession);
                    if(packed==null)
                        foreach(float density in new[] {.5f,.25f,.125f})
                        {
                            packed=ReverseUvNativeFamilyPacking.TryPack(coordinates,previousSide,options,token,Valid,
                                out scale,out side,allowLargerCanvas:true,fixedScale:density,validateAtlas:AuditAtlas,footprints:footprints,chartSession:chartSession);
                            if(packed!=null) break;
                        }
                    bool nativeSelected=packed!=null;
                    // Different shells favour different packing orders and
                    // resolutions. Compare a complete contour repack with the
                    // native candidate; a smaller density alone cannot win.
                    try
                    {
                        var candidate=ReverseUvFamilyPacking.Pack(coordinates,options,token,out int candidateSide,out float candidateScale,Valid);
                        int completedSide=candidateSide;
                        candidate=ReverseUvFootprintPacking.Improve(coordinates,candidate,candidateSide,candidateScale,
                            options,token,Valid,out candidateScale,out _,search,resized=>completedSide=resized,testNative:false);
                        if((packed==null || BoundsCost(candidate,candidateScale)<BoundsCost(packed,scale))
                            && AuditAtlas(candidate,completedSide).Count==0)
                        {packed=candidate;scale=candidateScale;side=completedSide;nativeSelected=search.nativePacked;}
                    }
                    catch(InvalidOperationException error)
                    {UvtLog.Info(UvtLog.Category.Repack,"[ReverseUV] Alternative complete shell repack refused: "+error.Message);}
                    if(packed==null) throw new InvalidOperationException("Complete shell Repack could not produce an audited atlas.");
                    search.nativePacked=nativeSelected;footprintImproved=true;
                    UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Complete shell Repack: {groups.Length} LOD groups, {side}px, scale {scale:G6}, native {nativeSelected}, {timing.ElapsedMilliseconds} ms.");
                }
                else
                {
                try {packed=ReverseUvFamilyPacking.Pack(coordinates,options,token,out side,out scale,Valid);}
                catch(InvalidOperationException error)
                {
                    report.finalPackingRectangleRefusal=error.Message;
                    packed=ReverseUvFootprintPacking.PackFresh(coordinates,previousSide,options,token,Valid,out side);
                    scale=1;freshFootprints=true;
                    UvtLog.Info(UvtLog.Category.Repack,"[ReverseUV] Rectangle trial refused; packed family footprints on an empty canvas: "+error.Message);
                }
                if(logTiming) UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Family rectangles: {timing.ElapsedMilliseconds} ms. Test triangle footprints.");
                int completedSide=side;
                packed = ReverseUvFootprintPacking.Improve(coordinates,packed,side,scale,options,token,Valid,
                    out scale,out footprintImproved,search,resized=>completedSide=resized);
                side=completedSide;
                }
                if(fill)
                {
                    // A denser atlas only packs tighter on a finer pixel grid: its
                    // gutter grows with the density, so a bake at the requested
                    // density keeps the requested padding between charts. Float
                    // noise in the equal cost of a merely scaled placement never wins.
                    float requested=scale;double cost=BoundsCost(packed,scale);int gutter=options.padding;
                    foreach(float multiple in new[] {1.5f,2f,3f,4f,6f,8f})
                    {
                        float density=requested*multiple;var denser=options.AtDensity(density);
                        var candidate=ReverseUvNativeFamilyPacking.TryPack(coordinates,16,denser,token,Valid,
                            out float candidateScale,out int candidateSide,allowLargerCanvas:true,fixedScale:density,
                            validateAtlas:AuditAtlas,footprints:footprints,chartSession:chartSession);
                        if(candidate==null || BoundsCost(candidate,candidateScale)>=cost*(1-1e-4)) continue;
                        packed=candidate;scale=candidateScale;side=candidateSide;cost=BoundsCost(packed,scale);
                        gutter=denser.padding;search.nativePacked=true;
                    }
                    UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Fill: {scale/requested:G3}x the packed density, {side}px, gutter {gutter}px.");
                }
                if(AuditAtlas(packed,side).Count>0)
                    throw new InvalidOperationException("Complete shared-shell placement contains an unexpected UV overlap.");
                if(logTiming) UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Final family packing: {timing.ElapsedMilliseconds} ms, footprint improvement {footprintImproved}; native {search.nativePacked}; moved {search.tightenedFamilies}; completed orders {search.completedOrders}; search work {search.tighteningWork}/{search.repackingWork}; validations {search.validations}.");
                if(logTiming && search.refusedOrders.Count>0) UvtLog.Info(UvtLog.Category.Repack,"[ReverseUV] Footprint trials: "+string.Join("; ",search.refusedOrders));
                var replacements = surfaces.ToDictionary(s => s,s => new Vector2[s.pixels.Length]);
                for (int g = 0; g < groups.Length; ++g)
                    for (int c = 0; c < groups[g].Count; ++c)
                    {
                        var corner = groups[g][c];
                        corner.surface.faces[corner.corner/3].packingFamily=g;
                        replacements[corner.surface][corner.corner] = packed[g][c];
                    }
                foreach (var surface in surfaces)
                    for (int f = 0; f < surface.faces.Length; ++f)
                    {
                        token.ThrowIfCancellationRequested();
                        int t = f*3; var uv = replacements[surface];
                        var input=surface.pixels;
                        double oldMetric = Metric(surface,t,input[t],input[t+1],input[t+2],previousSide);
                        double newMetric = Metric(surface,t,uv[t],uv[t+1],uv[t+2],side);
                        var frame=surface.packingFrames!=null?surface.packingFrames[f]
                            :ReverseUvTransfer.TriangleFrame(surface.positions[surface.indices[t]],surface.positions[surface.indices[t+1]],surface.positions[surface.indices[t+2]]);
                        bool limited=options.regroupPreparedAtlas && ReverseUvShellPrecision.ResolutionLimited(frame,uv[t],uv[t+1],uv[t+2]);
                        if(limited) ++report.resolutionLimitedFaces;
                        if(limited && !ReverseUvShellPrecision.Fits(frame,uv[t],uv[t+1],uv[t+2],options.maxAnisotropy))
                            throw new InvalidOperationException("Final UV precision adjustment exceeded the 0.01 texel budget.");
                        if (!limited && !MetricFits(newMetric,oldMetric,
                            Altitude(input[t],input[t+1],input[t+2]),Altitude(uv[t],uv[t+1],uv[t+2]),options))
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
            // One name parse per surface, not per face.
            var groupKeys=new Dictionary<ReverseUvTransfer.Surface,string>();
            foreach(var s in surfaces.Where(s=>!groupKeys.ContainsKey(s)))
                groupKeys.Add(s,UvToolContext.ExtractGroupKey(s.input.key));
            foreach(var part in surfaces.SelectMany(s=>Enumerable.Range(0,s.faces.Length).Select(f=>(s,f)))
                .GroupBy(p=>(groupKeys[p.s],p.s.faces[p.f].coordinateDomain)))
            {
                if(part.Select(p=>p.s.lod).Distinct().Count()<2) continue;
                var faces=part.ToArray();
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
