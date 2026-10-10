using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Synchronized collapse of render wedges along an authored crease. Zero
    // tolerances retain affine fields; bounded mode tracks the original polyline.
    internal static class LodFeatureChains
    {
        internal struct Settings
        {
            internal float relativeDeviation, normalAngle, colorError, uvError;
        }
        readonly struct Segment
        {
            internal readonly int slot;
            internal readonly Vector3 a,b,an,bn;
            internal Segment(int slot,Vector3 a,Vector3 b,Vector3 an,Vector3 bn)
            { this.slot = slot; this.a = a; this.b = b; this.an = an; this.bn = bn; }
        }
        internal sealed class Result
        {
            internal Mesh mesh;
            internal int removedPoints, removedTriangles, candidates, separatedContacts, refusedComponents, degreeTwoPoints;
            internal string refusal;
            internal Settings settings;
            internal string Note => $"Feature chains: removed {removedPoints} chain points / {removedTriangles} triangles; " +
                $"tested {candidates} collapses from {degreeTwoPoints} degree-two points, separated {separatedContacts} disconnected contact fans, kept {refusedComponents} invalid components." + (refusal == null ? "" : " " + refusal);
            internal LodHardEdges.Report Configure(LodHardEdges.Report report)
            {
                report.coarsenedPoints = removedPoints; report.coarsenedTriangles = removedTriangles;
                report.featureDeviation = settings.relativeDeviation; report.featureNormalAngle = settings.normalAngle;
                return report;
            }
        }

        internal static Result Coarsen(Mesh source, Func<bool> cancelled = null, Settings settings = default)
        {
            var result = new Result { settings = settings };
            var data = new LodMeshData(source);
            if (data.normals.Length != data.positions.Length) { result.refusal = "Authored normals unavailable."; return result; }
            var ids = new Dictionary<Vector3,int>();
            var faces = new List<LodSourceTopology.Face>();
            for (int slot = 0; slot < source.subMeshCount; slot++)
            {
                var indices = LodMeshData.Triangles(source,slot);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    var vertices = indices.Skip(i).Take(3).ToArray();
                    var points = vertices.Select(v => {
                        if (!ids.TryGetValue(data.positions[v],out int id)) ids[data.positions[v]] = id = ids.Count;
                        return id;
                    }).ToArray();
                    faces.Add(Face(slot,points,vertices));
                }
            }
            var edgeFaces = Edges(faces);
            var pinned = new HashSet<int>();
            foreach (var edge in edgeFaces.Values)
                if (edge.Count > 2 || edge.Select(i => i.face).Distinct().Count() != edge.Count)
                    foreach (var side in edge) pinned.UnionWith(faces[side.face].points);
            result.separatedContacts = SeparateContactFans(faces,edgeFaces,ids.Count,pinned);
            var groups = Components(faces);
            var output = new List<LodSourceTopology.Face>();
            for (int group = 0; group < groups.Count; group++)
            {
                CheckCancellation(cancelled);
                var component = groups[group];
                var others = groups.Where(g => !ReferenceEquals(g,component)).SelectMany(g => g).ToList();
                groups[group] = CoarsenComponent(component,others,data,pinned,result,cancelled);
                output.AddRange(groups[group]);
            }
            if (result.removedPoints > 0) result.mesh = data.CreateMesh(output,out _,ensureChannels:false);
            return result;
        }

        static List<LodSourceTopology.Face> CoarsenComponent(List<LodSourceTopology.Face> faces,List<LodSourceTopology.Face> others,
            LodMeshData data,HashSet<int> pinned,Result result,Func<bool> cancelled)
        {
            if (!LodTopologyGraph.TryBuild(faces,data,out var graph,out string error))
            { result.refusedComponents++; result.refusal = "Source component refused: " + error; return faces; }
            var signature = graph.signature;
            result.degreeTwoPoints += Features(faces,data,graph).Values.Count(neighbors => neighbors.Count == 2);
            var paths = new Dictionary<long,List<Segment>>();
            foreach (var pair in Features(faces,data,graph)) foreach (int neighbor in pair.Value)
            {
                long key = LodTopologyGraph.EdgeKey(pair.Key,neighbor);
                if (!paths.ContainsKey(key)) paths[key] = Segments(key,faces,data,graph);
            }
            float deviation = data.scale*Mathf.Max(1e-6f,result.settings.relativeDeviation);
            bool changed;
            do
            {
                CheckCancellation(cancelled);
                changed = false;
                var features = Features(faces,data,graph);
                foreach (int a in features.Keys.OrderBy(id => id))
                {
                    CheckCancellation(cancelled);
                    if (features[a].Count != 2 || graph.boundary.Contains(a) || pinned.Contains(a)) continue;
                    var ends = features[a].OrderBy(id => id).ToArray();
                    var p = Position(a,faces,data,graph); var b = Position(ends[0],faces,data,graph); var c = Position(ends[1],faces,data,graph);
                    var line = c-b;
                    if (line.sqrMagnitude == 0) continue;
                    float t = Vector3.Dot(p-b,line)/line.sqrMagnitude;
                    long first = LodTopologyGraph.EdgeKey(a,ends[0]), second = LodTopologyGraph.EdgeKey(a,ends[1]), merged = LodTopologyGraph.EdgeKey(ends[0],ends[1]);
                    if (t <= 0 || t >= 1 || !paths.ContainsKey(first) || !paths.ContainsKey(second) || paths.ContainsKey(merged)) continue;
                    if (!graph.edges[first].Select(h => faces[h.face].submesh).OrderBy(slot => slot)
                        .SequenceEqual(graph.edges[second].Select(h => faces[h.face].submesh).OrderBy(slot => slot)) ||
                        graph.neighbors[a].Any(neighbor => !features[a].Contains(neighbor) &&
                            graph.edges[LodTopologyGraph.EdgeKey(a,neighbor)].Select(h => faces[h.face].submesh).Distinct().Count() > 1)) continue;
                    var path = paths[first].Concat(paths[second]).ToList();
                    if (path.Any(segment => SegmentDistance(segment.a,b,c) > deviation || SegmentDistance(segment.b,b,c) > deviation)) continue;
                    foreach (int survivor in ends)
                    {
                        result.candidates++;
                        var proposed = Collapse(a,survivor,faces,others,data,graph,result.settings);
                        if (proposed == null || !LodTopologyGraph.TryBuild(proposed,data,out var next,out _) || next.signature != signature) continue;
                        var replacement = Segments(merged,proposed,data,next);
                        if (path.Any(original => !replacement.Any(target => Covers(original,target,deviation,result.settings.normalAngle)))) continue;
                        faces = proposed; graph = next; result.removedPoints++; result.removedTriangles += 2;
                        paths.Remove(first); paths.Remove(second); paths[merged] = path;
                        changed = true; break;
                    }
                    if (changed) break; // Rebuild adjacency before another collapse.
                }
            } while (changed);
            return faces;
        }

        static List<Segment> Segments(long key,List<LodSourceTopology.Face> faces,LodMeshData data,LodTopologyGraph graph)
        {
            var result = new List<Segment>();
            if (!graph.edges.TryGetValue(key,out var edge)) return result;
            foreach (var side in edge)
            {
                var face = faces[side.face]; int a = face.vertices[side.slot], b = face.vertices[(side.slot+1)%3];
                result.Add(new Segment(face.submesh,data.positions[a],data.positions[b],data.normals[a],data.normals[b]));
            }
            return result;
        }
        static bool Covers(Segment original,Segment target,float deviation,float angle)
        {
            if (original.slot != target.slot) return false;
            var line = target.b-target.a;
            if (line.sqrMagnitude == 0) return false;
            float u = Vector3.Dot(original.a-target.a,line)/line.sqrMagnitude, v = Vector3.Dot(original.b-target.a,line)/line.sqrMagnitude;
            return u >= -1e-6f && v <= 1+1e-6f && v > u &&
                (original.a-(target.a+u*line)).sqrMagnitude <= deviation*deviation &&
                (original.b-(target.a+v*line)).sqrMagnitude <= deviation*deviation &&
                NormalMatch(original.an,Vector3.LerpUnclamped(target.an,target.bn,u),angle) &&
                NormalMatch(original.bn,Vector3.LerpUnclamped(target.an,target.bn,v),angle);
        }

        static Dictionary<long,List<(int face,int a,int b)>> Edges(List<LodSourceTopology.Face> faces)
        {
            var edges = new Dictionary<long,List<(int face,int a,int b)>>();
            for (int i = 0; i < faces.Count; i++) for (int k = 0; k < 3; k++)
            {
                int a = faces[i].points[k], b = faces[i].points[(k+1)%3];
                long key = LodTopologyGraph.EdgeKey(a,b);
                if (!edges.TryGetValue(key,out var sides)) edges[key] = sides = new List<(int face,int a,int b)>();
                sides.Add((i,a,b));
            }
            return edges;
        }

        static int SeparateContactFans(List<LodSourceTopology.Face> faces,Dictionary<long,List<(int face,int a,int b)>> edges,int nextId,HashSet<int> pinned)
        {
            var original = faces.Select(f => (int[])f.points.Clone()).ToArray();
            var incident = new Dictionary<int,HashSet<int>>();
            for (int i = 0; i < original.Length; i++) foreach (int point in original[i])
            { if (!incident.TryGetValue(point,out var set)) incident[point] = set = new HashSet<int>(); set.Add(i); }
            int contacts = 0;
            foreach (var pair in incident)
            {
                int fan = 0;
                while (pair.Value.Count > 0)
                {
                    int start = pair.Value.Min(); var queue = new Queue<int>(); queue.Enqueue(start); pair.Value.Remove(start);
                    int id = fan++ == 0 ? pair.Key : nextId++;
                    if (pinned.Contains(pair.Key)) pinned.Add(id);
                    if (id != pair.Key) contacts++;
                    while (queue.Count > 0)
                    {
                        int face = queue.Dequeue();
                        for (int k = 0; k < 3; k++) if (original[face][k] == pair.Key) faces[face].points[k] = id;
                        foreach (int neighbor in original[face])
                        {
                            if (neighbor == pair.Key) continue;
                            var sides = edges[LodTopologyGraph.EdgeKey(pair.Key,neighbor)];
                            if (sides.Count != 2 || sides[0].a != sides[1].b || sides[0].b != sides[1].a) continue;
                            foreach (var side in sides) if (pair.Value.Remove(side.face)) queue.Enqueue(side.face);
                        }
                    }
                }
            }
            return contacts;
        }

        static List<List<LodSourceTopology.Face>> Components(List<LodSourceTopology.Face> faces)
        {
            var adjacent = Enumerable.Range(0,faces.Count).Select(_ => new HashSet<int>()).ToArray();
            foreach (var edge in Edges(faces).Values)
                foreach (var a in edge) foreach (var b in edge) if (a.face != b.face) adjacent[a.face].Add(b.face);
            var seen = new HashSet<int>(); var groups = new List<List<LodSourceTopology.Face>>();
            for (int i = 0; i < faces.Count; i++)
            {
                if (!seen.Add(i)) continue;
                var group = new List<LodSourceTopology.Face>(); var queue = new Queue<int>(); queue.Enqueue(i);
                while (queue.Count > 0)
                { int face = queue.Dequeue(); group.Add(faces[face]); foreach (int next in adjacent[face]) if (seen.Add(next)) queue.Enqueue(next); }
                groups.Add(group);
            }
            return groups;
        }

        static Dictionary<int,HashSet<int>> Features(List<LodSourceTopology.Face> faces,LodMeshData data,LodTopologyGraph graph)
        {
            var result = new Dictionary<int,HashSet<int>>();
            foreach (var edge in graph.edges.Values)
            {
                if (edge.Count != 2) continue;
                var h = edge[0]; var other = edge[1];
                var f = faces[h.face]; var g = faces[other.face];
                if (NormalEqual(data.normals[f.vertices[h.slot]],data.normals[g.vertices[(other.slot+1)%3]]) &&
                    NormalEqual(data.normals[f.vertices[(h.slot+1)%3]],data.normals[g.vertices[other.slot]])) continue;
                Add(result,h.a,h.b); Add(result,h.b,h.a);
            }
            return result;
        }
        static void Add(Dictionary<int,HashSet<int>> map,int a,int b)
        { if (!map.TryGetValue(a,out var neighbors)) map[a] = neighbors = new HashSet<int>(); neighbors.Add(b); }
        static Vector3 Position(int id,List<LodSourceTopology.Face> faces,LodMeshData data,LodTopologyGraph graph)
        { var face = faces[graph.vertexFaces[id][0]]; return data.positions[face.vertices[Array.IndexOf(face.points,id)]]; }

        static List<LodSourceTopology.Face> Collapse(int a,int b,List<LodSourceTopology.Face> faces,List<LodSourceTopology.Face> others,LodMeshData data,LodTopologyGraph graph,Settings settings)
        {
            var edge = graph.edges[LodTopologyGraph.EdgeKey(a,b)];
            var common = new HashSet<int>(graph.neighbors[a]); common.IntersectWith(graph.neighbors[b]);
            var opposite = new HashSet<int>(edge.Select(h => faces[h.face].points.First(p => p != a && p != b)));
            if (!common.SetEquals(opposite)) return null; // Edge-collapse link condition.
            var wedges = new List<(int from,int to)>();
            foreach (var h in edge)
            {
                var face = faces[h.face];
                wedges.Add((face.vertices[Array.IndexOf(face.points,a)],face.vertices[Array.IndexOf(face.points,b)]));
            }
            var replacements = new List<LodSourceTopology.Face>();
            var removed = new HashSet<int>(graph.vertexFaces[a]);
            foreach (int index in graph.vertexFaces[a])
            {
                var face = faces[index];
                if (face.points.Contains(b)) continue;
                int corner = Array.IndexOf(face.points,a), vertex = face.vertices[corner];
                var choices = wedges.Where(w => data.SameAttributes(vertex,w.from)).Select(w => w.to).Distinct().ToArray();
                if (choices.Length == 0 || choices.Skip(1).Any(v => !data.SameAttributes(v,choices[0]))) return null;
                int to = choices[0];
                // In bounded mode compare the field at the old physical point,
                // below, rather than demanding equal normals at different points.
                if (settings.relativeDeviation <= 0 && (!NormalEqual(data.normals[vertex],data.normals[to]) ||
                    (data.tangents.Length == data.positions.Length && !TangentMatch(data.tangents[vertex],data.tangents[to],0)))) return null;
                var vertices = (int[])face.vertices.Clone(); vertices[corner] = to;
                var points = (int[])face.points.Clone(); points[corner] = b;
                if (!PreservesFace(face.vertices,vertices,corner,data,settings)) return null;
                replacements.Add(Face(face.submesh,points,vertices));
            }
            if (new LodTriangleIntersections(faces.Concat(others).ToList(),data.positions).Intersects(replacements,removed)) return null;
            var proposed = faces.Where((f,i) => !removed.Contains(i)).Concat(replacements).ToList();
            // Keep every material surface present; tiny closed components cannot vanish.
            if (faces.Select(f => f.submesh).Except(proposed.Select(f => f.submesh)).Any()) return null;
            return proposed;
        }

        static bool PreservesFace(int[] original,int[] target,int corner,LodMeshData data,Settings settings)
        {
            var p = data.positions;
            var oldNormal = Vector3.Cross(p[original[1]]-p[original[0]],p[original[2]]-p[original[0]]);
            var normal = Vector3.Cross(p[target[1]]-p[target[0]],p[target[2]]-p[target[0]]);
            if (normal.sqrMagnitude == 0 || Vector3.Dot(normal,oldNormal) <= 0 ||
                Vector3.Angle(normal,oldNormal) > 45 ||
                Mathf.Abs(Vector3.Dot(oldNormal.normalized,p[target[corner]]-p[original[corner]])) > data.scale*Mathf.Max(1e-6f,settings.relativeDeviation)) return false;
            // Preserve the affine UV/RGBA field on every changed face, including all
            // UV dimensions/channels and alpha/HDR. Extrapolation is intentional.
            var e0 = p[target[1]]-p[target[0]]; var e1 = p[target[2]]-p[target[0]];
            var q = p[original[corner]]-p[target[0]];
            float d00 = Vector3.Dot(e0,e0), d01 = Vector3.Dot(e0,e1), d11 = Vector3.Dot(e1,e1);
            float denominator = d00*d11-d01*d01;
            if (denominator <= 0) return false;
            float v = (d11*Vector3.Dot(q,e0)-d01*Vector3.Dot(q,e1))/denominator;
            float w = (d00*Vector3.Dot(q,e1)-d01*Vector3.Dot(q,e0))/denominator;
            if (!NormalMatch(data.normals[original[corner]],(1-v-w)*data.normals[target[0]]+v*data.normals[target[1]]+w*data.normals[target[2]],settings.normalAngle)) return false;
            if (data.tangents.Length == p.Length && !TangentMatch(data.tangents[original[corner]],
                (1-v-w)*data.tangents[target[0]]+v*data.tangents[target[1]]+w*data.tangents[target[2]],settings.normalAngle)) return false;
            for (int ch = 0; ch < 8; ch++)
                if (data.uvs[ch] != null && !FieldEqual(data.uvs[ch][original[corner]],
                    (1-v-w)*data.uvs[ch][target[0]]+v*data.uvs[ch][target[1]]+w*data.uvs[ch][target[2]],settings.uvError)) return false;
            if (data.colors.Length == p.Length && !FieldEqual(data.colors[original[corner]],
                (1-v-w)*(Vector4)data.colors[target[0]]+v*(Vector4)data.colors[target[1]]+w*(Vector4)data.colors[target[2]],settings.colorError)) return false;
            return true;
        }
        static bool FieldEqual(Vector4 a,Vector4 b,float tolerance = 0)
        {
            var d = a-b;
            return tolerance > 0 ? Mathf.Max(Mathf.Max(Mathf.Abs(d.x),Mathf.Abs(d.y)),Mathf.Max(Mathf.Abs(d.z),Mathf.Abs(d.w))) <= tolerance : d.sqrMagnitude <= 1e-10f*Mathf.Max(1,a.sqrMagnitude);
        }
        static bool NormalMatch(Vector3 a,Vector3 b,float angle) => angle <= 0 ? NormalEqual(a,b) : a.sqrMagnitude > .5f && b.sqrMagnitude > .5f && Vector3.Angle(a,b) <= angle;
        static bool TangentMatch(Vector4 a,Vector4 b,float angle) => Mathf.Abs(a.w-b.w) <= 1e-5f && NormalMatch(a,b,angle);
        static float SegmentDistance(Vector3 p,Vector3 a,Vector3 b)
        { var line = b-a; return (p-(a+line*Mathf.Clamp01(Vector3.Dot(p-a,line)/line.sqrMagnitude))).magnitude; }
        static bool NormalEqual(Vector3 a,Vector3 b) => (a-b).sqrMagnitude <= 1e-10f;
        static LodSourceTopology.Face Face(int slot,int[] points,int[] vertices)
            => new LodSourceTopology.Face { submesh = slot,points = points,vertices = vertices,triangles = vertices,referenceTriangles = vertices };
        static void CheckCancellation(Func<bool> cancelled)
        { if (cancelled?.Invoke() == true) throw new OperationCanceledException("LOD feature-chain coarsening cancelled."); }
    }
}
