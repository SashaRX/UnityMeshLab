// LightmapTransferTool.cs — independently discoverable tab backed by the shared UV workflow.
namespace SashaRX.UnityMeshLab
{
    [MeshLabTool("uv2_transfer", MeshLabLibraries.UvTransfer, MeshLabLibraries.Assets)]
    public class LightmapTransferTool : UvTransferWorkflow, IUvTool, IUvToolAssetLifecycle
    {
        public string ToolName => "UV2 Transfer";
        public string ToolId => "uv2_transfer";
        public int ToolOrder => 0;
    }
}
