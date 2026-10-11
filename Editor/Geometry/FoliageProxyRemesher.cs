using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal interface IFoliageProxyRemesher
    {
        FoliageProxyRemesher.Result Remesh(RemeshNative.IndexedMesh input, FoliageNormalsSettings settings, CancellationToken token);
    }

    // Original implementation of incremental isotropic surface remeshing.
    // No third-party implementation is copied; projection uses the shared BVH.
    internal sealed class FoliageProxyRemesher : IFoliageProxyRemesher
    {
        internal sealed class Quality
        {
            internal double edgeMean, edgeVariation, areaVariation, meanShape, minimumAngle = 180;
            internal int triangles, edges;
        }
        internal sealed class Diagnostics
        {
            internal Quality before, after;
            internal float targetEdgeLength, maxSurfaceDistance, maxReverseDistance;
            internal int splits, collapses, flips, rejected, iterations;
            internal double volumeChange;
            public override string ToString() => FormattableString.Invariant($"Remesh: {before.triangles:N0} → {after.triangles:N0} triangles · target edge {targetEdgeLength:G4}\nEdge CV {before.edgeVariation:P1} → {after.edgeVariation:P1} · area CV {before.areaVariation:P1} → {after.areaVariation:P1}\nMinimum angle {before.minimumAngle:F1}° → {after.minimumAngle:F1}° · mean shape {before.meanShape:F3} → {after.meanShape:F3}\n{splits:N0} splits / {collapses:N0} collapses / {flips:N0} flips · {rejected:N0} constrained edits · {iterations} iterations\nSurface deviation {maxSurfaceDistance:G4} / reverse {maxReverseDistance:G4} · volume change {volumeChange:P1}");
        }
        internal sealed class Result
        {
            internal RemeshNative.IndexedMesh mesh; internal Diagnostics diagnostics;
        }

        public Result Remesh(RemeshNative.IndexedMesh input, FoliageNormalsSettings settings, CancellationToken token)
        {
            settings.Validate();
            FoliageProxyBuilder.CheckTriangleBudget(input, settings.maximumProxyTriangles, token);
            FoliageProxyBuilder.Validate(input, token, false);
            // Geometry-only proxies may arrive with exact position splits. Work on shared
            // vertices just as RemeshTopology does, without changing the input arrays.
            input = Weld(input, token);
            var topology = FoliageProxyBuilder.Validate(input, token, false);
            var before = Measure(input, token);
            float length = settings.remeshEdgeLength > 0 ? settings.remeshEdgeLength : (float)before.edgeMean;
            if (!FoliageTargetSnapshot.Finite(length) || length <= 0)
            {
                throw new InvalidOperationException("No finite proxy target edge length.");
            }

            double area = SurfaceArea(input, token);
            double estimated = area / (Math.Sqrt(3) * .25 * length * length);
            if (input.TriangleCount > settings.maximumProxyTriangles || estimated > settings.maximumProxyTriangles)
            {
                throw Budget(settings.maximumProxyTriangles);
            }

            var diagnostics = new Diagnostics { before = before, targetEdgeLength = length };
            var state = new Surface(input, topology, length, settings.maximumProxyTriangles, diagnostics, token);
            for (int iteration = 0; iteration < settings.remeshIterations; iteration++)
            {
                state.Split();
                state.Collapse();
                state.Flip();
                state.Relax(settings.remeshRelaxation);
                token.ThrowIfCancellationRequested();
                diagnostics.iterations++;
            }
            var result = state.Export();
            var afterTopology = FoliageProxyBuilder.Validate(result, token, false);
            if (!afterTopology.PreservesComponents(topology, false))
            {
                throw new InvalidOperationException("Proxy remeshing changed components or Euler characteristics.");
            }

            result.normals = MeshGeometry.AveragedNormals(result.positions, result.indices, token);
            if (result.normals.Any(n => !FoliageTargetSnapshot.Finite(n) || Math.Abs(n.magnitude - 1) > .001f))
            {
                throw new InvalidOperationException("Proxy remeshing produced invalid normals.");
            }

            diagnostics.after = Measure(result, token);
            diagnostics.volumeChange = Math.Abs(FoliageProxyBuilder.SignedVolume(result.positions, result.indices) / FoliageProxyBuilder.SignedVolume(input.positions, input.indices) - 1);
            diagnostics.maxSurfaceDistance = Deviation(result, state.reference, token);
            diagnostics.maxReverseDistance = Deviation(input, new TriangleBvh(result.positions, result.indices), token);
            if (diagnostics.volumeChange > .25 || Math.Max(diagnostics.maxSurfaceDistance, diagnostics.maxReverseDistance) > length * .5f)
            {
                throw new InvalidOperationException("Proxy remesh deviates too far from the smoothed surface. Reduce target edge length or relaxation.");
            }

            return new Result { mesh = result, diagnostics = diagnostics };
        }

        static InvalidOperationException Budget(int limit) => new InvalidOperationException($"Proxy remesh triangle budget exceeded ({limit:N0}; working adjacency is approximately 1 KiB/triangle). Increase target edge length or reduce voxel resolution.");
        static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);
        static RemeshNative.IndexedMesh Weld(RemeshNative.IndexedMesh input, CancellationToken token)
        {
            var slots = MeshGeometry.WeldPositions(input.positions, out int count);
            if (count == input.positions.Length)
            {
                return input;
            }

            var positions = new Vector3[count];
            var indices = new int[input.indices.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                if ((i & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                positions[slots[i]] = input.positions[i];
            }
            for (int i = 0; i < indices.Length; i++)
            {
                if ((i & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                indices[i] = slots[input.indices[i]];
            }
            return new RemeshNative.IndexedMesh { positions = positions, indices = indices };
        }

        sealed class Vertex
        {
            internal Vector3 position;
            internal int component;
            internal readonly HashSet<int> faces = new HashSet<int>(), neighbours = new HashSet<int>();
        }
        readonly struct Face
        {
            internal readonly int a, b, c;
            internal Face(int a, int b, int c)
            {
                this.a = a;
                this.b = b;
                this.c = c;
            }
            internal int Opposite(int u, int v)
            {
                if (a != u && a != v) { return a; }
                return b != u && b != v ? b : c;
            }
            internal bool Forward(int u, int v) => a == u && b == v || b == u && c == v || c == u && a == v;
            internal Face Replace(int from, int to) => new Face(a == from ? to : a, b == from ? to : b, c == from ? to : c);
        }
        sealed class Edge
        {
            internal int first, second = -1;
            internal Edge(int face)
            {
                first = face;
            }
        }

        sealed class Surface
        {
            static (int, int, int) FaceKey(int a, int b, int c)
            {
                if (a > b)
                {
                    (a, b) = (b, a);
                }

                if (b > c)
                {
                    (b, c) = (c, b);
                }

                if (a > b)
                {
                    (a, b) = (b, a);
                }

                return (a, b, c);
            }
            static int Root(int[] parents, int i)
            {
                while (parents[i] != i)
                {
                    i = parents[i];
                }

                return i;
            }
            readonly List<Vertex> vertices = new List<Vertex>();
            readonly Dictionary<int, Face> faces = new Dictionary<int, Face>();
            readonly Dictionary<(int, int), Edge> edges = new Dictionary<(int, int), Edge>();
            readonly Dictionary<(int, int, int), int> faceKeys = new Dictionary<(int, int, int), int>();
            readonly Stack<int> freeFaces = new Stack<int>();
            readonly Stack<int> freeVertices = new Stack<int>();
            int nextFace;
            readonly int[] referenceComponents;
            internal readonly TriangleBvh reference;
            readonly float lowSq, highSq;
            readonly int budget;
            readonly Diagnostics diagnostics;
            readonly CancellationToken token;
            internal Surface(RemeshNative.IndexedMesh input, RemeshTopology.Snapshot topology, float length, int budget, Diagnostics diagnostics, CancellationToken token)
            {
                this.budget = budget;
                this.diagnostics = diagnostics;
                this.token = token;
                lowSq = length * length * .64f;
                highSq = length * length * (16f / 9);
                reference = new TriangleBvh(input.positions, input.indices);
                referenceComponents = new int[input.TriangleCount];
                for (int i = 0; i < input.positions.Length; i++)
                {
                    vertices.Add(new Vertex { position = input.positions[i] });
                }

                for (int f = 0; f < input.TriangleCount; f++)
                {
                    Check(f);
                    int k = f * 3, component = Root(topology.components, f);
                    referenceComponents[f] = component;
                    int a = input.indices[k], b = input.indices[k + 1], c = input.indices[k + 2];
                    vertices[a].component = component;
                    vertices[b].component = component;
                    vertices[c].component = component;
                    Add(new Face(a, b, c));
                }
            }
            void Check(int i)
            {
                if ((i & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }
            }
            float LengthSq((int, int) edge) => (vertices[edge.Item1].position - vertices[edge.Item2].position).sqrMagnitude;
            List<(int, int)> OrderedEdges(bool shortestFirst = false)
            {
                token.ThrowIfCancellationRequested();
                var list = new List<(int, int)>(edges.Keys);
                list.Sort((a, b) =>
                {
                    int order = shortestFirst ? LengthSq(a).CompareTo(LengthSq(b)) : 0;
                    if (order == 0)
                    {
                        order = a.Item1.CompareTo(b.Item1);
                    }

                    return order != 0 ? order : a.Item2.CompareTo(b.Item2);
                });
                return list;
            }
            int[] Incident(int a, int b = -1)
            {
                var set = new HashSet<int>(vertices[a].faces);
                if (b >= 0)
                {
                    set.UnionWith(vertices[b].faces);
                }

                var result = new int[set.Count];
                set.CopyTo(result);
                Array.Sort(result);
                return result;
            }
            int[] Neighbours(int v)
            {
                var result = new int[vertices[v].neighbours.Count];
                vertices[v].neighbours.CopyTo(result);
                Array.Sort(result);
                return result;
            }
            bool Project(Vector3 position, int component, out Vector3 projected)
            {
                var hit = reference.FindNearest(position);
                projected = hit.point;
                return hit.triangleIndex >= 0 && referenceComponents[hit.triangleIndex] == component && FoliageTargetSnapshot.Finite(projected);
            }
            Vector3 Normal(Face f) => Vector3.Cross(vertices[f.b].position - vertices[f.a].position, vertices[f.c].position - vertices[f.a].position);
            Vector3 Point(int v, int moved, Vector3 position) => v == moved ? position : vertices[v].position;
            bool Valid(Face old, Face replacement, int moved = -1, Vector3 position = default)
            {
                if (replacement.a == replacement.b || replacement.b == replacement.c || replacement.c == replacement.a)
                {
                    return false;
                }

                var a = Point(replacement.a, moved, position);
                var b = Point(replacement.b, moved, position);
                var c = Point(replacement.c, moved, position);
                var normal = Vector3.Cross(b - a, c - a);
                var prior = Normal(old);
                return FoliageTargetSnapshot.Finite(normal) && normal.sqrMagnitude > 0 && Vector3.Dot(normal, prior) > .1f * normal.magnitude * prior.magnitude;
            }
            void AddEdge(int a, int b, int f)
            {
                var key = EdgeKey(a, b);
                if (edges.TryGetValue(key, out var edge))
                {
                    if (edge.second >= 0)
                    {
                        throw new InvalidOperationException("Non-manifold remesh edit.");
                    }

                    edge.second = f;
                }
                else
                {
                    edges.Add(key, new Edge(f));
                    vertices[a].neighbours.Add(b);
                    vertices[b].neighbours.Add(a);
                }
            }
            void RemoveEdge(int a, int b, int f)
            {
                var key = EdgeKey(a, b);
                var edge = edges[key];
                if (edge.first == f)
                {
                    edge.first = edge.second;
                    edge.second = -1;
                }
                else
                {
                    edge.second = -1;
                }

                if (edge.first >= 0)
                {
                    return;
                }

                edges.Remove(key);
                vertices[a].neighbours.Remove(b);
                vertices[b].neighbours.Remove(a);
            }
            void Add(Face face)
            {
                if (faces.Count >= budget)
                {
                    throw Budget(budget);
                }

                int id = freeFaces.Count > 0 ? freeFaces.Pop() : nextFace++;
                faces.Add(id, face);
                faceKeys.Add(FaceKey(face.a, face.b, face.c), id);
                vertices[face.a].faces.Add(id);
                vertices[face.b].faces.Add(id);
                vertices[face.c].faces.Add(id);
                AddEdge(face.a, face.b, id);
                AddEdge(face.b, face.c, id);
                AddEdge(face.c, face.a, id);
            }
            void Remove(int id)
            {
                var face = faces[id];
                faces.Remove(id);
                faceKeys.Remove(FaceKey(face.a, face.b, face.c));
                freeFaces.Push(id);
                vertices[face.a].faces.Remove(id);
                vertices[face.b].faces.Remove(id);
                vertices[face.c].faces.Remove(id);
                RemoveEdge(face.a, face.b, id);
                RemoveEdge(face.b, face.c, id);
                RemoveEdge(face.c, face.a, id);
            }

            internal void Split()
            {
                var candidates = OrderedEdges();
                for (int i = 0; i < candidates.Count; i++)
                {
                    Check(i);
                    SplitEdge(candidates[i]);
                }
            }

            void SplitEdge((int, int) key)
            {
                if (!edges.TryGetValue(key, out var edge) || edge.second < 0 || LengthSq(key) <= highSq)
                {
                    return;
                }

                if (faces.Count + 2 > budget)
                {
                    throw Budget(budget);
                }

                int u = key.Item1, v = key.Item2;
                if (!Project((vertices[u].position + vertices[v].position) * .5f, vertices[u].component, out var p))
                {
                    diagnostics.rejected++;
                    return;
                }
                int m;
                if (freeVertices.Count > 0)
                {
                    m = freeVertices.Pop();
                    vertices[m].position = p;
                    vertices[m].component = vertices[u].component;
                }
                else
                {
                    m = vertices.Count;
                    vertices.Add(new Vertex { position = p, component = vertices[u].component });
                }
                int f0 = edge.first, f1 = edge.second;
                var original = new[] { faces[f0], faces[f1] };
                var patch = new Face[4];
                bool valid = true;
                for (int side = 0; side < 2; side++)
                {
                    var face = original[side];
                    int a = face.Forward(u, v) ? u : v, b = a == u ? v : u, c = face.Opposite(u, v);
                    patch[side * 2] = new Face(a, m, c);
                    patch[side * 2 + 1] = new Face(m, b, c);
                    valid &= Valid(face, patch[side * 2]) && Valid(face, patch[side * 2 + 1]);
                }
                if (!valid)
                {
                    freeVertices.Push(m);
                    diagnostics.rejected++;
                    return;
                }
                Remove(f0);
                Remove(f1);
                foreach (var f in patch)
                {
                    Add(f);
                }

                diagnostics.splits++;
            }

            internal void Collapse()
            {
                var candidates = OrderedEdges(true);
                for (int i = 0; i < candidates.Count; i++)
                {
                    Check(i);
                    CollapseEdge(candidates[i]);
                }
            }

            void CollapseEdge((int, int) key)
            {
                if (!edges.TryGetValue(key, out var edge) || edge.second < 0 || LengthSq(key) >= lowSq)
                {
                    return;
                }

                int u = key.Item1, v = key.Item2, c = faces[edge.first].Opposite(u, v), d = faces[edge.second].Opposite(u, v);
                int common = vertices[u].neighbours.Count(neighbour => vertices[v].neighbours.Contains(neighbour));

                if (common != 2 || c == d)
                {
                    diagnostics.rejected++;
                    return;
                }
                if (!Project((vertices[u].position + vertices[v].position) * .5f, vertices[u].component, out var p))
                {
                    diagnostics.rejected++;
                    return;
                }
                var incident = Incident(u, v);
                bool valid = CollapsePatch(incident, edge, u, v, p, out var patch);
                valid &= NeighboursWithinLimit(u, v, p);
                valid &= NeighboursWithinLimit(v, u, p);

                if (!valid)
                {
                    diagnostics.rejected++;
                    return;
                }
                foreach (int f in incident)
                {
                    Remove(f);
                }

                vertices[u].position = p;
                foreach (var f in patch)
                {
                    Add(f);
                }

                freeVertices.Push(v);
                diagnostics.collapses++;
            }

            bool CollapsePatch(int[] incident, Edge edge, int u, int v, Vector3 p, out List<Face> patch)
            {
                var removed = new HashSet<int>(incident);
                var unique = new HashSet<(int, int, int)>();
                patch = new List<Face>();
                bool valid = true;
                foreach (int f in incident)
                {
                    if (f == edge.first || f == edge.second)
                    {
                        continue;
                    }

                    var old = faces[f];
                    var next = old.Replace(v, u);
                    var triangleKey = FaceKey(next.a, next.b, next.c);
                    valid &= Valid(old, next, u, p) && unique.Add(triangleKey) && (!faceKeys.TryGetValue(triangleKey, out int existing) || removed.Contains(existing));
                    patch.Add(next);
                }
                return valid;
            }

            bool NeighboursWithinLimit(int node, int excluded, Vector3 p)
            {
                return vertices[node].neighbours.All(neighbour => neighbour == excluded || (vertices[neighbour].position - p).sqrMagnitude <= highSq);
            }

            internal void Flip()
            {
                var candidates = OrderedEdges();
                for (int i = 0; i < candidates.Count; i++)
                {
                    Check(i);
                    FlipEdge(candidates[i]);
                }
            }

            void FlipEdge((int, int) key)
            {
                if (!edges.TryGetValue(key, out var edge) || edge.second < 0)
                {
                    return;
                }

                int f0 = edge.first, f1 = edge.second;
                var a = faces[f0];
                var b = faces[f1];
                int u = key.Item1, v = key.Item2;
                if (!a.Forward(u, v))
                {
                    (u, v) = (v, u);
                }

                int c = a.Opposite(u, v), d = b.Opposite(u, v);
                if (c == d || edges.ContainsKey(EdgeKey(c, d)) || LengthSq(EdgeKey(c, d)) > highSq)
                {
                    return;
                }

                var x = new Face(c, d, v);
                var y = new Face(d, c, u);
                if (!Valid(a, x) || !Valid(b, y))
                {
                    return;
                }

                int Cost(int node, int change) => Math.Abs(vertices[node].neighbours.Count + change - 6);
                int before = Cost(u, 0) + Cost(v, 0) + Cost(c, 0) + Cost(d, 0), after = Cost(u, -1) + Cost(v, -1) + Cost(c, 1) + Cost(d, 1);
                double shapeBefore = Math.Min(Shape(a), Shape(b)), shapeAfter = Math.Min(Shape(x), Shape(y));
                if (after > before || after == before && shapeAfter <= shapeBefore + 1e-5 || shapeAfter < shapeBefore * .8)
                {
                    return;
                }

                Remove(f0);
                Remove(f1);
                Add(x);
                Add(y);
                diagnostics.flips++;
            }

            double Shape(Face f) => TriangleShape(vertices[f.a].position, vertices[f.b].position, vertices[f.c].position);

            internal void Relax(float strength)
            {
                if (strength <= 0) { return; }
                var positions = new Vector3[vertices.Count];
                for (int i = 0; i < vertices.Count; i++)
                {
                    Check(i);
                    positions[i] = RelaxPosition(i, strength);
                }
                ApplyRelaxedPositions(positions);
            }

            Vector3 RelaxPosition(int i, float strength)
            {
                var vertex = vertices[i];
                if (vertex.faces.Count == 0)
                {
                    return vertex.position;
                }

                var incident = Incident(i);
                Vector3 normal = Vector3.zero;
                foreach (int f in incident)
                {
                    normal += Normal(faces[f]);
                }

                normal = MeshGeometry.UnitDirection(normal);
                Vector3 sum = Vector3.zero;
                foreach (int v in Neighbours(i))
                {
                    sum += vertices[v].position - vertex.position;
                }

                var delta = sum * (strength / vertex.neighbours.Count);
                delta -= normal * Vector3.Dot(delta, normal);
                if (Project(vertex.position + delta, vertex.component, out var projected))
                {
                    return projected;
                }
                else
                {
                    diagnostics.rejected++;
                }
                return vertex.position;
            }

            void ApplyRelaxedPositions(Vector3[] positions)
            {
                for (int i = 0; i < vertices.Count; i++)
                {
                    Check(i);
                    if (vertices[i].faces.Count == 0)
                    {
                        continue;
                    }

                    bool valid = Incident(i).All(f => Valid(faces[f], faces[f], i, positions[i]));

                    if (valid)
                    {
                        vertices[i].position = positions[i];
                    }
                    else
                    {
                        diagnostics.rejected++;
                    }
                }
            }

            internal RemeshNative.IndexedMesh Export()
            {
                token.ThrowIfCancellationRequested();
                var map = new int[vertices.Count];
                var points = new List<Vector3>();
                for (int i = 0; i < vertices.Count; i++)
                {
                    Check(i);
                    map[i] = points.Count;
                    if (vertices[i].faces.Count > 0)
                    {
                        points.Add(vertices[i].position);
                    }
                }
                var ids = new List<int>(faces.Keys);
                ids.Sort();
                var indices = new int[ids.Count * 3];
                for (int i = 0; i < ids.Count; i++)
                {
                    Check(i);
                    var f = faces[ids[i]];
                    indices[i * 3] = map[f.a];
                    indices[i * 3 + 1] = map[f.b];
                    indices[i * 3 + 2] = map[f.c];
                }
                return new RemeshNative.IndexedMesh { positions = points.ToArray(), indices = indices };
            }
        }

        static double TriangleShape(Vector3 a, Vector3 b, Vector3 c)
        {
            double sum = (a - b).sqrMagnitude + (b - c).sqrMagnitude + (c - a).sqrMagnitude;
            return sum > 0 ? 2 * Math.Sqrt(3) * Vector3.Cross(b - a, c - a).magnitude / sum : 0;
        }
        static double SurfaceArea(RemeshNative.IndexedMesh mesh, CancellationToken token)
        {
            double area = 0;
            for (int k = 0; k < mesh.indices.Length; k += 3)
            {
                if ((k & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                area += Vector3.Cross(mesh.positions[mesh.indices[k + 1]] - mesh.positions[mesh.indices[k]], mesh.positions[mesh.indices[k + 2]] - mesh.positions[mesh.indices[k]]).magnitude * .5;
            }
            return area;
        }
        internal static Quality Measure(RemeshNative.IndexedMesh mesh, CancellationToken token)
        {
            var edges = new HashSet<(int, int)>();
            var quality = new Quality { triangles = mesh.TriangleCount };
            double areaSum = 0, areaSq = 0;
            for (int k = 0; k < mesh.indices.Length; k += 3)
            {
                if ((k & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                int a = mesh.indices[k], b = mesh.indices[k + 1], c = mesh.indices[k + 2];
                edges.Add(EdgeKey(a, b));
                edges.Add(EdgeKey(b, c));
                edges.Add(EdgeKey(c, a));
                var p = mesh.positions[a];
                var q = mesh.positions[b];
                var r = mesh.positions[c];
                double area = Vector3.Cross(q - p, r - p).magnitude * .5;
                areaSum += area;
                areaSq += area * area;
                quality.meanShape += TriangleShape(p, q, r);
                quality.minimumAngle = Math.Min(quality.minimumAngle, Math.Min(MeshGeometry.CornerAngle(q - p, r - p), Math.Min(MeshGeometry.CornerAngle(p - q, r - q), MeshGeometry.CornerAngle(p - r, q - r))) * Mathf.Rad2Deg);
            }
            // Stable summation order, independent of hash iteration.
            var ordered = new List<(int, int)>(edges);
            ordered.Sort();
            double sum = 0, squared = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                if ((i & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                var edge = ordered[i];
                double length = (mesh.positions[edge.Item1] - mesh.positions[edge.Item2]).magnitude;
                sum += length;
                squared += length * length;
            }
            quality.edges = edges.Count;
            quality.edgeMean = sum / edges.Count;
            quality.edgeVariation = Math.Sqrt(Math.Max(0, squared / edges.Count - quality.edgeMean * quality.edgeMean)) / quality.edgeMean;
            double meanArea = areaSum / mesh.TriangleCount;
            quality.areaVariation = Math.Sqrt(Math.Max(0, areaSq / mesh.TriangleCount - meanArea * meanArea)) / meanArea;
            quality.meanShape /= mesh.TriangleCount;
            return quality;
        }
        static float Deviation(RemeshNative.IndexedMesh mesh, TriangleBvh reference, CancellationToken token)
        {
            float maximum = 0;
            for (int i = 0; i < mesh.positions.Length; i++)
            {
                if ((i & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                maximum = Math.Max(maximum, reference.FindNearest(mesh.positions[i]).distSq);
            }
            for (int k = 0; k < mesh.indices.Length; k += 3)
            {
                if ((k & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                var p = (mesh.positions[mesh.indices[k]] + mesh.positions[mesh.indices[k + 1]] + mesh.positions[mesh.indices[k + 2]]) / 3;
                maximum = Math.Max(maximum, reference.FindNearest(p).distSq);
            }
            return Mathf.Sqrt(maximum);
        }
    }
}
