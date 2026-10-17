using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // An exact source vertex is stronger projection evidence than interpolation
    // through a slightly displaced target UV0 point. The selected source shell
    // remains authoritative; coincident vertices in another shell cannot win.
    internal static class ForwardUvExactFamilyProjection
    {
        internal static bool TryImprove(Vector3[] source,Vector2[] sourceUv,List<UvShell> sourceShells,
            Vector3[] positions,int[] indices,Vector2[] original,int[] donors,out Vector2[] improved,out string reason,
            CancellationToken token=default,long comparisonBudget=200000)
        {
            Check(token);improved=null;reason="invalid input";
            if(!ValidInput(source,sourceUv,sourceShells,positions,indices,original,donors,comparisonBudget)) return false;
            var before=Scan(original,indices,token,ref comparisonBudget);
            reason="existing input needs no exact-vertex contact recovery";
            if(before==null || !before.complete || before.pairs==0 || before.degenerateFaces!=0 || before.invalidFaces!=0) return false;
            var lookup=BuildSource(source,sourceUv,sourceShells,token);
            var aliases=AliasGroups(positions,original,indices,token);
            var proposal=Propose(positions,original,donors,indices,aliases,lookup,token,out bool changed);
            reason="no unambiguous selected-shell vertex projection";
            if(!changed) return false;
            if(!RestoreRefusedAliases(positions,indices,original,proposal,aliases,token))
            {reason="metric or winding refusal could not retain original aliases";return false;}
            var after=Scan(proposal,indices,token,ref comparisonBudget);
            reason="complete final contact/degeneracy audit refused exact-vertex recovery";
            if(after==null || !after.complete || after.pairs!=0 || after.degenerateFaces!=0 || after.invalidFaces!=0) return false;
            reason="exact selected-shell projection; original incident metric/winding guards and zero final contacts";
            improved=proposal;return true;
        }

        static bool ValidInput(Vector3[] source,Vector2[] sourceUv,List<UvShell> sourceShells,
            Vector3[] positions,int[] indices,Vector2[] original,int[] donors,long comparisonBudget)
        {
            if(source==null || sourceUv==null || sourceShells==null || positions==null || indices==null
                || original==null || donors==null) return false;
            if(source.Length!=sourceUv.Length || positions.Length!=original.Length
                || donors.Length!=positions.Length || indices.Length%3!=0 || comparisonBudget<=0) return false;
            foreach(int v in indices) if(v<0 || v>=positions.Length) return false;
            return true;
        }

        static Vector2[] Propose(Vector3[] positions,Vector2[] original,int[] donors,int[] indices,
            Dictionary<(Vector3,Vector2),List<int>> aliases,List<Dictionary<Vector3,Vector2?>> lookup,
            CancellationToken token,out bool changed)
        {
            var active=new HashSet<int>(indices);
            var proposal=(Vector2[])original.Clone();changed=false;
            foreach(var group in aliases.Values)
            {
                Check(token);
                if(!SelectAlias(group,positions,donors,active,lookup,out var selected)) continue;
                foreach(int v in group)
                {
                    proposal[v]=selected;
                    changed|=!proposal[v].Equals(original[v]);
                }
            }
            return proposal;
        }

        static bool SelectAlias(List<int> group,Vector3[] positions,int[] donors,HashSet<int> active,
            List<Dictionary<Vector3,Vector2?>> lookup,out Vector2 selected)
        {
            Vector2? candidate=null;selected=default;
            foreach(int v in group)
            {
                if(!active.Contains(v)) return false;
                int donor=donors[v];
                if(donor<0 || donor>=lookup.Count || !lookup[donor].TryGetValue(positions[v],out var value)
                    || !value.HasValue) return false;
                if(candidate.HasValue && !candidate.Value.Equals(value.Value)) return false;
                candidate=value;
            }
            if(!candidate.HasValue) return false;
            selected=candidate.Value;return true;
        }

        static List<Dictionary<Vector3,Vector2?>> BuildSource(Vector3[] positions,Vector2[] uv,List<UvShell> shells,CancellationToken token)
        {
            var result=new List<Dictionary<Vector3,Vector2?>>();
            foreach(var shell in shells)
            {
                var values=new Dictionary<Vector3,Vector2?>();result.Add(values);
                foreach(int v in shell.vertexIndices)
                {
                    Check(token);
                    if(v<0 || v>=positions.Length || !Finite(positions[v],uv[v])) continue;
                    if(!values.TryGetValue(positions[v],out var old)) values.Add(positions[v],uv[v]);
                    else if(old.HasValue && !old.Value.Equals(uv[v])) values[positions[v]]=null;
                }
            }
            return result;
        }

        static Dictionary<(Vector3,Vector2),List<int>> AliasGroups(Vector3[] positions,Vector2[] uv,int[] indices,CancellationToken token)
        {
            var result=new Dictionary<(Vector3,Vector2),List<int>>();var used=new HashSet<int>(indices);
            for(int v=0;v<positions.Length;++v)
            {
                Check(token);
                // An unused duplicate still constrains a shared original point.
                // Retain the complete alias set when any of its members is used.
                var key=(positions[v],uv[v]);
                if(!result.TryGetValue(key,out var group)) {group=new List<int>();result.Add(key,group);}
                group.Add(v);
            }
            var remove=new List<(Vector3,Vector2)>();
            foreach(var pair in result)
            {
                bool active=false;foreach(int v in pair.Value) active|=used.Contains(v);
                if(!active) remove.Add(pair.Key);
            }
            foreach(var key in remove) result.Remove(key);
            return result;
        }

        static bool RestoreRefusedAliases(Vector3[] positions,int[] indices,Vector2[] original,Vector2[] proposal,
            Dictionary<(Vector3,Vector2),List<int>> aliases,CancellationToken token)
        {
            // Each changed vertex reverts at most once. Queue only its actual
            // incident faces: at most the original scan plus three corner uses
            // per face, rather than a quadratic number of whole-mesh passes.
            var incident=IncidentFaces(original.Length,indices);
            var queued=new bool[indices.Length/3];var queue=new Queue<int>();
            for(int face=0;face<queued.Length;++face)
            {
                queue.Enqueue(face);queued[face]=true;
            }
            while(queue.Count!=0)
            {
                Check(token);int face=queue.Dequeue();queued[face]=false;int c=face*3;
                int a=indices[c],b=indices[c+1],d=indices[c+2];
                if(proposal[a].Equals(original[a]) && proposal[b].Equals(original[b]) && proposal[d].Equals(original[d])) continue;
                if(Fits(positions[a],positions[b],positions[d],original[a],original[b],original[d],proposal[a],proposal[b],proposal[d])) continue;
                if(!RestoreFace(c,positions,indices,original,proposal,aliases,incident,queue,queued)) return false;
            }
            return true;
        }

        static List<int>[] IncidentFaces(int count,int[] indices)
        {
            var incident=new List<int>[count];
            for(int c=0;c<indices.Length;++c)
            {
                int v=indices[c];
                if(incident[v]==null) incident[v]=new List<int>();
                incident[v].Add(c/3);
            }
            return incident;
        }

        static bool RestoreFace(int corner,Vector3[] positions,int[] indices,Vector2[] original,Vector2[] proposal,
            Dictionary<(Vector3,Vector2),List<int>> aliases,List<int>[] incident,Queue<int> queue,bool[] queued)
        {
            bool changed=false;
            for(int k=0;k<3;++k)
            {
                int v=indices[corner+k];
                changed|=RestoreAlias(aliases[(positions[v],original[v])],original,proposal,incident,queue,queued);
            }
            return changed;
        }

        static bool RestoreAlias(List<int> group,Vector2[] original,Vector2[] proposal,List<int>[] incident,
            Queue<int> queue,bool[] queued)
        {
            bool changed=false;
            foreach(int other in group)
            {
                if(proposal[other].Equals(original[other])) continue;
                proposal[other]=original[other];changed=true;
                if(incident[other]==null) continue;
                foreach(int affected in incident[other])
                {
                    if(queued[affected]) continue;
                    queued[affected]=true;queue.Enqueue(affected);
                }
            }
            return changed;
        }

        internal static bool Fits(Vector3 a,Vector3 b,Vector3 c,Vector2 x,Vector2 y,Vector2 z,Vector2 u,Vector2 v,Vector2 w)
        {
            var frame=Frame(a,b,c);
            double before=ReverseUvTransfer.TriangleAnisotropy(frame,x,y,z);
            double after=ReverseUvTransfer.TriangleAnisotropy(frame,u,v,w);
            return double.IsFinite(after) && after<=4
                && UvTransferTolerance.MetricFits(after,before,UvTransferTolerance.RelativeStretch)
                && Math.Sign(ForwardUvReference.Area(x,y,z))==Math.Sign(ForwardUvReference.Area(u,v,w));
        }

        static (double length,double x,double y) Frame(Vector3 a,Vector3 b,Vector3 c)
        {
            double x=(double)b.x-a.x,y=(double)b.y-a.y,z=(double)b.z-a.z;
            double u=(double)c.x-a.x,v=(double)c.y-a.y,w=(double)c.z-a.z;
            double length=Math.Sqrt(x*x+y*y+z*z),cx=y*w-z*v,cy=z*u-x*w,cz=x*v-y*u;
            return (length,(x*u+y*v+z*w)/length,Math.Sqrt(cx*cx+cy*cy+cz*cz)/length);
        }

        static UvAtlasDiagnostics.Report Scan(Vector2[] uv,int[] indices,CancellationToken token,ref long remaining)
        {
            Check(token);if(remaining<=0) return null;
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=uv,indices=indices,charts=new int[uv.Length]},
                token,comparisonBudget:remaining);
            remaining-=scan.comparisons;return scan;
        }

        static bool Finite(Vector3 p,Vector2 u)=>float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z)
            && float.IsFinite(u.x) && float.IsFinite(u.y);
        static void Check(CancellationToken token)
        {token.ThrowIfCancellationRequested();if(UvProgress.CancelRequested) throw new OperationCanceledException();}
    }
}
