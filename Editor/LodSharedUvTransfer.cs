using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    // A far triangle reads one connected source UV2 chart. Corner duplication
    // isolates chart assignments without adding triangles or moving geometry.
    internal static class LodSharedUvTransfer
    {
        [Serializable]
        internal sealed class Report
        {
            public bool accepted;
            public int charts, assignedFaces, unresolvedFaces, verticesBefore, verticesAfter;
            public float uvRmsBefore, uvRmsAfter;
            public bool bakedField;
            public int visibleSamples;
        }
        sealed class Chart
        {
            internal int slot;
            internal int[] faces;
            internal Vector3[] normals;
            internal TriangleBvh bvh;
        }
        sealed class Visible
        {
            internal Vector3 bary, normal;
            internal Vector4[] desired;
            internal int view, pixel;
        }
        static readonly Vector3[] Samples = { Vector3.one/3, new Vector3(.8f,.1f,.1f),
            new Vector3(.1f,.8f,.1f),new Vector3(.1f,.1f,.8f),new Vector3(.45f,.45f,.1f),
            new Vector3(.45f,.1f,.45f),new Vector3(.1f,.45f,.45f) };
        internal static Mesh Transfer(Mesh source, Mesh reduced, MeshSimplifier.SimplifySettings settings,
            out Report report, Func<bool> cancelled = null, LodScreenValidation screen = null, Func<Vector2, Vector3, Vector4>[] bakedFields = null, int minVisiblePixels = 0)
        {
            report = new Report { verticesBefore = reduced.vertexCount, verticesAfter = reduced.vertexCount };
            if (source.blendShapeCount > 0 || reduced.blendShapeCount > 0 ||
                source.HasVertexAttribute(VertexAttribute.BlendWeight) || reduced.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                !source.HasVertexAttribute(VertexAttribute.TexCoord1) || !reduced.HasVertexAttribute(VertexAttribute.TexCoord1) ||
                source.GetVertexAttributeDimension(VertexAttribute.TexCoord1) != 2 || reduced.GetVertexAttributeDimension(VertexAttribute.TexCoord1) != 2 ||
                Enumerable.Range(0, reduced.subMeshCount).Any(s => reduced.GetTopology(s) != MeshTopology.Triangles)) return null;
            Check(cancelled);
            var original = new LodMeshData(source); var target = new LodMeshData(reduced);
            var charts = Charts(original, cancelled); report.charts = charts.Count;
            var visible = new List<Visible>[LodMeshData.TriangleCount(reduced)];
            bool baked = screen != null && bakedFields?.Length > 0; report.bakedField = baked;
            if (baked)
            {
                var sourceFaces = Enumerable.Range(0, source.subMeshCount).SelectMany(s => LodMeshData.Triangles(source, s)).ToArray();
                screen.AddVisibleSurfaceSamples(reduced, (sf, sb, tf, tb, sn, tn, view, pixel) =>
                {
                    var tex = (Vector2)(original.uvs[1][sourceFaces[sf * 3]] * sb.x + original.uvs[1][sourceFaces[sf * 3 + 1]] * sb.y + original.uvs[1][sourceFaces[sf * 3 + 2]] * sb.z);
                    if (visible[tf] == null) visible[tf] = new List<Visible>();
                    visible[tf].Add(new Visible { bary = tb, normal = tn, view = view, pixel = pixel, desired = bakedFields.Select(field => field(tex, sn)).ToArray() });
                }, cancelled);
                report.visibleSamples = visible.Sum(samples => samples?.Count ?? 0);
            }
            var corners = new List<int>(); var uv = new List<Vector2>(); var indices = new List<int>[reduced.subMeshCount];
            TransferFaces(original, target, charts, visible, baked, bakedFields, minVisiblePixels, corners, uv, indices, report, cancelled);
            Mesh mesh = null;
            try
            {
                Check(cancelled);
                mesh = new Mesh { name = reduced.name, indexFormat = corners.Count > 65535 ? IndexFormat.UInt32 : reduced.indexFormat };
                SetCornerFields(mesh, target, reduced, corners, uv);
                mesh.subMeshCount = indices.Length;
                for (int slot = 0; slot < indices.Length; slot++) mesh.SetTriangles(indices[slot], slot);
                mesh.RecalculateBounds();
                if (MeshUvState.IsDraft(reduced)) MeshUvState.SetDraft(mesh, true);
                var before = LodSurfaceValidation.MeasureMeshes(source, reduced, settings, cancelled, ignoreDegenerateFaces: true);
                var after = LodSurfaceValidation.MeasureMeshes(source, mesh, settings, cancelled, ignoreDegenerateFaces: true);
                report.uvRmsBefore = before.UvRms; report.uvRmsAfter = after.UvRms;
                if (float.IsNaN(after.UvRms) || float.IsInfinity(after.UvRms) ||
                    (baked ? report.assignedFaces == 0 : after.UvRms >= before.UvRms - 1e-6f)) return null;
                report.accepted = true; report.verticesAfter = mesh.vertexCount;
                var result = mesh; mesh = null; return result;
            }
            finally { if (mesh) UnityEngine.Object.DestroyImmediate(mesh); }
        }
        static double[] Errors(Vector2[] values, List<Visible> samples, Func<Vector2, Vector3, Vector4>[] fields)
        {
            int views = samples?.Select(s => s.view).DefaultIfEmpty(5).Max() + 1 ?? 6;
            var errors = new double[views * fields.Length];
            if (samples == null) return errors;
            foreach (var group in samples.GroupBy(s => (s.view, s.pixel)))
            {
                var actual = new Vector4[fields.Length]; var desired = new Vector4[fields.Length]; int count = 0;
                foreach (var sample in group)
                {
                    var uv = values[0] * sample.bary.x + values[1] * sample.bary.y + values[2] * sample.bary.z; count++;
                    for (int i = 0; i < fields.Length; i++) { actual[i] += fields[i](uv, sample.normal); desired[i] += sample.desired[i]; }
                }
                for (int i = 0; i < fields.Length; i++) errors[group.Key.view * fields.Length + i] += (actual[i] - desired[i]).sqrMagnitude / count;
            }
            return errors;
        }
        // Keep a fitted corner inside the same source atlas island as its prior.
        // Ambiguous seed islands stay pinned. No render corners are introduced.
        internal static void ProjectOriginalCharts(Mesh source, Mesh target, Vector2[] values, Func<bool> cancelled = null)
        {
            var original = new LodMeshData(source); var reduced = new LodMeshData(target);
            var charts = Charts(original, cancelled);
            var points = original.uvs[1].Select(v => new Vector3(v.x, v.y, 0)).ToArray();
            var trees = charts.Select(c => new TriangleBvh(points, c.faces)).ToArray();
            for (int v = 0; v < values.Length; v++)
            {
                Check(cancelled); var prior = new Vector3(reduced.uvs[1][v].x, reduced.uvs[1][v].y, 0);
                int selected = SelectOriginalChart(charts, trees, prior, reduced.positions[v], original.scale, out bool ambiguous);
                if (selected < 0 || ambiguous) { values[v] = (Vector2)reduced.uvs[1][v]; continue; }
                var hit = trees[selected].FindNearest(new Vector3(values[v].x, values[v].y, 0));
                if (hit.triangleIndex < 0) { values[v] = (Vector2)reduced.uvs[1][v]; continue; }
                var faces = charts[selected].faces; int i = hit.triangleIndex * 3;
                values[v] = (Vector2)(original.uvs[1][faces[i]] * hit.barycentric.x +
                    original.uvs[1][faces[i + 1]] * hit.barycentric.y + original.uvs[1][faces[i + 2]] * hit.barycentric.z);
            }
        }

        static List<Chart> Charts(LodMeshData mesh, Func<bool> cancelled)
        {
            var result = new List<Chart>();
            for (int slot = 0; slot < mesh.source.subMeshCount; slot++)
            {
                Check(cancelled); var faces = LodMeshData.Triangles(mesh.source, slot);
                var parents = Enumerable.Range(0, faces.Length / 3).ToArray();
                var ids = ChartPointIds(mesh);
                var edges = ChartEdges(faces, ids);
                foreach (var owners in edges.Values)
                    if (owners.Count == 2) parents[Root(owners[0])] = Root(owners[1]);
                foreach (var group in Enumerable.Range(0, parents.Length).GroupBy(Root))
                {
                    int[] triangles = group.SelectMany(f => new[] { faces[3 * f], faces[3 * f + 1], faces[3 * f + 2] }).ToArray();
                    var normals = new Vector3[triangles.Length / 3];
                    for (int f = 0; f < triangles.Length; f += 3)
                        normals[f / 3] = Vector3.Cross(mesh.positions[triangles[f + 1]] - mesh.positions[triangles[f]], mesh.positions[triangles[f + 2]] - mesh.positions[triangles[f]]).normalized;
                    result.Add(new Chart { slot = slot, faces = triangles, normals = normals, bvh = new TriangleBvh(mesh.positions, triangles) });
                }
                int Root(int i) { while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; } return i; }
            }
            return result;
        }
        static double ChartDistance(Chart chart, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
        {
            double error = 0;
            foreach (var bary in Samples)
            {
                var hit = chart.bvh.FindNearestNormalFiltered(a * bary.x + b * bary.y + c * bary.z, normal, chart.normals, .001f);
                if (hit.triangleIndex < 0) { error = double.PositiveInfinity; break; }
                error += hit.distSq;
            }
            return error;
        }

        static Vector2[] ProjectChart(LodMeshData original, LodMeshData target, int[] faces, int f, Vector3 normal, Chart best, out bool resolved)
        {
            var projected = new Vector2[3]; resolved = best != null;
            for (int corner = 0; corner < 3 && resolved; corner++)
            {
                var hit = best.bvh.FindNearestNormalFiltered(target.positions[faces[f + corner]], normal, best.normals, .001f);
                if (hit.triangleIndex < 0) { resolved = false; break; }
                int j = hit.triangleIndex * 3;
                projected[corner] = (Vector2)(original.uvs[1][best.faces[j]] * hit.barycentric.x +
                    original.uvs[1][best.faces[j + 1]] * hit.barycentric.y + original.uvs[1][best.faces[j + 2]] * hit.barycentric.z);
            }
            return projected;
        }

        static Vector2[] SelectBakedProjection(LodMeshData original, LodMeshData target, int[] faces, int f, Vector3 normal,
            List<(Chart chart, double distance)> nearby, List<Visible> training, int minVisiblePixels, Func<Vector2, Vector3, Vector4>[] bakedFields,
            double score, Func<bool> cancelled, out bool resolved, out Chart best)
        {
            if (training != null && training.GroupBy(s => s.view).Max(g => g.Select(s => s.pixel).Distinct().Count()) < minVisiblePixels) training = null;
            var projected = Enumerable.Range(0, 3).Select(i => (Vector2)target.uvs[1][faces[f + i]]).ToArray();
            var errors = Errors(projected, training, bakedFields); double fieldScore = errors.Sum();
            resolved = false; best = null;
            double reach = score + 7 * Math.Pow(original.scale / 64, 2);
            foreach (var chart in nearby.Where(n => n.distance <= reach).OrderBy(n => n.distance).Take(12).Select(candidate => candidate.chart))
            {
                Check(cancelled); var values = ProjectChart(original, target, faces, f, normal, chart, out bool valid);
                if (!valid) continue;
                var proposed = Errors(values, training, bakedFields);
                if (proposed.Any(e => double.IsNaN(e) || double.IsInfinity(e)) ||
                    proposed.Sum() >= fieldScore - 1e-9 || proposed.Where((e, i) => e > errors[i] + 1e-10).Any()) continue;
                projected = values; errors = proposed; fieldScore = errors.Sum(); resolved = true; best = chart;
            }
            return projected;
        }

        static void SetCornerFields(Mesh mesh, LodMeshData target, Mesh reduced, List<int> corners, List<Vector2> uv)
        {
            mesh.SetVertices(corners.ConvertAll(v => target.positions[v]));
            if (target.normals.Length == target.positions.Length) mesh.SetNormals(corners.ConvertAll(v => target.normals[v]));
            if (target.tangents.Length == target.positions.Length) mesh.SetTangents(corners.ConvertAll(v => target.tangents[v]));
            if (target.colors.Length == target.positions.Length)
            {
                if (reduced.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8)
                    mesh.SetColors(corners.ConvertAll(v => (Color32)target.colors[v]));
                else mesh.SetColors(corners.ConvertAll(v => target.colors[v]));
            }
            SetCornerUv(mesh, target, corners, uv);
        }

        static void SetCornerUv(Mesh mesh, LodMeshData target, List<int> corners, List<Vector2> uv)
        {
            for (int ch = 0; ch < 8; ch++)
            {
                if (target.uvs[ch] == null) continue;
                int channel = ch;
                if (ch == 1) mesh.SetUVs(ch, uv);
                else if (target.uvDimensions[ch] == 2) mesh.SetUVs(ch, corners.ConvertAll(v => (Vector2)target.uvs[channel][v]));
                else if (target.uvDimensions[ch] == 3) mesh.SetUVs(ch, corners.ConvertAll(v => (Vector3)target.uvs[channel][v]));
                else mesh.SetUVs(ch, corners.ConvertAll(v => target.uvs[channel][v]));
            }
        }

        static int SelectOriginalChart(List<Chart> charts, TriangleBvh[] trees, Vector3 prior, Vector3 position, float scale, out bool ambiguous)
        {
            int selected = -1; float best = float.PositiveInfinity; ambiguous = false;
            for (int c = 0; c < charts.Count; c++)
            {
                var uv = trees[c].FindNearest(prior); var geometry = charts[c].bvh.FindNearest(position);
                if (uv.triangleIndex < 0 || geometry.triangleIndex < 0) continue;
                float score = uv.distSq + geometry.distSq / (scale * scale);
                if (score < best - 1e-10f) { best = score; selected = c; ambiguous = false; }
                else if (Mathf.Abs(score - best) <= 1e-10f) ambiguous = true;
            }
            return selected;
        }

        static Dictionary<(int, int), List<int>> ChartEdges(int[] faces, int[] ids)
        {
            var edges = new Dictionary<(int, int), List<int>>();
            for (int f = 0; f < faces.Length; f += 3) for (int corner = 0; corner < 3; corner++)
            {
                int a = ids[faces[f + corner]], b = ids[faces[f + (corner + 1) % 3]];
                var edge = (Math.Min(a, b), Math.Max(a, b));
                if (!edges.TryGetValue(edge, out var owners)) edges[edge] = owners = new List<int>();
                owners.Add(f / 3);
            }
            return edges;
        }

        static int[] ChartPointIds(LodMeshData mesh)
        {
            var keys = new Dictionary<(Vector3, Vector2), int>(); var ids = new int[mesh.positions.Length];
            for (int v = 0; v < ids.Length; v++)
            {
                var key = (mesh.positions[v], (Vector2)mesh.uvs[1][v]);
                if (!keys.TryGetValue(key, out int id)) { id = keys.Count; keys.Add(key, id); }
                ids[v] = id;
            }
            return ids;
        }

        static void TransferFaces(LodMeshData original, LodMeshData target, List<Chart> charts, List<Visible>[] visible, bool baked,
            Func<Vector2, Vector3, Vector4>[] bakedFields, int minVisiblePixels, List<int> corners, List<Vector2> uv, List<int>[] indices, Report report, Func<bool> cancelled)
        {
            var remap = new Dictionary<(int, Chart, Vector2), int>(); int faceIndex = 0;
            for (int slot = 0; slot < target.source.subMeshCount; slot++)
            {
                indices[slot] = new List<int>();
                var faces = LodMeshData.Triangles(target.source, slot);
                var candidates = charts.Where(c => c.slot == slot).ToArray();
                for (int f = 0; f < faces.Length; f += 3, faceIndex++)
                {
                    Check(cancelled);
                    var projected = ResolveFace(original, target, faces, f, candidates, visible[faceIndex], baked, bakedFields, minVisiblePixels, cancelled,
                        out bool resolved, out Chart best);
                    if (resolved) report.assignedFaces++; else report.unresolvedFaces++;
                    AddFaceCorners(target, faces, f, projected, resolved, best, remap, corners, uv, indices[slot]);
                }
            }
        }

        static Vector2[] ResolveFace(LodMeshData original, LodMeshData target, int[] faces, int f, Chart[] candidates, List<Visible> training, bool baked,
            Func<Vector2, Vector3, Vector4>[] bakedFields, int minVisiblePixels, Func<bool> cancelled, out bool resolved, out Chart best)
        {
            Vector3 a = target.positions[faces[f]], b = target.positions[faces[f + 1]], c = target.positions[faces[f + 2]];
            var normal = Vector3.Cross(b - a, c - a).normalized;
            best = null; double score = double.PositiveInfinity;
            var nearby = new List<(Chart chart, double distance)>();
            foreach (var chart in candidates)
            {
                double error = ChartDistance(chart, a, b, c, normal);
                if (error < score) { score = error; best = chart; }
                if (!double.IsInfinity(error)) nearby.Add((chart, error));
            }
            var projected = ProjectChart(original, target, faces, f, normal, best, out resolved);
            if (baked)
            {
                projected = SelectBakedProjection(original, target, faces, f, normal, nearby, training, minVisiblePixels, bakedFields, score, cancelled, out resolved, out best);
            }
            return projected;
        }

        static void AddFaceCorners(LodMeshData target, int[] faces, int f, Vector2[] projected, bool resolved, Chart best,
            Dictionary<(int, Chart, Vector2), int> remap, List<int> corners, List<Vector2> uv, List<int> mappedIndices)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                int vertex = faces[f + corner]; var value = resolved ? projected[corner] : (Vector2)target.uvs[1][vertex];
                var key = (vertex, resolved ? best : null, value);
                if (!remap.TryGetValue(key, out int mapped)) { mapped = corners.Count; remap.Add(key, mapped); corners.Add(vertex); uv.Add(value); }
                mappedIndices.Add(mapped);
            }
        }

        static void Check(Func<bool> cancelled) { if (cancelled?.Invoke() == true) throw new OperationCanceledException(); }
    }
}
