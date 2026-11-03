// PrepareOnImport.cs — prepares a model the first time it is imported, for a recipe with Run on
// Import on (Project Settings ▸ Mesh Lab ▸ Prepare: Recipes; off in every recipe by default).
// A model is new when Unity imports it without import settings: no .meta yet, as a file just
// added to the project. The model postprocessor (Uv2AssetPostprocessor) reports each import, so
// no postprocessor is added to the models' import (a new one would reimport every model of a
// project). The import may run in an import worker (Parallel Import), so a new model is noted
// as a file under Library/, which the main editor reads once its imports are over
// (PrepareOnImportWatcher, an all-assets callback, which adds no import dependency): each new
// model is then taken through its recipe's chain as Prepare ▸ Apply would, one at a time, and
// the result logged. A reimport never runs it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class PrepareOnImport
    {
        const string NotesFolder = "MeshLabPrepareOnImport";
        static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

        static readonly List<string> pending = new List<string>();
        static bool scheduled;

        /// <summary>Where new models are noted: Library/, shared by the editor and its import workers.</summary>
        static string Notes => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", NotesFolder);

        /// <summary>A model imported, in whatever process: a new one is noted for the main editor.</summary>
        internal static void Imported(string path, bool isNew)
        {
            if (!isNew || string.IsNullOrEmpty(path)) return;
            try
            {
                Directory.CreateDirectory(Notes);
                File.WriteAllText(Path.Combine(Notes, Hash128.Compute(path) + ".txt"), path);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                UvtLog.Warn($"[{PrepareRunner.Tag}] '{path}' is new, but could not be noted for Run on Import: {error.Message}");
            }
        }

        /// <summary>
        /// In the main editor, after imports: each noted model among <paramref name="imported"/>
        /// waits to be prepared once the editor is idle (a note left a day is dropped); a waiting
        /// model that was deleted no longer waits, and one that was moved waits at its new path
        /// (<paramref name="moved"/>[i] from <paramref name="movedFrom"/>[i]).
        /// </summary>
        internal static void AfterImports(IReadOnlyCollection<string> imported, IReadOnlyCollection<string> deleted,
            IReadOnlyList<string> moved, IReadOnlyList<string> movedFrom)
        {
            pending.RemoveAll(deleted.Contains);
            for (int i = 0; i < Math.Min(moved.Count, movedFrom.Count); i++)
            {
                int at = pending.IndexOf(movedFrom[i]);
                if (at >= 0) pending[at] = moved[i];
            }
            if (imported.Count > 0 && Directory.Exists(Notes)) ReadNotes(new HashSet<string>(imported, StringComparer.Ordinal));
            if (pending.Count == 0 || scheduled) return;
            scheduled = true;
            EditorApplication.delayCall += RunPending;
        }

        // Each note of a model just imported is taken (its model waits); one left a day is dropped.
        static void ReadNotes(HashSet<string> imported)
        {
            foreach (string note in Directory.GetFiles(Notes, "*.txt"))
            {
                try
                {
                    string path = File.ReadAllText(note);
                    if (imported.Contains(path))
                    {
                        File.Delete(note);
                        if (!pending.Contains(path)) pending.Add(path);
                    }
                    else if (DateTime.UtcNow - File.GetLastWriteTimeUtc(note) > StaleAfter) File.Delete(note);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    UvtLog.Verbose($"[{PrepareRunner.Tag}] a Run on Import note was not read: {error.Message}");
                }
            }
        }

        /// <summary>Whether <paramref name="path"/> waits to be prepared as a new model.</summary>
        internal static bool IsPending(string path) => pending.Contains(path);

        static void RunPending()
        {
            scheduled = false;
            var paths = pending.ToList();
            pending.Clear();
            var recipes = MeshLabProjectSettings.Instance.recipes;
            if (recipes == null || !recipes.Any(r => r != null && r.runOnImport)) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer) return;
            foreach (string path in paths)
            {
                var result = Run(path, recipes);
                if (result == null) continue;
                UvtLog.Info($"[{PrepareRunner.Tag}] '{path}' was imported for the first time: its recipe runs on import");
                result.Log();
            }
        }

        /// <summary>
        /// Applies the chain of the model at <paramref name="path"/> when its recipe runs on
        /// import; null when it is no model, or takes no recipe that does.
        /// </summary>
        internal static PrepareResult Run(string path, IReadOnlyList<PrepareRecipe> recipes)
        {
            var target = PrepareTarget.ForAsset(path);
            if (target == null || target.Kind != PrepareTargetKind.ModelAsset) return null;
            var chain = PrepareRecipes.Chain(target, recipes ?? Array.Empty<PrepareRecipe>());
            return chain.Recipe != null && chain.Recipe.runOnImport ? PrepareRunner.Apply(target, chain) : null;
        }
    }

    /// <summary>
    /// The main editor's call after each batch of imports, for PrepareOnImport. An all-assets
    /// callback only: it adds nothing to any asset's import.
    /// </summary>
    internal sealed class PrepareOnImportWatcher : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
            => PrepareOnImport.AfterImports(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
    }
}
