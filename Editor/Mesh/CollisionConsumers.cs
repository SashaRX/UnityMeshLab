// CollisionConsumers.cs — the objects that show or collide with collision meshes about to move
// into the model root's space (FbxCollisionPlacement bakes them into the FBX,
// CollisionRootPlacement on import through the sidecar): in the loaded scenes, and in the
// prefab assets that use the model (an unpacked copy saved as a prefab, e.g. by Variant
// Export, keeps its own transforms just as one in a scene does).
//
// Scan, before the change: each object showing a moving mesh, with its placement, and why the
// change must be refused — the object has child objects of its own (they would move with it),
// or a MeshCollider uses a moving mesh away from its node (its collision would move; on the
// root of an instance of the model it lands where the model shows the node, the point of the
// change). Problems: an object a Transform cannot keep in place (it would need shear). Apply,
// after the change: each object takes the local transform that keeps its mesh where it was,
// a scene object with Undo, a prefab asset saved.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SashaRX.UnityMeshLab
{
    internal sealed class CollisionConsumers
    {
        // Scene objects, and per prefab asset its objects by path below the prefab's root (the
        // contents are loaded again for each pass); each with its old placement (in the prefab's
        // root space for a prefab) and the path of the model node whose mesh it shows.
        readonly List<(Transform node, Matrix4x4 world, string path)> scene = new List<(Transform, Matrix4x4, string)>();
        // In the order they were scanned: a base before the prefabs built on it.
        readonly List<KeyValuePair<string, List<(string node, Matrix4x4 world, string path)>>> prefabs
            = new List<KeyValuePair<string, List<(string, Matrix4x4, string)>>>();
        readonly List<string> refusals = new List<string>();

        /// <summary>Why the change must not happen; empty when it may.</summary>
        internal IReadOnlyList<string> Refusals => refusals;

        /// <summary>MeshColliders on the root of an instance of the model using a moving mesh: they follow their node.</summary>
        internal int RootColliders { get; private set; }

        /// <summary>
        /// The consumers of <paramref name="meshes"/> (each mesh with the path of its node in the
        /// model at <paramref name="fbxPath"/>), before they move.
        /// </summary>
        internal static CollisionConsumers Scan(string fbxPath, GameObject model, Dictionary<Mesh, string> meshes)
        {
            var consumers = new CollisionConsumers();
            foreach (var root in LoadedSceneRoots()) consumers.ScanRoot(root, null, model, meshes);
            foreach (string prefab in DependentPrefabs(fbxPath))
                WithContents(prefab, root => { consumers.ScanRoot(root, prefab, model, meshes); return false; });
            return consumers;
        }

        void ScanRoot(GameObject root, string prefab, GameObject model, Dictionary<Mesh, string> meshes)
        {
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || !meshes.TryGetValue(filter.sharedMesh, out string path)) continue;
                var node = filter.transform;
                if (node.childCount > 0)
                    refusals.Add($"{Where(filter, prefab)} has child objects of its own, which would move with '{filter.sharedMesh.name}'");
                else if (prefab == null)
                    scene.Add((node, node.localToWorldMatrix, path));
                else
                    Uses(prefab).Add((CollisionHierarchyAnalyzer.RelativePath(root.transform, node), root.transform.worldToLocalMatrix * node.localToWorldMatrix, path));
            }
            foreach (var collider in root.GetComponentsInChildren<MeshCollider>(true))
            {
                var follow = Follow(collider, meshes.ContainsKey, model);
                if (follow == ColliderFollow.OnModelRoot) RootColliders++;
                else if (follow == ColliderFollow.Moves)
                    refusals.Add($"{Where(collider, prefab)} has a MeshCollider using '{collider.sharedMesh.name}' away from its node; moving that mesh "
                        + "into the root's space would move this collision (give the node its own collider, or clear this one)");
            }
        }

        List<(string, Matrix4x4, string)> Uses(string prefab)
        {
            int at = prefabs.FindIndex(p => p.Key == prefab);
            if (at >= 0) return prefabs[at].Value;
            var uses = new List<(string, Matrix4x4, string)>();
            prefabs.Add(new KeyValuePair<string, List<(string, Matrix4x4, string)>>(prefab, uses));
            return uses;
        }

        static string Where(Component component, string prefab) => prefab == null ? $"'{component.name}'" : $"'{component.name}' in '{prefab}'";

        /// <summary>
        /// The objects a Transform cannot keep in place when their nodes go from
        /// <paramref name="before"/> to <paramref name="after"/> (placements in the root's space by node path).
        /// </summary>
        internal List<string> Problems(Dictionary<string, Matrix4x4> before, Dictionary<string, Matrix4x4> after)
        {
            var problems = new List<string>();
            foreach (var (node, local) in SceneLocals(before, after))
                if (!IsTrs(local)) problems.Add($"'{node.name}' would need a sheared transform to keep its collision where it was");
            foreach (var prefab in prefabs)
                WithContents(prefab.Key, root =>
                {
                    foreach (var (node, local) in PrefabLocals(root, prefab.Value, before, after))
                        if (!IsTrs(local)) problems.Add($"'{node.name}' in '{prefab.Key}' would need a sheared transform to keep its collision where it was");
                    return false;
                });
            return problems;
        }

        /// <summary>
        /// Gives each object the local transform that keeps its mesh where it was: a scene object
        /// with Undo, a prefab asset saved. An object already there (one that follows the model) is
        /// left alone. Returns how many scene objects moved and how many prefab assets were saved.
        /// </summary>
        internal (int moved, int prefabs) Apply(Dictionary<string, Matrix4x4> before, Dictionary<string, Matrix4x4> after)
        {
            int moved = 0, saved = 0;
            foreach (var (node, local) in SceneLocals(before, after))
                if (Set(node, local, true)) moved++;
            foreach (var prefab in prefabs)
            {
                bool changed = WithContents(prefab.Key, root =>
                {
                    bool any = false;
                    foreach (var (node, local) in PrefabLocals(root, prefab.Value, before, after))
                        any |= Set(node, local, false);
                    return any;
                });
                if (changed) saved++;
            }
            return (moved, saved);
        }

        IEnumerable<(Transform node, Matrix4x4 local)> SceneLocals(Dictionary<string, Matrix4x4> before, Dictionary<string, Matrix4x4> after)
        {
            foreach (var (node, world, path) in scene)
                if (node != null && after.TryGetValue(path, out var placed))
                    yield return (node, UnderParent(node, world * before[path].inverse * placed));
        }

        static IEnumerable<(Transform node, Matrix4x4 local)> PrefabLocals(GameObject root, List<(string node, Matrix4x4 world, string path)> uses,
            Dictionary<string, Matrix4x4> before, Dictionary<string, Matrix4x4> after)
        {
            foreach (var (nodePath, world, path) in uses)
            {
                var node = CollisionHierarchyAnalyzer.Find(root.transform, nodePath);
                if (node != null && after.TryGetValue(path, out var placed))
                    yield return (node, UnderParent(node, root.transform.localToWorldMatrix * world * before[path].inverse * placed));
            }
        }

        // The mesh moved from its node's old placement to the new one, so the object keeps it where
        // it was at its old placement less that move, under its parent as it is now.
        static Matrix4x4 UnderParent(Transform node, Matrix4x4 world) => node.parent != null ? node.parent.worldToLocalMatrix * world : world;

        static bool Set(Transform node, Matrix4x4 local, bool undo)
        {
            if (SameMatrix(Local(node), local)) return false;
            if (undo) Undo.RecordObject(node, "Keep Collision in Place");
            node.localPosition = local.GetColumn(3);
            node.localRotation = local.rotation;
            node.localScale = local.lossyScale;
            if (PrefabUtility.IsPartOfPrefabInstance(node)) PrefabUtility.RecordPrefabInstancePropertyModifications(node);
            return true;
        }

        // ── Where the consumers are ──

        /// <summary>The roots of every loaded scene (inactive objects are under them too).</summary>
        static IEnumerable<GameObject> LoadedSceneRoots()
        {
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) yield return root;
            }
        }

        /// <summary>
        /// The project's prefab assets that use the model at <paramref name="fbxPath"/>, directly (its
        /// meshes, or an instance of it) or through another prefab (a variant of one that does), a
        /// base before the prefabs built on it: a prefab that follows its base needs nothing once
        /// the base is saved.
        /// </summary>
        static IEnumerable<string> DependentPrefabs(string fbxPath)
            => AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct(StringComparer.Ordinal)
                .Select(path => (path, dependencies: AssetDatabase.GetDependencies(path, true)))
                .Where(p => p.dependencies.Contains(fbxPath, StringComparer.OrdinalIgnoreCase))
                .OrderBy(p => p.dependencies.Length)
                .Select(p => p.path)
                .ToList();

        // Loads the prefab's contents, runs work on them and saves them when it reports a change.
        static bool WithContents(string prefab, Func<GameObject, bool> work)
        {
            var root = PrefabUtility.LoadPrefabContents(prefab);
            try
            {
                bool changed = work(root);
                if (changed) PrefabUtility.SaveAsPrefabAsset(root, prefab);
                return changed;
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // ── Colliders ──

        enum ColliderFollow { Unaffected, WithItsObject, OnModelRoot, Moves }

        static ColliderFollow Follow(MeshCollider collider, Func<Mesh, bool> moving, GameObject model)
        {
            var mesh = collider.sharedMesh;
            if (mesh == null || !moving(mesh)) return ColliderFollow.Unaffected;
            if (collider.TryGetComponent(out MeshFilter filter) && filter.sharedMesh == mesh) return ColliderFollow.WithItsObject;
            return model != null && PrefabUtility.GetCorrespondingObjectFromOriginalSource(collider.gameObject) == model
                ? ColliderFollow.OnModelRoot
                : ColliderFollow.Moves;
        }

        // ── Matrices ──

        internal static Matrix4x4 Local(Transform t) => Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);

        /// <summary>Whether a Transform's position, rotation and scale can hold <paramref name="m"/>: it has no shear.</summary>
        internal static bool IsTrs(Matrix4x4 m) => SameMatrix(Matrix4x4.TRS(m.GetColumn(3), m.rotation, m.lossyScale), m, 1e-4f);

        /// <summary>Entry by entry, within the tolerance relative to the entry's size (at least 1).</summary>
        internal static bool SameMatrix(Matrix4x4 a, Matrix4x4 b, float tolerance = 1e-5f)
        {
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > tolerance * Mathf.Max(1f, Mathf.Abs(b[i]))) return false;
            return true;
        }
    }
}
