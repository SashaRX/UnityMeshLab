using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    /// <summary>
    /// Centralized logger with verbosity levels.
    /// Stored in EditorPrefs per-user.
    /// </summary>
    public static class UvtLog
    {
        public enum Level { Off = 0, Error = 1, Warning = 2, Info = 3, Verbose = 4 }

        const string PrefKey = "LightmapUvTool_LogLevel";
        const string Prefix  = "[LightmapUV] ";

        static Level? _cached;

        public static Level Current
        {
            get
            {
                if (!_cached.HasValue)
                    _cached = (Level)EditorPrefs.GetInt(PrefKey, (int)Level.Info);
                return _cached.Value;
            }
            set
            {
                _cached = value;
                EditorPrefs.SetInt(PrefKey, (int)value);
            }
        }

        public static void Error(string msg)
        {
            if (Current >= Level.Error)
                Debug.LogError(Prefix + msg);
        }

        public static void Warn(string msg)
        {
            if (Current >= Level.Warning)
                Debug.LogWarning(Prefix + msg);
        }

        public static void Info(string msg)
        {
            if (Current >= Level.Info)
                Debug.Log(Prefix + msg);
        }

        public static void Verbose(string msg)
        {
            if (Current >= Level.Verbose)
                Debug.Log(Prefix + msg);
        }
    }
}
