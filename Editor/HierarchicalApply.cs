// HierarchicalApply.cs — Stage 6 driver: take the per-LOD Result the
// hier pipeline produced, clone the LOD meshes with the new uv2 baked
// in (BuildFinalMeshes on HierarchicalRepack), then swap the clones
// into the LODGroup's MeshFilters under one Undo group so a single
// Ctrl+Z reverts the whole apply.
//
// Mesh-mutation safety rules (per unity-undo-prefab-safety):
//   1. New Mesh() — register with Undo.RegisterCreatedObjectUndo after
//      the assignment, so Undo destroys the clone on revert.
//   2. mf.sharedMesh = clone — Undo.RecordObject(mf, …) BEFORE the
//      mutation. Never touch mf.mesh in edit mode (clones + leaks).
//   3. If MeshFilter is on a prefab instance, follow up with
//      PrefabUtility.RecordPrefabInstancePropertyModifications(mf).
//   4. Group everything via Undo.IncrementCurrentGroup +
//      CollapseUndoOperations so a single Ctrl+Z reverses the whole
//      apply.

using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class HierarchicalApply
    {
        const string kMenuPath = "Mesh Lab/Hier/Apply UV2 to Selected LODGroup";

        [MenuItem(kMenuPath)]
        static void ApplyToSelectedLodGroup()
        {
            var sel = Selection.activeGameObject;
            if (sel == null)
            {
                EditorUtility.DisplayDialog("Hier UV2",
                    "Select a GameObject that's part of a LODGroup, then run the menu again.",
                    "OK");
                return;
            }
            var lg = sel.GetComponentInParent<LODGroup>();
            if (lg == null)
            {
                EditorUtility.DisplayDialog("Hier UV2",
                    $"'{sel.name}' has no LODGroup in its parent chain.",
                    "OK");
                return;
            }

            var opts = HierarchicalRepack.Options.Default;
            var result = HierarchicalRepack.Build(lg, opts);
            if (!string.IsNullOrEmpty(result.error))
            {
                EditorUtility.DisplayDialog("Hier UV2",
                    $"Build failed on '{lg.name}':\n{result.error}",
                    "OK");
                return;
            }

            HierarchicalRepack.BuildFinalMeshes(lg, result);
            int applyCount = ApplyFinalMeshesToLodGroup(lg, result);

            if (applyCount == 0)
            {
                EditorUtility.DisplayDialog("Hier UV2",
                    "No final meshes were produced for any LOD on this group.\n" +
                    "Check the Console for stage-specific warnings.",
                    "OK");
                return;
            }

            UvtLog.Info(UvtLog.Category.Benchmark,
                $"[HierRepack] Applied UV2 to '{lg.name}' ({applyCount} LODs). Ctrl+Z to revert.");
            EditorUtility.DisplayDialog("Hier UV2",
                $"Applied hierarchical UV2 to {applyCount} LOD(s) on '{lg.name}'.\n" +
                "Use Ctrl+Z to revert.",
                "OK");
        }

        [MenuItem(kMenuPath, true)]
        static bool ApplyToSelectedLodGroupValidate()
        {
            var sel = Selection.activeGameObject;
            return sel != null && sel.GetComponentInParent<LODGroup>() != null;
        }

        /// <summary>Swap each fine LOD's MeshFilter.sharedMesh for the
        /// matching <c>result.finalMeshes[li]</c>. All swaps land in one
        /// Undo group named after the LODGroup so a single Ctrl+Z reverts
        /// the apply. Returns the count of LODs actually swapped.</summary>
        internal static int ApplyFinalMeshesToLodGroup(LODGroup lg,
            HierarchicalRepack.Result result)
        {
            if (lg == null || result.finalMeshes == null) return 0;
            var lods = lg.GetLODs();

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName($"Apply Hier UV2 ({lg.name})");

            int applied = 0;
            try
            {
                for (int li = 0; li < lods.Length && li < result.finalMeshes.Length; li++)
                {
                    var clone = result.finalMeshes[li];
                    if (clone == null) continue;
                    var rs = lods[li].renderers;
                    if (rs == null || rs.Length == 0 || rs[0] == null) continue;
                    var mf = rs[0].GetComponent<MeshFilter>();
                    if (mf == null) continue;

                    // Register the new mesh first — RegisterCreatedObjectUndo
                    // attaches it to the current undo group so Ctrl+Z
                    // destroys the clone alongside reverting the MF swap.
                    Undo.RegisterCreatedObjectUndo(clone, "Apply Hier UV2 (mesh)");

                    // Snapshot the MeshFilter's state BEFORE mutation so
                    // Undo restores the original sharedMesh reference.
                    Undo.RecordObject(mf, "Apply Hier UV2 (filter)");
                    mf.sharedMesh = clone;

                    // Persist as a prefab-instance override when relevant.
                    if (PrefabUtility.IsPartOfPrefabInstance(mf))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(mf);

                    applied++;
                }
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }
            return applied;
        }
    }
}
