// FbxCollisionPlacement.cs — bakes a collision node's offset from the model root into its
// mesh, in the FBX itself: the node's transform is reset so it sits at the root's
// placement, and its control points take the positions its mesh has in the root's space.
// A MeshCollider on the root then lands where the mesh lies. No mesh asset is copied.
//
// The write goes through FbxChannelWrite (one document load, an atomic save, the
// reimport and relink). Before the file is replaced, the edited document is imported once
// with the model's settings and every baked node checked: at the root's placement, its
// surface where it was. Otherwise the bake is refused and the file is left as it was.
//
// A collision node the sidecar's collision entries write is left alone: the next save
// would write it back next to its source node.

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    internal static class FbxCollisionPlacement
    {
        const string MenuPath = "Assets/Mesh Lab/Bake Collision Offsets into FBX";

        /// <summary>How far a baked surface may lie from where it was, relative to the mesh's size (at least 0.1 mm).</summary>
        const float SurfaceTolerance = 1e-4f;

        /// <summary>
        /// The collision nodes of the model at <paramref name="fbxPath"/> (named as collision by
        /// MeshNaming) that sit off the root's placement, each with its mesh in the root's space;
        /// <paramref name="skipped"/> names the ones left as they are and why, and
        /// <paramref name="offsets"/> gives each baked mesh's node placement in the root's space.
        /// The caller disposes the plan.
        /// </summary>
        internal static FbxStructurePlan Plan(string fbxPath, out List<string> skipped, out Dictionary<Mesh, Matrix4x4> offsets)
        {
            skipped = new List<string>();
            offsets = new Dictionary<Mesh, Matrix4x4>();
            var plan = new FbxStructurePlan();
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (root == null) return plan;
            var managed = SidecarStore.CollisionSources(fbxPath).Keys.ToList();
            try
            {
                // The legacy finder also takes "_COLOR"-like names; a render mesh written as
                // collision would lose its UVs, colours and materials.
                foreach (var node in MeshHygieneUtility.FindCollisionObjects(root.transform).Where(n => MeshNaming.IsCollision(n.name)))
                {
                    var mesh = node.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                    if (mesh == null || LodHierarchy.OffsetFromRoot(root.transform, node.transform) == null) continue;
                    string reason = SkipReason(root.transform, node.transform, managed);
                    if (reason != null)
                    {
                        skipped.Add($"'{node.name}' {reason}");
                        continue;
                    }
                    var offset = root.transform.worldToLocalMatrix * node.transform.localToWorldMatrix;
                    var rootMesh = MeshAccess.ReadableCopy(mesh);
                    rootMesh.name = mesh.name;
                    MeshGeometry.TransformCollisionMesh(rootMesh, offset);
                    rootMesh.RecalculateNormals();
                    offsets[mesh] = offset;
                    plan.rootPlacements.Add((CollisionHierarchyAnalyzer.RelativePath(root.transform, node.transform), rootMesh));
                }
            }
            catch
            {
                plan.Dispose();
                throw;
            }
            return plan;
        }

        // Why a collision node off the root's placement is left as it is, or null when it is baked.
        static string SkipReason(Transform root, Transform node, List<string> managed)
        {
            string key = managed.FirstOrDefault(k => MeshNaming.IsCollisionOf(node.name, k));
            if (key != null) return $"is written from the sidecar's collision entry '{key}'; the next save would put it back next to its source";
            if (node.parent != root) return $"sits under '{node.parent.name}', not directly under the model root";
            if (node.childCount > 0) return "has child nodes, which would move with it";
            return null;
        }

        /// <summary>
        /// Imports the edited <paramref name="document"/> once with the source's settings and
        /// checks each node of <paramref name="placements"/>: at the root's placement, its
        /// surface where the planned mesh's is. Throws a refusal naming the first that is not.
        /// </summary>
        internal static void Verify(FbxSourceDocument document, string sourceFbxPath, List<(string path, Mesh mesh)> placements)
        {
            string problem = FbxChannelWrite.ImportEditedCopy(document, sourceFbxPath, path =>
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null) return "the edited file did not import as a model";
                return placements.Select(p => PlacementProblem(root.transform, p.path, p.mesh)).FirstOrDefault(p => p != null);
            });
            if (problem != null)
                throw new FbxStructureRefusalException($"{problem}; the collision offsets were not baked and the file is unchanged");
        }

        // What is wrong with the baked node at nodePath in the imported edit, or null.
        static string PlacementProblem(Transform root, string nodePath, Mesh expected)
        {
            var node = CollisionHierarchyAnalyzer.Find(root, nodePath);
            var mesh = node != null && node.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
            if (mesh == null) return $"'{nodePath}' has no mesh in the edited file";
            string offset = LodHierarchy.OffsetFromRoot(root, node);
            if (offset != null) return $"'{node.name}' imports off the root's placement ({offset})";
            string surface = SurfaceDifference(mesh, expected);
            return surface == null ? null : $"'{node.name}': {surface}";
        }

        /// <summary>
        /// Null when every vertex of each mesh lies on the other's surface within the
        /// tolerance; else how far the farthest lies.
        /// </summary>
        internal static string SurfaceDifference(Mesh imported, Mesh expected)
        {
            float tolerance = SurfaceTolerance * Mathf.Max(1f, expected.bounds.size.magnitude);
            float farthest = Mathf.Max(Farthest(imported.vertices, expected), Farthest(expected.vertices, imported));
            return farthest <= tolerance ? null : $"its surface moved by up to {farthest:G3} m";
        }

        static float Farthest(Vector3[] points, Mesh surface)
        {
            var bvh = new TriangleBvh(surface.vertices, surface.triangles);
            float farthest = 0f;
            foreach (var p in points)
            {
                var hit = bvh.FindNearest(p);
                farthest = Mathf.Max(farthest, hit.triangleIndex < 0 ? float.PositiveInfinity : Vector3.Distance(p, hit.point));
            }
            return farthest;
        }

        /// <summary>
        /// Bakes the offsets of the collision nodes of the model at <paramref name="fbxPath"/>
        /// into the file. False when nothing was written (nothing to bake, or a refusal, logged).
        /// </summary>
        internal static bool BakeOffsets(string fbxPath)
        {
            using var plan = Plan(fbxPath, out var skipped, out var offsets);
            foreach (string reason in skipped) UvtLog.Warn($"[Collision] {reason}; not baked.");
            if (plan.rootPlacements.Count == 0)
            {
                UvtLog.Info($"[Collision] '{fbxPath}': no collision node off the root's placement to bake.");
                return false;
            }
            var scene = SceneEffectOf(offsets, AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath));
            if (scene.refusals.Count > 0)
            {
                UvtLog.Error($"[Collision] '{fbxPath}' was not written: {string.Join("; ", scene.refusals)}.");
                return false;
            }
            var bindings = FbxExport.CaptureSceneMeshReferences(fbxPath, Array.Empty<MeshEntry>());
            bool written;
            try { written = FbxChannelWrite.Write(fbxPath, Array.Empty<MeshEntry>(), FbxExportIntent.None, null, null, plan); }
            catch (FbxStructureRefusalException ex)
            {
                UvtLog.Error($"[Collision] '{fbxPath}' was not written: {ex.Message}");
                return false;
            }
            finally { bindings?.Relink(fbxPath); }
            if (!written) return false;
            int moved = KeepScenePlacements(scene.moves);
            UvtLog.Info($"[Collision] '{fbxPath}': {plan.rootPlacements.Count} collision node(s) baked to the root's placement"
                + (moved > 0 ? $"; {moved} scene object(s) that do not follow the model's transforms were moved to keep their collision where it was" : "")
                + (scene.rootColliders > 0 ? $"; {scene.rootColliders} MeshCollider(s) on the model's root now lie where the model shows their node" : "") + ".");
            return true;
        }

        /// <summary>
        /// What the bake does to the loaded scenes: the objects showing a baked mesh, each with
        /// the local transform that keeps it where it was; how many MeshColliders on the root of
        /// an instance of the model take their node's placement; and why the bake is refused, if it is.
        /// </summary>
        sealed class SceneEffect
        {
            public readonly List<(Transform node, Matrix4x4 local)> moves = new List<(Transform, Matrix4x4)>();
            public readonly List<string> refusals = new List<string>();
            public int rootColliders;
        }

        // Every object of the loaded scenes (inactive ones too) whose MeshFilter or MeshCollider uses one of the meshes.
        static SceneEffect SceneEffectOf(Dictionary<Mesh, Matrix4x4> offsets, GameObject model)
        {
            var effect = new SceneEffect();
            foreach (var root in LoadedSceneRoots())
            {
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null && offsets.TryGetValue(filter.sharedMesh, out var offset))
                        AddMove(effect, filter.transform, filter.sharedMesh, offset);
                foreach (var collider in root.GetComponentsInChildren<MeshCollider>(true))
                    AddCollider(effect, collider, offsets, model);
            }
            return effect;
        }

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
        /// The baked mesh moved by its offset, so an object showing it keeps it in place at its
        /// old local transform times the offset's inverse. A Transform holds no shear: when that
        /// needs one (the node rotated and unevenly scaled against the object), the bake is refused.
        /// </summary>
        static void AddMove(SceneEffect effect, Transform node, Mesh mesh, Matrix4x4 offset)
        {
            var local = Local(node) * offset.inverse;
            if (IsTrs(local)) effect.moves.Add((node, local));
            else effect.refusals.Add($"'{node.name}' would need a sheared transform to keep '{mesh.name}' where it shows it "
                + "(the node is rotated and unevenly scaled against that object's transform)");
        }

        /// <summary>
        /// A MeshCollider using a baked mesh without its object showing it: its collision moves
        /// with the mesh. On the root of an instance of the model that is the point (it lands
        /// where the model shows the node); anywhere else the bake is refused.
        /// </summary>
        static void AddCollider(SceneEffect effect, MeshCollider collider, Dictionary<Mesh, Matrix4x4> offsets, GameObject model)
        {
            var mesh = collider.sharedMesh;
            if (mesh == null || !offsets.ContainsKey(mesh)) return;
            if (collider.TryGetComponent(out MeshFilter filter) && filter.sharedMesh == mesh) return;   // moved with its MeshFilter
            if (model != null && PrefabUtility.GetCorrespondingObjectFromOriginalSource(collider.gameObject) == model) effect.rootColliders++;
            else effect.refusals.Add($"'{collider.name}' has a MeshCollider using '{mesh.name}' away from its node, so the bake would move that collision "
                + "(give the node its own collider, or clear this one)");
        }

        /// <summary>
        /// Sets each object's local transform to the one that keeps its baked mesh in place. A
        /// prefab instance that follows the model already has it (identity) after the import; an
        /// unpacked copy or an instance with its own transform override is moved, with Undo and
        /// the override recorded.
        /// </summary>
        static int KeepScenePlacements(List<(Transform node, Matrix4x4 local)> moves)
        {
            int moved = 0;
            foreach (var (node, local) in moves)
            {
                if (node == null || SameMatrix(Local(node), local)) continue;
                Undo.RecordObject(node, "Keep Baked Collision in Place");
                node.localPosition = local.GetColumn(3);
                node.localRotation = local.rotation;
                node.localScale = local.lossyScale;
                if (PrefabUtility.IsPartOfPrefabInstance(node)) PrefabUtility.RecordPrefabInstancePropertyModifications(node);
                moved++;
            }
            return moved;
        }

        static Matrix4x4 Local(Transform t) => Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);

        /// <summary>Whether a Transform's position, rotation and scale can hold <paramref name="m"/>: it has no shear.</summary>
        internal static bool IsTrs(Matrix4x4 m) => SameMatrix(Matrix4x4.TRS(m.GetColumn(3), m.rotation, m.lossyScale), m, 1e-4f);

        // Entry by entry, within the tolerance relative to the entry's size (at least 1).
        static bool SameMatrix(Matrix4x4 a, Matrix4x4 b, float tolerance = 1e-5f)
        {
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > tolerance * Mathf.Max(1f, Mathf.Abs(b[i]))) return false;
            return true;
        }

        static IEnumerable<string> SelectedModels() => Selection.assetGUIDs
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && AssetImporter.GetAtPath(path) is ModelImporter)
            .Distinct();

        [MenuItem(MenuPath, true)]
        static bool CanBakeSelection() => SelectedModels().Any();

        [MenuItem(MenuPath, false, 2001)]
        static void BakeSelection()
        {
            foreach (string path in SelectedModels().ToList()) BakeOffsets(path);
        }
    }
}
#endif
