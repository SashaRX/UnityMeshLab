using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Only chooses one rigid coordinate frame for the complete family. The
    // largest prepared island on its finest LOD supplies the visible axis.
    internal static class ReverseUvFamilyAxis
    {
        internal static Vector2[][] Align(Vector2[][] source,
            List<(ReverseUvTransfer.Surface surface,int corner)>[] groups,CancellationToken token)
        {
            var output=new Vector2[source.Length][];
            for(int g=0;g<source.Length;++g)
            {
                token.ThrowIfCancellationRequested();var entries=groups[g];int finest=entries.Min(c=>c.surface.lod);
                var primary=Enumerable.Range(0,entries.Count/3).Where(f=>entries[f*3].surface.lod==finest)
                    .GroupBy(f=>(entries[f*3].surface,region:Region(entries[f*3])))
                    .OrderByDescending(part=>part.Sum(f=>Area(entries[f*3]))).ThenBy(part=>part.Min()).First();
                var reference=primary.SelectMany(f=>Enumerable.Range(f*3,3)).Select(c=>source[g][c]).ToArray();
                output[g]=Rotate(source[g],reference,token);
            }
            return output;
        }

        static int Region((ReverseUvTransfer.Surface surface,int corner) entry)
            =>entry.surface.preparedRegions==null?entry.surface.faces[entry.corner/3].chart:entry.surface.preparedRegions[entry.corner/3];

        static double Area((ReverseUvTransfer.Surface surface,int corner) entry)
        {
            var frame=entry.surface.packingFrames[entry.corner/3];return frame.length*frame.y;
        }

        internal static Vector2[] Rotate(Vector2[] family,Vector2[] reference,CancellationToken token)
        {
            var hull=Hull(reference,token);
            if(hull.Count<3) return family;
            double best=double.PositiveInfinity,angle=0;
            for(int i=0;i<hull.Count;++i)
            {
                token.ThrowIfCancellationRequested();var a=hull[i];var b=hull[(i+1)%hull.Count];
                double candidate=Math.Atan2((double)b.y-a.y,(double)b.x-a.x);
                candidate-=Math.Round(candidate/(Math.PI*.5))*(Math.PI*.5);
                double area=RectangleArea(hull,candidate);
                int comparison=area.CompareTo(best);
                if(comparison<0 || comparison==0 && Math.Abs(candidate)<Math.Abs(angle)) {best=area;angle=candidate;}
            }
            if(angle==0) return family;
            double c=Math.Cos(angle),s=Math.Sin(angle);var origin=family[0];var output=new Vector2[family.Length];
            for(int i=0;i<output.Length;++i)
            {
                token.ThrowIfCancellationRequested();double x=(double)family[i].x-origin.x,y=(double)family[i].y-origin.y;
                output[i]=new Vector2((float)(x*c+y*s),(float)(y*c-x*s));
            }
            return output;
        }

        static double RectangleArea(List<Vector2> hull,double angle)
        {
            double c=Math.Cos(angle),s=Math.Sin(angle),left=double.PositiveInfinity,right=double.NegativeInfinity;
            double bottom=double.PositiveInfinity,top=double.NegativeInfinity;
            foreach(var p in hull)
            {
                double x=p.x*c+p.y*s,y=p.y*c-p.x*s;
                left=Math.Min(left,x);right=Math.Max(right,x);bottom=Math.Min(bottom,y);top=Math.Max(top,y);
            }
            return (right-left)*(top-bottom);
        }

        static List<Vector2> Hull(Vector2[] points,CancellationToken token)
        {
            var sorted=points.Distinct().OrderBy(p=>p.x).ThenBy(p=>p.y).ToArray();
            if(sorted.Length<3) return sorted.ToList();
            var lower=new List<Vector2>();var upper=new List<Vector2>();
            foreach(var p in sorted) {token.ThrowIfCancellationRequested();Append(lower,p);}
            for(int i=sorted.Length-1;i>=0;--i) {token.ThrowIfCancellationRequested();Append(upper,sorted[i]);}
            lower.RemoveAt(lower.Count-1);upper.RemoveAt(upper.Count-1);lower.AddRange(upper);return lower;
        }

        static void Append(List<Vector2> hull,Vector2 point)
        {
            while(hull.Count>=2 && Cross(hull[hull.Count-2],hull[hull.Count-1],point)<=0) hull.RemoveAt(hull.Count-1);
            hull.Add(point);
        }

        static double Cross(Vector2 a,Vector2 b,Vector2 c)
            =>((double)b.x-a.x)*((double)c.y-a.y)-((double)b.y-a.y)*((double)c.x-a.x);
    }
}
