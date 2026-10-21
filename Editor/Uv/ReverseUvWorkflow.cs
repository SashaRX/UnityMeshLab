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
        bool reverseFillAtlas = true;
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
                "After all correspondences, try higher texel densities with fresh packing of every group. Padding stays in final pixels; actual density can exceed the requested minimum."),reverseFillAtlas);
            reverseReach = EditorGUILayout.FloatField(new GUIContent("Projection reach (model units)"), reverseReach);
            using (new EditorGUI.DisabledScope(!ctx.LodGroup || ctx.MeshEntries.Where(e => e.include).Select(e => e.lodIndex).Distinct().Count() < 2))
                if (GUILayout.Button("Run Reverse UV", GUILayout.Height(24)))
                    FireAndForget(() => RunReverseTransfer(true), "Reverse UV");
            if (!string.IsNullOrEmpty(reverseSummary)) EditorGUILayout.HelpBox(reverseSummary, MessageType.Info);
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
            // ordinary pipeline receives detached in-memory copies.
            if(reverseUseForwardCharts && reversePrepareSeed) PrepareTransferInputs();
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
                    repackFinalAtlas = true, fillFinalAtlas = reverseFillAtlas,
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
                        entry.repackedAtlasWidth = entry.repackedAtlasHeight = (uint)result.report.atlasSize;
                        entry.shellTransferResult = null; entry.transferState = null; entry.validationReport = null;
                        entry.reverseTransferJson = json[reportNode++];
                    }
                ctx.HasRepack = ctx.HasTransfer = true;
                lastForwardRun = null;
                ctx.ClearAllCaches();
                reverseSummary = $"Atlas {result.report.atlasSize}² · inherited {result.report.inheritedFaces} · new {result.report.newFaces}"
                    + $" · overlap {result.report.overlapFaces} · ambiguous {result.report.ambiguousFaces} · split source faces {result.report.splitSourceFaces} · removed zero-area {removed}";
                reverseSummary += $" · {result.report.texelsPerUnit:G4} tex/m · fully repacked";
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
