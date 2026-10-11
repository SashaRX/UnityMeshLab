using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>One rigid placement per cross-LOD family. The free cells form a
    /// disjoint partition, so thousands of families do not trigger MaxRects'
    /// repeated containment pruning. Coordinates are rounded only at output.</summary>
    internal static class ReverseUvFamilyPacking
    {
        sealed class Box
        {
            internal int id;
            internal Vector2 min;
            internal Vector2[] points;
            internal double width, height, altitude;
        }

        internal readonly struct Placement
        {
            internal readonly RectInt rectangle;
            internal readonly int rotation;
            internal bool turn=>rotation%2!=0;
            internal readonly Vector2 phase;
            internal Placement(RectInt rectangle, int rotation, Vector2 phase)
            { this.rectangle=rectangle; this.rotation=rotation; this.phase=phase; }
        }

        internal static Vector2[][] Pack(Vector2[][] coordinates, ReverseUvTransfer.Options options,
            CancellationToken token, out int side, out float scale, Func<int,Vector2[],int,bool> validate=null)
        {
            token.ThrowIfCancellationRequested();
            if(coordinates==null || coordinates.Length==0 || coordinates.Any(points=>points==null || points.Length==0 || points.Length%3!=0
                || points.Any(p=>!Finite(p.x) || !Finite(p.y))))
                throw new InvalidOperationException("Reverse families require finite triangle corners.");
            var boxes=coordinates.Select((points,id) => {
                var min=points.Aggregate(Vector2.Min); var max=points.Aggregate(Vector2.Max);
                return new Box { id=id, min=min, points=points, width=(double)max.x-min.x, height=(double)max.y-min.y,
                    altitude=MinimumAltitude(points) };
            }).ToArray();
            if(boxes.Any(box=>!Finite(box.altitude) || box.altitude<=0))
                throw new InvalidOperationException("Reverse families contain collapsed UV triangles.");
            var orders=new[] {
                boxes.OrderBy(b=>b.altitude<.0001?0:1).ThenBy(b=>b.altitude<.0001?b.altitude:0).ThenByDescending(b=>Math.Max(b.width,b.height)).ThenBy(b=>b.id).ToArray(),
                boxes.OrderByDescending(b=>Math.Max(b.width,b.height)).ThenByDescending(b=>b.width*b.height).ThenBy(b=>b.id).ToArray(),
                boxes.OrderByDescending(b=>b.width*b.height).ThenBy(b=>b.id).ToArray(),
                boxes.OrderByDescending(b=>Math.Min(b.width,b.height)).ThenByDescending(b=>Math.Max(b.width,b.height)).ThenBy(b=>b.id).ToArray()
            };
            double area=boxes.Sum(b=>(Math.Ceiling(b.width)+options.padding)*(Math.Ceiling(b.height)+options.padding));
            if (!Finite(area) || Math.Sqrt(area)+options.padding*2>options.maxAtlasSize)
                throw new InvalidOperationException("Complete reverse atlas exceeds its size limit.");
            side=Math.Max(16,Mathf.NextPowerOfTwo((int)Math.Ceiling(Math.Sqrt(area)+options.padding*2)));
            Placement[] placements;
            while (!TryOrders(orders,side,options,1,token,validate,out placements))
            {
                side*=2;
                if (side>options.maxAtlasSize) throw new InvalidOperationException("Complete reverse atlas exceeds its size limit.");
            }
            double chosenScale=1;
            if (options.fillFinalAtlas)
            {
                // Capacity is monotone; Float32 representability is not. Find
                // the geometric capacity first, then test descending scales.
                // Treating one rounding refusal as an upper capacity bound
                // stranded otherwise valid families at their initial density.
                double low=1, high=2;
                Placement[] geometric=placements;
                while (TryOrders(orders,side,options,high,token,null,out var larger))
                { low=high; geometric=larger; high*=2; }
                for (int attempt=0;attempt<14;++attempt)
                {
                    double trial=(low+high)*.5;
                    if (TryOrders(orders,side,options,trial,token,null,out var candidate))
                    { low=trial; geometric=candidate; }
                    else high=trial;
                }
                if(validate==null) {chosenScale=low;placements=geometric;}
                else
                    for(int attempt=0;attempt<64;++attempt)
                    {
                        double trial=low*Math.Pow(.985,attempt);
                        if(trial<=chosenScale) break;
                        if(!TryOrders(orders,side,options,trial,token,validate,out var candidate)) continue;
                        chosenScale=trial;placements=candidate;break;
                    }
            }
            // A square capacity search can leave an L-shaped arrangement even
            // when the same rigid families fit a much denser rectangle. Keep
            // scale and gutter fixed while testing shorter canvases in both axes.
            double bestArea=BoundsArea(boxes,placements,chosenScale);
            int extent=side-options.padding*2;
            foreach(double fraction in new[] {.5,.625,.75,.875})
                for(int axis=0;axis<2;++axis)
                {
                    int shorter=(int)Math.Ceiling(extent*fraction);
                    if(!TryOrders(orders,side,options,chosenScale,token,validate,out var compact,
                        axis==0?extent:shorter,axis==0?shorter:extent)) continue;
                    double candidateArea=BoundsArea(boxes,compact,chosenScale);
                    if(candidateArea>=bestArea) continue;
                    bestArea=candidateArea;placements=compact;
                }
            double minX=double.MaxValue,minY=double.MaxValue,maxX=0,maxY=0;
            foreach (var box in boxes)
            {
                var placement=placements[box.id];
                double x=placement.rectangle.x+options.padding+placement.phase.x, y=placement.rectangle.y+options.padding+placement.phase.y;
                minX=Math.Min(minX,x); minY=Math.Min(minY,y);
                maxX=Math.Max(maxX,x+(placement.turn?box.height:box.width)*chosenScale);
                maxY=Math.Max(maxY,y+(placement.turn?box.width:box.height)*chosenScale);
            }
            double fit=options.fillFinalAtlas?Math.Max(1,(side-options.padding*2)/Math.Max(maxX-minX,maxY-minY)):1;
            var output=Materialize(boxes,placements,chosenScale,1,options.padding,0,0,token);
            if (fit>1)
            {
                var fitted=Materialize(boxes,placements,chosenScale,fit,options.padding,minX-options.padding,minY-options.padding,token);
                bool valid=true;
                foreach(var box in boxes)
                    if(!Within(fitted[box.id],side,options.padding) || (validate!=null && !validate(box.id,fitted[box.id],side)))
                    {valid=false;break;}
                if (valid)
                    output=fitted;
                else fit=1;
            }
            scale=(float)(chosenScale*fit);
            return output;
        }

        static Vector2[][] Materialize(Box[] boxes,Placement[] placements,double scale,double fit,
            int padding,double minX,double minY,CancellationToken token)
        {
            var output=new Vector2[boxes.Length][];
            foreach (var box in boxes)
            {
                token.ThrowIfCancellationRequested();
                var p=placements[box.id];
                output[box.id]=Coordinates(box,p.rotation,scale*fit,
                    (p.rectangle.x+p.phase.x-minX)*fit+padding,
                    (p.rectangle.y+p.phase.y-minY)*fit+padding);
            }
            return output;
        }

        static Vector2[] Coordinates(Box box,int rotation,double scale,double originX,double originY)
        {
            var result=new Vector2[box.points.Length];
            for (int p=0;p<result.Length;++p)
            {
                double x=(double)box.points[p].x-box.min.x, y=(double)box.points[p].y-box.min.y;
                double oldX=x;
                if(rotation==1) {x=box.height-y;y=oldX;}
                else if(rotation==2) {x=box.width-x;y=box.height-y;}
                else if(rotation==3) {x=y;y=box.width-oldX;}
                result[p]=new Vector2((float)(x*scale+originX),(float)(y*scale+originY));
            }
            return result;
        }

        static bool Within(Vector2[] points,int side,int padding)
            => points.All(p=>p.x>=padding && p.y>=padding && p.x<=side-padding && p.y<=side-padding);

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        static double BoundsArea(Box[] boxes,Placement[] placements,double scale)
        {
            double minX=double.MaxValue,minY=double.MaxValue,maxX=0,maxY=0;
            foreach(var box in boxes)
            {
                var p=placements[box.id];double x=p.rectangle.x+p.phase.x,y=p.rectangle.y+p.phase.y;
                minX=Math.Min(minX,x);minY=Math.Min(minY,y);
                maxX=Math.Max(maxX,x+(p.turn?box.height:box.width)*scale);
                maxY=Math.Max(maxY,y+(p.turn?box.width:box.height)*scale);
            }
            return (maxX-minX)*(maxY-minY);
        }

        static double MinimumAltitude(Vector2[] points)
        {
            double minimum=double.MaxValue;
            for (int i=0;i<points.Length;i+=3)
            {
                double ax=(double)points[i+1].x-points[i].x, ay=(double)points[i+1].y-points[i].y;
                double bx=(double)points[i+2].x-points[i].x, by=(double)points[i+2].y-points[i].y;
                double longest=Math.Sqrt(Math.Max(ax*ax+ay*ay,Math.Max(bx*bx+by*by,(bx-ax)*(bx-ax)+(by-ay)*(by-ay))));
                minimum=Math.Min(minimum,Math.Abs(ax*by-ay*bx)/longest);
            }
            return minimum;
        }

        static bool TryOrders(Box[][] orders, int side, ReverseUvTransfer.Options options, double scale,
            CancellationToken token, Func<int,Vector2[],int,bool> validate, out Placement[] output,
            int width=0,int height=0)
        {
            output=null;
            foreach (var order in orders)
            {
                if (TryPack(order,side,options,scale,token,validate,out output,width,height)) return true;
                if (TryRows(order,side,options,scale,token,validate,out output,width,height)) return true;
            }
            return false;
        }

        static bool TryRows(Box[] boxes,int side,ReverseUvTransfer.Options options,double scale,
            CancellationToken token,Func<int,Vector2[],int,bool> validate,out Placement[] output,int canvasWidth,int canvasHeight)
        {
            output=null;
            int extent=side-options.padding*2,nextY=0;
            if(extent<=0 || side>options.maxAtlasSize) return false;
            canvasWidth=canvasWidth>0?canvasWidth:extent;canvasHeight=canvasHeight>0?canvasHeight:extent;
            var rows=new List<RectInt>();var placements=new Placement[boxes.Length];
            foreach(var box in boxes)
            {
                token.ThrowIfCancellationRequested();
                double width=Math.Ceiling(box.width*scale)+options.padding,height=Math.Ceiling(box.height*scale)+options.padding;
                if(Math.Min(width,height)>Math.Min(canvasWidth,canvasHeight) || Math.Max(width,height)>Math.Max(canvasWidth,canvasHeight)) return false;
                int best=int.MaxValue,waste=int.MaxValue,selected=-1;Placement chosen=default;
                for(int r=0;r<rows.Count;++r)
                    for(int rotation=0;rotation<(options.rotateCharts?(validate==null?2:4):1);++rotation)
                    {
                        var row=rows[r];double w=rotation%2==0?width:height,h=rotation%2==0?height:width;
                        if(w>row.width || h>row.height) continue;
                        int score=row.height-(int)h,other=row.width-(int)w;
                        if(selected>=0 && (score>best || (score==best && other>=waste))) continue;
                        if(!ValidPlacement(box,row,rotation,scale,side,options.padding,validate,out var placement)) continue;
                        best=row.height-placement.rectangle.height;waste=row.width-placement.rectangle.width;
                        selected=r;chosen=placement;
                    }
                if(selected<0)
                {
                    var remaining=new RectInt(0,nextY,canvasWidth,canvasHeight-nextY);
                    for(int rotation=0;rotation<(options.rotateCharts?(validate==null?2:4):1);++rotation)
                    {
                        double w=rotation%2==0?width:height,h=rotation%2==0?height:width;
                        if(w>remaining.width || h>remaining.height || (selected>=0 && h>=chosen.rectangle.height)) continue;
                        if(!ValidPlacement(box,remaining,rotation,scale,side,options.padding,validate,out var placement)) continue;
                        if(selected>=0 && placement.rectangle.height>=chosen.rectangle.height) continue;
                        selected=rows.Count;chosen=placement;
                    }
                    if(selected<0) return false;
                    rows.Add(new RectInt(0,nextY,canvasWidth,chosen.rectangle.height));nextY+=chosen.rectangle.height;
                }
                var cell=rows[selected];int consumed=chosen.rectangle.width;
                rows[selected]=new RectInt(cell.x+consumed,cell.y,cell.width-consumed,cell.height);
                placements[box.id]=chosen;
            }
            output=placements;return true;
        }

        static bool TryPack(Box[] boxes, int side, ReverseUvTransfer.Options options, double scale,
            CancellationToken token, Func<int,Vector2[],int,bool> validate, out Placement[] output,int canvasWidth,int canvasHeight)
        {
            output=null;
            if (side>options.maxAtlasSize || side<=options.padding*2) return false;
            int extent=side-options.padding*2;
            var free=new List<RectInt> {new RectInt(0,0,canvasWidth>0?canvasWidth:extent,canvasHeight>0?canvasHeight:extent)};
            var placements=new Placement[boxes.Length];
            foreach (var box in boxes)
            {
                token.ThrowIfCancellationRequested();
                double w=Math.Ceiling(box.width*scale)+options.padding, h=Math.Ceiling(box.height*scale)+options.padding;
                if (w>side || h>side) return false;
                int width=(int)w,height=(int)h;
                int best=int.MaxValue,waste=int.MaxValue,selected=-1;
                RectInt used=default;int chosenRotation=0;Vector2 phase=default;
                for (int r=0;r<free.Count;++r)
                    for (int rotation=0;rotation<(options.rotateCharts?(validate==null?2:4):1);++rotation)
                    {
                        int rw=rotation%2==0?width:height,rh=rotation%2==0?height:width;
                        var cell=free[r];
                        if (rw>cell.width || rh>cell.height) continue;
                        int score=Math.Min(cell.width-rw,cell.height-rh), other=Math.Max(cell.width-rw,cell.height-rh);
                        if (selected>=0 && (score>best || (score==best && other>=waste))) continue;
                        if (!ValidPlacement(box,cell,rotation,scale,side,options.padding,validate,out var placement)) continue;
                        used=placement.rectangle;chosenRotation=placement.rotation;phase=placement.phase;
                        best=Math.Min(cell.width-used.width,cell.height-used.height);
                        waste=Math.Max(cell.width-used.width,cell.height-used.height);selected=r;
                    }
                if (selected<0) return false;
                placements[box.id]=new Placement(used,chosenRotation,phase);
                var cellToSplit=free[selected];free.RemoveAt(selected);
                int dx=cellToSplit.width-used.width,dy=cellToSplit.height-used.height;
                // Keep the larger residual direction uncut for later long strips.
                if (dx>dy)
                {
                    if (dx>0) free.Add(new RectInt(used.xMax,cellToSplit.y,dx,cellToSplit.height));
                    if (dy>0) free.Add(new RectInt(cellToSplit.x,used.yMax,used.width,dy));
                }
                else
                {
                    if (dy>0) free.Add(new RectInt(cellToSplit.x,used.yMax,cellToSplit.width,dy));
                    if (dx>0) free.Add(new RectInt(used.xMax,cellToSplit.y,dx,used.height));
                }
            }
            output=placements;return true;
        }

        static bool ValidPlacement(Box box,RectInt cell,int rotation,double scale,int side,int padding,
            Func<int,Vector2[],int,bool> validate,out Placement placement)
        {
            placement=default;
            double width=(rotation%2!=0?box.height:box.width)*scale,height=(rotation%2!=0?box.width:box.height)*scale;
            var offsets=validate==null || padding==0?new[] {0f}:new[] {0f,.0625f,.125f,.25f,.375f};
            foreach (float x in offsets)
                foreach (float y in offsets)
                {
                    var phase=new Vector2(x*padding,y*padding);
                    var rectangle=new RectInt(cell.x,cell.y,(int)Math.Ceiling(width+phase.x)+padding,
                        (int)Math.Ceiling(height+phase.y)+padding);
                    if(rectangle.width>cell.width || rectangle.height>cell.height) continue;
                    if(validate!=null)
                    {
                        var points=Coordinates(box,rotation,scale,rectangle.x+padding+phase.x,rectangle.y+padding+phase.y);
                        if(!Within(points,side,padding) || !validate(box.id,points,side)) continue;
                    }
                    // Positive phase is included in the allocated cell. The
                    // next family's geometry starts padding pixels past it,
                    // preserving the requested gutter rather than doubling it.
                    placement=new Placement(rectangle,rotation,phase);return true;
                }
            return false;
        }
    }
}
