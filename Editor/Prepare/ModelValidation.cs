// ModelValidation.cs — checks a model (an FBX or prefab asset, or a scene object) against
// ModelRequirements into a PrepareReport: the file (import scale, units, smoothing groups,
// ModelFileChecks), the nodes' scale, the model's size, the LOD chain and node names
// (ModelLodChecks), then every mesh. A collision mesh (a _COL node, or a MeshCollider's
// mesh, that no node renders) is held to the geometry checks only. Each mesh is checked
// once, however many nodes share it; an unreadable one through a readable copy that is
// destroyed afterwards.
//
// Assets ▸ Mesh Lab ▸ Validate Models logs the report of each selected model.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class ModelValidation
    {
        internal const string Tag = "Validate";
        const string MenuPath = "Assets/Mesh Lab/Validate Models";
        const string ToolsMenuPath = "Tools/Mesh Lab/Validate Models";

        /// <summary>The report of the model or prefab asset at <paramref name="assetPath"/>.</summary>
        internal static PrepareReport Validate(string assetPath, ModelRequirements requirements = null)
        {
            requirements = requirements ?? ModelRequirements.Default;
            var report = new PrepareReport(assetPath);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (root == null)
            {
                report.Add(PrepareLevel.Error, assetPath, "Model", "not a model or prefab asset");
                return report;
            }
            if (AssetImporter.GetAtPath(assetPath) is ModelImporter importer)
                ModelFileChecks.CheckImporter(importer, assetPath, requirements, report);
            var collisionOnly = new HashSet<string>(Uses(root).Where(u => !u.render).Select(u => u.mesh.name), StringComparer.Ordinal);
            ModelFileChecks.CheckSmoothing(assetPath, requirements, report, collisionOnly);
            Validate(root, requirements, report, includeRootScale: true);
            return report;
        }

        /// <summary>
        /// Checks the nodes' scale, the size and every mesh under <paramref name="root"/> into
        /// <paramref name="report"/>. The root's own scale counts for an asset
        /// (<paramref name="includeRootScale"/>), not for a placed scene object.
        /// </summary>
        internal static void Validate(GameObject root, ModelRequirements requirements, PrepareReport report, bool includeRootScale = false)
        {
            requirements = requirements ?? ModelRequirements.Default;
            ModelFileChecks.CheckNodeScales(root, includeRootScale, requirements, report);
            ModelFileChecks.CheckSize(root, requirements, report);
            ModelLodChecks.CheckLods(RenderNodes(root), requirements, report);
            ModelLodChecks.CheckNames(root, requirements, report);
            var uses = Uses(root);
            foreach (var use in uses)
            {
                var (mesh, node, render) = use;
                string subject = Subject(root.transform, node, mesh);
                Mesh readable;
                bool isCopy;
                try { readable = MeshAccess.Readable(mesh, out isCopy); }
                catch (Exception error)
                {
                    report.Add(PrepareLevel.Error, subject, "Read", $"the mesh could not be read: {error.Message}");
                    continue;
                }
                try { MeshValidation.Check(readable, subject, !render, requirements, report); }
                finally { if (isCopy) UnityEngine.Object.DestroyImmediate(readable); }
            }
            if (uses.Count == 0) report.Add(PrepareLevel.Warning, root.name, "Model", "no meshes to check");
        }

        /// <summary>
        /// Each mesh once, in hierarchy order, with the node it is reported under: a render
        /// mesh when any node renders it (from the first such node), else a collision mesh
        /// held to the geometry checks.
        /// </summary>
        static List<(Mesh mesh, Transform node, bool render)> Uses(GameObject root)
        {
            var uses = new List<(Mesh mesh, Transform node, bool render)>();
            var index = new Dictionary<Mesh, int>();
            foreach (var (node, mesh, collider) in Meshes(root))
            {
                if (mesh == null) continue;
                bool render = !collider && !MeshNaming.IsCollision(node.name) && !MeshNaming.IsCollision(mesh.name);
                if (!index.TryGetValue(mesh, out int at)) { index[mesh] = uses.Count; uses.Add((mesh, node, render)); }
                else if (render && !uses[at].render) uses[at] = (mesh, node, true);
            }
            return uses;
        }

        /// <summary>Each node's render mesh, then its collider's mesh (flagged as collision).</summary>
        static IEnumerable<(Transform node, Mesh mesh, bool collider)> Meshes(GameObject root)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform.TryGetComponent(out MeshFilter filter)) yield return (transform, filter.sharedMesh, false);
                if (transform.TryGetComponent(out SkinnedMeshRenderer skinned)) yield return (transform, skinned.sharedMesh, false);
                foreach (var meshCollider in transform.GetComponents<MeshCollider>())
                    yield return (transform, meshCollider.sharedMesh, true);
            }
        }

        /// <summary>Each render (not collision) node's name and triangle count (a quad is two); an unreadable mesh counts its indices too.</summary>
        static IEnumerable<(string name, int triangles)> RenderNodes(GameObject root)
        {
            foreach (var (node, mesh, collider) in Meshes(root))
            {
                if (collider || mesh == null || MeshNaming.IsCollision(node.name) || MeshNaming.IsCollision(mesh.name)) continue;
                long triangles = 0;
                for (int sub = 0; sub < mesh.subMeshCount; ++sub)
                {
                    var topology = mesh.GetTopology(sub);
                    if (topology == MeshTopology.Triangles) triangles += mesh.GetIndexCount(sub) / 3;
                    else if (topology == MeshTopology.Quads) triangles += mesh.GetIndexCount(sub) / 2;   // two triangles per quad
                }
                yield return (node.name, (int)triangles);
            }
        }

        /// <summary>The node's path below the root (the root's own name for the root), and the mesh when its name differs.</summary>
        static string Subject(Transform root, Transform node, Mesh mesh)
        {
            string path = node == root ? root.name : AnimationUtility.CalculateTransformPath(node, root);
            return mesh.name == node.name ? $"'{path}'" : $"'{path}' (mesh '{mesh.name}')";
        }

        static IEnumerable<string> SelectedModels() => ModelSelection.Paths(path
            => AssetImporter.GetAtPath(path) is ModelImporter || path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));

        [MenuItem(MenuPath, true)]
        [MenuItem(ToolsMenuPath, true)]
        static bool CanValidateSelection() => SelectedModels().Any();

        [MenuItem(MenuPath, false, 2000)]
        [MenuItem(ToolsMenuPath, false, 20)]
        static void ValidateSelection()
        {
            var paths = SelectedModels().ToList();
            int blocked = 0;
            foreach (var path in paths)
            {
                var report = Validate(path);
                report.Log(Tag);
                if (report.BlocksApply) ++blocked;
            }
            UvtLog.Info($"[{Tag}] {MeshValidation.Plural(paths.Count, "model", "models")} checked, {blocked} with errors");
        }
    }
}
