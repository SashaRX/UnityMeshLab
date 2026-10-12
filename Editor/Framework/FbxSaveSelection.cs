using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Shared per-file selection for tools that own a hierarchy result.</summary>
    internal static class FbxSaveSelection
    {
        internal static void Sync(IEnumerable<MeshEntry> entries, Dictionary<string, bool> selection)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry == null || !entry.include || entry.fbxMesh == null) continue;
                string path = AssetDatabase.GetAssetPath(entry.fbxMesh);
                if (!SidecarStore.IsFbxPath(path)) continue;
                paths.Add(path);
                if (!selection.ContainsKey(path)) selection[path] = true;
            }
            foreach (var path in selection.Keys.Where(p => !paths.Contains(p)).ToList()) selection.Remove(path);
        }

        internal static void Draw(IEnumerable<MeshEntry> entries, Dictionary<string, bool> selection,
            ref bool foldout, ref Vector2 scroll, int visibleRows)
        {
            Sync(entries, selection);
            if (selection.Count == 0)
            {
                EditorGUILayout.HelpBox("No included FBX-backed meshes.", MessageType.Info);
                return;
            }
            int selected = selection.Values.Count(use => use);
            foldout = EditorGUILayout.Foldout(foldout, $"FBX files  ({selected} / {selection.Count} selected)", true);
            if (!foldout) return;
            float height = Mathf.Min(selection.Count, visibleRows) * (EditorGUIUtility.singleLineHeight + 4f) + 6f;
            EditorGUI.indentLevel++;
            scroll = EditorGUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar,
                GUI.skin.scrollView, GUILayout.Height(height));
            foreach (var path in selection.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList())
            {
                EditorGUILayout.BeginHorizontal();
                selection[path] = EditorGUILayout.Toggle(selection[path], GUILayout.Width(22));
                GUILayout.Space(4);
                if (GUILayout.Button(new GUIContent(Path.GetFileName(path), path + "\nClick to ping in Project"), EditorStyles.label))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    if (asset != null) EditorGUIUtility.PingObject(asset);
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
            EditorGUI.indentLevel--;
        }
    }
}
