using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Optional seam changes must preserve already inherited faces and
    /// per-face distortion before any result is published to the LOD chain.</summary>
    internal static class ReverseUvJunctionTrial
    {
        internal sealed class PreparedResult : IDisposable
        {
            internal ReverseUvInputs inputs;
            internal ReverseUvTransfer.Result result;
            internal string refusal;
            public void Dispose() { result?.Dispose(); inputs?.Dispose(); }
        }

        internal static async Task<PreparedResult> Build(ReverseUvTransfer.Level[] sources,
            ReverseUvTransfer.Options options, bool prepareSeed, float density, bool useAsync = false,
            CancellationToken token = default)
        {
            var baseline = await BuildOne(sources, options, prepareSeed, density, false, useAsync, token);
            if (!options.cutNarrowJunctions) return baseline;
            PreparedResult candidate = null;
            try
            {
                candidate = await BuildOne(sources, options, prepareSeed, density, true, useAsync, token);
                string refusal = Compare(baseline, candidate, token);
                if (refusal == null)
                {
                    baseline.Dispose();
                    return candidate;
                }
                baseline.refusal = refusal;
            }
            catch (InvalidOperationException error)
            {
                baseline.refusal = error.Message;
            }
            catch
            {
                candidate?.Dispose(); baseline.Dispose(); throw;
            }
            candidate?.Dispose();
            UvtLog.Warn(UvtLog.Category.Repack, $"[JunctionCuts] Reverse retained the baseline: {baseline.refusal}");
            return baseline;
        }

        static async Task<PreparedResult> BuildOne(ReverseUvTransfer.Level[] sources,
            ReverseUvTransfer.Options options, bool prepareSeed, float density, bool cuts, bool useAsync,
            CancellationToken token)
        {
            var state = new PreparedResult();
            try
            {
                state.inputs = ReverseUvInputs.Prepare(sources, token);
                int size = prepareSeed ? state.inputs.PrepareSeed(options.seedResolution, options.padding, density, token, cuts)
                    : options.seedResolution;
                var trial = new ReverseUvTransfer.Options { seedResolution = size, padding = options.padding,
                    maxAtlasSize = options.maxAtlasSize, projectionReach = options.projectionReach, normalDot = options.normalDot,
                    maxAnisotropy = options.maxAnisotropy, preserveProjectedOverlap = options.preserveProjectedOverlap,
                    comparisonBudget = options.comparisonBudget, cutNarrowJunctions = cuts,
                    splitDonorSeams = options.splitDonorSeams, fillAtlasVacancies = options.fillAtlasVacancies };
                state.result = await ReverseUvTransfer.Build(state.inputs.levels, trial, useAsync, token);
                return state;
            }
            catch { state.Dispose(); throw; }
        }

        internal static string Compare(PreparedResult baseline, PreparedResult candidate, CancellationToken token = default)
            => Compare(baseline.result, candidate.result, baseline.inputs?.levels, token);

        internal static string Compare(ReverseUvTransfer.Result baseline, ReverseUvTransfer.Result candidate,
            ReverseUvTransfer.Level[] levels, CancellationToken token = default)
        {
            var before = baseline.report; var after = candidate.report;
            if (after.atlasSize > before.atlasSize) return "cuts require a larger atlas";
            if (after.texelsPerUnit < before.texelsPerUnit * .9999f) return "cuts reduce texel density";
            if (before.nodes.Count != after.nodes.Count) return "cuts change the input node count";
            for (int n = 0; n < before.nodes.Count; ++n)
            {
                var a = before.nodes[n]; var b = after.nodes[n];
                var descendants = Enumerable.Range(0, b.faces.Length).GroupBy(f => b.sourceFaces == null ? f : b.sourceFaces[f])
                    .ToDictionary(g => g.Key, g => g.ToArray());
                for (int f = 0; f < a.faces.Length; ++f)
                {
                    token.ThrowIfCancellationRequested();
                    int source = a.sourceFaces == null ? f : a.sourceFaces[f];
                    if (!descendants.TryGetValue(source, out var pieces)) return "cuts lose a source face";
                    if (a.faces[f].inherited && !Covered(a, f, b, pieces.Where(p => b.faces[p].inherited), token))
                        return $"LOD{a.lod} '{a.key}' face {f} loses inheritance across a new seam";
                    if (a.faces[f].intentionalOverlap && !Covered(a, f, b, pieces.Where(p => b.faces[p].intentionalOverlap), token))
                        return $"LOD{a.lod} '{a.key}' face {f} loses its projected overlap";
                }
            }
            if (baseline.meshes.Count == 0) return null;
            var origin = levels[0].inputs[0].toWorld.GetColumn(3);
            int reportNode = 0;
            for (int l = 0; l < baseline.meshes.Count; ++l)
                for (int n = 0; n < baseline.meshes[l].Length; ++n)
                {
                    var a = baseline.meshes[l][n]; var b = candidate.meshes[l][n];
                    var oldNode = before.nodes[reportNode]; var newNode = after.nodes[reportNode++];
                    var matrix = levels[l].inputs[n].toWorld;
                    var positions = ReverseUvTransfer.RelativePositions(new ReverseUvTransfer.Input { mesh = a, toWorld = matrix }, origin);
                    var indices = a.triangles; var otherIndices = b.triangles; var uv = a.uv2; var otherUv = b.uv2;
                    var oldStretchBySource = new Dictionary<int, double>();
                    for (int t = 0; t < indices.Length; t += 3)
                    {
                        token.ThrowIfCancellationRequested();
                        var p = positions[indices[t]]; var q = positions[indices[t + 1]]; var r = positions[indices[t + 2]];
                        double oldStretch = ReverseUvTransfer.TriangleAnisotropy(p, q, r, uv[indices[t]], uv[indices[t + 1]], uv[indices[t + 2]]);
                        if (!oldNode.seed && !oldNode.faces[t / 3].inherited) continue;
                        int source = oldNode.sourceFaces[t / 3];
                        oldStretchBySource.TryGetValue(source, out double previous);
                        oldStretchBySource[source] = Math.Max(previous, oldStretch);
                    }
                    var otherPositions = ReverseUvTransfer.RelativePositions(new ReverseUvTransfer.Input { mesh = b, toWorld = matrix }, origin);
                    for (int t = 0; t < otherIndices.Length; t += 3)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!oldStretchBySource.TryGetValue(newNode.sourceFaces[t / 3], out double oldStretch)) continue;
                        double newStretch = ReverseUvTransfer.TriangleAnisotropy(otherPositions[otherIndices[t]], otherPositions[otherIndices[t + 1]],
                            otherPositions[otherIndices[t + 2]], otherUv[otherIndices[t]], otherUv[otherIndices[t + 1]], otherUv[otherIndices[t + 2]]);
                        if (newStretch > oldStretch * 1.0001 + .0001)
                            return $"LOD{levels[l].lod} face {t / 3} increases distortion";
                    }
                }
            return null;
        }

        static Vector2[] Region(ReverseUvTransfer.NodeReport node, int face)
        {
            if (node.sourceBarycentrics == null) return new[] { Vector2.zero, Vector2.right, Vector2.up };
            return Enumerable.Range(face * 3, 3).Select(i => new Vector2(node.sourceBarycentrics[i].y, node.sourceBarycentrics[i].z)).ToArray();
        }

        internal static bool Covered(ReverseUvTransfer.NodeReport before, int face, ReverseUvTransfer.NodeReport after,
            IEnumerable<int> pieces, CancellationToken token)
        {
            var triangle = Region(before, face); double area = Math.Abs(Cross(triangle[0], triangle[1], triangle[2]));
            double covered = 0;
            foreach (int piece in pieces)
            {
                token.ThrowIfCancellationRequested();
                var clip = Region(after, piece); var polygon = triangle.ToList();
                int orientation = Math.Sign(Cross(clip[0], clip[1], clip[2]));
                for (int e = 0; e < 3 && polygon.Count > 0; ++e)
                {
                    var output = new List<Vector2>(); var previous = polygon[polygon.Count - 1];
                    double last = orientation * Cross(clip[e], clip[(e + 1) % 3], previous);
                    foreach (var point in polygon)
                    {
                        double next = orientation * Cross(clip[e], clip[(e + 1) % 3], point);
                        if ((last >= 0) != (next >= 0)) output.Add(Vector2.LerpUnclamped(previous, point, (float)(last / (last - next))));
                        if (next >= 0) output.Add(point);
                        previous = point; last = next;
                    }
                    polygon = output;
                }
                for (int k = 1; k + 1 < polygon.Count; ++k) covered += Math.Abs(Cross(polygon[0], polygon[k], polygon[k + 1]));
            }
            return area > 0 && covered >= area * (1 - 1e-5);
        }

        static double Cross(Vector2 a, Vector2 b, Vector2 c)
            => ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);
    }
}
