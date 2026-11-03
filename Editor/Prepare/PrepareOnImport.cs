// PrepareOnImport.cs — prepares a model the first time it is imported, for a recipe with Run on
// Import on (Project Settings ▸ Mesh Lab ▸ Prepare: Recipes; off in every recipe by default).
// A model is new when Unity imports it without import settings: no .meta yet, as a file just
// added to the project. The model postprocessor (Uv2AssetPostprocessor) reports each import, so
// no postprocessor is added to the models' import (a new one would reimport every model of a
// project). Once the import is over, each new model is taken through its recipe's chain as
// Prepare ▸ Apply would, one at a time, and the result logged. A reimport never runs it.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    internal static class PrepareOnImport
    {
        static readonly List<string> pending = new List<string>();
        static bool scheduled;

        /// <summary>A model the editor imported; a new one is prepared once the import is over.</summary>
        internal static void Imported(string path, bool isNew)
        {
            if (!isNew || string.IsNullOrEmpty(path) || pending.Contains(path)) return;
            pending.Add(path);
            if (scheduled) return;
            scheduled = true;
            EditorApplication.delayCall += RunPending;
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
}
