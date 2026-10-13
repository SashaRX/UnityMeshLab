using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Union used only by the packing proxy. LOD descendants and
    /// intentional overlays reserve pixels once; actual shell corners are never
    /// clipped, triangulated, or independently parameterized here.</summary>
    internal static class ReverseUvPackingFootprint
    {
        readonly struct Point
        {
            internal readonly double x,y;
            internal Point(double x,double y) {this.x=x;this.y=y;}
            internal Vector2 Float => new Vector2((float)x,(float)y);
        }

        internal static Vector2[] Union(Vector2[] triangles,CancellationToken token)
        {
            if(triangles.Length%3!=0) throw new ArgumentException("Packing footprint requires complete triangles.");
            if(triangles.Length<6) return triangles.ToArray();
            var lo=triangles.Aggregate(Vector2.Min);var hi=triangles.Aggregate(Vector2.Max);
            var span=hi-lo;
            var grid=new Dictionary<(int,int),List<int>>();
            var ordered=Enumerable.Range(0,triangles.Length/3).OrderByDescending(f=>
                Math.Abs(Side(P(triangles[f*3]),P(triangles[f*3+1]),P(triangles[f*3+2])))).ToArray();
            var emitted=new List<Vector2>();long work=0;
            foreach(int f in ordered)
            {
                token.ThrowIfCancellationRequested();int t=f*3;
                var original=new List<Point> {P(triangles[t]),P(triangles[t+1]),P(triangles[t+2])};
                var pieces=new List<List<Point>> {original};
                var cells=Cells(f).ToArray();
                var candidates=cells.Where(grid.ContainsKey).SelectMany(c=>grid[c]).Distinct().OrderBy(i=>i).ToArray();
                foreach(int other in candidates)
                {
                    if(++work>2000000) return triangles.ToArray();
                    if((work&1023)==0) token.ThrowIfCancellationRequested();
                    int u=other*3;
                    var a=P(triangles[u]);var b=P(triangles[u+1]);var c=P(triangles[u+2]);
                    if(!Touches(original,a,b,c)) continue;
                    int sign=Side(a,b,c)>=0?1:-1;
                    var remaining=new List<List<Point>>();
                    foreach(var polygon in pieces)
                    {
                        var inside=polygon;
                        foreach(var edge in new[] {(a,b),(b,c),(c,a)})
                        {
                            if(inside.Count<3) break;
                            var outside=Half(inside,edge.Item1,edge.Item2,-sign);
                            if(Area(outside)>0) remaining.Add(outside);
                            inside=Half(inside,edge.Item1,edge.Item2,sign);
                        }
                    }
                    pieces=remaining;
                    if(pieces.Count==0) break;
                    // This is optional proxy simplification, never permission
                    // to lose geometry or allow an unbounded arrangement.
                    if(pieces.Count>256) return triangles.ToArray();
                }
                foreach(var polygon in pieces)
                    for(int k=1;k+1<polygon.Count;++k)
                    {
                        var a=polygon[0].Float;var b=polygon[k].Float;var c=polygon[k+1].Float;
                        if(Side(P(a),P(b),P(c))==0) continue;
                        emitted.Add(a);emitted.Add(b);emitted.Add(c);
                    }
                if(emitted.Count>750000) return triangles.ToArray();
                foreach(var cell in cells)
                {
                    if(!grid.TryGetValue(cell,out var values)) {values=new List<int>();grid.Add(cell,values);}
                    values.Add(f);
                }
            }
            return emitted.Count>0?emitted.ToArray():triangles.ToArray();

            IEnumerable<(int,int)> Cells(int face)
            {
                int t=face*3;
                var min=Vector2.Min(triangles[t],Vector2.Min(triangles[t+1],triangles[t+2]));
                var max=Vector2.Max(triangles[t],Vector2.Max(triangles[t+1],triangles[t+2]));
                int Cell(float value,float start,float length)=>length>0?Math.Clamp((int)((value-start)/length*16),0,15):0;
                for(int x=Cell(min.x,lo.x,span.x);x<=Cell(max.x,lo.x,span.x);++x)
                    for(int y=Cell(min.y,lo.y,span.y);y<=Cell(max.y,lo.y,span.y);++y) yield return (x,y);
            }
        }

        static Point P(Vector2 p)=>new Point(p.x,p.y);
        static double Side(Point a,Point b,Point p)=>(b.x-a.x)*(p.y-a.y)-(b.y-a.y)*(p.x-a.x);
        static double Area(List<Point> p)
        {
            double area=0;for(int i=1;i+1<p.Count;++i) area+=Side(p[0],p[i],p[i+1]);
            return Math.Abs(area)*.5;
        }
        static bool Touches(List<Point> p,Point a,Point b,Point c)
            =>p.Max(q=>q.x)>Math.Min(a.x,Math.Min(b.x,c.x)) && p.Min(q=>q.x)<Math.Max(a.x,Math.Max(b.x,c.x))
                && p.Max(q=>q.y)>Math.Min(a.y,Math.Min(b.y,c.y)) && p.Min(q=>q.y)<Math.Max(a.y,Math.Max(b.y,c.y));
        static List<Point> Half(List<Point> polygon,Point a,Point b,int sign)
        {
            var output=new List<Point>();var previous=polygon[polygon.Count-1];double last=sign*Side(a,b,previous);
            foreach(var point in polygon)
            {
                double next=sign*Side(a,b,point);
                if(last<0 && next>0 || last>0 && next<0)
                {
                    double t=last/(last-next);
                    output.Add(new Point(previous.x+(point.x-previous.x)*t,previous.y+(point.y-previous.y)*t));
                }
                if(next>=0) output.Add(point);
                previous=point;last=next;
            }
            return output;
        }
    }
}
