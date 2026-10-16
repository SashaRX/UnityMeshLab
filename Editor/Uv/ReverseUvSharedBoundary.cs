using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        // A whole-island fit may drift at an exact shared surface vertex.
        // Offer one rigid translation, without moving an accepted neighbour.
        // Ambiguous donor branches and disagreeing boundary anchors stay separate.
        // The caller retains its original anchor, metric and complete contact audits.
        internal static Vector2[] FitFrozenSharedTranslation(Vector2[] trial,Vector3[] positions,
            Vector2[] anchors,bool[] anchored,IReadOnlyDictionary<Vector3,Vector2?> earlier,
            float fitTolerance,float boundaryTolerance,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();
            if(trial.Length!=positions.Length || trial.Length!=anchors.Length || trial.Length!=anchored.Length)
                throw new ArgumentException("Shared-boundary arrays must have matching corner counts.");
            double dx=0,dy=0;bool found=false;
            for(int c=0;c<trial.Length;++c)
            {
                token.ThrowIfCancellationRequested();
                if(!anchored[c] || !earlier.TryGetValue(positions[c],out var value)) continue;
                if(!value.HasValue) return null;
                var target=value.Value;
                if(!(Distance(target,anchors[c])<=fitTolerance)) return null;
                double x=(double)target.x-trial[c].x,y=(double)target.y-trial[c].y;
                if(!found) {dx=x;dy=y;found=true;}
                else if(!(Math.Sqrt((x-dx)*(x-dx)+(y-dy)*(y-dy))<=boundaryTolerance)) return null;
            }
            if(!found || !(Math.Sqrt(dx*dx+dy*dy)<=fitTolerance)) return null;
            var result=new Vector2[trial.Length];
            for(int c=0;c<trial.Length;++c)
            {
                token.ThrowIfCancellationRequested();
                var p=new Vector2((float)(trial[c].x+dx),(float)(trial[c].y+dy));
                if(!Finite(p.x) || !Finite(p.y) || (anchored[c] && !(Distance(p,anchors[c])<=fitTolerance))) return null;
                if(anchored[c] && earlier.TryGetValue(positions[c],out var target)
                    && (!target.HasValue || !(Distance(p,target.Value)<=boundaryTolerance))) return null;
                result[c]=p;
            }
            return result;
        }
    }
}
