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
            public int missingEdges, missingFaces, missingInterfaces;
            public bool sourceFallback;
            internal bool Valid => missingEdges == 0 && missingFaces == 0 && missingInterfaces == 0;
            internal string Note => $"Hard edges: {edges-missingEdges}/{edges} retained; protected faces {protectedTriangles}, " +
                $"ambiguous edges {ambiguousEdges}, patch interfaces {interfaces-missingInterfaces}/{interfaces}." +
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
        internal readonly bool[][] frozen;
        internal readonly int protectedTriangles, ambiguousEdges;

        internal LodHardEdges(Mesh mesh)
        {
            source = new LodMeshData(mesh); frozen = new bool[mesh.subMeshCount][];
            for (int slot = 0; slot < frozen.Length; slot++)
            {
                int[] faces = LodMeshData.Triangles(mesh,slot); frozen[slot] = new bool[faces.Length/3];
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
                foreach (var side in sides) frozen[side.slot][side.face] = true;
            }
            foreach (var entry in edges)
                if (entry.Value.Any(s => frozen[s.slot][s.face]) && entry.Value.Any(s => !frozen[s.slot][s.face]))
                    interfaces.Add(entry.Key);
            protectedTriangles = frozen.Sum(slot => slot.Count(value => value));
        }
        int Point(Vector3 p)
        { if (!points.TryGetValue(p,out int id)) points[p] = id = points.Count; return id; }
        int Endpoint(Side side,int vertex) => source.positions[side.a].Equals(source.positions[vertex]) ? side.a : side.b;
        bool SameNormal(int a,int b) => NormalEqual(source.normals[a],source.normals[b]);
        static bool NormalEqual(Vector3 a,Vector3 b) => a.sqrMagnitude > .5f && b.sqrMagnitude > .5f
            ? (a.normalized-b.normalized).sqrMagnitude <= 1e-10f : a.Equals(b);

        internal Report Measure(Mesh target)
        {
            var report = new Report { edges = hard.Count, protectedTriangles = protectedTriangles,
                ambiguousEdges = ambiguousEdges, interfaces = interfaces.Count };
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
                    if (frozen[slot][i/3])
                    {
                        var face = FaceKey(slot,points[source.positions[faces[i]]],points[source.positions[faces[i+1]]],points[source.positions[faces[i+2]]]);
                        targetFaces.TryGetValue(face,out int occurrences);
                        if (occurrences == 0) report.missingFaces++;
                        else targetFaces[face] = occurrences-1;
                    }
            }
            foreach (var edge in interfaces)
                if (!targetEdges.TryGetValue(edge,out var sides) || edges[edge].Any(f =>
                    !sides.Any(s => s.slot == f.slot && positions[s.a].Equals(source.positions[f.a]) && positions[s.b].Equals(source.positions[f.b]))))
                    report.missingInterfaces++;
            return report;
        }
        static (int slot,int a,int b,int c) FaceKey(int slot,int a,int b,int c)
        {
            var first = (slot,a,b,c); var second = (slot,b,c,a); var third = (slot,c,a,b);
            if (second.CompareTo(first) < 0) first = second;
            return third.CompareTo(first) < 0 ? third : first;
        }
    }
}
