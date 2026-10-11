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
            var conflicts=new Dictionary<Surface,HashSet<int>>();
            foreach(var level in chain)
            {
                var refs=level.SelectMany(s=>Enumerable.Range(0,s.faces.Length).Select(f=>(s,f))).ToArray();
                var scan=UvAtlasDiagnostics.Measure(Atlas(level),token,comparisonBudget:options.comparisonBudget,collectConflicts:true);
                if(!scan.complete) throw new InvalidOperationException("Ordinary Transfer input contact audit exceeded its budget.");
                foreach(var (a,b) in scan.conflicts)
                    foreach(int index in new[] {a,b})
                    {
                        var (s,f)=refs[index];
                        if(!conflicts.TryGetValue(s,out var faces)) {faces=new HashSet<int>();conflicts.Add(s,faces);}
                        faces.Add(f);
                    }
            }
            foreach(var surface in chain.SelectMany(l=>l))
            {
                token.ThrowIfCancellationRequested();
                var bad=Enumerable.Range(0,surface.faces.Length).Where(f=>Anisotropy(surface,f)>options.maxAnisotropy
                    || (conflicts.TryGetValue(surface,out var contacts) && contacts.Contains(f))).ToArray();
                if(bad.Length>0)
                {
                    UvtLog.Info($"[ReverseUV] Prepared input repair LOD{surface.lod} '{surface.input.key}': {bad.Length}/{surface.faces.Length} faces, {density:G6} tex/m.");
                    var positions=bad.SelectMany(f=>Enumerable.Range(0,3).Select(k=>surface.positions[surface.indices[f*3+k]])).ToArray();
                    var frames=bad.Select(f=>surface.metricFrames[f]).ToArray();
                    var repaired=ReverseUvSeedPacking.Prepare(positions,new Vector2[positions.Length],density,true,options,token,out _,frames:frames);
                    repaired=ReverseUvAtlasPlacement.Place(positions,repaired,chain.ToList(),Array.Empty<Surface>(),options,width,height,token,
                        Enumerable.Repeat(true,bad.Length).ToArray(),density,frames);
                    for(int i=0;i<bad.Length;++i)
                    {
                        for(int k=0;k<3;++k) surface.pixels[bad[i]*3+k]=repaired[i*3+k];
                        surface.faces[bad[i]].localFallback=true;
                    }
                    var max=repaired.Aggregate(Vector2.Max);
                    width=Math.Max(width,max.x+options.padding);height=Math.Max(height,max.y+options.padding);
                }
                // Only audited input defects were repaired above. Keep the
                // remaining prepared islands intact even if correspondence
                // later accepts just part of an island.
                AssignCharts(surface,Enumerable.Range(0,surface.faces.Length).ToArray(),ref nextChart);
                surface.preparedRegions=surface.faces.Select(f=>f.chart).ToArray();
                surface.sourceUvCorners=surface.pixels.ToArray();
            }
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
                    for(int c=0;c<surface.pixels.Length;++c) surface.pixels[c]*=scale;
                    surface.sourceUvCorners=surface.pixels.ToArray();
                }
                width*=scale;height*=scale;options.seedDensity=density*scale;
            }
        }

        static void ProjectPreparedChain(Surface[] donor,Surface[] targets,Options options,CancellationToken token)
        {
            var originals=targets.ToDictionary(s=>s,s=>s.pixels.ToArray());
            var originalFaces=targets.ToDictionary(s=>s,s=>s.faces.Select(f=>f.Copy()).ToArray());
            var charts=Charts(donor);var parents=charts.ToDictionary(c=>c.id);long queries=0;
            ProjectFaces(donor,targets,charts,options,ref queries,token);
            var moved=new HashSet<(Surface,int)>();
            foreach(var surface in targets)
                foreach(var group in Enumerable.Range(0,surface.faces.Length).GroupBy(f=>surface.preparedRegions[f]))
                {
                    token.ThrowIfCancellationRequested();
                    var faces=group.ToArray();
                    bool samePlace=faces.All(f=>surface.faces[f].inherited && Enumerable.Range(0,3).All(k=>
                        (surface.pixels[f*3+k]-originals[surface][f*3+k]).magnitude<=options.boundaryToleranceTexels));
                    if(!samePlace && options.preserveProjectedOverlap && TryKeepChartShape(surface,faces,parents,options,token))
                    {moved.Add((surface,group.Key));continue;}
                    foreach(int f in faces)
                    {
                        var face=surface.faces[f];
                        bool matching=face.inherited && Enumerable.Range(0,3).All(k=>
                            (surface.pixels[f*3+k]-originals[surface][f*3+k]).magnitude<=options.boundaryToleranceTexels);
                        for(int k=0;k<3;++k) surface.pixels[f*3+k]=originals[surface][f*3+k];
                        if(!matching) surface.faces[f]=originalFaces[surface][f].Copy();
                    }
                }
            ResolveOverlaps(targets,options,token);
            // A moved overlay must be accepted with every face of its prepared
            // island. All unchanged islands remain in their original shared frame.
            bool changed;
            do
            {
                changed=false;
                var scan=UvAtlasDiagnostics.Measure(Atlas(targets),token,comparisonBudget:options.comparisonBudget,collectConflicts:true);
                if(!scan.complete) throw new InvalidOperationException("Prepared chart contact audit exceeded its budget.");
                var refs=targets.SelectMany(s=>Enumerable.Range(0,s.faces.Length).Select(f=>(s,f))).ToArray();
                var reject=new HashSet<(Surface,int)>();
                foreach(var pair in moved)
                    if(Enumerable.Range(0,pair.Item1.faces.Length).Any(f=>pair.Item1.preparedRegions[f]==pair.Item2 && !pair.Item1.faces[f].inherited)) reject.Add(pair);
                foreach(var (a,b) in scan.conflicts)
                {
                    var x=refs[a];var y=refs[b];
                    if(IsOrderedOverlap(x.s.faces[x.f],y.s.faces[y.f])) continue;
                    if(moved.Contains((x.s,x.s.preparedRegions[x.f]))) reject.Add((x.s,x.s.preparedRegions[x.f]));
                    if(moved.Contains((y.s,y.s.preparedRegions[y.f]))) reject.Add((y.s,y.s.preparedRegions[y.f]));
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
            UvtLog.Info(UvtLog.Category.General,$"[ReverseUV] Regroup prepared Transfer: {targets.Sum(s=>s.preparedRegions.Distinct().Count())} intact islands; {moved.Count} whole projected overlays; no new projection cuts.");
        }
    }
}
