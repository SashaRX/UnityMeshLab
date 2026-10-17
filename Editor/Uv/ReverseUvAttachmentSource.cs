using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // A per-operation snapshot of the complete ordinary UV surface. It owns no
    // Unity objects and is never cached across a transfer or serialized.
    internal sealed class ReverseUvAttachmentSource
    {
        readonly Vector3[] points;
        readonly Vector2[] uv;
        readonly int[] donors;
        readonly Dictionary<(Vector3,Vector2),int> aliases=new Dictionary<(Vector3,Vector2),int>();
        readonly Dictionary<(Vector3,Vector3),int> edgeUses=new Dictionary<(Vector3,Vector3),int>();

        ReverseUvAttachmentSource(Vector3[] points,Vector2[] uv,int[] donors,CancellationToken token)
        {
            this.points=points;this.uv=uv;this.donors=donors;
            for(int c=0;c<points.Length;++c)
            {
                token.ThrowIfCancellationRequested();
                var key=(points[c],uv[c]);aliases.TryGetValue(key,out int count);aliases[key]=count+1;
            }
            for(int f=0;f<points.Length;f+=3)
                for(int k=0;k<3;++k)
                {
                    token.ThrowIfCancellationRequested();
                    var edge=ReverseUvCorrespondenceGraph.Key(points[f+k],points[f+(k+1)%3]);
                    edgeUses.TryGetValue(edge,out int count);edgeUses[edge]=count+1;
                }
        }

        internal static ReverseUvAttachmentSource Capture(Vector3[] vertices,int[] indices,Vector2[] cornerUv,
            int[] cornerDonors,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(indices.Length%3!=0 || cornerUv.Length!=indices.Length
                || cornerDonors!=null && cornerDonors.Length!=indices.Length) return null;
            var points=new Vector3[indices.Length];
            for(int c=0;c<indices.Length;++c)
            {
                token.ThrowIfCancellationRequested();
                if(indices[c]<0 || indices[c]>=vertices.Length) return null;
                points[c]=vertices[indices[c]];
                if(!Finite(points[c],cornerUv[c])) return null;
            }
            return new ReverseUvAttachmentSource(points,(Vector2[])cornerUv.Clone(),
                cornerDonors==null?null:(int[])cornerDonors.Clone(),token);
        }

        static bool Finite(Vector3 p,Vector2 t)=>float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z)
            && float.IsFinite(t.x) && float.IsFinite(t.y);

        internal Scope ForPatch(int[] corners,Vector2[] localReference,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(corners.Length!=localReference.Length || corners.Length%3!=0) return null;
            var included=new HashSet<int>();var counts=new Dictionary<(Vector3,Vector2),int>();
            foreach(int c in corners)
            {
                token.ThrowIfCancellationRequested();
                if(c<0 || c>=points.Length || !included.Add(c)) return null;
                var key=(points[c],uv[c]);counts.TryGetValue(key,out int count);counts[key]=count+1;
            }
            var locked=new bool[corners.Length];
            for(int i=0;i<corners.Length;++i)
            {
                token.ThrowIfCancellationRequested();int c=corners[i];
                locked[i]=aliases[(points[c],uv[c])]>counts[(points[c],uv[c])];
            }
            return new Scope(this,corners,localReference,locked);
        }

        internal sealed class Scope
        {
            readonly ReverseUvAttachmentSource source;
            readonly int[] corners;
            readonly Vector2[] reference;
            readonly bool[] locked;

            internal Scope(ReverseUvAttachmentSource source,int[] corners,Vector2[] reference,bool[] locked)
            {this.source=source;this.corners=(int[])corners.Clone();this.reference=(Vector2[])reference.Clone();this.locked=locked;}

            internal bool Matches(Vector3[] points,Vector2[] reference,CancellationToken token)
            {
                if(points.Length!=corners.Length || reference.Length!=corners.Length) return false;
                for(int c=0;c<corners.Length;++c)
                {
                    token.ThrowIfCancellationRequested();
                    if(!source.points[corners[c]].Equals(points[c]) || !this.reference[c].Equals(reference[c])) return false;
                }
                return true;
            }

            internal bool CanMove(int[] patchCorners)
            {
                foreach(int c in patchCorners) if(locked[c]) return false;
                return true;
            }

            internal bool CanJoin(int a,int b,int c,int d)
            {
                int x=corners[a],y=corners[b],z=corners[c],w=corners[d];
                var edge=ReverseUvCorrespondenceGraph.Key(source.points[x],source.points[y]);
                if(source.edgeUses[edge]!=2 || !source.uv[x].Equals(source.uv[z]) || !source.uv[y].Equals(source.uv[w])) return false;
                return source.donors==null || source.donors[x]==source.donors[z] && source.donors[y]==source.donors[w];
            }
        }
    }
}
