using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class LodLoopSimplifier
    {
        internal struct Settings
        {
            internal MeshSimplifier.SimplifySettings simplify;
            internal float maxNormalAngle, maxColorError;
            internal bool skipColorValidation;
            internal int candidateCount;
            internal int strategyOffset;
        }
        internal sealed class Result
        {
            internal bool ok, cancelled;
            internal string error;
            internal Mesh mesh;
            internal int removedLoops, blockedLoops, sourceQuads;
            internal float maxDistance, maxNormalAngle, maxColorError;
            internal LodSurfaceValidation.Metrics metrics;
            internal int evaluatedCandidates, selectedCandidate;
            internal int nativeProbes;
            internal float silhouetteMean, silhouetteMax, selectionScore;
            internal List<LodBudgetTriangleSimplifier.CandidateReport> budgetCandidates;
            internal LodAttributeCorrection.Report attributeCorrection;
        }
        sealed class Candidate
        {
            internal List<LodSourceTopology.Face> faces;
            internal HashSet<int> removedFaces;
            internal List<LodSourceTopology.Face> replacements;
            internal float cost;
            internal int budgetDistance;
            internal LodSurfaceValidation.Metrics metrics;
        }

        internal static Result Simplify(LodSourceTopology source, Settings settings, Func<bool> cancelled = null, Action<int, int> progress = null)
        {
            Result best = null;
            int count = Mathf.Clamp(settings.candidateCount, 1, 5);
            int target = Mathf.Max(1, Mathf.CeilToInt(LodMeshData.TriangleCount(source.data.source) * settings.simplify.targetRatio));
            try
            {
                for (int variant = 0; variant < count; variant++)
                {
                    progress?.Invoke(variant + 1, count);
                    if (cancelled?.Invoke() == true)
                    {
                        if (best?.mesh != null) UnityEngine.Object.DestroyImmediate(best.mesh);
                        return new Result { cancelled = true, error = "LOD generation cancelled." };
                    }
                    var candidate = SimplifyOne(source, settings, variant + settings.strategyOffset, cancelled);
                    if (!candidate.ok) { if (best?.mesh != null) UnityEngine.Object.DestroyImmediate(best.mesh); return candidate; }
                    candidate.selectedCandidate = variant + settings.strategyOffset + 1;
                    if (best == null || BetterResult(candidate, best, target))
                    {
                        if (best?.mesh != null) UnityEngine.Object.DestroyImmediate(best.mesh);
                        best = candidate;
                    }
                    else UnityEngine.Object.DestroyImmediate(candidate.mesh);
                }
                best.evaluatedCandidates = count;
                return best;
            }
            catch (OperationCanceledException)
            {
                if (best?.mesh != null) UnityEngine.Object.DestroyImmediate(best.mesh);
                return new Result { cancelled = true, error = "LOD generation cancelled." };
            }
            catch { if (best?.mesh != null) UnityEngine.Object.DestroyImmediate(best.mesh); throw; }
        }

        internal static bool BetterResult(Result candidate, Result best, int target)
        {
            int a = LodMeshData.TriangleCount(candidate.mesh), b = LodMeshData.TriangleCount(best.mesh);
            // First compare budget attainment, then quality at equal triangle count.
            // Extra geometry cannot win solely by giving a smaller surface error.
            int da = Math.Abs(a - target), db = Math.Abs(b - target);
            if ((a <= target) != (b <= target)) return a <= target;
            if (da != db) return da < db;
            if (Mathf.Abs(candidate.metrics.weightedError - best.metrics.weightedError) > 1e-7f)
                return candidate.metrics.weightedError < best.metrics.weightedError;
            return candidate.metrics.ColorRms.sqrMagnitude < best.metrics.ColorRms.sqrMagnitude - 1e-12f;
        }

        static Result SimplifyOne(LodSourceTopology source, Settings settings, int variant, Func<bool> cancelled)
        {
            var result = new Result { sourceQuads = source.QuadCount };
            if (!LodTopologyGraph.TryBuild(source.faces, source.data, out var graph, out result.error)) return result;
            var signature = graph.signature;
            var faces = new List<LodSourceTopology.Face>(source.faces);
            int target = Mathf.Max(1, Mathf.CeilToInt(LodMeshData.TriangleCount(source.data.source) * settings.simplify.targetRatio));
            var data = source.data;
            while (faces.Sum(f => f.triangles.Length / 3) > target)
            {
                if (cancelled?.Invoke() == true) { result.cancelled = true; result.error = "LOD generation cancelled."; return result; }
                var seen = new HashSet<long>();
                Candidate best = null;
                var intersection = new LodTriangleIntersections(faces, data.positions);
                var orderedEdges = graph.edges.Keys.OrderBy(k => variant == 4 ? -k : k);
                foreach (long edgeKey in orderedEdges)
                {
                    if (cancelled?.Invoke() == true) { result.cancelled = true; result.error = "LOD generation cancelled."; return result; }
                    if (seen.Contains(edgeKey)) continue;
                    var edge = graph.edges[edgeKey][0];
                    var loop = TraceLoop(edge.a, edge.b, graph, faces);
                    if (loop == null) continue;
                    for (int i = 0; i + 1 < loop.Count; i++) seen.Add(LodTopologyGraph.EdgeKey(loop[i], loop[i + 1]));
                    if (loop[0] == loop[loop.Count - 1]) loop.RemoveAt(loop.Count - 1);
                    if (loop.Any(v => Protected(v, graph, faces, data, settings.simplify.lockBorder))) { result.blockedLoops++; continue; }
                    var candidate = MergeLoop(loop, graph, faces, data);
                    if (candidate == null || !LodTopologyGraph.TryBuild(candidate.faces, data, out var nextGraph, out _) || nextGraph.signature != signature ||
                        !Validate(candidate, data, settings, intersection, cancelled)) { result.blockedLoops++; continue; }
                    int savings = faces.Sum(f => f.triangles.Length) - candidate.faces.Sum(f => f.triangles.Length);
                    float error = variant == 1 ? candidate.metrics.distance :
                        variant == 2 ? candidate.metrics.ColorRms.magnitude * settings.simplify.colorWeight +
                            candidate.metrics.normalAngle / 180f * settings.simplify.normalWeight : candidate.metrics.weightedError;
                    candidate.cost = error / Mathf.Max(1, savings);
                    candidate.budgetDistance = Math.Abs(candidate.faces.Sum(f => f.triangles.Length / 3) - target);
                    if (variant == 3) candidate.cost = candidate.budgetDistance + Mathf.Min(candidate.cost, .5f);
                    // Roundoff in a constant color/UV field must not decide between
                    // equivalent planar loops and cause avoidable budget overshoot.
                    if (best == null || candidate.cost < best.cost - 1e-7f ||
                        (Mathf.Abs(candidate.cost - best.cost) <= 1e-7f && candidate.budgetDistance < best.budgetDistance)) best = candidate;
                }
                if (best == null) break;
                faces = best.faces;
                result.removedLoops++;
                // Revalidate against source references after each merge, never the preceding LOD.
                result.maxDistance = Mathf.Max(result.maxDistance, best.metrics.distance);
                result.maxNormalAngle = Mathf.Max(result.maxNormalAngle, best.metrics.normalAngle);
                result.maxColorError = Mathf.Max(result.maxColorError, best.metrics.colorError);
                if (!LodTopologyGraph.TryBuild(faces, data, out graph, out result.error)) return result;
            }
            result.mesh = data.CreateMesh(faces);
            try
            {
                // Identical settings and sampling for every complete candidate, always LOD0.
                result.metrics = LodSurfaceValidation.MeasureFaces(data, faces, settings.simplify, cancelled);
                result.maxDistance = result.metrics.distance;
                result.maxNormalAngle = result.metrics.normalAngle;
                result.maxColorError = result.metrics.colorError;
                if (!Accept(result.metrics, settings))
                {
                    UnityEngine.Object.DestroyImmediate(result.mesh);
                    result.mesh = data.CreateMesh(source.faces);
                    result.metrics = LodSurfaceValidation.MeasureFaces(data, source.faces, settings.simplify, cancelled);
                    result.maxDistance = result.metrics.distance; result.maxNormalAngle = result.metrics.normalAngle; result.maxColorError = result.metrics.colorError;
                    result.removedLoops = 0;
                }
            }
            catch { UnityEngine.Object.DestroyImmediate(result.mesh); throw; }
            result.ok = true;
            return result;
        }

        static int Opposite(int vertex, int previous, LodTopologyGraph graph, List<LodSourceTopology.Face> faces)
        {
            var neighbors = graph.neighbors[vertex];
            if (graph.boundary.Contains(vertex)) return neighbors.Count == 3 && graph.vertexFaces[vertex].Count == 2 ? -1 : -2;
            if (neighbors.Count != 4 || graph.vertexFaces[vertex].Count != 4 || graph.vertexFaces[vertex].Any(f => faces[f].points.Length != 4)) return -2;
            var adjacent = new HashSet<int> { previous };
            foreach (int f in graph.vertexFaces[vertex])
            {
                int slot = Array.IndexOf(faces[f].points, vertex);
                int a = faces[f].points[(slot + 1) % 4], b = faces[f].points[(slot + 3) % 4];
                if (a == previous) adjacent.Add(b);
                if (b == previous) adjacent.Add(a);
            }
            var opposite = neighbors.Where(n => !adjacent.Contains(n)).ToArray();
            return opposite.Length == 1 ? opposite[0] : -2;
        }

        static List<int> TraceLoop(int a, int b, LodTopologyGraph graph, List<LodSourceTopology.Face> faces)
        {
            var loop = new List<int> { a, b };
            var visited = new HashSet<int> { a, b };
            int previous = a, vertex = b;
            while (true)
            {
                int next = Opposite(vertex, previous, graph, faces);
                if (next == a) { loop.Add(a); return loop; }
                if (next == -2) return null;
                if (next == -1) break;
                if (!visited.Add(next)) return null;
                loop.Add(next); previous = vertex; vertex = next;
            }
            previous = b; vertex = a;
            while (true)
            {
                int next = Opposite(vertex, previous, graph, faces);
                if (next == -1) return loop;
                if (next < 0 || !visited.Add(next)) return null;
                loop.Insert(0, next); previous = vertex; vertex = next;
            }
        }

        static bool Protected(int vertex, LodTopologyGraph graph, List<LodSourceTopology.Face> faces, LodMeshData data, bool lockBorder)
        {
            if (lockBorder && graph.boundary.Contains(vertex)) return true;
            var incident = graph.vertexFaces[vertex];
            if (incident.Any(f => faces[f].points.Length != 4)) return true;
            int firstFace = incident[0];
            int firstVertex = faces[firstFace].vertices[Array.IndexOf(faces[firstFace].points, vertex)];
            foreach (int f in incident)
            {
                if (faces[f].submesh != faces[firstFace].submesh || !data.SameAttributes(firstVertex, faces[f].vertices[Array.IndexOf(faces[f].points, vertex)])) return true;
            }
            // Geometry creases remain protected even when imported normals were smoothed.
            foreach (int next in graph.neighbors[vertex])
            {
                var edge = graph.edges[LodTopologyGraph.EdgeKey(vertex, next)];
                if (edge.Count == 2 && Vector3.Dot(FaceNormal(faces[edge[0].face], data), FaceNormal(faces[edge[1].face], data)) < 0.7071067f) return true;
            }
            return false;
        }

        static Vector3 FaceNormal(LodSourceTopology.Face face, LodMeshData data)
        {
            Vector3 normal = Vector3.zero;
            Vector3 origin = data.positions[face.vertices[0]];
            for (int i = 0; i < face.vertices.Length; i++)
                normal += Vector3.Cross(data.positions[face.vertices[i]] - origin, data.positions[face.vertices[(i + 1) % face.vertices.Length]] - origin);
            return normal.normalized;
        }

        static Candidate MergeLoop(List<int> loop, LodTopologyGraph graph, List<LodSourceTopology.Face> faces, LodMeshData data)
        {
            var removedPoints = new HashSet<int>(loop);
            var removedFaces = new HashSet<int>();
            var replacements = new List<LodSourceTopology.Face>();
            foreach (var pair in graph.edges.OrderBy(k => k.Key))
            {
                var edge = pair.Value;
                if (!removedPoints.Contains(edge[0].a) || !removedPoints.Contains(edge[0].b)) continue;
                if (edge.Count != 2 || !removedFaces.Add(edge[0].face) || !removedFaces.Add(edge[1].face)) return null;
                var first = faces[edge[0].face]; var second = faces[edge[1].face];
                if (first.points.Length != 4 || second.points.Length != 4 || first.submesh != second.submesh) return null;
                var boundary = new Dictionary<int, (int next, int vertex)>();
                foreach (var face in new[] { first, second })
                    for (int i = 0; i < 4; i++)
                    {
                        int a = face.points[i], b = face.points[(i + 1) % 4];
                        if (LodTopologyGraph.EdgeKey(a, b) == pair.Key) continue;
                        if (boundary.ContainsKey(a)) return null;
                        boundary[a] = (b, face.vertices[i]);
                    }
                if (boundary.Count != 6) return null;
                int start = boundary.Keys.Min(), current = start;
                var points = new List<int>(); var vertices = new List<int>();
                for (int i = 0; i < 6; i++)
                {
                    if (!boundary.TryGetValue(current, out var corner)) return null;
                    if (!removedPoints.Contains(current)) { points.Add(current); vertices.Add(corner.vertex); }
                    current = corner.next;
                }
                if (current != start || points.Count != 4 || points.Distinct().Count() != 4) return null;
                int[] v = vertices.ToArray();
                var faceOut = new LodSourceTopology.Face
                {
                    points = points.ToArray(), vertices = v, submesh = first.submesh,
                    triangles = new[] { v[0], v[1], v[2], v[0], v[2], v[3] },
                    referenceTriangles = first.referenceTriangles.Concat(second.referenceTriangles).ToArray()
                };
                if (FaceNormal(faceOut, data).sqrMagnitude < 0.5f) return null;
                replacements.Add(faceOut);
            }
            if (replacements.Count == 0 || loop.Any(v => graph.vertexFaces[v].Any(f => !removedFaces.Contains(f)))) return null;
            var nextFaces = faces.Where((_, f) => !removedFaces.Contains(f)).ToList();
            nextFaces.AddRange(replacements);
            return new Candidate { faces = nextFaces, replacements = replacements, removedFaces = removedFaces };
        }

        static bool Validate(Candidate candidate, LodMeshData data, Settings settings, LodTriangleIntersections intersection, Func<bool> cancelled)
        {
            var metrics = new LodSurfaceValidation.Metrics();
            foreach (var face in candidate.replacements)
            {
                if (!TryChooseDiagonal(face, data, settings, intersection, candidate.removedFaces, out var best, cancelled)) return false;
                metrics.Include(best);
            }
            if (intersection.Intersects(candidate.replacements, candidate.removedFaces)) return false;
            candidate.metrics = metrics;
            return true;
        }

        internal static bool TryChooseDiagonal(LodSourceTopology.Face face, LodMeshData data, Settings settings,
            LodTriangleIntersections intersection, HashSet<int> removedFaces, out LodSurfaceValidation.Metrics best, Func<bool> cancelled = null)
        {
            var v = face.vertices;
            var first = face.triangles;
            var second = new[] { v[0], v[1], v[3], v[1], v[2], v[3] };
            int[] chosen = null;
            best = new LodSurfaceValidation.Metrics { weightedError = float.PositiveInfinity };
            foreach (var diagonal in new[] { first, second })
            {
                face.triangles = diagonal;
                var measured = LodSurfaceValidation.Measure(data, diagonal, face.referenceTriangles, settings.simplify, cancelled);
                if (!Accept(measured, settings) || intersection.Intersects(new List<LodSourceTopology.Face> { face }, removedFaces)) continue;
                if (chosen == null || measured.weightedError < best.weightedError - 1e-7f ||
                    (Mathf.Abs(measured.weightedError - best.weightedError) <= 1e-7f && measured.ColorRms.sqrMagnitude < best.ColorRms.sqrMagnitude - 1e-12f))
                { chosen = diagonal; best = measured; }
            }
            face.triangles = chosen ?? first;
            return chosen != null;
        }

        static bool Accept(LodSurfaceValidation.Metrics metrics, Settings settings)
            => metrics.weightedError <= settings.simplify.targetError && metrics.normalAngle <= settings.maxNormalAngle &&
                (settings.skipColorValidation || metrics.colorError <= settings.maxColorError);
    }
}
