using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Centralized logger with verbosity levels and per-category mute mask.
    /// Stored in EditorPrefs per-user.
    /// </summary>
    public static class UvtLog
    {
        public enum Level { Off = 0, Error = 1, Warning = 2, Info = 3, Verbose = 4 }

        // Categories map to subsystems of the Lightmap UV pipeline. Bit index = (int)Category.
        [System.Flags]
        public enum Category
        {
            General    = 1 << 0,
            SymSplit   = 1 << 1,
            Repack     = 1 << 2,
            Match      = 1 << 3,
            Dedup      = 1 << 4,
            Overlap    = 1 << 5,
            Topology   = 1 << 6,
            Validation = 1 << 7,
            Export     = 1 << 8,
            Benchmark  = 1 << 9,

            All = General | SymSplit | Repack | Match | Dedup | Overlap | Topology | Validation | Export | Benchmark,
        }

        const string LevelPrefKey = "LightmapUvTool_LogLevel";
        const string MaskPrefKey  = "LightmapUvTool_LogCategoryMask";
        const string Prefix       = "[LightmapUV]";

        static Level? _cachedLevel;
        static int?   _cachedMask;

        public static Level Current
        {
            get
            {
                if (!_cachedLevel.HasValue)
                    _cachedLevel = (Level)EditorPrefs.GetInt(LevelPrefKey, (int)Level.Info);
                return _cachedLevel.Value;
            }
            set
            {
                _cachedLevel = value;
                EditorPrefs.SetInt(LevelPrefKey, (int)value);
            }
        }

        /// <summary>Bitmask of enabled categories. Disabled categories are silenced regardless of Level.</summary>
        public static Category EnabledCategories
        {
            get
            {
                if (!_cachedMask.HasValue)
                    _cachedMask = EditorPrefs.GetInt(MaskPrefKey, (int)Category.All);
                return (Category)_cachedMask.Value;
            }
            set
            {
                _cachedMask = (int)value;
                EditorPrefs.SetInt(MaskPrefKey, (int)value);
            }
        }

        public static bool IsCategoryEnabled(Category c) => (EnabledCategories & c) != 0;

        public static void SetCategoryEnabled(Category c, bool enabled)
        {
            var mask = EnabledCategories;
            if (enabled) mask |= c;
            else         mask &= ~c;
            EnabledCategories = mask;
        }

        static string Tag(Category c) => $"{Prefix}[{c}] ";

        // ── Category-tagged overloads ──

        public static void Error(Category c, string msg)
        {
            if (Current >= Level.Error && IsCategoryEnabled(c))
                Debug.LogError(Tag(c) + msg);
        }

        public static void Warn(Category c, string msg)
        {
            if (Current >= Level.Warning && IsCategoryEnabled(c))
                Debug.LogWarning(Tag(c) + msg);
        }

        public static void Info(Category c, string msg)
        {
            if (Current >= Level.Info && IsCategoryEnabled(c))
                Debug.Log(Tag(c) + msg);
        }

        public static void Verbose(Category c, string msg)
        {
            if (Current >= Level.Verbose && IsCategoryEnabled(c))
                Debug.Log(Tag(c) + msg);
        }

        // ── Legacy overloads (default to Category.General) ──

        public static void Error  (string msg) => Error  (Category.General, msg);
        public static void Warn   (string msg) => Warn   (Category.General, msg);
        public static void Info   (string msg) => Info   (Category.General, msg);
        public static void Verbose(string msg) => Verbose(Category.General, msg);
    }
}
