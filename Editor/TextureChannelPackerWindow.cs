using System;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Retains the old window type so saved layouts can migrate to the Mesh Lab tab.
    public sealed class TextureChannelPackerWindow : EditorWindow
    {
        // Keep the original field and nested type names for Unity layout deserialization.
        [Serializable]
        private sealed class Slot
        {
            public string path = "";
            public int channel;
            public int constant = 255;
        }

        [Serializable]
        private sealed class Output
        {
            public bool enabled;
            public int suffix;
            public bool split;
            public bool sourceAlpha = true;
            public Slot rgb = new Slot();
            public Slot[] slots = { new Slot(), new Slot { channel = 1 },
                new Slot { channel = 2 }, new Slot { channel = 3 } };
        }

        [SerializeField] private Output albedo = new Output { suffix = 0 };
        [SerializeField] private Output mrca = new Output { suffix = 2, split = true };
        [SerializeField] private Output normal = new Output { suffix = 1 };
        // Null identifies a freshly created adapter; a restored old window always has this field.
        [SerializeField] private string textureName;
        [SerializeField] private string outputFolder = "";
        [SerializeField] private bool prefix = true;
        [SerializeField] private bool followFbx = true;
        [SerializeField] private int widthIndex = 4;
        [SerializeField] private int heightIndex = 4;
        [SerializeField] private bool square = true;

        public static void Open() => UvToolHub.OpenWithTool("texture_channel_packer");

        public static string ModelBaseName(string path) => TextureChannelPackerPanel.ModelBaseName(path);

        private void OnEnable() => ScheduleMigration(OpenTabAndClose);
        private void OnDisable() => CancelMigration(OpenTabAndClose);
        private static void ScheduleMigration(EditorApplication.CallbackFunction callback) => EditorApplication.delayCall += callback;
        private static void CancelMigration(EditorApplication.CallbackFunction callback) => EditorApplication.delayCall -= callback;
        private void OpenTabAndClose()
        {
            try
            {
                bool restored = textureName != null;
                if (restored) EditorPrefs.SetString(MeshLabWindowPreferences.Key("TexturePacker"), JsonUtility.ToJson(this));
                var hub = UvToolHub.OpenWithTool("texture_channel_packer");
                if (restored) hub.FindTool<TextureChannelPackerTool>()?.RestoreWindowPreferences();
            }
            finally { Close(); }
        }
    }
}
