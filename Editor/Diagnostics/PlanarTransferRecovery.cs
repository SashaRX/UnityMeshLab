using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Recover a planar donor's affine geometry metric without depending
    /// on a lower LOD's differently distorted UV0. No atlas region is allocated.</summary>
    internal static class PlanarTransferRecovery
    {
        internal static Dictionary<int,Vector2> Project(UvShell target,int[] targetIndices,Vector3[] targetPositions,
            UvShell source,int[] sourceIndices,Vector3[] sourcePositions,Vector2[] sourceUv)
        {
            int basis=source.faceIndices.OrderByDescending(f=>Vector3.Cross(sourcePositions[sourceIndices[f*3+1]]-sourcePositions[sourceIndices[f*3]],
                sourcePositions[sourceIndices[f*3+2]]-sourcePositions[sourceIndices[f*3]]).sqrMagnitude).First();
            int start=basis*3;
            var a=sourcePositions[sourceIndices[start]];
            var e=sourcePositions[sourceIndices[start+1]]-a; var d=sourcePositions[sourceIndices[start+2]]-a;
            double Dot(Vector3 x,Vector3 y)=>(double)x.x*y.x+(double)x.y*y.y+(double)x.z*y.z;
            double ee=Dot(e,e),dd=Dot(d,d),ed=Dot(e,d),det=ee*dd-ed*ed;
            if (!(det>0)) return null;
            var origin=sourceUv[sourceIndices[start]];
            var u=sourceUv[sourceIndices[start+1]]-origin; var v=sourceUv[sourceIndices[start+2]]-origin;
            var normal=Vector3.Cross(e,d).normalized;
            float span=source.vertexIndices.Max(i=>(sourcePositions[i]-a).magnitude);
            double planeTolerance=Math.Max(1e-7,span*1e-5);
            Vector2 Map(Vector3 position)
            {
                var p=position-a;
                double pe=Dot(p,e),pd=Dot(p,d),x=(pe*dd-pd*ed)/det,y=(pd*ee-pe*ed)/det;
                return new Vector2((float)(origin.x+x*u.x+y*v.x),(float)(origin.y+x*u.y+y*v.y));
            }
            var min=source.vertexIndices.Select(i=>sourceUv[i]).Aggregate(Vector2.Min);
            var max=source.vertexIndices.Select(i=>sourceUv[i]).Aggregate(Vector2.Max);
            double uvTolerance=Math.Max(1e-8,(max-min).magnitude*1e-4);
            foreach(int i in source.vertexIndices)
                if (Math.Abs(Dot(sourcePositions[i]-a,normal))>planeTolerance
                    || Vector2.Distance(Map(sourcePositions[i]),sourceUv[i])>uvTolerance) return null;
            var candidate=new Dictionary<int,Vector2>();
            foreach(int i in target.vertexIndices)
            {
                if (Math.Abs(Dot(targetPositions[i]-a,normal))>planeTolerance) return null;
                candidate.Add(i,Map(targetPositions[i]));
            }
            var chart=new ReverseUvTransfer.Chart {pixels=source.faceIndices.SelectMany(f=>Enumerable.Range(0,3).Select(k=>sourceUv[sourceIndices[f*3+k]])).ToArray(),
                layers=new int[source.faceIndices.Count]};
            var coordinates=target.faceIndices.SelectMany(f=>Enumerable.Range(0,3).Select(k=>candidate[targetIndices[f*3+k]])).ToArray();
            long comparisons=0; var options=new ReverseUvTransfer.Options {comparisonBudget=200000};
            try
            {
                for(int t=0;t<coordinates.Length;t+=3)
                    if (!ReverseUvTransfer.CoveredByParentLayer(chart,0,coordinates,t,options,ref comparisons,CancellationToken.None)) return null;
            }
            catch(InvalidOperationException) {return null;}
            return candidate;
        }

        internal static int Apply(GroupedShellTransfer.TransferResult result,List<UvShell> targets,int[] targetIndices,
            Vector3[] targetPositions,List<UvShell> sources,int[] sourceIndices,Vector3[] sourcePositions,Vector2[] sourceUv,
            TransferMatchTrace trace)
        {
            var previous=new Dictionary<int,Dictionary<int,Vector2>>();
            foreach(var target in targets)
            {
                int shell=target.shellId;
                int source=result.targetShellToSourceShell[shell];
                if (source<0 || result.targetShellStatus[shell]==GroupedShellTransfer.ShellStatus.Rejected) continue;
                var old=target.vertexIndices.ToDictionary(i=>i,i=>result.uv2[i]);
                var before=TransferCandidateQuality.Measure(target,targetIndices,targetPositions,old,false);
                if (!before.NeedsRecovery) continue;
                var candidate=Project(target,targetIndices,targetPositions,sources[source],sourceIndices,sourcePositions,sourceUv);
                if (candidate==null) continue;
                before=TransferCandidateQuality.Measure(target,targetIndices,targetPositions,old);
                var after=TransferCandidateQuality.Measure(target,targetIndices,targetPositions,candidate);
                trace?.RecordProjection(trace.ForShell(shell),"planar-world",source,candidate,after);
                if (!after.Improves(before)) continue;
                previous.Add(shell,old);
                foreach(var point in candidate) result.uv2[point.Key]=point.Value;
            }
            if (previous.Count==0) return 0;
            var baseline=(Vector2[])result.uv2.Clone();
            foreach(var shell in previous.Values) foreach(var point in shell) baseline[point.Key]=point.Value;
            UvAtlasDiagnostics.Report Scan(Vector2[] uv)=>UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=uv,indices=targetIndices,
                charts=new int[uv.Length]},CancellationToken.None,comparisonBudget:2000000,collectConflicts:true);
            var oldScan=Scan(baseline);
            var contacts=new HashSet<(int,int)>(oldScan.conflicts.Select(p=>(Math.Min(p.a,p.b),Math.Max(p.a,p.b))));
            while(previous.Count>0)
            {
                var scan=Scan(result.uv2);
                var refused=new HashSet<int>();
                if (!oldScan.complete || !scan.complete) refused.UnionWith(previous.Keys);
                else foreach(var pair in scan.conflicts)
                    if (!contacts.Contains((Math.Min(pair.a,pair.b),Math.Max(pair.a,pair.b))))
                    { refused.Add(result.faceToTargetShell[pair.a]); refused.Add(result.faceToTargetShell[pair.b]); }
                refused.IntersectWith(previous.Keys);
                if (refused.Count==0) break;
                foreach(int shell in refused)
                {
                    foreach(var point in previous[shell]) result.uv2[point.Key]=point.Value;
                    previous.Remove(shell);
                }
            }
            foreach(int shell in previous.Keys)
                if (trace!=null) trace.ForShell(shell).projectionMethod="planar-world";
            return previous.Count;
        }
    }
}
