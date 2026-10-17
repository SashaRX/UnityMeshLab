using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvFinalPacking
    {
        static (Vector2[][] packed,int side) AlignAndCompactFrozen(Vector2[][] coordinates,
            List<(ReverseUvTransfer.Surface surface,int corner)>[] groups,ReverseUvTransfer.Options options,CancellationToken token,
            (Vector2[][] packed,int side,float scale) baseline,Func<int,Vector2[],int,bool> validate,
            Func<Vector2[][],int,HashSet<int>> audit)
        {
            // The existing raster search already distinguishes small exact
            // searches from large native workloads at 256 families.
            if(coordinates.Length>256 || !options.rotateCharts || !options.rotateChartsToAxis) return (baseline.packed,baseline.side);
            try
            {
                var aligned=ReverseUvFamilyAxis.Align(coordinates,groups,token);
                if(ReverseUvFootprintPacking.TryPackWithin(aligned,baseline.side,baseline.scale,options,token,validate,
                    out var placement,out int side) && audit(placement,side).Count==0)
                {
                    UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Finest-LOD family axes retained; fixed-density footprint canvas {baseline.side} -> {side}px.");
                    return (placement,side);
                }
            }
            catch(InvalidOperationException error)
            {
                UvtLog.Info(UvtLog.Category.Repack,"[ReverseUV] Aligned fixed-density footprint trial refused: "+error.Message);
            }
            return (baseline.packed,baseline.side);
        }
    }
}
