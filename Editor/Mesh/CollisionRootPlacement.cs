// CollisionRootPlacement.cs — a collision node off the model root's placement put at that
// placement through the sidecar (sidecar mode): the FBX keeps its offset, the sidecar lists
// the node, and every import bakes the offset into the imported mesh and resets the node, so
// a MeshCollider on the root lands where the mesh lies. FbxCollisionPlacement is the version
// that writes the same into the FBX itself.
//
// A listed node is a direct child of the root named as collision (MeshNaming), holding only
// collision nodes (a hull container); each mesh in it is used by that node alone. A node that
// stops qualifying is left where the file puts it, with a warning, and the import goes on.
//
// The scene follows: an object that shows a moved mesh without following the model's
// transforms (an unpacked copy, an instance with its own transform override) is moved, with
// Undo, so its collision stays where it was. The checks it shares with the FBX version (a
// collider that would move, a transform that would need shear) live here too.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SashaRX.UnityMeshLab
{
    internal static class CollisionRootPlacement
    {
        const string PlaceMenu = "Assets/Mesh Lab/Place Collision at Root (Sidecar)";
        const string ClearMenu = "Assets/Mesh Lab/Clear Collision Placement (Sidecar)";

        // ── On import ──

        /// <summary>
        /// Puts each listed collision node of the imported model at the root's placement: its
        /// meshes take the positions they have in the root's space and its transforms are reset.
        /// One that no longer qualifies is named in a warning and left as it is.
        /// </summary>
        internal static void ApplyOnImport(GameObject root, IList<string> paths, string modelPath)
        {
            if (root == null || paths == null || paths.Count == 0) return;
            var users = MeshUsers(root);
            foreach (string path in paths)
            {
                var node = CollisionHierarchyAnalyzer.Find(root.transform, path);
                string problem = node == null ? "is not in the model" : Refusal(root.transform, node, users) ?? UnreadableMesh(node);
                if (problem != null)
                {
                    UvtLog.Warn($"[Collision] '{modelPath}': '{CollisionHierarchyAnalyzer.NodeName(path)}' {problem}; the sidecar does not place it at the root.");
                    continue;
                }
                PlaceAtRoot(root.transform, node);
            }
        }

        static void PlaceAtRoot(Transform root, Transform node)
        {
            var filters = node.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).ToList();
            var toRoot = filters.Select(f => root.worldToLocalMatrix * f.transform.localToWorldMatrix).ToList();
            for (int i = 0; i < filters.Count; i++) MeshTransform.BakeMatrix(filters[i].sharedMesh, toRoot[i]);
            foreach (var t in node.GetComponentsInChildren<Transform>(true))
            {
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }
        }

        // Why the node cannot be placed at the root, or null.
        static string Refusal(Transform root, Transform node, Dictionary<Mesh, int> users)
        {
            if (node.parent != root) return "is not directly under the model root";
            var other = node.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => !MeshNaming.IsCollision(t.name));
            if (other != null) return $"holds '{other.name}', which is no collision node and would move with it";
            if (node.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0) return "is skinned";
            return node.GetComponentsInChildren<MeshFilter>(true).Select(f => MeshRefusal(f.sharedMesh, users)).FirstOrDefault(r => r != null);
        }

        static string MeshRefusal(Mesh mesh, Dictionary<Mesh, int> users)
        {
            if (mesh == null) return null;
            if (users.TryGetValue(mesh, out int count) && count > 1) return $"shares its mesh '{mesh.name}' with another node";
            return mesh.blendShapeCount > 0 ? $"has blend shapes on '{mesh.name}'" : null;
        }

        // On import every mesh is readable; one that is not would keep its old positions while its node moves.
        static string UnreadableMesh(Transform node)
        {
            var mesh = node.GetComponentsInChildren<MeshFilter>(true).Select(f => f.sharedMesh).FirstOrDefault(m => m != null && !m.isReadable);
            return mesh != null ? $"has '{mesh.name}' unreadable" : null;
        }

        static Dictionary<Mesh, int> MeshUsers(GameObject root)
        {
            var users = new Dictionary<Mesh, int>();
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null)
                {
                    users.TryGetValue(filter.sharedMesh, out int count);
                    users[filter.sharedMesh] = count + 1;
                }
            return users;
        }

        // ── Which nodes ──

        /// <summary>
        /// The collision nodes of the model at <paramref name="fbxPath"/> to place at the root:
        /// direct children of the root named as collision with a mesh off the root's placement;
        /// <paramref name="skipped"/> names the ones that cannot be placed and why.
        /// </summary>
        internal static List<Transform> Candidates(string fbxPath, out List<string> skipped)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (model == null) { skipped = new List<string>(); return new List<Transform>(); }
            return Candidates(model, AnimatedPaths(fbxPath), out skipped);
        }

        /// <summary>As above, with the transform paths the model's animation moves.</summary>
        internal static List<Transform> Candidates(GameObject model, ICollection<string> animated, out List<string> skipped)
        {
            skipped = new List<string>();
            var result = new List<Transform>();
            var root = model.transform;
            var users = MeshUsers(model);
            foreach (Transform node in root)
            {
                if (!MeshNaming.IsCollision(node.name) || !OffRoot(root, node)) continue;
                string reason = Refusal(root, node, users) ?? CurveRefusal(root, node, animated);
                if (reason != null) skipped.Add($"'{node.name}' {reason}");
                else result.Add(node);
            }
            return result;
        }

        static bool OffRoot(Transform root, Transform node) => node.GetComponentsInChildren<MeshFilter>(true)
            .Any(f => f.sharedMesh != null && LodHierarchy.OffsetFromRoot(root, f.transform) != null);

        static string CurveRefusal(Transform root, Transform node, ICollection<string> animated)
        {
            if (animated == null || animated.Count == 0) return null;
            return node.GetComponentsInChildren<Transform>(true).Any(t => animated.Contains(AnimationUtility.CalculateTransformPath(t, root)))
                ? "has transform curves in the model's animation, which would put the offset back when they play"
                : null;
        }

        // The transform paths the model's own clips animate.
        static HashSet<string> AnimatedPaths(string fbxPath)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var clip in AssetDatabase.LoadAllAssetRepresentationsAtPath(fbxPath).OfType<AnimationClip>())
            {
                if (clip.name.StartsWith("__preview__", StringComparison.Ordinal)) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    if (binding.type == typeof(Transform)) paths.Add(binding.path);
            }
            return paths;
        }

        // ── Place and clear ──

        /// <summary>
        /// Lists the collision nodes of the model at <paramref name="fbxPath"/> that sit off the
        /// root's placement in its sidecar and reimports it, so the import puts them at the root;
        /// the FBX is left as it is. False when nothing changed (nothing to place, or a refusal, logged).
        /// </summary>
        internal static bool PlaceInSidecar(string fbxPath)
        {
            if (!fbxPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                UvtLog.Warn($"[Collision] '{fbxPath}' is not in the project's Assets, so it can have no sidecar.");
                return false;
            }
            var tops = Candidates(fbxPath, out var skipped);
            foreach (string reason in skipped) UvtLog.Warn($"[Collision] {reason}; not placed.");
            if (tops.Count == 0)
            {
                UvtLog.Info($"[Collision] '{fbxPath}': no collision node off the root's placement to place.");
                return false;
            }
            var root = tops[0].parent;
            var paths = SidecarStore.CollisionAtRoot(fbxPath);
            var added = tops.Select(t => CollisionHierarchyAnalyzer.RelativePath(root, t)).Where(p => !paths.Contains(p)).ToList();
            paths.AddRange(added);
            if (!Reimport(fbxPath, tops, paths, out var effect)) return false;
            var placed = KeepPlaced(fbxPath, paths);
            UvtLog.Info($"[Collision] '{fbxPath}': {added.Count(placed.Contains)} collision node(s) placed at the root's placement by the sidecar; the FBX is unchanged"
                + Summary(effect, "now lie where the model shows their node") + ".");
            return true;
        }

        /// <summary>
        /// Drops the collision nodes the sidecar of <paramref name="fbxPath"/> places at the root
        /// and reimports the model, so they are back where the file puts them.
        /// </summary>
        internal static bool ClearSidecar(string fbxPath)
        {
            var paths = SidecarStore.CollisionAtRoot(fbxPath);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (paths.Count == 0 || model == null) return false;
            var tops = paths.Select(p => CollisionHierarchyAnalyzer.Find(model.transform, p)).Where(t => t != null).ToList();
            if (!Reimport(fbxPath, tops, new List<string>(), out var effect)) return false;
            UvtLog.Info($"[Collision] '{fbxPath}': {paths.Count} collision node(s) back where the FBX puts them" + Summary(effect, "lie off their node's placement again") + ".");
            return true;
        }

        static string Summary(SceneEffect effect, string rootColliders)
            => (effect.moved > 0 ? $"; {effect.moved} scene object(s) that do not follow the model's transforms were moved to keep their collision where it was" : "")
            + (effect.rootColliders > 0 ? $"; {effect.rootColliders} MeshCollider(s) on the model's root {rootColliders}" : "");

        // The listed paths whose nodes now sit at the root's placement; the rest the import refused (and named).
        static List<string> KeepPlaced(string fbxPath, List<string> paths)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var placed = paths.Where(p =>
            {
                var node = model != null ? CollisionHierarchyAnalyzer.Find(model.transform, p) : null;
                return node != null && !OffRoot(model.transform, node);
            }).ToList();
            if (placed.Count != paths.Count) SidecarStore.SetCollisionAtRoot(fbxPath, placed);
            return placed;
        }

        // ── The sidecar change and the scene ──

        /// <summary>What a reimport did to the loaded scenes.</summary>
        sealed class SceneEffect
        {
            public readonly List<(Transform node, Matrix4x4 world, string path)> uses = new List<(Transform, Matrix4x4, string)>();
            public readonly List<string> refusals = new List<string>();
            public int rootColliders, moved;
        }

        /// <summary>
        /// Sets the sidecar's list to <paramref name="paths"/> and reimports the model, keeping
        /// every scene object that shows a mesh of <paramref name="tops"/> where it was. Refused
        /// before anything changes when a collider elsewhere would move; undone (the list put
        /// back and the model reimported) when an object would need a sheared transform.
        /// </summary>
        static bool Reimport(string fbxPath, List<Transform> tops, List<string> paths, out SceneEffect effect)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var before = RootSpace(model, tops);
            effect = SceneUses(before.meshes, model);
            if (effect.refusals.Count > 0)
            {
                UvtLog.Error($"[Collision] '{fbxPath}': the sidecar was not changed: {string.Join("; ", effect.refusals)}.");
                return false;
            }
            var previous = SidecarStore.CollisionAtRoot(fbxPath);
            SidecarStore.SetCollisionAtRoot(fbxPath, paths);
            Import(fbxPath);
            var after = RootSpace(AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath), before.matrices.Keys);
            var moves = Moves(effect, before.matrices, after, out var problems);
            if (problems.Count > 0)
            {
                SidecarStore.SetCollisionAtRoot(fbxPath, previous);
                Import(fbxPath);
                UvtLog.Error($"[Collision] '{fbxPath}': the sidecar was put back: {string.Join("; ", problems)}.");
                return false;
            }
            effect.moved = Apply(moves);
            return true;
        }

        // Each mesh node of the tops: its path and its placement in the root's space, and its mesh.
        static (Dictionary<string, Matrix4x4> matrices, Dictionary<Mesh, string> meshes) RootSpace(GameObject model, IEnumerable<Transform> tops)
        {
            var matrices = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            var meshes = new Dictionary<Mesh, string>();
            foreach (var top in tops)
                foreach (var filter in top.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null) continue;
                    string path = CollisionHierarchyAnalyzer.RelativePath(model.transform, filter.transform);
                    matrices[path] = model.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                    meshes[filter.sharedMesh] = path;
                }
            return (matrices, meshes);
        }

        // The same nodes in the reimported model.
        static Dictionary<string, Matrix4x4> RootSpace(GameObject model, IEnumerable<string> paths)
        {
            var matrices = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            if (model == null) return matrices;
            foreach (string path in paths)
            {
                var node = CollisionHierarchyAnalyzer.Find(model.transform, path);
                if (node != null) matrices[path] = model.transform.worldToLocalMatrix * node.localToWorldMatrix;
            }
            return matrices;
        }

        static SceneEffect SceneUses(Dictionary<Mesh, string> meshes, GameObject model)
        {
            var effect = new SceneEffect();
            foreach (var root in LoadedSceneRoots())
            {
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null && meshes.TryGetValue(filter.sharedMesh, out string path))
                        effect.uses.Add((filter.transform, filter.transform.localToWorldMatrix, path));
                effect.rootColliders += CheckColliders(root, meshes.ContainsKey, model, effect.refusals);
            }
            return effect;
        }

        // Prefab instances in the loaded scenes are merged with the reimported model as part of the import.
        static void Import(string fbxPath)
            => AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

        /// <summary>
        /// The local transform that keeps each scene object's mesh where it was: its old world
        /// placement, less the move of its mesh into the new node placement (old × before⁻¹ ×
        /// after), under its parent as it is now. One a Transform cannot hold is a problem.
        /// </summary>
        static List<(Transform node, Matrix4x4 local)> Moves(SceneEffect effect, Dictionary<string, Matrix4x4> before,
            Dictionary<string, Matrix4x4> after, out List<string> problems)
        {
            problems = new List<string>();
            var moves = new List<(Transform, Matrix4x4)>();
            foreach (var (node, world, path) in effect.uses)
            {
                if (node == null || !after.TryGetValue(path, out var placed)) continue;
                var desired = world * before[path].inverse * placed;
                var local = node.parent != null ? node.parent.worldToLocalMatrix * desired : desired;
                if (IsTrs(local)) moves.Add((node, local));
                else problems.Add($"'{node.name}' would need a sheared transform to keep its collision where it was");
            }
            return moves;
        }

        /// <summary>
        /// Sets each object's local transform, with Undo and the prefab override recorded; an
        /// object already there (an instance that follows the model) is left alone.
        /// </summary>
        internal static int Apply(List<(Transform node, Matrix4x4 local)> moves)
        {
            int moved = 0;
            foreach (var (node, local) in moves)
            {
                if (node == null || SameMatrix(Local(node), local)) continue;
                Undo.RecordObject(node, "Keep Collision in Place");
                node.localPosition = local.GetColumn(3);
                node.localRotation = local.rotation;
                node.localScale = local.lossyScale;
                if (PrefabUtility.IsPartOfPrefabInstance(node)) PrefabUtility.RecordPrefabInstancePropertyModifications(node);
                moved++;
            }
            return moved;
        }

        // ── Shared with FbxCollisionPlacement ──

        /// <summary>How a MeshCollider follows when meshes move into the root's space.</summary>
        enum ColliderFollow
        {
            /// <summary>Its mesh does not move, or it has none.</summary>
            Unaffected,
            /// <summary>Its object shows the mesh too and is kept in place with it.</summary>
            WithItsObject,
            /// <summary>On the root of an instance of the model: it lands where the model shows the node, which is the point.</summary>
            OnModelRoot,
            /// <summary>Anywhere else its collision would move.</summary>
            Moves,
        }

        static ColliderFollow Follow(MeshCollider collider, Func<Mesh, bool> moving, GameObject model)
        {
            var mesh = collider.sharedMesh;
            if (mesh == null || !moving(mesh)) return ColliderFollow.Unaffected;
            if (collider.TryGetComponent(out MeshFilter filter) && filter.sharedMesh == mesh) return ColliderFollow.WithItsObject;
            return model != null && PrefabUtility.GetCorrespondingObjectFromOriginalSource(collider.gameObject) == model
                ? ColliderFollow.OnModelRoot
                : ColliderFollow.Moves;
        }

        /// <summary>
        /// The MeshColliders under <paramref name="sceneRoot"/> using a mesh that moves: each one
        /// whose collision would move is named in <paramref name="refusals"/>; returns how many
        /// sit on the root of an instance of <paramref name="model"/>.
        /// </summary>
        internal static int CheckColliders(GameObject sceneRoot, Func<Mesh, bool> moving, GameObject model, List<string> refusals)
        {
            int onRoot = 0;
            foreach (var collider in sceneRoot.GetComponentsInChildren<MeshCollider>(true))
            {
                var follow = Follow(collider, moving, model);
                if (follow == ColliderFollow.OnModelRoot) onRoot++;
                else if (follow == ColliderFollow.Moves) refusals.Add(MovesMessage(collider));
            }
            return onRoot;
        }

        static string MovesMessage(MeshCollider collider)
            => $"'{collider.name}' has a MeshCollider using '{collider.sharedMesh.name}' away from its node; moving that mesh into the root's space "
            + "would move this collision (give the node its own collider, or clear this one)";

        /// <summary>The roots of every loaded scene (inactive objects are under them too).</summary>
        internal static IEnumerable<GameObject> LoadedSceneRoots()
        {
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) yield return root;
            }
        }

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

        // ── Menu ──

        static IEnumerable<string> SelectedModels() => Selection.assetGUIDs
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => path.StartsWith("Assets/", StringComparison.Ordinal) && AssetImporter.GetAtPath(path) is ModelImporter)
            .Distinct();

        [MenuItem(PlaceMenu, true)]
        static bool CanPlaceSelection() => PostprocessorDefineManager.IsEnabled() && SelectedModels().Any();

        [MenuItem(PlaceMenu, false, 2002)]
        static void PlaceSelection()
        {
            foreach (string path in SelectedModels().ToList()) PlaceInSidecar(path);
        }

        [MenuItem(ClearMenu, true)]
        static bool CanClearSelection() => SelectedModels().Any(path => SidecarStore.CollisionAtRoot(path).Count > 0);

        [MenuItem(ClearMenu, false, 2003)]
        static void ClearSelection()
        {
            foreach (string path in SelectedModels().ToList()) ClearSidecar(path);
        }
    }
}
