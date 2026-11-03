// PrefabStep.cs — the Prefab step, the end of the chain: where a model's preparation is kept.
// A model asset (an FBX) gets a Prefab Variant next to it, named as the model without its
// class prefix and with spaces as underscores (SM_Old Chair.fbx → Old_Chair.prefab); the steps
// before it edit that variant: a new one is the model's instance, an existing one (a variant
// of the same model) is edited as it is, and a prefab of another kind in its place blocks
// Apply. A prefab asset is edited in place, a scene object in place with Undo.
//
// PrefabOutput opens the hierarchy for a model: the existing variant's contents, or an
// instance of the model in a preview scene that Save stores as the new variant.

using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SashaRX.UnityMeshLab
{
    internal sealed class PrefabStep : IPrepareStep
    {
        internal const string StepName = "Prefab";
        const string Check = "Variant";

        public string Name => StepName;

        public string Purpose => "An FBX gets a prefab variant next to it that keeps the changes; a prefab or a scene object is changed in place.";

        public string SkipReason(PrepareContext context)
        {
            switch (context.Target.Kind)
            {
                case PrepareTargetKind.PrefabAsset: return "a prefab is changed in place";
                case PrepareTargetKind.SceneObject: return "a scene object is changed in place, with Undo";
                default: return context.Output == null ? "the model was not opened" : null;
            }
        }

        public void Plan(PrepareContext context, PrepareReport report) => Report(context, report);

        // The runner saves the variant once every step has applied (PrepareRunner).
        public void Apply(PrepareContext context, PrepareReport report) => Report(context, report);

        static void Report(PrepareContext context, PrepareReport report)
        {
            var output = context.Output;
            if (output.Refusal != null) report.Add(PrepareLevel.Error, $"'{output.Path}'", Check, output.Refusal);
            else if (output.Creates) report.Add(PrepareLevel.Changed, $"'{output.Path}'", Check, "a new prefab variant of the model, next to it");
            else report.Add(PrepareLevel.Unchanged, $"'{output.Path}'", Check, "the model's prefab variant; the steps change it");
        }

        /// <summary>
        /// The variant's path for the model at <paramref name="modelPath"/>: in the model's
        /// folder, named as the model without its class prefix (SM_, SK_), each space an underscore.
        /// </summary>
        internal static string VariantPath(string modelPath)
        {
            string folder = Path.GetDirectoryName(modelPath)?.Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(modelPath);
            if (MeshNaming.Vocabulary.Default.SplitClassPrefix(name, out string rest) != null) name = rest;
            name = name.Replace(' ', '_');
            return string.IsNullOrEmpty(folder) ? name + ".prefab" : $"{folder}/{name}.prefab";
        }
    }

    /// <summary>The hierarchy a model's steps edit, and where it is kept: its prefab variant.</summary>
    internal sealed class PrefabOutput : IDisposable
    {
        Scene preview;
        bool loaded;

        PrefabOutput(string path) { Path = path; }

        /// <summary>The variant's asset path.</summary>
        internal string Path { get; }

        /// <summary>Whether Save creates the variant (none exists yet).</summary>
        internal bool Creates { get; private set; }

        /// <summary>Why the model cannot be prepared into <see cref="Path"/>, or null.</summary>
        internal string Refusal { get; private set; }

        /// <summary>The hierarchy the steps edit; null with a <see cref="Refusal"/>.</summary>
        internal GameObject Root { get; private set; }

        /// <summary>
        /// Opens the variant at <paramref name="variantPath"/> of the model (or prefab) at
        /// <paramref name="sourcePath"/>: its contents when it exists, else an instance of the
        /// source in a preview scene.
        /// </summary>
        internal static PrefabOutput Open(string sourcePath, string variantPath)
        {
            var output = new PrefabOutput(variantPath);
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (source == null)
            {
                output.Refusal = "the model has no hierarchy to make a prefab of";
                return output;
            }
            if (File.Exists(variantPath) || AssetDatabase.LoadMainAssetAtPath(variantPath) != null)
            {
                var existing = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
                var basis = existing != null && PrefabUtility.GetPrefabAssetType(existing) == PrefabAssetType.Variant
                    ? PrefabUtility.GetCorrespondingObjectFromSource(existing) : null;
                if (basis == null || AssetDatabase.GetAssetPath(basis) != sourcePath)
                {
                    output.Refusal = "something else is in the variant's place: it is not a prefab variant of this model; move or rename it";
                    return output;
                }
                output.Root = PrefabUtility.LoadPrefabContents(variantPath);
                output.loaded = true;
                return output;
            }
            output.Creates = true;
            output.preview = EditorSceneManager.NewPreviewScene();
            output.Root = (GameObject)PrefabUtility.InstantiatePrefab(source, output.preview);
            return output;
        }

        /// <summary>Stores the hierarchy as the variant; false, with the reason, when the save fails.</summary>
        internal bool Save(out string error)
        {
            error = null;
            if (Root == null) { error = Refusal ?? "nothing to save"; return false; }
            try
            {
                PrefabUtility.SaveAsPrefabAsset(Root, Path, out bool success);
                if (!success) error = "Unity refused to save it";
            }
            catch (Exception ex) { error = ex.Message; }
            return error == null;
        }

        public void Dispose()
        {
            if (loaded && Root != null) PrefabUtility.UnloadPrefabContents(Root);
            else if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
            loaded = false;
            Root = null;
        }
    }
}
