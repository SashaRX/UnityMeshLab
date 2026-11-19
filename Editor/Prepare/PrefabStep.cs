// PrefabStep.cs — the Prefab step, the end of the chain: where a model's preparation is kept.
// A model asset (an FBX) gets a Prefab Variant next to it, named as the model without its
// class prefix and with spaces as underscores (SM_Old Chair.fbx → Old_Chair.prefab); the steps
// before it edit that variant: a new one is the model's instance, an existing one (a variant
// of the same model) is edited as it is, and a prefab of another kind in its place blocks
// Apply. A prefab asset is edited in place, a scene object in place with Undo.
//
// What is kept is a prefab, so it is left as one: a scene object takes the prefab's name (its
// class prefix off, spaces as underscores; a variant's root is named by its file), and, when
// the recipe asks, the whole hierarchy is marked Static. A model with LODs gets, on each object
// that holds a LODGroup, the components the project lists for it by type name (Project Settings ▸
// Mesh Lab ▸ Prepare: LOD Group Components: a lightmapper's LOD sharing, say); the package
// names no such type itself.
//
// PrefabOutput opens the hierarchy for a model: the existing variant's contents, or an
// instance of the model in a preview scene that Save stores as the new variant.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        readonly bool makeStatic;
        readonly IReadOnlyList<string> lodComponents;

        /// <param name="makeStatic">Whether the hierarchy is marked Static.</param>
        /// <param name="lodComponents">The components each LODGroup's object gets, by full or short type name; none when null.</param>
        internal PrefabStep(bool makeStatic = false, IReadOnlyList<string> lodComponents = null)
        {
            this.makeStatic = makeStatic;
            this.lodComponents = (lodComponents ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct().ToList();
        }

        public string Name => StepName;

        public string Purpose => "An FBX gets a prefab variant next to it that keeps the changes; a prefab or a scene object is changed in place. "
            + "A scene object takes the prefab's name (no class prefix); the hierarchy can be marked Static.";

        public string SkipReason(PrepareContext context)
            => context.Target.Kind == PrepareTargetKind.ModelAsset && context.Output == null ? "the model was not opened" : null;

        public void Plan(PrepareContext context, PrepareReport report) => Run(context, report, false);

        // The runner saves the variant once every step has applied (PrepareRunner).
        public void Apply(PrepareContext context, PrepareReport report) => Run(context, report, true);

        void Run(PrepareContext context, PrepareReport report, bool apply)
        {
            var output = context.Output;
            if (context.Target.Kind == PrepareTargetKind.ModelAsset)
            {
                if (output.Refusal != null) { report.Add(PrepareLevel.Error, $"'{output.Path}'", Check, output.Refusal); return; }
                if (output.Creates) report.Add(PrepareLevel.Changed, $"'{output.Path}'", Check, "a new prefab variant of the model, next to it");
                else report.Add(PrepareLevel.Unchanged, $"'{output.Path}'", Check, "the model's prefab variant; the steps change it");
            }
            var root = context.Root;
            if (root == null) return;
            if (context.Target.Kind == PrepareTargetKind.SceneObject) Rename(context, report, apply, root);
            if (makeStatic) MakeStatic(context, report, apply, root);
            AddLodComponents(context, report, apply, root);
            if (report.Lines.Count == 0) report.Add(PrepareLevel.Unchanged, $"'{root.name}'", "Prefab", "changed in place; nothing of the step's to change");
        }

        // A scene object made into a prefab is named as the prefab: without the model's class prefix.
        static void Rename(PrepareContext context, PrepareReport report, bool apply, GameObject root)
        {
            string name = PrefabName(root.name);
            if (name == root.name) return;
            report.Add(PrepareLevel.Changed, $"'{root.name}'", "Name", $"→ '{name}': a prefab has no class prefix");
            if (!apply) return;
            context.Record(root, "Rename");
            root.name = name;
            PrepareContext.Changed(root);
        }

        /// <summary>Every static flag.</summary>
        internal static readonly StaticEditorFlags AllStatic = ((StaticEditorFlags[])Enum.GetValues(typeof(StaticEditorFlags)))
            .Aggregate((StaticEditorFlags)0, (all, flag) => all | flag);

        static void MakeStatic(PrepareContext context, PrepareReport report, bool apply, GameObject root)
        {
            // isStatic is true with any one flag: Static here is every flag, as the inspector's checkbox sets.
            var objects = root.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject)
                .Where(go => (GameObjectUtility.GetStaticEditorFlags(go) & AllStatic) != AllStatic).ToList();
            if (objects.Count == 0) return;
            report.Add(PrepareLevel.Changed, $"'{root.name}'", "Static",
                $"{objects.Count} {(objects.Count == 1 ? "object" : "objects")} marked Static");
            if (!apply) return;
            foreach (var go in objects)
            {
                context.Record(go, "Mark Static");
                GameObjectUtility.SetStaticEditorFlags(go, AllStatic);
                PrepareContext.Changed(go);
            }
        }

        // Each object with a LODGroup gets the listed components it lacks; one already there stays as it is.
        void AddLodComponents(PrepareContext context, PrepareReport report, bool apply, GameObject root)
        {
            if (lodComponents.Count == 0) return;
            var groups = root.GetComponentsInChildren<LODGroup>(true);
            if (groups.Length == 0) return;
            foreach (string name in lodComponents)
            {
                var type = ComponentSurfaceBinding.FindType(name, typeof(Component));
                if (type == null)
                {
                    report.Add(PrepareLevel.Warning, $"'{root.name}'", "LOD", $"there is no component type '{name}' in this project; the LOD group does not get it");
                    continue;
                }
                foreach (var group in groups)
                {
                    if (group.GetComponent(type) != null) continue;
                    string subject = $"'{group.name}'";
                    if (apply && context.AddComponent(group.gameObject, type) == null)
                    {
                        // Unity refused to add it (a script in an editor assembly, say) and said why in the Console.
                        report.Add(PrepareLevel.Warning, subject, "LOD", $"Unity did not add {type.Name} (see the Console)");
                        continue;
                    }
                    report.Add(PrepareLevel.Changed, subject, "LOD", $"{type.Name} added: the model has LODs");
                }
            }
        }

        /// <summary>A prefab's name for a model's or object's name: its class prefix (SM_, SK_) off, each space an underscore.</summary>
        internal static string PrefabName(string name)
        {
            if (MeshNaming.Vocabulary.Default.SplitClassPrefix(name, out string rest) != null) name = rest;
            return name.Replace(' ', '_');
        }

        /// <summary>
        /// The variant's path for the model at <paramref name="modelPath"/>: in the model's
        /// folder, named as the model without its class prefix (SM_, SK_), each space an underscore.
        /// </summary>
        internal static string VariantPath(string modelPath)
        {
            string folder = Path.GetDirectoryName(modelPath)?.Replace('\\', '/');
            string name = PrefabName(Path.GetFileNameWithoutExtension(modelPath));
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
