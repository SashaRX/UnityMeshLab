using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        internal sealed class FrozenShellPlacement
        {
            internal float x,y,rowHeight,width;
        }

        static void InitializeFrozenShellChain(Surface[][] chain,Options options,Report report,
            ref int nextChart,ref float width,ref float height,CancellationToken token)
        {
            // Ordinary Transfer is an immutable reference for every finer LOD.
            // Only the coarsest foundation is prepared before correspondence.
            foreach(var surface in chain.SelectMany(l=>l))
            {
                var uv=surface.input.mesh.uv2;
                if(!surface.input.usePreparedUv2 || uv.Length!=surface.positions.Length)
                    throw new InvalidOperationException("Frozen reverse requires ordinary Transfer UVs on every LOD.");
                surface.pixels=surface.indices.Select(i=>uv[i]*options.seedResolution).ToArray();
                AssignCharts(surface,Enumerable.Range(0,surface.faces.Length).ToArray(),ref nextChart);
                surface.preparedRegions=surface.faces.Select(f=>f.chart).ToArray();
            }
            float requested=options.seedDensity>0?options.seedDensity:Density(chain[0]);
            foreach(var surface in chain.SelectMany(l=>l))
            {
                var uv=surface.input.mesh.uv2;
                foreach(var island in Enumerable.Range(0,surface.faces.Length).GroupBy(f=>surface.preparedRegions[f]))
                {
                    token.ThrowIfCancellationRequested();
                    var faces=island.ToArray();var corners=faces.SelectMany(f=>Enumerable.Range(f*3,3)).ToArray();
                    // Keep each island in a local frame. A temporary canvas
                    // offset must not round a microscopic UV edge before Repack.
                    var original=corners.Select(c=>uv[surface.indices[c]]).ToArray();
                    var frames=faces.Select(f=>surface.metricFrames[f]).ToArray();
                    var local=NormalizeFrozenDensity(original,frames,requested);
                    if(surface.lod==chain[0][0].lod && !ValidFrozenShell(surface,faces,local,options,token))
                    {
                        var patch=corners.Select(c=>surface.positions[surface.indices[c]]).ToArray();
                        local=RepairPreparedShell(patch,local,frames,options,token);
                        local=NormalizeFrozenDensity(local,frames,requested);
                        foreach(int f in faces) surface.faces[f].localFallback=true;
                    }
                    for(int c=0;c<corners.Length;++c) surface.pixels[corners[c]]=local[c];
                }
                surface.sourceUvCorners=surface.pixels.ToArray();
            }
            options.seedDensity=requested;
            int cuts=0;
            foreach(var surface in chain[0])
            {
                // Optional cuts follow the user's "Cut narrow UV junctions" option, as
                // before Repack; without it every connected foundation island stays whole.
                var regions=(int[])surface.preparedRegions.Clone();
                if(options.cutFoundationJunctions)
                {
                    // A UV cut duplicates a seam at existing edges. The foundation's
                    // positions, triangle order and UV coordinates remain unchanged.
                    var unique=new Dictionary<(Vector3,Vector2),int>();var points=new List<Vector2>();
                    var indices=new int[surface.indices.Length];
                    for(int c=0;c<indices.Length;++c)
                    {
                        var key=(surface.positions[surface.indices[c]],surface.pixels[c]);
                        if(!unique.TryGetValue(key,out int vertex)) {vertex=points.Count;unique.Add(key,vertex);points.Add(key.Item2);}
                        indices[c]=vertex;
                    }
                    var plan=UvJunctionCuts.Find(points.ToArray(),indices,token);cuts+=plan.cuts;regions=plan.regions;
                }
                foreach(var region in Enumerable.Range(0,surface.faces.Length).GroupBy(f=>regions[f]))
                {
                    int chart=nextChart++;
                    var corners=region.SelectMany(f=>Enumerable.Range(f*3,3)).ToArray();
                    var origin=corners.Select(c=>surface.pixels[c]).Aggregate(Vector2.Min);
                    foreach(int c in corners) surface.pixels[c]=new Vector2(
                        (float)((double)surface.pixels[c].x-origin.x),(float)((double)surface.pixels[c].y-origin.y));
                    foreach(int f in region)
                    {
                        surface.faces[f].chart=chart;surface.faces[f].coordinateDomain=chart+1;
                        surface.faces[f].layer=0;surface.preparedRegions[f]=chart;
                    }
                }
            }
            var seedPoints=chain[0].SelectMany(s=>s.pixels).ToArray();
            width=Math.Max(16,seedPoints.Max(p=>p.x));height=Math.Max(16,seedPoints.Max(p=>p.y));
            options.frozenPlacement=new FrozenShellPlacement {x=width+options.padding,
                rowHeight=height,width=Math.Max(1024,options.seedResolution)};
            report.seedPreparation="frozen-forward-last-lod";
            UvtLog.Info(UvtLog.Category.General,$"[ReverseUV] Frozen ordinary Transfer; foundation LOD{chain[0][0].lod}: {cuts} seam edges, {chain[0].Sum(s=>s.preparedRegions.Distinct().Count())} islands. Finer UVs have not been repaired or cut.");
        }

        static void ProjectFrozenShells(Surface[] donor,Surface[] targets,Options options,
            ref int nextChart,ref float width,ref float height,CancellationToken token)
        {
            var originals=targets.ToDictionary(s=>s,s=>s.pixels.ToArray());
            List<Chart> charts;
            using (options.performance?.Measure("Donor chart BVHs", targets[0].lod)) charts=Charts(donor);
            long queries=0;
            // Face hits are anchor proposals only. No face result is published
            // independently, and no finer island is subdivided or seam-cut.
            ProjectFaces(donor,targets,charts,options,ref queries,token);
            using var shellTiming = options.performance?.Measure("Whole-shell matching and grouping", targets[0].lod);
            var proposed=targets.ToDictionary(s=>s,s=>s.pixels.ToArray());
            var records=targets.ToDictionary(s=>s,s=>s.faces.Select(f=>f.Copy()).ToArray());
            var shells=targets.SelectMany(s=>Enumerable.Range(0,s.faces.Length)
                .GroupBy(f=>s.preparedRegions[f]).Select(g=>(surface:s,faces:g.ToArray())))
                .OrderByDescending(p=>p.faces.Sum(f=>p.surface.metricFrames[f].length*p.surface.metricFrames[f].y)).ToArray();
            var accepted=new Dictionary<int,List<Vector2>>();int matches=0,newborn=0;
            foreach(var shell in shells)
            {
                token.ThrowIfCancellationRequested();var s=shell.surface;var faces=shell.faces;
                var corners=faces.SelectMany(f=>Enumerable.Range(f*3,3)).ToArray();
                var frozen=corners.Select(c=>originals[s][c]).ToArray();
                var cornerIndex=corners.Select((corner,index)=>(corner,index)).ToDictionary(p=>p.corner,p=>p.index);
                var refusals=new List<string>();
                // Whole-island correspondence is an approximation between LOD
                // surfaces. Its fit budget follows the permitted geometric reach;
                // the seam/contact budget remains a separate subpixel quantity.
                float fitTolerance=Math.Max(options.boundaryToleranceTexels,
                    options.projectionReach*options.seedDensity);
                bool Match(Vector2[] shape,out Vector2[] placement,out int group)
                {
                    placement=null;group=-1;
                    foreach(var candidate in faces.Where(f=>records[s][f].inherited).GroupBy(f=>records[s][f].chart)
                        .OrderByDescending(g=>g.Sum(f=>s.metricFrames[f].length*s.metricFrames[f].y)).ThenBy(g=>g.Key))
                    {
                        var anchors=candidate.SelectMany(f=>Enumerable.Range(f*3,3)).ToArray();
                        var trial=FitFrozenShell(shape,anchors.Select(c=>shape[cornerIndex[c]]).ToArray(),
                            anchors.Select(c=>proposed[s][c]).ToArray(),fitTolerance,out double residual);
                        if(trial==null) {refusals.Add($"chart {candidate.Key}: anchor residual {residual:G6} px ({candidate.Count()} faces)");continue;}
                        if(!ValidFrozenShell(s,faces,trial,options,token)) {refusals.Add($"chart {candidate.Key}: whole-island quality");continue;}
                        if(accepted.TryGetValue(candidate.Key,out var earlier) && HasFrozenContact(earlier,trial,options,token))
                        {refusals.Add($"chart {candidate.Key}: sibling contact");continue;}
                        placement=trial;group=candidate.Key;return true;
                    }
                    return false;
                }
                Match(frozen,out var placed,out int parent);
                if(placed==null)
                {
                    placed=frozen;
                    if(!ValidFrozenShell(s,faces,placed,options,token))
                    {
                        // Only a genuinely new island is prepared once, before
                        // it is frozen and becomes a donor. Established groups
                        // and their descendants are never repaired afterwards.
                        var patch=corners.Select(c=>s.positions[s.indices[c]]).ToArray();
                        var frames=faces.Select(f=>s.metricFrames[f]).ToArray();
                        placed=RepairPreparedShell(patch,placed,frames,options,token);
                        placed=NormalizeFrozenDensity(placed,frames,options.seedDensity);
                        for(int i=0;i<faces.Length;++i) records[s][faces[i]].localFallback=true;
                    }
                    // A repaired whole island can still have a geometric
                    // donor. Retry correspondence before declaring it new.
                    if(Match(placed,out var repairedMatch,out int repairedParent))
                    {placed=repairedMatch;parent=repairedParent;++matches;}
                    else
                    {
                        placed=PlaceFrozenNewIsland(placed,options,ref width,ref height);
                        parent=nextChart++;++newborn;
                        foreach(int f in faces) {records[s][f].inherited=false;records[s][f].parentLod=-1;
                            records[s][f].parentMesh=records[s][f].parentFace=-1;records[s][f].rejection="new-whole-island";
                            records[s][f].wholeMatchRefusal=refusals.Count==0?"no complete face anchors":string.Join("; ",refusals.Distinct());}
                    }
                }
                else ++matches;
                if(!accepted.TryGetValue(parent,out var occupied)) {occupied=new List<Vector2>();accepted.Add(parent,occupied);}
                occupied.AddRange(placed);
                for(int i=0;i<faces.Length;++i)
                {
                    int f=faces[i];var record=records[s][f];
                    if(record.chart!=parent)
                    {
                        record.inherited=false;record.parentLod=record.parentMesh=record.parentFace=-1;
                        if(record.rejection!="new-whole-island") record.rejection="whole-island-extension";
                    }
                    record.chart=parent;record.coordinateDomain=parent+1;record.layer=record.projectionLayer=0;
                    record.intentionalOverlap=false;record.overlayMesh=record.overlayFace=-1;
                    s.faces[f]=record;
                    for(int k=0;k<3;++k) s.pixels[f*3+k]=placed[i*3+k];
                }
            }
            UvtLog.Info(UvtLog.Category.General,$"[ReverseUV] Frozen cascade LOD{donor[0].lod} -> LOD{targets[0].lod}: {matches} whole-island matches, {newborn} new islands; all become the next donors; {queries} anchor queries. No finer cuts or per-face placement.");
        }

        internal static Vector2[] NormalizeFrozenDensity(Vector2[] uv,
            (double length,double x,double y)[] frames,float density)
        {
            double world=frames.Sum(f=>f.length*f.y),area=0;
            for(int t=0;t<uv.Length;t+=3)
                area+=Math.Abs(((double)uv[t+1].x-uv[t].x)*((double)uv[t+2].y-uv[t].y)
                    -((double)uv[t+1].y-uv[t].y)*((double)uv[t+2].x-uv[t].x));
            if(!(area>0) || !(world>0)) return uv.ToArray();
            double scale=density*Math.Sqrt(world/area);var origin=uv.Aggregate(Vector2.Min);
            return uv.Select(p=>new Vector2((float)(((double)p.x-origin.x)*scale),
                (float)(((double)p.y-origin.y)*scale))).ToArray();
        }

        static Vector2[] FitFrozenShell(Vector2[] whole,Vector2[] source,Vector2[] target,float tolerance,out double residual)
        {
            residual=double.PositiveInfinity;
            double sx=source.Average(p=>(double)p.x),sy=source.Average(p=>(double)p.y);
            double tx=target.Average(p=>(double)p.x),ty=target.Average(p=>(double)p.y);
            Vector2[] best=null;
            foreach(int sign in new[] {1,-1})
            {
                double length=0,dot=0,cross=0;
                for(int i=0;i<source.Length;++i)
                {
                    double x=source[i].x-sx,y=(source[i].y-sy)*sign,u=target[i].x-tx,v=target[i].y-ty;
                    length+=x*x+y*y;dot+=x*u+y*v;cross+=x*v-y*u;
                }
                if(length<=0) continue;
                double a=dot/length,b=cross/length;
                Vector2 Move(Vector2 p) {double x=p.x-sx,y=(p.y-sy)*sign;
                    return new Vector2((float)(a*x-b*y+tx),(float)(b*x+a*y+ty));}
                double error=source.Select((p,i)=>Distance(Move(p),target[i])).Max();
                if(error<residual) {residual=error;best=whole.Select(Move).ToArray();}
            }
            return residual<=tolerance?best:null;
        }

        static double Distance(Vector2 a,Vector2 b)
        {double x=(double)a.x-b.x,y=(double)a.y-b.y;return Math.Sqrt(x*x+y*y);}

        static bool ValidFrozenShell(Surface surface,int[] faces,Vector2[] uv,Options options,CancellationToken token)
        {
            for(int i=0;i<faces.Length;++i)
                if(!ReverseUvShellPrecision.Fits(surface.metricFrames[faces[i]],uv[i*3],uv[i*3+1],uv[i*3+2],options.maxAnisotropy)) return false;
            var audit=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=uv,
                indices=Enumerable.Range(0,uv.Length).ToArray(),charts=new int[uv.Length]},token,comparisonBudget:options.comparisonBudget);
            if(!audit.complete) throw new InvalidOperationException("Frozen shell audit exceeded its comparison budget.");
            return audit.invalidFaces==0 && audit.degenerateFaces==0 && audit.pairs==0;
        }

        static bool HasFrozenContact(List<Vector2> earlier,Vector2[] candidate,Options options,CancellationToken token)
        {
            var points=earlier.Concat(candidate).ToArray();
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=points,
                indices=Enumerable.Range(0,points.Length).ToArray(),charts=new int[points.Length]},token,comparisonBudget:options.comparisonBudget);
            if(!scan.complete) throw new InvalidOperationException("Frozen group contact audit exceeded its comparison budget.");
            return scan.pairs>0;
        }

        static Vector2[] PlaceFrozenNewIsland(Vector2[] uv,Options options,ref float width,ref float height)
        {
            var minimum=uv.Aggregate(Vector2.Min);var maximum=uv.Aggregate(Vector2.Max);var size=maximum-minimum;
            var cursor=options.frozenPlacement;
            if(cursor.x+size.x>cursor.width && cursor.x>0) {cursor.x=0;cursor.y+=cursor.rowHeight+options.padding;cursor.rowHeight=0;}
            // Placement is deferred until all families are known. The virtual
            // cursor records capacity; actual UVs retain their precise local frame.
            var result=uv.Select(p=>new Vector2((float)((double)p.x-minimum.x),
                (float)((double)p.y-minimum.y))).ToArray();
            cursor.x+=size.x+options.padding*2;cursor.rowHeight=Math.Max(cursor.rowHeight,size.y+options.padding);
            width=Math.Max(width,cursor.x);height=Math.Max(height,cursor.y+cursor.rowHeight);
            return result;
        }
    }
}
