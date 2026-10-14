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
            using var buildTiming = options?.performance?.Measure("Reverse total");
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
            options.newChartParameterizations=new Dictionary<Input,UnityEngine.Vector2[]>();
            if (!options.splitDonorSeams || options.regroupPreparedAtlas) return await BuildCore(levels, options, useAsync, token);
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
            trial.inheritedPriority=new HashSet<(int,int,int)>();
            for(int n=0;n<baseline.report.nodes.Count;++n)
            {
                var node=baseline.report.nodes[n];int mesh=baseline.report.nodes.Take(n).Count(p=>p.lod==node.lod);
                for(int f=0;f<node.faces.Length;++f)
                    if(node.faces[f].inherited) trial.inheritedPriority.Add((node.lod,mesh,node.sourceFaces[f]));
            }
            if(options.preserveProjectedOverlap)
            {
                trial.overlapPriority=new Dictionary<(int,int,int),int>();
                for(int n=0;n<baseline.report.nodes.Count;++n)
                {
                    var node=baseline.report.nodes[n];
                    int mesh=baseline.report.nodes.Take(n).Count(p=>p.lod==node.lod);
                    for(int f=0;f<node.faces.Length;++f)
                    {
                        var key=(node.lod,mesh,node.sourceFaces[f]);
                        int layer=node.faces[f].layer;
                        if(!trial.overlapPriority.TryGetValue(key,out int previous) || layer<previous) trial.overlapPriority[key]=layer;
                    }
                }
            }
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
                            extended |= ProtectParent(baseline.report,node.faces[f],trial,token);
                    }
                    if(!extended) extended=ProtectLostMeshes(baseline,candidate,trial,token);
                    candidate.Dispose(); candidate = null;
                    UvtLog.Info(UvtLog.Category.Repack,$"[ReverseUV] Local donor retry {attempt+1}: {refusal}; "
                        +$"protected {trial.seamCutExclusions.Sum(pair=>pair.Value.Count)} source faces; progress {extended}.");
                    if (!extended) break;
                }
                return (null,refusal);
            }
            finally {candidate?.Dispose();}
        }

        static Options TrialOptions(Options options, bool split) => new Options {
            performance = options.performance,
            cachePackingFootprints = options.cachePackingFootprints,
            indexFootprintSearch = options.indexFootprintSearch,
            cacheCollisionRows = options.cacheCollisionRows,
            nativeFootprintSearch = options.nativeFootprintSearch,
            reuseOccupancySnapshots = options.reuseOccupancySnapshots,
            burstFootprintRaster = options.burstFootprintRaster,
            footprintSearchApi = options.footprintSearchApi,
            queryBackend=options.queryBackend, queryApi=options.queryApi,
            seedResolution = options.seedResolution, padding = options.padding, maxAtlasSize = options.maxAtlasSize,
            projectionReach = options.projectionReach, normalDot = options.normalDot, maxAnisotropy = options.maxAnisotropy,
            boundaryToleranceTexels = options.boundaryToleranceTexels, relativeStretchTolerance = options.relativeStretchTolerance,
            preserveProjectedOverlap = options.preserveProjectedOverlap, cutNarrowJunctions = split && options.cutNarrowJunctions,
            fillAtlasVacancies = options.fillAtlasVacancies, splitDonorSeams = split, comparisonBudget = options.comparisonBudget,
            repackFinalAtlas = options.repackFinalAtlas, fillFinalAtlas = options.fillFinalAtlas,
                rotateCharts = options.rotateCharts, rotateChartsToAxis = options.rotateChartsToAxis,seedDensity=options.seedDensity,
            newChartParameterizations=options.newChartParameterizations,
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
                    UvtLog.Verbose(UvtLog.Category.Repack,$"[ReverseUV] Coverage loss LOD{a.lod} '{a.key}' source {a.sourceFaces[f]}: "
                        +string.Join(", ",descendants.Select(p=>$"{p}:{b.faces[p].rejection??"mapped"}/cut {b.faces[p].cutRefusal??"none"}")));
                    int mesh=before.nodes.Take(n).Count(p=>p.lod==a.lod);
                    // Overlap arbitration and boundary propagation affect the
                    // connected recipient patch, not only its first lost face.
                    // Preserve that patch in one retry instead of chasing its
                    // neighbouring triangles in successive whole-chain builds.
                    for(int recipient=0;recipient<a.faces.Length;++recipient)
                        if(a.faces[recipient].chart==face.chart && a.faces[recipient].layer==face.layer)
                            extended |= ProtectFace(trial,(a.lod,mesh),a.sourceFaces[recipient]);
                    extended |= ProtectParent(before,face,trial,token);
                }
            }
            return extended;
        }

        static bool ProtectParent(Report before,Face face,Options trial,CancellationToken token=default,HashSet<(int lod,int mesh,int chart,int layer)> visited=null)
        {
            if (!face.inherited) return false;
            var parent=before.nodes.Where(p=>p.lod==face.parentLod).ElementAt(face.parentMesh);
            if(parent.seed) return false;
            var donor=parent.faces[face.parentFace];
            if(visited==null) visited=new HashSet<(int,int,int,int)>();
            if(!visited.Add((parent.lod,face.parentMesh,donor.chart,donor.layer))) return false;
            bool extended=false;
            // parentFace records the centre hit, not every donor triangle used
            // by the child's footprint. Protect the complete connected donor
            // domain and its ancestors; retaining only the centre triangle let
            // its neighbours be recut/reparameterized on the next retry.
            for(int f=0;f<parent.faces.Length;++f)
            {
                token.ThrowIfCancellationRequested();
                if(parent.faces[f].chart==donor.chart && parent.faces[f].layer==donor.layer)
                {
                    extended|=ProtectFace(trial,(parent.lod,face.parentMesh),parent.sourceFaces[f]);
                    extended|=ProtectParent(before,parent.faces[f],trial,token,visited);
                }
            }
            return extended;
        }

        static bool ProtectFace(Options trial,(int lod,int node) key,int source)
        {
            if (trial.seamCutExclusions==null) trial.seamCutExclusions=new Dictionary<(int,int),HashSet<int>>();
            if (!trial.seamCutExclusions.TryGetValue(key,out var excluded))
            { excluded=new HashSet<int>(); trial.seamCutExclusions.Add(key,excluded); }
            return excluded.Add(source);
        }

        // If a connected-patch retry is unchanged, another patch of the same
        // input can still change arbitration or the donor's parameterization.
        // Retain cuts locally for that input rather than discard the complete
        // refined chain, including unrelated renderers and finer LODs.
        static bool ProtectLostMeshes(Result baseline,Result candidate,Options trial,CancellationToken token)
        {
            bool extended=false;var before=baseline.report;var after=candidate.report;
            var meshes=baseline.meshes.SelectMany(m=>m).ToArray();
            for(int n=0;n<before.nodes.Count;++n)
            {
                var node=before.nodes[n];if(node.seed) continue;
                var other=after.nodes[n];int mesh=before.nodes.Take(n).Count(p=>p.lod==node.lod);
                var coverage=new ReverseUvJunctionTrial.CoverageContext(node,meshes[n],before.atlasSize,trial.boundaryToleranceTexels);
                var pieces=Enumerable.Range(0,other.faces.Length).GroupBy(f=>other.sourceFaces[f]).ToDictionary(g=>g.Key,g=>g.ToArray());
                for(int f=0;f<node.faces.Length;++f)
                {
                    token.ThrowIfCancellationRequested();var face=node.faces[f];
                    if(!pieces.TryGetValue(node.sourceFaces[f],out var descendants)) continue;
                    if((!face.inherited || coverage.Covers(f,other,descendants.Where(p=>other.faces[p].inherited),token))
                        && (!face.intentionalOverlap || coverage.Covers(f,other,descendants.Where(p=>other.faces[p].intentionalOverlap),token))) continue;
                    foreach(int source in node.sourceFaces) extended|=ProtectFace(trial,(node.lod,mesh),source);
                    if(face.inherited)
                    {
                        var parent=before.nodes.Where(p=>p.lod==face.parentLod).ElementAt(face.parentMesh);
                        if(!parent.seed) foreach(int source in parent.sourceFaces) extended|=ProtectFace(trial,(parent.lod,face.parentMesh),source);
                    }
                    break;
                }
            }
            return extended;
        }
    }
}
