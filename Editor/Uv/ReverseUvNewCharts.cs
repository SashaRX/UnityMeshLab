using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class ReverseUvNewCharts
    {
        /// <summary>Keep the connected unwrap where it is valid. Bad local faces
        /// get their exact intrinsic triangle metric in separate padded shelves.
        /// This rescue never relocates inherited UVs or squashes a triangle to fit.</summary>
        internal static Vector2[] Prepare(Vector3[] corners, Vector2[] uv, float density,
            ReverseUvTransfer.Options options, CancellationToken token, out bool[] fallback)
        {
            fallback = new bool[corners.Length / 3];
            for (int f = 0; f < fallback.Length; ++f)
            {
                int t = f * 3;
                fallback[f] = ReverseUvTransfer.TriangleAnisotropy(corners[t],corners[t+1],corners[t+2],uv[t],uv[t+1],uv[t+2]) > options.maxAnisotropy;
            }
            var scan = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry { uv = uv,
                indices = Enumerable.Range(0, uv.Length).ToArray(), charts = new int[uv.Length] }, token,
                comparisonBudget: options.comparisonBudget, collectConflicts:true);
            if (!scan.complete) throw new InvalidOperationException("New reverse chart validation exceeded its budget.");
            foreach (var (a,b) in scan.conflicts) { fallback[a]=true; fallback[b]=true; }
            double worldArea = 0, uvArea = 0;
            var min = new Vector2(float.MaxValue,float.MaxValue); var max = -min;
            for (int f = 0; f < fallback.Length; ++f)
            {
                if (fallback[f]) continue;
                int t=f*3;
                worldArea+=Vector3.Cross(corners[t+1]-corners[t],corners[t+2]-corners[t]).magnitude;
                uvArea+=Math.Abs(Cross(uv[t+1]-uv[t],uv[t+2]-uv[t]));
                for(int k=0;k<3;++k) { min=Vector2.Min(min,uv[t+k]); max=Vector2.Max(max,uv[t+k]); }
            }
            float scale=uvArea>0?(float)(density*Math.Sqrt(worldArea/uvArea)):0;
            var output=new Vector2[uv.Length];
            for(int f=0;f<fallback.Length;++f)
                if(!fallback[f]) for(int k=0;k<3;++k) output[f*3+k]=(uv[f*3+k]-min)*scale;
            float left=uvArea>0?(max.x-min.x)*scale+options.padding:0;
            var rescue=new List<(int face,Vector2 b,Vector2 c,float minX,float width,float height)>();
            double rectangles=0;
            for(int f=0;f<fallback.Length;++f)
            {
                if(!fallback[f]) continue;
                int t=f*3; var e=corners[t+1]-corners[t]; var d=corners[t+2]-corners[t];
                float length=e.magnitude; var b=new Vector2(length*density,0);
                var c=new Vector2(Vector3.Dot(e,d)/length,Vector3.Cross(e,d).magnitude/length)*density;
                float minX=Math.Min(0,c.x),width=Math.Max(b.x,c.x)-minX;
                rescue.Add((f,b,c,minX,width,c.y)); rectangles+=(width+options.padding)*(c.y+options.padding);
            }
            float shelfWidth=(float)Math.Sqrt(rectangles*1.25),x=0,y=0,rowHeight=0;
            foreach(var rect in rescue.OrderByDescending(r=>r.height).ThenBy(r=>r.face))
            {
                token.ThrowIfCancellationRequested();
                if(x>0 && x+rect.width>shelfWidth) { y+=rowHeight+options.padding; x=0; rowHeight=0; }
                var offset=new Vector2(left+x-rect.minX,y);
                int t=rect.face*3; output[t]=offset; output[t+1]=rect.b+offset; output[t+2]=rect.c+offset;
                x+=rect.width+options.padding; rowHeight=Math.Max(rowHeight,rect.height);
            }
            return output;
        }
        static double Cross(Vector2 a,Vector2 b)=>(double)a.x*b.y-(double)a.y*b.x;
    }
}
