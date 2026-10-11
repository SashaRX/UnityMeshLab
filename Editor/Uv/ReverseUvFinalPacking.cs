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
                var packed = ReverseUvSeedPacking.PackRigid(coordinates,options,token,out int side,out float scale);
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
                        var a = surface.positions[surface.indices[t]];
                        var b = surface.positions[surface.indices[t+1]];
                        var c = surface.positions[surface.indices[t+2]];
                        double oldMetric = ReverseUvTransfer.TriangleAnisotropy(a,b,c,surface.pixels[t],surface.pixels[t+1],surface.pixels[t+2]);
                        double newMetric = ReverseUvTransfer.TriangleAnisotropy(a,b,c,uv[t],uv[t+1],uv[t+2]);
                        if (double.IsNaN(newMetric) || double.IsInfinity(newMetric) || newMetric > options.maxAnisotropy || newMetric > oldMetric*1.001+.001)
                            throw new InvalidOperationException($"Final packing cannot preserve LOD{surface.lod} face {f} at float precision.");
                    }
                foreach (var surface in surfaces) surface.pixels = replacements[surface];
                report.texelsPerUnit *= scale;
                report.finalPacking = "shared-chart-families";
                return side;
            }
            catch (InvalidOperationException error)
            {
                report.finalPackingRefusal = error.Message;
                UvtLog.Warn(UvtLog.Category.Repack,"[ReverseUV] Final chart packing retained validated coordinates: "+error.Message);
                return previousSide;
            }
        }
    }
}
