using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Explicit disk filling on a curved 3D rim. Fixed source vertices,
    /// bounded ear search, and atomic acceptance after topology/contact audits.</summary>
    internal static class RemeshSurfaceCap
    {
        const int MaxRimEdges = 64;
        const int MaxCandidates = 4096;

        sealed class Search
        {
            internal Vector3[] positions;
            internal RemeshCapIntersection.Q[][] exact;
            internal CancellationToken token;
            internal int trials, candidates, originalFaces, initialBoundary, rimEdges, limit;
            internal string lastFailure;
            internal readonly HashSet<(int,int,int)> sourceBlocked = new HashSet<(int,int,int)>();
        }

        readonly struct Ear
        {
            internal readonly int slot;
            internal readonly double score;
            internal Ear(int slot, double score) { this.slot = slot; this.score = score; }
        }

        internal static int[] Generate(Vector3[] positions, int[] source, List<int> loop, CancellationToken token,
            ref int trials, out int tests, RemeshPlanarCap.ExternalContacts external = null, int maxCandidates = MaxCandidates)
        {
            token.ThrowIfCancellationRequested(); tests = 0;
            if (loop.Count < 3 || loop.Count > MaxRimEdges) throw Refuse("disk rim must contain 3 to 64 edges");
            if (maxCandidates < 1 || maxCandidates > MaxCandidates) throw Refuse("invalid candidate budget");
            var topology = RemeshTopology.Inspect(positions, source, token);
            var search = new Search {positions = positions, token = token, trials = trials, originalFaces = source.Length / 3,
                initialBoundary = topology.boundary.Count, rimEdges = loop.Count, limit = maxCandidates,
                exact = new RemeshCapIntersection.Q[positions.Length][]};
            for (int i = 0; i < positions.Length; ++i) search.exact[i] = RemeshCapIntersection.Point(positions[i]);
            try {
                List<int> result=null; RemeshPlanarCap.ExternalContacts contacts=null;
                bool filled;
                try { filled=Fill(search,new List<int>(loop),new List<int>(source),external?.Fork(),out result,out contacts); }
                catch (InvalidOperationException failure) when (maxCandidates==MaxCandidates && failure.Message.Contains("candidate search budget exceeded")) {
                    filled=false;
                }
                if (!filled && maxCandidates==MaxCandidates)
                    filled=FillIntervals(search,source,loop,external,out result,out contacts);
                if (!filled) throw Refuse("no complete audited triangulation" + (search.lastFailure == null ? "" : ": " + search.lastFailure));
                // Re-audit the entire accepted patch before exporting any triangles or
                // statistics. Failed search branches never alter the caller's arrays.
                RemeshPlanarCap.AuditContacts(positions, search.exact, result.ToArray(), search.originalFaces,
                    token, ref search.trials, out tests, contacts);
                external?.Merge(contacts);
                return result.GetRange(source.Length, result.Count - source.Length).ToArray();
            }
            finally { trials = search.trials; }
        }

        static bool Fill(Search search, List<int> ring, List<int> faces, RemeshPlanarCap.ExternalContacts contacts,
            out List<int> result, out RemeshPlanarCap.ExternalContacts acceptedContacts)
        {
            search.token.ThrowIfCancellationRequested(); result = null; acceptedContacts = null;
            var topology = RemeshTopology.Inspect(search.positions, faces.ToArray(), search.token);
            var ears = RankedEars(search.positions, ring, topology);
            foreach (var ear in ears) {
                search.token.ThrowIfCancellationRequested();
                int a = ring[(ear.slot + ring.Count - 1) % ring.Count], b = ring[ear.slot], c = ring[(ear.slot + 1) % ring.Count];
                int low = Math.Min(a,Math.Min(b,c)), high = Math.Max(a,Math.Max(b,c));
                var key = (low,a+b+c-low-high,high);
                if (search.sourceBlocked.Contains(key)) continue;
                if (++search.candidates > search.limit) throw Refuse("candidate search budget exceeded; no partial patch accepted");
                var candidate = new List<int>(faces) {a,c,b};
                var next = new List<int>(ring); next.RemoveAt(ear.slot);
                var current = RemeshTopology.Inspect(search.positions, candidate.ToArray(), search.token);
                int remaining = ring.Count == 3 ? 0 : next.Count;
                if (!current.Valid || current.boundary.Count != search.initialBoundary - search.rimEdges + remaining) continue;
                var branch = contacts?.Fork();
                try {
                    RemeshPlanarCap.AuditContacts(search.positions, search.exact, candidate.ToArray(), faces.Count / 3,
                        search.token, ref search.trials, out _, branch);
                }
                catch (InvalidOperationException failure) when (!failure.Message.Contains("budget")) {
                    // Contact with fixed input faces is independent of earlier ears.
                    // Contact with another tentative Cap remains branch-dependent.
                    if (failure.Data[RemeshPlanarCap.ContactSourceFaceKey] is int face && face < search.originalFaces)
                        search.sourceBlocked.Add(key);
                    search.lastFailure = failure.Message; continue;
                }
                if (ring.Count == 3) { result = candidate; acceptedContacts = branch; return true; }
                if (Fill(search, next, candidate, branch, out result, out acceptedContacts)) return true;
            }
            return false;
        }

        sealed class DiskPath
        {
            internal double area;
            internal int order;
            internal int[] faces;
        }

        // A complete interval profile avoids exploring every permutation of the
        // same ears. Keep four least-area triangulations per subpolygon, pruning
        // immutable source contacts before ranking. This is a bounded candidate
        // family; the assembled disk still needs the exact whole-patch audit.
        static bool FillIntervals(Search search,int[] source,List<int> ring,RemeshPlanarCap.ExternalContacts external,
            out List<int> result,out RemeshPlanarCap.ExternalContacts acceptedContacts)
        {
            result=null; acceptedContacts=null;
            const int retained=4,maxCombinations=1000000;
            int n=ring.Count;
            long planned=(long)n*(n-1)*(n-2)/6*retained*retained;
            if (planned>maxCombinations) throw Refuse("interval search budget exceeded before allocation; no partial patch accepted");
            var grid=new List<DiskPath>[n,n];
            for (int i=0;i+1<n;++i) grid[i,i+1]=new List<DiskPath> {new DiskPath {faces=Array.Empty<int>()}};
            var filter=new RemeshPlanarCap.SourceContactFilter(search.positions,search.exact,source,search.token,external,search.trials);
            int work=0,order=0;
            try {
                for (int width=2;width<n;++width) for (int first=0;first+width<n;++first) {
                    search.token.ThrowIfCancellationRequested();
                    int last=first+width; var paths=new List<DiskPath>(); grid[first,last]=paths;
                    for (int middle=first+1;middle<last;++middle) {
                        var left=grid[first,middle]; var right=grid[middle,last];
                        if (left==null || right==null || left.Count==0 || right.Count==0) continue;
                        int a=ring[first],b=ring[last],c=ring[middle];
                        double area=TriangleArea(search.positions[a],search.positions[b],search.positions[c]);
                        if (!(area>0) || !filter.Allows(a,b,c)) continue;
                        foreach (var l in left) foreach (var r in right) {
                            if (++work>maxCombinations) throw Refuse("interval search budget exceeded; no partial patch accepted");
                            double sum=l.area+r.area+area;
                            if (paths.Count==retained && sum>=paths[paths.Count-1].area) continue;
                            var faces=new int[l.faces.Length+r.faces.Length+3];
                            Array.Copy(l.faces,faces,l.faces.Length); Array.Copy(r.faces,0,faces,l.faces.Length,r.faces.Length);
                            faces[faces.Length-3]=a; faces[faces.Length-2]=b; faces[faces.Length-1]=c;
                            paths.Add(new DiskPath {area=sum,order=++order,faces=faces});
                            paths.Sort((x,y)=>{int compared=x.area.CompareTo(y.area);return compared!=0?compared:x.order.CompareTo(y.order);});
                            if (paths.Count>retained) paths.RemoveAt(paths.Count-1);
                        }
                    }
                }
            }
            finally { search.trials=filter.trials; }
            foreach (var path in grid[0,n-1]) {
                search.token.ThrowIfCancellationRequested();
                var candidate=new List<int>(source); candidate.AddRange(path.faces);
                var topology=RemeshTopology.Inspect(search.positions,candidate.ToArray(),search.token);
                if (!topology.Valid || topology.boundary.Count!=search.initialBoundary-search.rimEdges) continue;
                var contacts=external?.Fork();
                try {
                    RemeshPlanarCap.AuditContacts(search.positions,search.exact,candidate.ToArray(),search.originalFaces,
                        search.token,ref search.trials,out _,contacts);
                }
                catch (InvalidOperationException failure) when (!failure.Message.Contains("budget")) {
                    search.lastFailure=failure.Message; continue;
                }
                result=candidate; acceptedContacts=contacts; return true;
            }
            return false;
        }

        static double TriangleArea(Vector3 a,Vector3 b,Vector3 c)
        {
            double ux=(double)b.x-a.x,uy=(double)b.y-a.y,uz=(double)b.z-a.z;
            double vx=(double)c.x-a.x,vy=(double)c.y-a.y,vz=(double)c.z-a.z;
            double x=uy*vz-uz*vy,y=uz*vx-ux*vz,z=ux*vy-uy*vx;
            return Math.Sqrt(x*x+y*y+z*z);
        }

        static List<Ear> RankedEars(Vector3[] positions, List<int> ring, RemeshTopology.Snapshot topology)
        {
            var normals = MeshGeometry.FaceNormals(positions, topology.indices);
            var ears = new List<Ear>(ring.Count);
            for (int slot = 0; slot < ring.Count; ++slot) {
                int a = ring[(slot + ring.Count - 1) % ring.Count], b = ring[slot], c = ring[(slot + 1) % ring.Count];
                var u = positions[c] - positions[a]; var v = positions[b] - positions[a];
                var normal = Vector3.Cross(u,v);
                double twiceArea = normal.magnitude;
                double sum = (double)u.sqrMagnitude + v.sqrMagnitude + (positions[c]-positions[b]).sqrMagnitude;
                if (!(twiceArea > 0) || !(sum > 0)) continue;
                normal.Normalize();
                // Prefer compact triangles and continuity against the actual donor
                // or earlier Cap face adjacent to each consumed boundary edge.
                double score = 2 * Math.Sqrt(3) * twiceArea / sum;
                foreach (var edge in new[] {(a,b),(b,c)}) {
                    var key = edge.Item1 < edge.Item2 ? edge : (edge.Item2,edge.Item1);
                    if (topology.edges.TryGetValue(key, out var adjacent))
                        score += .25 * Vector3.Dot(normal,normals[adjacent.firstFace]);
                }
                ears.Add(new Ear(slot,score));
            }
            ears.Sort((a,b) => {
                int comparison = b.score.CompareTo(a.score);
                return comparison != 0 ? comparison : ring[a.slot].CompareTo(ring[b.slot]);
            });
            return ears;
        }

        static InvalidOperationException Refuse(string reason) => new InvalidOperationException("Surface Cap refused: " + reason + ". Source donors were preserved.");
    }
}
