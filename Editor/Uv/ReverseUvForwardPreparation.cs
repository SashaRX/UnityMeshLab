using System;
using System.Collections.Generic;
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

        internal async Task<ForwardReverseInputs> PrepareForwardReverseInputs(bool runPipeline, bool useAsync, CancellationToken token)
        {
            var result = new ForwardReverseInputs();
            try
            {
                var entries = ctx.MeshEntries.Where(e => e.include && e.originalMesh).ToArray();
                if (!runPipeline && ctx.RepackPerMesh)
                    throw new InvalidOperationException("Reverse regrouping needs a shared forward atlas. Enable ordinary Transfer preparation.");
                if (runPipeline)
                {
                    var work = CopyReversePreparationSettings();
                    work.SourceLodIndex = entries.Min(e => e.lodIndex);
                    foreach (var entry in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        var source=entry.fbxMesh ? entry.fbxMesh : entry.originalMesh;
                        if(!source.isReadable && (source.blendShapeCount>0
                            || source.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight)
                            || source.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendIndices)))
                            throw new InvalidOperationException($"Enable Read/Write for '{source.name}' to preserve skinning and blend shapes during reverse preparation.");
                        var copy = result.Own(MeshAccess.ReadableCopy(source));
                        copy.name = entry.fbxMesh ? entry.fbxMesh.name : entry.originalMesh.name;
                        work.MeshEntries.Add(new MeshEntry { lodIndex=entry.lodIndex, renderer=entry.renderer,
                            fbxMesh=copy, originalMesh=copy, meshGroupKey=entry.meshGroupKey, previewTexture=entry.previewTexture });
                    }
                    var pipeline = result.workflow = new UvTransferWorkflow { ctx=work, canvas=new UvCanvasView(),
                        SymmetrySplitMode=SymmetrySplitMode, splitTargetsInSymmetryStep=splitTargetsInSymmetryStep,
                        skipSymmetrySplitStep=skipSymmetrySplitStep, stageRunAnalyzeUv0=stageRunAnalyzeUv0,
                        stageRunWeldUv0=stageRunWeldUv0, stageWeldRunMeshopt=stageWeldRunMeshopt,
                        TransferBoundaryToleranceTexels=TransferBoundaryToleranceTexels };
                    if (!await pipeline.ExecFullPipelineCoreImpl(useAsync))
                        throw new InvalidOperationException("Ordinary Transfer preparation did not complete; previous UVs were preserved.");
                    entries = work.MeshEntries.ToArray();
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
                            toWorld=e.renderer ? e.renderer.localToWorldMatrix : Matrix4x4.identity,
                            usePreparedUv2=true };
                    }).ToArray() }).ToArray();
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        UvToolContext CopyReversePreparationSettings() => new UvToolContext {
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
