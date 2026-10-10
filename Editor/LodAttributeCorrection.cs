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
            public string note;
        }

        // Returns an owned replacement only on improvement. Inputs are never edited.
        internal static Mesh Correct(Mesh source, Mesh reduced, MeshSimplifier.SimplifySettings settings,
            LodPipelineOps.Options options, out Report report, out LodSurfaceValidation.Metrics metrics,
            Func<bool> cancelled = null, LodSurfaceValidation.Metrics? baseline = null)
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
                    SetColors(trial,acceptedColors,reduced.GetVertexAttributeFormat(VertexAttribute.Color));
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
                            var measured = LodSurfaceValidation.MeasureMeshes(source,trial,settings,cancelled,ignoreDegenerateFaces:true); colorReport.trials++;
                            if (!ColorImproved(measured,best)) continue;
                            best = measured; acceptedColors = values; colorReport.colorsAccepted = true; colorReport.colorBlend = blend;
                            colorReport.colorMethod = method;
                        }
                    }
                }
                metrics = best;
                report.normalRmsAfter = best.NormalRms; report.normalMaxAfter = best.authoredNormalAngle;
                report.colorRmsAfter = best.ColorRms; report.colorMaxAfter = best.colorMax;
                report.note = $"Surface attribute fit: normals {(report.normalsAccepted ? "improved" : "kept")}, RGBA {(report.colorsAccepted ? "improved via "+report.colorMethod : "kept")} ({samples} samples, {report.trials} verification trials).";
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
