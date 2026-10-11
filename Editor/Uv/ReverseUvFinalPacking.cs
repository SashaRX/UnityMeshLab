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
            var surfaces = levels.SelectMany(l => l).ToArray();
            int count = surfaces.SelectMany(s => s.faces).Max(f => f.chart) + 1;
            var families = new DisjointSet(count);
            var byLod=levels.ToDictionary(l=>l[0].lod);
            foreach(var surface in surfaces)
                foreach(var face in surface.faces)
                    if(face.inherited && face.parentLod>surface.lod)
                    {
                        token.ThrowIfCancellationRequested();
                        var parent=byLod[face.parentLod][face.parentMesh].faces[face.parentFace];
                        families.Union(face.chart,parent.chart);
                    }
            foreach (var level in levels)
                foreach (var relation in report.overlaps.Where(r => r.lod == level[0].lod))
                    families.Union(level[relation.underMesh].faces[relation.underFace].chart,
                        level[relation.overMesh].faces[relation.overFace].chart);
            var corners = new SortedDictionary<int,List<(ReverseUvTransfer.Surface surface,int corner)>>();
            foreach (var surface in surfaces)
                for (int f = 0; f < surface.faces.Length; ++f)
                {
                    token.ThrowIfCancellationRequested();
                    int family = families.Find(surface.faces[f].chart);
                    if (!corners.TryGetValue(family,out var list))
                    { list = new List<(ReverseUvTransfer.Surface,int)>(); corners.Add(family,list); }
                    for (int k = 0; k < 3; ++k) list.Add((surface,f*3+k));
                }
            try
            {
                var groups = corners.Values.ToArray();
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
                        if(double.IsNaN(metric) || double.IsInfinity(metric) || metric>options.maxAnisotropy
                            || !UvTransferTolerance.MetricFits(metric,metrics[group][f],options.relativeStretchTolerance)) return false;
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
                                if(intersection.Degenerate(geometry,a)) return false;
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
                var packed = ReverseUvFamilyPacking.Pack(coordinates,options,token,out int side,out float scale,Valid);
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
                        if (double.IsNaN(newMetric) || double.IsInfinity(newMetric) || newMetric > options.maxAnisotropy
                            || !UvTransferTolerance.MetricFits(newMetric,oldMetric,options.relativeStretchTolerance))
                            throw new InvalidOperationException($"Final packing cannot preserve LOD{surface.lod} '{surface.input?.key}' face {f} at float precision (anisotropy {oldMetric:G6} -> {newMetric:G6}).");
                    }
                foreach (var surface in surfaces) surface.pixels = replacements[surface];
                report.texelsPerUnit *= scale;
                report.finalPacking = "shared-chart-families";
                report.finalPackingFamilies=groups.Length;
                report.finalPackingScale=scale;
                return side;
            }
            catch (InvalidOperationException error)
            {
                report.finalPackingRefusal = error.Message;
                UvtLog.Warn(UvtLog.Category.Repack,"[ReverseUV] Final chart packing retained validated coordinates: "+error.Message);
                return previousSide;
            }
        }

        static double Metric(ReverseUvTransfer.Surface surface,int corner,Vector2 a,Vector2 b,Vector2 c,int side)
            => surface.packingEdges!=null
                ?TransferUvQuality.TriangleAnisotropy(surface.packingEdges[corner/3*2],surface.packingEdges[corner/3*2+1],a/side,b/side,c/side)
                :ReverseUvTransfer.TriangleAnisotropy(surface.positions[surface.indices[corner]],
                    surface.positions[surface.indices[corner+1]],surface.positions[surface.indices[corner+2]],a,b,c);
    }
}
