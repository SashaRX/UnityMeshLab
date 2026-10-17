using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Managed provenance of the selected forward result. Owns no Unity objects,
    // is never serialized, and is usable only with its exact ordered mesh/UVs.
    internal sealed class ForwardUvReference
    {
        internal readonly Vector3[] positions;
        internal readonly Vector2[] legacy, current;
        internal readonly int[] donors;
        internal readonly bool[] rescued;

        ForwardUvReference(Vector3[] positions,Vector2[] legacy,Vector2[] current,int[] donors,bool[] rescued)
        {this.positions=positions;this.legacy=legacy;this.current=current;this.donors=donors;this.rescued=rescued;}

        internal static ForwardUvReference Capture(Mesh mesh,int[] originalIndices,Vector2[] legacyUv,int[] legacySources,
            int[] originalFaces,Vector3[] barycentrics,HashSet<int> projectedOriginals)
        {
            var indices=mesh.triangles;var vertices=mesh.vertices;var uv=mesh.uv2;
            var positions=new Vector3[indices.Length];var legacy=new Vector2[indices.Length];
            var current=new Vector2[indices.Length];var donors=new int[indices.Length];var rescued=new bool[originalFaces.Length];
            for(int f=0;f<originalFaces.Length;++f)
            {
                int old=originalFaces[f]*3,t=f*3;
                var a=legacyUv[originalIndices[old]];var b=legacyUv[originalIndices[old+1]];var c=legacyUv[originalIndices[old+2]];
                rescued[f]=projectedOriginals.Contains(originalFaces[f]) && Area(a,b,c)==0;
                for(int k=0;k<3;++k)
                {
                    int corner=t+k;var bary=barycentrics[corner];
                    positions[corner]=vertices[indices[corner]];current[corner]=uv[indices[corner]];
                    legacy[corner]=a*bary.x+b*bary.y+c*bary.z;
                    int nearest=2;
                    if(bary.x>=bary.y && bary.x>=bary.z) nearest=0;
                    else if(bary.y>=bary.z) nearest=1;
                    donors[corner]=legacySources[originalIndices[old+nearest]];
                }
            }
            return new ForwardUvReference(positions,legacy,current,donors,rescued);
        }

        internal bool Matches(Mesh mesh)
        {
            return Matches(mesh.vertices,mesh.triangles,mesh.uv2);
        }

        internal bool Matches(Vector3[] vertices,int[] indices,Vector2[] uv)
        {
            if(indices.Length!=positions.Length || uv.Length!=vertices.Length) return false;
            for(int c=0;c<indices.Length;++c)
                if(!positions[c].Equals(vertices[indices[c]]) || !current[c].Equals(uv[indices[c]])) return false;
            return true;
        }

        // Publication can clamp UVs without changing the chosen legacy/donor
        // provenance. Reapply only that exact policy, never refresh arbitrary
        // stale coordinates into a supposedly matching reference.
        internal ForwardUvReference ClampPublishedCoordinates(Mesh prepared)
        {
            if(!Matches(prepared)) return null;
            var published=(Vector2[])current.Clone();XatlasRepack.ClampUvsToUnit(published);
            return new ForwardUvReference(positions,legacy,published,donors,rescued);
        }

        internal ForwardUvReference RetainFaces(int[] faces)
        {
            var corners=faces.SelectMany(f=>Enumerable.Range(f*3,3)).ToArray();
            return new ForwardUvReference(corners.Select(c=>positions[c]).ToArray(),corners.Select(c=>legacy[c]).ToArray(),
                corners.Select(c=>current[c]).ToArray(),corners.Select(c=>donors[c]).ToArray(),faces.Select(f=>rescued[f]).ToArray());
        }

        internal static double Area(Vector2 a,Vector2 b,Vector2 c)
            =>((double)b.x-a.x)*((double)c.y-a.y)-((double)b.y-a.y)*((double)c.x-a.x);

        // Edges join only when BOTH original coordinates and donor identities
        // agree. No generated current UV seam is mistaken for an authored seam.
        internal List<int[]> Components(CancellationToken token)
        {
            var edges=ReferenceEdges(token);
            var blocked=new HashSet<int>();var neighbors=ReferenceNeighbors(edges,blocked);
            var result=new List<int[]>();var visited=new bool[rescued.Length];
            for(int seed=0;seed<visited.Length;++seed)
            {
                token.ThrowIfCancellationRequested();if(visited[seed]) continue;
                var faces=ReferenceComponent(seed,visited,neighbors);
                if(faces.Any(f=>blocked.Contains(f)) || !faces.Any(f=>rescued[f])) continue;
                faces.Sort();result.Add(faces.ToArray());
            }
            return result;
        }

        Dictionary<(int,int),List<(int a,int b)>> ReferenceEdges(CancellationToken token)
        {
            var edges=new Dictionary<(int,int),List<(int a,int b)>>();
            var vertices=new Dictionary<Vector3,int>();var ids=new int[positions.Length];
            for(int c=0;c<ids.Length;++c)
            {
                token.ThrowIfCancellationRequested();
                if(!vertices.TryGetValue(positions[c],out int id)) {id=vertices.Count;vertices.Add(positions[c],id);}
                ids[c]=id;
            }
            for(int f=0;f<rescued.Length;++f) for(int k=0;k<3;++k)
            {
                int a=f*3+k,b=f*3+(k+1)%3;var key=(Math.Min(ids[a],ids[b]),Math.Max(ids[a],ids[b]));
                if(!edges.TryGetValue(key,out var uses)) {uses=new List<(int,int)>();edges.Add(key,uses);}
                uses.Add(ids[a]<ids[b]?(a,b):(b,a));
            }
            return edges;
        }

        List<int>[] ReferenceNeighbors(Dictionary<(int,int),List<(int a,int b)>> edges,HashSet<int> blocked)
        {
            var neighbors=Enumerable.Range(0,rescued.Length).Select(_=>new List<int>()).ToArray();
            foreach(var uses in edges.Values)
            {
                if(uses.Count>2)
                {
                    foreach(var edge in uses) blocked.Add(edge.a/3);
                    continue;
                }
                if(uses.Count!=2) continue;
                var x=uses[0];var y=uses[1];
                if(!SameReference(x.a,y.a) || !SameReference(x.b,y.b)) continue;
                neighbors[x.a/3].Add(y.a/3);neighbors[y.a/3].Add(x.a/3);
            }
            return neighbors;
        }

        static List<int> ReferenceComponent(int seed,bool[] visited,List<int>[] neighbors)
        {
            var faces=new List<int>();var queue=new Queue<int>();queue.Enqueue(seed);visited[seed]=true;
            while(queue.Count>0)
            {
                int f=queue.Dequeue();faces.Add(f);
                foreach(int n in neighbors[f]) if(!visited[n]) {visited[n]=true;queue.Enqueue(n);}
            }
            return faces;
        }

        bool SameReference(int a,int b)=>donors[a]==donors[b] && donors[a]>=0 && legacy[a].Equals(legacy[b]);
    }
}
