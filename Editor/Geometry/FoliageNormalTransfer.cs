using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class FoliageNormalTransfer
    {
        internal sealed class Diagnostics
        {
            internal int renderers, faces, selected, transferred, misses, invalidHits, hemisphereFlips, duplicated, canonicalQueries;
            internal double sumDistance, maxDistance, cpuMs, gpuMs;
            internal bool gpu;
            internal double MeanDistance => transferred > 0 ? sumDistance / transferred : 0;
            internal double MissPercent => selected > 0 ? misses * 100.0 / selected : 0;
            internal void Add(Diagnostics d)
            {
                renderers += d.renderers;
                faces += d.faces;
                selected += d.selected;
                transferred += d.transferred;
                misses += d.misses;
                invalidHits += d.invalidHits;
                hemisphereFlips += d.hemisphereFlips;
                duplicated += d.duplicated;
                canonicalQueries += d.canonicalQueries;
                sumDistance += d.sumDistance;
                maxDistance = Math.Max(maxDistance, d.maxDistance);
                cpuMs += d.cpuMs;
                gpuMs += d.gpuMs;
                gpu |= d.gpu;
            }
            public override string ToString() => $"{(gpu ? "GPU" : "CPU")}: {transferred:N0}/{selected:N0} transferred, {misses:N0} misses ({MissPercent:F2}%), " +
                $"{invalidHits} invalid hits, {hemisphereFlips} flips, {duplicated} boundary copies, {canonicalQueries} canonical CPU queries; distance mean/max {MeanDistance:G4}/{maxDistance:G4}; CPU {cpuMs:F1} ms, GPU {gpuMs:F1} ms";
        }

        internal sealed class Result
        {
            internal Vector3[] normals; internal Diagnostics diagnostics;
        }

        internal static TriangleBvh.HitResult[] QueryCpu(TriangleBvh bvh, Vector3[] points, float reach, CancellationToken token)
        {
            var hits = new TriangleBvh.HitResult[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                if ((i & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                hits[i] = bvh.FindNearest(points[i], reach);
            }
            return hits;
        }

        internal static async Task<TriangleBvh.HitResult[]> QueryGpu(GpuBvh gpu, TriangleBvh bvh, Vector3[] points, float reach, CancellationToken token, Diagnostics diagnostics = null)
        {
            var clock = Stopwatch.StartNew();
            var queries = new Vector4[points.Length];
            var dummyNormals = new Vector4[points.Length];
            var hits = new GpuBvh.NearestHit[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                queries[i] = new Vector4(points[i].x, points[i].y, points[i].z, reach);
            }

            await gpu.NearestAsync(queries, dummyNormals, queries.Length, false, hits, token);
            if (diagnostics != null)
            {
                diagnostics.gpuMs += clock.Elapsed.TotalMilliseconds;
            }

            clock.Restart();
            var result = new TriangleBvh.HitResult[hits.Length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new TriangleBvh.HitResult { triangleIndex = hits[i].tri, distSq = hits[i].distSq, point = hits[i].point, barycentric = hits[i].bary };
            }
            // Floating-point evaluation on a shared edge can select either incident triangle.
            // Canonicalize only ambiguous edge/radius hits and misses through the existing CPU BVH;
            // report these queries rather than concealing a mixed backend or changing shared BVH semantics.
            int corrected = await Task.Run(() =>
            {
                int count = 0;
                for (int i = 0; i < hits.Length; i++)
                {
                    if ((i & 1023) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }

                    var hit = hits[i];
                    if (hit.tri >= 0 && Math.Min(hit.bary.x, Math.Min(hit.bary.y, hit.bary.z)) > .0001f &&
                        Math.Abs(hit.distSq - reach * reach) > reach * reach * .00001f)
                    {
                        continue;
                    }

                    result[i] = bvh.FindNearest(points[i], reach);
                    count++;
                }
                return count;
            }, token);
            if (diagnostics != null)
            {
                diagnostics.canonicalQueries += corrected;
                diagnostics.cpuMs += clock.Elapsed.TotalMilliseconds;
            }
            return result;
        }

        // Main thread orchestrates GPU batches. CPU queries and interpolation touch only captured arrays.
        internal static async Task<Result> Transfer(FoliageTargetSnapshot target, RemeshNative.IndexedMesh proxy, TriangleBvh bvh,
            GpuBvh gpu, FoliageNormalsSettings settings, float reach, CancellationToken token)
        {
            var selected = new System.Collections.Generic.List<int>();
            for (int i = 0; i < target.selectedVertices.Length; i++)
            {
                if (target.selectedVertices[i])
                {
                    selected.Add(i);
                }
            }

            var points = new Vector3[selected.Count];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = target.positions[selected[i]];
            }

            var clock = Stopwatch.StartNew();
            var queryDiagnostics = new Diagnostics();
            var hits = gpu != null ? await QueryGpu(gpu, bvh, points, reach, token, queryDiagnostics) : await Task.Run(() => QueryCpu(bvh, points, reach, token), token);
            double queryMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var result = await Task.Run(() => Resolve(target, proxy, selected.ToArray(), hits, settings, reach, token), token);
            result.diagnostics.gpu = gpu != null;
            result.diagnostics.cpuMs = clock.Elapsed.TotalMilliseconds + (gpu == null ? queryMs : queryDiagnostics.cpuMs);
            result.diagnostics.gpuMs = queryDiagnostics.gpuMs;
            result.diagnostics.canonicalQueries = queryDiagnostics.canonicalQueries;
            return result;
        }

        internal static Result Resolve(FoliageTargetSnapshot target, RemeshNative.IndexedMesh proxy, int[] selected,
            TriangleBvh.HitResult[] hits, FoliageNormalsSettings settings, float reach, CancellationToken token)
        {
            var r = new Result { normals = (Vector3[])target.localNormals.Clone(), diagnostics = new Diagnostics { renderers = 1, faces = target.selectedIndices.Length / 3, selected = selected.Length } };
            for (int k = 0; k < selected.Length; k++)
            {
                if ((k & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                int v = selected[k];
                var hit = hits[k];
                if (hit.triangleIndex < 0)
                {
                    r.diagnostics.misses++;
                    continue;
                }
                bool valid = hit.triangleIndex < proxy.TriangleCount && FoliageTargetSnapshot.Finite(hit.distSq) && hit.distSq >= 0 && hit.distSq <= reach * reach &&
                    FoliageTargetSnapshot.Finite(hit.point) && FoliageTargetSnapshot.Finite(hit.barycentric) &&
                    Math.Abs(hit.barycentric.x + hit.barycentric.y + hit.barycentric.z - 1) < .001f &&
                    hit.barycentric.x >= -.001f && hit.barycentric.y >= -.001f && hit.barycentric.z >= -.001f;
                Vector3 n = valid ? Interpolate(proxy, hit.triangleIndex, hit.barycentric) : Vector3.zero;
                if (!valid || !MeshGeometry.UsableNormal(n))
                {
                    r.diagnostics.invalidHits++;
                    r.diagnostics.misses++;
                    continue;
                }
                var original = target.captureNormals[v];
                if (settings.alignToOriginalHemisphere && Vector3.Dot(n, original) < 0)
                {
                    n = -n;
                    r.diagnostics.hemisphereFlips++;
                }
                n = MeshGeometry.UnitDirection(Vector3.Lerp(original, n, settings.transferBlend));
                if (!MeshGeometry.UsableNormal(n))
                {
                    r.diagnostics.invalidHits++;
                    r.diagnostics.misses++;
                    continue;
                }
                // Blend 0 and misses retain the original local value exactly.
                r.normals[v] = settings.transferBlend == 0 ? target.localNormals[v] : MeshGeometry.UnitDirection(target.normalCaptureToLocal.MultiplyVector(n));
                r.diagnostics.transferred++;
                double distance = Math.Sqrt(hit.distSq);
                r.diagnostics.sumDistance += distance;
                r.diagnostics.maxDistance = Math.Max(distance, r.diagnostics.maxDistance);
            }
            for (int i = 0; i < r.normals.Length; i++)
            {
                if (!FoliageTargetSnapshot.Finite(r.normals[i]) || Math.Abs(r.normals[i].magnitude - 1) > .01f)
                {
                    throw new InvalidOperationException("Output normal is not finite/unit length.");
                }

                if (!target.selectedVertices[i] && r.normals[i] != target.localNormals[i])
                {
                    throw new InvalidOperationException("Excluded vertex changed.");
                }
            }
            return r;
        }

        internal static Vector3 Interpolate(RemeshNative.IndexedMesh proxy, int face, Vector3 bary)
        {
            int f = face * 3;
            return MeshGeometry.UnitDirection(proxy.normals[proxy.indices[f]] * bary.x + proxy.normals[proxy.indices[f + 1]] * bary.y + proxy.normals[proxy.indices[f + 2]] * bary.z);
        }
    }
}
