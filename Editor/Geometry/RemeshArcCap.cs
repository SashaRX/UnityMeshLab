using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Bounded disk decomposition by continuous planar arcs, including
    /// parallel planes. No inferred apex, donor movement or triangular fan fallback.</summary>
    internal static class RemeshArcCap
    {
        sealed class Search
        {
            internal Vector3[] positions;
            internal CancellationToken token;
            internal double tolerance;
            internal int fits, samples;
            readonly Dictionary<string, List<List<List<int>>>> memo = new Dictionary<string, List<List<List<int>>>>();

            bool Planar(List<int> arc)
            {
                token.ThrowIfCancellationRequested();
                if (++fits > 32768 || (samples += arc.Count) > 2000000)
                    throw Refuse("plane search budget exceeded; no partial result was accepted");
                return RemeshCapPlanes.TryPlane(positions, arc, -1, false, out _, tolerance);
            }

            internal List<List<List<int>>> Plans(List<int> ring)
            {
                token.ThrowIfCancellationRequested();
                int start = 0;
                for (int i = 1; i < ring.Count; ++i) if (ring[i] < ring[start]) start = i;
                var loop = Arc(ring, start, ring.Count - 1);
                string key = string.Join(",", loop);
                if (memo.TryGetValue(key, out var cached)) return cached;
                if (memo.Count >= 2048) throw Refuse("contour search budget exceeded; no partial result was accepted");
                var result = new List<List<List<int>>>();
                memo.Add(key, result);
                // Four supported vertices are required even for the last patch.
                // Otherwise every warped contour could become arbitrary triangles.
                if (loop.Count < 4) return result;
                if (Planar(loop)) { result.Add(new List<List<int>> {loop}); return result; }
                int minimum = int.MaxValue;
                for (int a = 0; a < loop.Count; ++a) for (int length = 3; length <= loop.Count - 3; ++length) {
                    var arc = Arc(loop, a, length);
                    if (!Planar(arc)) continue;
                    var remaining = new List<int> {arc[0]};
                    for (int i = length; i < loop.Count; ++i) remaining.Add(loop[(a + i) % loop.Count]);
                    foreach (var suffix in Plans(remaining)) {
                        int count = suffix.Count + 1;
                        if (count > minimum) continue;
                        if (count < minimum) { result.Clear(); minimum = count; }
                        if (result.Count >= 128) throw Refuse("decomposition budget exceeded; no partial result was accepted");
                        var plan = new List<List<int>> {arc}; plan.AddRange(suffix); result.Add(plan);
                    }
                }
                return result;
            }

            static List<int> Arc(List<int> loop, int start, int length)
            {
                var result = new List<int>(length + 1);
                for (int i = 0; i <= length; ++i) result.Add(loop[(start + i) % loop.Count]);
                return result;
            }
        }

        internal static RemeshCompoundCap.Result Generate(Vector3[] p, int[] source, List<int> loop,
            CancellationToken token, ref int trials, double planeTolerance, RemeshPlanarCap.ExternalContacts external)
        {
            token.ThrowIfCancellationRequested();
            var plans = new Search {positions = p, token = token, tolerance = planeTolerance}.Plans(loop);
            if (plans.Count == 0) throw Refuse("no complete planar arc decomposition at the configured tolerance " +
                planeTolerance.ToString("G6", CultureInfo.InvariantCulture));
            var exact = new RemeshCapIntersection.Q[p.Length][];
            for (int i = 0; i < p.Length; ++i) exact[i] = RemeshCapIntersection.Point(p[i]);
            RemeshCompoundCap.Result winner = null;
            RemeshPlanarCap.ExternalContacts winnerContacts = null;
            string signature = null, lastFailure = null;
            foreach (var plan in plans) {
                token.ThrowIfCancellationRequested();
                var assembled = new List<int>(source);
                var stats = new RemeshPlanarCap.Support {originalFaces = source.Length / 3};
                var contacts = external?.Fork();
                var candidate = new RemeshCompoundCap.Result {positions = p};
                var remaining = loop;
                try {
                    for (int i = 0; i < plan.Count; ++i) {
                        bool closed = i == plan.Count - 1;
                        var arc = closed ? remaining : plan[i];
                        int oldCount = assembled.Count;
                        RemeshPlanarCap.AppendPatch(p, exact, assembled, arc, closed, token, stats, ref trials, planeTolerance, contacts);
                        candidate.patches.Add(assembled.GetRange(oldCount, assembled.Count - oldCount).ToArray());
                        if (!closed) remaining = RemeshPlanarCap.RemainingContour(p, assembled, arc, token);
                    }
                }
                catch (InvalidOperationException ex) when (!ex.Message.Contains("budget")) { lastFailure = ex.Message; continue; }
                // Orders of independent patches can differ, but a different audited
                // triangulated surface is an ambiguity, not permission to pick one.
                string candidateSignature = Signature(assembled, source.Length);
                if (winner != null && signature != candidateSignature) throw Refuse("multiple audited planar arc surfaces");
                if (winner == null) {
                    candidate.contacts = stats.contactTests;
                    winner = candidate; signature = candidateSignature; winnerContacts = contacts;
                }
            }
            if (winner == null) throw Refuse("no audited planar arc decomposition" + (lastFailure == null ? "" : ": " + lastFailure));
            external?.Merge(winnerContacts);
            return winner;
        }

        static string Signature(List<int> indices, int originalIndices)
        {
            var faces = new List<string>();
            for (int i = originalIndices; i < indices.Count; i += 3) {
                var triangle = new[] {indices[i], indices[i + 1], indices[i + 2]}; Array.Sort(triangle);
                faces.Add(string.Join(",", triangle));
            }
            faces.Sort(StringComparer.Ordinal);
            return string.Join(";", faces);
        }

        static InvalidOperationException Refuse(string reason) => new InvalidOperationException("Local arc Cap refused: " + reason + ". Source donors were preserved.");
    }
}
