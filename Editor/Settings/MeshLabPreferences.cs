using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LightmapUvTool
{
    static class MeshLabPreferences
    {
        const string AoChannelTypePref = "LightmapUvTool.DefaultAoChannelType";
        const string AoChannelCompPref = "LightmapUvTool.DefaultAoChannelComp";

        static readonly string[] channelTypeNames = { "Vertex Color", "UV0", "UV1", "UV2", "UV3", "UV4" };
        static readonly string[] colorCompNames   = { "R", "G", "B", "A" };
        static readonly string[] uvCompNames      = { "X", "Y" };

        public static int DefaultAoChannelType
        {
            get => EditorPrefs.GetInt(AoChannelTypePref, 0);
            set => EditorPrefs.SetInt(AoChannelTypePref, value);
        }

        public static int DefaultAoChannelComp
        {
            get => EditorPrefs.GetInt(AoChannelCompPref, 0);
            set => EditorPrefs.SetInt(AoChannelCompPref, value);
        }

        [SettingsProvider]
        static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Preferences/Mesh Lab", SettingsScope.User)
            {
                guiHandler = OnGUI,
                keywords = new HashSet<string> { "mesh", "lab", "sidecar", "uv2", "ao", "lightmap" }
            };
        }

        static void OnGUI(string searchContext)
        {
            EditorGUIUtility.labelWidth = 200;
            EditorGUILayout.Space(8);

            // ── Sidecar UV2 Mode ──
            EditorGUILayout.LabelField("Sidecar", EditorStyles.boldLabel);
            bool sidecar = PostprocessorDefineManager.IsEnabled();
            EditorGUI.BeginChangeCheck();
            sidecar = EditorGUILayout.Toggle("Sidecar UV2 Mode", sidecar);
            if (EditorGUI.EndChangeCheck())
                PostprocessorDefineManager.SetEnabled(sidecar);

            if (!sidecar)
                EditorGUILayout.HelpBox(
                    "Persistent replay is OFF. FBX overwrite will use one-shot replay only.",
                    MessageType.None);

            EditorGUILayout.Space(4);
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

            // ── Logging ──
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Logging", EditorStyles.boldLabel);
            UvtLog.Current = (UvtLog.Level)EditorGUILayout.EnumPopup("Log Level", UvtLog.Current);

            // ── Default AO Channel ──
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Vertex AO Defaults", EditorStyles.boldLabel);

            int chType = DefaultAoChannelType;
            int chComp = DefaultAoChannelComp;

            EditorGUI.BeginChangeCheck();
            chType = EditorGUILayout.Popup("Channel", chType, channelTypeNames);
            string[] compNames = chType == 0 ? colorCompNames : uvCompNames;
            int maxComp = compNames.Length - 1;
            if (chComp > maxComp) chComp = 0;
            chComp = EditorGUILayout.Popup("Component", chComp, compNames);
            if (EditorGUI.EndChangeCheck())
            {
                DefaultAoChannelType = chType;
                DefaultAoChannelComp = chComp;
            }

            string label = channelTypeNames[chType] + " " + compNames[chComp];
            EditorGUILayout.LabelField("Default target:", label, EditorStyles.miniLabel);
        }
    }
}
