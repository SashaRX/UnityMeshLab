using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Repairs a malformed lightmap chart by welding only its detached UV unknowns.
    /// Geometry must remain a manifold disk; valid UV disks and required geometric cuts are retained.</summary>
    internal static class UvShellTopologyRepair
    {
        internal static Vector2[] TryRepair(Vector3[] corners,Vector2[] original,int iterations,
            CancellationToken token,out string reason,(double length,double x,double y)[] metricFrames=null, double maxAnisotropy=4, long comparisonBudget=20000000)
        {
            token.ThrowIfCancellationRequested();reason="invalid input";
            if(corners==null || original==null || corners.Length!=original.Length || corners.Length%3!=0 || iterations<=0) return null;
            if(metricFrames!=null && metricFrames.Length!=corners.Length/3) return null;
            var points=new List<Vector3>();var unique=new Dictionary<Vector3,int>();var indices=new int[corners.Length];
            var sums=new List<(double x,double y,int count)>();
            var first=new Dictionary<Vector3,Vector2>();bool split=false;
            for(int c=0;c<corners.Length;++c)
            {
                var p=original[c];
                if(!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(corners[c].x)
                    || !float.IsFinite(corners[c].y) || !float.IsFinite(corners[c].z)) return null;
                if(!unique.TryGetValue(corners[c],out int v))
                {v=points.Count;unique.Add(corners[c],v);points.Add(corners[c]);sums.Add(default);first.Add(corners[c],p);}
                else split|=!first[corners[c]].Equals(p);
                indices[c]=v;var s=sums[v];sums[v]=(s.x+p.x,s.y+p.y,s.count+1);
            }
            reason="no internal coordinate aliases";
            if(!split) return null;
            reason="geometric quotient is not a manifold disk";
            if(!Disk(indices,points.Count,token)) return null;
            var uvUnique=new Dictionary<(Vector3,Vector2),int>();var uvIndices=new int[corners.Length];
            for(int c=0;c<corners.Length;++c)
            {
                var key=(corners[c],original[c]);
                if(!uvUnique.TryGetValue(key,out int v)) {v=uvUnique.Count;uvUnique.Add(key,v);}
                uvIndices[c]=v;
            }
            reason="existing UV chart is already a manifold disk";
            if(Disk(uvIndices,uvUnique.Count,token)) return null;
            var input=points.ToArray();
            var flat=sums.SelectMany(s=>new[] {(float)(s.x/s.count),(float)(s.y/s.count)}).ToArray();
            var faces=Enumerable.Range(0,indices.Length/3).ToArray();
            // ARAP's rest triangles use positive local winding. Restore the
            // original global winding after the solve; reflected charts remain legal.
            int winding=0;
            for(int f=0;f<faces.Length;++f)
            {
                double a=Area(original[f*3],original[f*3+1],original[f*3+2]);
                if(a==0) continue;
                if(winding!=0 && Math.Sign(a)!=winding) {reason="mixed authored winding";return null;}
                winding=Math.Sign(a);
            }
            if(winding<0) for(int i=1;i<flat.Length;i+=2) flat[i]=-flat[i];
            // Reverse coordinates are texels while ARAP's rest lengths are
            // geometric units. Give the solve matching area units so its
            // existing collapse guard does not mistake texel density for loss
            // of chart area. Restore units before any published-metric audit.
            double restArea=0,seedArea=0;
            for(int f=0;f<faces.Length;++f)
            {
                int a=indices[f*3],b=indices[f*3+1],c=indices[f*3+2];
                var frame=Frame(input[a],input[b],input[c]);restArea+=frame.length*frame.y;
                seedArea+=Math.Abs(((double)flat[b*2]-flat[a*2])*((double)flat[c*2+1]-flat[a*2+1])
                    -((double)flat[b*2+1]-flat[a*2+1])*((double)flat[c*2]-flat[a*2]));
            }
            double unitScale=Math.Sqrt(restArea/seedArea);
            if(!double.IsFinite(unitScale) || !(unitScale>0)) {reason="invalid seed area";return null;}
            double centerX=sums.Average(s=>s.x/s.count),centerY=sums.Average(s=>s.y/s.count)*(winding<0?-1:1);
            for(int i=0;i<flat.Length/2;++i)
            {flat[i*2]=(float)((flat[i*2]-centerX)*unitScale);flat[i*2+1]=(float)((flat[i*2+1]-centerY)*unitScale);}
            reason="ARAP did not converge";
            token.ThrowIfCancellationRequested();
            if(!ArapParameterization.Reparameterize(input,indices,faces,Enumerable.Range(0,input.Length).ToArray(),flat,iterations,out _)) return null;
            token.ThrowIfCancellationRequested();
            var candidate=indices.Select(i=>new Vector2((float)(flat[i*2]/unitScale+centerX),
                (float)((flat[i*2+1]/unitScale+centerY)*(winding<0?-1:1)))).ToArray();
            bool improved=false;
            for(int f=0;f<faces.Length;++f)
            {
                int c=f*3;var frame=metricFrames==null?Frame(corners[c],corners[c+1],corners[c+2]):metricFrames[f];
                double before=ReverseUvTransfer.TriangleAnisotropy(frame,original[c],original[c+1],original[c+2]);
                double after=ReverseUvTransfer.TriangleAnisotropy(frame,candidate[c],candidate[c+1],candidate[c+2]);
                if(!double.IsFinite(after) || after>maxAnisotropy || !UvTransferTolerance.MetricFits(after,before))
                {reason=$"face {f} metric {before:G8}->{after:G8}";return null;}
                if(winding!=0 && Math.Sign(Area(candidate[c],candidate[c+1],candidate[c+2]))!=winding)
                {reason="winding change";return null;}
                improved|=!double.IsFinite(before) || after<before*.999999;
            }
            // Topological repair is useful even when an isometric slit is healed.
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=candidate,
                indices=Enumerable.Range(0,candidate.Length).ToArray(),charts=new int[candidate.Length]},token,
                comparisonBudget:comparisonBudget);
            reason=$"contacts={scan.pairs},collapsed={scan.degenerateFaces},complete={scan.complete},metricImproved={improved}";
            return scan.complete && scan.pairs==0 && scan.degenerateFaces==0 && scan.invalidFaces==0?candidate:null;
        }

        static (double length,double x,double y) Frame(Vector3 a,Vector3 b,Vector3 c)
        {
            double x=(double)b.x-a.x,y=(double)b.y-a.y,z=(double)b.z-a.z;
            double u=(double)c.x-a.x,v=(double)c.y-a.y,w=(double)c.z-a.z;
            double length=Math.Sqrt(x*x+y*y+z*z),dot=x*u+y*v+z*w;
            double cx=y*w-z*v,cy=z*u-x*w,cz=x*v-y*u;
            return (length,dot/length,Math.Sqrt(cx*cx+cy*cy+cz*cz)/length);
        }
        static double Area(Vector2 a,Vector2 b,Vector2 c)
            =>((double)b.x-a.x)*((double)c.y-a.y)-((double)b.y-a.y)*((double)c.x-a.x);

        static bool Disk(int[] ix,int vertices,CancellationToken token)
        {
            var edges=new Dictionary<(int,int),List<(int face,int a,int b)>>();
            var fans=Enumerable.Range(0,vertices).Select(_=>new HashSet<int>()).ToArray();
            var neighbors=new Dictionary<int,List<int>>();
            for(int f=0;f<ix.Length/3;++f)
            {
                token.ThrowIfCancellationRequested();
                for(int k=0;k<3;++k)
                {
                    int a=ix[f*3+k],b=ix[f*3+(k+1)%3];
                    if(a==b) return false;
                    fans[a].Add(f);var key=(Math.Min(a,b),Math.Max(a,b));
                    if(!edges.TryGetValue(key,out var uses)) {uses=new List<(int,int,int)>();edges.Add(key,uses);}
                    uses.Add((f,a,b));
                }
            }
            if(vertices-edges.Count+ix.Length/3!=1) return false;
            var boundary=new List<(int,int)>();var boundaryDegree=new int[vertices];
            foreach(var edge in edges)
            {
                var uses=edge.Value;
                if(uses.Count==1)
                {boundary.Add(edge.Key);++boundaryDegree[edge.Key.Item1];++boundaryDegree[edge.Key.Item2];}
                else if(uses.Count!=2 || uses[0].a!=uses[1].b || uses[0].b!=uses[1].a) return false;
                else
                {
                    foreach(var pair in new[] {(uses[0].face,uses[1].face),(uses[1].face,uses[0].face)})
                    {
                        if(!neighbors.TryGetValue(pair.Item1,out var next)) {next=new List<int>();neighbors.Add(pair.Item1,next);}
                        next.Add(pair.Item2);
                    }
                }
            }
            if(boundary.Count<3 || boundaryDegree.Any(d=>d!=0 && d!=2)) return false;
            HashSet<int> Reach(int seed,HashSet<int> allowed)
            {
                var found=new HashSet<int> {seed};var stack=new Stack<int>();stack.Push(seed);
                while(stack.Count>0)
                    if(neighbors.TryGetValue(stack.Pop(),out var next))
                        foreach(int f in next) if(allowed.Contains(f) && found.Add(f)) stack.Push(f);
                return found;
            }
            var all=new HashSet<int>(Enumerable.Range(0,ix.Length/3));
            if(Reach(0,all).Count!=all.Count) return false;
            // Edge-manifold tests alone miss bow-tie vertices. The incident
            // triangles must form one connected fan at every welded position.
            foreach(var fan in fans)
            {
                if(fan.Count==0 || Reach(fan.First(),fan).Count!=fan.Count) return false;
            }
            return true;
        }
    }
}
