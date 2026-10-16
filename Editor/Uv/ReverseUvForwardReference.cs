using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        // Restore a reference-connected foundation before current UV islands get
        // independent normalizations. No reference, stale data, or any failed
        // proposal leaves the previous behavior intact.
        internal static Vector2[] RepairForwardReference(Surface surface,Vector2[] prepared,Options options,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var reference=surface.input.forwardReference;
            if(reference==null || surface.sourceBarycentrics!=null
                || !reference.Matches(surface.sourcePositions,surface.sourceIndices,prepared)) return prepared;
            var result=prepared;
            foreach(var faces in reference.Components(token))
            {
                token.ThrowIfCancellationRequested();
                var corners=faces.SelectMany(f=>Enumerable.Range(f*3,3)).ToArray();
                var points=corners.Select(c=>surface.positions[surface.indices[c]]).ToArray();
                var legacy=corners.Select(c=>reference.legacy[c]).ToArray();
                var current=corners.Select(c=>prepared[surface.indices[c]]).ToArray();
                if(!ReferencePatchEligible(points,legacy,current,faces,reference,token)) continue;
                var frames=faces.Select(f=>surface.metricFrames[f]).ToArray();
                Vector2[] proposal;
                try
                {
                    proposal=RepairPreparedShell(points,NormalizeFrozenDensity(legacy,frames,options.seedDensity),frames,options,token);
                    proposal=NormalizeFrozenDensity(proposal,frames,options.seedDensity);
                }
                catch(InvalidOperationException) {continue;}
                if(!ReferencePatchFits(legacy,current,proposal,frames,options,token)) continue;
                if(!TryReferenceVertexChanges(surface,corners,proposal,(prepared,options.seedResolution),out var changes)) continue;
                if(ReferenceEquals(result,prepared)) result=(Vector2[])prepared.Clone();
                foreach(var change in changes) result[change.Key]=change.Value;
            }
            return result;
        }

        static bool ReferencePatchEligible(Vector3[] points,Vector2[] legacy,Vector2[] current,
            int[] faces,ForwardUvReference reference,CancellationToken token)
        {
            if(!UvShellTopologyRepair.IsGeometricDisk(points,token)) return false;
            return ReferenceSharedPointsFit(points,legacy,current,faces,reference)
                && ReferenceWindingFits(legacy,current,faces,reference);
        }

        static bool ReferenceSharedPointsFit(Vector3[] points,Vector2[] legacy,Vector2[] current,int[] faces,ForwardUvReference reference)
        {
            var shared=new Dictionary<Vector3,(Vector2 uv,int donor,Vector2 current)>();
            bool split=false;
            for(int i=0;i<points.Length;++i)
            {
                int source=faces[i/3]*3+i%3;
                if(shared.TryGetValue(points[i],out var previous))
                {
                    if(!previous.uv.Equals(legacy[i]) || previous.donor!=reference.donors[source]) return false;
                    split|=!previous.current.Equals(current[i]);
                }
                else shared.Add(points[i],(legacy[i],reference.donors[source],current[i]));
            }
            return split;
        }

        static bool ReferenceWindingFits(Vector2[] legacy,Vector2[] current,int[] faces,ForwardUvReference reference)
        {
            int winding=0;
            for(int i=0;i<faces.Length;++i)
            {
                int t=i*3;double area=ForwardUvReference.Area(legacy[t],legacy[t+1],legacy[t+2]);
                double now=ForwardUvReference.Area(current[t],current[t+1],current[t+2]);
                if(area==0)
                {
                    if(!reference.rescued[faces[i]] || now==0) return false;
                    continue;
                }
                int sign=Math.Sign(area);
                if(winding!=0 && winding!=sign || now==0 || Math.Sign(now)!=sign) return false;
                winding=sign;
            }
            return winding!=0;
        }

        static bool ReferencePatchFits(Vector2[] legacy,Vector2[] current,Vector2[] proposal,
            (double length,double x,double y)[] frames,Options options,CancellationToken token)
        {
            for(int f=0;f<frames.Length;++f)
            {
                token.ThrowIfCancellationRequested();int t=f*3;
                double before=TriangleAnisotropy(frames[f],current[t],current[t+1],current[t+2]);
                double after=TriangleAnisotropy(frames[f],proposal[t],proposal[t+1],proposal[t+2]);
                double area=ForwardUvReference.Area(legacy[t],legacy[t+1],legacy[t+2]);
                double next=ForwardUvReference.Area(proposal[t],proposal[t+1],proposal[t+2]);
                if(!(after<=options.maxAnisotropy) || !UvTransferTolerance.MetricFits(after,before,options.relativeStretchTolerance)
                    || next==0 || area!=0 && Math.Sign(area)!=Math.Sign(next)) return false;
            }
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=proposal,
                indices=Enumerable.Range(0,proposal.Length).ToArray(),charts=new int[proposal.Length]},token,
                comparisonBudget:options.comparisonBudget);
            return scan.complete && scan.invalidFaces==0 && scan.degenerateFaces==0 && scan.pairs==0;
        }

        static bool TryReferenceVertexChanges(Surface surface,int[] corners,Vector2[] proposal,
            (Vector2[] current,int resolution) state,out Dictionary<int,Vector2> changes)
        {
            changes=new Dictionary<int,Vector2>();var included=new HashSet<int>(corners);
            var shared=new Dictionary<Vector3,Vector2>();
            for(int i=0;i<corners.Length;++i)
            {
                int vertex=surface.indices[corners[i]];var next=proposal[i]/state.resolution;
                if(changes.TryGetValue(vertex,out var previous) && !previous.Equals(next)) return false;
                var point=surface.positions[vertex];
                if(shared.TryGetValue(point,out previous) && !previous.Equals(next)) return false;
                shared[point]=next;changes[vertex]=next;
            }
            for(int c=0;c<surface.indices.Length;++c)
                if(!included.Contains(c) && changes.TryGetValue(surface.indices[c],out var next)
                    && !state.current[surface.indices[c]].Equals(next)) return false;
            return true;
        }
    }
}
