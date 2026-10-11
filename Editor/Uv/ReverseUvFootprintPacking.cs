using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Pack the union of every descendant triangle in a family. Empty
    /// space inside a ring or branch is available to unrelated padded families;
    /// overlays never become independent packing items.</summary>
    internal static class ReverseUvFootprintPacking
    {
        internal sealed class Diagnostics
        {
            internal long tighteningWork, repackingWork, validations;
            internal int tightenedFamilies, completedOrders;
            internal List<string> refusedOrders=new List<string>();
        }

        sealed class Mask
        {
            internal int id, width, height;
            internal Vector2[] local;
            internal List<(int start,int end)>[] rows;
        }

        internal static Vector2[][] Improve(Vector2[][] source, Vector2[][] baseline, int side, float scale,
            ReverseUvTransfer.Options options, CancellationToken token, Func<int,Vector2[],int,bool> validate,
            out float finalScale, out bool improved, Diagnostics diagnostics=null)
        {
            finalScale=scale; improved=false;
            var masks=source.Select((points,id)=>Enumerable.Range(0,options.rotateCharts?4:1)
                .Select(turn=>Raster(points,id,turn,scale,options.padding,token)).ToArray()).ToArray();
            var orders=new[] {
                masks.OrderBy(m=>ReverseUvFamilyPacking.MinimumAltitude(source[m[0].id])<.0001?0:1)
                    .ThenBy(m=>ReverseUvFamilyPacking.MinimumAltitude(source[m[0].id])<.0001
                        ?ReverseUvFamilyPacking.MinimumAltitude(source[m[0].id]):0)
                    .ThenByDescending(m=>Math.Max(m[0].width,m[0].height)).ThenBy(m=>m[0].id).ToArray(),
                masks.OrderByDescending(m=>Math.Max(m[0].width,m[0].height)).ThenBy(m=>m[0].id).ToArray(),
                masks.OrderByDescending((Mask[] m)=>m[0].rows.Sum((List<(int start,int end)> r)=>
                    r.Sum(((int start,int end) span)=>span.end-span.start))).ThenBy(m=>m[0].id).ToArray()
            };
            var best=baseline; double bestArea=BoundsArea(best);
            long work=0,validations=0;
            var tightened=Tighten(baseline,masks,side,options,token,validate,ref work,ref validations);
            if(diagnostics!=null)
            {
                diagnostics.tighteningWork=work;
                diagnostics.tightenedFamilies=tightened==null?0:Enumerable.Range(0,baseline.Length)
                    .Count(g=>!baseline[g].SequenceEqual(tightened[g]));
            }
            if(tightened!=null && BoundsArea(tightened)<bestArea)
            {best=tightened;bestArea=BoundsArea(best);improved=true;}
            foreach(var order in orders.GroupBy(o=>string.Join(",",o.Select(m=>m[0].id))).Select(g=>g.First()))
            {
                token.ThrowIfCancellationRequested();
                // Each alternative starts a new search. The preliminary local
                // tightening must not consume the entire fresh packing budget.
                long packingWork=0,packingValidations=0;
                var packed=TryPack(order,side,options,token,validate,ref packingWork,ref packingValidations,diagnostics);
                work+=packingWork;validations+=packingValidations;
                if(packed==null) continue;
                if(diagnostics!=null) ++diagnostics.completedOrders;
                double area=BoundsArea(packed);
                if(area>=bestArea) continue;
                bestArea=area;best=packed;improved=true;
            }
            if(diagnostics!=null) {diagnostics.repackingWork=work-diagnostics.tighteningWork;diagnostics.validations=validations;}
            if(!improved || !options.fillFinalAtlas) return best;
            var pointsAll=best.SelectMany(p=>p).ToArray();var min=pointsAll.Aggregate(Vector2.Min);var max=pointsAll.Aggregate(Vector2.Max);
            double fit=(side-options.padding*2)/Math.Max((double)max.x-min.x,(double)max.y-min.y);
            if(fit<=1) return best;
            var filled=best.Select(points=>points.Select(p=>new Vector2((float)(((double)p.x-min.x)*fit+options.padding),
                (float)(((double)p.y-min.y)*fit+options.padding))).ToArray()).ToArray();
            for(int group=0;group<filled.Length;++group)
                if(validate!=null && !validate(group,filled[group],side)) return best;
            finalScale=(float)(scale*fit);
            return filled;
        }

        // Start from the already validated family placement. A failed move of
        // one thin family must not discard the compaction of every other family.
        // Counts permit removing one footprint even when conservative gutters
        // of two valid baseline families share raster cells.
        static Vector2[][] Tighten(Vector2[][] baseline,Mask[][] variants,int side,ReverseUvTransfer.Options options,
            CancellationToken token,Func<int,Vector2[],int,bool> validate,ref long work,ref long validations)
        {
            if((long)side*side>16777216) return null;
            var counts=new int[side*side];
            long rasterWork=0;
            var rows=Enumerable.Range(0,side).Select(_=>new List<(int start,int end)>()).ToArray();
            var output=baseline.ToArray();
            var placed=new (Mask mask,int x,int y)[baseline.Length];
            for(int id=0;id<baseline.Length;++id)
            {
                token.ThrowIfCancellationRequested();
                var min=baseline[id].Aggregate(Vector2.Min);
                placed[id]=(Raster(baseline[id],id,0,1,options.padding,token),
                    (int)Math.Floor(min.x)-options.padding,(int)Math.Floor(min.y)-options.padding);
                Occupy(placed[id],1,false);
                if(work+rasterWork>128000000) {work+=rasterWork;return null;}
            }
            for(int y=0;y<side;++y) Refresh(y);
            var order=Enumerable.Range(0,baseline.Length).OrderByDescending(id=>baseline[id].Max(p=>Math.Max(p.x,p.y)))
                .ThenByDescending(id=>baseline[id].Max(p=>p.x)*baseline[id].Max(p=>p.y)).ThenBy(id=>id).ToArray();
            var globalMax=baseline.SelectMany(p=>p).Aggregate(Vector2.Max);
            foreach(int id in order)
            {
                token.ThrowIfCancellationRequested();
                if(work+rasterWork>128000000) break;
                var old=placed[id];Occupy(old,-1,true);
                var previousMax=output[id].Aggregate(Vector2.Max);
                float bestScore=Math.Max(previousMax.x,previousMax.y);Vector2[] selected=null;
                bool Valid(int group,Vector2[] points,int atlas)
                {
                    var max=points.Aggregate(Vector2.Max);
                    return max.x<=globalMax.x && max.y<=globalMax.y && Math.Max(max.x,max.y)<bestScore
                        && (validate==null || validate(group,points,atlas));
                }
                foreach(var mask in variants[id])
                {
                    work+=rasterWork;rasterWork=0;
                    if(!Find(mask,rows,options,token,Valid,ref work,ref validations,
                        Math.Max(8192,baseline.Length*8L),out _,out _,out var points)) continue;
                    var max=points.Aggregate(Vector2.Max);bestScore=Math.Max(max.x,max.y);selected=points;
                }
                if(selected!=null)
                {
                    output[id]=selected;var min=selected.Aggregate(Vector2.Min);
                    placed[id]=(Raster(selected,id,0,1,options.padding,token),
                        (int)Math.Floor(min.x)-options.padding,(int)Math.Floor(min.y)-options.padding);
                }
                Occupy(placed[id],1,true);
            }
            work+=rasterWork;return output;

            void Occupy((Mask mask,int x,int y) placement,int delta,bool refresh)
            {
                for(int r=0;r<placement.mask.height;++r)
                {
                    int y=placement.y+r;if(y<0 || y>=side) continue;
                    foreach(var span in placement.mask.rows[r])
                        for(int x=Math.Max(0,placement.x+span.start);x<Math.Min(side,placement.x+span.end);++x)
                        {counts[y*side+x]+=delta;++rasterWork;}
                    if(refresh) Refresh(y);
                }
            }
            void Refresh(int y)
            {
                var spans=rows[y];spans.Clear();int start=-1;
                for(int x=0;x<=side;++x)
                {
                    bool occupied=x<side && counts[y*side+x]>0;
                    if(occupied && start<0) start=x;
                    else if(!occupied && start>=0) {spans.Add((start,x));start=-1;}
                }
                rasterWork+=side;
            }
        }

        static double BoundsArea(Vector2[][] coordinates)
        {
            var points=coordinates.SelectMany(p=>p).ToArray();var min=points.Aggregate(Vector2.Min);var max=points.Aggregate(Vector2.Max);
            return ((double)max.x-min.x)*((double)max.y-min.y);
        }

        static Mask Raster(Vector2[] points,int id,int turn,double scale,int padding,CancellationToken token)
        {
            var min=points.Aggregate(Vector2.Min);var max=points.Aggregate(Vector2.Max);
            double width=(double)max.x-min.x,height=(double)max.y-min.y;
            var local=points.Select(p=>{
                double x=(double)p.x-min.x,y=(double)p.y-min.y,old=x;
                if(turn==1) {x=height-y;y=old;}
                else if(turn==2) {x=width-x;y=height-y;}
                else if(turn==3) {x=y;y=width-old;}
                return new Vector2((float)(x*scale+padding),(float)(y*scale+padding));
            }).ToArray();
            int w=(int)Math.Ceiling((turn%2==0?width:height)*scale)+padding*2+2;
            int h=(int)Math.Ceiling((turn%2==0?height:width)*scale)+padding*2+2;
            var rows=Enumerable.Range(0,h).Select(_=>new List<(int start,int end)>()).ToArray();
            // Each family contributes half the requested gutter. One additional
            // texel conservatively contains subpixel placement phases and edges.
            double radius=padding*.5+1;
            for(int t=0;t<local.Length;t+=3)
            {
                token.ThrowIfCancellationRequested();
                var triangle=new[] {local[t],local[t+1],local[t+2]};
                int lo=Math.Max(0,(int)Math.Floor(triangle.Min(p=>p.y)-radius));
                int hi=Math.Min(h,(int)Math.Ceiling(triangle.Max(p=>p.y)+radius));
                for(int y=lo;y<hi;++y)
                {
                    double bottom=y-radius,top=y+1+radius,left=double.MaxValue,right=double.MinValue;
                    foreach(var p in triangle) if(p.y>=bottom && p.y<=top) {left=Math.Min(left,p.x);right=Math.Max(right,p.x);}
                    for(int e=0;e<3;++e)
                    {
                        var a=triangle[e];var b=triangle[(e+1)%3];double rise=(double)b.y-a.y;
                        if(rise==0) continue;
                        foreach(double v in new[] {bottom,top})
                        {
                            double fraction=(v-a.y)/rise;
                            if(fraction<0 || fraction>1) continue;
                            double x=a.x+((double)b.x-a.x)*fraction;
                            left=Math.Min(left,x);right=Math.Max(right,x);
                        }
                    }
                    if(left<=right) Reserve(rows[y],Math.Max(0,(int)Math.Floor(left-radius)),Math.Min(w,(int)Math.Ceiling(right+radius)));
                }
            }
            return new Mask {id=id,width=w,height=h,local=local,rows=rows};
        }

        static Vector2[][] TryPack(Mask[][] order,int side,ReverseUvTransfer.Options options,CancellationToken token,
            Func<int,Vector2[],int,bool> validate,ref long work,ref long validations,Diagnostics diagnostics)
        {
            var rows=Enumerable.Range(0,side).Select(_=>new List<(int start,int end)>()).ToArray();
            var output=new Vector2[order.Length][];
            foreach(var variants in order)
            {
                token.ThrowIfCancellationRequested();
                Mask selected=null;Vector2[] placed=null;int left=0,bottom=0,best=int.MaxValue;
                foreach(var mask in variants)
                {
                    if(!Find(mask,rows,options,token,validate,ref work,ref validations,
                        Math.Max(8192,order.Length*8L),out int x,out int y,out var points)) continue;
                    int score=Math.Max(x+mask.width,y+mask.height);
                    if(score>=best) continue;
                    selected=mask;placed=points;left=x;bottom=y;best=score;
                }
                if(selected==null)
                {
                    diagnostics?.refusedOrders.Add($"family {variants[0].id}, work {work}, validations {validations}");
                    return null;
                }
                output[selected.id]=placed;
                for(int r=0;r<selected.height;++r)
                    foreach(var span in selected.rows[r]) Reserve(rows[bottom+r],left+span.start,left+span.end);
            }
            return output;
        }

        static bool Find(Mask mask,List<(int start,int end)>[] rows,ReverseUvTransfer.Options options,CancellationToken token,
            Func<int,Vector2[],int,bool> validate,ref long work,ref long validations,long validationBudget,
            out int left,out int bottom,out Vector2[] placed)
        {
            placed=null;left=bottom=0;int best=int.MaxValue,localValidations=0;
            for(int y=0;y+mask.height<=rows.Length;++y)
            {
                token.ThrowIfCancellationRequested();
                if(y+mask.height>=best) break;
                int x=0;
                while(x+mask.width<=rows.Length)
                {
                    bool free=true;int next=x+1;
                    for(int r=0;r<mask.height && free;++r)
                        foreach(var span in mask.rows[r])
                        {
                            if(++work>128000000) return false;
                            var occupied=rows[y+r];int i=FirstAfter(occupied,x+span.start);
                            if(i>=occupied.Count || occupied[i].start>=x+span.end) continue;
                            next=Math.Max(next,occupied[i].end-span.start);free=false;break;
                        }
                    if(free)
                    {
                        int score=Math.Max(x+mask.width,y+mask.height);
                        if(score>=best) break;
                        foreach(float phase in new[] {0f,.125f,.25f,.375f})
                        {
                            // Free cells can still be unrepresentable at Float32
                            // precision. Do not exhaust the canvas with expensive
                            // metric/contact audits for one such family.
                            if(++validations>validationBudget || ++localValidations>64) return placed!=null;
                            // The raster includes one extra texel for phase
                            // changes. Keep the offset inside that reserve even
                            // when the requested gutter is large.
                            float offset=phase;
                            var points=mask.local.Select(p=>p+new Vector2(x+offset,y+offset)).ToArray();
                            if(points.Any(p=>p.x<options.padding || p.y<options.padding || p.x>rows.Length-options.padding || p.y>rows.Length-options.padding)
                                || (validate!=null && !validate(mask.id,points,rows.Length))) continue;
                            if(score<best) {left=x;bottom=y;placed=points;best=score;}
                            // A later X on this row cannot improve its extent.
                            break;
                        }
                        if(best==Math.Max(mask.width,mask.height)) return true;
                    }
                    if(free && placed!=null && bottom==y) break;
                    x=next;
                }
            }
            return placed!=null;
        }

        static int FirstAfter(List<(int start,int end)> spans,int x)
        {
            int lo=0,hi=spans.Count;
            while(lo<hi) {int middle=(lo+hi)/2;if(spans[middle].end<=x) lo=middle+1;else hi=middle;}
            return lo;
        }

        static void Reserve(List<(int start,int end)> spans,int start,int end)
        {
            if(start>=end) return;
            int first=FirstAfter(spans,start),last=first;
            while(last<spans.Count && spans[last].start<=end)
            {start=Math.Min(start,spans[last].start);end=Math.Max(end,spans[last].end);++last;}
            if(last>first) spans.RemoveRange(first,last-first);
            spans.Insert(first,(start,end));
        }
    }
}
