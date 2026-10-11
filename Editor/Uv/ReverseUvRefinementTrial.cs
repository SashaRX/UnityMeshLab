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
            var result=await BuildRefined(levels,options,useAsync,token);
            try
            {
                // Compare donor cuts at their requested density. Filling the
                // atlas first gives the uncut trial an unrelated packing bonus
                // and can reject an otherwise valid refined topology.
                await FinalizePacking(result,options,useAsync,token);
                return result;
            }
            catch {result.Dispose();throw;}
        }

        static async Task<Result> BuildRefined(Level[] levels, Options options, bool useAsync, CancellationToken token)
        {
            Validate(levels, options);
            if (!options.splitDonorSeams) return await BuildCore(levels, options, useAsync, token);
            Result baseline = null, candidate = null, incrementalCandidate=null;
            try
            {
                try { baseline = await BuildCore(levels, TrialOptions(options, false), useAsync, token); }
                catch (InvalidOperationException) { return await BuildCore(levels, options, useAsync, token); }
                var stable=await TryRefinement(levels,options,baseline,false,useAsync,token);
                candidate=stable.result;
                if (candidate!=null && (candidate.report.nodes.Any(n=>n.splitSourceFaces>0)
                    || !candidate.report.nodes.Any(n=>n.faces.Any(f=>f.cutRefusal!=null
                        && f.cutRefusal.StartsWith("seam-precision:",StringComparison.Ordinal)))))
                {
                    baseline.Dispose();baseline=null;
                    var accepted=candidate;candidate=null;return accepted;
                }
                if (candidate==null)
                {
                    baseline.report.refinementRefusal=stable.refusal;
                    UvtLog.Warn(UvtLog.Category.Repack,"[ReverseUV] Donor seam trial retained continuous baseline charts: "+stable.refusal);
                    var retained=baseline;baseline=null;return retained;
                }
                var incremental=await TryRefinement(levels,options,baseline,true,useAsync,token);
                incrementalCandidate=incremental.result;
                if (incrementalCandidate!=null && ReverseUvJunctionTrial.Compare(candidate,incrementalCandidate,levels,token)==null)
                {
                    candidate?.Dispose();candidate=null;baseline.Dispose();baseline=null;
                    var accepted=incrementalCandidate;incrementalCandidate=null;return accepted;
                }
                candidate.report.refinementRefusal=incremental.refusal;
                baseline.Dispose();baseline=null;
                var continuous=candidate;candidate=null;return continuous;
            }
            finally { incrementalCandidate?.Dispose();candidate?.Dispose();baseline?.Dispose(); }
        }

        static async Task<(Result result,string refusal)> TryRefinement(Level[] levels,Options options,Result baseline,
            bool incremental,bool useAsync,CancellationToken token)
        {
            var trial=TrialOptions(options,true);trial.validateCutsIncrementally=incremental;
            string refusal=null;Result candidate=null;
            try
            {
                for(int attempt=0;attempt<8;++attempt)
                {
                    token.ThrowIfCancellationRequested();
                    try {candidate=await BuildCore(levels,trial,useAsync,token);}
                    catch(InvalidOperationException error) {refusal=error.Message;break;}
                    try {refusal=ReverseUvJunctionTrial.Compare(baseline,candidate,levels,token);}
                    catch(InvalidOperationException error) {refusal=error.Message;break;}
                    if(refusal==null) {var accepted=candidate;candidate=null;return (accepted,null);}
                    bool extended=ProtectDonorAncestors(baseline,candidate,trial,token);
                    foreach (var failure in ReverseUvJunctionTrial.DistortionFailures(baseline,candidate,levels,token))
                    {
                        var node=baseline.report.nodes[failure.node];
                        int mesh=baseline.report.nodes.Take(failure.node).Count(p=>p.lod==node.lod);
                        extended |= ProtectFace(trial,(node.lod,mesh),failure.source);
                        foreach (int f in Enumerable.Range(0,node.faces.Length).Where(f=>node.sourceFaces[f]==failure.source))
                            extended |= ProtectParent(baseline.report,node.faces[f],trial);
                    }
                    candidate.Dispose(); candidate = null;
                    if (!extended) break;
                }
                return (null,refusal);
            }
            finally {candidate?.Dispose();}
        }

        static Options TrialOptions(Options options, bool split) => new Options {
            seedResolution = options.seedResolution, padding = options.padding, maxAtlasSize = options.maxAtlasSize,
            projectionReach = options.projectionReach, normalDot = options.normalDot, maxAnisotropy = options.maxAnisotropy,
            boundaryToleranceTexels = options.boundaryToleranceTexels, relativeStretchTolerance = options.relativeStretchTolerance,
            preserveProjectedOverlap = options.preserveProjectedOverlap, cutNarrowJunctions = options.cutNarrowJunctions,
            fillAtlasVacancies = options.fillAtlasVacancies, splitDonorSeams = split, comparisonBudget = options.comparisonBudget,
            repackFinalAtlas = options.repackFinalAtlas, fillFinalAtlas = options.fillFinalAtlas,
                rotateCharts = options.rotateCharts, rotateChartsToAxis = options.rotateChartsToAxis,seedDensity=options.seedDensity,
            seamCutExclusions = new Dictionary<(int, int), HashSet<int>>() };

        static bool ProtectDonorAncestors(Result baseline, Result candidate, Options trial, CancellationToken token)
        {
            var before=baseline.report;var after=candidate.report;
            var meshes=baseline.meshes.SelectMany(m=>m).ToArray();
            bool extended = false;
            for (int n = 0; n < before.nodes.Count; ++n)
            {
                var a = before.nodes[n]; var b = after.nodes[n]; if (a.seed) continue;
                var coverage=new ReverseUvJunctionTrial.CoverageContext(a,meshes.ElementAtOrDefault(n),before.atlasSize,trial.boundaryToleranceTexels);
                var pieces = Enumerable.Range(0, b.faces.Length).GroupBy(f => b.sourceFaces[f]).ToDictionary(g => g.Key, g => g.ToArray());
                for (int f = 0; f < a.faces.Length; ++f)
                {
                    token.ThrowIfCancellationRequested(); var face = a.faces[f];
                    if (!face.inherited && !face.intentionalOverlap) continue;
                    if (!pieces.TryGetValue(a.sourceFaces[f],out var descendants)) return false;
                    bool inheritedCovered = !face.inherited || coverage.Covers(f,b,descendants.Where(p=>b.faces[p].inherited),token);
                    bool overlapCovered = !face.intentionalOverlap || coverage.Covers(f,b,descendants.Where(p=>b.faces[p].intentionalOverlap),token);
                    if (inheritedCovered && overlapCovered) continue;
                    int mesh=before.nodes.Take(n).Count(p=>p.lod==a.lod);
                    extended |= ProtectFace(trial,(a.lod,mesh),a.sourceFaces[f]);
                    extended |= ProtectParent(before,face,trial);
                }
            }
            return extended;
        }

        static bool ProtectParent(Report before,Face face,Options trial)
        {
            if (!face.inherited) return false;
            var parent=before.nodes.Where(p=>p.lod==face.parentLod).ElementAt(face.parentMesh);
            return !parent.seed && ProtectFace(trial,(face.parentLod,face.parentMesh),parent.sourceFaces[face.parentFace]);
        }

        static bool ProtectFace(Options trial,(int lod,int node) key,int source)
        {
            if (trial.seamCutExclusions==null) trial.seamCutExclusions=new Dictionary<(int,int),HashSet<int>>();
            if (!trial.seamCutExclusions.TryGetValue(key,out var excluded))
            { excluded=new HashSet<int>(); trial.seamCutExclusions.Add(key,excluded); }
            return excluded.Add(source);
        }
    }
}
