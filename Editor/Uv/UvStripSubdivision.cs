using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Triangulation-independent subdivision in UV space, performed
    /// before any chart has descendants. Each new corner retains barycentric
    /// provenance in its original triangle; no source vertex is moved.</summary>
    internal static class UvStripSubdivision
    {
        internal sealed class Result
        {
            internal int[] sourceFaces, regions;
            internal Vector3[] barycentrics;
            internal Vector2[] uv;
            internal int originalCharts, charts, cuts;
        }

        readonly struct Point
        {
            internal readonly double x,y,b,c;
            internal Point(double x,double y,double b,double c) {this.x=x;this.y=y;this.b=b;this.c=c;}
            internal Vector3 Bary=>new Vector3((float)(1-b-c),(float)b,(float)c);
        }

        readonly struct Line
        {
            internal readonly Vector2 start, end, direction;
            readonly double tolerance;
            internal Line(Vector2 start,Vector2 end,Vector2 direction,double tolerance) {this.start=start;this.end=end;this.direction=direction;this.tolerance=tolerance;}
            internal double Side(Point p)
            {
                double side=(double)direction.x*(p.y-start.y)-(double)direction.y*(p.x-start.x);
                // Border coordinates and directions were stored as Float32.
                // A collinear corner must not generate a microscopic extra face.
                return Math.Abs(side)<=tolerance?0:side;
            }
            internal bool Cuts(List<Point> polygon)
            {
                bool positive=false,negative=false;double lo=double.MaxValue,hi=double.MinValue;
                for(int i=0;i<polygon.Count;++i)
                {
                    var a=polygon[i];var b=polygon[(i+1)%polygon.Count];double sa=Side(a),sb=Side(b);
                    positive|=sa>0;negative|=sa<0;
                    if(sa!=0 && (sa>0)==(sb>0) && sb!=0) continue;
                    double t=sa==0?0:sa/(sa-sb);
                    double along=direction.x*(a.x+(b.x-a.x)*t-start.x)+direction.y*(a.y+(b.y-a.y)*t-start.y);
                    lo=Math.Min(lo,along);hi=Math.Max(hi,along);
                }
                double finish=Vector2.Dot(end-start,direction);
                return positive && negative && Math.Min(hi,Math.Max(0,finish))-Math.Max(lo,Math.Min(0,finish))>tolerance;
            }
            internal bool ContainsEdge(Vector2 a,Vector2 b)
            {
                if(Side(new Point(a.x,a.y,0,0))!=0 || Side(new Point(b.x,b.y,0,0))!=0) return false;
                double along=Vector2.Dot((a+b)*.5f-start,direction),finish=Vector2.Dot(end-start,direction);
                return along>=Math.Min(0,finish)-tolerance && along<=Math.Max(0,finish)+tolerance;
            }
        }

        sealed class Edge
        {
            internal int a,b;
            internal readonly List<int> faces=new List<int>();
        }
        sealed class Piece {internal int face;internal List<Point> points;}
        static long Key(int a,int b)=>((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
        static double Cross(Vector2 a,Vector2 b)=>(double)a.x*b.y-(double)a.y*b.x;
        static double Area(Point a,Point b,Point c)=>(b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x);

        internal static Result Build(Vector2[] uv,int[] triangles,CancellationToken token=default,Vector3[] positions=null,
            double minimumAltitude=0, HashSet<int> protectedFaces=null)
        {
            var connected=new DisjointSet(triangles.Length/3);var edges=new Dictionary<long,Edge>();
            for(int f=0;f<triangles.Length/3;++f)
            {
                token.ThrowIfCancellationRequested();
                for(int k=0;k<3;++k)
                {
                    int a=triangles[f*3+k],b=triangles[f*3+(k+1)%3];long key=Key(a,b);
                    if(!edges.TryGetValue(key,out var edge)) {edge=new Edge {a=a,b=b};edges.Add(key,edge);}
                    else connected.Union(f,edge.faces[0]);
                    edge.faces.Add(f);
                }
            }
            var groups=Enumerable.Range(0,triangles.Length/3).GroupBy(connected.Find).OrderBy(g=>g.Min()).ToArray();
            var componentEdges=edges.Values.GroupBy(e=>connected.Find(e.faces[0])).ToDictionary(g=>g.Key,g=>g.ToArray());
            var facesOut=new List<int>();var baryOut=new List<Vector3>();var uvOut=new List<Vector2>();var regionsOut=new List<int>();
            var result=new Result {originalCharts=groups.Length};
            foreach(var group in groups)
            {
                token.ThrowIfCancellationRequested();var faces=group.ToArray();
                // This decomposition targets flat frame/branch charts. Cutting a
                // curved strip changes donor domains without improving its surface
                // parameterization; leave it to the curved-chart preparation.
                var lines=faces.Length<=8192 && (protectedFaces==null || !faces.Any(protectedFaces.Contains))
                    && (positions==null || Planar(positions,triangles,faces,token))
                    ?Propose(uv,triangles,faces,componentEdges[group.Key],token):Array.Empty<Line>();
                if(positions!=null && minimumAltitude>0)
                    lines=lines.Where(line=>RepresentableLine(line,uv,triangles,faces,positions,minimumAltitude,token)).ToArray();
                var pieces=Partition(uv,triangles,faces,lines,token);
                var localFaces=new List<int>();var localBary=new List<Vector3>();var localUv=new List<Vector2>();
                var canonical=new Dictionary<Vector2,Vector2>();
                foreach(var piece in pieces)
                    foreach(var triangle in Triangulate(piece.points))
                    {
                        localFaces.Add(piece.face);
                        foreach(var p in triangle)
                        {
                            var pixel=Pixel(p,uv,triangles,piece.face);
                            if(!canonical.TryGetValue(pixel,out var value)) {value=pixel;canonical.Add(pixel,pixel);}
                            localUv.Add(value);localBary.Add(p.Bary);
                        }
                    }
                var regions=MergeCompactNeighbours(localUv,Regions(localUv,localFaces.Count,lines),token);int charts=regions.Distinct().Count();
                double original=UvJunctionCuts.BestBox(uv,triangles,faces,token).Area;
                var indices=Enumerable.Range(0,localUv.Count).ToArray();var coordinates=localUv.ToArray();
                double divided=Enumerable.Range(0,regions.Length).GroupBy(f=>regions[f])
                    .Sum(g=>UvJunctionCuts.BestBox(coordinates,indices,g.ToArray(),token).Area);
                var covered=new Dictionary<int,double>();
                for(int i=0;i<localFaces.Count;++i)
                {
                    var a=localBary[i*3];var b=localBary[i*3+1];var c=localBary[i*3+2];
                    covered.TryGetValue(localFaces[i],out double sum);
                    covered[localFaces[i]]=sum+Math.Abs(((double)b.y-a.y)*(c.z-a.z)-((double)b.z-a.z)*(c.y-a.y));
                }
                bool complete=faces.All(f=>covered.TryGetValue(f,out double sum) && Math.Abs(sum-1)<1e-5);
                var partCounts=localFaces.GroupBy(f=>f).ToDictionary(g=>g.Key,g=>g.Count());
                if(positions!=null)
                    for(int i=0;i<localFaces.Count && complete;++i)
                    {
                        int t=localFaces[i]*3;
                        Vector3 Position(int k)
                        {
                            var b=localBary[i*3+k];
                            return positions[triangles[t]]*b.x+positions[triangles[t+1]]*b.y+positions[triangles[t+2]]*b.z;
                        }
                        complete=MeshGeometry.HasArea(Position(0),Position(1),Position(2));
                        if(complete && minimumAltitude>0 && partCounts[localFaces[i]]>1)
                            complete=Altitude(Position(0),Position(1),Position(2))>=minimumAltitude;
                    }
                bool use=complete && lines.Length>0 && charts>1 && original>0 && divided<original*.85;
                if(use)
                {
                    var mapping=new Dictionary<int,int>();
                    for(int f=0;f<localFaces.Count;++f)
                    {
                        if(!mapping.TryGetValue(regions[f],out int region)) {region=result.charts++;mapping.Add(regions[f],region);}
                        facesOut.Add(localFaces[f]);regionsOut.Add(region);
                        for(int k=0;k<3;++k) {uvOut.Add(localUv[f*3+k]);baryOut.Add(localBary[f*3+k]);}
                    }
                    result.cuts+=lines.Length;
                }
                else
                {
                    foreach(int f in faces)
                    {
                        facesOut.Add(f);regionsOut.Add(result.charts);
                        for(int k=0;k<3;++k) {uvOut.Add(uv[triangles[f*3+k]]);baryOut.Add(k==0?Vector3.right:k==1?Vector3.up:Vector3.forward);}
                    }
                    ++result.charts;
                }
            }
            // Mesh.triangles is in material order. A connected chart may span
            // material slots, so component iteration order is not output order.
            var order=Enumerable.Range(0,facesOut.Count).OrderBy(i=>facesOut[i]).ToArray();
            result.sourceFaces=order.Select(i=>facesOut[i]).ToArray();
            result.regions=order.Select(i=>regionsOut[i]).ToArray();
            result.barycentrics=order.SelectMany(i=>Enumerable.Range(0,3).Select(k=>baryOut[i*3+k])).ToArray();
            result.uv=order.SelectMany(i=>Enumerable.Range(0,3).Select(k=>uvOut[i*3+k])).ToArray();
            return result;
        }

        static bool Planar(Vector3[] positions,int[] triangles,int[] faces,CancellationToken token)
        {
            var weighted=Vector3.zero;var center=Vector3.zero;var bounds=new Bounds(positions[triangles[faces[0]*3]],Vector3.zero);
            foreach(int f in faces)
            {
                token.ThrowIfCancellationRequested();int t=f*3;
                var a=positions[triangles[t]];var b=positions[triangles[t+1]];var c=positions[triangles[t+2]];
                weighted+=Vector3.Cross(b-a,c-a);center+=a+b+c;
                bounds.Encapsulate(a);bounds.Encapsulate(b);bounds.Encapsulate(c);
            }
            if(weighted==Vector3.zero) return false;
            var normal=weighted.normalized;center/=faces.Length*3;
            double tolerance=bounds.size.magnitude*.01;
            foreach(int f in faces)
            {
                token.ThrowIfCancellationRequested();int t=f*3;
                var a=positions[triangles[t]];var b=positions[triangles[t+1]];var c=positions[triangles[t+2]];
                if(Vector3.Dot(Vector3.Cross(b-a,c-a).normalized,normal)<.99f) return false;
                for(int k=0;k<3;++k)
                    if(Math.Abs(Vector3.Dot(positions[triangles[t+k]]-center,normal))>tolerance) return false;
            }
            return true;
        }

        static Line[] Propose(Vector2[] uv,int[] triangles,int[] faces,Edge[] edges,CancellationToken token)
        {
            int sign=0;var next=new Dictionary<int,int>();var previous=new Dictionary<int,int>();
            foreach(int f in faces)
            {
                int t=f*3;double area=Cross(uv[triangles[t+1]]-uv[triangles[t]],uv[triangles[t+2]]-uv[triangles[t]]);
                if(double.IsNaN(area)||double.IsInfinity(area)||Math.Abs(area)<double.Epsilon) return Array.Empty<Line>();
                int current=Math.Sign(area);
                if(sign!=0 && sign!=current) return Array.Empty<Line>();
                sign=current;
            }
            foreach(var edge in edges)
            {
                if(edge.faces.Count>2) return Array.Empty<Line>();
                if(edge.faces.Count!=1) continue;
                if(next.ContainsKey(edge.a)||previous.ContainsKey(edge.b)) return Array.Empty<Line>();
                next.Add(edge.a,edge.b);previous.Add(edge.b,edge.a);
            }
            if(next.Count==0 || next.Keys.Any(v=>!previous.ContainsKey(v))) return Array.Empty<Line>();
            var lines=new List<Line>();
            var border=edges.Where(e=>e.faces.Count==1).ToArray();long work=0;
            var origin=uv[next.Keys.First()];
            double span=next.Keys.Max(v=>(uv[v]-origin).magnitude);
            foreach(int v in next.Keys.OrderBy(v=>v))
            {
                token.ThrowIfCancellationRequested();
                var incoming=uv[v]-uv[previous[v]];var outgoing=uv[next[v]]-uv[v];
                if(Cross(incoming,outgoing)*sign>=-1e-6*Math.Sqrt((double)incoming.sqrMagnitude*outgoing.sqrMagnitude)) continue;
                double nearest=double.MaxValue;Vector2 direction=default;
                foreach(var candidate in new[] {incoming.normalized,-outgoing.normalized})
                    foreach(var edge in border)
                    {
                        if(++work>2000000) return Array.Empty<Line>();
                        if(edge.a==v || edge.b==v) continue;
                        var a=uv[edge.a]-uv[v];var d=uv[edge.b]-uv[edge.a];double denominator=Cross(candidate,d);
                        if(Math.Abs(denominator)<=1e-9*d.magnitude) continue;
                        double along=Cross(a,d)/denominator,across=Cross(a,candidate)/denominator;
                        if(along<=0 || across<0 || across>1 || along>=nearest) continue;
                        nearest=along;direction=candidate;
                    }
                if(nearest==double.MaxValue) continue;
                var end=uv[v]+direction*(float)nearest;
                if(direction.x<0 || (Math.Abs(direction.x)<float.Epsilon && direction.y<0)) direction=-direction;
                bool duplicate=lines.Any(l=>Math.Abs(Cross(l.direction,direction))<1e-5
                    && Math.Abs(Cross(l.direction,uv[v]-l.start))<=span*4e-6
                    && ((l.start-uv[v]).magnitude+(l.end-end).magnitude<=span*8e-6
                        || (l.end-uv[v]).magnitude+(l.start-end).magnitude<=span*8e-6));
                if(!duplicate) lines.Add(new Line(uv[v],end,direction,span*2e-6));
                if(lines.Count>64) return Array.Empty<Line>();
            }
            return lines.ToArray();
        }

        static double Altitude(Vector3 a,Vector3 b,Vector3 c)
        {
            var frame=ReverseUvTransfer.TriangleFrame(a,b,c);
            double Length(Vector3 p,Vector3 q)
            {double x=(double)q.x-p.x,y=(double)q.y-p.y,z=(double)q.z-p.z;return Math.Sqrt(x*x+y*y+z*z);}
            return frame.length*frame.y/Math.Max(frame.length,Math.Max(Length(b,c),Length(c,a)));
        }

        static bool RepresentableLine(Line line,Vector2[] uv,int[] indices,int[] faces,Vector3[] positions,
            double minimumAltitude,CancellationToken token)
        {
            foreach(int f in faces)
            {
                token.ThrowIfCancellationRequested();int t=f*3;
                var polygon=Enumerable.Range(0,3).Select(k=>{
                    var p=uv[indices[t+k]];return new Point(p.x,p.y,k==1?1:0,k==2?1:0);
                }).ToList();
                if(!line.Cuts(polygon)) continue;
                foreach(int side in new[] {1,-1}) foreach(var triangle in Triangulate(Clip(polygon,line,side)))
                {
                    var p=triangle.Select(c=>{
                        var b=c.Bary;return positions[indices[t]]*b.x+positions[indices[t+1]]*b.y+positions[indices[t+2]]*b.z;
                    }).ToArray();
                    if(!MeshGeometry.HasArea(p[0],p[1],p[2]) || Altitude(p[0],p[1],p[2])<minimumAltitude) return false;
                }
            }
            return true;
        }

        static List<Piece> Partition(Vector2[] uv,int[] triangles,int[] faces,Line[] lines,CancellationToken token)
        {
            var result=new List<Piece>();long work=0;
            foreach(int f in faces)
            {
                var polygon=Enumerable.Range(0,3).Select(k=>{
                    var p=uv[triangles[f*3+k]];return new Point(p.x,p.y,k==1?1:0,k==2?1:0);
                }).ToList();
                var parts=new List<Piece> {new Piece {face=f,points=polygon}};
                for(int line=0;line<lines.Length;++line)
                {
                    token.ThrowIfCancellationRequested();
                    var split=new List<Piece>();
                    foreach(var part in parts)
                    {
                        if(++work>2000000) return Partition(uv,triangles,faces,Array.Empty<Line>(),token);
                        if(!lines[line].Cuts(part.points)) {split.Add(part);continue;}
                        foreach(int side in new[] {1,-1})
                        {
                            var clipped=Clip(part.points,lines[line],side);
                            if(clipped.Count<3) continue;
                            double area=0;for(int k=1;k+1<clipped.Count;++k) area+=Area(clipped[0],clipped[k],clipped[k+1]);
                            if(Math.Abs(area)<=Math.Abs(Area(polygon[0],polygon[1],polygon[2]))*1e-10) continue;
                            split.Add(new Piece {face=f,points=clipped});
                        }
                    }
                    parts=split;
                }
                result.AddRange(parts);
            }
            return result;
        }

        static List<Point> Clip(List<Point> polygon,Line line,int sign)
        {
            var output=new List<Point>();var previous=polygon[polygon.Count-1];double old=sign*line.Side(previous);
            foreach(var p in polygon)
            {
                double side=sign*line.Side(p);
                if((side>=0)!=(old>=0))
                {
                    double t=old/(old-side);
                    output.Add(new Point(previous.x+(p.x-previous.x)*t,previous.y+(p.y-previous.y)*t,
                        previous.b+(p.b-previous.b)*t,previous.c+(p.c-previous.c)*t));
                }
                if(side>=0) output.Add(p);
                previous=p;old=side;
            }
            for(int i=output.Count-1;i>=0 && output.Count>1;--i)
            {
                var a=output[i];var b=output[(i+1)%output.Count];
                // Coordinate identity, rather than geometric proximity: distinct
                // corners of a tiny positive-area face must remain distinct.
                if((a.x,a.y).Equals((b.x,b.y))) output.RemoveAt(i);
            }
            return output;
        }

        static IEnumerable<Point[]> Triangulate(List<Point> polygon)
        {
            var points=new List<Point>(polygon);
            while(points.Count>3)
            {
                int winner=-1;double best=0;
                for(int i=0;i<points.Count;++i)
                {
                    double area=Math.Abs(Area(points[(i+points.Count-1)%points.Count],points[i],points[(i+1)%points.Count]));
                    if(area>best) {best=area;winner=i;}
                }
                if(winner<0) yield break;
                yield return new[] {points[(winner+points.Count-1)%points.Count],points[winner],points[(winner+1)%points.Count]};
                points.RemoveAt(winner);
            }
            if(points.Count==3 && Area(points[0],points[1],points[2])!=0) yield return points.ToArray();
        }

        static Vector2 Pixel(Point point,Vector2[] uv,int[] indices,int face)
        {
            var weights=new[] {1-point.b-point.c,point.b,point.c};
            int exact=Array.FindIndex(weights,w=>Math.Abs(w-1)<1e-12);
            if(exact>=0) return uv[indices[face*3+exact]];
            int zero=Array.FindIndex(weights,w=>Math.Abs(w)<1e-10);
            if(zero>=0)
            {
                int a=(zero+1)%3,b=(zero+2)%3,ia=indices[face*3+a],ib=indices[face*3+b];
                if(ia>ib) {(ia,ib)=(ib,ia);(a,b)=(b,a);}
                double t=Math.Round(weights[b]/(weights[a]+weights[b]),9);
                return new Vector2((float)(uv[ia].x+((double)uv[ib].x-uv[ia].x)*t),(float)(uv[ia].y+((double)uv[ib].y-uv[ia].y)*t));
            }
            return new Vector2((float)point.x,(float)point.y);
        }

        static int[] Regions(List<Vector2> uv,int count,Line[] lines)
        {
            var union=new DisjointSet(count);var edges=new Dictionary<(Vector2,Vector2),int>();
            for(int f=0;f<count;++f)
                for(int k=0;k<3;++k)
                {
                    var a=uv[f*3+k];var b=uv[f*3+(k+1)%3];
                    if(edges.TryGetValue((a,b),out int other)||edges.TryGetValue((b,a),out other))
                    {if(!lines.Any(l=>l.ContainsEdge(a,b))) union.Union(f,other);}
                    else edges.Add((a,b),f);
                }
            return Enumerable.Range(0,count).Select(union.Find).ToArray();
        }

        static int[] MergeCompactNeighbours(List<Vector2> uv,int[] regions,CancellationToken token)
        {
            var union=new DisjointSet(regions.Length);
            for(int f=0;f<regions.Length;++f) union.Union(f,regions[f]);
            var edges=new Dictionary<(Vector2,Vector2),int>();var adjacent=new List<(int,int)>();
            for(int f=0;f<regions.Length;++f) for(int k=0;k<3;++k)
            {
                var a=uv[f*3+k];var b=uv[f*3+(k+1)%3];
                if(edges.TryGetValue((a,b),out int other)||edges.TryGetValue((b,a),out other))
                {if(regions[f]!=regions[other]) adjacent.Add((f,other));}
                else edges.Add((a,b),f);
            }
            var coordinates=uv.ToArray();var indices=Enumerable.Range(0,uv.Count).ToArray();long work=0;
            bool changed;
            do
            {
                changed=false;
                var groups=Enumerable.Range(0,regions.Length).GroupBy(union.Find).ToDictionary(g=>g.Key,g=>g.ToArray());
                var visited=new HashSet<(int,int)>();
                foreach(var pair in adjacent)
                {
                    token.ThrowIfCancellationRequested();
                    int a=union.Find(pair.Item1),b=union.Find(pair.Item2);
                    var key=(Math.Min(a,b),Math.Max(a,b));
                    if(a==b || !visited.Add(key)) continue;
                    var faces=groups[a].Concat(groups[b]).ToArray();
                    work+=faces.Length*64L;if(work>2000000) break;
                    double area=faces.Sum(f=>Math.Abs(Cross(uv[f*3+1]-uv[f*3],uv[f*3+2]-uv[f*3]))*.5);
                    double rectangle=UvJunctionCuts.BestBox(coordinates,indices,faces,token).Area;
                    if(rectangle<=0 || area<rectangle*.98) continue;
                    union.Union(a,b);changed=true;break;
                }
            } while(changed);
            return Enumerable.Range(0,regions.Length).Select(union.Find).ToArray();
        }
    }
}

