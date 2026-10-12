using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    // Retains the old window type so saved layouts can migrate to the Mesh Lab tab.
    public sealed class TextureChannelPackerWindow : EditorWindow
    {
        public static void Open() => UvToolHub.OpenWithTool("texture_channel_packer");

        public static string ModelBaseName(string path) => TextureChannelPackerPanel.ModelBaseName(path);

        private void OnEnable() => EditorApplication.delayCall += OpenTabAndClose;
        private void OnDisable() => EditorApplication.delayCall -= OpenTabAndClose;
        private void OpenTabAndClose()
        {
            try { Open(); }
            finally { Close(); }
        }
    }
}
