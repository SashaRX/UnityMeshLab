using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Redistributes voxel triangles on the captured surface before decimation.</summary>
    internal static class RemeshSurfaceRefine
    {
        internal struct Report
        {
            internal int moves, flips, features;
            internal float meanQualityBefore, meanQualityAfter, maxDisplacement;
            internal bool reverted;
        }

        internal static RemeshNative.IndexedMesh Simplify(RemeshNative.IndexedMesh input,
            Vector3[] sourcePositions, int[] sourceIndices, RemeshSettings settings, CancellationToken token, out float error)
        {
            var baseline = RemeshNative.Simplify(input, settings, token, out error);
            if (sourcePositions.Length == 0 || sourceIndices.Length == 0) return baseline;
            float baselineError = error;
            var low = sourcePositions[0]; var high = low;
            foreach (var point in sourcePositions) { low = Vector3.Min(low, point); high = Vector3.Max(high, point); }
            var span = high - low;
            float cell = Mathf.Max(span.x, Mathf.Max(span.y, span.z)) / settings.voxelResolution;
            var prepared = Apply(input, sourcePositions, sourceIndices, cell, token, out var pre);
            Log(pre, cell, "before simplify");
            var candidate = ReferenceEquals(input, prepared) ? baseline : RemeshNative.Simplify(prepared, settings, token, out error);
            var bvh = new TriangleBvh(sourcePositions, sourceIndices);
            if (candidate.TriangleCount > Math.Max(baseline.TriangleCount * 1.1f, baseline.TriangleCount + 2) ||
                !PreservesSurface(bvh, sourcePositions, sourceIndices, baseline.positions, baseline.indices, candidate.positions, candidate.indices, cell, token)) {
                UvtLog.Info(UvtLog.Category.RemeshDiag, "Source surface refinement rejected its collapse candidate: retaining ordinary Simplify before final triangle refinement.");
                candidate = baseline; error = baselineError;
            }
            var result = Apply(candidate, sourcePositions, sourceIndices, cell, token, out var post);
            Log(post, cell, "after simplify");
            return result;
        }

        static void Log(Report report, float cell, string phase)
            => UvtLog.Info(UvtLog.Category.RemeshDiag, $"Source surface refinement {phase}: {report.moves} vertex moves, {report.flips} edge flips, " +
                $"{report.features} feature anchors; triangle quality {report.meanQualityBefore:F3} → {report.meanQualityAfter:F3}; " +
                $"max displacement {report.maxDisplacement:G4} ({report.maxDisplacement / cell:F3} cells){(report.reverted ? "; reverted by surface/topology gate" : "")}. " +
                "Simplify error measures native collapse only.");

        sealed class Source
        {
            internal readonly TriangleBvh bvh;
            internal readonly Vector3[] normals, positions;
            readonly int[] indices;
            readonly bool[] features;

            internal Source(Vector3[] p, int[] ix, CancellationToken token)
            {
                positions = p; indices = ix;
                normals = MeshGeometry.FaceNormals(p, ix);
                bvh = new TriangleBvh(p, ix);
                features = new bool[ix.Length];
                var slots = MeshGeometry.WeldPositions(p, out _);
                var edges = new Dictionary<(int, int), List<int>>();
                for (int i = 0; i < ix.Length; i++) {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    int next = i / 3 * 3 + (i + 1) % 3;
                    var key = Key(slots[ix[i]], slots[ix[next]]);
                    if (!edges.TryGetValue(key, out var corners)) edges[key] = corners = new List<int>(2);
                    corners.Add(i);
                }
                foreach (var corners in edges.Values) {
                    bool sharp = corners.Count != 2 || Vector3.Dot(normals[corners[0] / 3], normals[corners[1] / 3]) < .75f;
                    if (sharp) foreach (int corner in corners) features[corner] = true;
                }
            }

            internal TriangleBvh.HitResult Nearest(Vector3 p, Vector3 normal, float reach)
                => bvh.FindNearestNormalFiltered(p, normal, normals, .25f, reach);

            internal bool FeaturePoint(int face, Vector3 p, float reach, out Vector3 point)
            {
                point = p; float best = reach * reach; bool found = false;
                for (int k = 0; k < 3; k++) {
                    int corner = face * 3 + k;
                    if (!features[corner]) continue;
                    var a = positions[indices[corner]];
                    var b = positions[indices[face * 3 + (k + 1) % 3]];
                    var edge = b - a;
                    float length = edge.sqrMagnitude;
                    if (length <= 0) continue;
                    var candidate = a + edge * Mathf.Clamp01(Vector3.Dot(p - a, edge) / length);
                    float distance = (candidate - p).sqrMagnitude;
                    if (distance > best) continue;
                    best = distance; point = candidate; found = true;
                }
                return found;
            }
        }

        internal static RemeshNative.IndexedMesh Apply(RemeshNative.IndexedMesh input,
            Vector3[] sourcePositions, int[] sourceIndices, float cell, CancellationToken token, out Report report)
        {
            token.ThrowIfCancellationRequested();
            report = default;
            if (!(cell > 0) || float.IsInfinity(cell) || input.TriangleCount == 0 || sourceIndices.Length == 0) return input;
            var before = RemeshTopology.Inspect(input.positions, input.indices, token);
            if (!before.Valid) return input; // Do not disguise an upstream topology defect.
            // Voxel outputs have a single index per position. Split channel inputs need
            // coordinated corner motion and are deliberately outside this pre-UV pass.
            var unique = new HashSet<Vector3>(input.positions);
            if (unique.Count != input.positions.Length) return input;
            var p = (Vector3[])input.positions.Clone(); var ix = (int[])input.indices.Clone();
            var low = p[0]; var high = low;
            foreach (var point in p) { low = Vector3.Min(low, point); high = Vector3.Max(high, point); }
            var span = high - low;
            float extent = Mathf.Max(span.x, Mathf.Max(span.y, span.z)) + cell * .7f;
            // Native Clean uses area > extent²*FLT_EPSILON. Keep a margin so
            // fitting cannot create faces that the next native collapse deletes.
            float minCross = extent * extent * 4.76837158203125e-7f;
            report.meanQualityBefore = MeanQuality(p, ix);
            var source = new Source(sourcePositions, sourceIndices, token);
            var normals = MeshGeometry.AveragedNormals(p, ix, token);
            var fans = new List<int>[p.Length]; var neighbours = new SortedSet<int>[p.Length];
            var locked = new bool[p.Length]; var feature = new bool[p.Length];
            for (int v = 0; v < p.Length; v++) { fans[v] = new List<int>(); neighbours[v] = new SortedSet<int>(); }
            for (int f = 0; f < ix.Length / 3; f++)
                for (int k = 0; k < 3; k++) {
                    int v = ix[f * 3 + k]; fans[v].Add(f);
                    neighbours[v].Add(ix[f * 3 + (k + 1) % 3]); neighbours[v].Add(ix[f * 3 + (k + 2) % 3]);
                }
            var boundarySlots = new HashSet<int>();
            foreach (var pair in before.edges)
                if (pair.Value.count != 2) { boundarySlots.Add(pair.Key.Item1); boundarySlots.Add(pair.Key.Item2); }
            for (int v = 0; v < p.Length; v++) locked[v] = boundarySlots.Contains(before.slots[v]);
            for (int pass = 0; pass < 3; pass++)
                for (int v = 0; v < p.Length; v++) {
                    if ((v & 255) == 0) token.ThrowIfCancellationRequested();
                    if (locked[v] || fans[v].Count == 0) continue;
                    var hit = source.Nearest(p[v], normals[v], cell * 1.5f);
                    if (hit.triangleIndex < 0) continue; // Preserve the back of a thin sheet.
                    Vector3 target;
                    if (pass == 0) {
                        target = hit.point;
                        feature[v] = source.FeaturePoint(hit.triangleIndex, p[v], cell * .2f, out var crease);
                        if (feature[v]) { target = crease; report.features++; }
                    }
                    else {
                        if (feature[v]) continue;
                        // A smooth source normal neighbourhood only: curved features
                        // and sharp transitions must not be rounded by a centroid move.
                        bool smooth = true; var center = Vector3.zero;
                        foreach (int n in neighbours[v]) {
                            center += p[n];
                            var other = source.Nearest(p[n], normals[n], cell * 1.5f);
                            if (other.triangleIndex < 0 || Vector3.Dot(source.normals[hit.triangleIndex], source.normals[other.triangleIndex]) < .9f) smooth = false;
                        }
                        if (!smooth) continue;
                        var displacement = center / neighbours[v].Count - p[v];
                        var normal = source.normals[hit.triangleIndex];
                        displacement -= normal * Vector3.Dot(normal, displacement);
                        var seed = p[v] + Vector3.ClampMagnitude(displacement * .25f, cell * .1f);
                        var next = source.Nearest(seed, normal, cell * 1.5f);
                        if (next.triangleIndex < 0) continue;
                        target = next.point;
                    }
                    if (Move(p, ix, v, target, input.positions[v], fans[v], cell, minCross, pass > 0)) report.moves++;
                }
            for (int pass = 0; pass < 4; pass++) report.flips += Flip(p, ix, source, cell, minCross, token);
            bool surfaceValid = PreservesSurface(source.bvh, sourcePositions, sourceIndices, input.positions, input.indices, p, ix, cell, token);
            if (!surfaceValid) {
                // Keep a useful diagonal change even if the vertex redistribution
                // loses source detail. Re-evaluate it with the original positions.
                p = (Vector3[])input.positions.Clone(); ix = (int[])input.indices.Clone();
                report.moves = 0; report.features = 0; report.flips = 0;
                for (int pass = 0; pass < 4; pass++) report.flips += Flip(p, ix, source, cell, minCross, token);
                surfaceValid = PreservesSurface(source.bvh, sourcePositions, sourceIndices, input.positions, input.indices, p, ix, cell, token);
            }
            var after = RemeshTopology.Inspect(p, ix, token);
            if (!after.Valid || !after.PreservesBoundary(before) || !after.PreservesComponents(before, false) ||
                !surfaceValid) {
                report.reverted = true;
                report.meanQualityAfter = report.meanQualityBefore;
                return input;
            }
            for (int v = 0; v < p.Length; v++) report.maxDisplacement = Mathf.Max(report.maxDisplacement, (p[v] - input.positions[v]).magnitude);
            report.meanQualityAfter = MeanQuality(p, ix);
            if (report.moves == 0 && report.flips == 0) return input;
            return new RemeshNative.IndexedMesh { positions = p, indices = ix }.PrepareChannels(token);
        }

        static bool Move(Vector3[] p, int[] ix, int v, Vector3 target, Vector3 origin, List<int> faces, float cell, float minCross, bool improve)
        {
            var old = p[v];
            target = origin + Vector3.ClampMagnitude(target - origin, cell * .35f);
            if ((target - old).sqrMagnitude < cell * cell * 1e-12f) return false;
            float oldMin = 1, oldSum = 0;
            var crosses = new Vector3[faces.Count];
            for (int j = 0; j < faces.Count; j++) {
                int f = faces[j]; crosses[j] = Cross(p, ix, f);
                float q = Quality(p[ix[f * 3]], p[ix[f * 3 + 1]], p[ix[f * 3 + 2]]);
                oldMin = Mathf.Min(oldMin, q); oldSum += q;
            }
            for (int attempt = 0; attempt < 5; attempt++) {
                p[v] = old + (target - old) * (1f / (1 << attempt));
                bool valid = true; float min = 1, sum = 0;
                for (int j = 0; j < faces.Count; j++) {
                    int f = faces[j]; var cross = Cross(p, ix, f); var previous = crosses[j];
                    if (Vector3.Dot(cross, previous) <= .5f * cross.magnitude * previous.magnitude || cross.sqrMagnitude < previous.sqrMagnitude * .0625f || cross.magnitude < minCross) valid = false;
                    float q = Quality(p[ix[f * 3]], p[ix[f * 3 + 1]], p[ix[f * 3 + 2]]);
                    min = Mathf.Min(min, q); sum += q;
                }
                if (valid && min >= oldMin * .5f && (!improve || min > oldMin * 1.001f && sum >= oldSum)) return true;
            }
            p[v] = old;
            return false;
        }

        static int Flip(Vector3[] p, int[] ix, Source source, float cell, float minCross, CancellationToken token)
        {
            var data = RemeshTopology.Inspect(p, ix, token);
            var used = new bool[ix.Length / 3]; int flips = 0;
            var edges = new List<KeyValuePair<(int, int), RemeshTopology.Edge>>(data.edges);
            foreach (var pair in edges) {
                if ((flips & 255) == 0) token.ThrowIfCancellationRequested();
                var edge = pair.Value;
                if (edge.count != 2 || used[edge.firstFace] || used[edge.secondFace]) continue;
                int f = edge.firstFace, g = edge.secondFace;
                int first = DirectedCorner(ix, f, pair.Key, data.slots);
                int a = ix[first], b = ix[f * 3 + (first + 1) % 3], c = ix[f * 3 + (first + 2) % 3];
                int d = -1;
                for (int k = 0; k < 3; k++) if (ix[g * 3 + k] != a && ix[g * 3 + k] != b) d = ix[g * 3 + k];
                if (d < 0 || data.edges.ContainsKey(Key(data.slots[c], data.slots[d]))) continue;
                var n0 = MeshGeometry.UnitDirection(Cross(p, ix, f)); var n1 = MeshGeometry.UnitDirection(Cross(p, ix, g));
                if (Vector3.Dot(n0, n1) < .9f) continue;
                var midpoint = (p[a] + p[b]) * .5f;
                var seam = source.Nearest(midpoint, MeshGeometry.UnitDirection(n0 + n1), cell * 1.5f);
                if (seam.triangleIndex >= 0 && source.FeaturePoint(seam.triangleIndex, midpoint, cell * .025f, out _)) continue;
                float before = Mathf.Min(Quality(p[a], p[b], p[c]), Quality(p[b], p[a], p[d]));
                float after = Mathf.Min(Quality(p[c], p[d], p[b]), Quality(p[d], p[c], p[a]));
                if (after <= before * 1.02f) continue;
                var cross0 = Vector3.Cross(p[d] - p[c], p[b] - p[c]); var cross1 = Vector3.Cross(p[c] - p[d], p[a] - p[d]);
                if (cross0.magnitude < minCross || cross1.magnitude < minCross) continue;
                var m0 = MeshGeometry.UnitDirection(cross0); var m1 = MeshGeometry.UnitDirection(cross1);
                if (Vector3.Dot(m0, n0) < .9f || Vector3.Dot(m1, n1) < .9f) continue;
                var s0 = source.Nearest((p[a] + p[b] + p[c]) / 3, n0, cell * 1.5f);
                var s1 = source.Nearest((p[b] + p[a] + p[d]) / 3, n1, cell * 1.5f);
                if (s0.triangleIndex < 0 || s1.triangleIndex < 0 || Vector3.Dot(source.normals[s0.triangleIndex], source.normals[s1.triangleIndex]) < .95f) continue;
                float baseline = Mathf.Max(SurfaceError(source, p[a], p[b], p[c], n0, cell), SurfaceError(source, p[b], p[a], p[d], n1, cell));
                float candidate = Mathf.Max(SurfaceError(source, p[c], p[d], p[b], m0, cell), SurfaceError(source, p[d], p[c], p[a], m1, cell));
                if (candidate > baseline + cell * .025f || float.IsInfinity(candidate)) continue;
                ix[f * 3] = c; ix[f * 3 + 1] = d; ix[f * 3 + 2] = b;
                ix[g * 3] = d; ix[g * 3 + 1] = c; ix[g * 3 + 2] = a;
                used[f] = used[g] = true;
                data.edges[Key(data.slots[c], data.slots[d])] = edge;
                flips++;
            }
            return flips;
        }

        static int DirectedCorner(int[] ix, int face, (int, int) edge, int[] slots)
        {
            for (int k = 0; k < 3; k++) {
                int corner = face * 3 + k, next = face * 3 + (k + 1) % 3;
                if (Key(slots[ix[corner]], slots[ix[next]]) == edge) return corner;
            }
            throw new InvalidOperationException("Missing refinement edge.");
        }

        static float SurfaceError(Source source, Vector3 a, Vector3 b, Vector3 c, Vector3 normal, float cell)
        {
            float error = 0;
            foreach (var point in new[] { (a + b) * .5f, (b + c) * .5f, (c + a) * .5f, (a + b + c) / 3 }) {
                var hit = source.Nearest(point, normal, cell * 2);
                if (hit.triangleIndex < 0) return float.PositiveInfinity;
                error = Mathf.Max(error, Mathf.Sqrt(hit.distSq));
            }
            return error;
        }

        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
        static Vector3 Cross(Vector3[] p, int[] ix, int f)
            => Vector3.Cross(p[ix[f * 3 + 1]] - p[ix[f * 3]], p[ix[f * 3 + 2]] - p[ix[f * 3]]);
        internal static float Quality(Vector3 a, Vector3 b, Vector3 c)
        {
            float sum = (b - a).sqrMagnitude + (c - b).sqrMagnitude + (a - c).sqrMagnitude;
            return sum > 0 ? 2 * Mathf.Sqrt(3) * Vector3.Cross(b - a, c - a).magnitude / sum : 0;
        }
        static float MeanQuality(Vector3[] p, int[] ix)
        {
            double total = 0;
            for (int f = 0; f < ix.Length; f += 3) total += Quality(p[ix[f]], p[ix[f + 1]], p[ix[f + 2]]);
            return (float)(total / (ix.Length / 3));
        }

        static bool PreservesSurface(TriangleBvh bvh, Vector3[] sourcePositions, int[] sourceIndices,
            Vector3[] before, int[] oldIndices, Vector3[] after, int[] newIndices, float cell, CancellationToken token)
        {
            var oldError = SampleError(bvh, before, oldIndices, token);
            var newError = SampleError(bvh, after, newIndices, token);
            if (!Within(oldError, newError, cell)) return false;
            // The reverse probes catch lost source protrusions that a one-way
            // target-to-source distance could hide on an otherwise flat patch.
            oldError = SampleError(new TriangleBvh(before, oldIndices), sourcePositions, sourceIndices, token, true);
            newError = SampleError(new TriangleBvh(after, newIndices), sourcePositions, sourceIndices, token, true);
            return Within(oldError, newError, cell);
        }

        static bool Within((double rms, double max) before, (double rms, double max) after, float cell)
            => after.rms <= Math.Max(before.rms * 1.05, before.rms + cell * .005) && after.max <= before.max + cell * .025;

        // Area-weighted centroid/edge-midpoint probes. This is a surface-error
        // guard, not a certified Hausdorff bound or a 3D intersection test.
        static (double rms, double max) SampleError(TriangleBvh bvh, Vector3[] p, int[] ix, CancellationToken token, bool centroidsOnly = false)
        {
            double area = 0, sum = 0, max = 0;
            for (int f = 0; f < ix.Length; f += 3) {
                if ((f & 255) == 0) token.ThrowIfCancellationRequested();
                var a = p[ix[f]]; var b = p[ix[f + 1]]; var c = p[ix[f + 2]];
                double weight = Vector3.Cross(b - a, c - a).magnitude;
                var probes = centroidsOnly ? new[] { (a + b + c) / 3 } : new[] { (a + b) * .5f, (b + c) * .5f, (c + a) * .5f, (a + b + c) / 3 };
                foreach (var point in probes) {
                    double distance2 = bvh.FindNearest(point).distSq;
                    area += weight; sum += weight * distance2; max = Math.Max(max, distance2);
                }
            }
            return (Math.Sqrt(sum / area), Math.Sqrt(max));
        }
    }
}
