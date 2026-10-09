using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Three continuous planar arcs meeting at an inferred corner. Every
    /// candidate is audited; a different second valid corner makes the closure ambiguous.</summary>
    internal static class RemeshCompoundCap
    {
        internal sealed class Result
        {
            internal Vector3[] positions;
            internal readonly List<int[]> patches = new List<int[]>();
            internal int contacts;
        }

        internal static Result Generate(Vector3[] p,int[] source,List<int> loop,CancellationToken token,ref int trials,double planeTolerance = 0)
        {
            if(loop.Count>64) throw Refuse("three-plane rim exceeds 64 edges");
            Result winner=null; int fits=0,samples=0;
            var low=p[loop[0]]; var high=low;
            foreach(int v in loop) { low=Vector3.Min(low,p[v]); high=Vector3.Max(high,p[v]); }
            double tolerance=Math.Max((high-low).magnitude*1e-5,planeTolerance);
            for(int a=0;a<loop.Count;++a) for(int b=a+2;b<loop.Count;++b) for(int c=b+2;c<loop.Count;++c) {
                token.ThrowIfCancellationRequested();
                if(loop.Count-c+a<2) continue;
                var arcs=new[] { Arc(loop,a,b-a),Arc(loop,b,c-b),Arc(loop,c,loop.Count-c+a) };
                var planes=new RemeshCapPlanes.Plane[3]; bool supported=true;
                for(int k=0;k<3;++k) {
                    if(++fits>32768 || (samples+=arcs[k].Count)>2000000) throw Refuse("plane search budget exceeded; no partial result was accepted");
                    if(!RemeshCapPlanes.TryPlane(p,arcs[k],tolerance,false,out planes[k])) { supported=false; break; }
                }
                if(!supported || !Intersection(p,arcs,planes,out var corner)) continue;
                var extent=high-low;
                if(corner.x<low.x-extent.magnitude || corner.x>high.x+extent.magnitude ||
                    corner.y<low.y-extent.magnitude || corner.y>high.y+extent.magnitude ||
                    corner.z<low.z-extent.magnitude || corner.z>high.z+extent.magnitude) continue;
                var points=new Vector3[p.Length+1]; Array.Copy(p,points,p.Length); points[p.Length]=corner;
                var exact=new RemeshCapIntersection.Q[points.Length][];
                for(int i=0;i<points.Length;++i) exact[i]=RemeshCapIntersection.Point(points[i]);
                var candidate=new List<int>(source); var result=new Result {positions=points};
                try {
                    foreach(var arc in arcs) {
                        // Re-extract actual boundary after each patch. The next arc
                        // must still be continuous and its directed edges unchanged.
                        var before=RemeshTopology.Inspect(points,candidate.ToArray(),token);
                        var expected=Edges(before);
                        for(int i=0;i<arc.Count-1;++i) {
                            int x=arc[i],y=arc[i+1];
                            if(!expected.Remove(Key(before.slots[x],before.slots[y]))) throw Refuse("remaining arc is no longer a boundary");
                        }
                        void Toggle(int x,int y) { var edge=Key(before.slots[x],before.slots[y]); if(!expected.Remove(edge)) expected.Add(edge); }
                        Toggle(arc[0],p.Length); Toggle(arc[arc.Count-1],p.Length);
                        var polygon=new List<int>(arc) {p.Length};
                        var patch=RemeshPlanarCap.Triangulate(points,exact,polygon,token,planeTolerance); int oldFaces=candidate.Count/3;
                        candidate.AddRange(patch); var all=candidate.ToArray(); var after=RemeshTopology.Inspect(points,all,token);
                        if(!after.Valid || !Edges(after).SetEquals(expected)) throw Refuse("patch has invalid topology or changes another boundary");
                        RemeshPlanarCap.AuditContacts(points,exact,all,oldFaces,token,ref trials,out int tested);
                        result.contacts+=tested; result.patches.Add(patch);
                    }
                }
                catch(InvalidOperationException ex) when(!ex.Message.Contains("budget")) { continue; }
                if(winner!=null && (winner.positions[p.Length]-corner).magnitude>tolerance)
                    throw Refuse("no unique local closure: multiple non-intersecting three-plane corners");
                if(winner==null) winner=result;
            }
            return winner ?? throw Refuse("no unique local closure: no audited three-plane corner");
        }

        static List<int> Arc(List<int> loop,int start,int length)
        {
            var result=new List<int>();
            for(int i=0;i<=length;++i) result.Add(loop[(start+i)%loop.Count]);
            return result;
        }
        static (int,int) Key(int a,int b)=>a<b?(a,b):(b,a);
        static HashSet<(int,int)> Edges(RemeshTopology.Snapshot topology)
        {
            var result=new HashSet<(int,int)>();
            foreach(var edge in topology.edges) if(edge.Value.count==1) result.Add(edge.Key);
            return result;
        }

        static bool Intersection(Vector3[] p,List<int>[] arcs,RemeshCapPlanes.Plane[] planes,out Vector3 corner)
        {
            // Cramer's rule in doubles around a local origin; reject ill-conditioned
            // planes rather than extrapolating a remote artificial apex.
            var origin=p[arcs[0][0]]; var x=planes[0]; var y=planes[1]; var z=planes[2];
            double cx=y.ny*z.nz-y.nz*z.ny,cy=y.nz*z.nx-y.nx*z.nz,cz=y.nx*z.ny-y.ny*z.nx;
            double determinant=x.nx*cx+x.ny*cy+x.nz*cz; corner=default;
            if(Math.Abs(determinant)<.05) return false;
            double D(int i) { var n=planes[i]; var q=p[arcs[i][0]]; return n.nx*((double)q.x-origin.x)+n.ny*((double)q.y-origin.y)+n.nz*((double)q.z-origin.z); }
            double a=D(0),b=D(1),c=D(2);
            double px=origin.x+(a*cx+b*(z.ny*x.nz-z.nz*x.ny)+c*(x.ny*y.nz-x.nz*y.ny))/determinant;
            double py=origin.y+(a*cy+b*(z.nz*x.nx-z.nx*x.nz)+c*(x.nz*y.nx-x.nx*y.nz))/determinant;
            double pz=origin.z+(a*cz+b*(z.nx*x.ny-z.ny*x.nx)+c*(x.nx*y.ny-x.ny*y.nx))/determinant;
            if(!double.IsFinite(px)||!double.IsFinite(py)||!double.IsFinite(pz)) return false;
            corner=new Vector3((float)px,(float)py,(float)pz); return true;
        }
        static InvalidOperationException Refuse(string reason)=>new InvalidOperationException("Compound Cap refused: "+reason+". Source donors were preserved.");
    }
}
