using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Prepare unpublished seed charts before projection. Refinement
    /// inside an established chain must preserve inherited regions and metric.</summary>
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
            using var workflowTiming = options.performance?.Measure("Reverse workflow total");
            if(options.regroupPreparedAtlas) return await BuildOne(sources,options,false,density,false,useAsync,token,false);
            if (!prepareSeed) return await BuildOne(sources,options,false,density,false,useAsync,token,false);
            // Seed charts have not yet been published. Choose their topology
            // before projecting; newly exposed regions are legitimate new charts.
            // The donor refinement trial still preserves inherited regions inside
            // each resulting chain, and publication still requires final audits.
            string refusal = null;
            try { return await BuildOne(sources,options,true,density,options.cutNarrowJunctions,useAsync,token,true); }
            catch (InvalidOperationException error) { refusal = error.Message; }
            PreparedResult fallback;
            try { fallback = await BuildOne(sources,options,true,density,false,useAsync,token,options.cutNarrowJunctions); }
            catch (InvalidOperationException)
            {
                try { fallback = await BuildOne(sources,options,true,density,false,useAsync,token,false); }
                catch (InvalidOperationException error)
                { throw new InvalidOperationException("Requested reverse seed failed: "+refusal+" Audited fallback failed: "+error.Message,error); }
            }
            fallback.refusal = fallback.result.report.seedPackingRefusal = refusal;
            UvtLog.Warn(UvtLog.Category.Repack,"[ReverseUV] Requested seed preparation failed; retained audited fallback: "+refusal);
            return fallback;
        }

        static async Task<PreparedResult> BuildOne(ReverseUvTransfer.Level[] sources,
            ReverseUvTransfer.Options options, bool prepareSeed, float density, bool cuts, bool useAsync,
            CancellationToken token, bool compact)
        {
            var state = new PreparedResult();
            try
            {
                if(UvProgress.IsActive) UvProgress.Report(.12f,compact?"Normalize seed and test compact packing":"Normalize and repair coarsest LOD");
                using (options.performance?.Measure("Input cleanup"))
                    state.inputs = ReverseUvInputs.Prepare(sources, token);
                int size;
                using (options.performance?.Measure("Seed preparation"))
                    size = prepareSeed && !options.regroupPreparedAtlas ? state.inputs.PrepareSeed(options.seedResolution, options.padding, density, token, cuts,
                        options.rotateCharts, options.rotateChartsToAxis, compact, options.rebuildSeedWithUnity)
                        : options.seedResolution;
                var trial = new ReverseUvTransfer.Options { seedResolution = size, padding = options.padding,
                    performance = options.performance,
                    cachePackingFootprints = options.cachePackingFootprints,
                    cacheNativeCharts = options.cacheNativeCharts,
                    indexFootprintSearch = options.indexFootprintSearch,
                    cacheCollisionRows = options.cacheCollisionRows,
                    nativeFootprintSearch = options.nativeFootprintSearch,
                    reuseOccupancySnapshots = options.reuseOccupancySnapshots,
                    burstFootprintRaster = options.burstFootprintRaster,
                    footprintSearchApi = options.footprintSearchApi,
                    queryBackend=options.queryBackend, queryApi=options.queryApi,
                    maxAtlasSize = options.maxAtlasSize, projectionReach = options.projectionReach, normalDot = options.normalDot,
                    maxAnisotropy = options.maxAnisotropy, preserveProjectedOverlap = options.preserveProjectedOverlap,
                    boundaryToleranceTexels = options.boundaryToleranceTexels, relativeStretchTolerance = options.relativeStretchTolerance,
                    comparisonBudget = options.comparisonBudget, cutNarrowJunctions = cuts,
                    regroupPreparedAtlas=options.regroupPreparedAtlas,
                    frozenShellCascade=options.frozenShellCascade, beforeFinalPacking=options.beforeFinalPacking,
                    splitDonorSeams = options.splitDonorSeams, fillAtlasVacancies = options.fillAtlasVacancies,
                    rotateCharts = options.rotateCharts, rotateChartsToAxis = options.rotateChartsToAxis,
                    repackFinalAtlas = options.repackFinalAtlas, fillFinalAtlas = options.fillFinalAtlas,
                    seedDensity=options.regroupPreparedAtlas?density:prepareSeed?state.inputs.seedDensity:options.seedDensity };
                if(UvProgress.IsActive) UvProgress.Report(.25f,cuts?"Project with junction cuts":"Validate packing across the LOD chain");
                state.result = await ReverseUvTransfer.Build(state.inputs.levels, trial, useAsync, token);
                state.result.report.seedPacking=prepareSeed?(compact?"compact":"normalized-original"):"existing";
                state.result.report.seedPreparation=options.frozenShellCascade && !options.preserveProjectedOverlap?"frozen-forward-last-lod"
                    :options.regroupPreparedAtlas?"ordinary-transfer-charts":prepareSeed?(options.rebuildSeedWithUnity?"unity-unwrapping":"source-charts-local-repair"):"existing-uv2";
                state.result.report.seedChartRotation=prepareSeed && compact && options.rotateCharts;
                using var auditTiming = options.performance?.Measure("Final layout validation");
                for(int level=0;level<state.result.meshes.Count;++level)
                    for(int node=0;node<state.result.meshes[level].Length;++node)
                    {
                        var mesh=state.result.meshes[level][node];
                        var quality=TransferUvQuality.Measure(mesh,mesh.uv2,Vector2.one,state.inputs.levels[level].inputs[node].toWorld);
                        if(!quality.overlapScanComplete || quality.invalidFaces>0 || quality.degenerateFaces>0
                            || quality.outOfBoundsVertices>0 || double.IsNaN(quality.worstAnisotropy))
                            throw new InvalidOperationException($"Reverse layout metric failed for LOD{state.inputs.levels[level].lod} '{state.inputs.levels[level].inputs[node].key}' (anisotropy {quality.worstAnisotropy:G6}, invalid {quality.invalidFaces}, collapsed {quality.degenerateFaces}, outside {quality.outOfBoundsVertices}).");
                        var vertices=mesh.vertices;var indices=mesh.triangles;var uv=mesh.uv2;
                        for(int f=0;f<indices.Length;f+=3)
                        {
                            var frame=TransferUvQuality.WorldFrame(vertices[indices[f]],vertices[indices[f+1]],vertices[indices[f+2]],
                                state.inputs.levels[level].inputs[node].toWorld);
                            if(!options.regroupPreparedAtlas && ReverseUvTransfer.TriangleAnisotropy(frame,uv[indices[f]],uv[indices[f+1]],uv[indices[f+2]])>4.001
                                || options.regroupPreparedAtlas && !ReverseUvShellPrecision.Fits(frame,uv[indices[f]]*state.result.report.atlasSize,
                                    uv[indices[f+1]]*state.result.report.atlasSize,uv[indices[f+2]]*state.result.report.atlasSize,4.001))
                                throw new InvalidOperationException($"Reverse shell metric failed for LOD{state.inputs.levels[level].lod} '{state.inputs.levels[level].inputs[node].key}' face {f/3} "
                                    + $"(anisotropy {ReverseUvTransfer.TriangleAnisotropy(frame,uv[indices[f]],uv[indices[f+1]],uv[indices[f+2]]):G9}; atlas {state.result.report.atlasSize}; "
                                    + $"frame {frame.length:R}/{frame.x:R}/{frame.y:R}; pixels "
                                    + string.Join("; ",Enumerable.Range(f,3).Select(c=>{var p=uv[indices[c]]*state.result.report.atlasSize;return $"{p.x:R}/{p.y:R}";}))+").");
                        }

                    }
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
            var meshes=baseline.meshes.SelectMany(m=>m).ToArray();
            // Refinement may spend a small part of the density gained by final
            // atlas filling. The requested seed density is still audited.
            if (after.texelsPerUnit < before.texelsPerUnit * (1-Math.Max(.0001f,after.relativeStretchTolerance)))
                return $"cuts reduce texel density ({after.texelsPerUnit:G6} versus {before.texelsPerUnit:G6})";
            if (before.nodes.Count != after.nodes.Count) return "cuts change the input node count";
            for (int n = 0; n < before.nodes.Count; ++n)
            {
                var a = before.nodes[n]; var b = after.nodes[n];
                var coverage=new CoverageContext(a,meshes.ElementAtOrDefault(n),before.atlasSize,after.boundaryToleranceTexels);
                var descendants = Enumerable.Range(0, b.faces.Length).GroupBy(f => b.sourceFaces == null ? f : b.sourceFaces[f])
                    .ToDictionary(g => g.Key, g => g.ToArray());
                for (int f = 0; f < a.faces.Length; ++f)
                {
                    token.ThrowIfCancellationRequested();
                    int source = a.sourceFaces == null ? f : a.sourceFaces[f];
                    if (!descendants.TryGetValue(source, out var pieces)) return "cuts lose a source face";
                    if (a.faces[f].inherited && !coverage.Covers(f, b, pieces.Where(p => b.faces[p].inherited), token))
                        return $"LOD{a.lod} '{a.key}' face {f} loses inheritance across a new seam";
                    if (a.faces[f].intentionalOverlap && !coverage.Covers(f, b, pieces.Where(p => b.faces[p].intentionalOverlap), token))
                        return $"LOD{a.lod} '{a.key}' face {f} loses its projected overlap";
                }
            }
            using var failures = DistortionFailures(baseline,candidate,levels,token).GetEnumerator();
            if (failures.MoveNext())
                return $"LOD{after.nodes[failures.Current.node].lod} face {failures.Current.face} increases distortion";
            return null;
        }

        internal static IEnumerable<(int node,int face,int source)> DistortionFailures(ReverseUvTransfer.Result baseline,
            ReverseUvTransfer.Result candidate,ReverseUvTransfer.Level[] levels,CancellationToken token)
        {
            if (baseline.meshes.Count == 0) yield break;
            var before=baseline.report; var after=candidate.report;
            int reportNode = 0;
            for (int l = 0; l < baseline.meshes.Count; ++l)
                for (int n = 0; n < baseline.meshes[l].Length; ++n)
                {
                    var a = baseline.meshes[l][n]; var b = candidate.meshes[l][n];
                    var oldNode = before.nodes[reportNode]; var newNode = after.nodes[reportNode++];
                    var matrix = levels[l].inputs[n].toWorld;
                    var positions = a.vertices;
                    var indices = a.triangles; var otherIndices = b.triangles; var uv = a.uv2; var otherUv = b.uv2;
                    var oldStretchBySource = new Dictionary<int, double>();
                    for (int t = 0; t < indices.Length; t += 3)
                    {
                        token.ThrowIfCancellationRequested();
                        var p = positions[indices[t]]; var q = positions[indices[t + 1]]; var r = positions[indices[t + 2]];
                        double oldStretch = ReverseUvTransfer.TriangleAnisotropy(p, q, r, uv[indices[t]], uv[indices[t + 1]], uv[indices[t + 2]],matrix);
                        if (!oldNode.seed && !oldNode.faces[t / 3].inherited) continue;
                        int source = oldNode.sourceFaces[t / 3];
                        oldStretchBySource.TryGetValue(source, out double previous);
                        oldStretchBySource[source] = Math.Max(previous, oldStretch);
                    }
                    var otherPositions = b.vertices;
                    for (int t = 0; t < otherIndices.Length; t += 3)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!oldStretchBySource.TryGetValue(newNode.sourceFaces[t / 3], out double oldStretch)) continue;
                        double newStretch = ReverseUvTransfer.TriangleAnisotropy(otherPositions[otherIndices[t]], otherPositions[otherIndices[t + 1]],
                            otherPositions[otherIndices[t + 2]], otherUv[otherIndices[t]], otherUv[otherIndices[t + 1]], otherUv[otherIndices[t + 2]],matrix);
                        if (!UvTransferTolerance.MetricFits(newStretch,oldStretch,after.relativeStretchTolerance))
                            yield return (reportNode-1,t/3,newNode.sourceFaces[t/3]);
                    }
                }
        }

        static Vector2[] Region(ReverseUvTransfer.NodeReport node, int face)
        {
            if (node.sourceBarycentrics == null) return new[] { Vector2.zero, Vector2.right, Vector2.up };
            return Enumerable.Range(face * 3, 3).Select(i => new Vector2(node.sourceBarycentrics[i].y, node.sourceBarycentrics[i].z)).ToArray();
        }

        internal sealed class CoverageContext
        {
            readonly ReverseUvTransfer.NodeReport before;
            readonly Vector2[] pixels;
            readonly int[] indices;
            readonly float tolerance;
            long work;

            internal CoverageContext(ReverseUvTransfer.NodeReport before,Mesh mesh,int side,float tolerance)
            {
                this.before=before;this.tolerance=tolerance;
                if(mesh) {pixels=mesh.uv2.Select(p=>p*side).ToArray();indices=mesh.triangles;}
            }

            internal bool Covers(int face,ReverseUvTransfer.NodeReport after,IEnumerable<int> pieces,CancellationToken token)
            {
                var selected=pieces.ToArray();
                if(Covered(before,face,after,selected,token)) return true;
                if(tolerance<=0 || selected.Length==0 || pixels==null) return false;
                var region=Region(before,face);int start=face*3;
                var triangle=new[] {pixels[indices[start]],pixels[indices[start+1]],pixels[indices[start+2]]};
                double det=Cross(region[0],region[1],region[2]);
                if(det==0) return false;
                Vector2 Map(Vector2 p)
                {
                    double x=Cross(region[0],p,region[2])/det,y=Cross(region[0],region[1],p)/det;
                    return new Vector2((float)(triangle[0].x+x*(triangle[1].x-triangle[0].x)+y*(triangle[2].x-triangle[0].x)),
                        (float)(triangle[0].y+x*(triangle[1].y-triangle[0].y)+y*(triangle[2].y-triangle[0].y)));
                }
                var donors=selected.Select(f=>Region(after,f).Select(Map).ToArray());
                return UvTransferTolerance.Covers(triangle,donors,tolerance,ref work,2000000,token);
            }
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
