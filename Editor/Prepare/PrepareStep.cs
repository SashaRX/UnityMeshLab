// PrepareStep.cs — one step of preparing a model for the game (Preset, Materials, Colliders,
// Prefab; which of them a target takes is its recipe's, PrepareRecipe): what it would change,
// changing nothing (Plan), and the change itself (Apply), each into the step's report line by
// line. A step that cannot act on a target says why it is skipped.
//
// PrepareTarget is what a run prepares: a prefab asset (its contents are loaded, edited and
// saved), a scene object (edited with Undo) or a model asset (its importer; its hierarchy is
// the model's own and is edited through its prefab variant, PrefabStep). PrepareContext is what the steps of one
// run share: the target, the hierarchy they edit, and the edits themselves, with Undo for a
// scene object.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    internal interface IPrepareStep
    {
        /// <summary>The step's name in the chain and the report ("Colliders").</summary>
        string Name { get; }

        /// <summary>What the step is for, in one phrase.</summary>
        string Purpose { get; }

        /// <summary>Why the step does not act on the run's target, or null when it does.</summary>
        string SkipReason(PrepareContext context);

        /// <summary>Reports what <see cref="Apply"/> would change; changes nothing.</summary>
        void Plan(PrepareContext context, PrepareReport report);

        /// <summary>Makes the changes <see cref="Plan"/> reports, reporting each.</summary>
        void Apply(PrepareContext context, PrepareReport report);
    }

    /// <summary>
    /// A step that changes a model asset's importer: it applies before the model's prefab
    /// variant is opened, so the steps after it see the reimported model.
    /// </summary>
    internal interface IImporterStep : IPrepareStep { }

    /// <summary>
    /// A step that only checks the target and changes nothing. On a model whose importer an
    /// importer step changes first, its plan's errors wait: it checks again once the model is
    /// reimported, and an error then stops the steps after it.
    /// </summary>
    internal interface ICheckStep : IPrepareStep { }

    internal enum PrepareTargetKind { ModelAsset, PrefabAsset, SceneObject }

    /// <summary>A model asset, a prefab asset or a scene object to prepare.</summary>
    internal sealed class PrepareTarget
    {
        PrepareTarget(PrepareTargetKind kind, string assetPath, GameObject sceneObject)
        {
            Kind = kind; AssetPath = assetPath; SceneObject = sceneObject;
        }

        internal PrepareTargetKind Kind { get; }

        /// <summary>The model or prefab asset's path; null for a scene object.</summary>
        internal string AssetPath { get; }

        /// <summary>The scene object; null for an asset.</summary>
        internal GameObject SceneObject { get; }

        /// <summary>The asset's file name without extension, or the scene object's name.</summary>
        internal string Name => SceneObject != null ? SceneObject.name : Path.GetFileNameWithoutExtension(AssetPath);

        /// <summary>The asset's path, or the scene object's path in its scene.</summary>
        internal string Display => SceneObject != null ? ScenePath(SceneObject.transform) : AssetPath;

        /// <summary>
        /// The model the target is made from: a model's own path; a prefab's or a scene object's
        /// whose root is an instance of a model's root (a variant of it included); else null.
        /// </summary>
        internal string ModelPath
        {
            get
            {
                if (Kind == PrepareTargetKind.ModelAsset) return AssetPath;
                var root = SceneObject != null ? SceneObject : AssetDatabase.LoadAssetAtPath<GameObject>(AssetPath);
                var source = root != null ? PrefabUtility.GetCorrespondingObjectFromOriginalSource(root) : null;
                if (source == null || source.transform.parent != null) return null;
                string path = AssetDatabase.GetAssetPath(source);
                return AssetImporter.GetAtPath(path) is ModelImporter ? path : null;
            }
        }

        /// <summary>The target for the asset at <paramref name="path"/>: a model or a prefab; null for any other asset.</summary>
        internal static PrepareTarget ForAsset(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (AssetImporter.GetAtPath(path) is ModelImporter) return new PrepareTarget(PrepareTargetKind.ModelAsset, path, null);
            return path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                ? new PrepareTarget(PrepareTargetKind.PrefabAsset, path, null)
                : null;
        }

        internal static PrepareTarget ForSceneObject(GameObject sceneObject)
            => sceneObject != null && sceneObject.scene.IsValid() ? new PrepareTarget(PrepareTargetKind.SceneObject, null, sceneObject) : null;

        /// <summary>
        /// The selected models and prefabs in the Project window, and the selected scene
        /// objects (one under another selected object is part of it, not a target of its own).
        /// </summary>
        internal static List<PrepareTarget> FromSelection()
        {
            var targets = Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath).Distinct(StringComparer.Ordinal)
                .Select(ForAsset).Where(t => t != null).ToList();
            var objects = Selection.gameObjects.Where(go => go != null && go.scene.IsValid()).ToList();
            targets.AddRange(objects.Where(go => !objects.Any(other => other != go && go.transform.IsChildOf(other.transform)))
                .Select(ForSceneObject));
            return targets;
        }

        static string ScenePath(Transform node)
        {
            var names = new List<string>();
            for (var t = node; t != null; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join('/', names);
        }
    }

    /// <summary>What the steps of one run share: the target, the hierarchy they edit and how they edit it.</summary>
    internal sealed class PrepareContext
    {
        internal PrepareContext(PrepareTarget target, GameObject root, bool usesUndo, string noRoot = null, PrefabOutput output = null)
        {
            Target = target; Root = root; UsesUndo = usesUndo; NoRoot = noRoot; Output = output;
        }

        internal PrepareTarget Target { get; }

        /// <summary>
        /// The hierarchy the steps edit: the prefab's contents, the scene object, or a model's
        /// prefab variant; null when there is none (<see cref="NoRoot"/> says why).
        /// </summary>
        internal GameObject Root { get; }

        /// <summary>Why there is no hierarchy to edit, or null.</summary>
        internal string NoRoot { get; }

        /// <summary>A model's prefab variant (the Prefab step's), or null.</summary>
        internal PrefabOutput Output { get; }

        /// <summary>Whether edits are recorded with Undo (a scene object's are).</summary>
        internal bool UsesUndo { get; }

        /// <summary>The asset a search for related assets starts from: the target's, else its scene's.</summary>
        internal string NearPath => Target.AssetPath ?? Target.SceneObject?.scene.path;

        internal Component AddComponent(GameObject host, Type type) => UsesUndo ? Undo.AddComponent(host, type) : host.AddComponent(type);

        internal T AddComponent<T>(GameObject host) where T : Component => (T)AddComponent(host, typeof(T));

        /// <summary>Records <paramref name="target"/> before a change (with Undo only).</summary>
        internal void Record(Object target, string name)
        {
            if (UsesUndo) Undo.RecordObject(target, name);
        }

        /// <summary>
        /// After a change made through the object's API: an object of a prefab instance keeps it
        /// as an override (a model's variant is an instance too, edited without Undo).
        /// </summary>
        internal static void Changed(Object target)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(target)) PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }

        internal void Destroy(Object target)
        {
            if (UsesUndo) Undo.DestroyObjectImmediate(target);
            else Object.DestroyImmediate(target);
        }
    }
}
