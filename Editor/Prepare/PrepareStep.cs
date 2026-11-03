// PrepareStep.cs — one step of preparing a model for the game (Colliders; Prefab, Materials
// and Preset to come): what it would change, changing nothing (Plan), and the change itself
// (Apply), each into the step's report line by line. A step that cannot act on a target says
// why it is skipped.
//
// PrepareTarget is what a run prepares: a prefab asset (its contents are loaded, edited and
// saved), a scene object (edited with Undo) or a model asset (its importer; its hierarchy is
// the model's own and is edited through a prefab). PrepareContext is what the steps of one
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
        internal PrepareContext(PrepareTarget target, GameObject root, bool usesUndo)
        {
            Target = target; Root = root; UsesUndo = usesUndo;
        }

        internal PrepareTarget Target { get; }

        /// <summary>The hierarchy the steps edit: the prefab's contents or the scene object; null for a model asset.</summary>
        internal GameObject Root { get; }

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

        /// <summary>After a change to a recorded object: an object of a prefab instance keeps it as an override.</summary>
        internal void Changed(Object target)
        {
            if (UsesUndo && PrefabUtility.IsPartOfPrefabInstance(target)) PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }

        internal void Destroy(Object target)
        {
            if (UsesUndo) Undo.DestroyObjectImmediate(target);
            else Object.DestroyImmediate(target);
        }
    }
}
