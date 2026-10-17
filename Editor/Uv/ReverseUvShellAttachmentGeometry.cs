using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        // Equal face counts use metric area, then a stable face identity. The
        // selected main never changes during an attachment.
        static int CompareWholeDonorOrder(int[] donor,int[] main,(double length,double x,double y)[] frames)
        {
            int order=donor.Length.CompareTo(main.Length);
            if(order!=0) return order;
            double donorArea=0,mainArea=0;
            foreach(int face in donor) donorArea+=frames[face].length*frames[face].y;
            foreach(int face in main) mainArea+=frames[face].length*frames[face].y;
            order=donorArea.CompareTo(mainArea);
            return order!=0?order:donor.Min().CompareTo(main.Min());
        }

        static bool TryRelocateLooseBlockers(Vector3[] positions,Vector2[] before,Vector2[] trial,
            Surface surface,Dictionary<int,int[]> groups,int donor,int main,UvAtlasDiagnostics.Report scan,
            Options options,CancellationToken token,ReverseUvAttachmentSource.Scope scope,out Vector2[] result)
        {
            result=null;
            if(!TryCollectLooseBlockers(surface,donor,main,scan,token,out var blockers)) return false;
            var moved=(Vector2[])trial.Clone();
            foreach(int blocker in blockers)
            {
                token.ThrowIfCancellationRequested();
                if(!TryTranslateLooseBlocker(positions,before,moved,groups[blocker],options,scope,token)) return false;
            }
            result=moved;return true;
        }

        static bool TryCollectLooseBlockers(Surface surface,int donor,int main,UvAtlasDiagnostics.Report scan,
            CancellationToken token,out SortedSet<int> blockers)
        {
            blockers=new SortedSet<int>();
            foreach(var pair in scan.conflicts)
            {
                token.ThrowIfCancellationRequested();
                int a=surface.faces[pair.a].chart,b=surface.faces[pair.b].chart;
                if(a==main || b==main || a==b || (a!=donor && b!=donor)) return false;
                blockers.Add(a==donor?b:a);
            }
            return blockers.Count!=0;
        }

        // An independent native chart can be translated out of temporary local
        // collisions. A point shared with any outside UV side remains anchored.
        static bool TryTranslateLooseBlocker(Vector3[] positions,Vector2[] before,Vector2[] result,int[] faces,
            Options options,ReverseUvAttachmentSource.Scope scope,CancellationToken token)
        {
            if(!TryWholeDonorCorners(positions,before,faces,scope,token,out var corners)) return false;
            double right=result.Max(p=>(double)p.x),left=corners.Min(c=>(double)result[c].x);
            double offset=Math.Ceiling(right-left+Math.Max(1,options.padding));
            if(!double.IsFinite(offset)) return false;
            foreach(int c in corners)
            {
                token.ThrowIfCancellationRequested();
                result[c]=new Vector2((float)(result[c].x+offset),result[c].y);
            }
            return true;
        }

        static bool TryWholeDonorCorners(Vector3[] positions,Vector2[] before,int[] faces,
            ReverseUvAttachmentSource.Scope scope,CancellationToken token,out int[] corners)
        {
            corners=faces.SelectMany(f=>Enumerable.Range(f*3,3)).ToArray();
            if(!scope.CanMove(corners)) return false;
            var inside=new HashSet<int>(corners);
            var aliases=new HashSet<(Vector3,Vector2)>(corners.Select(c=>(positions[c],before[c])));
            for(int c=0;c<before.Length;++c)
            {
                token.ThrowIfCancellationRequested();
                if(!inside.Contains(c) && aliases.Contains((positions[c],before[c]))) return false;
            }
            return true;
        }

        static Dictionary<(int,int),List<(int a,int b)>> WholeDonorEdges(Vector3[] corners)
        {
            var points=new Dictionary<Vector3,int>();var ids=new int[corners.Length];
            for(int c=0;c<corners.Length;++c)
            {
                if(!points.TryGetValue(corners[c],out int id)) {id=points.Count;points.Add(corners[c],id);}
                ids[c]=id;
            }
            var edges=new Dictionary<(int,int),List<(int a,int b)>>();
            for(int f=0;f<corners.Length/3;++f) for(int k=0;k<3;++k)
            {
                int a=f*3+k,b=f*3+(k+1)%3;
                var key=(Math.Min(ids[a],ids[b]),Math.Max(ids[a],ids[b]));
                if(!edges.TryGetValue(key,out var uses)) {uses=new List<(int,int)>();edges.Add(key,uses);}
                uses.Add(ids[a]<ids[b]?(a,b):(b,a));
            }
            return edges;
        }

        static Vector3 WholeDonorNormal(Vector3[] corners,int f)
            =>MeshGeometry.UnitDirection(Vector3.Cross(corners[f*3+1]-corners[f*3],corners[f*3+2]-corners[f*3]));

        static bool TryWholeDonorProposal(Vector3[] positions,Vector2[] before,int[] faces,
            (int a,int b,int c,int d) edge,CancellationToken token,ReverseUvAttachmentSource.Scope scope,out Vector2[] trial)
        {
            trial=null;
            if(!TryWholeDonorCorners(positions,before,faces,scope,token,out var corners)) return false;
            var from=new[] {before[edge.a],before[edge.b]};var to=new[] {before[edge.c],before[edge.d]};
            double ex=(double)from[1].x-from[0].x,ey=(double)from[1].y-from[0].y;
            double tx=(double)to[1].x-to[0].x,ty=(double)to[1].y-to[0].y;
            double denominator=ex*ex+ey*ey;
            if(!(denominator>0)) return false;
            double a=(tx*ex+ty*ey)/denominator,b=(ty*ex-tx*ey)/denominator;
            double scale=Math.Sqrt(a*a+b*b);
            if(scale<UvChartMerge.MinDensityScale || scale>UvChartMerge.MaxDensityScale) return false;
            double sx=((double)from[0].x+from[1].x)*.5,sy=((double)from[0].y+from[1].y)*.5;
            double cx=((double)to[0].x+to[1].x)*.5,cy=((double)to[0].y+to[1].y)*.5;
            trial=(Vector2[])before.Clone();
            for(int i=0;i<corners.Length;++i)
            {
                token.ThrowIfCancellationRequested();int c=corners[i];
                double x=before[c].x-sx,y=before[c].y-sy;
                trial[c]=new Vector2((float)(a*x-b*y+cx),(float)(b*x+a*y+cy));
                if(positions[c].Equals(positions[edge.a]) && before[c].Equals(before[edge.a])) trial[c]=before[edge.c];
                if(positions[c].Equals(positions[edge.b]) && before[c].Equals(before[edge.b])) trial[c]=before[edge.d];
            }
            return true;
        }

        static bool WholeDonorMetrics(Vector2[] before,Vector2[] after,int[] faces,
            (double length,double x,double y)[] frames,Options options)
        {
            foreach(int f in faces)
            {
                int t=f*3;
                double old=TriangleAnisotropy(frames[f],before[t],before[t+1],before[t+2]);
                double metric=TriangleAnisotropy(frames[f],after[t],after[t+1],after[t+2]);
                if(!double.IsFinite(metric) || metric>options.maxAnisotropy
                    || !UvTransferTolerance.MetricFits(metric,old,options.relativeStretchTolerance)) return false;
                if(Math.Sign(ForwardUvReference.Area(before[t],before[t+1],before[t+2]))
                    !=Math.Sign(ForwardUvReference.Area(after[t],after[t+1],after[t+2]))) return false;
            }
            return true;
        }
    }
}
