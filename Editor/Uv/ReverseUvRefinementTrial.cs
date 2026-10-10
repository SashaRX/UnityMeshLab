using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        /// <summary>An intermediate split can improve one LOD but make its next
        /// descendant lose UV coverage. Retain whole donor faces responsible for
        /// that loss, retry locally, and publish only a chain that preserves the
        /// complete previously inherited regions.</summary>
        internal static async Task<Result> Build(Level[] levels, Options options, bool useAsync = false, CancellationToken token = default)
        {
            Validate(levels, options);
            if (!options.splitDonorSeams) return await BuildCore(levels, options, useAsync, token);
            Result baseline = null, candidate = null;
            try
            {
                try { baseline = await BuildCore(levels, TrialOptions(options, false), useAsync, token); }
                catch (InvalidOperationException) { return await BuildCore(levels, options, useAsync, token); }
                var trial = TrialOptions(options, true); string refusal = null;
                for (int attempt = 0; attempt < 8; ++attempt)
                {
                    token.ThrowIfCancellationRequested();
                    try { candidate = await BuildCore(levels, trial, useAsync, token); }
                    catch (InvalidOperationException error) { refusal = error.Message; break; }
                    try { refusal = ReverseUvJunctionTrial.Compare(baseline, candidate, levels, token); }
                    catch (InvalidOperationException error) { refusal = error.Message; break; }
                    if (refusal == null)
                    {
                        baseline.Dispose(); baseline = null;
                        var accepted = candidate; candidate = null; return accepted;
                    }
                    bool extended = ProtectDonorAncestors(baseline.report, candidate.report, trial, token);
                    candidate.Dispose(); candidate = null;
                    if (!extended) break;
                }
                baseline.report.refinementRefusal = refusal;
                UvtLog.Warn(UvtLog.Category.Repack, "[ReverseUV] Donor seam trial retained continuous baseline charts: " + refusal);
                var retained = baseline; baseline = null; return retained;
            }
            finally { candidate?.Dispose(); baseline?.Dispose(); }
        }

        static Options TrialOptions(Options options, bool split) => new Options {
            seedResolution = options.seedResolution, padding = options.padding, maxAtlasSize = options.maxAtlasSize,
            projectionReach = options.projectionReach, normalDot = options.normalDot, maxAnisotropy = options.maxAnisotropy,
            preserveProjectedOverlap = options.preserveProjectedOverlap, cutNarrowJunctions = options.cutNarrowJunctions,
            fillAtlasVacancies = options.fillAtlasVacancies, splitDonorSeams = split, comparisonBudget = options.comparisonBudget,
                rotateCharts = options.rotateCharts, rotateChartsToAxis = options.rotateChartsToAxis,seedDensity=options.seedDensity,
            seamCutExclusions = new Dictionary<(int, int), HashSet<int>>() };

        static bool ProtectDonorAncestors(Report before, Report after, Options trial, CancellationToken token)
        {
            bool extended = false;
            for (int n = 0; n < before.nodes.Count; ++n)
            {
                var a = before.nodes[n]; var b = after.nodes[n]; if (a.seed) continue;
                var pieces = Enumerable.Range(0, b.faces.Length).GroupBy(f => b.sourceFaces[f]).ToDictionary(g => g.Key, g => g.ToArray());
                for (int f = 0; f < a.faces.Length; ++f)
                {
                    token.ThrowIfCancellationRequested(); var face = a.faces[f];
                    if (!face.inherited || ReverseUvJunctionTrial.Covered(a, f, b, pieces[a.sourceFaces[f]].Where(p => b.faces[p].inherited), token)) continue;
                    var parent = before.nodes.Where(p => p.lod == face.parentLod).ElementAt(face.parentMesh);
                    if (parent.seed) continue;
                    var key = (face.parentLod, face.parentMesh);
                    if (!trial.seamCutExclusions.TryGetValue(key, out var excluded))
                    { excluded = new HashSet<int>(); trial.seamCutExclusions.Add(key, excluded); }
                    extended |= excluded.Add(parent.sourceFaces[face.parentFace]);
                }
            }
            return extended;
        }
    }
}
