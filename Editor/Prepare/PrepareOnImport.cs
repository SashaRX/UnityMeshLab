// PrepareOnImport.cs — prepares a model the first time it is imported, for a recipe with Run on
// Import on (Project Settings ▸ Mesh Lab ▸ Prepare: Recipes; off in every recipe by default).
// A model is new when Unity imports it without import settings: no .meta yet, as a file just
// added to the project. The model postprocessor (Uv2AssetPostprocessor) reports each import, so
// no postprocessor is added to the models' import (a new one would reimport every model of a
// project). The import may run in an import worker (Parallel Import), so a new model is noted
// as a file under Library/, and the notes are the queue: the main editor takes them once its
// imports are over (PrepareOnImportWatcher, an all-assets callback, which adds no import
// dependency) or after a domain reload, and removes each only once its model has been taken
// through its recipe's chain as Prepare ▸ Apply would, the result logged. In play mode or while
// a player builds, the notes wait. A reimport never runs it.

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

        static bool scheduled;

        /// <summary>Where new models are noted: Library/, shared by the editor and its import workers.</summary>
        static string Notes => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", NotesFolder);

        static string NoteOf(string path) => Path.Combine(Notes, Hash128.Compute(path) + ".txt");

        /// <summary>A model imported, in whatever process: a new one is noted for the main editor.</summary>
        internal static void Imported(string path, bool isNew)
        {
            if (!isNew || string.IsNullOrEmpty(path)) return;
            try
            {
                Directory.CreateDirectory(Notes);
                File.WriteAllText(NoteOf(path), path);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                UvtLog.Warn($"[{PrepareRunner.Tag}] '{path}' is new, but could not be noted for Run on Import: {error.Message}");
            }
        }

        /// <summary>Whether <paramref name="path"/> waits to be prepared as a new model.</summary>
        internal static bool IsPending(string path) => File.Exists(NoteOf(path));

        /// <summary>
        /// In the main editor, after imports: a waiting model that was moved waits at its new
        /// path (<paramref name="moved"/>[i] from <paramref name="movedFrom"/>[i]), and the
        /// waiting models are prepared once the editor is idle.
        /// </summary>
        internal static void AfterImports(IReadOnlyList<string> moved, IReadOnlyList<string> movedFrom)
        {
            for (int i = 0; i < Math.Min(moved.Count, movedFrom.Count); i++)
            {
                if (!IsPending(movedFrom[i])) continue;
                Imported(moved[i], true);
                Remove(NoteOf(movedFrom[i]));
            }
            Schedule();
        }

        // After a domain reload (a script change, entering play mode) the notes are still there.
        [InitializeOnLoadMethod]
        static void AfterReload() => Schedule();

        static void Schedule()
        {
            if (scheduled || AssetDatabase.IsAssetImportWorkerProcess() || !Directory.Exists(Notes)) return;
            if (!Directory.EnumerateFiles(Notes, "*.txt").Any()) return;
            scheduled = true;
            EditorApplication.delayCall += RunPending;
        }

        static void RunPending()
        {
            scheduled = false;
            // In play mode or while a player builds, the models wait for the editor to be idle again.
            if (EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer)
            {
                scheduled = true;
                EditorApplication.delayCall += RunPending;
                return;
            }
            if (!Directory.Exists(Notes)) return;
            var recipes = MeshLabProjectSettings.Instance.recipes;
            bool runs = recipes != null && recipes.Any(r => r != null && r.runOnImport);
            foreach (string note in Directory.GetFiles(Notes, "*.txt"))
            {
                string path = Read(note);
                // A note is removed once its model is done with, prepared or not; a failure is
                // logged with the model's path and leaves the others to be prepared.
                try
                {
                    if (runs && path != null) Prepare(path, recipes);
                }
                catch (Exception error)
                {
                    UvtLog.Error($"[{PrepareRunner.Tag}] '{path}': preparing it on import failed: {error}");
                }
                finally { Remove(note); }
            }
        }

        static void Prepare(string path, IReadOnlyList<PrepareRecipe> recipes)
        {
            var result = Run(path, recipes);
            if (result == null) return;
            UvtLog.Info($"[{PrepareRunner.Tag}] '{path}' was imported for the first time: its recipe runs on import");
            result.Log();
        }

        static string Read(string note)
        {
            try { return File.ReadAllText(note); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                UvtLog.Verbose($"[{PrepareRunner.Tag}] a Run on Import note was not read: {error.Message}");
                return null;
            }
        }

        static void Remove(string note)
        {
            try { File.Delete(note); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                UvtLog.Verbose($"[{PrepareRunner.Tag}] a Run on Import note was not removed: {error.Message}");
            }
        }

        /// <summary>
        /// Applies the chain of the model at <paramref name="path"/> when its recipe runs on
        /// import; null when it is no model (any more), or takes no recipe that does.
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
        {
            // A deleted model's note goes when the queue is run: the model is no model any more.
            if (importedAssets.Length + deletedAssets.Length + movedAssets.Length == 0) return;
            PrepareOnImport.AfterImports(movedAssets, movedFromAssetPaths);
        }
    }
}
