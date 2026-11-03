// PrepareRecipe.cs — which prepare steps a target takes, and with what settings. A recipe
// (Project Settings ▸ Mesh Lab ▸ Prepare: Recipes) names the targets it is for by a name
// pattern and a folder pattern, and turns each step of the chain (Preset → Materials →
// Colliders → Prefab) on or off with the step's settings. A target takes the first recipe of
// the list that matches it: by its own name and folder, or, for a prefab or a scene object made
// from a model, by the model's. The default list holds one recipe, for SM_ models.
//
// PrepareChain is a target's chain: the recipe it matched and why, and the four steps, a step
// the recipe turns off (or every step, when no recipe matches) skipped with the reason, so the
// report says why each step did not act.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Presets;

namespace SashaRX.UnityMeshLab
{
    /// <summary>The targets a recipe is for and the steps it takes them through.</summary>
    [Serializable]
    internal sealed class PrepareRecipe
    {
        /// <summary>The recipe's name in the settings and the report.</summary>
        public string name = "Static meshes";

        /// <summary>
        /// The name the target (or its model) must have: '*' stands for any run of characters,
        /// '?' for one; several patterns separated by ';'; empty matches any name. Case is ignored.
        /// </summary>
        public string namePattern = "SM_*";

        /// <summary>
        /// The folder the target (or its model) must be in, or under: '*' stands for one folder's
        /// name or part of it, '**' for any path; several separated by ';'; empty matches any folder.
        /// </summary>
        public string folderPattern = "";

        public bool preset = true;

        /// <summary>The model importer preset the Preset step applies; none skips the step.</summary>
        public Preset importerPreset;

        /// <summary>Whether the preset's LOD screen percentages are applied too.</summary>
        public bool presetLodScreenPercentages;

        public bool materials = true;

        public bool colliders = true;

        /// <summary>Whether each collider gets its surface (Project Settings ▸ Mesh Lab ▸ Prepare: Collider Surfaces).</summary>
        public bool colliderSurfaces = true;

        /// <summary>The LOD level a model without collision nodes collides with; -1 for its coarsest.</summary>
        public int colliderFallbackLod = -1;

        public bool prefab = true;

        /// <summary>The recipe the project starts with: SM_ models through every step.</summary>
        internal static PrepareRecipe StaticMeshes() => new PrepareRecipe();

        /// <summary>The recipe's chain, a step it turns off skipped with the reason.</summary>
        internal List<IPrepareStep> Steps()
        {
            string off = $"off in the recipe '{name}'";
            return new List<IPrepareStep>
            {
                preset ? new PresetStep(importerPreset, presetLodScreenPercentages) : (IPrepareStep)new SkippedStep(new PresetStep(null), off),
                materials ? new MaterialStep() : (IPrepareStep)new SkippedStep(new MaterialStep(), off),
                colliders ? Colliders() : (IPrepareStep)new SkippedStep(new ColliderStep(), off),
                prefab ? new PrefabStep() : (IPrepareStep)new SkippedStep(new PrefabStep(), off),
            };
        }

        ColliderStep Colliders()
        {
            var step = colliderSurfaces ? new ColliderStep(SurfaceBinding.FromSettings(), SurfaceBinding.Vocabulary()) : new ColliderStep();
            step.FallbackLod = colliderFallbackLod;
            return step;
        }

        /// <summary>Whether a name and a folder (an asset's, without a trailing '/') match the recipe's patterns.</summary>
        internal bool Matches(string targetName, string folder)
            => PrepareRecipes.NameMatches(namePattern, targetName) && PrepareRecipes.FolderMatches(folderPattern, folder);

        internal string Patterns()
        {
            string names = string.IsNullOrWhiteSpace(namePattern) ? "any name" : $"'{namePattern.Trim()}'";
            return string.IsNullOrWhiteSpace(folderPattern) ? names : $"{names} in '{folderPattern.Trim()}'";
        }
    }

    /// <summary>A step the target does not take, with the reason; its name and purpose are the step's.</summary>
    internal sealed class SkippedStep : IPrepareStep
    {
        internal SkippedStep(IPrepareStep step, string reason)
        {
            Step = step; Reason = reason;
        }

        /// <summary>The step that is skipped.</summary>
        internal IPrepareStep Step { get; }

        internal string Reason { get; }

        public string Name => Step.Name;

        public string Purpose => Step.Purpose;

        public string SkipReason(PrepareContext context) => Reason;

        public void Plan(PrepareContext context, PrepareReport report) { /* skipped: never planned */ }

        public void Apply(PrepareContext context, PrepareReport report) { /* skipped: never applied */ }
    }

    /// <summary>A target's chain: the recipe it matched (or none) and why, and its steps.</summary>
    internal sealed class PrepareChain
    {
        internal PrepareChain(PrepareRecipe recipe, string match, IReadOnlyList<IPrepareStep> steps)
        {
            Recipe = recipe; Match = match; Steps = steps;
        }

