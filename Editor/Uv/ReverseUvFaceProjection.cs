using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        /// <summary>Passes over fewer target faces stay on the calling thread;
        /// int.MaxValue keeps every pass in order. Internal so tests and the
        /// performance control can force either path.</summary>
        internal static int ParallelProjectionMinimumFaces { get; set; } = 256;

        /// <summary>What one thread of a projection pass owns: its chart buffer,
        /// the running query count and the counters the pass reports.</summary>
        internal sealed class ProjectionWorker
        {
            internal ProjectionBatch batch;
            internal readonly List<int> nearby = new List<int>();
            internal long queries, charged, candidates, footprintTicks, footprintCalls;
        }

        /// <summary>One face projection pass. Donor charts, their index and the
        /// target geometry are only read; a face writes only its own three
        /// pixels and its own record, so faces are independent of each other.</summary>
        internal sealed class FaceProjector
        {
            readonly Surface[] donor;
            readonly List<Chart> charts;
            readonly ChartBoundsIndex chartIndex;
            readonly Options options;
            readonly CancellationToken token;

            internal FaceProjector(Surface[] donor, List<Chart> charts, Options options, CancellationToken token)
            {
                this.donor = donor; this.charts = charts; this.options = options; this.token = token;
                chartIndex = ChartBoundsIndex.Create(charts, options.projectionReach, token);
            }

            internal ProjectionWorker ProjectInOrder(Surface[] targets, ProjectionBatch batch, ref long queries)
            {
                var worker = new ProjectionWorker { batch = batch, queries = queries };
                foreach (var target in targets)
                    for (int f = 0; f < target.faces.Length; ++f)
                    {
                        token.ThrowIfCancellationRequested();
                        if(f%ReverseUvQueryBackend.BatchSize==0) batch.Prepare(target,f);
                        if (!target.faces[f].inherited) Project(target, f, target.faces[f], worker);
                    }
                queries = worker.queries;
                return worker;
            }

            /// <summary>Projects every face on worker threads. Each face is charged
            /// from the pass's starting count, so a face that runs out of budget
            /// here also runs out in order; a pass whose total stays within the
            /// budget passes every check of the ordered pass too.</summary>
            /// <returns>Null when the pass must run in order: the native batch keeps
            /// per-surface state between faces, the pass is small, a face failed or
            /// the total exceeds the budget. The pixels are then restored and no
            /// record has changed, so the ordered pass reproduces the result or the
            /// failure exactly.</returns>
            internal ProjectionWorker ProjectInParallel(Surface[] targets, ref long queries)
            {
                if (!Parallelizable(targets)) return null;
                var starts = new int[targets.Length + 1];
                for (int s = 0; s < targets.Length; ++s) starts[s + 1] = checked(starts[s] + targets[s].faces.Length);
                var pixels = Array.ConvertAll(targets, s => (Vector2[])s.pixels.Clone());
                var records = new Face[starts[targets.Length]];
                var workers = new ConcurrentQueue<ProjectionWorker>();
                long start = queries;
                bool failed = false;
                try
                {
                    Parallel.For(0, records.Length, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                        () => new ProjectionWorker(), (i, _, worker) =>
                        {
                            token.ThrowIfCancellationRequested();
                            int s = SurfaceAt(starts, i), f = i - starts[s];
                            if (targets[s].faces[f].inherited) return worker;
                            var record = targets[s].faces[f].Copy();
                            records[i] = record;
                            worker.queries = start;
                            Project(targets[s], f, record, worker);
                            worker.charged += worker.queries - start;
                            return worker;
                        },
                        workers.Enqueue);
                }
                catch (AggregateException)
                {
                    token.ThrowIfCancellationRequested();
                    failed = true;
                }
                var total = new ProjectionWorker { queries = start };
                foreach (var worker in workers)
                {
                    total.queries += worker.charged; total.candidates += worker.candidates;
                    total.footprintTicks += worker.footprintTicks; total.footprintCalls += worker.footprintCalls;
                }
                if (failed || total.queries > options.comparisonBudget)
                {
                    for (int s = 0; s < targets.Length; ++s) Array.Copy(pixels[s], targets[s].pixels, pixels[s].Length);
                    return null;
                }
                Publish(targets, starts, records);
                queries = total.queries;
                return total;
            }

            static void Publish(Surface[] targets, int[] starts, Face[] records)
            {
                for (int s = 0; s < targets.Length; ++s)
                    for (int f = 0; f < targets[s].faces.Length; ++f)
                        if (records[starts[s] + f] != null) CopyProjection(records[starts[s] + f], targets[s].faces[f]);
            }

            bool Parallelizable(Surface[] targets)
            {
                long faces = 0;
                foreach (var target in targets) faces += target.faces.Length;
                if (faces < ParallelProjectionMinimumFaces) return false;
                return options.queryBackend == ReverseUvQueryBackend.Mode.Legacy || options.queryBackend == ReverseUvQueryBackend.Mode.Managed
                    || (options.queryApi ?? ReverseUvQueryBackend.Available) == null;
            }

            static int SurfaceAt(int[] starts, int face)
            {
                int low = 0, high = starts.Length - 1;
                while (low + 1 < high)
                {
                    int middle = low + (high - low) / 2;
                    if (starts[middle] <= face) low = middle;
                    else high = middle;
                }
                return low;
            }

            /// <summary>Every record field <see cref="Project"/> writes.</summary>
            static void CopyProjection(Face from, Face to)
            {
                to.inherited = from.inherited; to.ambiguous = from.ambiguous; to.rejection = from.rejection;
                to.chart = from.chart; to.distance = from.distance; to.coordinateDomain = from.coordinateDomain;
                to.parentLod = from.parentLod; to.parentMesh = from.parentMesh; to.parentFace = from.parentFace;
                to.layer = from.layer; to.projectionLayer = from.projectionLayer;
            }

            void Charge(ProjectionWorker worker)
            {
                if (++worker.queries > options.comparisonBudget) throw new InvalidOperationException("Reverse UV projection query budget exceeded.");
            }

            static void Reject(Face record, string rejection)
            {
                record.ambiguous = true; record.rejection = rejection;
            }

            internal void Project(Surface target, int f, Face record, ProjectionWorker worker)
            {
                int t = f * 3;
                var a = target.positions[target.indices[t]]; var b = target.positions[target.indices[t + 1]];
                var c = target.positions[target.indices[t + 2]]; var center = (a + b + c) / 3;
                var normal = Normal(target, f);
                var (winner, best, ambiguous) = NearestChart(target, f, center, normal, worker);
                record.ambiguous = ambiguous;
                if (winner == null) record.rejection = "no-donor";
                else record.rejection = ambiguous ? "donor-tie" : null;
                if (winner == null || ambiguous) return;
                float maxDistance = best.distSq;
                if (!ProjectCorners(winner, best, target, f, normal, worker, ref maxDistance)
                    || Anisotropy(target, f, options.stretchFrame) > options.maxAnisotropy)
                { Reject(record, "corner-or-metric"); return; }
                if (!InteriorAgrees(winner, target, f, normal, worker)) { Reject(record, "non-affine-or-hole"); return; }
                long footprintStarted = options.performance == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
                bool covered = CoveredByParentLayer(winner, winner.layers[best.triangleIndex], target.pixels, t,
                    options, ref worker.queries, token);
                if (options.performance != null)
                { worker.footprintTicks += System.Diagnostics.Stopwatch.GetTimestamp() - footprintStarted; ++worker.footprintCalls; }
                if (!covered) { Reject(record, "parent-footprint"); return; }
                record.inherited = true; record.chart = winner.id; record.distance = Mathf.Sqrt(maxDistance);
                record.coordinateDomain=winner.coordinateDomain;
                record.ambiguous = false; record.rejection = null;
                record.parentLod = donor[0].lod; record.parentMesh = winner.nodes[best.triangleIndex];
                record.parentFace = winner.faces[best.triangleIndex];
                record.projectionLayer = winner.layers[best.triangleIndex]; record.layer = record.projectionLayer;
            }

            (Chart winner, TriangleBvh.HitResult best, bool ambiguous) NearestChart(Surface target, int f,
                Vector3 center, Vector3 normal, ProjectionWorker worker)
            {
                Chart winner = null;
                var best = new TriangleBvh.HitResult { triangleIndex = -1, distSq = float.MaxValue };
                bool ambiguous = false;
                var nearby = chartIndex?.Near(center, worker.nearby);
                int count = nearby?.Count ?? charts.Count;
                for (int n = 0; n < count; ++n)
                {
                    var chart = charts[nearby == null ? n : nearby[n]];
                    ++worker.candidates;
                    if (chart.bounds.SqrDistance(center) > options.projectionReach * options.projectionReach) continue;
                    Charge(worker);
                    var cached = worker.batch != null && worker.batch.Get(target, f, chart, out var cachedHit) ? (TriangleBvh.HitResult?)cachedHit : null;
                    var hit = NearestDonor(chart, center, normal, options, ref worker.queries, token, out bool chartAmbiguous, cached);
                    if (hit.triangleIndex < 0) continue;
                    float tolerance = Math.Max(1e-12f, options.projectionReach * options.projectionReach * 1e-6f);
                    if (hit.distSq < best.distSq - tolerance) { winner = chart; best = hit; ambiguous = chartAmbiguous; }
                    else if (Math.Abs(hit.distSq - best.distSq) <= tolerance) ambiguous = true;
                }
                return (winner, best, ambiguous);
            }

            /// <summary>Snaps the corners to the winning chart; false when a corner
            /// has no unambiguous hit or the face turns against its donor.</summary>
            bool ProjectCorners(Chart winner, TriangleBvh.HitResult best, Surface target, int f, Vector3 normal,
                ProjectionWorker worker, ref float maxDistance)
            {
                int t = f * 3;
                for (int k = 0; k < 3; ++k)
                {
                    Charge(worker);
                    var hit = NearestDonor(winner, target.positions[target.indices[t + k]], normal, options,
                        ref worker.queries, token, out bool cornerAmbiguous);
                    if (hit.triangleIndex < 0 || cornerAmbiguous) return false;
                    target.pixels[t + k] = Pixel(winner, hit);
                    maxDistance = Math.Max(maxDistance, hit.distSq);
                }
                int donorCorner = best.triangleIndex * 3;
                int expectedSign = Math.Sign(Cross(winner.pixels[donorCorner + 1] - winner.pixels[donorCorner],
                    winner.pixels[donorCorner + 2] - winner.pixels[donorCorner])) * winner.orientations[best.triangleIndex];
                int actualSign = Math.Sign(Cross(target.pixels[t + 1] - target.pixels[t], target.pixels[t + 2] - target.pixels[t])) * target.orientation;
                return actualSign == expectedSign;
            }

            /// <summary>Interior probes must land where the corners interpolate them.</summary>
            bool InteriorAgrees(Chart winner, Surface target, int f, Vector3 normal, ProjectionWorker worker)
            {
                int t = f * 3;
                var a = target.positions[target.indices[t]]; var b = target.positions[target.indices[t + 1]];
                var c = target.positions[target.indices[t + 2]];
                float span = Math.Max((target.pixels[t + 1] - target.pixels[t]).magnitude, (target.pixels[t + 2] - target.pixels[t]).magnitude);
                foreach (var bary in Interior)
                {
                    Charge(worker);
                    var hit = NearestDonor(winner, a * bary.x + b * bary.y + c * bary.z, normal, options,
                        ref worker.queries, token, out bool probeAmbiguous);
                    var interpolated = target.pixels[t] * bary.x + target.pixels[t + 1] * bary.y + target.pixels[t + 2] * bary.z;
                    if (hit.triangleIndex < 0 || probeAmbiguous || (Pixel(winner, hit) - interpolated).magnitude > Math.Max(.25f, span * .02f))
                        return false;
                }
                return true;
            }
        }
    }
}
