using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Bounded, audited annular zipper. Rim vertices are never moved or resampled.</summary>
    internal static class RemeshBridge
    {
        const int MaxEdges = 64, MaxStates = 100000;
        sealed class Path { internal double score; internal string moves; }

        internal static int[] Generate(Vector3[] p, int[] source, List<int> a, List<int> forwardB,
            CancellationToken token, ref int trials, out int contacts, RemeshPlanarCap.ExternalContacts external = null)
        {
            if (a.Count > MaxEdges || forwardB.Count > MaxEdges)
                throw new InvalidOperationException("Bridge refused: each rim is limited to 64 edges.");
            var used = new HashSet<int>(a);
            foreach (int v in forwardB) if (used.Contains(v)) throw new InvalidOperationException("Bridge refused: rims touch.");
            var topology = RemeshTopology.Inspect(p, source, token);
            var normals = MeshGeometry.FaceNormals(p, source);
            var na = Collar(topology, normals, a); var nbForward = Collar(topology, normals, forwardB);
            var low = p[a[0]]; var high = low;
            foreach (int v in a) { low = Vector3.Min(low, p[v]); high = Vector3.Max(high, p[v]); }
            foreach (int v in forwardB) { low = Vector3.Min(low, p[v]); high = Vector3.Max(high, p[v]); }
            float scale = (high-low).magnitude;
            var normalized = new Vector3[p.Length]; var exact = new RemeshCapIntersection.Q[p.Length][];
            for (int i = 0; i < p.Length; ++i) { normalized[i] = (p[i]-low)/scale; exact[i] = RemeshCapIntersection.Point(p[i]); }
            int states = 0; contacts = 0; int[] best = null; double score = double.PositiveInfinity;
            RemeshPlanarCap.ExternalContacts bestContacts = null;
            for (int phase = 0; phase < forwardB.Count; ++phase) {
                var b = new List<int>(); var nb = new Vector3[forwardB.Count];
                for (int j = 0; j < forwardB.Count; ++j) {
                    b.Add(forwardB[(phase-j+forwardB.Count)%forwardB.Count]);
                    nb[j] = nbForward[(phase-j-1+forwardB.Count*2)%forwardB.Count];
                }
                foreach (var path in Paths(normalized, a, b, na, nb, token, ref states)) {
                    var patch = Faces(a,b,path.moves); var candidate = new int[source.Length+patch.Length];
                    Array.Copy(source,candidate,source.Length); Array.Copy(patch,0,candidate,source.Length,patch.Length);
                    var after = RemeshTopology.Inspect(p,candidate,token);
                    if (!after.Valid || after.boundary.Count != topology.boundary.Count-a.Count-b.Count) continue;
                    // The patch itself must be one annulus; duplicates, fans and winding
                    // are checked above, not inferred from the zipper's score.
                    var annulus = RemeshTopology.Inspect(p,patch,token);
                    if (!annulus.Valid || annulus.euler.Count != 1 || annulus.euler[0] != 0 || annulus.boundary.Count != a.Count+b.Count) continue;
                    var candidateContacts = external?.Fork();
                    try { RemeshPlanarCap.AuditContacts(p,exact,candidate,source.Length/3,token,ref trials,out int tested,candidateContacts); contacts += tested; }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("contacts face")) { continue; }
                    if (path.score < score) { score = path.score; best = patch; bestContacts = candidateContacts; }
                }
            }
            if (best == null) throw new InvalidOperationException("Bridge refused: no non-intersecting annulus in the bounded zipper family.");
            external?.Merge(bestContacts);
            return best;
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

        static List<Path> Paths(Vector3[] p,List<int> a,List<int> b,Vector3[] na,Vector3[] nb,CancellationToken token,ref int states)
        {
            int m=a.Count,n=b.Count; var grid=new List<Path>[m+1,n+1];
            grid[0,0]=new List<Path> { new Path { moves="" } };
            for(int i=0;i<=m;++i) for(int j=0;j<=n;++j) {
                token.ThrowIfCancellationRequested();
                if(++states>MaxStates) throw new InvalidOperationException("Bridge search budget exceeded; no partial winner was accepted.");
                if(i==0 || i==m&&j==0 || j==n&&i<m) continue;
                var paths=new List<Path>();
                void Extend(List<Path> previous,double cost,string move) {
                    if(previous==null || !double.IsFinite(cost)) return;
                    foreach(var old in previous) paths.Add(new Path {score=old.score+cost,moves=old.moves+move});
                }
                Extend(grid[i-1,j],Cost(p,a[(i-1)%m],b[j%n],a[i%m],na[(i-1)%m]),"A");
                if(j>0) Extend(grid[i,j-1],Cost(p,a[i%m],b[(j-1)%n],b[j%n],nb[(j-1)%n]),"B");
                paths.Sort((x,y)=> { int order=x.score.CompareTo(y.score); return order!=0?order:string.CompareOrdinal(x.moves,y.moves); });
                if(paths.Count>2) paths.RemoveRange(2,paths.Count-2);
                grid[i,j]=paths;
            }
            return grid[m,n] ?? new List<Path>();
        }

        static int[] Faces(List<int> a,List<int> b,string moves)
        {
            var faces=new int[moves.Length*3]; int i=0,j=0,k=0;
            foreach(char move in moves) {
                faces[k++]=a[i%a.Count]; faces[k++]=b[j%b.Count];
                faces[k++]=move=='A'?a[(++i)%a.Count]:b[(++j)%b.Count];
            }
            return faces;
        }
    }
}
