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
        float reverseReach = .05f;
        string reverseSummary;

        void DrawReverseTransferActions()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Reverse UV — coarse → fine (experimental)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Standalone alternative to forward Transfer. Starts at the last included LOD; a prior forward Transfer is not required.", MessageType.None);
            reversePrepareSeed = EditorGUILayout.ToggleLeft(new GUIContent("Normalize and repair coarsest LOD",
                "Unwrap from geometry, repair invalid UV faces, normalize density per chart and repack before projecting the next LOD. Uses the Repack rotation and narrow-junction settings."), reversePrepareSeed);
            reversePreserveOverlap = EditorGUILayout.ToggleLeft(new GUIContent("Allow projected detail overlap",
                "Details can inherit the light under them. Overlap ancestry is saved; ordered baking is not implemented yet."), reversePreserveOverlap);
            reverseReach = EditorGUILayout.FloatField(new GUIContent("Projection reach (world units)"), reverseReach);
            using (new EditorGUI.DisabledScope(!ctx.LodGroup || ctx.MeshEntries.Where(e => e.include).Select(e => e.lodIndex).Distinct().Count() < 2))
                if (GUILayout.Button("Run Reverse UV", GUILayout.Height(24)))
                    FireAndForget(() => RunReverseTransfer(true), "Reverse UV");
            if (!string.IsNullOrEmpty(reverseSummary)) EditorGUILayout.HelpBox(reverseSummary, MessageType.Info);
        }

        // An unrelated or failed operation must not remove the export guard from
        // reverse-generated meshes that are still owned by their entries.
        void ClearReverseSummary() => reverseSummary = null;

        /// <summary>Standalone progressive mode. Existing Analyze/Weld can be run
        /// explicitly beforehand; no target shell matching is used here.</summary>
        internal async Task RunReverseTransfer(bool useAsync)
        {
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
                var sources = groups.Select((List<MeshEntry> g, int level) => new ReverseUvTransfer.Level {
                    lod = g[0].lodIndex,
                    inputs = g.Select((MeshEntry e) => new ReverseUvTransfer.Input {
                        mesh = level == 0 && !reversePrepareSeed ? e.repackedMesh ?? e.originalMesh : e.originalMesh,
                        toWorld = e.renderer ? e.renderer.localToWorldMatrix : Matrix4x4.identity,
                        key = e.fbxMesh ? e.fbxMesh.name : e.originalMesh.name
                    }).ToArray()
                }).ToArray();
                int seedSize = groups[0].Max(e => (int)e.repackedAtlasWidth);
                if (reversePrepareSeed)
                    seedSize = SanitizeAtlasResolution(ctx.AtlasResolution);
                PollCancel(); cancellation.Token.ThrowIfCancellationRequested();
                var options = new ReverseUvTransfer.Options {
                    seedResolution = seedSize > 0 ? seedSize : SanitizeAtlasResolution(ctx.AtlasResolution),
                    padding = SanitizePadding(ctx.ShellPaddingPx), projectionReach = reverseReach,
                    preserveProjectedOverlap = reversePreserveOverlap, cutNarrowJunctions = ctx.CutNarrowUvJunctions,
                    rotateCharts = ctx.XatlasRotateCharts, rotateChartsToAxis = ctx.XatlasRotateChartsToAxis
                };
                UvProgress.Report(.15f, "Project and expand atlas");
                using var prepared = await ReverseUvJunctionTrial.Build(sources, options, reversePrepareSeed,
                    ctx.RepackResolutionMode == ResolutionMode.AutoFromTexelDensity ? ctx.LightmapDensity : 0,
                    useAsync, cancellation.Token);
                var result = prepared.result;
                var levels = prepared.inputs.levels;
                int removed = 0;
                foreach (var level in levels)
                    foreach (var input in level.inputs) removed += input.removedSourceFaces.Length;
                cancellation.Token.ThrowIfCancellationRequested();
                // Serialize and write the audit before publishing any mesh. A failed
                // write or validation cannot leave a partly updated LOD chain.
                var json = ReverseUvAudit.Provenance(result.report);
                string auditPath = ReverseUvAudit.Write(result, levels);
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
                ctx.SourceLodIndex = groups[0][0].lodIndex;
                ctx.HasRepack = ctx.HasTransfer = true;
                ctx.ClearAllCaches();
                reverseSummary = $"Atlas {result.report.atlasSize}² · inherited {result.report.inheritedFaces} · new {result.report.newFaces}"
                    + $" · overlap {result.report.overlapFaces} · ambiguous {result.report.ambiguousFaces} · split source faces {result.report.splitSourceFaces} · removed zero-area {removed}";
                if (prepared.refusal != null) reverseSummary += " · retained safer layout: " + prepared.refusal;
                if (result.report.refinementRefusal != null) reverseSummary += " · donor seam cuts skipped: " + result.report.refinementRefusal;
                UvtLog.Info($"[ReverseUV] {reverseSummary}. Audit: {auditPath}");
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
