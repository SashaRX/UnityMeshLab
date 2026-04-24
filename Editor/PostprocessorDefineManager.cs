// PostprocessorDefineManager.cs — sidecar replay toggle.
// Storage now lives in MeshLabProjectSettings (per-project, committed).
// This class is kept as a thin shim so existing call sites don't need changes.

namespace SashaRX.UnityMeshLab
{
    static class PostprocessorDefineManager
    {
        internal static bool IsEnabled()
        {
            return MeshLabProjectSettings.Instance.sidecarMode;
        }

        internal static void SetEnabled(bool enabled)
        {
            MeshLabProjectSettings.Instance.sidecarMode = enabled;
            MeshLabProjectSettings.Save();
        }
    }
}
