using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        static ReverseUvAttachmentSource CaptureShellAttachmentSource(Surface surface,Vector2[] uv,CancellationToken token)
        {
            if(surface.sourceBarycentrics!=null) return null;
            var reference=surface.input.forwardReference;
            if(reference!=null && !reference.Matches(surface.sourcePositions,surface.sourceIndices,uv)) return null;
            var current=surface.indices.Select(i=>uv[i]).ToArray();
            return ReverseUvAttachmentSource.Capture(surface.positions,surface.indices,current,reference?.donors,token);
        }

        enum ShellAttachmentDecision { Refused,Accepted,Exhausted }
        sealed class ShellAttachmentState
        {
            internal Surface surface;
            internal Dictionary<int,int[]> groups;
        }
        readonly struct ShellAttachmentPlan
        {
            internal readonly int a,b,c,d,donor,main;
            internal readonly double angle;
            internal ShellAttachmentPlan((int a,int b) edge,(int a,int b) target,int donor,int main,double angle)
            {a=edge.a;b=edge.b;c=target.a;d=target.b;this.donor=donor;this.main=main;this.angle=angle;}
        }

        internal static Vector2[] AttachPreparedShells(Vector3[] corners,Vector2[] reference,Vector2[] initial,
            (double length,double x,double y)[] frames,Options options,CancellationToken token,out int merged,
            ReverseUvAttachmentSource.Scope scope)
        {
            token.ThrowIfCancellationRequested();merged=0;
            if(scope==null || !scope.Matches(corners,reference,token)) return initial;
            var output=(Vector2[])initial.Clone();var edges=WholeDonorEdges(corners);
            var indices=Enumerable.Range(0,initial.Length).ToArray();
            var allFaces=Enumerable.Range(0,frames.Length).ToArray();
            long remaining=options.comparisonBudget;
            while(true)
            {
                token.ThrowIfCancellationRequested();
                var state=CreateShellAttachmentState(corners,output,indices,allFaces);
                var plans=ShellAttachmentPlans(corners,reference,frames,edges,state,options,scope,token);
                var decision=TryShellAttachment(corners,initial,frames,options,state,plans,output,indices,allFaces,
                    scope,token,ref remaining,out var next);
                if(decision==ShellAttachmentDecision.Exhausted) {merged=0;return initial;}
                if(decision==ShellAttachmentDecision.Refused) return merged==0?initial:output;
                output=next;++merged;
            }
        }

        static ShellAttachmentState CreateShellAttachmentState(Vector3[] corners,Vector2[] output,int[] indices,int[] allFaces)
        {
            var surface=new Surface {positions=corners,indices=indices,pixels=output,
                faces=allFaces.Select(_=>new Face()).ToArray()};
            int next=0;AssignCharts(surface,allFaces,ref next);
            return new ShellAttachmentState {surface=surface,
                groups=allFaces.GroupBy(f=>surface.faces[f].chart).ToDictionary(g=>g.Key,g=>g.ToArray())};
        }

        static List<ShellAttachmentPlan> ShellAttachmentPlans(Vector3[] corners,Vector2[] reference,
            (double length,double x,double y)[] frames,Dictionary<(int,int),List<(int a,int b)>> edges,
            ShellAttachmentState state,Options options,ReverseUvAttachmentSource.Scope scope,CancellationToken token)
        {
            var plans=new List<ShellAttachmentPlan>();
            foreach(var uses in edges.Values)
            {
                token.ThrowIfCancellationRequested();
                if(uses.Count!=2) continue;
                AddShellAttachmentPlan(uses[0],uses[1],corners,reference,frames,state,options,scope,plans);
                AddShellAttachmentPlan(uses[1],uses[0],corners,reference,frames,state,options,scope,plans);
            }
            return plans.OrderByDescending(p=>(corners[p.a]-corners[p.b]).sqrMagnitude).ThenBy(p=>p.angle)
                .ThenByDescending(p=>state.groups[p.main].Length).ThenBy(p=>p.a).ToList();
        }

        static void AddShellAttachmentPlan((int a,int b) x,(int a,int b) y,Vector3[] corners,Vector2[] reference,
            (double length,double x,double y)[] frames,ShellAttachmentState state,Options options,
            ReverseUvAttachmentSource.Scope scope,List<ShellAttachmentPlan> plans)
        {
            int donor=state.surface.faces[x.a/3].chart,main=state.surface.faces[y.a/3].chart;
            if(donor==main || CompareWholeDonorOrder(state.groups[donor],state.groups[main],frames)>=0) return;
            if(!reference[x.a].Equals(reference[y.a]) || !reference[x.b].Equals(reference[y.b]) || !scope.CanJoin(x.a,x.b,y.a,y.b)) return;
            var normal=WholeDonorNormal(corners,x.a/3);var other=WholeDonorNormal(corners,y.a/3);
            if(Vector3.Dot(normal,other)<options.normalDot) return;
            plans.Add(new ShellAttachmentPlan(x,y,donor,main,Vector3.Angle(normal,other)));
        }

        static ShellAttachmentDecision TryShellAttachment(Vector3[] corners,Vector2[] initial,
            (double length,double x,double y)[] frames,Options options,ShellAttachmentState state,
            List<ShellAttachmentPlan> plans,Vector2[] output,int[] indices,int[] allFaces,
            ReverseUvAttachmentSource.Scope scope,CancellationToken token,ref long remaining,out Vector2[] next)
        {
            next=null;
            foreach(var plan in plans)
            {
                token.ThrowIfCancellationRequested();
                if(!TryWholeDonorProposal(corners,output,state.groups[plan.donor],(plan.a,plan.b,plan.c,plan.d),token,scope,out var trial)
                    || !WholeDonorMetrics(initial,trial,allFaces,frames,options)) continue;
                var decision=AuditShellAttachment(corners,initial,trial,frames,options,state,plan,indices,allFaces,scope,token,ref remaining,out next);
                if(decision!=ShellAttachmentDecision.Refused) return decision;
            }
            return ShellAttachmentDecision.Refused;
        }

        static ShellAttachmentDecision AuditShellAttachment(Vector3[] corners,Vector2[] initial,Vector2[] trial,
            (double length,double x,double y)[] frames,Options options,ShellAttachmentState state,ShellAttachmentPlan plan,
            int[] indices,int[] allFaces,ReverseUvAttachmentSource.Scope scope,CancellationToken token,ref long remaining,out Vector2[] next)
        {
            next=null;
            var scan=ScanShellAttachment(trial,indices,token,ref remaining,true);
            if(scan==null || !scan.complete) return ShellAttachmentDecision.Exhausted;
            if(scan.degenerateFaces!=0 || scan.invalidFaces!=0) return ShellAttachmentDecision.Refused;
            if(scan.pairs!=0)
            {
                if(!TryRelocateLooseBlockers(corners,state.surface.pixels,trial,state.surface,state.groups,plan.donor,plan.main,scan,options,token,scope,out var relocated)
                    || !WholeDonorMetrics(initial,relocated,allFaces,frames,options)) return ShellAttachmentDecision.Refused;
                trial=relocated;
                scan=ScanShellAttachment(trial,indices,token,ref remaining,false);
                if(scan==null || !scan.complete) return ShellAttachmentDecision.Exhausted;
                if(scan.pairs!=0 || scan.degenerateFaces!=0 || scan.invalidFaces!=0) return ShellAttachmentDecision.Refused;
            }
            next=trial;return ShellAttachmentDecision.Accepted;
        }

        static UvAtlasDiagnostics.Report ScanShellAttachment(Vector2[] trial,int[] indices,CancellationToken token,ref long remaining,bool conflicts)
        {
            if(remaining<=0) return null;
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=trial,indices=indices,charts=new int[trial.Length]},
                token,comparisonBudget:remaining,collectConflicts:conflicts);
            remaining-=scan.comparisons;return scan;
        }
    }
}
