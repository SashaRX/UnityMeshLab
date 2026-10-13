using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        // Fit one similarity to the complete prepared island. A per-face nearest
        // point result is only a correspondence proposal, never a new unwrap.
        static bool TryKeepChartShape(Surface surface,int[] faces,Dictionary<int,Chart> parents,
            Options options,CancellationToken token)
        {
            var first=surface.faces[faces[0]];
            if(!first.inherited || faces.Any(f=>!surface.faces[f].inherited
                || surface.faces[f].chart!=first.chart || surface.faces[f].parentLod!=first.parentLod
                || surface.faces[f].projectionLayer!=first.projectionLayer)) return false;
            if(!parents.TryGetValue(first.chart,out var parent)) return false;
            var corners=faces.SelectMany(f=>Enumerable.Range(0,3).Select(k=>f*3+k)).ToArray();
            var source=corners.Select(c=>surface.sourceUvCorners[c]).ToArray();
            var target=corners.Select(c=>surface.pixels[c]).ToArray();
            var fitted=FitChart(source,target,options.boundaryToleranceTexels);
            if(fitted==null) return false;
            long comparisons=0;
            for(int i=0;i<faces.Length;++i)
            {
                token.ThrowIfCancellationRequested();int t=i*3;
                if(TriangleAnisotropy(surface.metricFrames[faces[i]],fitted[t],fitted[t+1],fitted[t+2])>options.maxAnisotropy
                    || !CoveredByParentLayer(parent,first.parentLod==surface.lod?first.projectionLayer-1:first.projectionLayer,
                        fitted,t,options,ref comparisons,token)) return false;
                if(Math.Sign(Cross(fitted[t+1]-fitted[t],fitted[t+2]-fitted[t]))
                    !=Math.Sign(Cross(target[t+1]-target[t],target[t+2]-target[t]))) return false;
            }
            for(int i=0;i<corners.Length;++i) surface.pixels[corners[i]]=fitted[i];
            return true;
        }

        static Vector2[] FitChart(Vector2[] source,Vector2[] target,float tolerance)
        {
            double sx=source.Average(p=>(double)p.x),sy=source.Average(p=>(double)p.y);
            double tx=target.Average(p=>(double)p.x),ty=target.Average(p=>(double)p.y);
            Vector2[] best=null;double bestError=double.PositiveInfinity;
            foreach(int sign in new[] {1,-1})
            {
                double length=0,dot=0,cross=0;
                for(int i=0;i<source.Length;++i)
                {
                    double x=source[i].x-sx,y=(source[i].y-sy)*sign;
                    double u=target[i].x-tx,v=target[i].y-ty;
                    length+=x*x+y*y;dot+=x*u+y*v;cross+=x*v-y*u;
                }
                if(length==0) continue;
                double a=dot/length,b=cross/length,error=0;
                var output=new Vector2[source.Length];
                for(int i=0;i<source.Length;++i)
                {
                    double x=source[i].x-sx,y=(source[i].y-sy)*sign;
                    output[i]=new Vector2((float)(a*x-b*y+tx),(float)(b*x+a*y+ty));
                    error=Math.Max(error,(output[i]-target[i]).magnitude);
                }
                if(error<=tolerance && error<bestError) {best=output;bestError=error;}
            }
            return best;
        }
    }
}
