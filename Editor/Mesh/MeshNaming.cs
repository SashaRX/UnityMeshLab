using System;
using System.Text.RegularExpressions;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// The one place that reads and writes the package's naming conventions: the
    /// <c>Name_LOD{N}</c> LOD suffix (separator <c>_</c>, <c>-</c> or whitespace, case-insensitive),
    /// the collision suffixes (<c>_COL</c>, <c>_COL_Hull{N}</c>, <c>_COL_*</c>, <c>_Collider</c>,
    /// <c>_Collision</c>), the pipeline suffixes the transfer tools append, and the group
    /// key that pairs LOD and collision nodes of one asset. Every tool and library asks
    /// here; none keeps a regex of its own.
    /// </summary>
    internal static class MeshNaming
    {
        /// <summary>Highest number of LOD levels a Unity LODGroup supports; name-derived indices at or above this are rejected, not clamped.</summary>
        public const int MaxLodLevels = 8;

        const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.Compiled;
        // A match timeout bounds the backtracking on a hostile name (an asset name is user
        // input); every name these patterns see matches in microseconds.
        static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
        static readonly Regex LodSuffix = new Regex(@"^(.*?)([_\-\s]+LOD(\d+))$", Ci, MatchTimeout);
        static readonly Regex CollisionSuffix = new Regex(@"[_\-\s]+(?:COL(?:_\w+)?|Collider|Collision)$", Ci, MatchTimeout);
        static readonly Regex LodOrCollisionSuffix = new Regex(@"[_\-\s]+(LOD\d+|COL\w*|Collider|Collision)$", Ci, MatchTimeout);
        static readonly Regex GroupSuffixes = new Regex(@"(?:[_\-\s]+(?:LOD\d+|COL(?:_Hull\d+)?|Collider|Collision))+(?<instance>\.\d+)?$", Ci, MatchTimeout);
        static readonly Regex PipelineSuffixes = new Regex(@"(_wc|_repack|_uvTransfer|_optimized|_LOD\d+)+$", RegexOptions.Compiled, MatchTimeout);

        /// <summary>True when the name ends with a LOD suffix of any index (valid or not).</summary>
        public static bool HasLodSuffix(string name) => !string.IsNullOrEmpty(name) && LodSuffix.IsMatch(name);

        /// <summary>
        /// Splits <c>Base_LOD3</c> into "Base" and 3. False without a suffix, with an index
        /// that does not parse or overflows, or with an index outside the LODGroup range.
        /// </summary>
        public static bool TryParseLod(string name, out string baseName, out int lod)
        {
            baseName = name; lod = -1;
            if (string.IsNullOrEmpty(name)) return false;
            var m = LodSuffix.Match(name);
            if (!m.Success || !int.TryParse(m.Groups[3].Value, out lod) || lod < 0 || lod >= MaxLodLevels) { lod = -1; return false; }
            baseName = m.Groups[1].Value;
            return true;
        }

        /// <summary>The LOD index of the name, or -1.</summary>
        public static int LodIndex(string name) => TryParseLod(name, out _, out int lod) ? lod : -1;

        /// <summary>The name without its LOD suffix (any index), or the name itself.</summary>
        public static string StripLod(string name) => SplitLodSuffix(name, out _);

        /// <summary>
        /// The name without its LOD suffix, with the suffix text exactly as written
        /// ("-LOD1", " LOD2", "_lod0") in <paramref name="suffix"/> so a rename can
        /// put it back unchanged; suffix is "" when there is none.
        /// </summary>
        public static string SplitLodSuffix(string name, out string suffix)
        {
            suffix = "";
            if (string.IsNullOrEmpty(name)) return name;
            var m = LodSuffix.Match(name);
            if (!m.Success) return name;
            suffix = m.Groups[2].Value;
            return m.Groups[1].Value;
        }

        /// <summary>The canonical LOD name: <c>base_LOD{lod}</c>.</summary>
        public static string LodName(string baseName, int lod) => baseName + "_LOD" + lod;

        /// <summary>
        /// Collision node or mesh: ends with <c>_COL</c>, <c>_COL_Hull{N}</c> or another
        /// <c>_COL_*</c> form, <c>_Collider</c> or <c>_Collision</c> (case-insensitive). A
        /// word that merely starts with COL (<c>_COLOR</c>) is not one.
        /// </summary>
        public static bool IsCollision(string name) => !string.IsNullOrEmpty(name) && CollisionSuffix.IsMatch(name);

        /// <summary>The name without its collision suffix, or the name itself.</summary>
        public static string StripCollision(string name)
            => string.IsNullOrEmpty(name) ? name : CollisionSuffix.Replace(name, "");

        /// <summary>True when a clean base name still carries a LOD or collision suffix.</summary>
        public static bool HasLodOrCollisionSuffix(string name) => !string.IsNullOrEmpty(name) && LodOrCollisionSuffix.IsMatch(name);

        /// <summary>
        /// The stable key that pairs the LOD levels and collision nodes of one asset:
        /// every trailing LOD / COL / COL_Hull / Collider / Collision suffix removed.
        /// </summary>
        public static string GroupKey(string name) => string.IsNullOrEmpty(name) ? name : GroupSuffixes.Replace(name, "${instance}");

        /// <summary>
        /// The mesh name without the suffixes the transfer pipeline appends
        /// (<c>_wc</c>, <c>_repack</c>, <c>_uvTransfer</c>, <c>_optimized</c>, <c>_LOD{N}</c>, in any
        /// order), the base a generated LOD is named from.
        /// </summary>
        public static string StripPipelineSuffixes(string name) => string.IsNullOrEmpty(name) ? name : PipelineSuffixes.Replace(name, "");
    }
}
