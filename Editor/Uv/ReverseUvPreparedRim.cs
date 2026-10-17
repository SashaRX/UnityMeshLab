using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        static bool TryRepairPreparedRim(Vector3[] points,Vector2[] uv,(double length,double x,double y)[] frames,
            Options options,CancellationToken token,out Vector2[] result,long availableComparisons=-1)
        {
            token.ThrowIfCancellationRequested();result=null;
            if(points==null || uv==null || frames==null || points.Length!=uv.Length || points.Length!=frames.Length*3
                || !UvShellTopologyRepair.IsUvDisk(points,uv,token)) return false;
            return new PreparedRimRepair(points,uv,frames,options,availableComparisons<0?options.comparisonBudget:availableComparisons).TryRepair(token,out result);
        }

        static bool PreparedRimFitsBaseline(Vector3[] points,Vector2[] candidate,Vector2[] baseline,
            (double length,double x,double y)[] frames,Options options)
        {
            if(!SamePreparedRimAliases(points,candidate,baseline)) return false;
            for(int f=0;f<frames.Length;++f)
            {
                int t=f*3;
                double after=TriangleAnisotropy(frames[f],candidate[t],candidate[t+1],candidate[t+2]);
                double before=TriangleAnisotropy(frames[f],baseline[t],baseline[t+1],baseline[t+2]);
                if(!UvTransferTolerance.MetricFits(after,before,options.relativeStretchTolerance)) return false;
            }
            return true;
        }

        static bool SamePreparedRimAliases(Vector3[] points,Vector2[] candidate,Vector2[] baseline)
        {
            // This recovery changes a damaged shape, never the established
            // repair's seams. Topology-changing repair needs chain-wide proof.
            var forward=new Dictionary<(Vector3,Vector2),Vector2>();
            var reverse=new Dictionary<(Vector3,Vector2),Vector2>();
            for(int c=0;c<points.Length;++c)
            {
                var oldKey=(points[c],baseline[c]);var newKey=(points[c],candidate[c]);
                if(forward.TryGetValue(oldKey,out var a) && !a.Equals(candidate[c])) return false;
                if(reverse.TryGetValue(newKey,out var b) && !b.Equals(baseline[c])) return false;
                forward[oldKey]=candidate[c];reverse[newKey]=baseline[c];
            }
            return true;
        }

        sealed class PreparedRimRepair
        {
            readonly Vector3[] points;
            readonly Vector2[] uv;
            readonly (double length,double x,double y)[] frames;
            readonly Options options;
            readonly long availableComparisons;
            internal PreparedRimRepair(Vector3[] points,Vector2[] uv,(double length,double x,double y)[] frames,Options options,long availableComparisons)
            {this.points=points;this.uv=uv;this.frames=frames;this.options=options;this.availableComparisons=availableComparisons;}

            internal bool TryRepair(CancellationToken token,out Vector2[] result)
            {
                result=null;
                var bad=new HashSet<int>(Enumerable.Range(0,frames.Length).Where(f=>
                    TriangleAnisotropy(frames[f],uv[f*3],uv[f*3+1],uv[f*3+2])>options.maxAnisotropy));
                if(bad.Count==0 || bad.Count==frames.Length || !TryEdges(out var edges)) return false;
                var main=MainComponent(bad,edges);
                var donor=new HashSet<int>(Enumerable.Range(0,frames.Length).Where(f=>!main.Contains(f)));
                if(donor.Sum(f=>frames[f].length*frames[f].y)>=main.Sum(f=>frames[f].length*frames[f].y)) return false;
                var internalEdges=edges.Where(u=>u.Count==2 && u.All(v=>donor.Contains(v.face))).ToArray();
                // Exact intrinsic development is limited to a triangle tree.
                if(internalEdges.Length!=donor.Count-1) return false;
                return TryContacts(edges,main,donor,internalEdges,token,out result);
            }

            bool TryEdges(out List<(int face,int a,int b)>[] result)
            {
                var edges=new Dictionary<((Vector3,Vector2),(Vector3,Vector2)),List<(int face,int a,int b)>>();
                for(int f=0;f<frames.Length;++f)
                    for(int e=0;e<3;++e)
                    {
                        int a=f*3+e,b=f*3+(e+1)%3;var x=(points[a],uv[a]);var y=(points[b],uv[b]);
                        if(Compare(x.Item1,y.Item1)>0) {(x,y)=(y,x);(a,b)=(b,a);}
                        var key=(x,y);
                        if(!edges.TryGetValue(key,out var uses))
                        {uses=new List<(int,int,int)>();edges.Add(key,uses);}
                        uses.Add((f,a,b));
                        if(uses.Count>2) {result=null;return false;}
                    }
                result=edges.Values.ToArray();return true;
            }

            HashSet<int> MainComponent(HashSet<int> bad,List<(int face,int a,int b)>[] edges)
            {
                var neighbors=Enumerable.Range(0,frames.Length).Select(_=>new List<int>()).ToArray();
                foreach(var uses in edges.Where(u=>u.Count==2))
                {neighbors[uses[0].face].Add(uses[1].face);neighbors[uses[1].face].Add(uses[0].face);}
                var healthy=new HashSet<int>(Enumerable.Range(0,frames.Length).Where(f=>!bad.Contains(f)));
                var components=new List<int[]>();
                while(healthy.Count>0) components.Add(HealthyComponent(healthy,neighbors));
                return new HashSet<int>(components.OrderByDescending(c=>c.Sum(f=>frames[f].length*frames[f].y)).First());
            }

            static int[] HealthyComponent(HashSet<int> healthy,List<int>[] neighbors)
            {
                int first=healthy.First();healthy.Remove(first);var found=new List<int>();var pending=new Stack<int>();pending.Push(first);
                while(pending.Count>0)
                {
                    int f=pending.Pop();found.Add(f);
                    foreach(int n in neighbors[f].Where(healthy.Remove)) pending.Push(n);
                }
                return found.ToArray();
            }

            bool TryContacts(List<(int face,int a,int b)>[] edges,HashSet<int> main,HashSet<int> donor,
                List<(int face,int a,int b)>[] internalEdges,CancellationToken token,out Vector2[] result)
            {
                result=null;
                var contacts=edges.Where(u=>u.Count==2 && donor.Contains(u[0].face)!=donor.Contains(u[1].face))
                    .OrderByDescending(u=>(points[u[0].b]-points[u[0].a]).sqrMagnitude).ToArray();
                long remaining=availableComparisons;
                foreach(var contact in contacts)
                {
                    token.ThrowIfCancellationRequested();var entry=contact.First(u=>donor.Contains(u.face));
                    if(!TryDevelop(main,donor,internalEdges,entry,token,out var candidate) || !MetricsFit(candidate)) continue;
                    var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=candidate,
                        indices=Enumerable.Range(0,candidate.Length).ToArray(),charts=new int[candidate.Length]},token,comparisonBudget:remaining);
                    remaining-=scan.comparisons;
                    if(!scan.complete) return false;
                    if(scan.pairs==0 && scan.degenerateFaces==0 && scan.invalidFaces==0) {result=candidate;return true;}
                    if(remaining<=0) return false;
                }
                return false;
            }

            bool MetricsFit(Vector2[] candidate)
            {
                for(int f=0;f<frames.Length;++f)
                {
                    int t=f*3;double after=TriangleAnisotropy(frames[f],candidate[t],candidate[t+1],candidate[t+2]);
                    double before=TriangleAnisotropy(frames[f],uv[t],uv[t+1],uv[t+2]);
                    if(!(after<=options.maxAnisotropy) || !UvTransferTolerance.MetricFits(after,before,options.relativeStretchTolerance)) return false;
                    if(Math.Sign(Cross(candidate[t+1]-candidate[t],candidate[t+2]-candidate[t]))
                        !=Math.Sign(Cross(uv[t+1]-uv[t],uv[t+2]-uv[t]))) return false;
                }
                return true;
            }

            bool TryDevelop(HashSet<int> main,HashSet<int> donor,List<(int face,int a,int b)>[] internalEdges,
                (int face,int a,int b) entry,CancellationToken token,out Vector2[] result)
            {
                result=(Vector2[])uv.Clone();var values=new Dictionary<(Vector3,Vector2),Vector2>();
                var anchors=new Dictionary<(Vector3,Vector2),Vector2>();
                foreach(int face in main)
                    for(int k=0;k<3;++k) anchors[(points[face*3+k],uv[face*3+k])]=uv[face*3+k];
                values[(points[entry.a],uv[entry.a])]=uv[entry.a];values[(points[entry.b],uv[entry.b])]=uv[entry.b];
                var pending=new Queue<int>();pending.Enqueue(entry.face);var completed=new HashSet<int>();
                var neighbors=donor.ToDictionary(face=>face,_=>new List<int>());
                foreach(var edge in internalEdges)
                {neighbors[edge[0].face].Add(edge[1].face);neighbors[edge[1].face].Add(edge[0].face);}
                while(pending.Count>0)
                {
                    token.ThrowIfCancellationRequested();int face=pending.Dequeue();if(!completed.Add(face)) continue;
                    if(!PlaceFace(face,values) || !WriteFace(face,values,anchors,result)) return false;
                    foreach(int neighbor in neighbors[face]) pending.Enqueue(neighbor);
                }
                return completed.Count==donor.Count;
            }

            bool PlaceFace(int face,Dictionary<(Vector3,Vector2),Vector2> values)
            {
                int t=face*3;var known=Enumerable.Range(0,3).Where(k=>values.ContainsKey((points[t+k],uv[t+k]))).ToArray();
                if(known.Length<2) return false;
                if(known.Length==3) return true;
                int a=known[0],b=known[1],c=3-a-b;var u=values[(points[t+a],uv[t+a])];var v=values[(points[t+b],uv[t+b])];
                if(!TryThird(face,a,b,c,u,v,out var third)) return false;
                values[(points[t+c],uv[t+c])]=third;return true;
            }

            bool TryThird(int face,int a,int b,int c,Vector2 u,Vector2 v,out Vector2 third)
            {
                third=default;int t=face*3;var frame=frames[face];
                var local=new[] {Vector2.zero,new Vector2((float)frame.length,0),new Vector2((float)frame.x,(float)frame.y)};
                double baseLength=(local[b]-local[a]).magnitude,length=(v-u).magnitude;
                if(!(baseLength>0 && length>0)) return false;
                double scale=length/baseLength,la=(local[c]-local[a]).magnitude*scale,lb=(local[c]-local[b]).magnitude*scale;
                double x=(la*la-lb*lb+length*length)/(2*length),height2=la*la-x*x;
                if(height2<0) return false;
                double ex=((double)v.x-u.x)/length,ey=((double)v.y-u.y)/length,height=Math.Sqrt(height2);
                third=new Vector2((float)(u.x+ex*x-ey*height),(float)(u.y+ey*x+ex*height));
                var triangle=new Vector2[3];triangle[a]=u;triangle[b]=v;triangle[c]=third;
                int winding=Math.Sign(Cross(uv[t+1]-uv[t],uv[t+2]-uv[t]));
                if(Math.Sign(Cross(triangle[1]-triangle[0],triangle[2]-triangle[0]))!=winding)
                    third=new Vector2((float)(u.x+ex*x+ey*height),(float)(u.y+ey*x-ex*height));
                return true;
            }

            bool WriteFace(int face,Dictionary<(Vector3,Vector2),Vector2> values,Dictionary<(Vector3,Vector2),Vector2> anchors,Vector2[] result)
            {
                int t=face*3;
                for(int k=0;k<3;++k)
                {
                    var key=(points[t+k],uv[t+k]);var value=values[key];
                    if(anchors.TryGetValue(key,out var anchor))
                    {
                        if((value-anchor).magnitude>options.boundaryToleranceTexels) return false;
                        value=anchor;values[key]=anchor;
                    }
                    result[t+k]=value;
                }
                return true;
            }

            static int Compare(Vector3 a,Vector3 b)
            {
                int c=a.x.CompareTo(b.x);
                if(c==0) c=a.y.CompareTo(b.y);
                return c==0?a.z.CompareTo(b.z):c;
            }
        }
    }
}
