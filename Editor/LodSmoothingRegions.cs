using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Connected normal-continuous regions of working LOD0, not FBX smoothing bitmasks.
    // Only an unambiguous, oppositely oriented pair of geometric edges joins UV
    // duplicates. Equal positions at an isolated corner never establish adjacency.
    internal sealed class LodSmoothingRegions
    {
        [Serializable] internal struct Error
        {
            public int region, samples, unresolved;
            public float rms, maximum;
        }
        readonly struct Sample
        {
            internal readonly int region, a, b, c;
            internal readonly Vector3 bary, normal;
            internal readonly double weight;
            internal Sample(int region,int[] faces,int i,Vector3 bary,Vector3 normal,double weight)
            { this.region = region; a = faces[i]; b = faces[i+1]; c = faces[i+2]; this.bary = bary; this.normal = normal; this.weight = weight; }
        }
        internal sealed class Slot
        {
            internal readonly int[] sourceFaces, targetFaces, sourceRegions, targetRegions;
            internal readonly Dictionary<int,Surface> sourceSurfaces = new Dictionary<int,Surface>();
            internal readonly Dictionary<int,Surface> targetSurfaces = new Dictionary<int,Surface>();
            internal Slot(LodMeshData source,LodMeshData target,int slot,ref int count,Func<bool> cancelled)
            {
                sourceFaces = LodSurfaceValidation.SurfaceTriangles(source,LodMeshData.Triangles(source.source,slot));
                targetFaces = LodSurfaceValidation.SurfaceTriangles(target,LodMeshData.Triangles(target.source,slot));
                sourceRegions = Regions(source,sourceFaces);
                var ids = new Dictionary<int,int>();
                foreach (int id in sourceRegions.Distinct()) ids[id] = count++;
                for (int i = 0; i < sourceRegions.Length; i++) sourceRegions[i] = ids[sourceRegions[i]];
                Fill(source,sourceFaces,sourceRegions,sourceSurfaces);
                targetRegions = Enumerable.Repeat(-1,targetFaces.Length/3).ToArray();
                if (sourceFaces.Length == 0) return;
                var all = new Surface(source,sourceFaces);
                bool identical = sourceFaces.SequenceEqual(targetFaces) && source.positions.SequenceEqual(target.positions);
                for (int i = 0; i < targetFaces.Length; i += 3)
                {
                    Cancel(cancelled);
                    int region = -1; bool mixed = false;
                    foreach (var bary in Interior)
                    {
                        var hit = all.Hit(target,targetFaces,i,bary,identical);
                        if (hit.triangleIndex < 0) { mixed = true; break; }
                        int current = sourceRegions[hit.triangleIndex];
                        if (region >= 0 && region != current) { mixed = true; break; }
                        region = current;
                    }
                    if (!mixed) targetRegions[i/3] = region;
                }
                Fill(target,targetFaces,targetRegions,targetSurfaces);
            }
            static void Fill(LodMeshData data,int[] faces,int[] regions,Dictionary<int,Surface> surfaces)
            {
                var groups = new Dictionary<int,List<int>>();
                for (int i = 0; i < faces.Length; i += 3)
                {
                    int region = regions[i/3]; if (region < 0) continue;
                    if (!groups.TryGetValue(region,out var group)) groups[region] = group = new List<int>();
                    group.Add(faces[i]); group.Add(faces[i+1]); group.Add(faces[i+2]);
                }
                foreach (var group in groups) surfaces[group.Key] = new Surface(data,group.Value.ToArray());
            }
        }
        internal sealed class Surface
        {
            internal readonly LodMeshData data;
            internal readonly int[] faces;
            readonly TriangleBvh tree;
            readonly Vector3[] geometric;
            internal Surface(LodMeshData data,int[] faces)
            {
                this.data = data; this.faces = faces; tree = new TriangleBvh(data.positions,faces);
                geometric = Enumerable.Range(0,faces.Length/3).Select(i => LodSurfaceValidation.GeometricNormal(data,faces,i*3)).ToArray();
            }
            internal TriangleBvh.HitResult Hit(LodMeshData from,int[] triangles,int i,Vector3 bary,bool identical = false)
                => LodSurfaceValidation.CorrespondingHit(from,triangles,i,bary,data,faces,tree,geometric,
                    LodSurfaceValidation.GeometricNormal(from,triangles,i),identical);
        }
        static readonly Vector3[] Interior = { Vector3.one/3,new Vector3(2f/3,1f/6,1f/6),
            new Vector3(1f/6,2f/3,1f/6),new Vector3(1f/6,1f/6,2f/3) };
        static readonly Vector3[] Extrema = { Vector3.right,Vector3.up,Vector3.forward,
            new Vector3(.5f,.5f,0),new Vector3(.5f,0,.5f),new Vector3(0,.5f,.5f) };
        readonly List<Sample> samples = new List<Sample>();
        readonly int[] unresolved;
        internal readonly List<Slot> slots = new List<Slot>();
        internal readonly int[] owners;
        internal readonly bool[] pinned;
        internal readonly int count, mixedFaces, linkedDuplicates, missingRegions;

        internal LodSmoothingRegions(LodMeshData source,LodMeshData target,Func<bool> cancelled)
        {
            int regions = 0;
            for (int slot = 0; slot < source.source.subMeshCount; slot++)
                slots.Add(new Slot(source,target,slot,ref regions,cancelled));
            count = regions; unresolved = new int[count]; owners = Enumerable.Range(0,target.positions.Length).ToArray(); pinned = new bool[owners.Length];
            var vertexRegions = Enumerable.Repeat(-1,owners.Length).ToArray();
            foreach (var slot in slots)
            {
                for (int i = 0; i < slot.targetFaces.Length; i += 3)
                {
                    int region = slot.targetRegions[i/3];
                    if (region < 0) mixedFaces++;
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int v = slot.targetFaces[i+corner];
                        if (region < 0 || vertexRegions[v] >= 0 && vertexRegions[v] != region) pinned[v] = true;
                        if (region >= 0) vertexRegions[v] = region;
                    }
                }
                foreach (var pair in EdgePairs(target,slot.targetFaces))
                {
                    var a = pair[0]; var b = pair[1];
                    if (slot.targetRegions[a.face] < 0 || slot.targetRegions[a.face] != slot.targetRegions[b.face]) continue;
                    if (a.a == b.b && a.b == b.a) continue; // Already shared render vertices.
                    if (!ContinuousSourceEdge(slot,target,a,b)) continue;
                    Union(owners,a.a,b.b); Union(owners,a.b,b.a);
                }
                foreach (var region in slot.sourceSurfaces)
                {
                    Cancel(cancelled);
                    if (!slot.targetSurfaces.TryGetValue(region.Key,out var destination)) { missingRegions++; continue; }
                    Cache(region.Key,destination,region.Value,false,cancelled);
                    Cache(region.Key,region.Value,destination,true,cancelled);
                }
            }
            for (int i = 0; i < owners.Length; i++) { owners[i] = Root(owners,i); if (owners[i] != i) linkedDuplicates++; }
            var fixedOwners = new HashSet<int>(Enumerable.Range(0,owners.Length).Where(i => pinned[i]).Select(i => owners[i]));
            for (int i = 0; i < owners.Length; i++) if (fixedOwners.Contains(owners[i])) pinned[i] = true;
        }

        void Cache(int region,Surface from,Surface to,bool reverse,Func<bool> cancelled)
        {
            bool identical = from.faces.SequenceEqual(to.faces) && from.data.positions.SequenceEqual(to.data.positions);
            float scale = reverse ? from.data.scale : to.data.scale;
            for (int i = 0; i < from.faces.Length; i += 3)
            {
                Cancel(cancelled);
                double weight = Vector3.Cross((from.data.positions[from.faces[i+1]]-from.data.positions[from.faces[i]])/scale,
                    (from.data.positions[from.faces[i+2]]-from.data.positions[from.faces[i]])/scale).magnitude/8;
                foreach (var bary in Interior) Add(bary,weight);
                foreach (var bary in Extrema) Add(bary,0);
                void Add(Vector3 bary,double area)
                {
                    var hit = to.Hit(from.data,from.faces,i,bary,identical);
                    if (hit.triangleIndex < 0)
                    {
                        unresolved[region]++;
                        // An incomplete correspondence cannot authorize a normal fit.
                        foreach (int v in (reverse ? to : from).faces) pinned[v] = true;
                        return;
                    }
                    int j = hit.triangleIndex*3;
                    var original = reverse ? from : to; int sourceIndex = reverse ? i : j;
                    var sourceBary = reverse ? bary : hit.barycentric;
                    var destination = reverse ? to : from;
                    var n = original.data.normals[original.faces[sourceIndex]]*sourceBary.x +
                        original.data.normals[original.faces[sourceIndex+1]]*sourceBary.y + original.data.normals[original.faces[sourceIndex+2]]*sourceBary.z;
                    samples.Add(new Sample(region,destination.faces,reverse ? j : i,reverse ? hit.barycentric : bary,n.normalized,area));
                }
            }
        }

        internal Error[] Measure(Vector3[] normals,Func<bool> cancelled)
        {
            var result = Enumerable.Range(0,count).Select(i => new Error { region = i,unresolved = unresolved[i] }).ToArray();
            var area = new double[count]; var squared = new double[count];
            foreach (var sample in samples)
            {
                Cancel(cancelled);
                float angle = Vector3.Angle(sample.normal,normals[sample.a]*sample.bary.x+normals[sample.b]*sample.bary.y+normals[sample.c]*sample.bary.z);
                int id = sample.region; result[id].maximum = Mathf.Max(result[id].maximum,angle); result[id].samples++;
                area[id] += sample.weight; squared[id] += angle*angle*sample.weight;
            }
            for (int i = 0; i < count; i++) result[i].rms = area[i] > 0 ? Mathf.Sqrt((float)(squared[i]/area[i])) : 0;
            return result;
        }
        internal static bool NoRegression(Error[] after,Error[] before)
            => after.Length == before.Length && after.Zip(before,(a,b) => a.region == b.region && a.samples == b.samples && a.unresolved == b.unresolved &&
                !float.IsNaN(a.rms) && !float.IsInfinity(a.rms) && !float.IsNaN(a.maximum) && !float.IsInfinity(a.maximum) &&
                a.rms <= b.rms+1e-4f && a.maximum <= b.maximum+1e-4f).All(ok => ok);

        readonly struct Edge
        {
            internal readonly int face,a,b;
            internal Edge(int face,int a,int b) { this.face = face; this.a = a; this.b = b; }
        }
        static IEnumerable<Edge[]> EdgePairs(LodMeshData mesh,int[] faces)
        {
            var points = new Dictionary<Vector3,int>();
            int Point(int v)
            {
                var p = mesh.positions[v];
                if (!points.TryGetValue(p,out int id)) points[p] = id = points.Count;
                return id;
            }
            var edges = new Dictionary<(int,int),List<Edge>>();
            for (int i = 0; i < faces.Length; i += 3) for (int corner = 0; corner < 3; corner++)
            {
                int a = faces[i+corner],b = faces[i+(corner+1)%3],pa = Point(a),pb = Point(b);
                var key = pa < pb ? (pa,pb) : (pb,pa);
                if (!edges.TryGetValue(key,out var list)) edges[key] = list = new List<Edge>();
                list.Add(new Edge(i/3,a,b));
            }
            foreach (var edge in edges.Values)
                if (edge.Count == 2 && edge[0].face != edge[1].face &&
                    mesh.positions[edge[0].a].Equals(mesh.positions[edge[1].b]) && mesh.positions[edge[0].b].Equals(mesh.positions[edge[1].a]))
                {
                    // Coincident opposite triangles are overlapping shells, not
                    // the two sides of a verified smooth adjacency.
                    Vector3 Third(Edge e)
                    {
                        for (int corner = 0; corner < 3; corner++)
                        {
                            int v = faces[e.face*3+corner]; if (v != e.a && v != e.b) return mesh.positions[v];
                        }
                        return mesh.positions[e.a];
                    }
                    var thirdA = Third(edge[0]); var thirdB = Third(edge[1]);
                    if (!thirdA.Equals(thirdB)) yield return edge.ToArray();
                }
        }
        static bool Continuous(LodMeshData mesh,Edge a,Edge b)
            => SameNormal(mesh.normals[a.a],mesh.normals[b.b]) && SameNormal(mesh.normals[a.b],mesh.normals[b.a]);
        static bool ContinuousSourceEdge(Slot slot,LodMeshData target,Edge a,Edge b)
        {
            var source = slot.sourceSurfaces[slot.targetRegions[a.face]];
            bool TryNormal(Edge edge,int vertex,out Vector3 normal)
            {
                int i = edge.face*3,corner = Array.IndexOf(slot.targetFaces,vertex,i,3)-i;
                var bary = Vector3.zero; bary[corner] = 1;
                var hit = source.Hit(target,slot.targetFaces,i,bary);
                if (hit.triangleIndex < 0) { normal = Vector3.zero; return false; }
                int j = hit.triangleIndex*3;
                normal = source.data.normals[source.faces[j]]*hit.barycentric.x + source.data.normals[source.faces[j+1]]*hit.barycentric.y +
                    source.data.normals[source.faces[j+2]]*hit.barycentric.z;
                return true;
            }
            return TryNormal(a,a.a,out var aa) && TryNormal(b,b.b,out var bb) && SameNormal(aa,bb) &&
                TryNormal(a,a.b,out var ab) && TryNormal(b,b.a,out var ba) && SameNormal(ab,ba);
        }
        static bool SameNormal(Vector3 a,Vector3 b)
            => a.sqrMagnitude > .5f && b.sqrMagnitude > .5f && (a.normalized-b.normalized).sqrMagnitude <= 1e-10f;
        internal static int[] Regions(LodMeshData mesh,int[] faces)
        {
            var parent = Enumerable.Range(0,faces.Length/3).ToArray();
            foreach (var pair in EdgePairs(mesh,faces)) if (Continuous(mesh,pair[0],pair[1])) Union(parent,pair[0].face,pair[1].face);
            return parent.Select((_,i) => Root(parent,i)).ToArray();
        }
        static int Root(int[] parent,int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        static void Union(int[] parent,int first,int second)
        {
            first = Root(parent,first); second = Root(parent,second);
            parent[Math.Max(first,second)] = Math.Min(first,second);
        }
        static void Cancel(Func<bool> cancelled) { if (cancelled?.Invoke() == true) throw new OperationCanceledException("LOD smoothing analysis cancelled."); }
    }
}
