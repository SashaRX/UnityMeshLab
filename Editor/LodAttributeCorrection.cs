using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    // Fits the interpolated fields over the retained surface, rather than copying
    // attributes at vertices which meshoptimizer already retained from LOD0.
    internal static class LodAttributeCorrection
    {
        [Serializable] internal sealed class Report
        {
            public bool normalsAccepted, colorsAccepted;
            public float normalBlend, colorBlend, normalRmsBefore, normalRmsAfter, normalMaxBefore, normalMaxAfter;
            public Vector4 colorRmsBefore, colorRmsAfter, colorMaxBefore, colorMaxAfter;
            public int samples, trials;
            public int smoothingRegions, linkedNormalDuplicates, mixedSmoothingFaces, missingSmoothingRegions, incompleteSmoothingRegions;
            public LodSmoothingRegions.Error[] regionNormalsBefore, regionNormalsAfter;
            public string colorMethod;
            public bool screenColorVerification;
            public int visibleColorSamples;
            public float visibleRgbaBefore, visibleRgbaAfter;
            public Vector4 visibleChannelsBefore, visibleChannelsAfter;
            public VisibleNormalReport visibleNormals;
            public VisibleColorBoundaryReport visibleColorBoundaries;
            public VisibleColorBoundaryReport polygonColorBoundaries;
            public LodPaintRetriangulation.Report paintRetriangulation;
            public LodSharedUvTransfer.Report sharedLightmapUv;
            public string note;
        }

        [Serializable] internal sealed class VisibleColorBoundaryReport
        {
            public bool accepted;
            public int samples,trials,verticesBefore,verticesAfter;
            public int polygonGroups;
            public int activePolygonGroups;
            public float viewColorRmsTolerance;
            public float boundaryBefore,boundaryAfter,worstBefore,worstAfter,rgbaBefore,rgbaAfter;
            public float[] viewsBefore,viewsAfter;
            public CornerSharingReport cornerSharing;
        }

        [Serializable] internal sealed class CornerSharingReport
        {
            public int verticesBefore,verticesAfter,trials;
            public float colorTolerance,rgbaBefore,rgbaAfter,boundaryBefore,boundaryAfter;
        }

        internal static Mesh ShareVisibleColorCorners(Mesh fitted,LodScreenValidation screen,
            out CornerSharingReport report,Func<bool> cancelled = null)
        {
            report = new CornerSharingReport { verticesBefore=fitted.vertexCount,verticesAfter=fitted.vertexCount };
            if (screen == null || fitted.blendShapeCount > 0 || fitted.HasVertexAttribute(VertexAttribute.BlendWeight)) return null;
            var before = screen.Measure(fitted,cancelled);
            report.rgbaBefore = report.rgbaAfter = before.rgbaRms;
            report.boundaryBefore = report.boundaryAfter = before.colorBoundaryMean;
            var data = new LodMeshData(fitted); Mesh accepted = null;
            try
            {
                // Exact RGBA sharing retains the fitted field in every camera.
                // Approximate sharing passed the CPU guide but slightly worsened
                // real bench GPU captures, so it is not enabled for generation.
                var tolerances = new[] { 0f };
                foreach (float tolerance in tolerances)
                {
                    CheckCancellation(cancelled);
                    Mesh candidate = data.ShareRenderCorners(tolerance,cancelled);
                    try
                    {
                        if (!candidate || candidate.vertexCount >= report.verticesAfter) continue;
                        UvProgress.Report(UvProgress.Current.fraction,$"{fitted.name}: verify shared far color corners");
                        var measured = screen.Measure(candidate,cancelled); report.trials++;
                        if (!measured.DoesNotWorsen(before,true)) continue;
                        if (accepted) UnityEngine.Object.DestroyImmediate(accepted);
                        accepted = candidate; candidate = null;
                        report.verticesAfter = accepted.vertexCount; report.colorTolerance = tolerance;
                        report.rgbaAfter = measured.rgbaRms; report.boundaryAfter = measured.colorBoundaryMean;
                    }
                    finally { if (candidate) UnityEngine.Object.DestroyImmediate(candidate); }
                }
                var result = accepted; accepted = null; return result;
            }
            finally { if (accepted) UnityEngine.Object.DestroyImmediate(accepted); }
        }

        // Color seams need independent render corners, not more triangles.
        // Keep all geometry/other fields and require every measured paint view
        // and RGBA channel to retain or improve its source correspondence.
        internal static Mesh CorrectVisibleColorBoundaries(Mesh source,Mesh reduced,LodScreenValidation screen,
            out VisibleColorBoundaryReport report,Func<bool> cancelled = null)
        {
            report = new VisibleColorBoundaryReport { verticesBefore=reduced.vertexCount,verticesAfter=reduced.vertexCount };
            var sourceColors = source.colors; var reducedColors = reduced.colors;
            if (screen == null || source.blendShapeCount > 0 || reduced.blendShapeCount > 0 ||
                source.HasVertexAttribute(VertexAttribute.BlendWeight) || reduced.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                sourceColors.Length != source.vertexCount || reducedColors.Length != reduced.vertexCount ||
                !sourceColors.Any(c => !c.Equals(sourceColors[0]))) return null;
            var before = screen.Measure(reduced,cancelled);
            if (!before.rgbaEvaluated || before.colorViews == 0 || before.colorBoundaryMean <= 0) return null;
            report.boundaryBefore = report.boundaryAfter = before.colorBoundaryMean;
            report.worstBefore = report.worstAfter = before.colorLoss; report.rgbaBefore = report.rgbaAfter = before.rgbaRms;
            report.viewsBefore = report.viewsAfter = before.colorBoundaryViewLoss;
            Mesh trial = null;
            try
            {
                trial = new LodMeshData(reduced).ExpandTriangleCorners();
                var original = trial.colors; var accepted = original; var best = before;
                foreach (float weight in new[] { 1f,8f })
                {
                    var fit = new FieldFit(original.Select(c => (Vector4)c).ToArray());
                    report.samples = screen.AddVisibleBoundaryColorSamples(trial,fit.Add,weight,cancelled);
                    if (report.samples == 0) continue;
                    var values = fit.Solve(.002f,cancelled);
                    foreach (float blend in new[] { 1f,.5f,.25f })
                    {
                        CheckCancellation(cancelled);
                        SetColors(trial,original.Select((c,i) => (Color)Vector4.Lerp(c,values[i],blend)).ToArray(),reduced.GetVertexAttributeFormat(VertexAttribute.Color));
                        UvProgress.Report(UvProgress.Current.fraction,$"{source.name}: verify far color boundary corners");
                        CheckCancellation(cancelled);
                        var local = screen.Measure(trial,cancelled); report.trials++;
                        if (!local.DoesNotWorsen(best,true) || local.colorBoundaryMean >= best.colorBoundaryMean-1e-6f) continue;
                        accepted = trial.colors; best = local; report.accepted = true;
                    }
                }
                if (!report.accepted) return null;
                SetColors(trial,accepted,reduced.GetVertexAttributeFormat(VertexAttribute.Color));
                var shared = ShareVisibleColorCorners(trial,screen,out report.cornerSharing,cancelled);
                if (shared)
                {
                    UnityEngine.Object.DestroyImmediate(trial); trial = shared;
                    best = screen.Measure(trial,cancelled);
                }
                report.boundaryAfter = best.colorBoundaryMean; report.worstAfter = best.colorLoss;
                report.rgbaAfter = best.rgbaRms; report.viewsAfter = best.colorBoundaryViewLoss; report.verticesAfter = trial.vertexCount;
                var result = trial; trial = null; return result;
            }
            finally { if (trial) UnityEngine.Object.DestroyImmediate(trial); }
        }

        // Replace stretched gradients on faces covering lost paint transitions.
        // Existing shared corners stay one color variable, so flat replacements
        // cannot expand the render buffer or change other authored fields.
        internal static Mesh PaintFaceBoundaries(Mesh source,Mesh reduced,LodScreenValidation screen,
            out VisibleColorBoundaryReport report,Func<bool> cancelled = null)
        {
            report = new VisibleColorBoundaryReport { verticesBefore=reduced.vertexCount,verticesAfter=reduced.vertexCount };
            if (screen == null || source.vertexCount == 0 || reduced.vertexCount == 0 || source.colors.Length != source.vertexCount || reduced.colors.Length != reduced.vertexCount ||
                source.subMeshCount != 1 || reduced.subMeshCount != 1 ||
                source.blendShapeCount > 0 || reduced.blendShapeCount > 0 || source.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                reduced.HasVertexAttribute(VertexAttribute.BlendWeight) || LodMeshData.TriangleCount(reduced) > 128) return null;
            var before = screen.Measure(reduced,cancelled); var best = before;
            report.boundaryBefore = report.boundaryAfter = before.colorBoundaryMean;
            report.worstBefore = report.worstAfter = before.colorLoss; report.rgbaBefore = report.rgbaAfter = before.rgbaRms;
            report.viewsBefore = report.viewsAfter = before.colorBoundaryViewLoss;
            if (!before.rgbaEvaluated || before.objectPixels > 128 || before.colorViews == 0 || before.colorBoundaryMean <= 0) return null;
            var verification = screen.WithAllSides(source,cancelled); var allSidesBefore = verification.Measure(reduced,cancelled);
            report.viewColorRmsTolerance = 1f/255;
            Mesh trial = null, accepted = null;
            try
            {
                trial = UnityEngine.Object.Instantiate(reduced);
                var indices = reduced.triangles; int faces = indices.Length/3;
                var owners = Enumerable.Range(0,faces).ToArray(); var first = Enumerable.Repeat(-1,reduced.vertexCount).ToArray();
                int Owner(int face) { while (owners[face] != face) face = owners[face]; return face; }
                for (int f = 0; f < faces; f++) for (int k = 0; k < 3; k++)
                {
                    int v = indices[f*3+k]; if (first[v] < 0) first[v] = f;
                    else owners[Owner(f)] = Owner(first[v]);
                }
                for (int f = 0; f < faces; f++) owners[f] = Owner(f);
                report.polygonGroups = owners.Distinct().Count();
                var active = screen.LostPaintFaces(reduced,cancelled).Select(f => owners[f]).Distinct().OrderBy(f => f).ToArray();
                report.activePolygonGroups = active.Length;
                var original = reduced.colors;
                foreach (float weight in new[] { 1f,8f,32f })
                {
                    var sums = new Vector4[faces]; var masses = new double[faces];
                    var clusters = Enumerable.Range(0,faces).Select(_ => new List<(Vector4 sum,double mass)>()).ToArray();
                    report.samples = screen.AddVisibleBoundaryColorSamples(trial,(indices,offset,bary,value,mass) => {
                        int face = owners[offset/3]; sums[face] += value*(float)mass; masses[face] += mass;
                        var list = clusters[face]; int found = -1;
                        for (int i = 0; i < list.Count; i++)
                        {
                            var delta = value-list[i].sum/(float)list[i].mass;
                            if (Mathf.Max(Mathf.Abs(delta.x),Mathf.Max(Mathf.Abs(delta.y),Mathf.Max(Mathf.Abs(delta.z),Mathf.Abs(delta.w)))) < .15f) { found = i; break; }
                        }
                        if (found < 0) list.Add((value*(float)mass,mass));
                        else list[found] = (list[found].sum+value*(float)mass,list[found].mass+mass);
                    },weight,cancelled);
                    foreach (bool dominant in new[] { false,true }) foreach (int ownerToPaint in active)
                    {
                        UvProgress.Report(UvProgress.Current.fraction,$"{source.name}: verify polygon paint boundary");
                        CheckCancellation(cancelled);
                        var values = accepted ? accepted.colors : (Color[])original.Clone();
                        for (int f = 0; f < faces; f++)
                        {
                            int owner = owners[f]; if (owner != ownerToPaint || masses[owner] == 0) continue;
                            var value = sums[owner]/(float)masses[owner];
                            if (dominant) { var cluster = clusters[owner].OrderByDescending(c => c.mass).First(); value = cluster.sum/(float)cluster.mass; }
                            for (int k = 0; k < 3; k++) values[indices[f*3+k]] = value;
                        }
                        SetColors(trial,values,reduced.GetVertexAttributeFormat(VertexAttribute.Color));
                        var local = screen.Measure(trial,cancelled); report.trials++;
                        if (!local.DoesNotWorsen(before) || !local.PaintViewsDoNotWorsen(before) || local.rgbaRms > before.rgbaRms+1e-6f ||
                            local.colorBoundaryMean >= best.colorBoundaryMean-1e-6f) continue;
                        if (!verification.Measure(trial,cancelled).ColorViewsWithin(allSidesBefore,report.viewColorRmsTolerance)) continue;
                        var shared = UnityEngine.Object.Instantiate(trial);
                        if (accepted) UnityEngine.Object.DestroyImmediate(accepted);
                        accepted = shared; best = local; report.accepted = true;
                    }
                }
                if (!accepted) return null;
                report.verticesAfter = accepted.vertexCount; report.boundaryAfter = best.colorBoundaryMean;
                report.worstAfter = best.colorLoss; report.rgbaAfter = best.rgbaRms; report.viewsAfter = best.colorBoundaryViewLoss;
                var result = accepted; accepted = null; return result;
            }
            finally { if (trial) UnityEngine.Object.DestroyImmediate(trial); if (accepted) UnityEngine.Object.DestroyImmediate(accepted); }
        }

        // A continuous variable per current UV2 wedge keeps atlas seams separate
        // while fitting the original chart's visible field after retriangulation.
        internal static void FitVisibleUv2(Mesh source,Mesh target,LodScreenValidation screen,Func<bool> cancelled = null)
        {
            if (!source.HasVertexAttribute(VertexAttribute.TexCoord1) || !target.HasVertexAttribute(VertexAttribute.TexCoord1) ||
                source.GetVertexAttributeDimension(VertexAttribute.TexCoord1)!=2 || target.GetVertexAttributeDimension(VertexAttribute.TexCoord1)!=2) return;
            var original=new LodMeshData(source); var data=new LodMeshData(target);
            var sourceIndices=source.triangles; var targetIndices=target.triangles;
            var representatives=new Dictionary<(Vector3,Vector4),int>();
            var owners=Enumerable.Range(0,target.vertexCount).Select(v => {
                var key=(data.positions[v],data.uvs[1][v]);
                if (!representatives.TryGetValue(key,out int owner)) representatives[key]=owner=v; return owner;
            }).ToArray();
            var fit=new FieldFit(data.uvs[1].ToArray(),owners);
            int mismatches=0;
            screen.AddVisibleSurfaceSamples(target,(sf,sb,tf,tb,sn,tn,view,pixel) => {
                var desired=original.uvs[1][sourceIndices[sf*3]]*sb.x+original.uvs[1][sourceIndices[sf*3+1]]*sb.y+original.uvs[1][sourceIndices[sf*3+2]]*sb.z;
                var current=data.uvs[1][targetIndices[tf*3]]*tb.x+data.uvs[1][targetIndices[tf*3+1]]*tb.y+data.uvs[1][targetIndices[tf*3+2]]*tb.z;
                // Opposite sheets / atlas islands cannot donate a distant UV.
                float difference=(current-desired).sqrMagnitude;
                if (difference<=.05f*.05f) { fit.Add(targetIndices,tf*3,tb,desired,.25); if (difference>1e-12f) mismatches++; }
            },cancelled);
            if (mismatches==0) return;
            var values=fit.Solve(.02f,cancelled).Select(v => (Vector2)v).ToArray();
            LodSharedUvTransfer.ProjectOriginalCharts(source,target,values,cancelled);
            target.SetUVs(1,values.ToList());
        }

        [Serializable] internal sealed class VisibleNormalReport
        {
            public bool accepted;
            public int samples, trials;
            public string method;
            public float blend, normalBefore, normalAfter, headlightBefore, headlightAfter;
            public float[] normalViewsBefore, normalViewsAfter, headlightViewsBefore, headlightViewsAfter;
            public LodSmoothingRegions.Error[] regionsBefore, regionsAfter;
        }

        // Final attribute-only pass: it cannot change the selected triangle budget
        // or silhouette. Far faces spanning several source regions can fit visible
        // normals independently at retained render splits instead of staying stale.
        internal static Mesh CorrectVisibleNormals(Mesh source,Mesh reduced,MeshSimplifier.SimplifySettings settings,
            LodScreenValidation screen,out VisibleNormalReport report,out LodSurfaceValidation.Metrics metrics,
            Func<bool> cancelled = null,LodSurfaceValidation.Metrics? baseline = null)
        {
            metrics = baseline ?? LodSurfaceValidation.MeasureMeshes(source,reduced,settings,cancelled,ignoreDegenerateFaces:true);
            report = new VisibleNormalReport();
            if (screen == null || source.blendShapeCount > 0 || source.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                reduced.blendShapeCount > 0 || reduced.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                source.normals.Length != source.vertexCount || reduced.normals.Length != reduced.vertexCount) return null;
            var before = screen.Measure(reduced,cancelled);
            if (!before.normalsEvaluated || before.objectPixels > 128 || LodMeshData.TriangleCount(reduced) > 512 || before.normalRms < 75) return null;
            report.normalBefore = report.normalAfter = before.normalRms; report.headlightBefore = report.headlightAfter = before.headlightRms;
            report.normalViewsBefore = report.normalViewsAfter = before.normalViewRms;
            report.headlightViewsBefore = report.headlightViewsAfter = before.headlightViewRms;
            var original = new LodMeshData(source); var target = new LodMeshData(reduced);
            var smoothing = new LodSmoothingRegions(original,target,cancelled);
            report.regionsBefore = report.regionsAfter = smoothing.Measure(target.normals,cancelled);
            var fit = new FieldFit(target.normals.Select(n => (Vector4)n.normalized).ToArray(),smoothing.owners);
            var slots = new HashSet<int>[target.positions.Length];
            for (int i = 0; i < slots.Length; i++) slots[i] = new HashSet<int>();
            for (int s = 0; s < reduced.subMeshCount; s++) foreach (int vertex in LodMeshData.Triangles(reduced,s)) slots[vertex].Add(s);
            for (int i = 0; i < slots.Length; i++) if (slots[i].Count > 1) fit.pinned[i] = true;
            report.samples = screen.AddVisibleNormalSamples(reduced,fit.Add,cancelled);
            if (report.samples == 0) return null;
            var normalReport = report; var bestScreen = before; var bestMetrics = metrics;
            Vector3[] accepted = target.normals;
            Mesh trial = null;
            try
            {
                trial = UnityEngine.Object.Instantiate(reduced); trial.name = reduced.name;
                foreach (float prior in new[] { .1f,.02f,.002f }) Verify(fit.Solve(prior,cancelled),"visible normal least-squares "+prior);
                Verify(fit.Resample(cancelled),"visible normal mass-average");
                metrics = bestMetrics;
                if (!report.accepted) return null;
                report.regionsAfter = smoothing.Measure(accepted,cancelled);
                trial.normals = accepted;
                if (target.tangents.Length == target.positions.Length) trial.tangents = OrthogonalTangents(target.tangents,target.normals,accepted);
                if (MeshUvState.IsDraft(reduced)) MeshUvState.SetDraft(trial,true);
                var result = trial; trial = null; return result;
                void Verify(Vector4[] fitted,string method)
                {
                    foreach (float blend in new[] { 1f,.5f,.25f })
                    {
                        CheckCancellation(cancelled); var values = new Vector3[target.positions.Length];
                        for (int i = 0; i < values.Length; i++)
                        {
                            var n = ((Vector3)fitted[i]).normalized;
                            values[i] = fit.pinned[i] || n.sqrMagnitude < .5f ? target.normals[i] : Vector3.Lerp(target.normals[i].normalized,n,blend).normalized;
                            if (values[i].sqrMagnitude < .5f) values[i] = target.normals[i];
                        }
                        if (values.SequenceEqual(accepted)) continue;
                        trial.normals = values;
                        UvProgress.Report(UvProgress.Current.fraction,$"{source.name}: verify far visible normals {method} {blend:P0}");
                        var local = screen.Measure(trial,cancelled); normalReport.trials++;
                        if (!local.DoesNotWorsen(bestScreen,true) || local.normalRms >= bestScreen.normalRms-1e-4f ||
                            local.headlightRms >= bestScreen.headlightRms-1e-6f || !ViewsDoNotWorsen(local.normalViewRms,bestScreen.normalViewRms,1e-4f) ||
                            !ViewsDoNotWorsen(local.headlightViewRms,bestScreen.headlightViewRms,1e-6f)) continue;
                        var measured = LodSurfaceValidation.MeasureMeshes(source,trial,settings,cancelled,ignoreDegenerateFaces:true);
                        if (!Finite(measured) || measured.NormalRms > bestMetrics.NormalRms+1e-4f || measured.authoredNormalAngle > bestMetrics.authoredNormalAngle+1e-4f) continue;
                        bestMetrics = measured; bestScreen = local; accepted = values; normalReport.accepted = true;
                        normalReport.method = method; normalReport.blend = blend; normalReport.normalAfter = local.normalRms; normalReport.headlightAfter = local.headlightRms;
                        normalReport.normalViewsAfter = local.normalViewRms; normalReport.headlightViewsAfter = local.headlightViewRms;
                    }
                }
            }
            finally { if (trial) UnityEngine.Object.DestroyImmediate(trial); }
        }
        static bool ViewsDoNotWorsen(float[] after,float[] before,float tolerance)
            => after.Length == before.Length && after.Zip(before,(a,b) => !float.IsNaN(a) && !float.IsInfinity(a) && a <= b+tolerance).All(ok => ok);

        // Returns an owned replacement only on improvement. Inputs are never edited.
        internal static Mesh Correct(Mesh source, Mesh reduced, MeshSimplifier.SimplifySettings settings,
            LodPipelineOps.Options options, out Report report, out LodSurfaceValidation.Metrics metrics,
            Func<bool> cancelled = null, LodSurfaceValidation.Metrics? baseline = null,LodScreenValidation screen = null)
        {
            metrics = baseline ?? LodSurfaceValidation.MeasureMeshes(source,reduced,settings,cancelled,ignoreDegenerateFaces:true);
            report = new Report { normalRmsBefore = metrics.NormalRms, normalRmsAfter = metrics.NormalRms,
                normalMaxBefore = metrics.authoredNormalAngle, normalMaxAfter = metrics.authoredNormalAngle,
                colorRmsBefore = metrics.ColorRms, colorRmsAfter = metrics.ColorRms,
                colorMaxBefore = metrics.colorMax, colorMaxAfter = metrics.colorMax };
            if (source.blendShapeCount > 0 || source.HasVertexAttribute(VertexAttribute.BlendWeight))
            { report.note = "Attribute correction skipped for deforming meshes."; return null; }
            if (float.IsInfinity(metrics.weightedError) || float.IsNaN(metrics.weightedError))
            { report.note = "Attribute correction skipped: invalid surface correspondence."; return null; }
            var original = new LodMeshData(source); var target = new LodMeshData(reduced);
            var visible = options.screenBudget ? screen : null;
            var bestScreen = visible?.Measure(reduced,cancelled);
            if (bestScreen != null)
            {
                report.screenColorVerification = true;
                report.visibleRgbaBefore = report.visibleRgbaAfter = bestScreen.rgbaRms;
                report.visibleChannelsBefore = report.visibleChannelsAfter = bestScreen.rgbaChannelRms;
            }
            bool normals = original.normals.Length == original.positions.Length && target.normals.Length == target.positions.Length;
            bool colors = original.colors.Length == original.positions.Length && target.colors.Length == target.positions.Length;
            if (!normals && !colors) { report.note = "No authored normals or RGBA channel to correct."; return null; }
            var smoothing = normals ? new LodSmoothingRegions(original,target,cancelled) : null;
            var normalFit = normals ? new FieldFit(target.normals.Select(n => (Vector4)n.normalized).ToArray(),smoothing.owners) : null;
            var colorOwners = colors ? ColorOwners(target) : null;
            var colorFit = colors ? new FieldFit(target.colors.Select(c => (Vector4)c).ToArray(),colorOwners) : null;
            PinSeams(target,normalFit,colorFit,smoothing?.owners,colorOwners);
            if (smoothing != null)
            {
                report.smoothingRegions = smoothing.count; report.linkedNormalDuplicates = smoothing.linkedDuplicates;
                report.mixedSmoothingFaces = smoothing.mixedFaces; report.missingSmoothingRegions = smoothing.missingRegions;
                report.regionNormalsBefore = smoothing.Measure(target.normals,cancelled); report.regionNormalsAfter = report.regionNormalsBefore;
                report.incompleteSmoothingRegions = report.regionNormalsBefore.Count(e => e.unresolved > 0);
                for (int i = 0; i < smoothing.pinned.Length; i++) if (smoothing.pinned[i]) normalFit.pinned[i] = true;
            }
            int samples = 0;
            for (int s = 0; s < source.subMeshCount; s++)
            {
                var src = LodSurfaceValidation.SurfaceTriangles(original,LodMeshData.Triangles(source,s));
                var dst = LodSurfaceValidation.SurfaceTriangles(target,LodMeshData.Triangles(reduced,s));
                if (src.Length == 0 || dst.Length == 0) continue;
                samples += FitDirection(target,dst,original,src,false,original.scale,normalFit,colorFit,smoothing?.slots[s],cancelled);
                samples += FitDirection(original,src,target,dst,true,original.scale,normalFit,colorFit,smoothing?.slots[s],cancelled);
            }
            report.samples = samples;
            // A weaker prior on far LODs permits more field redistribution. The
            // acceptance gate remains independent of the simplification weights.
            float prior = options.maxColorError >= .1f ? .02f : .1f;
            var fittedNormals = normalFit?.Solve(prior,cancelled);
            var fittedColors = colorFit?.Solve(prior,cancelled);
            Mesh trial = null;
            try
            {
                trial = UnityEngine.Object.Instantiate(reduced); trial.name = reduced.name;
                if (MeshUvState.IsDraft(reduced)) MeshUvState.SetDraft(trial,true);
                var best = metrics;
                var acceptedNormals = target.normals;
                float[] blends = { 1, .5f, .25f };
                if (normals)
                {
                    foreach (float blend in blends)
                    {
                        CheckCancellation(cancelled);
                        var values = new Vector3[target.positions.Length];
                        var blockedOwners = new bool[values.Length];
                        for (int i = 0; i < values.Length; i++)
                        {
                            var fitted = ((Vector3)fittedNormals[i]).normalized;
                            if (normalFit.pinned[i] || fitted.sqrMagnitude <= .5f || Vector3.Dot(fitted,target.normals[i].normalized) <= .1f)
                                blockedOwners[smoothing.owners[i]] = true;
                        }
                        for (int i = 0; i < values.Length; i++)
                        {
                            var fitted = ((Vector3)fittedNormals[i]).normalized;
                            // Keep the original hemisphere, including at hard edges.
                            values[i] = !blockedOwners[smoothing.owners[i]]
                                ? Vector3.Lerp(target.normals[smoothing.owners[i]].normalized,fitted,blend).normalized : target.normals[i];
                        }
                        if (values.SequenceEqual(target.normals)) continue;
                        trial.normals = values;
                        UvProgress.Report(UvProgress.Current.fraction,$"{source.name}: verify normal fit {blend:P0}");
                        var measured = LodSurfaceValidation.MeasureMeshes(source,trial,settings,cancelled,ignoreDegenerateFaces:true); report.trials++;
                        if (!NormalImproved(measured,best)) continue;
                        var regions = smoothing.Measure(values,cancelled);
                        if (!LodSmoothingRegions.NoRegression(regions,report.regionNormalsAfter) ||
                            !LodSmoothingRegions.NoRegression(regions,report.regionNormalsBefore)) continue;
                        var local = visible?.Measure(trial,cancelled);
                        if (local != null && !local.DoesNotWorsen(bestScreen,true)) continue;
                        if (local != null) bestScreen = local;
                        report.regionNormalsAfter = regions;
                        best = measured; acceptedNormals = values; report.normalsAccepted = true; report.normalBlend = blend;
                    }
                    trial.normals = acceptedNormals;
                    if (report.normalsAccepted && target.tangents.Length == target.positions.Length)
                        trial.tangents = OrthogonalTangents(target.tangents,target.normals,acceptedNormals);
                }
                var acceptedColors = target.colors;
                if (colors)
                {
                    var colorReport = report;
                    VerifyColors(fittedColors,"least-squares");
                    // Reuse the same facing-compatible, same-material source
                    // barycentric samples for a bounded mass-average alternative.
                    // Only retry rejected varying-color fits, avoiding extra
                    // verification passes for already improved or constant fields.
                    if (!report.colorsAccepted && original.colors.Any(c => !c.Equals(original.colors[0])))
                        VerifyColors(colorFit.Resample(cancelled),"area-resampled");
                    int visibleSamples = 0;
                    if (visible != null && original.colors.Any(c => !c.Equals(original.colors[0])))
                    {
                        var screenFit = new FieldFit(target.colors.Select(c => (Vector4)c).ToArray(),colorOwners);
                        Array.Copy(colorFit.pinned,screenFit.pinned,colorFit.pinned.Length);
                        visibleSamples = visible.AddVisibleColorSamples(reduced,screenFit.Add,cancelled);
                        if (visibleSamples > 0)
                        {
                            VerifyColors(screenFit.Solve(.02f,cancelled),"visible-pixel least-squares");
                            VerifyColors(screenFit.Solve(.002f,cancelled),"visible-pixel weak-prior fit");
                        }
                    }
                    SetColors(trial,acceptedColors,reduced.GetVertexAttributeFormat(VertexAttribute.Color));
                    if (visible != null)
                    {
                        if (report.colorsAccepted)
                        {
                            var measured = LodSurfaceValidation.MeasureMeshes(source,trial,settings,cancelled,ignoreDegenerateFaces:true);
                            if (Finite(measured)) best = measured;
                            else
                            {
                                acceptedColors = target.colors; SetColors(trial,acceptedColors,reduced.GetVertexAttributeFormat(VertexAttribute.Color));
                                report.colorsAccepted = false; report.colorBlend = 0; bestScreen = visible.Measure(trial,cancelled);
                            }
                        }
                        report.visibleColorSamples = visibleSamples;
                        report.visibleRgbaAfter = bestScreen.rgbaRms; report.visibleChannelsAfter = bestScreen.rgbaChannelRms;
                        report.note = $" Far RGBA uses {visibleSamples} paired visible samples; all four screen-channel RMS values and local paint loss must not increase. Surface RGBA errors remain diagnostic.";
                    }
                    void VerifyColors(Vector4[] fitted,string method)
                    {
                        foreach (float blend in blends)
                        {
                            CheckCancellation(cancelled);
                            var values = new Color[target.positions.Length];
                            for (int i = 0; i < values.Length; i++) values[i] = (Color)Vector4.Lerp(target.colors[i],fitted[i],blend);
                            if (values.SequenceEqual(target.colors)) continue;
                            SetColors(trial,values,reduced.GetVertexAttributeFormat(VertexAttribute.Color));
                            UvProgress.Report(UvProgress.Current.fraction,$"{source.name}: verify RGBA {method} {blend:P0}");
                            if (visible != null) { VerifyVisible(values,method,blend); continue; }
                            var measured = LodSurfaceValidation.MeasureMeshes(source,trial,settings,cancelled,ignoreDegenerateFaces:true); colorReport.trials++;
                            if (!ColorImproved(measured,best)) continue;
                            best = measured; acceptedColors = values; colorReport.colorsAccepted = true; colorReport.colorBlend = blend;
                            colorReport.colorMethod = method;
                        }
                        if (visible == null) return;
                        // Different RGBA channels can prefer different priors or
                        // blend amounts. Keep every already accepted channel and
                        // test a single-channel replacement against the full view.
                        for (int channel = 0; channel < 4; channel++) foreach (float blend in blends)
                        {
                            CheckCancellation(cancelled);
                            var values = (Color[])acceptedColors.Clone();
                            for (int i = 0; i < values.Length; i++) values[i][channel] = Mathf.Lerp(target.colors[i][channel],fitted[i][channel],blend);
                            if (values.SequenceEqual(acceptedColors)) continue;
                            SetColors(trial,values,reduced.GetVertexAttributeFormat(VertexAttribute.Color));
                            VerifyVisible(values,method+" channel "+channel,blend);
                        }
                        void VerifyVisible(Color[] values,string name,float blend)
                        {
                            var local = visible.Measure(trial,cancelled); colorReport.trials++;
                            if (float.IsNaN(local.rgbaRms) || float.IsInfinity(local.rgbaRms) || !local.DoesNotWorsen(bestScreen,true)) return;
                            bool improved = local.rgbaRms < bestScreen.rgbaRms-1e-6f;
                            for (int ch = 0; ch < 4; ch++) improved |= local.rgbaChannelRms[ch] < bestScreen.rgbaChannelRms[ch]-1e-6f;
                            if (!improved) return;
                            bestScreen = local; acceptedColors = values; colorReport.colorsAccepted = true;
                            colorReport.colorBlend = blend; colorReport.colorMethod = name;
                        }
                    }
                }
                metrics = best;
                report.normalRmsAfter = best.NormalRms; report.normalMaxAfter = best.authoredNormalAngle;
                report.colorRmsAfter = best.ColorRms; report.colorMaxAfter = best.colorMax;
                report.note = $"Surface attribute fit: normals {(report.normalsAccepted ? "improved" : "kept")}, RGBA {(report.colorsAccepted ? "improved via "+report.colorMethod : "kept")} ({samples} samples, {report.trials} verification trials)."+report.note;
                if (smoothing != null) report.note += $" Smoothing: {smoothing.count} source regions, {smoothing.linkedDuplicates} linked normal duplicates, " +
                    $"{smoothing.mixedFaces} mixed/unmapped faces pinned, {smoothing.missingRegions} source regions without a mapped LOD face, " +
                    $"{report.incompleteSmoothingRegions} incomplete correspondences pinned. Per-region normal RMS/max must not increase.";
                if (!report.normalsAccepted && !report.colorsAccepted) return null;
                var result = trial; trial = null; return result;
            }
            finally { if (trial) UnityEngine.Object.DestroyImmediate(trial); }
        }

        internal static bool NormalImproved(LodSurfaceValidation.Metrics a,LodSurfaceValidation.Metrics b)
            => Finite(a) && a.NormalRms < b.NormalRms-Mathf.Max(1e-4f,b.NormalRms*1e-4f) &&
                a.authoredNormalAngle <= b.authoredNormalAngle+1e-4f;

        internal static bool ColorImproved(LodSurfaceValidation.Metrics a,LodSurfaceValidation.Metrics b)
        {
            if (!Finite(a)) return false;
            bool improved = false;
            for (int ch = 0; ch < 4; ch++)
            {
                if (a.colorMax[ch] > b.colorMax[ch]+1e-6f || a.ColorRms[ch] > b.ColorRms[ch]+1e-6f) return false;
                improved |= a.ColorRms[ch] < b.ColorRms[ch]-Mathf.Max(1e-6f,b.ColorRms[ch]*1e-4f);
            }
            return improved;
        }
        static bool Finite(LodSurfaceValidation.Metrics m) => !float.IsNaN(m.weightedError) && !float.IsInfinity(m.weightedError);
        static void CheckCancellation(Func<bool> cancelled)
        { if (cancelled?.Invoke() == true) throw new OperationCanceledException("LOD attribute correction cancelled."); }
        static void SetColors(Mesh mesh,Color[] values,VertexAttributeFormat format)
        {
            if (format == VertexAttributeFormat.UNorm8) mesh.SetColors(values.Select(c => (Color32)c).ToArray());
            else mesh.SetColors(values);
        }
        static Vector4[] OrthogonalTangents(Vector4[] original,Vector3[] previousNormals,Vector3[] normals)
        {
            var result = new Vector4[original.Length];
            for (int i = 0; i < result.Length; i++)
            {
                if (normals[i].Equals(previousNormals[i])) { result[i] = original[i]; continue; }
                Vector3 t = (Vector3)original[i]; t -= normals[i]*Vector3.Dot(normals[i],t);
                if (t.sqrMagnitude < 1e-10f)
                    t = Vector3.Cross(normals[i],Mathf.Abs(normals[i].y) < .9f ? Vector3.up : Vector3.right);
                t.Normalize(); result[i] = new Vector4(t.x,t.y,t.z,original[i].w);
            }
            return result;
        }

        internal static int[] ColorOwners(LodMeshData mesh)
        {
            var owners = Enumerable.Range(0,mesh.positions.Length).ToArray();
            int Root(int vertex)
            {
                while (owners[vertex] != vertex)
                {
                    owners[vertex] = owners[owners[vertex]];
                    vertex = owners[vertex];
                }
                return vertex;
            }
            void Join(int first,int second)
            {
                if (!mesh.colors[first].Equals(mesh.colors[second])) return;
                first = Root(first); second = Root(second);
                owners[Math.Max(first,second)] = Math.Min(first,second);
            }
            // Join only unambiguous adjacent corners within a material slot.
            // Coincident opposite faces and corner contacts remain independent.
            for (int slot = 0; slot < mesh.source.subMeshCount; slot++)
                foreach (var pair in LodSmoothingRegions.EdgePairs(mesh,LodMeshData.Triangles(mesh.source,slot)))
                {
                    Join(pair[0].a,pair[1].b);
                    Join(pair[0].b,pair[1].a);
                }
            for (int vertex = 0; vertex < owners.Length; vertex++) owners[vertex] = Root(vertex);
            return owners;
        }

        static void PinSeams(LodMeshData mesh,FieldFit normals,FieldFit colors,int[] normalOwners,int[] colorOwners)
        {
            var groups = new Dictionary<Vector3,List<int>>();
            for (int i = 0; i < mesh.positions.Length; i++)
            {
                if (!groups.TryGetValue(mesh.positions[i],out var group)) groups[mesh.positions[i]] = group = new List<int>();
                group.Add(i);
            }
            foreach (var group in groups.Values)
            {
                bool normalSeam = normals != null && group.Any(i => normalOwners[group[0]] != normalOwners[i] &&
                    Vector3.Dot(mesh.normals[group[0]].normalized,mesh.normals[i].normalized) < .9999f);
                bool colorSeam = colors != null && group.Any(i => colorOwners[group[0]] != colorOwners[i] ||
                    ((Vector4)mesh.colors[group[0]]-(Vector4)mesh.colors[i]).sqrMagnitude > 1e-8f);
                foreach (int i in group)
                {
                    if (normalSeam) normals.pinned[i] = true;
                    if (colorSeam) colors.pinned[i] = true;
                }
            }
            // A render vertex shared by material slots cannot fit two fields independently.
            var slots = new int[mesh.positions.Length]; for (int i = 0; i < slots.Length; i++) slots[i] = -1;
            for (int s = 0; s < mesh.source.subMeshCount; s++) foreach (int v in mesh.source.GetIndices(s))
            {
                if (slots[v] >= 0 && slots[v] != s)
                {
                    if (normals != null) normals.pinned[v] = true;
                    if (colors != null) colors.pinned[v] = true;
                }
                slots[v] = s;
            }
        }

        static int FitDirection(LodMeshData from,int[] faces,LodMeshData to,int[] other,bool reverse,float sourceScale,
            FieldFit normals,FieldFit colors,LodSmoothingRegions.Slot smoothing,Func<bool> cancelled)
        {
            var geometric = new Vector3[other.Length/3];
            for (int i = 0; i < other.Length; i += 3) geometric[i/3] = LodSurfaceValidation.GeometricNormal(to,other,i);
            var bvh = new TriangleBvh(to.positions,other);
            bool identical = faces.SequenceEqual(other) && from.positions.SequenceEqual(to.positions);
            int count = 0;
            for (int i = 0; i < faces.Length; i += 3)
            {
                CheckCancellation(cancelled);
                var faceNormal = LodSurfaceValidation.GeometricNormal(from,faces,i);
                double weight = Vector3.Cross((from.positions[faces[i+1]]-from.positions[faces[i]])/sourceScale,
                    (from.positions[faces[i+2]]-from.positions[faces[i]])/sourceScale).magnitude/18;
                // Nine equal-area centroids, distinct from the normal verification
                // quadrature. Both projection directions constrain the fitted field.
                for (int x = 0; x < 3; x++) for (int y = 0; y < 3-x; y++)
                {
                    Add(new Vector3((x+1f/3)/3,(y+1f/3)/3,1-(x+y+2f/3)/3));
                    if (x+y < 2) Add(new Vector3((x+2f/3)/3,(y+2f/3)/3,1-(x+y+4f/3)/3));
                }
                void Add(Vector3 bary)
                {
                    var hit = LodSurfaceValidation.CorrespondingHit(from,faces,i,bary,to,other,bvh,geometric,faceNormal,identical);
                    if (hit.triangleIndex < 0) return;
                    int j = hit.triangleIndex*3;
                    var source = reverse ? from : to; var srcFaces = reverse ? faces : other;
                    int srcIndex = reverse ? i : j; var srcBary = reverse ? bary : hit.barycentric;
                    var dstFaces = reverse ? other : faces; int dstIndex = reverse ? j : i;
                    var dstBary = reverse ? hit.barycentric : bary;
                    if (normals != null)
                    {
                        int region = reverse ? smoothing.sourceRegions[i/3] : smoothing.targetRegions[i/3];
                        var surfaces = reverse ? smoothing.targetSurfaces : smoothing.sourceSurfaces;
                        if (region >= 0 && surfaces.TryGetValue(region,out var surface))
                        {
                            var normalHit = surface.Hit(from,faces,i,bary);
                            if (normalHit.triangleIndex >= 0)
                            {
                                int at = normalHit.triangleIndex*3;
                                var normalSourceFaces = reverse ? faces : surface.faces; int normalSourceIndex = reverse ? i : at;
                                var normalSourceBary = reverse ? bary : normalHit.barycentric;
                                var n = source.normals[normalSourceFaces[normalSourceIndex]]*normalSourceBary.x +
                                    source.normals[normalSourceFaces[normalSourceIndex+1]]*normalSourceBary.y + source.normals[normalSourceFaces[normalSourceIndex+2]]*normalSourceBary.z;
                                normals.Add(reverse ? surface.faces : faces,reverse ? at : i,reverse ? normalHit.barycentric : bary,n.normalized,weight);
                            }
                        }
                    }
                    if (colors != null)
                    {
                        var c = source.colors[srcFaces[srcIndex]]*srcBary.x + source.colors[srcFaces[srcIndex+1]]*srcBary.y + source.colors[srcFaces[srcIndex+2]]*srcBary.z;
                        colors.Add(dstFaces,dstIndex,dstBary,c,weight);
                    }
                    count++;
                }
            }
            return count;
        }

        // Sparse normal equations for barycentric least squares, solved with
        // preconditioned conjugate gradients. No position welding or dense NxN matrix.
        sealed class FieldFit
        {
            readonly Dictionary<int,double>[] rows;
            readonly Vector4[] prior, rhs, minimum, maximum;
            readonly double[] mass;
            readonly int[] owners;
            internal readonly bool[] pinned;
            internal FieldFit(Vector4[] values,int[] representatives = null)
            {
                representatives = representatives ?? Enumerable.Range(0,values.Length).ToArray();
                var roots = representatives.Distinct().ToArray(); var mapping = roots.Select((v,i) => (v,i)).ToDictionary(p => p.v,p => p.i);
                owners = representatives.Select(v => mapping[v]).ToArray(); prior = roots.Select(v => values[v]).ToArray();
                rhs = new Vector4[prior.Length]; mass = new double[prior.Length];
                minimum = (Vector4[])prior.Clone(); maximum = (Vector4[])prior.Clone(); pinned = new bool[values.Length];
                rows = new Dictionary<int,double>[prior.Length];
                for (int i = 0; i < rows.Length; i++) rows[i] = new Dictionary<int,double>();
            }
            internal void Add(int[] face,int index,Vector3 bary,Vector4 desired,double weight)
            {
                for (int a = 0; a < 3; a++)
                {
                    int v = owners[face[index+a]]; double w = weight*bary[a];
                    mass[v] += w; rhs[v] += desired*(float)w;
                    minimum[v] = Vector4.Min(minimum[v],desired); maximum[v] = Vector4.Max(maximum[v],desired);
                    for (int b = 0; b < 3; b++)
                    {
                        int u = owners[face[index+b]]; rows[v].TryGetValue(u,out double old); rows[v][u] = old+w*bary[b];
                    }
                }
            }
            internal Vector4[] Solve(float regularization,Func<bool> cancelled)
            {
                var result = (Vector4[])prior.Clone(); int count = rows.Length;
                var fixedVariables = new bool[count];
                for (int i = 0; i < pinned.Length; i++) if (pinned[i]) fixedVariables[owners[i]] = true;
                // A pinned copy fixes the shared variable and every render duplicate.
                for (int i = 0; i < pinned.Length; i++) pinned[i] = fixedVariables[owners[i]];
                var lambda = new double[count]; var diagonal = new double[count];
                for (int i = 0; i < count; i++)
                {
                    lambda[i] = Math.Max(1e-20,mass[i]*regularization);
                    rows[i].TryGetValue(i,out double d); diagonal[i] = d+lambda[i];
                }
                for (int channel = 0; channel < 4; channel++)
                {
                    var x = new double[count]; var r = new double[count]; var p = new double[count]; var ap = new double[count];
                    for (int i = 0; i < count; i++) x[i] = prior[i][channel];
                    Multiply(x,ap,lambda);
                    double rz = 0;
                    for (int i = 0; i < count; i++)
                    {
                        r[i] = fixedVariables[i] ? 0 : rhs[i][channel]+lambda[i]*x[i]-ap[i];
                        p[i] = r[i]/diagonal[i]; rz += r[i]*p[i];
                    }
                    double initial = rz;
                    for (int iteration = 0; iteration < 64 && rz > Math.Max(1e-24,initial*1e-10); iteration++)
                    {
                        CheckCancellation(cancelled); Multiply(p,ap,lambda);
                        double denom = 0; for (int i = 0; i < count; i++) denom += p[i]*ap[i];
                        if (denom <= 0) break;
                        double alpha = rz/denom, next = 0;
                        for (int i = 0; i < count; i++)
                        {
                            x[i] += alpha*p[i]; r[i] -= alpha*ap[i];
                            if (fixedVariables[i]) r[i] = 0;
                            next += r[i]*r[i]/diagonal[i];
                        }
                        double beta = next/rz;
                        for (int i = 0; i < count; i++) p[i] = r[i]/diagonal[i]+beta*p[i];
                        rz = next;
                    }
                    for (int i = 0; i < count; i++)
                        if (!fixedVariables[i]) result[i][channel] = Mathf.Clamp((float)x[i],minimum[i][channel],maximum[i][channel]);
                }
                return owners.Select(i => result[i]).ToArray();
            }
            internal Vector4[] Resample(Func<bool> cancelled)
            {
                var result = (Vector4[])prior.Clone();
                var fixedVariables = new bool[prior.Length];
                for (int i = 0; i < pinned.Length; i++) if (pinned[i]) fixedVariables[owners[i]] = true;
                for (int i = 0; i < result.Length; i++)
                {
                    CheckCancellation(cancelled);
                    if (fixedVariables[i] || mass[i] <= 0) continue;
                    var value = rhs[i]/(float)mass[i];
                    for (int channel = 0; channel < 4; channel++)
                        result[i][channel] = Mathf.Clamp(value[channel],minimum[i][channel],maximum[i][channel]);
                }
                return owners.Select(i => result[i]).ToArray();
            }
            void Multiply(double[] values,double[] result,double[] lambda)
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    double value = lambda[i]*values[i];
                    foreach (var item in rows[i]) value += item.Value*values[item.Key];
                    result[i] = value;
                }
            }
        }
    }
}
