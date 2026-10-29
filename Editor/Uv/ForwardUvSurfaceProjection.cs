using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        /// <summary>
        /// What a kept-face rescue did with the defective faces of a transferred
        /// layout: overlapped, collapsed or stretched past the bound. The workflow
        /// logs it, so a refused rescue says why its faces stayed as they were.
        /// </summary>
        internal sealed class ForwardRescueReport
        {
            /// <summary>Defective faces the projection kept (cleanup drops zero-area triangles).</summary>
            public int defective;
            /// <summary>Defective faces with a donor projection within the bound, before contacts were undone.</summary>
            public int projected;
            /// <summary>Defective faces whose donor projection is stretched past the bound.</summary>
            public int overStretched;
            /// <summary>Rescues undone because they touch another face.</summary>
            public int touching;
            /// <summary>Rescues returned.</summary>
            public int rescued;
            /// <summary>Defective faces with no donor face, by the projection's rejection.</summary>
            public readonly SortedDictionary<string, int> unprojected = new SortedDictionary<string, int>();
            /// <summary>Why no rescue was returned; null when one was.</summary>
            public string refusal;

            internal void Unprojected(string rejection)
            {
                unprojected.TryGetValue(rejection, out int count);
                unprojected[rejection] = count + 1;
            }

            /// <summary>The counts, as "rescued R of D defective faces (…)".</summary>
            public string Faces()
            {
                var parts = new List<string>();
                if (touching > 0) parts.Add($"{touching} touching another face");
                if (overStretched > 0) parts.Add($"{overStretched} stretched past 4:1 on the donor");
                parts.AddRange(unprojected.Select(p => $"{p.Value} without a donor face ({p.Key})"));
                return $"rescued {rescued} of {defective} defective face(s)" + (parts.Count > 0 ? $" ({string.Join(", ", parts)})" : string.Empty);
            }

            public override string ToString() => refusal == null ? Faces() : $"{refusal}; {Faces()}";
        }

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
            TransferUvQuality legacyQuality = null, ForwardRescueReport report = null)
            => RecoverForwardSurfaceCoreAsync(source, target, legacyUv, legacySources, resolution, useAsync, renderedFrame, token,
                performance, comparisonBudget, null, legacyQuality, keepFaces, report);

        // Seam preparation hands over the preparation its passes share (clean
        // inputs, donor, donor charts, legacy contact audit) and its measure of
        // the legacy layout. Recovery projects with its own options.
        static async Task<(Mesh mesh, int[] sources, ForwardUvReference reference, TransferUvQuality quality)> RecoverForwardSurfaceCoreAsync(
            Mesh source, Mesh target, Vector2[] legacyUv, int[] legacySources, int resolution,
            bool useAsync, Matrix4x4 renderedFrame, CancellationToken token, TransferPerformanceProfile performance,
            long comparisonBudget, ForwardSeamShared shared, TransferUvQuality legacyQuality, bool keepFaces = false,
            ForwardRescueReport report = null)
        {
            (Mesh, int[], ForwardUvReference, TransferUvQuality) Refuse(string reason)
            {
                if (report != null) { report.refusal = reason; report.rescued = 0; }
                return default;
            }
            token.ThrowIfCancellationRequested();
            var indices = target.triangles;
            var positions = target.vertices;
            if (legacyUv.Length != positions.Length || legacySources.Length != positions.Length)
                return Refuse("the transferred UVs do not match the target's vertices");
            var options = new Options {seedResolution = resolution, maxAtlasSize = resolution,
                projectionReach = .05f, normalDot = .9f, comparisonBudget = comparisonBudget,
                stretchFrame = renderedFrame, performance = performance};
            var before = legacyQuality ?? TransferUvQuality.Measure(target, legacyUv, Vector2.one, renderedFrame);
            if (!before.overlapScanComplete) return Refuse("the overlap scan of the transferred layout did not finish");
            if (before.overlapPairs == 0 && before.degenerateFaces == 0
                && !(keepFaces && before.worstAnisotropy > options.maxAnisotropy)) return Refuse("no defective face");
            using var owned = shared == null ? new ForwardSeamShared() : null;
            var pass = shared ?? owned;
            if (!pass.prepared) pass.Prepare(source, target, resolution, renderedFrame, token);
            if (pass.unusable) return Refuse("the donor has no UV2 face within 4:1");
            var donorShells = pass.SourceFaceShells(source);
            var projectedFaces = new HashSet<int>();
            // Without kept faces a healthy face may be replaced too; the report counts defective ones.
            var defectiveFaces = new HashSet<int>();
            int faceCount = indices.Length / 3;
            Vector2[] uv = null;
            int[] cornerSources = null;
            bool ready = await RunForwardCpuWork(cancellation => {
                var contacts = pass.LegacyContacts(legacyUv, indices, cancellation, performance);
                if (contacts == null) {
                    if (report != null) report.refusal = "the contact audit of the transferred layout exceeded its budget";
                    return false;
                }
                var conflicts = new HashSet<int>(contacts.SelectMany(p => new[] {p.Item1, p.Item2}));
                var projected = pass.ProjectInitialCopy(options, cancellation).projected;
                var origins = projected.sourceFaces.Select(f => pass.inputs.levels[1].inputs[0].sourceFaces[f]).ToArray();
                // This recovery changes only UV seams, never the ordered triangles. A
                // target keeping its faces may also lack the zero-area triangles that
                // cleanup removed; they keep their legacy UVs.
                if (keepFaces ? !Ascending(origins, faceCount) : !origins.SequenceEqual(Enumerable.Range(0, faceCount))) {
                    if (report != null) report.refusal = "the projection does not keep the target's triangles in order";
                    return false;
                }
                var projectionOf = Enumerable.Repeat(-1, faceCount).ToArray();
                for (int p = 0; p < origins.Length; ++p) projectionOf[origins[p]] = p;
                uv = new Vector2[indices.Length];
                cornerSources = new int[indices.Length];
                for (int f = 0; f < faceCount; ++f) {
                    cancellation.ThrowIfCancellationRequested();
                    int start = f * 3, p = projectionOf[f];
                    bool replace = false;
                    double oldMetric = ForwardAnisotropy(positions[indices[start]], positions[indices[start + 1]],
                        positions[indices[start + 2]], legacyUv[indices[start]], legacyUv[indices[start + 1]], legacyUv[indices[start + 2]], renderedFrame);
                    // Overlapped, collapsed or stretched past the bound; cleanup's
                    // zero-area triangles have no projection and are not counted.
                    bool defective = p >= 0 && (conflicts.Contains(f) || oldMetric > options.maxAnisotropy);
                    if (defective) defectiveFaces.Add(f);
                    if (p >= 0 && projected.faces[p].inherited) {
                        double newMetric = ForwardAnisotropy(positions[indices[start]], positions[indices[start + 1]],
                            positions[indices[start + 2]], projected.pixels[p * 3], projected.pixels[p * 3 + 1], projected.pixels[p * 3 + 2], renderedFrame);
                        // A kept face is replaced only when defective: overlapped,
                        // collapsed or stretched past the bound.
                        replace = newMetric <= options.maxAnisotropy && (conflicts.Contains(f)
                            || (keepFaces ? oldMetric > options.maxAnisotropy : UvTransferTolerance.MetricFits(newMetric, oldMetric)));
                        if (report != null && defective && newMetric > options.maxAnisotropy) ++report.overStretched;
                    }
                    else if (report != null && defective) report.Unprojected(projected.faces[p].rejection ?? "not inherited");
                    if (replace) projectedFaces.Add(f);
                    for (int k = 0; k < 3; ++k) {
                        int corner = start + k;
                        uv[corner] = replace ? projected.pixels[p * 3 + k] / resolution : legacyUv[indices[corner]];
                        cornerSources[corner] = replace ? donorShells[pass.validFaces[projected.faces[p].parentFace]] : legacySources[indices[corner]];
                    }
                }
                if (report != null) report.projected = projectedFaces.Count(defectiveFaces.Contains);
                // Revert every rescue touching another face until none does; a
                // reverted face may uncover a contact with another rescue.
                while (keepFaces && projectedFaces.Count > 0) {
                    UvAtlasDiagnostics.Report audit;
                    using (performance?.Accumulated("Forward seam contact audits"))
                        audit = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv = uv, indices = Enumerable.Range(0, uv.Length).ToArray(),
                            charts = new int[uv.Length]}, cancellation, comparisonBudget: 2000000, collectConflicts: true);
                    if (!audit.complete) {
                        if (report != null) report.refusal = "the contact audit of the rescued faces exceeded its budget";
                        return false;
                    }
                    var touching = audit.conflicts.SelectMany(p => new[] {p.a, p.b}).Where(projectedFaces.Contains).Distinct().ToArray();
                    if (touching.Length == 0) break;
                    if (report != null) report.touching += touching.Length;
                    foreach (int f in touching) {
                        projectedFaces.Remove(f);
                        for (int corner = f * 3; corner < f * 3 + 3; ++corner) {
                            uv[corner] = legacyUv[indices[corner]];
                            cornerSources[corner] = legacySources[indices[corner]];
                        }
                    }
                }
                if (projectedFaces.Count == 0 && report != null) report.refusal = "no defective face could be rescued";
                return projectedFaces.Count > 0;
            }, useAsync, token);
            if (report != null) {
                report.defective = defectiveFaces.Count;
                report.rescued = ready ? projectedFaces.Count(defectiveFaces.Contains) : 0;
            }
            if (!ready) return default;
            var faces = Enumerable.Range(0, faceCount).ToArray();
            var barycentrics = Enumerable.Range(0, indices.Length)
                .Select(i => i % 3 == 0 ? Vector3.right : i % 3 == 1 ? Vector3.up : Vector3.forward).ToArray();
            var sources = new List<int>();
            Mesh candidate = ReverseUvRefinedMesh.Copy(target, faces, barycentrics, uv,
                (vertex, corner) => sources.Add(cornerSources[corner]));
            try {
                var after = TransferUvQuality.Measure(candidate, candidate.uv2, Vector2.one, renderedFrame);
                if (!after.overlapScanComplete) return Refuse("the overlap scan of the rescued layout did not finish");
                if (!keepFaces && (after.overlapPairs != 0 || after.invalidFaces != 0
                    || after.degenerateFaces != 0 || after.outOfBoundsVertices != 0
                    || after.worstAnisotropy > options.maxAnisotropy)) return Refuse("the recovered layout is not clean");
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
