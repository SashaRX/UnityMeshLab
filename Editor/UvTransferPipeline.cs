// UvTransferPipeline.cs — Pipeline settings for UV transfer
// The actual transfer is handled by GroupedShellTransfer.Transfer().

namespace SashaRX.UnityMeshLab
{
    public static class UvTransferPipeline
    {
        public struct PipelineSettings
        {
            // Output
            public bool saveNewMeshAssets;
            public string savePath;

            public static PipelineSettings Default => new PipelineSettings
            {
                saveNewMeshAssets = true,
                savePath = "Assets/LightmapUvTool_Output"
            };
        }
    }
}
