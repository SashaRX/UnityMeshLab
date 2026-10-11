using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        /// <summary>Resolve new detail against the current LOD's actual lower
        /// surface, including newly allocated charts. Dependencies are evaluated
        /// before UV interpolation; atlas packing cannot invent this relation.</summary>
        static void ProjectNewUnderlays(Surface[] surfaces, Options options, CancellationToken token)
        {
            if (!options.preserveProjectedOverlap) return;
            var refs = surfaces.SelectMany(s => Enumerable.Range(0, s.faces.Length).Select(f => (s, f))).ToArray();
            var timing=System.Diagnostics.Stopwatch.StartNew();
            if(refs.Length>2048) UvtLog.Info(UvtLog.Category.General,$"[ReverseUV] Resolve current-LOD underlays: LOD{surfaces[0].lod}, {refs.Length} faces.");
            var positions = refs.SelectMany(p => Enumerable.Range(0, 3).Select(k => p.s.positions[p.s.indices[p.f * 3 + k]])).ToArray();
            var normals = refs.Select(p => Normal(p.s, p.f)).ToArray();
            var centers = refs.Select((p, i) => (positions[i * 3] + positions[i * 3 + 1] + positions[i * 3 + 2]) / 3).ToArray();
            var bvh = new TriangleBvh(positions, Enumerable.Range(0, positions.Length).ToArray());
            var states = new byte[refs.Length];
            var original=refs.Select(p=>(face:p.s.faces[p.f].Copy(),pixels:Enumerable.Range(0,3).Select(k=>p.s.pixels[p.f*3+k]).ToArray())).ToArray();
            var moved=new HashSet<int>();
            var rolledBack=new HashSet<int>();
            var byFace=Enumerable.Range(0,refs.Length).ToDictionary(i=>(refs[i].s.node,refs[i].f),i=>i);
            var byChart=Enumerable.Range(0,refs.Length).GroupBy(i=>refs[i].s.faces[refs[i].f].chart)
                .ToDictionary(g=>g.Key,g=>new HashSet<int>(g));
            long comparisons = 0;
            float depthEpsilon = options.projectionReach * 1e-5f;
            float tie = Math.Max(1e-12f, options.projectionReach * options.projectionReach * 1e-6f);

            List<TriangleBvh.HitResult> Hits(int target, Vector3 point)
            {
                var hits = new List<TriangleBvh.HitResult>();
                bvh.AnyNormalFilteredWithin(point, normals[target], normals, options.normalDot,
                    options.projectionReach * options.projectionReach, hit => {
                        int face = hit.triangleIndex;
                        var offset=point-hit.point;
                        float depth=Vector3.Dot(offset,normals[face]);
                        var lateral=offset-normals[face]*depth;
                        // A clamped nearest point on a neighbouring curved face
                        // is not a projection onto an underlying surface. Permit
                        // only a normal projection, with 1% angular slack.
                        float lateralLimit=Math.Max(options.projectionReach*1e-6f,Math.Abs(depth)*.01f);
                        // The sign is geometric. Renderer/triangle order never
                        // decides which surface lies underneath the detail.
                        if (face != target && Vector3.Dot(centers[face] - centers[target], normals[target]) < -depthEpsilon
                            && Vector3.Dot(hit.point - point, normals[target]) <= depthEpsilon
                            && lateral.sqrMagnitude<=lateralLimit*lateralLimit)
                            hits.Add(hit);
                        return false;
                    }, ref comparisons, options.comparisonBudget, token);
                return hits;
            }

            bool Resolve(int index)
            {
                token.ThrowIfCancellationRequested();
                var (surface, faceIndex) = refs[index]; var face = surface.faces[faceIndex];
                if (states[index] == 2 || face.inherited) return true;
                if (states[index] == 1 || states[index] == 3) return false;
                states[index] = 1;
                var choices = Hits(index, centers[index]).OrderBy(h => h.distSq).ThenBy(h => h.triangleIndex).ToArray();
                if (choices.Length == 0) { states[index] = 2; return true; }
                var probes = new List<Vector3> { positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2] };
                probes.AddRange(Interior.Select(b => positions[index * 3] * b.x + positions[index * 3 + 1] * b.y + positions[index * 3 + 2] * b.z));
                var samples = probes.Select(point => Hits(index, point)).ToArray();
                if(!Resolve(choices[0].triangleIndex)) {states[index]=3;return false;}
                var closest = choices[0]; var (parentSurface, parentFaceIndex) = refs[closest.triangleIndex];
                var parentFace = parentSurface.faces[parentFaceIndex];
                if (choices.Any(h => h.distSq <= closest.distSq + tie && refs[h.triangleIndex].s.faces[refs[h.triangleIndex].f].chart != parentFace.chart))
                { states[index] = 2; return true; }
                var selected = new TriangleBvh.HitResult[samples.Length];
                for (int p = 0; p < samples.Length; ++p)
                {
                    var eligible = samples[p].Where(h => refs[h.triangleIndex].s.faces[refs[h.triangleIndex].f].chart == parentFace.chart)
                        .OrderBy(h => h.distSq).ThenBy(h => h.triangleIndex).ToArray();
                    if (eligible.Length == 0) { states[index] = 2; return true; }
                    selected[p] = eligible[0];
                    if(!Resolve(selected[p].triangleIndex)) {states[index]=3;return false;}
                    if(refs[selected[p].triangleIndex].s.faces[refs[selected[p].triangleIndex].f].chart!=parentFace.chart)
                    {states[index]=2;return true;}
                }
                Vector2 PixelAt(TriangleBvh.HitResult hit)
                {
                    var (s, f) = refs[hit.triangleIndex]; int t = f * 3;
                    return s.pixels[t] * hit.barycentric.x + s.pixels[t + 1] * hit.barycentric.y + s.pixels[t + 2] * hit.barycentric.z;
                }
                var pixels = selected.Take(3).Select(PixelAt).ToArray();
                double metric = surface.metricFrames!=null && options.stretchFrame==Matrix4x4.identity
                    ?TriangleAnisotropy(surface.metricFrames[faceIndex],pixels[0],pixels[1],pixels[2])
                    :TriangleAnisotropy(positions[index*3],positions[index*3+1],positions[index*3+2],pixels[0],pixels[1],pixels[2],options.stretchFrame);
                if (double.IsNaN(metric) || metric > options.maxAnisotropy) { states[index] = 2; return true; }
                int parentStart = parentFaceIndex * 3;
                if (Math.Sign(Cross(pixels[1] - pixels[0], pixels[2] - pixels[0])) * surface.orientation
                    != Math.Sign(Cross(parentSurface.pixels[parentStart + 1] - parentSurface.pixels[parentStart],
                        parentSurface.pixels[parentStart + 2] - parentSurface.pixels[parentStart])) * parentSurface.orientation)
                { states[index] = 2; return true; }
                for (int p = 0; p < Interior.Length; ++p)
                {
                    var b = Interior[p]; var interpolated = pixels[0] * b.x + pixels[1] * b.y + pixels[2] * b.z;
                    if ((PixelAt(selected[p + 3]) - interpolated).magnitude > options.boundaryToleranceTexels)
                    { states[index] = 2; return true; }
                }
                var support = byChart[parentFace.chart].Where(i=>Vector3.Dot(centers[i]-centers[index],normals[index]) < -depthEpsilon)
                    .Select(i=>(p:refs[i],i)).ToArray();
                var chart = new Chart { pixels = support.SelectMany(p => Enumerable.Range(0, 3).Select(k => p.p.s.pixels[p.p.f * 3 + k])).ToArray(),
                    layers = support.Select(p => p.p.s.faces[p.p.f].layer).ToArray() };
                if (!CoveredByParentLayer(chart, parentFace.layer, pixels, 0, options, ref comparisons, token))
                { states[index] = 2; return true; }
                byChart[face.chart].Remove(index);byChart[parentFace.chart].Add(index);
                for (int k = 0; k < 3; ++k) surface.pixels[faceIndex * 3 + k] = pixels[k];
                moved.Add(index);
                face.inherited = true; face.ambiguous = false; face.rejection = null;
                face.localFallback = false; face.chart = parentFace.chart;
                face.parentLod = surface.lod; face.parentMesh = parentSurface.node; face.parentFace = parentFaceIndex;
                face.distance = Mathf.Sqrt(selected.Max(h => h.distSq)); face.layer = parentFace.layer + 1;
                foreach (var hit in selected)
                {
                    var parent = refs[hit.triangleIndex];
                    parent.s.faces[parent.f].projectionSupport = true;
                }
                // Every intersected support face, not just seven sample hits,
                // must participate in the ordered overlap audit.
                foreach (var item in support) item.p.s.faces[item.p.f].projectionSupport = true;
                states[index] = 2; return true;
            }
            for (int f = 0; f < refs.Length; ++f) if (!refs[f].s.faces[refs[f].f].inherited) Resolve(f);
            SnapContinuousCorners(surfaces, options);
            ResolveOverlaps(surfaces, options, token);
            // A refused optional projection retains its independently allocated
            // UVs. Do not leave rejected faces sitting on a former underlay.
            bool changed;
            do
            {
                changed=false;
                foreach(int i in moved.ToArray())
                {
                    var (s,f)=refs[i];var face=s.faces[f];
                    bool missingParent=face.parentLod==s.lod && (surfaces[face.parentMesh].faces[face.parentFace].chart!=face.chart
                        || rolledBack.Contains(byFace[(face.parentMesh,face.parentFace)]));
                    if(face.inherited && !missingParent) continue;
                    s.faces[f]=original[i].face.Copy();
                    for(int k=0;k<3;++k) s.pixels[f*3+k]=original[i].pixels[k];
                    moved.Remove(i);rolledBack.Add(i);changed=true;
                }
                if(changed) ResolveOverlaps(surfaces,options,token);
            } while(changed);
            if(Enumerable.Range(0,refs.Length).Any(i=>original[i].face.inherited && !refs[i].s.faces[refs[i].f].inherited))
            {
                for(int i=0;i<refs.Length;++i)
                {
                    var (s,f)=refs[i];s.faces[f]=original[i].face.Copy();
                    for(int k=0;k<3;++k) s.pixels[f*3+k]=original[i].pixels[k];
                }
                ResolveOverlaps(surfaces,options,token);
            }
            if(refs.Length>2048) UvtLog.Info(UvtLog.Category.General,$"[ReverseUV] Current-LOD underlays: {moved.Count} accepted projections, {timing.ElapsedMilliseconds} ms.");
        }
    }
}
