// PrepareBatch.cs — prepares the project's models from the command line:
//
//   Unity -batchmode -quit -projectPath <project> -executeMethod SashaRX.UnityMeshLab.PrepareBatch.Run
//         [-prepareFolders "Assets/Art;Assets/Props"] [-preparePreview] [-prepareReport <file>]
//
// Every model under the folders (Assets when none are given) that a recipe matches is taken
// through its chain, as Prepare ▸ Apply would; with -preparePreview each is planned and nothing
// changes. A model no recipe matches is counted and left. Each result is logged, the report
// file (when given) holds them all, and in batch mode Unity exits with 1 when a model was not
// applied (in a preview: would not be, an error blocking it) or a folder does not exist, else 0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Public only for Unity -executeMethod.</summary>
    public static class PrepareBatch
    {
        internal const string FoldersArgument = "-prepareFolders";
        internal const string PreviewArgument = "-preparePreview";
        internal const string ReportArgument = "-prepareReport";

        /// <summary>What the command line asks for.</summary>
        internal sealed class Options
        {
            internal List<string> folders = new List<string> { "Assets" };
            internal bool preview;
            internal string report;
        }

        /// <summary>A batch run: each prepared model's result, and how many models no recipe matched.</summary>
        internal sealed class Outcome
        {
            internal readonly List<PrepareResult> results = new List<PrepareResult>();
            internal int unmatched;
            internal bool preview;

            /// <summary>Whether a model was not applied, or in a preview would not be.</summary>
            internal bool Failed => results.Any(r => r.Refused != null || (preview && r.BlocksApply));
        }

        /// <summary>The options in <paramref name="args"/>; a name is matched whatever its case.</summary>
        internal static Options Parse(IReadOnlyList<string> args)
        {
            var options = new Options();
            int i = 0;
            while (i < args.Count)
            {
                string arg = args[i];
                // A name that takes a value consumes the argument after it, unless that is another name.
                string next = i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal) ? args[i + 1] : null;
                bool valued = next != null && (Is(arg, FoldersArgument) || Is(arg, ReportArgument));
                if (Is(arg, PreviewArgument)) options.preview = true;
                else if (valued && Is(arg, FoldersArgument))
                    options.folders = next.Split(';').Select(f => f.Trim().TrimEnd('/')).Where(f => f.Length > 0).ToList();
                else if (valued) options.report = next;
                i += valued ? 2 : 1;
            }
            if (options.folders.Count == 0) options.folders.Add("Assets");
            return options;
        }

        static bool Is(string arg, string name) => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase);

        /// <summary>The -executeMethod entry: prepares the models the command line names and exits in batch mode.</summary>
        public static void Run()
        {
            var options = Parse(Environment.GetCommandLineArgs());
            var missing = options.folders.Where(f => !AssetDatabase.IsValidFolder(f)).ToList();
            if (missing.Count > 0)
            {
                UvtLog.Error($"[{PrepareRunner.Tag}] no such folder: {string.Join(", ", missing)}");
                Exit(1);
                return;
            }
            var outcome = Prepare(Models(options.folders), MeshLabProjectSettings.Instance.recipes, !options.preview);
            string report = Report(outcome);
            if (outcome.Failed) UvtLog.Error(report);
            else UvtLog.Info(report);
            if (!string.IsNullOrEmpty(options.report)) File.WriteAllText(options.report, report);
            Exit(outcome.Failed ? 1 : 0);
        }

        static void Exit(int code)
        {
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        /// <summary>The model assets under <paramref name="folders"/>, by path.</summary>
        internal static List<string> Models(IEnumerable<string> folders)
            => AssetDatabase.FindAssets("t:Model", folders.ToArray()).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => AssetImporter.GetAtPath(p) is ModelImporter).Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal).ToList();

        /// <summary>Each model of <paramref name="paths"/> a recipe matches, through its chain: applied, or planned.</summary>
        internal static Outcome Prepare(IEnumerable<string> paths, IReadOnlyList<PrepareRecipe> recipes, bool apply)
        {
            var outcome = new Outcome { preview = !apply };
            foreach (string path in paths)
            {
                var target = PrepareTarget.ForAsset(path);
                if (target == null) continue;
                var chain = PrepareRecipes.Chain(target, recipes);
                if (chain.Recipe == null)
                {
                    outcome.unmatched++;
                    continue;
                }
                outcome.results.Add(apply ? PrepareRunner.Apply(target, chain) : PrepareRunner.Preview(target, chain));
            }
            return outcome;
        }

        /// <summary>The run in words: the counts, then each model's result.</summary>
        internal static string Report(Outcome outcome)
        {
            int failed = outcome.results.Count(r => r.Refused != null || (outcome.preview && r.BlocksApply));
            int changed = outcome.results.Count(r => r.Changes);
            var text = new StringBuilder($"[{PrepareRunner.Tag}] batch {(outcome.preview ? "preview" : "apply")}: ")
                .Append($"{MeshValidation.Plural(outcome.results.Count, "model", "models")} prepared, {changed} with changes, ")
                .Append($"{failed} {(outcome.preview ? "blocked" : "not applied")}, {outcome.unmatched} matched no recipe");
            foreach (var result in outcome.results) text.Append('\n').Append(result);
            return text.ToString();
        }
    }
}
