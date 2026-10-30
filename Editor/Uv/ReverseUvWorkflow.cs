using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public partial class UvTransferWorkflow
    {
        bool reversePrepareSeed = true, reversePreserveOverlap;
        bool reverseFillAtlas;
        bool reverseUseForwardCharts = true;
        float reverseReach = .05f;
        string reverseSummary;

        void DrawReverseTransferActions()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Reverse UV — coarse → fine (experimental)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Freeze ordinary Transfer, cut the coarsest foundation, then match whole islands towards LOD0. Repack the completed groups together.", MessageType.None);
            reversePrepareSeed = EditorGUILayout.ToggleLeft(new GUIContent("Prepare with ordinary Transfer",
                "Run Analyze / Weld / Symmetry / Repack / Transfer on detached copies. Uses the Repack junction-cut settings. Disable to regroup an existing forward result."), reversePrepareSeed);
            reversePreserveOverlap = EditorGUILayout.ToggleLeft(new GUIContent("Allow projected detail overlap",
                "Details can inherit the light under them. Overlap ancestry is saved; ordered baking is not implemented yet."), reversePreserveOverlap);
            reverseFillAtlas = EditorGUILayout.ToggleLeft(new GUIContent("Fill final atlas uniformly",
                "After packing at the requested density, try denser atlases whose padding grows by the same factor, so a bake at the requested density keeps the padding between charts. A denser atlas is kept only when it packs tighter."),reverseFillAtlas);
            reverseReach = EditorGUILayout.FloatField(new GUIContent("Projection reach (model units)"), reverseReach);
            using (new EditorGUI.DisabledScope(!ctx.LodGroup || ctx.MeshEntries.Where(e => e.include).Select(e => e.lodIndex).Distinct().Count() < 2))
                if (GUILayout.Button("Run Reverse UV", GUILayout.Height(24)) && ConfirmReverseReimport())
                    FireAndForget(() => RunReverseTransfer(true), "Reverse UV");
            if (!string.IsNullOrEmpty(reverseSummary)) EditorGUILayout.HelpBox(reverseSummary, MessageType.Info);
        }

        // Preparation reimports models that are not yet imported for precision, and the reimport
        // resets the whole working chain without undo. Ask before that discards a result;
        // regrouping the existing forward result never reimports.
        bool ConfirmReverseReimport()
        {
            if (!reverseUseForwardCharts || !reversePrepareSeed) return true;
            // The reset clears every entry, not only the reimported models.
            var discarded = ctx.MeshEntries.Where(HasWorkingResult).ToList();
            if (discarded.Count == 0) return true;
            var paths = UvTransferInputPreparation.FindPaths(ctx.MeshEntries);
            if (paths.Count == 0) return true;
            return EditorUtility.DisplayDialog("Reimport for Reverse UV?",
                "'Prepare with ordinary Transfer' reads the imported meshes without mesh compression, optimization or importer welding. "
                + $"Reimported that way first: {NameList(paths.Select(System.IO.Path.GetFileName))}.\n\n"
                + "The reimport resets the working chain without undo, discarding the weld, symmetry, repack, Transfer and Reverse results of "
                + $"{NameList(discarded.Select(e => e.fbxMesh ? e.fbxMesh.name : e.originalMesh.name))}.\n\n"
                + "To keep the chain, cancel and turn off 'Prepare with ordinary Transfer' to regroup the existing forward result.",
                "Reimport and run", CancelButton);
        }

        static string NameList(IEnumerable<string> names)
        {
            var list = names.ToList();
            return string.Join(", ", list.Take(3)) + (list.Count > 3 ? $" and {list.Count - 3} more" : string.Empty);
        }

        /// <summary>A result the working-mesh reset discards: a derived mesh, a working clone or Reverse provenance.</summary>
        internal static bool HasWorkingResult(MeshEntry entry) => entry != null
            && (entry.transferredMesh || entry.repackedMesh || !string.IsNullOrEmpty(entry.reverseTransferJson)
                || (entry.originalMesh && entry.Baseline && entry.originalMesh != entry.Baseline));

        /// <summary>"Atlas 512²", or each mesh atlas's side when the meshes were packed on their own.</summary>
        internal static string ReverseAtlases(ReverseUvTransfer.Report report)
        {
            var sides = report.nodes.GroupBy(n => n.packingSet).Select(g => g.First().AtlasSide(report)).ToList();
            return sides.Count > 1 ? $"Atlases {string.Join(" + ", sides.Select(s => $"{s}²"))}" : $"Atlas {report.atlasSize}²";
        }

        /// <summary>The final texel density, as a range when the mesh atlases differ.</summary>
        internal static string ReverseDensity(ReverseUvTransfer.Report report)
        {
            var densities = report.nodes.Select(n => n.texelsPerUnit > 0 ? n.texelsPerUnit : report.texelsPerUnit).DefaultIfEmpty(report.texelsPerUnit).ToList();
            float low = densities.Min(), high = densities.Max();
            return high > low ? $"{low:G4}–{high:G4}" : $"{low:G4}";
        }

        // An unrelated or failed operation must not remove the export guard from
        // reverse-generated meshes that are still owned by their entries.
        void ClearReverseSummary() => reverseSummary = null;

        /// <summary>Prepare ordinary islands on detached copies, then regroup and
        /// repack the complete chain before publishing any result.</summary>
        internal async Task RunReverseTransfer(bool useAsync)
        {
            if(float.IsNaN(reverseReach) || float.IsInfinity(reverseReach) || reverseReach<=0)
                throw new ArgumentException("Projection reach must be finite and positive.");
            // Precision preparation needs the imported asset paths, before the
            // ordinary pipeline receives detached in-memory copies. Its reimport resets
            // the working chain first; record that even when the reimport then fails.
            if(reverseUseForwardCharts && reversePrepareSeed && UvTransferInputPreparation.FindPaths(ctx.MeshEntries).Count>0)
            {
                bool discards=ctx.MeshEntries.Any(HasWorkingResult);
                try {PrepareTransferInputs();}
                finally {if(discards) UvtLog.Warn("[ReverseUV] Preparing inputs without mesh compression, optimization or importer welding reset the previous working chain.");}
            }
            using var previewChange = PreservePreviewDuringMeshChange();
            var groups = ctx.MeshEntries.Where(e => e.include && e.originalMesh)
                .GroupBy(e => e.lodIndex).OrderByDescending(g => g.Key).Select(g => g.ToList()).ToList();
            if (groups.Count < 2) throw new InvalidOperationException("Reverse UV requires at least two included LODs.");
            using var cancellation = new CancellationTokenSource();
            void PollCancel() { if (UvProgress.CancelRequested) cancellation.Cancel(); }
            bool ownsProgress = !UvProgress.IsActive;
            if (ownsProgress) UvProgress.Begin("Reverse UV: coarse → fine", cancelable: true);
            SubscribeReverseCancellation(PollCancel);
            try
            {
                UvProgress.Report(0, "Clean detached working copies");
                using var forward = reverseUseForwardCharts
                    ? await PrepareForwardReverseInputs(reversePrepareSeed, useAsync, cancellation.Token) : null;
                if (!UvProgress.IsActive) UvProgress.Begin("Reverse UV: regroup prepared islands", cancelable: true);
                var sources = forward?.levels ?? groups.Select((List<MeshEntry> g, int level) => new ReverseUvTransfer.Level {
                    lod = g[0].lodIndex,
                    inputs = g.Select((MeshEntry e) => new ReverseUvTransfer.Input {
                        mesh = level == 0 && !reversePrepareSeed ? e.repackedMesh ?? e.transferredMesh ?? e.originalMesh : e.originalMesh,
                        toWorld = ctx.PreviewMatrix(e.renderer ? e.renderer.transform : null),
                        key = e.fbxMesh ? e.fbxMesh.name : e.originalMesh.name
                    }).ToArray()
                }).ToArray();
                int seedSize = groups[0].Max(e => (int)e.repackedAtlasWidth);
                if (forward != null) seedSize = forward.atlasSize;
                else if (reversePrepareSeed) seedSize = SanitizeAtlasResolution(ctx.AtlasResolution);
                PollCancel(); cancellation.Token.ThrowIfCancellationRequested();
                var options = new ReverseUvTransfer.Options {
                    seedResolution = seedSize > 0 ? seedSize : SanitizeAtlasResolution(ctx.AtlasResolution),
                    padding = SanitizePadding(ctx.ShellPaddingPx), projectionReach = reverseReach,
                    preserveProjectedOverlap = reversePreserveOverlap, cutNarrowJunctions = ctx.CutNarrowUvJunctions,
                    rotateCharts = ctx.XatlasRotateCharts, rotateChartsToAxis = ctx.XatlasRotateChartsToAxis,
                    repackFinalAtlas = true, fillFinalAtlas = reverseFillAtlas, packPerMesh = ctx.RepackPerMesh,
                    regroupPreparedAtlas = forward != null,
                    frozenShellCascade = forward != null && !reversePreserveOverlap,
                    rebuildSeedWithUnity = false
                };
                UvProgress.Report(.15f, "Match whole islands from coarse to fine");
                using var prepared = await ReverseUvJunctionTrial.Build(sources, options, reversePrepareSeed,
                    ctx.RepackResolutionMode == ResolutionMode.AutoFromTexelDensity ? ctx.LightmapDensity : 0,
                    useAsync, cancellation.Token);
                var result = prepared.result;
                var levels = prepared.inputs.levels;
                int removed = 0;
                foreach (var level in levels)
                    foreach (var input in level.inputs) removed += input.removedSourceFaces.Length;
                cancellation.Token.ThrowIfCancellationRequested();
                // Serialize and audit before publishing any mesh: a failed validation cannot
                // leave a partly updated LOD chain. The audit's report goes to disk only at the
                // Verbose log level, and a failed write is logged, never a reason to withhold the result.
                var json = ReverseUvAudit.Provenance(result.report);
                var audit = ReverseUvAudit.Measure(result, levels);
                string auditPath = null;
                if (UvtLog.Current >= UvtLog.Level.Verbose)
                {
                    try { auditPath = ReverseUvAudit.Save(audit, ReverseUvAudit.KeptReports); }
                    catch (Exception ex) { UvtLog.Warn("[ReverseUV] Audit report not written: " + ex.Message); }
                }
                ClearReverseSummary();
                int reportNode = 0;
                for (int level = 0; level < groups.Count; ++level)
                    for (int node = 0; node < groups[level].Count; ++node)
                    {
                        var entry = groups[level][node];
                        if (entry.transferredMesh) UnityEngine.Object.DestroyImmediate(entry.transferredMesh);
                        if (entry.repackedMesh) UnityEngine.Object.DestroyImmediate(entry.repackedMesh);
                        entry.transferredMesh = entry.repackedMesh = null;
                        if (level == 0) entry.repackedMesh = result.meshes[level][node];
                        else entry.transferredMesh = result.meshes[level][node];
                        result.meshes[level][node] = null;
                        entry.repackedAtlasWidth = entry.repackedAtlasHeight = (uint)result.report.nodes[reportNode].AtlasSide(result.report);
                        entry.shellTransferResult = null; entry.transferState = null; entry.validationReport = null;
                        entry.reverseTransferJson = json[reportNode++];
                    }
                ctx.HasRepack = ctx.HasTransfer = true;
                lastForwardRun = null;
                ctx.ClearAllCaches();
                reverseSummary = $"{ReverseAtlases(result.report)} · inherited {result.report.inheritedFaces} · new {result.report.newFaces}"
                    + $" · overlap {result.report.overlapFaces} · ambiguous {result.report.ambiguousFaces} · split source faces {result.report.splitSourceFaces} · removed zero-area {removed}";
                reverseSummary += $" · {ReverseDensity(result.report)} tex/m · fully repacked";
                if (result.report.finalPackingRefusal != null) reverseSummary += " · " + result.report.finalPackingRefusal;
                if (prepared.refusal != null) reverseSummary += " · retained safer layout: " + prepared.refusal;
                if (result.report.refinementRefusal != null) reverseSummary += " · donor seam cuts skipped: " + result.report.refinementRefusal;
                UvtLog.Info($"[ReverseUV] {reverseSummary}." + (auditPath != null ? $" Audit: {auditPath}" : string.Empty));
                if (ownsProgress) UvProgress.End();
            }
            catch (OperationCanceledException)
            {
                if (ownsProgress) UvProgress.Cancel();
                throw;
            }
            catch (Exception ex)
            {
                if (ownsProgress) UvProgress.Fail(ex.Message);
                throw;
            }
            finally
            {
                UnsubscribeReverseCancellation(PollCancel);
                RequestRepaint?.Invoke();
            }
        }

        static void SubscribeReverseCancellation(EditorApplication.CallbackFunction callback)
            => EditorApplication.update += callback;
        static void UnsubscribeReverseCancellation(EditorApplication.CallbackFunction callback)
            => EditorApplication.update -= callback;
    }
}
