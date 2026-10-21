using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public partial class UvTransferWorkflow
    {
        // The regular pipeline operates on detached entries. Renderer references
        // supply transforms and materials only; no MeshFilter is attached, so a
        // failed preparation cannot replace scene meshes or the previous result.
        internal sealed class ForwardReverseInputs : IDisposable
        {
            internal ReverseUvTransfer.Level[] levels;
            internal int atlasSize;
            internal UvTransferWorkflow workflow;
            readonly List<Mesh> owned = new List<Mesh>();

            internal Mesh Own(Mesh mesh) { owned.Add(mesh); return mesh; }
            public void Dispose()
            {
                if (workflow != null)
                {
                    workflow.ResetWorkingMeshesToFbx();
                    workflow.canvas.Cleanup();
                    workflow=null;
                }
                foreach (var mesh in owned) if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
                owned.Clear();
            }
        }

        /// <summary>
        /// Reverse preparation reuses the last completed Full Pipeline result when it is
        /// exactly what the detached preparation would produce; off always repeats it.
        /// </summary>
        internal bool reuseForwardRunForReverse = true;
        ForwardRun lastForwardRun;

        /// <summary>The settings key and per-entry facts of a completed Full Pipeline.</summary>
        sealed class ForwardRun
        {
            internal string settings;
            internal ForwardRunNode[] nodes;
        }

        /// <summary>
        /// One included entry: everything the detached preparation reads for it, and
        /// the result the Full Pipeline published. Meshes are compared by reference
        /// and by content fingerprint, so an in-place edit also invalidates reuse.
        /// </summary>
        sealed class ForwardRunNode
        {
            internal MeshEntry entry;
            internal int lod;
            internal string groupKey, textureMetric;
            internal Renderer renderer;
            internal Texture previewTexture;
            internal Matrix4x4 frame;
            internal Mesh input, repacked, transferred;
            internal byte[] inputHash, resultHash;
            internal ForwardUvReference reference;
            internal uint atlasWidth, atlasHeight;

            internal bool Matches(ForwardRunNode other) => other != null && ReferenceEquals(entry, other.entry)
                && lod == other.lod && groupKey == other.groupKey && textureMetric == other.textureMetric
                && renderer == other.renderer && previewTexture == other.previewTexture && frame.Equals(other.frame)
                && input == other.input && repacked == other.repacked && transferred == other.transferred
                && inputHash.SequenceEqual(other.inputHash) && resultHash.SequenceEqual(other.resultHash)
                && ReferenceEquals(reference, other.reference)
                && atlasWidth == other.atlasWidth && atlasHeight == other.atlasHeight;
        }

        internal async Task<ForwardReverseInputs> PrepareForwardReverseInputs(bool runPipeline, bool useAsync, CancellationToken token)
        {
            var result = new ForwardReverseInputs();
            try
            {
                var entries = ReverseSourceEntries();
                if (!runPipeline && ctx.RepackPerMesh)
                    throw new InvalidOperationException("Reverse regrouping needs a shared forward atlas. Enable ordinary Transfer preparation.");
                if (runPipeline)
                {
                    foreach (var entry in entries)
                    {
                        // The renderer's baseline: a scene edit (MeshEntry.sceneEditMesh) when there is one, else the import.
                        var baseline=entry.Baseline; var source=baseline ? baseline : entry.originalMesh;
                        if(!source.isReadable && (source.blendShapeCount>0
                            || source.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight)
                            || source.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendIndices)))
                            throw new InvalidOperationException($"Enable Read/Write for '{source.name}' to preserve skinning and blend shapes during reverse preparation.");
                    }
                    token.ThrowIfCancellationRequested();
                    string refusal = ForwardRunRefusal(entries);
                    if (refusal == null)
                        UvtLog.Info($"[ReverseUV] Reusing the Full Pipeline result of {entries.Length} meshes: "
                            + "its inputs, settings and results match the detached preparation.");
                    else
                    {
                        if (lastForwardRun != null) UvtLog.Info($"[ReverseUV] Repeating ordinary Transfer preparation: {refusal}.");
                        entries = await RunDetachedPreparation(result, entries, useAsync, token);
                    }
                }
                token.ThrowIfCancellationRequested();
                if (entries.Any(e => !(e.repackedMesh || e.transferredMesh) || !string.IsNullOrEmpty(e.reverseTransferJson)))
                    throw new InvalidOperationException("Reverse regrouping requires ordinary Transfer results for every included LOD. Enable preparation or run the full forward pipeline first.");
                result.atlasSize = (int)entries.Max(e => e.repackedAtlasWidth);
                if (result.atlasSize < 16) throw new InvalidOperationException("Ordinary Transfer atlas dimensions are missing.");
                result.levels = entries.GroupBy(e => e.lodIndex).OrderByDescending(g => g.Key)
                    .Select(g => new ReverseUvTransfer.Level { lod=g.Key, inputs=g.Select(e => {
                        var source=e.repackedMesh ? e.repackedMesh : e.transferredMesh;
                        var copy=result.Own(UnityEngine.Object.Instantiate(source));copy.name=source.name;
                        return new ReverseUvTransfer.Input { mesh=copy, key=e.fbxMesh ? e.fbxMesh.name : e.originalMesh.name,
                            toWorld=ctx.PreviewMatrix(e.renderer ? e.renderer.transform : null),
                            usePreparedUv2=true,forwardReference=e.forwardUvReference };
                    }).ToArray() }).ToArray();
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        MeshEntry[] ReverseSourceEntries() => ctx.MeshEntries.Where(e => e.include && e.originalMesh).ToArray();

        async Task<MeshEntry[]> RunDetachedPreparation(ForwardReverseInputs result, MeshEntry[] entries, bool useAsync, CancellationToken token)
        {
            var work = DetachedPreparationContext(ctx, entries);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var baseline=entry.Baseline; var source=baseline ? baseline : entry.originalMesh;
                var copy = result.Own(MeshAccess.ReadableCopy(source));
                copy.name = entry.fbxMesh ? entry.fbxMesh.name : entry.originalMesh.name;
                work.MeshEntries.Add(new MeshEntry { lodIndex=entry.lodIndex, renderer=entry.renderer,
                    fbxMesh=copy, originalMesh=copy, meshGroupKey=entry.meshGroupKey, previewTexture=entry.previewTexture });
            }
            var pipeline = result.workflow = DetachedPreparation(work, this);
            pipeline.canvas = new UvCanvasView();
            if (!await pipeline.ExecFullPipelineCoreImpl(useAsync))
                throw new InvalidOperationException("Ordinary Transfer preparation did not complete; previous UVs were preserved.");
            return work.MeshEntries.ToArray();
        }

        static UvToolContext DetachedPreparationContext(UvToolContext ctx, MeshEntry[] entries)
        {
            var work = CopyReversePreparationSettings(ctx);
            work.SourceLodIndex = entries.Min(e => e.lodIndex);
            return work;
        }

        static UvTransferWorkflow DetachedPreparation(UvToolContext work, UvTransferWorkflow settings) => new UvTransferWorkflow {
            ctx=work, SymmetrySplitMode=settings.SymmetrySplitMode, splitTargetsInSymmetryStep=settings.splitTargetsInSymmetryStep,
            skipSymmetrySplitStep=settings.skipSymmetrySplitStep, stageRunAnalyzeUv0=settings.stageRunAnalyzeUv0,
            stageRunWeldUv0=settings.stageRunWeldUv0, stageWeldRunMeshopt=settings.stageWeldRunMeshopt,
            TransferBoundaryToleranceTexels=settings.TransferBoundaryToleranceTexels };

        /// <summary>The settings key of the detached preparation for <paramref name="entries"/>, built as that preparation builds its pipeline.</summary>
        internal static string DetachedPreparationSettings(UvToolContext ctx, UvTransferWorkflow settings, MeshEntry[] entries)
        {
            var work = DetachedPreparationContext(ctx, entries);
            return PreparationSettings(work, DetachedPreparation(work, settings));
        }

        /// <summary>
        /// Every context and workflow setting the Full Pipeline reads; floats by bit
        /// pattern. View state, caches and diagnostics are not settings.
        /// </summary>
        internal static string PreparationSettings(UvToolContext c, UvTransferWorkflow w)
        {
            var key = new System.Text.StringBuilder();
            void Add(object value) => key.Append(value is float f
                ? BitConverter.SingleToInt32Bits(f).ToString(CultureInfo.InvariantCulture)
                : Convert.ToString(value, CultureInfo.InvariantCulture)).Append('|');
            Add(c.LodGroup ? c.LodGroup.GetInstanceID() : 0); Add(c.SourceLodIndex);
            Add(c.AtlasResolution); Add(c.ShellPaddingPx); Add(c.BorderPaddingPx); Add(c.RepackPerMesh);
            Add(c.RepackResolutionMode); Add(c.LightmapDensity); Add(c.XatlasMaxChartSize); Add(c.XatlasBilinear);
            Add(c.XatlasBlockAlign); Add(c.XatlasBlockSize); Add(c.XatlasTexelsPerUnit); Add(c.XatlasBruteForce);
            Add(c.XatlasRotateCharts); Add(c.XatlasRotateChartsToAxis); Add(c.NormalizeTexelDensity);
            Add(c.CorrectSourceTextureAspect); Add(c.CutNarrowUvJunctions); Add(c.ReparameterizeStretchedShells);
            Add(c.StretchThreshold); Add(c.ArapIterations); Add(c.ClampLightmapToUnit); Add(c.PostPackDensityCorrection);
            Add(c.InternalOversample); Add(c.TargetUvCoverage);
            Add(w.SymmetrySplitMode); Add(w.splitTargetsInSymmetryStep); Add(w.skipSymmetrySplitStep);
            Add(w.stageRunAnalyzeUv0); Add(w.stageRunWeldUv0); Add(w.stageWeldRunMeshopt);
            Add(w.stageRunRepack); Add(w.stageRunTransfer); Add(w.TransferBoundaryToleranceTexels);
            return key.ToString();
        }

        /// <summary>
        /// Fingerprints a completed Full Pipeline when a detached preparation of the
        /// same entries would run with identical settings; otherwise records nothing.
        /// </summary>
        void RecordForwardRun(string settings)
        {
            lastForwardRun = null;
            var entries = ReverseSourceEntries();
            if (entries.Select(e => e.lodIndex).Distinct().Count() < 2 || settings != PreparationSettings(ctx, this)
                || settings != DetachedPreparationSettings(ctx, this, entries)) return;
            using (ForwardPerformance?.Measure("Forward run fingerprint"))
            {
                var nodes = entries.Select(CaptureForwardRunNode).ToArray();
                if (nodes.All(node => node != null)) lastForwardRun = new ForwardRun { settings = settings, nodes = nodes };
            }
        }

        /// <summary>Null when the last Full Pipeline result can stand in for a detached preparation of <paramref name="entries"/>, else why not.</summary>
        string ForwardRunRefusal(MeshEntry[] entries)
        {
            if (!reuseForwardRunForReverse) return "reuse is disabled";
            var run = lastForwardRun;
            if (run == null) return "no completed Full Pipeline result to reuse";
            if (run.settings != DetachedPreparationSettings(ctx, this, entries)) return "settings changed since the Full Pipeline";
            if (run.nodes.Length != entries.Length) return "included meshes changed since the Full Pipeline";
            for (int i = 0; i < entries.Length; ++i)
            {
                string name = entries[i].fbxMesh ? entries[i].fbxMesh.name : entries[i].originalMesh.name;
                // An unreadable buffer costs only the reuse; the detached preparation still runs.
                try { if (!run.nodes[i].Matches(CaptureForwardRunNode(entries[i]))) return $"'{name}' changed since the Full Pipeline"; }
                catch (Exception error) when (!(error is OperationCanceledException)) { return $"'{name}' cannot be fingerprinted ({error.Message})"; }
            }
            return null;
        }

        /// <summary>
        /// Null when the entry cannot be compared: a scene edit or missing import (the
        /// detached copy would differ in name or content from the ordinary input), a
        /// missing result, or a mesh whose buffers cannot be fingerprinted.
        /// </summary>
        ForwardRunNode CaptureForwardRunNode(MeshEntry e)
        {
            if (!e.fbxMesh || e.sceneEditMesh || !string.IsNullOrEmpty(e.reverseTransferJson)) return null;
            var result = e.repackedMesh ? e.repackedMesh : e.transferredMesh;
            if (!result) return null;
            var inputHash = MeshAccess.Fingerprint(e.fbxMesh); var resultHash = MeshAccess.Fingerprint(result);
            if (inputHash == null || resultHash == null) return null;
            return new ForwardRunNode {
                entry = e, lod = e.lodIndex, groupKey = e.meshGroupKey, renderer = e.renderer, previewTexture = e.previewTexture,
                textureMetric = JsonUtility.ToJson(SourceTextureUvMetric.Resolve(e.renderer, e.previewTexture, e.fbxMesh)),
                frame = ctx.PreviewMatrix(e.renderer ? e.renderer.transform : null),
                input = e.fbxMesh, repacked = e.repackedMesh, transferred = e.transferredMesh,
                inputHash = inputHash, resultHash = resultHash, reference = e.forwardUvReference,
                atlasWidth = e.repackedAtlasWidth, atlasHeight = e.repackedAtlasHeight };
        }

        static UvToolContext CopyReversePreparationSettings(UvToolContext ctx) => new UvToolContext {
            LodGroup=ctx.LodGroup, AtlasResolution=ctx.AtlasResolution,
            ShellPaddingPx=ctx.ShellPaddingPx, BorderPaddingPx=ctx.BorderPaddingPx,
            RepackPerMesh=false, // Every LOD must share one atlas coordinate system.
            RepackResolutionMode=ctx.RepackResolutionMode, LightmapDensity=ctx.LightmapDensity,
            XatlasMaxChartSize=ctx.XatlasMaxChartSize, XatlasBilinear=ctx.XatlasBilinear,
            XatlasBlockAlign=ctx.XatlasBlockAlign, XatlasBlockSize=ctx.XatlasBlockSize,
            XatlasTexelsPerUnit=ctx.XatlasTexelsPerUnit, XatlasBruteForce=ctx.XatlasBruteForce,
            XatlasRotateCharts=ctx.XatlasRotateCharts, XatlasRotateChartsToAxis=ctx.XatlasRotateChartsToAxis,
            NormalizeTexelDensity=ctx.NormalizeTexelDensity, CorrectSourceTextureAspect=ctx.CorrectSourceTextureAspect,
            CutNarrowUvJunctions=ctx.CutNarrowUvJunctions, ReparameterizeStretchedShells=ctx.ReparameterizeStretchedShells,
            StretchThreshold=ctx.StretchThreshold, ArapIterations=ctx.ArapIterations,
            ClampLightmapToUnit=ctx.ClampLightmapToUnit, PostPackDensityCorrection=ctx.PostPackDensityCorrection,
            InternalOversample=ctx.InternalOversample, TargetUvCoverage=ctx.TargetUvCoverage,
            PipeSettings=new UvTransferPipeline.PipelineSettings { saveNewMeshAssets=false }
        };
    }
}
