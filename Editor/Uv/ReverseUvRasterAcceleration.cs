using System;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // The optional Burst assembly registers a pure bulk evaluator. Core code
    // does not reference Burst/Collections and always retains its scalar path.
    internal static class ReverseUvRasterAcceleration
    {
        internal struct Triangle
        {
            internal Vector2 a,b,c;
            internal int firstRow,lastRow,outputStart;
        }
        internal struct Span
        {
            internal int row,start,end;
        }
        internal sealed class Output
        {
            internal Span[] spans;
            internal bool burstExecuted;
        }
        internal static Func<Triangle[],int,int,double,CancellationToken,Output> evaluate;
        internal static bool Available => evaluate!=null;
        internal static string LastRefusal;
        internal static Func<IDisposable> beginTestScope;

        internal static Output TryRasterize(Vector2[] local,int width,int height,double radius,CancellationToken token)
        {
            var builder=evaluate;
            if(builder==null || local.Length<96) return null;
            var triangles=new Triangle[local.Length/3];long capacity=0;
            for(int t=0;t<triangles.Length;++t)
            {
                token.ThrowIfCancellationRequested();var a=local[t*3];var b=local[t*3+1];var c=local[t*3+2];
                int lo=Math.Max(0,(int)Math.Floor(Math.Min(a.y,Math.Min(b.y,c.y))-radius));
                int hi=Math.Min(height,(int)Math.Ceiling(Math.Max(a.y,Math.Max(b.y,c.y))+radius));
                triangles[t]=new Triangle {a=a,b=b,c=c,firstRow=lo,lastRow=hi,outputStart=(int)capacity};
                capacity+=Math.Max(0,hi-lo);
                // Bound temporary storage; large masks keep scalar rasterization.
                if(capacity>2000000) return null;
            }
            if(capacity==0) return null;
            var result=builder(triangles,(int)capacity,width,radius,token);
            if(result==null || !result.burstExecuted) return null;
            if(result.spans==null || result.spans.Length!=capacity) throw new InvalidOperationException("Burst raster output length mismatch.");
            return result;
        }
    }
}
