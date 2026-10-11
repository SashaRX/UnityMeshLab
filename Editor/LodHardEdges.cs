using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Exact positional adjacency and authored corner normals; UV/color splits are
    // not hard edges. Retain both incident triangles as a conservative feature
    // belt, simplifying the remainder with locked patch boundaries.
    internal sealed class LodHardEdges
    {
        internal sealed class Report
        {
            public int edges, protectedTriangles, ambiguousEdges, interfaces;
            public int coarsenedPoints, coarsenedTriangles;
            public float featureDeviation, featureNormalAngle;
            public int missingEdges, missingFaces, missingInterfaces;
            public bool sourceFallback;
            public bool nativeConstraints, lockedChainRetry, beltFallback;
            public bool screenBudgetRelaxed;
            internal bool Valid => missingEdges == 0 && missingFaces == 0 && missingInterfaces == 0;
            internal string Note => $"Hard-edge coverage: {edges-missingEdges}/{edges}; protected faces {protectedTriangles}, " +
                $"ambiguous edges {ambiguousEdges}, patch interfaces {interfaces-missingInterfaces}/{interfaces}." +
                (coarsenedPoints > 0 ? $" Coarsened {coarsenedPoints} points; feature deviation ≤{featureDeviation:P2} of source diagonal, endpoint normal guide {featureNormalAngle:F1}°." : "") +
                (screenBudgetRelaxed ? " Far screen budget: exact crease/face coverage is diagnostic; silhouette, normals and RGBA are ranked at the object footprint." : "") +
                (nativeConstraints ? " Direct native constraints; only ambiguous faces remain frozen." : "") +
                (lockedChainRetry ? " Retried with all crease vertices locked." : "") +
                (beltFallback ? " Native coverage rejected both attempts; used strict incident-face protection." : "") +
                (sourceFallback ? " Rejected candidate; retained source copy." : "");
        }
        readonly struct Side
        {
            internal readonly int slot, face, a, b, c;
            internal Side(int slot,int face,int a,int b,int c)
            { this.slot = slot; this.face = face; this.a = a; this.b = b; this.c = c; }
        }
        readonly struct Edge
        {
            internal readonly int a, b;
            internal Edge(int a,int b) { this.a = Mathf.Min(a,b); this.b = Mathf.Max(a,b); }
        }
        readonly LodMeshData source;
        readonly Dictionary<Vector3,int> points = new Dictionary<Vector3,int>();
        readonly Dictionary<Edge,List<Side>> edges = new Dictionary<Edge,List<Side>>();
        readonly List<List<Side>> hard = new List<List<Side>>();
        readonly List<Edge> interfaces = new List<Edge>();
        readonly List<Edge> nativeInterfaces = new List<Edge>();
        internal readonly bool[][] frozen;
        internal readonly bool[][] ambiguousFrozen;
        internal readonly int protectedTriangles, ambiguousEdges;

        internal LodHardEdges(Mesh mesh)
        {
            source = new LodMeshData(mesh); frozen = new bool[mesh.subMeshCount][];
            ambiguousFrozen = new bool[mesh.subMeshCount][];
            for (int slot = 0; slot < frozen.Length; slot++)
            {
                int[] faces = LodMeshData.Triangles(mesh,slot); frozen[slot] = new bool[faces.Length/3];
                ambiguousFrozen[slot] = new bool[faces.Length/3];
                for (int i = 0; i < faces.Length; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = faces[i+k], b = faces[i+(k+1)%3], c = faces[i+(k+2)%3];
                        var key = new Edge(Point(source.positions[a]),Point(source.positions[b]));
                        if (key.a == key.b) continue;
                        if (!edges.TryGetValue(key,out var sides)) edges[key] = sides = new List<Side>();
                        sides.Add(new Side(slot,i/3,a,b,c));
                    }
            }
            foreach (var entry in edges)
            {
                var sides = entry.Value;
                if (sides.Count < 2) continue; // Open boundaries are controlled separately by Lock Border.
                bool ambiguous = sides.Count != 2 ||
                    !source.positions[sides[0].a].Equals(source.positions[sides[1].b]) ||
                    source.positions[sides[0].c].Equals(source.positions[sides[1].c]);
                bool discontinuous = source.normals.Length == source.positions.Length &&
                    sides.Skip(1).Any(s => !SameNormal(sides[0].a,Endpoint(s,sides[0].a)) || !SameNormal(sides[0].b,Endpoint(s,sides[0].b)));
                if (!ambiguous && !discontinuous) continue;
                if (ambiguous) ambiguousEdges++;
                if (discontinuous) hard.Add(sides);
                foreach (var side in sides)
                {
                    frozen[side.slot][side.face] = true;
                    if (ambiguous) ambiguousFrozen[side.slot][side.face] = true;
                }
            }
            foreach (var entry in edges)
            {
                if (entry.Value.Any(s => frozen[s.slot][s.face]) && entry.Value.Any(s => !frozen[s.slot][s.face]))
                    interfaces.Add(entry.Key);
                if (entry.Value.Select(s => s.slot).Distinct().Count() > 1 ||
                    entry.Value.Any(s => ambiguousFrozen[s.slot][s.face]) && entry.Value.Any(s => !ambiguousFrozen[s.slot][s.face]))
                    nativeInterfaces.Add(entry.Key);
            }
            protectedTriangles = frozen.Sum(slot => slot.Count(value => value));
        }
        int Point(Vector3 p)
        {
            if (!points.TryGetValue(p,out int id)) points[p] = id = points.Count;
            return id;
        }
        int Endpoint(Side side,int vertex) => source.positions[side.a].Equals(source.positions[vertex]) ? side.a : side.b;
        bool SameNormal(int a,int b) => NormalEqual(source.normals[a],source.normals[b]);
        static bool NormalEqual(Vector3 a,Vector3 b) => a.sqrMagnitude > .5f && b.sqrMagnitude > .5f
            ? (a.normalized-b.normalized).sqrMagnitude <= 1e-10f : a.Equals(b);

        // Protect degree-two shading seams; endpoints, junctions, material borders
        // and every vertex of an ambiguous face are immovable. Tag all wedges.
        internal byte[] VertexLocks(bool lockChains)
        {
            var flags = new byte[points.Count];
            var degree = new int[points.Count];
            foreach (var feature in hard)
            {
                degree[points[source.positions[feature[0].a]]]++;
                degree[points[source.positions[feature[0].b]]]++;
            }
            for (int i = 0; i < flags.Length; i++)
                if (degree[i] > 0) flags[i] = lockChains || degree[i] != 2 ? MeshoptNative.VertexLock : MeshoptNative.VertexProtect;
            foreach (var edge in nativeInterfaces)
                flags[edge.a] = flags[edge.b] = MeshoptNative.VertexLock;
            LockContactFans(flags);
            foreach (var entry in edges.Values)
                foreach (var side in entry)
                    if (ambiguousFrozen[side.slot][side.face])
                        flags[points[source.positions[side.a]]] = flags[points[source.positions[side.b]]] = flags[points[source.positions[side.c]]] = MeshoptNative.VertexLock;
            return source.positions.Select(p => points.TryGetValue(p,out int id) ? flags[id] : (byte)0).ToArray();
        }

        // Coincidence alone does not connect vertex fans. Keep contact locations
        // fixed so permissive wedge processing cannot move disconnected surfaces
        // through each other while shortening a crease on one of them.
        void LockContactFans(byte[] flags)
        {
            var incident = new Dictionary<int,HashSet<(int slot,int face)>>();
            var connected = new Dictionary<(int point,int slot,int face),List<(int slot,int face)>>();
            foreach (var entry in edges)
                foreach (int point in new[] {entry.Key.a,entry.Key.b})
                {
                    if (!incident.TryGetValue(point,out var faces)) incident[point] = faces = new HashSet<(int,int)>();
                    foreach (var side in entry.Value) faces.Add((side.slot,side.face));
                    var sides = entry.Value;
                    if (sides.Count != 2 || !source.positions[sides[0].a].Equals(source.positions[sides[1].b]) ||
                        !source.positions[sides[0].b].Equals(source.positions[sides[1].a])) continue;
                    for (int i = 0; i < 2; i++)
                    {
                        var key = (point,sides[i].slot,sides[i].face);
                        if (!connected.TryGetValue(key,out var neighbors)) connected[key] = neighbors = new List<(int,int)>();
                        neighbors.Add((sides[1-i].slot,sides[1-i].face));
                    }
                }
            foreach (var entry in incident)
            {
                var remaining = entry.Value;
                var queue = new Queue<(int slot,int face)>();
                var first = remaining.First(); remaining.Remove(first); queue.Enqueue(first);
                while (queue.Count > 0)
                {
                    var face = queue.Dequeue();
                    if (!connected.TryGetValue((entry.Key,face.slot,face.face),out var neighbors)) continue;
                    foreach (var neighbor in neighbors) if (remaining.Remove(neighbor)) queue.Enqueue(neighbor);
                }
                if (remaining.Count > 0) flags[entry.Key] = MeshoptNative.VertexLock;
            }
        }

        internal int NativeProtectedTriangles => ambiguousFrozen.Sum(slot => slot.Count(value => value));
        internal Report MeasureNative(Mesh target,bool lockedRetry = false)
        {
            var report = Measure(target,true);
            report.nativeConstraints = true; report.lockedChainRetry = lockedRetry;
            // Native seam collapses must cover the prepared crease exactly. Only
            // the separately verified managed prepass gets a deviation allowance.
            return MeasureCoarsened(target,report);
        }

        internal Report Measure(Mesh target) => Measure(target,false);
        Report Measure(Mesh target,bool native)
        {
            var retained = native ? ambiguousFrozen : frozen;
            var boundaries = native ? nativeInterfaces : interfaces;
            var report = new Report { edges = hard.Count, protectedTriangles = native ? NativeProtectedTriangles : protectedTriangles,
                ambiguousEdges = ambiguousEdges, interfaces = boundaries.Count };
            var positions = target.vertices; var normals = target.normals;
            var targetEdges = new Dictionary<Edge,List<Side>>();
            var targetFaces = new Dictionary<(int slot,int a,int b,int c),int>();
            for (int slot = 0; slot < target.subMeshCount; slot++)
            {
                var faces = LodMeshData.Triangles(target,slot);
                for (int i = 0; i < faces.Length; i += 3)
                {
                    var ids = new int[3];
                    for (int k = 0; k < 3; k++) ids[k] = points.TryGetValue(positions[faces[i+k]],out int id) ? id : -1;
                    if (ids.All(id => id >= 0))
                    {
                        var face = FaceKey(slot,ids[0],ids[1],ids[2]);
                        targetFaces.TryGetValue(face,out int occurrences); targetFaces[face] = occurrences+1;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        int a = ids[k], b = ids[(k+1)%3]; if (a < 0 || b < 0 || a == b) continue;
                        var key = new Edge(a,b);
                        if (!targetEdges.TryGetValue(key,out var sides)) targetEdges[key] = sides = new List<Side>();
                        sides.Add(new Side(slot,i/3,faces[i+k],faces[i+(k+1)%3],faces[i+(k+2)%3]));
                    }
                }
            }
            foreach (var feature in hard)
            {
                if (native) continue;
                var key = new Edge(points[source.positions[feature[0].a]],points[source.positions[feature[0].b]]);
                if (!targetEdges.TryGetValue(key,out var sides) || normals.Length != positions.Length ||
                    feature.Any(f => !sides.Any(s => s.slot == f.slot && positions[s.a].Equals(source.positions[f.a]) &&
                        positions[s.b].Equals(source.positions[f.b]) && NormalEqual(normals[s.a],source.normals[f.a]) && NormalEqual(normals[s.b],source.normals[f.b]))))
                    report.missingEdges++;
            }
            for (int slot = 0; slot < frozen.Length; slot++)
            {
                int[] faces = LodMeshData.Triangles(source.source,slot);
                for (int i = 0; i < faces.Length; i += 3)
                    if (retained[slot][i/3])
                    {
                        var face = FaceKey(slot,points[source.positions[faces[i]]],points[source.positions[faces[i+1]]],points[source.positions[faces[i+2]]]);
                        targetFaces.TryGetValue(face,out int occurrences);
                        if (occurrences == 0) report.missingFaces++;
                        else targetFaces[face] = occurrences-1;
                    }
            }
            foreach (var edge in boundaries)
                if (!targetEdges.TryGetValue(edge,out var sides) || edges[edge].Any(f =>
                    !sides.Any(s => s.slot == f.slot && positions[s.a].Equals(source.positions[f.a]) && positions[s.b].Equals(source.positions[f.b]))))
                    report.missingInterfaces++;
            return report;
        }

        // Original feature coverage is checked in addition to the coarsened
        // reference's exact face/interface checks. Longer segments must preserve
        // both oriented material sides and their authored endpoint normal field.
        internal Report MeasureCoarsened(Mesh target,Report reference)
        {
            reference.edges = hard.Count; reference.missingEdges = 0;
            var p = target.vertices; var n = target.normals;
            var targetEdges = new Dictionary<Edge,List<Side>>();
            for (int slot = 0; slot < target.subMeshCount; slot++)
            {
                var indices = LodMeshData.Triangles(target,slot);
                for (int i = 0; i < indices.Length; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = indices[i+k], b = indices[i+(k+1)%3];
                        if (!points.TryGetValue(p[a],out int pa) || !points.TryGetValue(p[b],out int pb) || pa == pb) continue;
                        var key = new Edge(pa,pb);
                        if (!targetEdges.TryGetValue(key,out var sides)) targetEdges[key] = sides = new List<Side>();
                        sides.Add(new Side(slot,i/3,a,b,indices[i+(k+2)%3]));
                    }
            }
            foreach (var feature in hard)
            {
                bool Covered(List<Side> sides) => n.Length == p.Length && sides.Count >= 2 && sides.Skip(1).Any(s =>
                    !NormalEqual(n[sides[0].a],n[p[s.a].Equals(p[sides[0].a]) ? s.a : s.b]) ||
                    !NormalEqual(n[sides[0].b],n[p[s.a].Equals(p[sides[0].b]) ? s.a : s.b])) &&
                    feature.All(f => sides.Any(s => s.slot == f.slot &&
                        Covers(source.positions[f.a],source.normals[f.a],source.positions[f.b],source.normals[f.b],p[s.a],n[s.a],p[s.b],n[s.b],reference.featureDeviation,reference.featureNormalAngle)));
                var key = new Edge(points[source.positions[feature[0].a]],points[source.positions[feature[0].b]]);
                if (!(targetEdges.TryGetValue(key,out var exact) && Covered(exact)) && !targetEdges.Values.Any(Covered))
                    reference.missingEdges++;
            }
            return reference;
        }
        bool Covers(Vector3 a,Vector3 an,Vector3 b,Vector3 bn,Vector3 from,Vector3 fn,Vector3 to,Vector3 tn,float deviation,float angle)
        {
            var line = to-from;
            if (line.sqrMagnitude == 0) return false;
            float u = Vector3.Dot(a-from,line)/line.sqrMagnitude, v = Vector3.Dot(b-from,line)/line.sqrMagnitude;
            float epsilon = source.scale*Mathf.Max(1e-6f,deviation);
            return u >= -1e-6f && v <= 1+1e-6f && v > u &&
                (a-(from+u*line)).sqrMagnitude <= epsilon*epsilon && (b-(from+v*line)).sqrMagnitude <= epsilon*epsilon &&
                FeatureNormalEqual(an,Vector3.LerpUnclamped(fn,tn,u),angle) && FeatureNormalEqual(bn,Vector3.LerpUnclamped(fn,tn,v),angle);
        }
        static bool FeatureNormalEqual(Vector3 a,Vector3 b,float angle) => angle <= 0 ? NormalEqual(a,b) :
            a.sqrMagnitude > .5f && b.sqrMagnitude > .5f && Vector3.Angle(a,b) <= angle;
        static (int slot,int a,int b,int c) FaceKey(int slot,int a,int b,int c)
        {
            var first = (slot,a,b,c); var second = (slot,b,c,a); var third = (slot,c,a,b);
            if (second.CompareTo(first) < 0) first = second;
            return third.CompareTo(first) < 0 ? third : first;
        }
    }
}
