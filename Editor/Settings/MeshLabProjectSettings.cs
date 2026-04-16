using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace LightmapUvTool
{
    class MeshLabProjectSettings : ScriptableObject
    {
        const string AssetPath = "ProjectSettings/MeshLabSettings.asset";

        public int atlasResolution = 256;
        public int shellPaddingPx  = 2;
        public int borderPaddingPx = 0;
        public bool repackPerMesh;
        public string savePath = "Assets/LightmapUvTool_Output";

        static MeshLabProjectSettings s_Instance;

        public static MeshLabProjectSettings Instance
        {
            get
            {
                if (s_Instance != null) return s_Instance;
                var objs = UnityEditorInternal.InternalEditorUtility
                    .LoadSerializedFileAndForget(AssetPath);
                if (objs != null && objs.Length > 0)
                    s_Instance = objs[0] as MeshLabProjectSettings;
                if (s_Instance == null)
                {
                    s_Instance = CreateInstance<MeshLabProjectSettings>();
                    s_Instance.hideFlags = HideFlags.HideAndDontSave;
                    Save();
                }
                return s_Instance;
            }
        }

        public static void Save()
        {
            if (s_Instance == null) return;
            UnityEditorInternal.InternalEditorUtility
                .SaveToSerializedFileAndForget(
                    new Object[] { s_Instance }, AssetPath, true);
        }

        [SettingsProvider]
        static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/Mesh Lab", SettingsScope.Project)
            {
                guiHandler = OnGUI,
                keywords = new HashSet<string> { "mesh", "lab", "atlas", "repack", "padding", "uv" }
            };
        }

        static void OnGUI(string searchContext)
        {
            EditorGUIUtility.labelWidth = 200;
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Repack Defaults", EditorStyles.boldLabel);

            var inst = Instance;
            EditorGUI.BeginChangeCheck();

            inst.atlasResolution = EditorGUILayout.IntField("Atlas Resolution", inst.atlasResolution);
            inst.shellPaddingPx  = EditorGUILayout.IntSlider("Shell Padding", inst.shellPaddingPx, 0, 16);
            inst.borderPaddingPx = EditorGUILayout.IntSlider("Border Padding", inst.borderPaddingPx, 0, 16);
            inst.repackPerMesh   = EditorGUILayout.Toggle("Repack Per Mesh", inst.repackPerMesh);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            inst.savePath = EditorGUILayout.TextField("Default Save Path", inst.savePath);

            if (EditorGUI.EndChangeCheck())
                Save();
        }
    }
}
