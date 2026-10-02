// LightmapTransferTool.cs — independently discoverable tab backed by the shared UV workflow.
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    [MeshLabTool("uv2_transfer", MeshLabLibraries.UvTransfer, MeshLabLibraries.Assets)]
    public class LightmapTransferTool : UvTransferWorkflow, IUvTool, IUvToolAssetLifecycle
    {
        public string ToolName => "UV2 Transfer";
        public string ToolId => "uv2_transfer";
        public int ToolOrder => 0;

        // Compatibility entry points for existing callers and reflection-based validation.
        new static bool IsBruteForcePackAvailable(int value) => UvTransferWorkflow.IsBruteForcePackAvailable(value);
        new static bool HasIncludedTransferTargets(IEnumerable<MeshEntry> entries, int sourceLod)
            => UvTransferWorkflow.HasIncludedTransferTargets(entries, sourceLod);
        new static bool CanApplyUv2(bool hasRepack, bool hasTransfer) => UvTransferWorkflow.CanApplyUv2(hasRepack, hasTransfer);
        new static int SanitizeAtlasResolution(int value) => UvTransferWorkflow.SanitizeAtlasResolution(value);
        new static int SanitizePadding(int value) => UvTransferWorkflow.SanitizePadding(value);
        new static bool IsSafeAssetFolderPath(string path) => UvTransferWorkflow.IsSafeAssetFolderPath(path);
        static bool TryValidateSweep(TestSuiteAsset.SweepMatrix matrix, UvToolContext context, out int cells, out string error)
            => SweepRunner.TryValidate(matrix, context, SymmetrySplitShells.ThresholdMode.LegacyFixed, out cells, out error);
    }
}