        /// <summary>The recipe the target matched; null when none did.</summary>
        internal PrepareRecipe Recipe { get; }

        /// <summary>Which recipe and why ("Recipe 'Static meshes': the name 'SM_Bench' matches 'SM_*'"), or that none matches.</summary>
        internal string Match { get; }

        internal IReadOnlyList<IPrepareStep> Steps { get; }
    }

    internal static class PrepareRecipes
    {
        /// <summary>What a recipe is matched against: the target's name and folder, and its model's.</summary>
        internal readonly struct Subject
        {
            internal Subject(string what, string name, string folder)
            {
                What = what; Name = name; Folder = folder ?? "";
            }

            /// <summary>"the name", "its model".</summary>
            internal string What { get; }
            internal string Name { get; }
            internal string Folder { get; }

            public override string ToString() => string.IsNullOrEmpty(Folder) ? $"{What} '{Name}'" : $"{What} '{Name}' in '{Folder}'";
        }

        /// <summary>The chain the first of <paramref name="recipes"/> that matches <paramref name="target"/> gives it.</summary>
        internal static PrepareChain Chain(PrepareTarget target, IEnumerable<PrepareRecipe> recipes)
        {
            var subjects = Subjects(target);
            var (recipe, match) = Match(subjects, recipes);
            if (recipe != null) return new PrepareChain(recipe, match, recipe.Steps());
            string reason = $"no recipe matches '{target.Name}'";
            // The chain's steps, for their names and purposes; no surface settings needed.
            var steps = new PrepareRecipe { colliderSurfaces = false }.Steps();
            return new PrepareChain(null, match, steps.Select(s => (IPrepareStep)new SkippedStep(s, reason)).ToList());
        }

        /// <summary>
        /// The first recipe one of <paramref name="subjects"/> matches, with why; (null, why none)
        /// when no recipe matches any.
        /// </summary>
        internal static (PrepareRecipe recipe, string match) Match(IReadOnlyList<Subject> subjects, IEnumerable<PrepareRecipe> recipes)
        {
            // In the recipes' order, then the subjects': the first pair that matches.
            var (recipe, subject) = (recipes ?? Enumerable.Empty<PrepareRecipe>()).Where(r => r != null)
                .SelectMany(r => subjects.Where(s => r.Matches(s.Name, s.Folder)).Select(s => (r, s)))
                .FirstOrDefault();
            if (recipe != null) return (recipe, $"Recipe '{recipe.name}': {subject} matches {recipe.Patterns()}");
            string none = subjects.Count > 1 ? $"neither {string.Join(" nor ", subjects)} matches a recipe" : $"{subjects.FirstOrDefault()} matches no recipe";
            return (null, $"No recipe: {none}; add one in Project Settings ▸ Mesh Lab ▸ Prepare: Recipes");
        }

        /// <summary>The target's name and folder, then its model's when it is a prefab or a scene object made from one.</summary>
        internal static List<Subject> Subjects(PrepareTarget target)
        {
            var subjects = new List<Subject> { new Subject("the name", target.Name, FolderOf(target.AssetPath ?? target.SceneObject?.scene.path)) };
            string model = target.ModelPath;
            if (model != null && model != target.AssetPath)
                subjects.Add(new Subject("its model", Path.GetFileNameWithoutExtension(model), FolderOf(model)));
            return subjects;
        }

        static string FolderOf(string path) => string.IsNullOrEmpty(path) ? "" : (Path.GetDirectoryName(path) ?? "").Replace('\\', '/');

        /// <summary>Whether <paramref name="value"/> matches one of the ';'-separated patterns: '*' any run, '?' one character; empty matches all.</summary>
        internal static bool NameMatches(string patterns, string value)
            => Any(patterns, pattern => Regex(pattern, false).IsMatch(value ?? ""));

        /// <summary>
        /// Whether <paramref name="folder"/> is, or is under, a folder one of the ';'-separated
        /// patterns names: '*' within one folder's name, '**' across folders; empty matches all.
        /// </summary>
        internal static bool FolderMatches(string patterns, string folder)
            => Any(patterns, pattern => Regex(pattern.TrimEnd('/'), true).IsMatch((folder ?? "").TrimEnd('/')));

        static bool Any(string patterns, Func<string, bool> matches)
        {
            var list = (patterns ?? "").Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            return list.Count == 0 || list.Any(matches);
        }

        static System.Text.RegularExpressions.Regex Regex(string pattern, bool folder)
        {
            // '**/' is any number of folders, none included; '**' any path; '*' and '?' stay in one folder.
            string body = System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace(@"\*\*/", "\u0002").Replace(@"\*\*", "\u0001")
                .Replace(@"\*", folder ? "[^/]*" : ".*").Replace(@"\?", folder ? "[^/]" : ".")
                .Replace("\u0002", "(?:.*/)?").Replace("\u0001", ".*");
            return new System.Text.RegularExpressions.Regex("^" + body + (folder ? "(/.*)?$" : "$"),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
    }
}
