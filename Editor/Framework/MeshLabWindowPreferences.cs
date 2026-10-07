using System;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // UI preferences are personal and project-specific, never scene or package assets.
    internal static class MeshLabWindowPreferences
    {
        internal static string Key(string section)
            => "MeshLab.Window." + Hash128.Compute(Application.dataPath.Replace('\\', '/')) + "." + section;

        internal static T Load<T>(string section) where T : class, new()
        {
            var value = new T();
            string json = EditorPrefs.GetString(Key(section), "");
            if (string.IsNullOrEmpty(json)) return value;
            try { JsonUtility.FromJsonOverwrite(json, value); }
            catch (ArgumentException ex) {
                UvtLog.Warn("[Window] Could not restore " + section + " preferences: " + ex.Message);
                return new T();
            }
            return value;
        }

        internal static void Save(string section, object value)
        {
            string key = Key(section), json = JsonUtility.ToJson(value);
            if (EditorPrefs.GetString(key, "") != json) EditorPrefs.SetString(key, json);
        }

        internal static T ValidEnum<T>(T value, T fallback) where T : struct
            => Enum.IsDefined(typeof(T), value) ? value : fallback;

        internal static float Clamp(float value, float min, float max, float fallback)
            => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp(value, min, max);
    }
}
