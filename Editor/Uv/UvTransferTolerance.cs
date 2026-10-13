using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class UvTransferTolerance
    {
        internal const float BoundaryTexels = .25f;
        internal const float RelativeStretch = .01f;

        internal static bool MetricFits(double after, double before, double tolerance = RelativeStretch)
            => after <= before * (1 + tolerance) + 1e-6;

        readonly struct Point
        {
            internal readonly double x, y;
            internal Point(double x, double y) { this.x = x; this.y = y; }
        }

        static double Side(Point a, Point b, Point p)
            => (b.x-a.x)*(p.y-a.y)-(b.y-a.y)*(p.x-a.x);

        static double Area(List<Point> p)
        {
            double area=0;
            for(int i=1;i+1<p.Count;++i) area+=Side(p[0],p[i],p[i+1]);
            return Math.Abs(area);
        }

        static List<Point> Half(List<Point> polygon, Point a, Point b, bool inside)
        {
            var result=new List<Point>();
            var previous=polygon[polygon.Count-1]; double last=Side(a,b,previous);
            foreach(var p in polygon)
            {
                double next=Side(a,b,p);
                bool keep=inside?next>=0:next<=0, kept=inside?last>=0:last<=0;
                if(keep!=kept)
                {
                    double t=last/(last-next);
                    result.Add(new Point(previous.x+(p.x-previous.x)*t,previous.y+(p.y-previous.y)*t));
                }
                if(keep) result.Add(p);
                previous=p;last=next;
            }
            return result;
        }

        // The hull of triangle + an inscribed octagon stays inside the Euclidean
        // texel band, including acute corners. Expanding infinite edge planes
        // would extend far beyond the requested distance at such corners.
        static List<Point> Dilate(Vector2[] triangle, double radius)
        {
            var points=new List<Point>();
            foreach(var p in triangle)
                for(int i=0;i<8;++i)
                {
                    double angle=i*Math.PI/4;
                    points.Add(new Point(p.x+radius*Math.Cos(angle),p.y+radius*Math.Sin(angle)));
                }
            points=points.OrderBy(p=>p.x).ThenBy(p=>p.y).ToList();
            var hull=new List<Point>();
            foreach(var p in points)
            {
                while(hull.Count>=2 && Side(hull[hull.Count-2],hull[hull.Count-1],p)<=0) hull.RemoveAt(hull.Count-1);
                hull.Add(p);
            }
            int lower=hull.Count;
            for(int i=points.Count-2;i>=0;--i)
            {
                var p=points[i];
                while(hull.Count>lower && Side(hull[hull.Count-2],hull[hull.Count-1],p)<=0) hull.RemoveAt(hull.Count-1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count-1);
            return hull;
        }

        /// <summary>Subtract the union of bounded donor footprints from a target
        /// triangle. Inputs and radius share coordinate units; donor overlaps
        /// never count twice. A hole wider than the band remains uncovered.</summary>
        internal static bool Covers(Vector2[] triangle, IEnumerable<Vector2[]> donors, double radius,
            ref long work, long budget, CancellationToken token)
        {
            if(!(radius>0) || double.IsNaN(radius) || double.IsInfinity(radius)) return false;
            var original=triangle.Select(p=>new Point(p.x,p.y)).ToList();
            double area=Area(original); if(!(area>0)) return false;
            var remaining=new List<List<Point>> {original};
            var min=triangle.Aggregate(Vector2.Min);var max=triangle.Aggregate(Vector2.Max);
            foreach(var donor in donors)
            {
                token.ThrowIfCancellationRequested();
                if(++work>budget) throw new InvalidOperationException("UV boundary tolerance comparison budget exceeded.");
                var lo=donor.Aggregate(Vector2.Min);var hi=donor.Aggregate(Vector2.Max);
                if(hi.x+radius<min.x || hi.y+radius<min.y || lo.x-radius>max.x || lo.y-radius>max.y) continue;
                var hull=Dilate(donor,radius);var outside=new List<List<Point>>();
                foreach(var polygon in remaining)
                {
                    var inside=polygon;
                    for(int e=0;e<hull.Count && inside.Count>=3;++e)
                    {
                        token.ThrowIfCancellationRequested();
                        if(++work>budget) throw new InvalidOperationException("UV boundary tolerance comparison budget exceeded.");
                        var a=hull[e];var b=hull[(e+1)%hull.Count];
                        var part=Half(inside,a,b,false);
                        if(part.Count>=3 && Area(part)>area*1e-12) outside.Add(part);
                        inside=Half(inside,a,b,true);
                    }
                }
                remaining=outside;
                if(remaining.Count>2048) throw new InvalidOperationException("UV boundary tolerance partition budget exceeded.");
                if(remaining.Sum(Area)<=area*1e-12) return true;
            }
            return false;
        }
    }
}
