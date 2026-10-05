using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Fits voxel vertices and chooses source-aligned diagonals before and after decimation.</summary>
    internal static class RemeshSurfaceRefine
    {
        internal struct Report
        {
            internal int moves, flips, features;
            internal int attemptedMoves, attemptedFeatures;
            internal int motionBacktracks;
            internal float meanQualityBefore, meanQualityAfter, maxDisplacement;
            internal float attemptedMaxDisplacement, motionScale;
            internal bool reverted, movementFallback;
            internal string fallbackReason, rejectionReason, backtrackReason;
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
            float triangleLimit = Math.Max(baseline.TriangleCount * 1.1f, baseline.TriangleCount + 2);
            bool exceedsTriangleBudget = candidate.TriangleCount > triangleLimit;
            string collapseReason = null;
            if (exceedsTriangleBudget ||
                !PreservesSurface(bvh, sourcePositions, sourceIndices, baseline.positions, baseline.indices, candidate.positions, candidate.indices, cell, token, out collapseReason)) {
                if (exceedsTriangleBudget) collapseReason = $"triangle count {candidate.TriangleCount} exceeds {triangleLimit:G6} (ordinary {baseline.TriangleCount})";
                UvtLog.Info(UvtLog.Category.RemeshDiag, $"Source surface refinement rejected its collapse candidate ({collapseReason}): " +
                    "retaining ordinary Simplify; pre-refinement changes discarded before final triangle refinement.");
                candidate = baseline; error = baselineError;
            }
            var result = Apply(candidate, sourcePositions, sourceIndices, cell, token, out var post);
            Log(post, cell, "after simplify");
            var aligned = Retriangulate(result, sourcePositions, sourceIndices, cell, token, out var triangles);
            Log(triangles, cell, "source-aligned triangulation");
            aligned = FitCoarse(aligned, sourcePositions, sourceIndices, cell, token, out var fit);
            Log(fit, cell, "coarse source fit");
            if (fit.moves == 0 || fit.reverted) return aligned;
            var regularized = RegularizeFitted(aligned, sourcePositions, sourceIndices, cell, token, out var shape);
            Log(shape, cell, "fitted triangle regularization");
            return regularized;
        }

        static void Log(Report report, float cell, string phase)
            => UvtLog.Info(UvtLog.Category.RemeshDiag, $"Source surface refinement {phase}: {report.moves} vertex moves, {report.flips} edge flips, " +
                $"{report.features} feature anchors; triangle quality {report.meanQualityBefore:F3} → {report.meanQualityAfter:F3}; " +
                $"max displacement {report.maxDisplacement:G4} ({report.maxDisplacement / cell:F3} cells); attempted {report.attemptedMoves} vertex moves, " +
                $"{report.attemptedFeatures} feature anchors, max {report.attemptedMaxDisplacement / cell:F3} cells" +
                $"; motion scale {report.motionScale:G3}, backtrack trials {report.motionBacktracks}" +
                (report.movementFallback ? $"; flips-only fallback ({report.fallbackReason})" :
                    report.motionBacktracks > 0 ? $"; reduced motion accepted; full motion rejected ({report.fallbackReason})" : "") +
                (report.movementFallback && report.motionBacktracks > 0 ? $"; last reduced-motion rejection ({report.backtrackReason})" : "") +
                (report.reverted ? $"; reverted ({report.rejectionReason})" : "") + ". " +
                "Simplify error measures native collapse only.");

        sealed class Source
        {
            internal readonly TriangleBvh bvh;
            internal readonly Vector3[] normals, positions;
            internal readonly int[] indices;
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
            report.attemptedMoves = report.moves; report.attemptedFeatures = report.features;
            for (int v = 0; v < p.Length; v++)
                report.attemptedMaxDisplacement = Mathf.Max(report.attemptedMaxDisplacement, (p[v] - input.positions[v]).magnitude);
            report.motionScale = report.moves > 0 ? 1f : 0f;
            bool surfaceValid = PreservesSurface(source.bvh, sourcePositions, sourceIndices, input.positions, input.indices, p, ix, cell, token, out var surfaceReason);
            if (!surfaceValid) {
                report.fallbackReason = surfaceReason;
                var fitted = p;
                surfaceValid = report.attemptedMaxDisplacement > 0 && BacktrackMotion(input, fitted, source, cell, minCross, before, token,
                    ref report, out p, out ix);
                if (!surfaceValid) {
                    report.movementFallback = true;
                    // Keep useful diagonal changes if every bounded motion trial
                    // loses source detail. Start again from the original positions.
                    p = (Vector3[])input.positions.Clone(); ix = (int[])input.indices.Clone();
                    report.moves = 0; report.features = 0; report.flips = 0; report.motionScale = 0;
                    for (int pass = 0; pass < 4; pass++) report.flips += Flip(p, ix, source, cell, minCross, token);
                    surfaceValid = PreservesSurface(source.bvh, sourcePositions, sourceIndices, input.positions, input.indices, p, ix, cell, token, out surfaceReason);
                }
            }
            var after = RemeshTopology.Inspect(p, ix, token);
            if (!after.Valid || !after.PreservesBoundary(before) || !after.PreservesComponents(before, false) ||
                !surfaceValid) {
                report.reverted = true;
                report.rejectionReason = !surfaceValid ? surfaceReason : $"topology: {after.Description}; " +
                    $"boundary preserved {after.PreservesBoundary(before)}, component topology preserved {after.PreservesComponents(before, false)}";
                report.meanQualityAfter = report.meanQualityBefore;
                return input;
            }
            for (int v = 0; v < p.Length; v++) report.maxDisplacement = Mathf.Max(report.maxDisplacement, (p[v] - input.positions[v]).magnitude);
            report.meanQualityAfter = MeanQuality(p, ix);
            if (report.moves == 0 && report.flips == 0) return input;
            return new RemeshNative.IndexedMesh { positions = p, indices = ix }.PrepareChannels(token);
        }

        static bool BacktrackMotion(RemeshNative.IndexedMesh input, Vector3[] fitted, Source source, float cell, float minCross,
            RemeshTopology.Snapshot before, CancellationToken token, ref Report report, out Vector3[] positions, out int[] indices)
        {
            positions = null; indices = null;
            // Rebuild every trial from the same snapshot. Reusing rejected flips
            // would combine unrelated topology and motion decisions.
            for (int trial = 1; trial <= 3; trial++) {
                token.ThrowIfCancellationRequested(); report.motionBacktracks++;
                float factor = 1f / (1 << trial);
                var p = new Vector3[input.positions.Length];
                for (int v = 0; v < p.Length; v++) {
                    if ((v & 1023) == 0) token.ThrowIfCancellationRequested();
                    p[v] = input.positions[v] + (fitted[v] - input.positions[v]) * factor;
                }
                if (!SafeCollectiveMotion(input, p, minCross, token)) {
                    report.backtrackReason = "original triangle orientation/area gate";
                    continue;
                }
                var ix = (int[])input.indices.Clone(); int flips = 0;
                for (int pass = 0; pass < 4; pass++) flips += Flip(p, ix, source, cell, minCross, token);
                if (!PreservesSurface(source.bvh, source.positions, source.indices, input.positions, input.indices, p, ix, cell, token, out var reason)) {
                    report.backtrackReason = reason;
                    continue;
                }
                var after = RemeshTopology.Inspect(p, ix, token);
                if (!after.Valid || !after.PreservesBoundary(before) || !after.PreservesComponents(before, false)) {
                    report.backtrackReason = $"topology: {after.Description}; boundary/component preservation failed";
                    continue;
                }
                report.moves = 0;
                for (int v = 0; v < p.Length; v++) if ((p[v] - input.positions[v]).sqrMagnitude > 0) report.moves++;
                report.features = 0; // A blended feature target is not an exact anchor.
                report.flips = flips; report.motionScale = factor;
                positions = p; indices = ix;
                return true;
            }
            return false;
        }

        static bool SafeCollectiveMotion(RemeshNative.IndexedMesh input, Vector3[] p, float minCross, CancellationToken token)
        {
            for (int f = 0; f < input.TriangleCount; f++) {
                if ((f & 1023) == 0) token.ThrowIfCancellationRequested();
                var previous = Cross(input.positions, input.indices, f);
                var cross = Cross(p, input.indices, f); float length = cross.magnitude, originalLength = previous.magnitude;
                // Native-clean input can contain valid faces below the extra fitting
                // margin. An unchanged face must pass; such a face may not shrink.
                float areaFloor = Mathf.Min(minCross, originalLength);
                if (!(length >= areaFloor) || float.IsInfinity(length) ||
                    Vector3.Dot(cross, previous) <= .5f * length * originalLength ||
                    cross.sqrMagnitude < previous.sqrMagnitude * .0625f) return false;
            }
            return true;
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

        // Relocate coarse vertices by the error of their whole one-ring, rather
        // than projecting each vertex independently and accepting all motion at once.
        internal static RemeshNative.IndexedMesh FitCoarse(RemeshNative.IndexedMesh input,
            Vector3[] sourcePositions, int[] sourceIndices, float cell, CancellationToken token, out Report report)
        {
            token.ThrowIfCancellationRequested(); report = default;
            if (!(cell > 0) || !float.IsFinite(cell) || input.TriangleCount == 0 || sourceIndices.Length == 0) return input;
            var before = RemeshTopology.Inspect(input.positions, input.indices, token);
            if (!before.Valid || new HashSet<Vector3>(input.positions).Count != input.positions.Length) return input;
            var p = (Vector3[])input.positions.Clone(); var ix = input.indices;
            var source = new Source(sourcePositions, sourceIndices, token);
            var fans = new List<int>[p.Length]; var neighbours = new SortedSet<int>[p.Length];
            var locked = new bool[p.Length];
            for (int v = 0; v < p.Length; v++) { fans[v] = new List<int>(); neighbours[v] = new SortedSet<int>(); }
            for (int f = 0; f < input.TriangleCount; f++) for (int k = 0; k < 3; k++) {
                int v = ix[f * 3 + k]; fans[v].Add(f);
                neighbours[v].Add(ix[f * 3 + (k + 1) % 3]); neighbours[v].Add(ix[f * 3 + (k + 2) % 3]);
            }
            foreach (var edge in before.edges) if (edge.Value.count != 2) {
                locked[edge.Key.Item1] = true; locked[edge.Key.Item2] = true;
            }
            report.meanQualityBefore = MeanQuality(p, ix);
            for (int pass = 0; pass < 3; pass++) {
                var normals = MeshGeometry.AveragedNormals(p, ix, token);
                for (int v = 0; v < p.Length; v++) {
                    if ((v & 63) == 0) token.ThrowIfCancellationRequested();
                    if (locked[v] || fans[v].Count == 0) continue;
                    float shortest = float.PositiveInfinity;
                    foreach (int n in neighbours[v]) shortest = Mathf.Min(shortest, (p[v] - p[n]).magnitude);
                    // Dense voxel triangles are handled by Apply; this pass is for
                    // the larger approximation patches left by decimation.
                    if (shortest < cell * 2) continue;
                    float budget = Mathf.Min(cell * 4, shortest * .25f), reach = Mathf.Max(cell * 4, shortest * .5f);
                    var hit = source.Nearest(p[v], normals[v], reach);
                    if (hit.triangleIndex < 0) continue;
                    // Keep existing sharp source anchors. A one-ring error decrease
                    // alone could otherwise slide a corner along another surface.
                    if (source.FeaturePoint(hit.triangleIndex, p[v], cell * .2f, out _)) continue;
                    var original = p[v];
                    var baseline = RingError(source, p, ix, fans[v], reach);
                    if (!double.IsFinite(baseline.meanSquared)) continue;
                    var best = baseline; var bestPoint = original;
                    var candidates = new[] { hit.point,
                        original + Vector3.right * budget, original - Vector3.right * budget,
                        original + Vector3.up * budget, original - Vector3.up * budget,
                        original + Vector3.forward * budget, original - Vector3.forward * budget };
                    foreach (var candidate in candidates) {
                        var projected = source.Nearest(candidate, normals[v], reach);
                        if (projected.triangleIndex < 0) continue;
                        var target = input.positions[v] + Vector3.ClampMagnitude(projected.point - input.positions[v], budget);
                        for (int trial = 0; trial < 3; trial++) {
                            var next = original + (target - original) * (1f / (1 << trial));
                            if ((next - original).sqrMagnitude < cell * cell * 1e-10f ||
                                !SafeRingMotion(p, ix, v, next, fans[v])) continue;
                            p[v] = next;
                            var vertexHit = source.Nearest(next, normals[v], reach);
                            var measured = RingError(source, p, ix, fans[v], reach);
                            p[v] = original;
                            if (vertexHit.triangleIndex < 0 || Mathf.Sqrt(vertexHit.distSq) > Mathf.Max(Mathf.Sqrt(hit.distSq) + cell * .1f, cell * .5f) ||
                                measured.max > baseline.max + cell * .005f || measured.meanSquared >= best.meanSquared * .99) continue;
                            best = measured; bestPoint = next;
                        }
                    }
                    if (bestPoint == original) continue;
                    p[v] = bestPoint; report.moves++;
                }
            }
            report.attemptedMoves = report.moves;
            for (int v = 0; v < p.Length; v++) report.attemptedMaxDisplacement = Mathf.Max(report.attemptedMaxDisplacement, (p[v] - input.positions[v]).magnitude);
            report.meanQualityAfter = MeanQuality(p, ix);
            if (report.moves == 0) return input;
            var after = RemeshTopology.Inspect(p, ix, token);
            bool surface = PreservesSurface(source.bvh, sourcePositions, sourceIndices, input.positions, ix, p, ix, cell, token, out var reason);
            if (!after.Valid || !after.PreservesBoundary(before) || !after.PreservesComponents(before, false) || !surface) {
                report.reverted = true; report.rejectionReason = !surface ? reason : after.Description;
                report.moves = 0; report.meanQualityAfter = report.meanQualityBefore; return input;
            }
            report.maxDisplacement = report.attemptedMaxDisplacement; report.motionScale = 1;
            return new RemeshNative.IndexedMesh { positions = p, indices = (int[])ix.Clone() }.PrepareChannels(token);
        }

        static (double meanSquared, float max) RingError(Source source, Vector3[] p, int[] ix, List<int> faces, float reach)
        {
            double sum = 0, area = 0; float max = 0;
            foreach (int f in faces) {
                var a = p[ix[f * 3]]; var b = p[ix[f * 3 + 1]]; var c = p[ix[f * 3 + 2]];
                var cross = Vector3.Cross(b - a, c - a); double weight = cross.magnitude;
                var error = SurfaceError(source, a, b, c, MeshGeometry.UnitDirection(cross), reach);
                sum += error.meanSquared * weight; area += weight; max = Mathf.Max(max, error.max);
            }
            return (sum / area, max);
        }

        static bool SafeRingMotion(Vector3[] p, int[] ix, int v, Vector3 target, List<int> faces)
        {
            var previous = p[v]; bool safe = true;
            foreach (int f in faces) {
                var oldCross = Cross(p, ix, f);
                float quality = Quality(p[ix[f * 3]], p[ix[f * 3 + 1]], p[ix[f * 3 + 2]]);
                p[v] = target;
                var newCross = Cross(p, ix, f);
                float nextQuality = Quality(p[ix[f * 3]], p[ix[f * 3 + 1]], p[ix[f * 3 + 2]]);
                p[v] = previous;
                if (Vector3.Dot(oldCross, newCross) <= .75f * oldCross.magnitude * newCross.magnitude ||
                    newCross.sqrMagnitude < oldCross.sqrMagnitude * .64f || nextQuality < quality * .8f) { safe = false; break; }
            }
            return safe;
        }

        // This final pass changes connectivity only. Its acceptance must not
        // depend on a different accepted vertex-motion backtrack.
        internal static RemeshNative.IndexedMesh Retriangulate(RemeshNative.IndexedMesh input,
            Vector3[] sourcePositions, int[] sourceIndices, float cell, CancellationToken token, out Report report)
            => RetriangulateCore(input, sourcePositions, sourceIndices, cell, token, false, out report);

        internal static RemeshNative.IndexedMesh RegularizeFitted(RemeshNative.IndexedMesh input,
            Vector3[] sourcePositions, int[] sourceIndices, float cell, CancellationToken token, out Report report)
            => RetriangulateCore(input, sourcePositions, sourceIndices, cell, token, true, out report);

        static RemeshNative.IndexedMesh RetriangulateCore(RemeshNative.IndexedMesh input,
            Vector3[] sourcePositions, int[] sourceIndices, float cell, CancellationToken token, bool regularize, out Report report)
        {
            token.ThrowIfCancellationRequested(); report = default;
            if (!(cell > 0) || !float.IsFinite(cell) || input.TriangleCount == 0 || sourceIndices.Length == 0) return input;
            var before = RemeshTopology.Inspect(input.positions, input.indices, token);
            if (!before.Valid || new HashSet<Vector3>(input.positions).Count != input.positions.Length) return input;
            var p = input.positions; var ix = (int[])input.indices.Clone();
            var low = p[0]; var high = low;
            foreach (var point in p) { low = Vector3.Min(low, point); high = Vector3.Max(high, point); }
            var span = high - low; float extent = Mathf.Max(span.x, Mathf.Max(span.y, span.z)) + cell * .7f;
            float minCross = extent * extent * 4.76837158203125e-7f;
            report.meanQualityBefore = MeanQuality(p, ix);
            var source = new Source(sourcePositions, sourceIndices, token);
            for (int pass = 0; pass < 4; pass++) report.flips += Flip(p, ix, source, cell, minCross, token, true, regularize);
            report.meanQualityAfter = MeanQuality(p, ix);
            if (report.flips == 0) return input;
            var after = RemeshTopology.Inspect(p, ix, token);
            bool surface = PreservesSurface(source.bvh, sourcePositions, sourceIndices, p, input.indices, p, ix, cell, token, out var reason);
            if (!after.Valid || !after.PreservesBoundary(before) || !after.PreservesComponents(before, false) || !surface) {
                report.reverted = true; report.rejectionReason = !surface ? reason : after.Description;
                report.meanQualityAfter = report.meanQualityBefore; return input;
            }
            return new RemeshNative.IndexedMesh { positions = (Vector3[])p.Clone(), indices = ix }.PrepareChannels(token);
        }

        static int Flip(Vector3[] p, int[] ix, Source source, float cell, float minCross, CancellationToken token, bool sourceAligned = false, bool regularize = false)
        {
            var data = RemeshTopology.Inspect(p, ix, token);
            var used = new bool[ix.Length / 3]; int flips = 0;
            var edges = new List<KeyValuePair<(int, int), RemeshTopology.Edge>>(data.edges);
            for (int e = 0; e < edges.Count; e++) {
                if ((e & 255) == 0) token.ThrowIfCancellationRequested();
                var pair = edges[e];
                var edge = pair.Value;
                if (edge.count != 2 || used[edge.firstFace] || used[edge.secondFace]) continue;
                int f = edge.firstFace, g = edge.secondFace;
                int first = DirectedCorner(ix, f, pair.Key, data.slots);
                int a = ix[first], b = ix[f * 3 + (first + 1) % 3], c = ix[f * 3 + (first + 2) % 3];
                int d = -1;
                for (int k = 0; k < 3; k++) if (ix[g * 3 + k] != a && ix[g * 3 + k] != b) d = ix[g * 3 + k];
                if (d < 0 || data.edges.ContainsKey(Key(data.slots[c], data.slots[d]))) continue;
                var n0 = MeshGeometry.UnitDirection(Cross(p, ix, f)); var n1 = MeshGeometry.UnitDirection(Cross(p, ix, g));
                // A decimated organic surface need not be almost planar. Keep
                // sharp folds, but let the source choose a diagonal on a bend.
                float normalLimit = sourceAligned ? .75f : .9f;
                if (Vector3.Dot(n0, n1) < normalLimit) continue;
                float reach = sourceAligned ? Mathf.Max(cell * 2, Mathf.Max((p[a] - p[b]).magnitude, (p[c] - p[d]).magnitude) * .5f) : cell * 1.5f;
                var midpoint = (p[a] + p[b]) * .5f;
                var seam = source.Nearest(midpoint, MeshGeometry.UnitDirection(n0 + n1), reach);
                if (seam.triangleIndex >= 0 && source.FeaturePoint(seam.triangleIndex, midpoint, cell * .025f, out _)) continue;
                float before = Mathf.Min(Quality(p[a], p[b], p[c]), Quality(p[b], p[a], p[d]));
                float after = Mathf.Min(Quality(p[c], p[d], p[b]), Quality(p[d], p[c], p[a]));
                // Never replace a usable patch with slivers for a small fit gain.
                if (sourceAligned ? after < Mathf.Min(.05f, before) || after < before * .8f : after <= before * 1.02f) continue;
                // On the dense voxel grid there is little curvature to recover
                // within one cell. Reserve the extra fit search for coarse edges.
                if (sourceAligned && after <= before * 1.02f && reach <= cell * 2) continue;
                var cross0 = Vector3.Cross(p[d] - p[c], p[b] - p[c]); var cross1 = Vector3.Cross(p[c] - p[d], p[a] - p[d]);
                if (cross0.magnitude < minCross || cross1.magnitude < minCross) continue;
                var m0 = MeshGeometry.UnitDirection(cross0); var m1 = MeshGeometry.UnitDirection(cross1);
                if (Vector3.Dot(m0, n0) < normalLimit || Vector3.Dot(m1, n1) < normalLimit || sourceAligned && Vector3.Dot(m0, m1) < .75f) continue;
                var s0 = source.Nearest((p[a] + p[b] + p[c]) / 3, n0, reach);
                var s1 = source.Nearest((p[b] + p[a] + p[d]) / 3, n1, reach);
                if (s0.triangleIndex < 0 || s1.triangleIndex < 0 || Vector3.Dot(source.normals[s0.triangleIndex], source.normals[s1.triangleIndex]) < (sourceAligned ? .75f : .95f)) continue;
                bool sourceBend = Vector3.Dot(source.normals[s0.triangleIndex], source.normals[s1.triangleIndex]) < .999f;
                if (sourceAligned && !sourceBend) {
                    // Centroids can both project onto the same side of a ridge.
                    // Also probe opposite interiors before calling it planar.
                    var left = source.Nearest(p[a] * .5f + (p[b] + p[c]) * .25f, n0, reach);
                    var right = source.Nearest(p[b] * .5f + (p[a] + p[d]) * .25f, n1, reach);
                    if (left.triangleIndex >= 0 && right.triangleIndex >= 0) {
                        float dot = Vector3.Dot(source.normals[left.triangleIndex], source.normals[right.triangleIndex]);
                        sourceBend = dot >= .75f && dot < .999f;
                    }
                }
                if (!ImprovesPatch(source, p, a, b, c, d, n0, n1, m0, m1, before, after, reach, cell, sourceAligned, sourceBend, regularize)) continue;
                ix[f * 3] = c; ix[f * 3 + 1] = d; ix[f * 3 + 2] = b;
                ix[g * 3] = d; ix[g * 3 + 1] = c; ix[g * 3 + 2] = a;
                used[f] = used[g] = true;
                data.edges[Key(data.slots[c], data.slots[d])] = edge;
                flips++;
            }
            return flips;
        }

        static bool ImprovesPatch(Source source, Vector3[] p, int a, int b, int c, int d, Vector3 n0, Vector3 n1, Vector3 m0, Vector3 m1,
            float before, float after, float reach, float cell, bool sourceAligned, bool sourceBend, bool regularize)
        {
            if (sourceAligned) {
                var baseline = PatchError(source, p[a], p[b], p[c], p[d], n0, n1, reach);
                var candidate = PatchError(source, p[c], p[d], p[b], p[a], m0, m1, reach);
                if (!float.IsFinite(candidate.max)) return false;
                // A large improvement to a poor triangle pair may spend a small,
                // sub-voxel approximation budget. Keeping every locally best-fit
                // diagonal can preserve decimation zigzags despite useful shape gains.
                // The whole-mesh, bidirectional error guard still applies afterward.
                bool regularized = regularize && before < .5f && after > before * 1.25f && candidate.max <= cell * .8f &&
                    candidate.max <= baseline.max + cell * .125f &&
                    Math.Sqrt(candidate.meanSquared) <= Math.Sqrt(baseline.meanSquared) + cell * .075f;
                if (candidate.max > baseline.max + cell * .025f) return regularized;
                // Fit has priority over triangle shape on curved patches. On a
                // flat patch, accept a shape gain only with essentially equal fit.
                double noise = (double)cell * cell * 1e-8;
                bool betterFit = sourceBend && candidate.meanSquared + noise < baseline.meanSquared * .9;
                bool betterShape = after > before * 1.02f && candidate.meanSquared <= baseline.meanSquared * 1.02 + noise;
                return betterFit || betterShape || regularized;
            }
            float oldError = Mathf.Max(CoarseSurfaceError(source, p[a], p[b], p[c], n0, cell), CoarseSurfaceError(source, p[b], p[a], p[d], n1, cell));
            float newError = Mathf.Max(CoarseSurfaceError(source, p[c], p[d], p[b], m0, cell), CoarseSurfaceError(source, p[d], p[c], p[a], m1, cell));
            return float.IsFinite(newError) && newError <= oldError + cell * .025f;
        }

        static float CoarseSurfaceError(Source source, Vector3 a, Vector3 b, Vector3 c, Vector3 normal, float cell)
        {
            float error = 0;
            foreach (var point in new[] { (a + b) * .5f, (b + c) * .5f, (c + a) * .5f, (a + b + c) / 3 }) {
                var hit = source.Nearest(point, normal, cell * 2);
                if (hit.triangleIndex < 0) return float.PositiveInfinity;
                error = Mathf.Max(error, Mathf.Sqrt(hit.distSq));
            }
            return error;
        }

        static int DirectedCorner(int[] ix, int face, (int, int) edge, int[] slots)
        {
            for (int k = 0; k < 3; k++) {
                int corner = face * 3 + k, next = face * 3 + (k + 1) % 3;
                if (Key(slots[ix[corner]], slots[ix[next]]) == edge) return corner;
            }
            throw new InvalidOperationException("Missing refinement edge.");
        }

        static (double meanSquared, float max) PatchError(Source source, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector3 n0, Vector3 n1, float reach)
        {
            var first = SurfaceError(source, a, b, c, n0, reach);
            var second = SurfaceError(source, a, b, d, n1, reach);
            double w0 = Vector3.Cross(b - a, c - a).magnitude, w1 = Vector3.Cross(a - b, d - b).magnitude;
            return ((first.meanSquared * w0 + second.meanSquared * w1) / (w0 + w1), Mathf.Max(first.max, second.max));
        }

        static (double meanSquared, float max) SurfaceError(Source source, Vector3 a, Vector3 b, Vector3 c, Vector3 normal, float reach)
        {
            double sum = 0; float maxSquared = 0;
            // Probe the diagonal along its length and the triangle interior;
            // one centroid can miss the bulge that a long chin edge cuts across.
            var points = new[] { (a + b) * .5f, a * .75f + b * .25f, a * .25f + b * .75f,
                (b + c) * .5f, b * .75f + c * .25f, b * .25f + c * .75f,
                (c + a) * .5f, c * .75f + a * .25f, c * .25f + a * .75f,
                (a + b + c) / 3, a * .5f + (b + c) * .25f, b * .5f + (a + c) * .25f, c * .5f + (a + b) * .25f };
            foreach (var point in points) {
                var hit = source.Nearest(point, normal, reach);
                if (hit.triangleIndex < 0) return (double.PositiveInfinity, float.PositiveInfinity);
                sum += hit.distSq; maxSquared = Mathf.Max(maxSquared, hit.distSq);
            }
            return (sum / points.Length, Mathf.Sqrt(maxSquared));
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
            Vector3[] before, int[] oldIndices, Vector3[] after, int[] newIndices, float cell, CancellationToken token, out string reason)
        {
            reason = null;
            var oldError = SampleError(bvh, before, oldIndices, token);
            var newError = SampleError(bvh, after, newIndices, token);
            if (!Within(oldError, newError, cell)) { reason = SurfaceRejection("target-to-source", oldError, newError, cell); return false; }
            // The reverse probes catch lost source protrusions that a one-way
            // target-to-source distance could hide on an otherwise flat patch.
            oldError = SampleError(new TriangleBvh(before, oldIndices), sourcePositions, sourceIndices, token, true);
            newError = SampleError(new TriangleBvh(after, newIndices), sourcePositions, sourceIndices, token, true);
            if (Within(oldError, newError, cell)) return true;
            reason = SurfaceRejection("source-to-target", oldError, newError, cell);
            return false;
        }

        static string SurfaceRejection(string direction, (double rms, double max) before, (double rms, double max) after, float cell)
        {
            double rmsLimit = Math.Max(before.rms * 1.05, before.rms + cell * .005);
            double maxLimit = before.max + cell * .025;
            string failed = after.rms > rmsLimit ? "RMS" : "";
            if (after.max > maxLimit) failed += failed.Length > 0 ? "+max" : "max";
            if (failed.Length == 0) failed = "non-finite samples";
            return $"{direction} {failed}: RMS {before.rms / cell:G6}→{after.rms / cell:G6} (limit {rmsLimit / cell:G6}), " +
                $"max {before.max / cell:G6}→{after.max / cell:G6} (limit {maxLimit / cell:G6}) cells";
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
