using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Separates overlapping chart branches by rotating about their sole shared UV vertex.</summary>
    internal static class UvPointJunctionRecovery
    {
        /// <summary>Returns a detached improvement, retaining the largest edge-connected patch and every shared vertex.</summary>
        internal static Dictionary<int, Vector2> TryRecover(UvShell shell, int[] indices, Vector3[] positions,
            Dictionary<int, Vector2> original, CancellationToken token, long comparisonBudget = 200000)
        {
            token.ThrowIfCancellationRequested();
            var patches = Patches(shell, indices, token);
            if (patches == null || patches.Count < 2) return null;
            var vertices = patches.Select(faces => new HashSet<int>(faces.SelectMany(f =>
                new[] { indices[f * 3], indices[f * 3 + 1], indices[f * 3 + 2] }))).ToArray();
            int main = 0;
            for (int i = 1; i < patches.Count; ++i)
                if (patches[i].Count > patches[main].Count) main = i;
            // Duplicated indices across normals/material boundaries can still
            // represent the same UV point. Keep those aliases anchored too.
            var counts = new Dictionary<(Vector3, Vector2), int>();
            foreach (var patch in vertices)
                foreach (var key in patch.Select(v => (positions[v], original[v])).Distinct())
                    counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
            // Only a branch held by exactly one shared point can rotate. Hard
            // normal edges split many charts into edge-aliased patches; those
            // never qualify, so skip their whole-chart contact scan.
            var anchors = new int[patches.Count];
            bool rotatable = false;
            for (int p = 0; p < patches.Count; ++p)
            {
                anchors[p] = -1;
                if (p == main) continue;
                var shared = vertices[p].Where(v => counts[(positions[v], original[v])] > 1).Take(2).ToArray();
                if (shared.Length != 1) continue;
                anchors[p] = shared[0];
                rotatable = true;
            }
            if (!rotatable)
            {
                // The skipped scan would have observed cancellation.
                token.ThrowIfCancellationRequested();
                return null;
            }
            var current = new Dictionary<int, Vector2>(original);
            var scan = Scan(shell, indices, current, token, ref comparisonBudget);
            if (!scan.complete || scan.pairs == 0 || scan.invalidFaces > 0 || scan.degenerateFaces > 0) return null;
            var allowedPairs = new HashSet<(int, int)>(scan.conflicts.Select(c => (c.a, c.b)));
            var turns = Enumerable.Range(1, 31).OrderBy(turn => turn % 8 == 0 ? 0 : 1).ThenBy(turn => turn).ToArray();
            bool changed = false;
            for (int p = 0; p < patches.Count; ++p)
            {
                token.ThrowIfCancellationRequested();
                if (anchors[p] < 0) continue;
                var faces = new HashSet<int>(patches[p]);
                bool? significant = SignificantContact(shell, indices, current, faces, scan, token, ref comparisonBudget);
                if (!significant.HasValue) return null;
                if (!significant.Value) continue;
                int anchor = anchors[p];
                var pivot = current[anchor];
                Dictionary<int, Vector2> best = null;
                var bestScan = scan;
                // Include exact quarter turns before finer angles. One rigid
                // transform covers every exclusive vertex of this branch.
                foreach (int turn in turns)
                {
                    token.ThrowIfCancellationRequested();
                    double angle = turn * Math.PI / 16;
                    double cos = Math.Cos(angle), sin = Math.Sin(angle);
                    if (turn % 8 == 0) { cos = Math.Round(cos); sin = Math.Round(sin); }
                    var candidate = new Dictionary<int, Vector2>(current);
                    foreach (int v in vertices[p])
                    {
                        if (v == anchor) continue;
                        double x = (double)current[v].x - pivot.x, y = (double)current[v].y - pivot.y;
                        candidate[v] = new Vector2((float)(pivot.x + cos * x - sin * y),
                            (float)(pivot.y + sin * x + cos * y));
                    }
                    if (!MetricsFit(patches[p], indices, positions, original, candidate)) continue;
                    var next = Scan(shell, indices, candidate, token, ref comparisonBudget);
                    if (!next.complete) return null;
                    if (next.invalidFaces > scan.invalidFaces || next.degenerateFaces > scan.degenerateFaces
                        || next.pairs >= bestScan.pairs || next.pairAreaSum > scan.pairAreaSum * 1.001 + 1e-14
                        || next.conflicts.Any(c => !allowedPairs.Contains((c.a, c.b)))) continue;
                    if (next.conflicts.Any(c => faces.Contains(shell.faceIndices[c.a]) != faces.Contains(shell.faceIndices[c.b]))) continue;
                    best = candidate; bestScan = next;
                    break;
                }
                if (best == null) continue;
                current = best; scan = bestScan; changed = true;
                if (scan.pairs == 0) break;
            }
            token.ThrowIfCancellationRequested();
            return changed ? current : null;
        }

        /// <summary>Leaves contacts within Float32 coordinate uncertainty to precision repair; null means the shared budget ran out.</summary>
        static bool? SignificantContact(UvShell shell, int[] indices, Dictionary<int, Vector2> uv, HashSet<int> branch,
            UvAtlasDiagnostics.Report scan, CancellationToken token, ref long budget)
        {
            foreach (var pair in scan.conflicts)
            {
                token.ThrowIfCancellationRequested();
                int a = shell.faceIndices[pair.a], b = shell.faceIndices[pair.b];
                if (branch.Contains(a) == branch.Contains(b)) continue;
                var faces = new List<int> { a, b };
                var contact = UvAtlasDiagnostics.Measure(UvAtlasDiagnostics.ShellGeometry(faces, indices, v => uv[v]),
                    token, comparisonBudget: Math.Max(0, budget));
                budget -= contact.comparisons;
                if (!contact.complete) return null;
                double coordinate = 0, perimeter = 0;
                foreach (int f in faces)
                    for (int k = 0; k < 3; ++k)
                    {
                        var p = uv[indices[f * 3 + k]];
                        var q = uv[indices[f * 3 + (k + 1) % 3]];
                        coordinate = Math.Max(coordinate, Math.Max(Math.Abs(p.x), Math.Abs(p.y)));
                        double x = (double)q.x - p.x, y = (double)q.y - p.y;
                        perimeter += Math.Sqrt(x * x + y * y);
                    }
                // A coordinate rounding band swept along the two perimeters.
                // This gates the repair, not the contact report: the sliver
                // remains visible and must not provoke a whole-branch rotation.
                if (contact.pairAreaSum > coordinate * perimeter * 1.1920928955078125e-7) return true;
            }
            return false;
        }

        /// <summary>Finds patches connected by full indexed edges, refusing nonmanifold edges.</summary>
        static List<List<int>> Patches(UvShell shell, int[] indices, CancellationToken token)
        {
            var sets = new DisjointSet(shell.faceIndices.Count);
            var edges = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < shell.faceIndices.Count; ++i)
            {
                token.ThrowIfCancellationRequested();
                int f = shell.faceIndices[i];
                for (int k = 0; k < 3; ++k)
                {
                    int a = indices[f * 3 + k], b = indices[f * 3 + (k + 1) % 3];
                    if (a == b) return null;
                    var key = a < b ? (a, b) : (b, a);
                    if (!edges.TryGetValue(key, out var uses)) { uses = new List<int>(); edges.Add(key, uses); }
                    uses.Add(i);
                    if (uses.Count > 2) return null;
                    if (uses.Count == 2) sets.Union(uses[0], i);
                }
            }
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < shell.faceIndices.Count; ++i)
            {
                int root = sets.Find(i);
                if (!groups.TryGetValue(root, out var faces)) { faces = new List<int>(); groups.Add(root, faces); }
                faces.Add(shell.faceIndices[i]);
            }
            return groups.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
        }

        /// <summary>Checks every moved face against its original metric and signed area after Float32 rounding.</summary>
        static bool MetricsFit(List<int> faces, int[] indices, Vector3[] positions,
            Dictionary<int, Vector2> original, Dictionary<int, Vector2> candidate)
        {
            foreach (int f in faces)
            {
                int a = indices[f * 3], b = indices[f * 3 + 1], c = indices[f * 3 + 2];
                double before = ReverseUvTransfer.TriangleAnisotropy(positions[a], positions[b], positions[c], original[a], original[b], original[c]);
                double after = ReverseUvTransfer.TriangleAnisotropy(positions[a], positions[b], positions[c], candidate[a], candidate[b], candidate[c]);
                double oldArea = Area(original[a], original[b], original[c]);
                double newArea = Area(candidate[a], candidate[b], candidate[c]);
                if (!double.IsFinite(before) || !double.IsFinite(after) || after > before * 1.01
                    || !double.IsFinite(oldArea) || !double.IsFinite(newArea) || oldArea == 0 || newArea == 0
                    || Math.Sign(oldArea) != Math.Sign(newArea)) return false;
            }
            return true;
        }

        /// <summary>Scans the complete chart, charging all attempts to one bounded comparison budget.</summary>
        static UvAtlasDiagnostics.Report Scan(UvShell shell, int[] indices, Dictionary<int, Vector2> uv,
            CancellationToken token, ref long budget)
        {
            var scan = UvAtlasDiagnostics.Measure(UvAtlasDiagnostics.ShellGeometry(shell.faceIndices, indices, v => uv[v]),
                token, comparisonBudget: Math.Max(0, budget), collectConflicts: true);
            budget -= scan.comparisons;
            return scan;
        }

        /// <summary>Computes signed double area without Float32 subtraction.</summary>
        static double Area(Vector2 a, Vector2 b, Vector2 c) =>
            ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);
    }
}
