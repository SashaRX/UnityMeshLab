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
        /// <returns>The recovered mesh with its measured quality, or default.</returns>
        internal static Task<(Mesh mesh, int[] sources, ForwardUvReference reference, TransferUvQuality quality)> RecoverForwardSurfaceAsync(
            Mesh source, Mesh target, Vector2[] legacyUv, int[] legacySources, int resolution,
            bool useAsync, Matrix4x4 renderedFrame, CancellationToken token = default,
            TransferPerformanceProfile performance = null, long comparisonBudget = 20000000)
            => RecoverForwardSurfaceCoreAsync(source, target, legacyUv, legacySources, resolution, useAsync, renderedFrame, token,
                performance, comparisonBudget, null, null);

        // Seam preparation hands over the preparation its passes share (clean
        // inputs, donor, donor charts, legacy contact audit) and its measure of
        // the legacy layout. Recovery projects with its own options.
        static async Task<(Mesh mesh, int[] sources, ForwardUvReference reference, TransferUvQuality quality)> RecoverForwardSurfaceCoreAsync(
            Mesh source, Mesh target, Vector2[] legacyUv, int[] legacySources, int resolution,
            bool useAsync, Matrix4x4 renderedFrame, CancellationToken token, TransferPerformanceProfile performance,
            long comparisonBudget, ForwardSeamShared shared, TransferUvQuality legacyQuality)
        {
            token.ThrowIfCancellationRequested();
            var indices = target.triangles;
            var positions = target.vertices;
            if (legacyUv.Length != positions.Length || legacySources.Length != positions.Length) return default;
            var before = legacyQuality ?? TransferUvQuality.Measure(target, legacyUv, Vector2.one, renderedFrame);
            if (!before.overlapScanComplete || (before.overlapPairs == 0 && before.degenerateFaces == 0)) return default;
            using var owned = shared == null ? new ForwardSeamShared() : null;
            var pass = shared ?? owned;
            if (!pass.prepared) pass.Prepare(source, target, resolution, renderedFrame, token);
            if (pass.unusable) return default;
            var donorShells = pass.SourceFaceShells(source);
            var options = new Options {seedResolution = resolution, maxAtlasSize = resolution,
                projectionReach = .05f, normalDot = .9f, comparisonBudget = comparisonBudget,
                stretchFrame = renderedFrame, performance = performance};
            var projectedFaces = new HashSet<int>();
            Vector2[] uv = null;
            int[] cornerSources = null, faces = null;
            bool ready = await RunForwardCpuWork(cancellation => {
                var contacts = pass.LegacyContacts(legacyUv, indices, cancellation, performance);
                if (contacts == null) return false;
                var conflicts = new HashSet<int>(contacts.SelectMany(p => new[] {p.Item1, p.Item2}));
                var projected = pass.ProjectInitialCopy(options, cancellation).projected;
                faces = projected.sourceFaces.Select(f => pass.inputs.levels[1].inputs[0].sourceFaces[f]).ToArray();
                // This recovery changes only UV seams, never the ordered triangles.
                if (!faces.SequenceEqual(Enumerable.Range(0, indices.Length / 3))) return false;
                uv = new Vector2[indices.Length];
                cornerSources = new int[indices.Length];
                for (int f = 0; f < faces.Length; ++f) {
                    cancellation.ThrowIfCancellationRequested();
                    int start = f * 3;
                    double oldMetric = ForwardAnisotropy(positions[indices[start]], positions[indices[start + 1]],
                        positions[indices[start + 2]], legacyUv[indices[start]], legacyUv[indices[start + 1]], legacyUv[indices[start + 2]], renderedFrame);
                    double newMetric = ForwardAnisotropy(positions[indices[start]], positions[indices[start + 1]],
                        positions[indices[start + 2]], projected.pixels[start], projected.pixels[start + 1], projected.pixels[start + 2], renderedFrame);
                    bool replace = projected.faces[f].inherited && newMetric <= options.maxAnisotropy
                        && (conflicts.Contains(f) || UvTransferTolerance.MetricFits(newMetric, oldMetric));
                    if (replace) projectedFaces.Add(f);
                    for (int k = 0; k < 3; ++k) {
                        int corner = start + k;
                        uv[corner] = replace ? projected.pixels[corner] / resolution : legacyUv[indices[corner]];
                        cornerSources[corner] = replace ? donorShells[pass.validFaces[projected.faces[f].parentFace]] : legacySources[indices[corner]];
                    }
                }
                return projectedFaces.Count > 0;
            }, useAsync, token);
            if (!ready) return default;
            var barycentrics = Enumerable.Range(0, indices.Length)
                .Select(i => i % 3 == 0 ? Vector3.right : i % 3 == 1 ? Vector3.up : Vector3.forward).ToArray();
            var sources = new List<int>();
            Mesh candidate = ReverseUvRefinedMesh.Copy(target, faces, barycentrics, uv,
                (vertex, corner) => sources.Add(cornerSources[corner]));
            try {
                var after = TransferUvQuality.Measure(candidate, candidate.uv2, Vector2.one, renderedFrame);
                if (!after.overlapScanComplete || after.overlapPairs != 0 || after.invalidFaces != 0
                    || after.degenerateFaces != 0 || after.outOfBoundsVertices != 0
                    || after.worstAnisotropy > options.maxAnisotropy) return default;
                var reference = ForwardUvReference.Capture(candidate, indices, legacyUv, legacySources,
                    faces, barycentrics, projectedFaces);
                var output = candidate;
                candidate = null;
                return (output, sources.ToArray(), reference, after);
            }
            finally {if (candidate) UnityEngine.Object.DestroyImmediate(candidate);}
        }
    }
}
