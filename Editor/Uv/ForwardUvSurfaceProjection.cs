using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        // UV0 matching can be locally isotropic but land on another atlas part.
        // Reuse the donor ambiguity, footprint, winding and affine checks with
        // stricter orientation. Healthy faces retain their metric bound. Only a
        // completely clean result may replace the invalid legacy atlas.
        // A target that keeps its faces (skinned or with blend shapes) rescues only
        // its defective faces, each clear of every other face, and returns any
        // rescue with its measure: the caller weighs it against the legacy layout,
        // whose measure in the same frame it may hand over.
        /// <returns>The recovered mesh with its measured quality, or default.</returns>
        internal static Task<(Mesh mesh, int[] sources, ForwardUvReference reference, TransferUvQuality quality)> RecoverForwardSurfaceAsync(
            Mesh source, Mesh target, Vector2[] legacyUv, int[] legacySources, int resolution,
            bool useAsync, Matrix4x4 renderedFrame, CancellationToken token = default,
            TransferPerformanceProfile performance = null, long comparisonBudget = 20000000, bool keepFaces = false,
            TransferUvQuality legacyQuality = null)
            => RecoverForwardSurfaceCoreAsync(source, target, legacyUv, legacySources, resolution, useAsync, renderedFrame, token,
                performance, comparisonBudget, null, legacyQuality, keepFaces);

        // Seam preparation hands over the preparation its passes share (clean
        // inputs, donor, donor charts, legacy contact audit) and its measure of
        // the legacy layout. Recovery projects with its own options.
        static async Task<(Mesh mesh, int[] sources, ForwardUvReference reference, TransferUvQuality quality)> RecoverForwardSurfaceCoreAsync(
            Mesh source, Mesh target, Vector2[] legacyUv, int[] legacySources, int resolution,
            bool useAsync, Matrix4x4 renderedFrame, CancellationToken token, TransferPerformanceProfile performance,
            long comparisonBudget, ForwardSeamShared shared, TransferUvQuality legacyQuality, bool keepFaces = false)
        {
            token.ThrowIfCancellationRequested();
            var indices = target.triangles;
            var positions = target.vertices;
            if (legacyUv.Length != positions.Length || legacySources.Length != positions.Length) return default;
            var options = new Options {seedResolution = resolution, maxAtlasSize = resolution,
                projectionReach = .05f, normalDot = .9f, comparisonBudget = comparisonBudget,
                stretchFrame = renderedFrame, performance = performance};
            var before = legacyQuality ?? TransferUvQuality.Measure(target, legacyUv, Vector2.one, renderedFrame);
            if (!before.overlapScanComplete || (before.overlapPairs == 0 && before.degenerateFaces == 0
                && !(keepFaces && before.worstAnisotropy > options.maxAnisotropy))) return default;
            using var owned = shared == null ? new ForwardSeamShared() : null;
            var pass = shared ?? owned;
            if (!pass.prepared) pass.Prepare(source, target, resolution, renderedFrame, token);
            if (pass.unusable) return default;
            var donorShells = pass.SourceFaceShells(source);
            var projectedFaces = new HashSet<int>();
            int faceCount = indices.Length / 3;
            Vector2[] uv = null;
            int[] cornerSources = null;
            bool ready = await RunForwardCpuWork(cancellation => {
                var contacts = pass.LegacyContacts(legacyUv, indices, cancellation, performance);
                if (contacts == null) return false;
                var conflicts = new HashSet<int>(contacts.SelectMany(p => new[] {p.Item1, p.Item2}));
                var projected = pass.ProjectInitialCopy(options, cancellation).projected;
                var origins = projected.sourceFaces.Select(f => pass.inputs.levels[1].inputs[0].sourceFaces[f]).ToArray();
                // This recovery changes only UV seams, never the ordered triangles. A
                // target keeping its faces may also lack the zero-area triangles that
                // cleanup removed; they keep their legacy UVs.
                if (keepFaces ? !Ascending(origins, faceCount) : !origins.SequenceEqual(Enumerable.Range(0, faceCount))) return false;
                var projectionOf = Enumerable.Repeat(-1, faceCount).ToArray();
                for (int p = 0; p < origins.Length; ++p) projectionOf[origins[p]] = p;
                uv = new Vector2[indices.Length];
                cornerSources = new int[indices.Length];
                for (int f = 0; f < faceCount; ++f) {
                    cancellation.ThrowIfCancellationRequested();
                    int start = f * 3, p = projectionOf[f];
                    bool replace = false;
                    if (p >= 0 && projected.faces[p].inherited) {
                        double oldMetric = ForwardAnisotropy(positions[indices[start]], positions[indices[start + 1]],
                            positions[indices[start + 2]], legacyUv[indices[start]], legacyUv[indices[start + 1]], legacyUv[indices[start + 2]], renderedFrame);
                        double newMetric = ForwardAnisotropy(positions[indices[start]], positions[indices[start + 1]],
                            positions[indices[start + 2]], projected.pixels[p * 3], projected.pixels[p * 3 + 1], projected.pixels[p * 3 + 2], renderedFrame);
                        // A kept face is replaced only when defective: overlapped,
                        // collapsed or stretched past the bound.
                        replace = newMetric <= options.maxAnisotropy && (conflicts.Contains(f)
                            || (keepFaces ? oldMetric > options.maxAnisotropy : UvTransferTolerance.MetricFits(newMetric, oldMetric)));
                    }
                    if (replace) projectedFaces.Add(f);
                    for (int k = 0; k < 3; ++k) {
                        int corner = start + k;
                        uv[corner] = replace ? projected.pixels[p * 3 + k] / resolution : legacyUv[indices[corner]];
                        cornerSources[corner] = replace ? donorShells[pass.validFaces[projected.faces[p].parentFace]] : legacySources[indices[corner]];
                    }
                }
                // Revert every rescue touching another face until none does; a
                // reverted face may uncover a contact with another rescue.
                while (keepFaces && projectedFaces.Count > 0) {
                    UvAtlasDiagnostics.Report audit;
                    using (performance?.Accumulated("Forward seam contact audits"))
                        audit = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv = uv, indices = Enumerable.Range(0, uv.Length).ToArray(),
                            charts = new int[uv.Length]}, cancellation, comparisonBudget: 2000000, collectConflicts: true);
                    if (!audit.complete) return false;
                    var touching = audit.conflicts.SelectMany(p => new[] {p.a, p.b}).Where(projectedFaces.Contains).Distinct().ToArray();
                    if (touching.Length == 0) break;
                    foreach (int f in touching) {
                        projectedFaces.Remove(f);
                        for (int corner = f * 3; corner < f * 3 + 3; ++corner) {
                            uv[corner] = legacyUv[indices[corner]];
                            cornerSources[corner] = legacySources[indices[corner]];
                        }
                    }
                }
                return projectedFaces.Count > 0;
            }, useAsync, token);
            if (!ready) return default;
            var faces = Enumerable.Range(0, faceCount).ToArray();
            var barycentrics = Enumerable.Range(0, indices.Length)
                .Select(i => i % 3 == 0 ? Vector3.right : i % 3 == 1 ? Vector3.up : Vector3.forward).ToArray();
            var sources = new List<int>();
            Mesh candidate = ReverseUvRefinedMesh.Copy(target, faces, barycentrics, uv,
                (vertex, corner) => sources.Add(cornerSources[corner]));
            try {
                var after = TransferUvQuality.Measure(candidate, candidate.uv2, Vector2.one, renderedFrame);
                if (!after.overlapScanComplete) return default;
                if (!keepFaces && (after.overlapPairs != 0 || after.invalidFaces != 0
                    || after.degenerateFaces != 0 || after.outOfBoundsVertices != 0
                    || after.worstAnisotropy > options.maxAnisotropy)) return default;
                var reference = ForwardUvReference.Capture(candidate, indices, legacyUv, legacySources,
                    faces, barycentrics, projectedFaces);
                var output = candidate;
                candidate = null;
                return (output, sources.ToArray(), reference, after);
            }
            finally {if (candidate) UnityEngine.Object.DestroyImmediate(candidate);}
        }

        static bool Ascending(int[] faces, int count)
        {
            for (int p = 0; p < faces.Length; ++p)
                if (faces[p] < (p == 0 ? 0 : faces[p - 1] + 1) || faces[p] >= count) return false;
            return true;
        }
    }
}
