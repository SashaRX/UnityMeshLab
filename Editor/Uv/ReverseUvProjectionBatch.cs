using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        // Discover across all donors, then retain each chart's own managed
        // nearest result. A globally closest proposal must never shrink the
        // radius: it could remove a valid anchor from a different chart.
        internal sealed class ProjectionBatch : IDisposable
        {
            readonly List<Chart> charts;
            readonly Options options;
            readonly CancellationToken token;
            readonly Dictionary<Chart, int> ordinal = new Dictionary<Chart, int>();
            readonly int[] faceStarts;
            readonly int totalFaces;
            ReverseUvCandidateBvh[] resolvers;
            ReverseUvQueryBackend.Api api;
            IntPtr scene;
            bool attempted;
            ReverseUvQueryBackend.Buffers buffers;
            TriangleBvh.HitResult[] hits;
            readonly int[] faceSlots = new int[ReverseUvQueryBackend.BatchSize];
            readonly int[] querySlots = new int[ReverseUvQueryBackend.BatchSize];
            int start;
            Surface current;
            internal ProjectionBatch(List<Chart> charts, Options options, CancellationToken token)
            {
                this.charts = charts;
                this.options = options;
                this.token = token;
                faceStarts = new int[charts.Count + 1];
                for (int i = 0; i < charts.Count; ++i)
                {
                    ordinal.Add(charts[i], i);
                    faceStarts[i + 1] = checked(faceStarts[i] + charts[i].faces.Length);
                }
                totalFaces = faceStarts[charts.Count];
                Array.Fill(querySlots, -1);
            }
            bool Create()
            {
                if (attempted)
                    return scene != IntPtr.Zero;
                attempted = true;
                api = options.queryApi ?? ReverseUvQueryBackend.Available;
                if (api == null || totalFaces == 0 || totalFaces > 200000)
                    return false;
                using var construction = options.performance?.Measure("Embree scene construction", current.lod);
                var positions = new Vector3[totalFaces * 3];
                var indices = new int[positions.Length];
                var normals = new Vector3[totalFaces];
                for (int chart = 0; chart < charts.Count; ++chart)
                {
                    token.ThrowIfCancellationRequested();
                    var source = charts[chart];
                    for (int face = 0; face < source.faces.Length; ++face)
                    {
                        int nativeFace = faceStarts[chart] + face;
                        normals[nativeFace] = source.normals[face];
                        if (!ReverseUvQueryBackend.Finite(normals[nativeFace]))
                            return false;
                        for (int k = 0; k < 3; ++k)
                        {
                            int index = nativeFace * 3 + k;
                            var point = source.positions[source.indices[face * 3 + k]];
                            if (!ReverseUvQueryBackend.Finite(point))
                                return false;
                            positions[index] = point;
                            indices[index] = index;
                        }
                    }
                }
                scene = api.create(positions, positions.Length, indices, indices.Length, normals, new byte[totalFaces]);
                if (scene == IntPtr.Zero)
                    return false;
                resolvers = new ReverseUvCandidateBvh[charts.Count];
                for (int i = 0; i < charts.Count; ++i)
                    resolvers[i] = new ReverseUvCandidateBvh(charts[i].bvh, charts[i].normals, null);
                buffers = new ReverseUvQueryBackend.Buffers();
                buffers.EnsureNative();
                hits = new TriangleBvh.HitResult[ReverseUvQueryBackend.BatchSize * ReverseUvQueryBackend.CandidateCapacity];
                options.performance?.Record("Embree scenes", 0, 1, current.lod);
                return true;
            }
            internal void Prepare(Surface surface, int first)
            {
                current = surface;
                start = first;
                Array.Fill(querySlots, -1);
                if (options.queryBackend == ReverseUvQueryBackend.Mode.Legacy || options.queryBackend == ReverseUvQueryBackend.Mode.Managed)
                    return;
                int limit = Math.Min(surface.faces.Length, first + ReverseUvQueryBackend.BatchSize), count = 0;
                for (int f = first; f < limit; ++f)
                {
                    if (!surface.faces[f].inherited)
                        faceSlots[count++] = f;
                }
                if (!ReverseUvQueryBackend.SelectEmbree(options.queryBackend, totalFaces, count, true) || !Create())
                    return;
                using var timing = options.performance?.Measure("Embree centre discovery", surface.lod);
                for (int i = 0; i < count; ++i)
                {
                    if ((i & 63) == 0)
                        token.ThrowIfCancellationRequested();
                    int f = faceSlots[i], t = f * 3;
                    var a = surface.positions[surface.indices[t]];
                    var b = surface.positions[surface.indices[t + 1]];
                    var c = surface.positions[surface.indices[t + 2]];
                    buffers.queries[i] = new ReverseUvQueryBackend.Query
                    {
                        origin = (a + b + c) / 3,
                        direction = Normal(surface, f),
                        reach = options.projectionReach,
                        dotMin = options.normalDot,
                        mode = 4
                    };
                }
                token.ThrowIfCancellationRequested();
                int workers = count >= 512 ? Math.Min(8, Environment.ProcessorCount) : 1;
                if (api.candidates(scene, buffers.queries, buffers.candidates, buffers.counts, count,
                    ReverseUvQueryBackend.CandidateCapacity, workers) != 0)
                {
                    options.performance?.Record("Embree failed batches", 0, 1, surface.lod);
                    return;
                }
                buffers.Workers(workers, token, w =>
                {
                    for (int i = w; i < count; i += workers)
                    {
                        if ((i & 63) == w)
                            token.ThrowIfCancellationRequested();
                        Resolve(i, buffers.scratch[w]);
                    }
                });
                int fallbacks = 0;
                for (int i = 0; i < count; ++i)
                {
                    if (buffers.counts[i] < 0)
                        ++fallbacks;
                    else
                        querySlots[faceSlots[i] - first] = i;
                }
                options.performance?.Record("Embree centre queries", 0, count, surface.lod);
                options.performance?.Record("Embree capacity fallback queries", 0, fallbacks, surface.lod);
            }
            void Resolve(int query, int[] scratch)
            {
                int count = buffers.counts[query];
                if (count < 0)
                    return;
                if (count > ReverseUvQueryBackend.CandidateCapacity)
                {
                    buffers.counts[query] = -1;
                    return;
                }
                int offset = query * ReverseUvQueryBackend.CandidateCapacity;
                Array.Sort(buffers.candidates, offset, count);
                int written = 0;
                for (int first = 0; first < count;)
                {
                    int face = buffers.candidates[offset + first];
                    if (face < 0 || face >= totalFaces)
                    {
                        buffers.counts[query] = -1;
                        return;
                    }
                    int chart = ChartAt(face), limit = first + 1;
                    while (limit < count && buffers.candidates[offset + limit] < faceStarts[chart + 1])
                        ++limit;
                    for (int k = first; k < limit; ++k)
                        buffers.candidates[offset + k] -= faceStarts[chart];
                    var answer = resolvers[chart].Resolve(buffers.queries[query], buffers.candidates, offset + first, limit - first, scratch);
                    if (answer.tri >= 0)
                    {
                        buffers.candidates[offset + written] = chart;
                        hits[offset + written] = new TriangleBvh.HitResult
                        {
                            triangleIndex = answer.tri,
                            distSq = answer.distance,
                            barycentric = answer.bary,
                            point = answer.point
                        };
                        ++written;
                    }
                    first = limit;
                }
                buffers.counts[query] = written;
            }
            int ChartAt(int face)
            {
                int low = 0, high = charts.Count;
                while (low + 1 < high)
                {
                    int middle = low + (high - low) / 2;
                    if (faceStarts[middle] <= face)
                        low = middle;
                    else
                        high = middle;
                }
                return low;
            }
            internal bool Get(Surface surface, int face, Chart chart, out TriangleBvh.HitResult hit)
            {
                hit = default;
                int index = face - start;
                if (!ReferenceEquals(surface, current) || index < 0 || index >= querySlots.Length || querySlots[index] < 0)
                    return false;
                int query = querySlots[index], wanted = ordinal[chart], offset = query * ReverseUvQueryBackend.CandidateCapacity;
                int low = 0, high = buffers.counts[query];
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (buffers.candidates[offset + middle] < wanted)
                        low = middle + 1;
                    else
                        high = middle;
                }
                hit = low < buffers.counts[query] && buffers.candidates[offset + low] == wanted ? hits[offset + low] : new TriangleBvh.HitResult
                {
                    triangleIndex = -1,
                    distSq = options.projectionReach >= float.MaxValue ? float.MaxValue : options.projectionReach * options.projectionReach
                };
                options.performance?.Accumulate("Reused centre hits", 0, 1, surface.lod);
                return true;
            }
            public void Dispose()
            {
                if (scene != IntPtr.Zero)
                {
                    var previous = scene;
                    scene = IntPtr.Zero;
                    api.destroy(previous);
                }
                options.performance?.Record("Native query buffer allocations", 0, buffers?.nativeAllocations ?? 0);
                resolvers = null;
                buffers = null;
                hits = null;
                current = null;
            }
        }
    }
}
