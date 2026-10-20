using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Develops collapsed target boundaries within their selected donor, retaining unrelated atlas defects.</summary>
    internal static class TargetBevelUvRecovery
    {
        /// <summary>Publishes only covered, contact-free local repairs after complete atlas scans; cancellation leaves UVs untouched.</summary>
        internal static int Apply(GroupedShellTransfer.TransferResult result, List<UvShell> targets,
            int[] indices, Vector3[] positions, List<UvShell> sources, int[] sourceIndices, Vector2[] sourceUv,
            int atlasSize, float boundaryToleranceTexels, TransferMatchTrace trace = null,
            CancellationToken token = default, long comparisonBudget = 2000000)
        {
            Check(token);
            if (atlasSize <= 0 || comparisonBudget <= 0) return 0;
            var owners = new int[positions.Length];
            foreach (var shell in targets) foreach (int v in shell.vertexIndices) ++owners[v];
            var proposal = (Vector2[])result.uv2.Clone();
            var accepted = new List<int>();
            UvAtlasDiagnostics.Report baseline = null;
            var contacts = new HashSet<(int, int)>();
            var touching = new HashSet<int>();
            long work = 0;
            foreach (var shell in targets)
            {
                Check(token);
                int donor = result.targetShellToSourceShell[shell.shellId];
                if (donor < 0 || result.targetShellStatus[shell.shellId] == GroupedShellTransfer.ShellStatus.Rejected) continue;
                if (shell.vertexIndices.Any(v => result.vertexToSourceShell[v] != donor)) continue;
                var original = shell.vertexIndices.ToDictionary(v => v, v => proposal[v]);
                var candidate = BevelUvRecovery.TryRecover(shell, indices, positions, original, token);
                if (candidate == null) continue;
                var changed = new HashSet<int>(candidate.Keys.Where(v => !candidate[v].Equals(original[v])));
                if (changed.Count == 0 || changed.Any(v => owners[v] != 1 || !InAtlas(candidate[v]))) continue;
                var faces = shell.faceIndices.Where(f => Changed(f, indices, changed)).ToList();
                if (!ImprovesFaces(faces, indices, positions, original, candidate)) continue;
                if (!Covered(faces, indices, candidate, sources[donor], sourceIndices, sourceUv,
                    boundaryToleranceTexels / atlasSize, token, ref work, comparisonBudget)) continue;
                if (baseline == null)
                {
                    baseline = Scan(result.uv2, indices, token, ref work, comparisonBudget);
                    if (baseline == null || !baseline.complete) return 0;
                    foreach (var pair in baseline.conflicts)
                    {
                        contacts.Add(Key(pair.a, pair.b)); touching.Add(pair.a); touching.Add(pair.b);
                    }
                }
                // Changing an existing contact could enlarge it without adding a pair.
                if (faces.Any(touching.Contains)) continue;
                foreach (int v in changed) proposal[v] = candidate[v];
                var after = Scan(proposal, indices, token, ref work, comparisonBudget);
                if (after == null || !after.complete) return 0;
                if (after.invalidFaces > baseline.invalidFaces || after.degenerateFaces > baseline.degenerateFaces
                    || after.outOfBoundsVertices > baseline.outOfBoundsVertices
                    || after.conflicts.Any(p => !contacts.Contains(Key(p.a, p.b))))
                {
                    foreach (int v in changed) proposal[v] = original[v];
                    continue;
                }
                accepted.Add(shell.shellId);
            }
            Check(token);
            if (accepted.Count == 0) return 0;
            result.uv2 = proposal;
            foreach (int shell in accepted)
            {
                var shellTrace = trace?.ForShell(shell);
                if (shellTrace != null) shellTrace.projectionMethod = "boundary-bevel";
            }
            return accepted.Count;
        }

        /// <summary>Requires every changed face to replace effective collapse with finite intrinsic anisotropy at most four.</summary>
        static bool ImprovesFaces(List<int> faces, int[] indices, Vector3[] positions,
            Dictionary<int, Vector2> original, Dictionary<int, Vector2> candidate)
        {
            foreach (int face in faces)
            {
                int a = indices[face * 3], b = indices[face * 3 + 1], c = indices[face * 3 + 2];
                double before = ReverseUvTransfer.TriangleAnisotropy(positions[a], positions[b], positions[c], original[a], original[b], original[c]);
                double after = ReverseUvTransfer.TriangleAnisotropy(positions[a], positions[b], positions[c], candidate[a], candidate[b], candidate[c]);
                if (double.IsNaN(before) || before <= 10000 || !double.IsFinite(after) || after > 4) return false;
            }
            return true;
        }

        /// <summary>Checks complete changed triangles against the selected donor footprint using the existing texel band.</summary>
        static bool Covered(List<int> faces, int[] indices, Dictionary<int, Vector2> candidate,
            UvShell source, int[] sourceIndices, Vector2[] sourceUv, float radius, CancellationToken token,
            ref long work, long budget)
        {
            var regions = source.faceIndices.Select(f => Enumerable.Range(0, 3)
                .Select(k => sourceUv[sourceIndices[f * 3 + k]]).ToArray()).ToArray();
            if (regions.Any(r => r.Any(p => !float.IsFinite(p.x) || !float.IsFinite(p.y)))) return false;
            try
            {
                foreach (int face in faces)
                {
                    var points = new[] { candidate[indices[face * 3]], candidate[indices[face * 3 + 1]], candidate[indices[face * 3 + 2]] };
                    // Subtract the union: overlapping donor faces must not count
                    // coverage twice and conceal an uncovered part of the bevel.
                    if (!UvTransferTolerance.Covers(points, regions, radius, ref work, budget, token)) return false;
                }
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>Charges each complete atlas scan against the call's shared work budget.</summary>
        static UvAtlasDiagnostics.Report Scan(Vector2[] uv, int[] indices, CancellationToken token, ref long work, long budget)
        {
            Check(token);
            if (work >= budget) return null;
            var report = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry { uv = uv, indices = indices, charts = new int[uv.Length] },
                token, comparisonBudget: budget - work, collectConflicts: true);
            work += report.comparisons;
            return report;
        }

        /// <summary>Tests whether a face uses any modified vertex.</summary>
        static bool Changed(int face, int[] indices, HashSet<int> changed) =>
            changed.Contains(indices[face * 3]) || changed.Contains(indices[face * 3 + 1]) || changed.Contains(indices[face * 3 + 2]);

        /// <summary>Canonicalizes an unordered contact pair.</summary>
        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

        /// <summary>Refuses nonfinite and out-of-atlas replacement coordinates.</summary>
        static bool InAtlas(Vector2 uv) => float.IsFinite(uv.x) && float.IsFinite(uv.y)
            && uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1;

        /// <summary>Observes both the caller's token and the editor's transfer cancellation.</summary>
        static void Check(CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (UvProgress.CancelRequested) throw new OperationCanceledException(); }
    }
}
