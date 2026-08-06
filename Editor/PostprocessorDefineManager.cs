// PostprocessorDefineManager.cs — sidecar replay toggle.
// Storage is a per-user opt-in so an untrusted project cannot enable replay.
// This class is kept as a thin shim so existing call sites don't need changes.

using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    static class PostprocessorDefineManager
    {
        const string PrefKey = "LightmapUvTool.SidecarUv2Mode";

        internal static bool IsEnabled()
        {
            return EditorPrefs.GetBool(PrefKey, false);
        }

        internal static void SetEnabled(bool enabled)
        {
            EditorPrefs.SetBool(PrefKey, enabled);
        }
    }
}
