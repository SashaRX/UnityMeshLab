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
            fallback = Classify(corners, uv, options, token);
            double worldArea = 0, uvArea = 0;
            var min = new Vector2(float.MaxValue,float.MaxValue); var max = -min;
            for (int f = 0; f < fallback.Length; ++f)
            {
                if (fallback[f]) continue;
                int t=f*3;
                var frame=ReverseUvTransfer.TriangleFrame(corners[t],corners[t+1],corners[t+2]);
                worldArea+=frame.length*frame.y;
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
                int t=f*3;
                var frame=ReverseUvTransfer.TriangleFrame(corners[t],corners[t+1],corners[t+2]);
                var b=new Vector2((float)(frame.length*density),0);
                var c=new Vector2((float)(frame.x*density),(float)(frame.y*density));
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
            return options.cutNarrowJunctions ? UvJunctionCuts.Pack(corners, output, options.padding, token,
                options.rotateCharts && options.rotateChartsToAxis) : output;
        }

        internal static bool[] Classify(Vector3[] corners, Vector2[] uv, ReverseUvTransfer.Options options, CancellationToken token)
        {
            var fallback = new bool[corners.Length / 3];
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
            return fallback;
        }
        /// <summary>Reconstruct only failed independent rescue triangles at their
        /// final atlas location. Float32 rounding depends on the translation;
        /// try cyclic bases and sub-ULP rigid shifts inside the reserved texel box.
        /// Geometry, density, inherited UVs and the stretch limit stay fixed.</summary>
        internal static void Stabilize(Vector3[] corners, Vector2[] pixels, bool[] fallback,
            float density, float maxAnisotropy, float texel = 1, CancellationToken token = default,
            bool reserveCollapsedFootprint = false)
        {
            for(int f=0;f<fallback.Length;++f)
            {
                token.ThrowIfCancellationRequested();
                int t=f*3;
                if(!fallback[f] || Metric(corners,pixels,t)<=maxAnisotropy) continue;
                var min=Vector2.Min(pixels[t],Vector2.Min(pixels[t+1],pixels[t+2]));
                var max=Vector2.Max(pixels[t],Vector2.Max(pixels[t+1],pixels[t+2]));
                double width=Math.Ceiling(((double)max.x-min.x)/texel)*texel;
                double height=Math.Ceiling(((double)max.y-min.y)/texel)*texel;
                if(reserveCollapsedFootprint) {width=Math.Max(texel,width);height=Math.Max(texel,height);}
                // Search less than one Float32 spacing at the largest coordinate.
                double step=Math.Pow(2,Math.Floor(Math.Log(Math.Max(1,Math.Max(Math.Abs(max.x),Math.Abs(max.y))),2))-23);
                double best=maxAnisotropy; Vector2[] selected=null;
                for(int basis=0;basis<3;++basis)
                {
                    var frame=ReverseUvTransfer.TriangleFrame(corners[t+basis],corners[t+(basis+1)%3],corners[t+(basis+2)%3]);
                    var local=new (double x,double y)[3];
                    local[(basis+1)%3]=(frame.length*density,0);
                    local[(basis+2)%3]=(frame.x*density,frame.y*density);
                    for(int turn=0;turn<4;++turn)
                    {
                        double sign=turn<2?1:-1;
                        var rotated=local.Select(p=>turn%2==0?(x:p.x*sign,y:p.y*sign):(x:-p.y*sign,y:p.x*sign)).ToArray();
                        double left=rotated.Min(p=>p.x),bottom=rotated.Min(p=>p.y);
                        bool vertical=rotated.Max(p=>p.y)-bottom>rotated.Max(p=>p.x)-left;
                        for(int phase=0;phase<16;++phase)
                        {
                            var candidate=new Vector2[3]; bool fits=true;
                            for(int k=0;k<3;++k)
                            {
                                double shift=step*phase/16;
                                candidate[k]=new Vector2((float)(rotated[k].x-left+min.x+(vertical?0:shift)),
                                    (float)(rotated[k].y-bottom+min.y+(vertical?shift:0)));
                                if(candidate[k].x<min.x || candidate[k].x>min.x+width
                                    || candidate[k].y<min.y || candidate[k].y>min.y+height) fits=false;
                            }
                            if(!fits) continue;
                            double quality=ReverseUvTransfer.TriangleAnisotropy(corners[t],corners[t+1],corners[t+2],candidate[0],candidate[1],candidate[2]);
                            if(quality>=best) continue;
                            best=quality; selected=candidate;
                        }
                    }
                }
                if(selected!=null) Array.Copy(selected,0,pixels,t,3);
            }
        }

        static double Metric(Vector3[] corners,Vector2[] pixels,int t)=>ReverseUvTransfer.TriangleAnisotropy(
            corners[t],corners[t+1],corners[t+2],pixels[t],pixels[t+1],pixels[t+2]);

        static double Cross(Vector2 a,Vector2 b)=>(double)a.x*b.y-(double)a.y*b.x;
    }
}
