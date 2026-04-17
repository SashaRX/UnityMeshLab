using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LightmapUvTool
{
    class MeshLabProjectSettings : ScriptableObject
    {
        const string AssetPath = "ProjectSettings/MeshLabSettings.asset";

        // ── Repack defaults ──
        public int atlasResolution = 256;
        public int shellPaddingPx  = 2;
        public int borderPaddingPx = 0;
        public bool repackPerMesh;

        // ── Output ──
        public string savePath = "Assets/LightmapUvTool_Output";

        // ── UV2 pipeline ──
        public bool sidecarMode;

        // ── Vertex AO defaults ──
        public int aoChannelType;   // 0=VertexColor, 1-5=UV0-UV4
        public int aoChannelComp;   // 0=R/X, 1=G/Y, 2=B, 3=A

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
                keywords = new HashSet<string>
                {
                    "mesh", "lab", "atlas", "repack", "padding",
                    "uv", "sidecar", "ao", "vertex", "lightmap"
                }
            };
        }

        static readonly string[] channelTypeNames = { "Vertex Color", "UV0", "UV1", "UV2", "UV3", "UV4" };
        static readonly string[] colorCompNames   = { "R", "G", "B", "A" };
        static readonly string[] uvCompNames      = { "X", "Y" };

        static void OnGUI(string searchContext)
        {
            EditorGUIUtility.labelWidth = 220;
            var inst = Instance;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Repack Defaults", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            inst.atlasResolution = EditorGUILayout.IntField("Atlas Resolution", inst.atlasResolution);
            inst.shellPaddingPx  = EditorGUILayout.IntSlider("Shell Padding", inst.shellPaddingPx, 0, 16);
            inst.borderPaddingPx = EditorGUILayout.IntSlider("Border Padding", inst.borderPaddingPx, 0, 16);
            inst.repackPerMesh   = EditorGUILayout.Toggle("Repack Per Mesh", inst.repackPerMesh);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            inst.savePath = EditorGUILayout.TextField("Default Save Path", inst.savePath);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("UV2 Pipeline", EditorStyles.boldLabel);
            inst.sidecarMode = EditorGUILayout.Toggle("Sidecar UV2 Mode", inst.sidecarMode);
            if (!inst.sidecarMode)
                EditorGUILayout.HelpBox(
                    "Persistent replay is OFF. FBX overwrite will use one-shot replay only.",
                    MessageType.None);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Vertex AO Defaults", EditorStyles.boldLabel);
            inst.aoChannelType = EditorGUILayout.Popup("Channel", inst.aoChannelType, channelTypeNames);
            string[] compNames = inst.aoChannelType == 0 ? colorCompNames : uvCompNames;
            if (inst.aoChannelComp >= compNames.Length) inst.aoChannelComp = 0;
            inst.aoChannelComp = EditorGUILayout.Popup("Component", inst.aoChannelComp, compNames);
            string label = channelTypeNames[inst.aoChannelType] + " " + compNames[inst.aoChannelComp];
            EditorGUILayout.LabelField("Default target:", label, EditorStyles.miniLabel);

            if (EditorGUI.EndChangeCheck())
                Save();

            // Log Level — stays per-user in EditorPrefs via UvtLog.
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Logging (per-user)", EditorStyles.boldLabel);
            UvtLog.Current = (UvtLog.Level)EditorGUILayout.EnumPopup("Log Level", UvtLog.Current);

            // Maintenance
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Maintenance", EditorStyles.boldLabel);
            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.7f, .15f, .15f);
            if (GUILayout.Button("Delete All Sidecars", GUILayout.Height(22), GUILayout.MaxWidth(200)))
            {
                if (EditorUtility.DisplayDialog("Delete All Sidecars",
                    "This will delete every _uv2data.asset in the project.\nContinue?",
                    "Delete", "Cancel"))
                {
                    UvToolHub.NukeAllSidecarsStatic();
                }
            }
            GUI.backgroundColor = bgc;
        }
    }
}
