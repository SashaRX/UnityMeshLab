using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Bounded, audited annular zipper. Rim vertices are never moved or resampled.</summary>
    internal static class RemeshBridge
    {
        const int MaxPhases = 32, PathsPerState = 8, MaxStates = 600000;
        const long MaxSearchBytes = 64L * 1024 * 1024;
        sealed class Path { internal double score; internal Path previous; internal char move; internal int order, length; }
        sealed class Candidate { internal Path path; internal List<int> b; }
        internal sealed class SearchReport
        {
            internal SearchReport(bool captureRejected = false) { this.captureRejected=captureRejected; }
            internal readonly bool captureRejected;
            internal int phases, states, candidates, audited, topologyRejected;
            internal long plannedStates, plannedBytes;
            internal string firstContact;
            internal int[] firstRejectedIndices;
            internal string Description => $"{phases} seam phases; {states}/{plannedStates} search states; {plannedBytes} estimated retained bytes; {candidates} candidates; {audited} audited; {topologyRejected} topology refusals";
        }

        internal static int[] Generate(Vector3[] p, int[] source, List<int> a, List<int> forwardB,
            CancellationToken token, ref int trials, out int contacts, RemeshPlanarCap.ExternalContacts external = null,
            SearchReport report = null, int maxStates = MaxStates, long maxSearchBytes = MaxSearchBytes)
        {
            token.ThrowIfCancellationRequested();
            contacts = 0;
            if (a.Count < 3 || forwardB.Count < 3) throw new InvalidOperationException("Bridge refused: each rim needs at least three edges.");
            report ??= new SearchReport();
            CheckBudget(a.Count,forwardB.Count,report,maxStates,maxSearchBytes);
            // Use the smaller rim for seam phases. This does not alter either
            // source winding: the second rim is reversed when constructing a strip.
            if (forwardB.Count > a.Count) (a,forwardB) = (forwardB,a);
            var used = new HashSet<int>(a);
            foreach (int v in forwardB) if (used.Contains(v)) throw new InvalidOperationException("Bridge refused: rims touch.");
            var topology = RemeshTopology.Inspect(p, source, token);
            if (!topology.Valid) throw new InvalidOperationException("Bridge refused: source topology: " + topology.Description);
            var normals = MeshGeometry.FaceNormals(p, source);
            var na = Collar(topology, normals, a); var nbForward = Collar(topology, normals, forwardB);
            var low = p[a[0]]; var high = low;
            foreach (int v in a) { low = Vector3.Min(low, p[v]); high = Vector3.Max(high, p[v]); }
            foreach (int v in forwardB) { low = Vector3.Min(low, p[v]); high = Vector3.Max(high, p[v]); }
            float scale = (high-low).magnitude;
            if (!(scale > 0) || !float.IsFinite(scale)) throw new InvalidOperationException("Bridge refused: invalid rim span.");
            var normalized = new Vector3[p.Length]; var exact = new RemeshCapIntersection.Q[p.Length][];
            for (int i = 0; i < p.Length; ++i) { normalized[i] = (p[i]-low)/scale; exact[i] = RemeshCapIntersection.Point(p[i]); }
            var selectedBoundary = new HashSet<(Vector3,Vector3)>();
            AddBoundary(p,a,selectedBoundary); AddBoundary(p,forwardB,selectedBoundary);
            if (selectedBoundary.Count != a.Count+forwardB.Count || !selectedBoundary.IsSubsetOf(topology.boundary))
                throw new InvalidOperationException("Bridge refused: selected rims must be disjoint source boundaries.");
            var remainingBoundary = new HashSet<(Vector3,Vector3)>(topology.boundary);
            remainingBoundary.ExceptWith(selectedBoundary);
            int componentCount = topology.euler.Count;
            if (Component(topology,a) != Component(topology,forwardB)) --componentCount;
            int states = 0, order = 0; var candidates = new List<Candidate>();
            // Complete the bounded search profile before auditing. Auditing in
            // score order lets us accept its best valid strip without spending
            // the contact budget on already dominated candidates.
            foreach (int phase in Phases(normalized,a,forwardB)) {
                ++report.phases;
                var b = new List<int>(); var nb = new Vector3[forwardB.Count];
                for (int j = 0; j < forwardB.Count; ++j) {
                    b.Add(forwardB[(phase-j+forwardB.Count)%forwardB.Count]);
                    nb[j] = nbForward[(phase-j-1+forwardB.Count*2)%forwardB.Count];
                }
                foreach (var path in Paths(normalized,a,b,na,nb,token,ref states,ref order,maxStates))
                    candidates.Add(new Candidate { path=path,b=b });
            }
            report.states = states; report.candidates = candidates.Count;
            candidates.Sort((x,y)=>Compare(x.path,y.path));
            foreach (var choice in candidates) {
                token.ThrowIfCancellationRequested();
                var patch = Faces(a,choice.b,choice.path); var candidate = new int[source.Length+patch.Length];
                Array.Copy(source,candidate,source.Length); Array.Copy(patch,0,candidate,source.Length,patch.Length);
                var after = RemeshTopology.Inspect(p,candidate,token);
                if (!after.Valid || !after.boundary.SetEquals(remainingBoundary) || after.euler.Count != componentCount ||
                    Euler(after) != Euler(topology)) { ++report.topologyRejected; continue; }
                // The patch itself must be one annulus; duplicates, fans and winding
                // are checked above, not inferred from the zipper's score.
                var annulus = RemeshTopology.Inspect(p,patch,token);
                if (!annulus.Valid || annulus.euler.Count != 1 || annulus.euler[0] != 0 || !annulus.boundary.SetEquals(selectedBoundary))
                    { ++report.topologyRejected; continue; }
                var candidateContacts = external?.Fork();
                ++report.audited;
                try { RemeshPlanarCap.AuditContacts(p,exact,candidate,source.Length/3,token,ref trials,out int tested,candidateContacts); contacts += tested; }
                catch (InvalidOperationException ex) when (ex.Message.Contains("contacts face")) {
                    if (report.firstContact==null) {
                        report.firstContact=ex.Message;
                        if (report.captureRejected) report.firstRejectedIndices=candidate;
                    }
                    continue;
                }
                external?.Merge(candidateContacts);
                return patch;
            }
            throw new InvalidOperationException("Bridge refused: no non-intersecting annulus in the bounded zipper family (" + report.Description + ")." +
                (report.firstContact == null ? "" : " First rejected contact: " + report.firstContact));
        }

        internal static void CheckBudget(int first,int second,SearchReport report,
            int maxStates = MaxStates,long maxSearchBytes = MaxSearchBytes)
        {
            if (first<3 || second<3) throw new InvalidOperationException("Bridge refused: each rim needs at least three edges.");
            long cells=((long)first+1)*((long)second+1);
            int phases=Math.Min(Math.Min(first,second),MaxPhases);
            report.plannedStates=Multiply(cells,phases);
            // Estimate retained storage, not the process heap: a grid cell/list/
            // backing array plus eight path nodes, and all terminal ancestry.
            // 64 bytes per path and 256 per cell conservatively cover supported
            // managed layouts. Transient allocations and GC timing are separate.
            long gridBytes=Multiply(cells,256+PathsPerState*64);
            long ancestryBytes=Multiply((long)first+second+1,(long)phases*PathsPerState*64);
            report.plannedBytes=gridBytes>long.MaxValue-ancestryBytes ? long.MaxValue : gridBytes+ancestryBytes;
            if (report.plannedStates>maxStates)
                throw new InvalidOperationException($"Bridge search budget exceeded before allocation: {first}x{second} rims need {report.plannedStates} states; budget {maxStates}. No partial winner was accepted.");
            if (report.plannedBytes>maxSearchBytes)
                throw new InvalidOperationException($"Bridge memory budget exceeded before allocation: {first}x{second} rims need an estimated {report.plannedBytes} retained bytes; budget {maxSearchBytes}. No partial winner was accepted.");
        }

        static long Multiply(long value,long count) => value>long.MaxValue/count ? long.MaxValue : value*count;

        static void AddBoundary(Vector3[] p,List<int> loop,HashSet<(Vector3,Vector3)> edges)
        {
            for (int i=0;i<loop.Count;++i) edges.Add(RemeshTopology.Snapshot.BoundaryKey(p[loop[i]],p[loop[(i+1)%loop.Count]]));
        }

        static int Component(RemeshTopology.Snapshot topology,List<int> loop)
        {
            int a=topology.slots[loop[0]],b=topology.slots[loop[1]];
            return topology.components.Find(topology.edges[a<b?(a,b):(b,a)].firstFace);
        }

        static int Euler(RemeshTopology.Snapshot topology)
        {
            int sum=0;
            foreach (int value in topology.euler) { sum+=value; }
            return sum;
        }

        static List<int> Phases(Vector3[] p,List<int> a,List<int> b)
        {
            var phases = new List<int>();
            if (b.Count <= MaxPhases) {
                for (int j=0;j<b.Count;++j) { phases.Add(j); }
                return phases;
            }
            // Half the profile covers the whole rim, half concentrates on short
            // cross-rim links. No claim of exhaustive correspondence is made.
            for (int j=0;j<MaxPhases/2;++j) phases.Add(j*b.Count/(MaxPhases/2));
            var nearest = new List<int>(); for (int j=0;j<b.Count;++j) nearest.Add(j);
            nearest.Sort((x,y)=> {
                int order=(p[b[x]]-p[a[0]]).sqrMagnitude.CompareTo((p[b[y]]-p[a[0]]).sqrMagnitude);
                return order!=0?order:x.CompareTo(y);
            });
            foreach (int phase in nearest) {
                if (!phases.Contains(phase)) phases.Add(phase);
                if (phases.Count==MaxPhases) break;
            }
            return phases;
        }

        static Vector3[] Collar(RemeshTopology.Snapshot topology, Vector3[] normals, List<int> loop)
        {
            var result = new Vector3[loop.Count];
            for (int i = 0; i < loop.Count; ++i) {
                int a = loop[i], b = loop[(i+1)%loop.Count];
                a = topology.slots[a]; b = topology.slots[b];
                result[i] = normals[topology.edges[a < b ? (a,b) : (b,a)].firstFace];
            }
            return result;
        }

        // Automatic selection requires mutual collar continuation toward the other
        // rim. A pair of opposite box holes grows away from one another and fails.
        // This is bounded shape evidence; Generate still audits topology/contacts.
        internal static bool ContinuesToward(Vector3[] p, RemeshTopology.Snapshot topology, Vector3[] normals, List<int> a, List<int> b)
        {
            var collar = Collar(topology,normals,a);
            int good = 0;
            for (int i = 0; i < a.Count; ++i) {
                var middle = (p[a[i]]+p[a[(i+1)%a.Count]])*.5f;
                var growth = Vector3.Cross((p[a[(i+1)%a.Count]]-p[a[i]]).normalized,collar[i]).normalized;
                float nearest = float.PositiveInfinity; Vector3 destination = default;
                for (int j = 0; j < b.Count; ++j) {
                    var start = p[b[j]]; var edge = p[b[(j+1)%b.Count]]-start;
                    var point = start + edge*Mathf.Clamp01(Vector3.Dot(middle-start,edge)/edge.sqrMagnitude);
                    float distance = (point-middle).sqrMagnitude;
                    if (distance < nearest) { nearest=distance; destination=point; }
                }
                if (nearest > 0 && Vector3.Dot(growth,(destination-middle).normalized) > .6f) ++good;
            }
            return good >= Math.Ceiling(a.Count*.8);
        }

        static double Cost(Vector3[] p, int a, int b, int c, Vector3 collar)
        {
            var u=p[b]-p[a]; var v=p[c]-p[a]; var w=p[c]-p[b];
            double nx=(double)u.y*v.z-(double)u.z*v.y, ny=(double)u.z*v.x-(double)u.x*v.z, nz=(double)u.x*v.y-(double)u.y*v.x;
            double area=Math.Sqrt(nx*nx+ny*ny+nz*nz);
            if (area <= 1e-12) return double.PositiveInfinity;
            return (u.sqrMagnitude+v.sqrMagnitude+w.sqrMagnitude)/area + 2*(1-(nx*collar.x+ny*collar.y+nz*collar.z)/area);
        }

        static int Compare(Path x,Path y)
        {
            int order=x.score.CompareTo(y.score); return order!=0?order:x.order.CompareTo(y.order);
        }

        static List<Path> Paths(Vector3[] p,List<int> a,List<int> b,Vector3[] na,Vector3[] nb,CancellationToken token,
            ref int states,ref int order,int maxStates)
        {
            int m=a.Count,n=b.Count; var grid=new List<Path>[m+1,n+1];
            grid[0,0]=new List<Path> { new Path() };
            int nextOrder=order;
            for(int i=0;i<=m;++i) for(int j=0;j<=n;++j) {
                token.ThrowIfCancellationRequested();
                if(++states>maxStates) throw new InvalidOperationException("Bridge search budget exceeded; no partial winner was accepted.");
                if(i==0 || i==m&&j==0 || j==n&&i<m) continue;
                var paths=new List<Path>();
                void Extend(List<Path> previous,double cost,char move) {
                    if(previous==null || !double.IsFinite(cost)) return;
                    foreach(var old in previous) paths.Add(new Path {score=old.score+cost,previous=old,move=move,length=old.length+1,order=++nextOrder});
                }
                Extend(grid[i-1,j],Cost(p,a[(i-1)%m],b[j%n],a[i%m],na[(i-1)%m]),'A');
                if(j>0) Extend(grid[i,j-1],Cost(p,a[i%m],b[(j-1)%n],b[j%n],nb[(j-1)%n]),'B');
                paths.Sort(Compare);
                if(paths.Count>PathsPerState) paths.RemoveRange(PathsPerState,paths.Count-PathsPerState);
                grid[i,j]=paths;
            }
            order=nextOrder;
            return grid[m,n] ?? new List<Path>();
        }

        static int[] Faces(List<int> a,List<int> b,Path path)
        {
            var moves=new char[path.length];
            for (int cursor=moves.Length-1;cursor>=0;--cursor) { moves[cursor]=path.move; path=path.previous; }
            var faces=new int[moves.Length*3]; int i=0,j=0,k=0;
            foreach(char move in moves) {
                faces[k++]=a[i%a.Count]; faces[k++]=b[j%b.Count];
                faces[k++]=move=='A'?a[(++i)%a.Count]:b[(++j)%b.Count];
            }
            return faces;
        }
    }
}
