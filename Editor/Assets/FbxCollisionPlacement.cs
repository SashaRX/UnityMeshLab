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
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    internal static class FbxCollisionPlacement
    {
        const string MenuPath = "Assets/Mesh Lab/Bake Collision Offsets into FBX";

        /// <summary>How far a baked surface may lie from where it was, relative to the mesh's size (at least 0.1 mm).</summary>
        const float SurfaceTolerance = 1e-4f;

        /// <summary>
        /// The collision nodes of the model at <paramref name="fbxPath"/> that sit off the
        /// root's placement, each with its mesh in the root's space; <paramref name="skipped"/>
        /// names the ones left as they are and why. The caller disposes the plan.
        /// </summary>
        internal static FbxStructurePlan Plan(string fbxPath, out List<string> skipped)
        {
            skipped = new List<string>();
            var plan = new FbxStructurePlan();
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (root == null) return plan;
            var managed = SidecarStore.CollisionSources(fbxPath).Keys.ToList();
            try
            {
                foreach (var node in MeshHygieneUtility.FindCollisionObjects(root.transform))
                {
                    var mesh = node.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                    if (mesh == null || LodHierarchy.OffsetFromRoot(root.transform, node.transform) == null) continue;
                    string reason = SkipReason(root.transform, node.transform, managed);
                    if (reason != null)
                    {
                        skipped.Add($"'{node.name}' {reason}");
                        continue;
                    }
                    var rootMesh = MeshAccess.ReadableCopy(mesh);
                    rootMesh.name = mesh.name;
                    MeshGeometry.TransformCollisionMesh(rootMesh, root.transform.worldToLocalMatrix * node.transform.localToWorldMatrix);
                    rootMesh.RecalculateNormals();
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
            using var plan = Plan(fbxPath, out var skipped);
            foreach (string reason in skipped) UvtLog.Warn($"[Collision] {reason}; not baked.");
            if (plan.rootPlacements.Count == 0)
            {
                UvtLog.Info($"[Collision] '{fbxPath}': no collision node off the root's placement to bake.");
                return false;
            }
            var bindings = FbxExport.CaptureSceneMeshReferences(fbxPath, Array.Empty<MeshEntry>());
            try
            {
                bool written = FbxChannelWrite.Write(fbxPath, Array.Empty<MeshEntry>(), FbxExportIntent.None, null, null, plan);
                if (written)
                    UvtLog.Info($"[Collision] '{fbxPath}': {plan.rootPlacements.Count} collision node(s) baked to the root's placement.");
                return written;
            }
            catch (FbxStructureRefusalException ex)
            {
                UvtLog.Error($"[Collision] '{fbxPath}' was not written: {ex.Message}");
                return false;
            }
            finally { bindings?.Relink(fbxPath); }
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
