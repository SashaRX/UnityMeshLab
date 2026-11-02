using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// The one place that reads and writes the package's naming conventions: the
    /// <c>Name_LOD{N}</c> LOD suffix (separator <c>_</c>, <c>-</c> or whitespace, case-insensitive),
    /// the collision suffixes (<c>_COL</c>, <c>_COL_Hull{N}</c>, <c>_COL_*</c>, <c>_Collider</c>,
    /// <c>_Collision</c>, the last two optionally numbered), the pipeline suffixes the
    /// transfer tools append, and the group key that pairs LOD and collision nodes of one
    /// asset. Every tool and library asks here; none keeps a regex of its own.
    /// </summary>
    internal static class MeshNaming
    {
        /// <summary>Highest number of LOD levels a Unity LODGroup supports; name-derived indices at or above this are rejected, not clamped.</summary>
        public const int MaxLodLevels = 8;

        const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.Compiled;
        // A match timeout bounds the backtracking on a hostile name (an asset name is user
        // input); every name these patterns see matches in microseconds.
        static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
        static readonly Regex LodSuffix = new Regex(@"^(.*?)([_\-\s]+LOD(\d+)(?<instance>\.\d+)?)$", Ci, MatchTimeout);
        static readonly Regex CollisionSuffix = new Regex(@"[_\-\s]+(?:COL(?:_\w+)?|(?:Collider|Collision)(?:[_\-\s]*\d+)?)(?<instance>\.\d+)?$", Ci, MatchTimeout);
        static readonly Regex LodOrCollisionSuffix = new Regex(@"[_\-\s]+(LOD\d+|COL\w*|Collider|Collision)(?:\.\d+)?$", Ci, MatchTimeout);
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
            if (!TryParseLodSuffix(name, out baseName, out lod)) return false;
            if (lod < MaxLodLevels) return true;
            baseName = name; lod = -1; return false;
        }

        /// <summary>Parses an unrestricted source LOD index and preserves its instance key for export renumbering.</summary>
        internal static bool TryParseLodSuffix(string name, out string baseName, out int lod)
        {
            baseName = name; lod = -1;
            if (string.IsNullOrEmpty(name)) return false;
            var m = LodSuffix.Match(name);
            if (!m.Success || !int.TryParse(m.Groups[3].Value, out lod) || lod < 0) { lod = -1; return false; }
            baseName = m.Groups[1].Value + m.Groups["instance"].Value;
            return true;
        }

        /// <summary>The LOD index of the name, or -1.</summary>
        public static int LodIndex(string name) => TryParseLod(name, out _, out int lod) ? lod : -1;

        /// <summary>The name without its LOD suffix (any index), or the name itself.</summary>
        public static string StripLod(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var match = LodSuffix.Match(name);
            return match.Success ? match.Groups[1].Value + match.Groups["instance"].Value : name;
        }

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
        /// <c>_COL_*</c> form, <c>_Collider</c> or <c>_Collision</c>, either of the last two
        /// optionally numbered (<c>_Collider_01</c>, <c>_Collision2</c>), case-insensitive. A
        /// word that merely starts with COL (<c>_COLOR</c>) is not one.
        /// </summary>
        public static bool IsCollision(string name) => !string.IsNullOrEmpty(name) && CollisionSuffix.IsMatch(name);

        /// <summary>A generated collision container or hull owned by one exact sidecar key.</summary>
        internal static bool IsCollisionOf(string name, string key)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(key)) return false;
            if (name == key + "_COL") return true;
            string prefix = key + "_COL_Hull";
            return name.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(name.Substring(prefix.Length), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int hull) && hull >= 0;
        }

        /// <summary>The name without its collision suffix, or the name itself.</summary>
        public static string StripCollision(string name)
            => string.IsNullOrEmpty(name) ? name : CollisionSuffix.Replace(name, "${instance}");

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

        // ── Name tags ──

        // The trailing layers a name can carry, in any order: Chair_wc_repack_LOD0, Seat_LOD0.001_wc.
        static readonly Regex InstanceTail = new Regex(@"\.\d+$", RegexOptions.Compiled, MatchTimeout);
        static readonly Regex PipelineTag = new Regex(@"_(wc|repack|uvTransfer|optimized)$", RegexOptions.Compiled, MatchTimeout);
        static readonly Regex LodTail = new Regex(@"[_\-\s]+LOD(\d+)$", Ci, MatchTimeout);
        static readonly Regex HullSuffix = new Regex(@"^[_\-\s]+COL_Hull(\d+)$", Ci, MatchTimeout);
        static readonly Regex WordSeparators = new Regex(@"[_\-\s.]+", RegexOptions.Compiled, MatchTimeout);
        // camelCase, ABBRWord and letter/digit boundaries: "BaseAtlas01" → Base Atlas 01, "UVMap" → UV Map.
        static readonly Regex WordBoundaries = new Regex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=\d)|(?<=\d)(?=[A-Za-z])",
            RegexOptions.Compiled, MatchTimeout);

        /// <summary>
        /// The words a name tag is read against: the class prefixes an asset name starts with,
        /// and the keywords that name a surface. A word names a surface when it starts with one
        /// of its keywords, case-insensitively ("Wood", "Wooden", "wood"); a keyword inside a
        /// word ("Textile") does not count.
        /// </summary>
        internal sealed class Vocabulary
        {
            /// <summary>Static and skeletal mesh prefixes, and the reference surfaces: wood, concrete, metal, glass, plastic, tile, cardboard.</summary>
            public static readonly Vocabulary Default = new Vocabulary(new[] {"SM", "SK"}, new[]
            {
                ("wood", "Wood"), ("concrete", "Concrete"), ("metal", "Metal"), ("glass", "Glass"),
                ("plastic", "Plastic"), ("tile", "Tile"), ("cardboard", "Cardboard"),
            });

            /// <summary>Class prefixes in canonical case, without their separator ("SM").</summary>
            public readonly string[] ClassPrefixes;
            /// <summary>Keyword → surface, tried in order; the first keyword a word starts with names its surface.</summary>
            public readonly (string keyword, string surface)[] SurfaceKeywords;
            readonly Regex classPrefix;

            public Vocabulary(string[] classPrefixes, (string keyword, string surface)[] surfaceKeywords)
            {
                ClassPrefixes = (classPrefixes ?? Array.Empty<string>()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
                SurfaceKeywords = (surfaceKeywords ?? Array.Empty<(string, string)>())
                    .Where(k => !string.IsNullOrEmpty(k.keyword) && !string.IsNullOrEmpty(k.surface)).ToArray();
                classPrefix = ClassPrefixes.Length == 0 ? null : new Regex(
                    "^(" + string.Join("|", ClassPrefixes.Select(Regex.Escape)) + @")[_\-\s]+", Ci, MatchTimeout);
            }

            /// <summary>The class prefix the name starts with, in its canonical case, and the name after it and its separator.</summary>
            internal string SplitClassPrefix(string name, out string rest)
            {
                rest = name;
                var match = classPrefix?.Match(name);
                if (match == null || !match.Success || match.Length == name.Length) return null;
                rest = name.Substring(match.Length);
                return ClassPrefixes.First(p => string.Equals(p, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            }

            /// <summary>The surface a word names, or null.</summary>
            internal string SurfaceOf(string word)
            {
                foreach (var (keyword, surface) in SurfaceKeywords)
                    if (word.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return surface;
                return null;
            }
        }

        /// <summary>What an asset or node name says, as <see cref="ParseTags"/> reads it.</summary>
        internal sealed class Tags
        {
            /// <summary>The name as given.</summary>
            public string Name = "";
            /// <summary>The class prefix in canonical case ("SM", "SK"), or null.</summary>
            public string ClassPrefix;
            /// <summary>The name without class prefix, LOD, collision and pipeline suffixes and instance number: "University_Bench_Straight _Seat_Wood".</summary>
            public string Body = "";
            /// <summary>The words of <see cref="Body"/>, split at separators, case changes and digits.</summary>
            public string[] Words = Array.Empty<string>();
            /// <summary>The surfaces the words name, in name order, each once.</summary>
            public string[] Surfaces = Array.Empty<string>();
            /// <summary>The LOD index (0 to <see cref="MaxLodLevels"/> - 1), or -1.</summary>
            public int Lod = -1;
            /// <summary>Whether the name is a collision node or mesh (<see cref="IsCollision"/>).</summary>
            public bool Collision;
            /// <summary>N of a <c>_COL_Hull{N}</c> suffix, or -1.</summary>
            public int Hull = -1;
            /// <summary>The pipeline suffixes the transfer tools append (wc, repack, uvTransfer, optimized), in name order.</summary>
            public string[] PipelineTags = Array.Empty<string>();
            /// <summary>The numeric instance suffix a DCC appends (".001"), or null.</summary>
            public string Instance;

            /// <summary>The first surface the name names, or null.</summary>
            public string Surface => Surfaces.Length > 0 ? Surfaces[0] : null;
        }

        /// <summary>
        /// Reads the tags of an asset or node name: the class prefix it starts with
        /// (<paramref name="vocabulary"/>, <see cref="Vocabulary.Default"/> when null), its LOD
        /// and collision suffixes, the pipeline suffixes and instance number the tools and DCCs
        /// append, and the words of what is left with the surfaces they name. Separators are
        /// <c>_</c>, <c>-</c>, whitespace and dots, so "University_Bench_Straight _Seat_Wood_COL"
        /// reads as a collision node of the words University Bench Straight Seat Wood, surface Wood.
        /// </summary>
        public static Tags ParseTags(string name, Vocabulary vocabulary = null)
        {
            var tags = new Tags {Name = name ?? ""};
            if (string.IsNullOrEmpty(name)) return tags;
            vocabulary = vocabulary ?? Vocabulary.Default;

            // Peel the trailing layers one at a time, whatever their order. LOD before collision:
            // a _COL_* form would take a LOD suffix after it as part of its own word.
            string core = name;
            var pipeline = new System.Collections.Generic.List<string>();
            for (bool peeled = true; peeled && core.Length > 0;)
            {
                Match layer;
                if ((layer = InstanceTail.Match(core)).Success) { if (tags.Instance == null) tags.Instance = layer.Value; }
                else if ((layer = PipelineTag.Match(core)).Success) pipeline.Insert(0, layer.Groups[1].Value);
                else if ((layer = LodTail.Match(core)).Success)
                {
                    if (tags.Lod < 0 && int.TryParse(layer.Groups[1].Value, out int lod) && lod < MaxLodLevels) tags.Lod = lod;
                }
                else if ((layer = CollisionSuffix.Match(core)).Success)
                {
                    tags.Collision = true;
                    var hull = HullSuffix.Match(layer.Value);
                    if (tags.Hull < 0 && hull.Success && int.TryParse(hull.Groups[1].Value, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out int hullIndex)) tags.Hull = hullIndex;
                }
                peeled = layer.Success && layer.Length > 0;
                if (peeled) core = core.Substring(0, layer.Index);
            }
            tags.PipelineTags = pipeline.ToArray();

            tags.ClassPrefix = vocabulary.SplitClassPrefix(core, out string body);
            tags.Body = body;
            tags.Words = WordSeparators.Split(body).Where(part => part.Length > 0)
                .SelectMany(part => WordBoundaries.Split(part)).Where(word => word.Length > 0).ToArray();
            tags.Surfaces = tags.Words.Select(vocabulary.SurfaceOf).Where(surface => surface != null).Distinct().ToArray();
            return tags;
        }
    }
}
