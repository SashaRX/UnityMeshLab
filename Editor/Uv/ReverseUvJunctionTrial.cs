using System;
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
                    comparisonBudget = options.comparisonBudget, cutNarrowJunctions = cuts };
                state.result = await ReverseUvTransfer.Build(state.inputs.levels, trial, useAsync, token);
                return state;
            }
            catch { state.Dispose(); throw; }
        }

        internal static string Compare(PreparedResult baseline, PreparedResult candidate, CancellationToken token = default)
        {
            var before = baseline.result.report; var after = candidate.result.report;
            if (after.atlasSize > before.atlasSize) return "cuts require a larger atlas";
            if (after.texelsPerUnit < before.texelsPerUnit * .9999f) return "cuts reduce texel density";
            if (before.nodes.Count != after.nodes.Count) return "cuts change the input node count";
            for (int n = 0; n < before.nodes.Count; ++n)
            {
                var a = before.nodes[n]; var b = after.nodes[n];
                if (a.faces.Length != b.faces.Length) return "cuts change the face count";
                for (int f = 0; f < a.faces.Length; ++f)
                {
                    token.ThrowIfCancellationRequested();
                    if (a.faces[f].inherited && !b.faces[f].inherited)
                        return $"LOD{a.lod} '{a.key}' face {f} loses inheritance across a new seam";
                    if (a.faces[f].intentionalOverlap && !b.faces[f].intentionalOverlap)
                        return $"LOD{a.lod} '{a.key}' face {f} loses its projected overlap";
                }
            }
            var origin = baseline.inputs.levels[0].inputs[0].toWorld.GetColumn(3);
            for (int l = 0; l < baseline.result.meshes.Count; ++l)
                for (int n = 0; n < baseline.result.meshes[l].Length; ++n)
                {
                    var a = baseline.result.meshes[l][n]; var b = candidate.result.meshes[l][n];
                    var matrix = baseline.inputs.levels[l].inputs[n].toWorld;
                    var positions = ReverseUvTransfer.RelativePositions(new ReverseUvTransfer.Input { mesh = a, toWorld = matrix }, origin);
                    var indices = a.triangles; var otherIndices = b.triangles; var uv = a.uv2; var otherUv = b.uv2;
                    for (int t = 0; t < indices.Length; t += 3)
                    {
                        token.ThrowIfCancellationRequested();
                        var p = positions[indices[t]]; var q = positions[indices[t + 1]]; var r = positions[indices[t + 2]];
                        double oldStretch = ReverseUvTransfer.TriangleAnisotropy(p, q, r, uv[indices[t]], uv[indices[t + 1]], uv[indices[t + 2]]);
                        double newStretch = ReverseUvTransfer.TriangleAnisotropy(p, q, r, otherUv[otherIndices[t]], otherUv[otherIndices[t + 1]], otherUv[otherIndices[t + 2]]);
                        if (newStretch > oldStretch * 1.0001 + .0001)
                            return $"LOD{baseline.inputs.levels[l].lod} face {t / 3} increases distortion";
                    }
                }
            return null;
        }
    }
}
