// DebugUi.cs — the one gate and the shared pieces of the package's diagnostic
// surfaces: Project Settings ▸ Mesh Lab ▸ Developer ▸ Show Debug UI decides whether a
// debug-only tab, menu item or section shows; the banner marks such a section; the
// log-filter block edits UvtLog's level and categories. Tabs opt in with
// IUvToolDebugOnly and the hub hides them while the setting is off.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Marker for a tool tab that exists for diagnostics; the hub shows it only with Show Debug UI on.</summary>
    internal interface IUvToolDebugOnly { }

    internal static class DebugUi
    {
        /// <summary>Project Settings ▸ Mesh Lab ▸ Developer ▸ Show Debug UI.</summary>
        internal static bool Enabled => MeshLabProjectSettings.Instance.showDebugUI;

        /// <summary>Every single-bit log category, in enum order (the composite `All` left out); built once.</summary>
        internal static readonly UvtLog.Category[] Categories = BuildCategories();

        static UvtLog.Category[] BuildCategories()
        {
            var all = (UvtLog.Category[])Enum.GetValues(typeof(UvtLog.Category));
            var list = new List<UvtLog.Category>(all.Length);
            foreach (var c in all)
                if (c != UvtLog.Category.All) list.Add(c);
            return list.ToArray();
        }

        /// <summary>The orange DEBUG strip that tells a diagnostic block apart from the production UI around it.</summary>
        internal static void Banner(string text = "DEBUG  ·  hide via Project Settings ▸ Mesh Lab ▸ Show Debug UI")
        {
            var rect = GUILayoutUtility.GetRect(0, 20f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.55f, 0.35f, 0.10f, 0.30f));
            var style = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(1f, 0.85f, 0.55f) },
            };
            GUI.Label(new Rect(rect.x + 6f, rect.y, rect.width - 12f, rect.height), text, style);
        }

        /// <summary>The log level popup and one toggle per category, under a foldout.</summary>
        internal static void LogFilters(ref bool fold)
        {
            fold = EditorGUILayout.Foldout(fold, "Log filters", true);
            if (!fold) return;
            EditorGUI.indentLevel++;
            UvtLog.Current = (UvtLog.Level)EditorGUILayout.EnumPopup("Level", UvtLog.Current);
            var enabled = UvtLog.EnabledCategories;
            foreach (var cat in Categories)
            {
                bool on = (enabled & cat) != 0;
                bool newOn = EditorGUILayout.ToggleLeft(cat.ToString(), on);
                if (newOn != on) UvtLog.SetCategoryEnabled(cat, newOn);
            }
            EditorGUI.indentLevel--;
        }
    }
}
