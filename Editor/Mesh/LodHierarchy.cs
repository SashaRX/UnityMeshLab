// LodHierarchy.cs — a LODGroup read from and written to its hierarchy: the LOD
// renderers a root's children name, the LOD array built from them, a group created
// from renderers, detected siblings or every renderer by its name, the compaction of
// a LOD array, a root mesh moved into a LOD0 child with the children sorted by
// polycount, and the root MeshCollider fed from a collision child. The Cleanup,
// Prefab Builder and LOD Generation tabs and the tool context each carried their
// own; this is the one implementation.
// LodGroupUtility stays the component-level half (recreate, apply, transitions).
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class LodHierarchy
    {
        /// <summary>How the transition heights of a rebuilt LOD array are laid out.</summary>
        internal enum Transitions
        {
            /// <summary>0.5, 0.25, 0.125 … (the Cleanup tab and detected-sibling groups).</summary>
            Halving,
            /// <summary>1.0 down to 0.01 in equal steps; 0.01 for a single level (the Prefab Builder).</summary>
            Linear,
        }

        // ── reading the hierarchy ──

        /// <summary>
        /// The renderers under <paramref name="root"/> whose node names carry a LOD index,
        /// per index: the root's direct children only, or every renderer below the root
        /// (meshes inside group containers) when <paramref name="recursive"/>. Collision
        /// objects are skipped; the root itself is never included.
        /// </summary>
        internal static SortedDictionary<int, List<Renderer>> CollectByName(Transform root, bool recursive)
        {
            var byIndex = new SortedDictionary<int, List<Renderer>>();
            if (root == null) return byIndex;
            var colSet = new HashSet<GameObject>(MeshHygieneUtility.FindCollisionObjects(root));

            void Add(Renderer r)
            {
                if (r == null || r.transform == root || colSet.Contains(r.gameObject)) return;
                if (!MeshNaming.TryParseLod(r.gameObject.name, out _, out int lodIdx)) return;
                if (!byIndex.TryGetValue(lodIdx, out var list)) byIndex[lodIdx] = list = new List<Renderer>();
                list.Add(r);
            }

            if (recursive)
                foreach (var r in root.GetComponentsInChildren<Renderer>(true)) Add(r);
            else
                for (int i = 0; i < root.childCount; i++) Add(root.GetChild(i).GetComponent<Renderer>());
            return byIndex;
        }

        /// <summary>
        /// The LOD array for a name map: one slot per index from 0 to the highest found
        /// (gaps as empty slots) with <paramref name="keepEmptySlots"/>, else one slot per
        /// index present; transitions per <paramref name="scheme"/>. Empty for an empty map.
        /// </summary>
        internal static LOD[] LodsFromNames(SortedDictionary<int, List<Renderer>> byIndex, bool keepEmptySlots, Transitions scheme)
        {
            if (byIndex == null || byIndex.Count == 0) return Array.Empty<LOD>();
            var slots = new List<Renderer[]>();
            if (keepEmptySlots)
            {
                int max = byIndex.Keys.Max();
                for (int i = 0; i <= max; i++)
                    slots.Add(byIndex.TryGetValue(i, out var list) ? list.ToArray() : Array.Empty<Renderer>());
            }
            else
            {
                foreach (var kvp in byIndex) slots.Add(kvp.Value.ToArray());
            }

            var lods = new LOD[slots.Count];
            for (int i = 0; i < lods.Length; i++)
                lods[i] = new LOD(Transition(scheme, i, lods.Length), slots[i]);
            return lods;
        }

        /// <summary>The transition height of slot <paramref name="index"/> of <paramref name="count"/> under a scheme.</summary>
        internal static float Transition(Transitions scheme, int index, int count)
        {
            switch (scheme)
            {
                case Transitions.Linear:
                    return count <= 1 ? 0.01f : 1f - ((float)index / (count - 1)) * 0.99f; // 1.0 → 0.01
                default:
                    return Mathf.Pow(0.5f, index + 1);
            }
        }

        // ── writing the group ──

        /// <summary>
        /// Rebuilds <paramref name="lodGroup"/>'s LOD array from the names under its root
        /// (see <see cref="CollectByName"/> and <see cref="LodsFromNames"/>), with Undo and
        /// recalculated bounds. Returns the number of LOD levels set; 0 when no LOD-named
        /// renderer was found and the group was left alone.
        /// </summary>
        internal static int RebuildFromNames(LODGroup lodGroup, bool recursive, bool keepEmptySlots, Transitions scheme, string undoLabel = "Rebuild LODGroup")
        {
            if (lodGroup == null) return 0;
            var lods = LodsFromNames(CollectByName(lodGroup.transform, recursive), keepEmptySlots, scheme);
            if (lods.Length == 0) return 0;
            Undo.RecordObject(lodGroup, undoLabel);
            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();
            return lods.Length;
        }

        /// <summary>
        /// Gives <paramref name="root"/> a LODGroup (a LODGroup it already has is reused,
        /// since a GameObject holds at most one) with every renderer below it as LOD0 at
        /// <paramref name="transition"/>. With <paramref name="requireRenderers"/> nothing is
        /// added and null returned when there is no renderer; otherwise the group is added
        /// empty. <paramref name="rendererCount"/> tells how many went in.
        /// </summary>
        internal static LODGroup CreateFromRenderers(GameObject root, float transition, bool requireRenderers, out int rendererCount)
        {
            rendererCount = 0;
            if (root == null) return null;
            var renderers = root.GetComponentsInChildren<Renderer>();
            rendererCount = renderers.Length;
            if (requireRenderers && renderers.Length == 0) return null;

            var lodGroup = GroupOn(root);
            if (renderers.Length > 0)
            {
                lodGroup.SetLODs(new[] { new LOD(transition, renderers) });
                lodGroup.RecalculateBounds();
            }
            return lodGroup;
        }

        /// <summary>
        /// Gathers every renderer below <paramref name="root"/> (its own included, collision
        /// objects left out) into one LODGroup on it: a renderer whose node name carries a
        /// LOD index goes to that level, any other to LOD0. One slot per index found (gaps
        /// close up) with halving transitions; a single level gets 0.01 so it never culls.
        /// LODGroups further down are removed, one on the root is reused, and the renderers
        /// that go in are enabled with their objects activated. One Undo step. Null, and
        /// nothing changed, when there is no renderer.
        /// </summary>
        internal static LODGroup CreateByName(GameObject root, out int levels, out int removedGroups)
        {
            levels = removedGroups = 0;
            if (root == null) return null;
            var colSet = new HashSet<GameObject>(MeshHygieneUtility.FindCollisionObjects(root.transform));
            var byIndex = new SortedDictionary<int, List<Renderer>>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (colSet.Contains(r.gameObject) || MeshNaming.IsCollision(r.gameObject.name)) continue;
                int index = r.transform != root.transform && MeshNaming.TryParseLod(r.gameObject.name, out _, out int lod) ? lod : 0;
                if (!byIndex.TryGetValue(index, out var list)) byIndex[index] = list = new List<Renderer>();
                list.Add(r);
            }
            if (byIndex.Count == 0) return null;

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create LODGroup by name");
            foreach (var nested in root.GetComponentsInChildren<LODGroup>(true))
                if (nested.gameObject != root) { Undo.DestroyObjectImmediate(nested); removedGroups++; }
            foreach (var list in byIndex.Values)
                foreach (var r in list) Show(r, root.transform);

            var lods = LodsFromNames(byIndex, keepEmptySlots: false, Transitions.Halving);
            if (lods.Length == 1) lods[0].screenRelativeTransitionHeight = 0.01f;
            var lodGroup = GroupOn(root);
            lodGroup.fadeMode = LODFadeMode.CrossFade;
            lodGroup.animateCrossFading = false;
            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();
            Undo.CollapseUndoOperations(undoGroup);
            levels = lods.Length;
            return lodGroup;
        }

        // A renderer going into the group is drawn: enabled, with its object and every
        // object between it and the root active.
        static void Show(Renderer renderer, Transform root)
        {
            if (!renderer.enabled) { Undo.RecordObject(renderer, "Create LODGroup by name"); renderer.enabled = true; }
            for (var t = renderer.transform; t != null && t != root; t = t.parent)
                if (!t.gameObject.activeSelf) { Undo.RecordObject(t.gameObject, "Create LODGroup by name"); t.gameObject.SetActive(true); }
        }

        /// <summary>
        /// Adds a LODGroup to the parent of detected LOD siblings (<c>Foo_LOD0</c>,
        /// <c>Foo_LOD2</c> …), one slot per sibling in list order (gaps close up), halving
        /// transitions, each sibling's renderers in its slot.
        /// </summary>
        internal static LODGroup CreateFromSiblings(List<(GameObject go, int lodIndex)> siblings)
        {
            if (siblings == null || siblings.Count == 0 || siblings[0].go == null || siblings[0].go.transform.parent == null) return null;
            var lodRoot = siblings[0].go.transform.parent.gameObject;
            var lodGroup = GroupOn(lodRoot);
            var lods = new LOD[siblings.Count];
            for (int i = 0; i < siblings.Count; i++)
                lods[i] = new LOD(Transition(Transitions.Halving, i, siblings.Count), siblings[i].go.GetComponentsInChildren<Renderer>());
            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();
            return lodGroup;
        }

        // One LODGroup per GameObject: AddComponent on a root that already has one returns
        // null (and logs), so the existing component is recorded for Undo and reused.
        static LODGroup GroupOn(GameObject root)
        {
            var existing = root.GetComponent<LODGroup>();
            if (existing == null) return Undo.AddComponent<LODGroup>(root);
            Undo.RecordObject(existing, "Create LODGroup");
            return existing;
        }

        /// <summary>
        /// Drops null renderers from every slot and, with <paramref name="removeEmptySlots"/>,
        /// the slots left empty (otherwise user-added empty levels survive). Writes back with
        /// Undo only when something changed. Returns whether it did.
        /// </summary>
        internal static bool Compact(LODGroup lodGroup, bool removeEmptySlots)
        {
            if (lodGroup == null) return false;
            var lods = lodGroup.GetLODs();
            var compacted = new List<LOD>();
            bool changed = false;
            foreach (var lod in lods)
            {
                var renderers = lod.renderers ?? Array.Empty<Renderer>();
                var valid = renderers.Where(r => r != null).ToArray();
                if (valid.Length != renderers.Length) changed = true;
                if (valid.Length == 0)
                {
                    if (removeEmptySlots) { changed = true; continue; }
                    compacted.Add(new LOD(lod.screenRelativeTransitionHeight, Array.Empty<Renderer>()));
                    continue;
                }
                compacted.Add(new LOD(lod.screenRelativeTransitionHeight, valid));
            }
            if (!changed) return false;
            Undo.RecordObject(lodGroup, "Compact LOD Array");
            lodGroup.SetLODs(compacted.ToArray());
            UvtLog.Info($"[Context] Compacted LOD array: {lods.Length} → {compacted.Count} slots.");
            return true;
        }

        // ── the root ──

        /// <summary>True when the root's own renderer sits in LOD0 (a root mesh that is the LOD0 by design, not a stray).</summary>
        internal static bool RootRendererIsLod0(GameObject root, LOD[] lods)
        {
            if (root == null || lods == null || lods.Length == 0) return false;
            var rootRenderer = root.GetComponent<Renderer>();
            if (rootRenderer == null) return false;
            var lod0 = lods[0].renderers;
            if (lod0 == null) return false;
            for (int i = 0; i < lod0.Length; i++)
                if (lod0[i] == rootRenderer) return true;
            return false;
        }

        /// <summary>
        /// Moves the root's MeshFilter/MeshRenderer into a new child (materials, renderer
        /// settings and static flags carried over; a MeshCollider stays on the root, where
        /// the Node(Collider) → LOD children convention wants it), then renames the root's
        /// mesh children <c>{base}_LOD0..N</c> by descending polycount
        /// (<see cref="SortChildrenAsLods"/>). Everything is recorded for Undo. Returns the
        /// number of LOD children named; 0 when the root has no mesh.
        /// </summary>
        internal static int MoveRootMeshToChild(GameObject root)
        {
            if (root == null) return 0;
            var rootMf = root.GetComponent<MeshFilter>();
            var rootMr = root.GetComponent<MeshRenderer>();
            if (rootMf == null || rootMf.sharedMesh == null) return 0;

            string baseName = BaseName(root.name);
            var lod0Child = new GameObject(baseName + "_temp");
            Undo.RegisterCreatedObjectUndo(lod0Child, "Move Root Mesh");
            lod0Child.transform.SetParent(root.transform, false);
            lod0Child.AddComponent<MeshFilter>().sharedMesh = rootMf.sharedMesh;
            if (rootMr != null)
            {
                var newMr = lod0Child.AddComponent<MeshRenderer>();
                // A visible preview's material is not the root's own: the child would keep it
                // after the preview restores the root, which this move destroys.
                newMr.sharedMaterials = UvToolHub.OriginalPreviewMaterials(rootMr, forAssetWrite: true);
                RendererSettings.Copy(rootMr, newMr, includeMaterials: false);
                GameObjectUtility.SetStaticEditorFlags(lod0Child, GameObjectUtility.GetStaticEditorFlags(root));
                Undo.DestroyObjectImmediate(rootMr);
            }
            Undo.DestroyObjectImmediate(rootMf);

            return SortChildrenAsLods(root.transform, baseName);
        }

        /// <summary>The group key of a node name, sanitized; "Unnamed" when nothing is left.</summary>
        internal static string BaseName(string nodeName)
        {
            string baseName = MeshNaming.GroupKey(nodeName);
            if (string.IsNullOrEmpty(baseName)) baseName = nodeName;
            baseName = MeshHygieneUtility.SanitizeName(baseName);
            return string.IsNullOrEmpty(baseName) ? "Unnamed" : baseName;
        }

        /// <summary>
        /// Renames the root's direct mesh children (collision objects excluded)
        /// <c>{baseName}_LOD0..N</c> in descending polycount order, through temporary names
        /// so existing <c>_LOD</c> names cannot collide. Returns how many were named.
        /// </summary>
        internal static int SortChildrenAsLods(Transform root, string baseName)
        {
            var colSet = new HashSet<GameObject>(MeshHygieneUtility.FindCollisionObjects(root));
            var candidates = new List<(Transform t, int polyCount)>();
            foreach (Transform child in root)
            {
                if (colSet.Contains(child.gameObject)) continue;
                var mf = child.GetComponent<MeshFilter>();
                var smr = child.GetComponent<SkinnedMeshRenderer>();
                var mesh = mf != null ? mf.sharedMesh : (smr != null ? smr.sharedMesh : null);
                if (mesh == null) continue;
                candidates.Add((child, MeshHygieneUtility.GetTriangleCount(mesh)));
            }
            candidates.Sort((a, b) => b.polyCount.CompareTo(a.polyCount));

            for (int i = 0; i < candidates.Count; i++)
            {
                Undo.RecordObject(candidates[i].t.gameObject, "Rename LOD");
                candidates[i].t.name = "__UVTMP_LOD_" + i + "_" + Guid.NewGuid().ToString("N");
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                Undo.RecordObject(candidates[i].t.gameObject, "Rename LOD");
                candidates[i].t.name = baseName + "_LOD" + i;
            }
            return candidates.Count;
        }

        // ── the collider ──

        /// <summary>How far a placement may lie from the root's: world metres for the position, matrix entries for rotation and scale.</summary>
        const float PlacementTolerance = 1e-4f;

        /// <summary>
        /// Where <paramref name="node"/> sits relative to <paramref name="root"/> when it is not
        /// at the root's placement (its position, rotation and scale there); null when it is.
        /// A mesh on the root's collider lands where it lies on the node only when this is null.
        /// </summary>
        internal static string OffsetFromRoot(Transform root, Transform node)
        {
            var m = root.worldToLocalMatrix * node.localToWorldMatrix;
            // The position apart, in world metres: a scaled root scales its local units.
            bool at = root.TransformVector(m.GetColumn(3)).magnitude <= PlacementTolerance;
            for (int r = 0; r < 3 && at; r++)
                for (int c = 0; c < 3 && at; c++)
                    at = Mathf.Abs(m[r, c] - (r == c ? 1f : 0f)) <= PlacementTolerance;
            if (at) return null;
            Vector3 p = m.GetColumn(3), e = m.rotation.eulerAngles, s = m.lossyScale;
            return $"position ({p.x:F3}, {p.y:F3}, {p.z:F3}), rotation ({e.x:F1}, {e.y:F1}, {e.z:F1}), scale ({s.x:F3}, {s.y:F3}, {s.z:F3})";
        }

        /// <summary>
        /// Whether the model has a MeshCollider that can collide: one with a mesh on the root,
        /// or an enabled one with a mesh on an active collision node.
        /// </summary>
        internal static bool HasCollisionCollider(GameObject root)
            => root != null && ((root.TryGetComponent(out MeshCollider own) && own.sharedMesh != null)
                || MeshHygieneUtility.FindCollisionObjects(root.transform).Any(o =>
                    o.activeInHierarchy && o.TryGetComponent(out MeshCollider c) && c.enabled && c.sharedMesh != null));

        /// <summary>
        /// Gives the first collision child that carries a mesh a MeshCollider over it and
        /// disables that child's renderer: on the root when the child sits at the root's
        /// placement, else on the child itself, where its mesh lies (activated when it alone
        /// is inactive; skipped, with a warning, under an inactive node). Null when the root
        /// or an active collision child already has a MeshCollider, or no collision child
        /// carries a mesh it can take.
        /// </summary>
        internal static MeshCollider AddColliderFromCollisionMesh(GameObject root)
        {
            if (root == null || HasCollisionCollider(root)) return null;
            foreach (var colObj in MeshHygieneUtility.FindCollisionObjects(root.transform))
            {
                var mf = colObj.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                string offset = OffsetFromRoot(root.transform, colObj.transform);
                if (offset != null && !colObj.activeInHierarchy)
                {
                    var parent = colObj.transform.parent;
                    if (parent != null && !parent.gameObject.activeInHierarchy)
                    {
                        UvtLog.Warn($"{colObj.name} sits off {root.name}'s placement under an inactive node, so a collider on it would not collide; "
                            + "activate that node, or bake its offset into the FBX.");
                        continue;
                    }
                    Undo.RecordObject(colObj, "Activate COL");
                    colObj.SetActive(true);
                    RecordInstanceOverride(colObj);
                }
                var host = offset == null ? root : colObj;
                var collider = host.GetComponent<MeshCollider>();
                bool added = collider == null;
                if (added) collider = Undo.AddComponent<MeshCollider>(host);
                else Undo.RecordObject(collider, "Assign Collision Mesh");
                collider.enabled = true;
                collider.sharedMesh = mf.sharedMesh;
                if (!added) RecordInstanceOverride(collider);
                UvtLog.Info(offset == null
                    ? $"Added MeshCollider to {root.name} from {colObj.name}"
                    : $"Added MeshCollider to {colObj.name}: it sits off {root.name}'s placement ({offset}), so on the root its mesh would land elsewhere");
                var mr = colObj.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    Undo.RecordObject(mr, "Disable COL Renderer");
                    mr.enabled = false;
                }
                return collider;
            }
            return null;
        }

        // A change to an object of a prefab instance is kept as an override of that instance.
        static void RecordInstanceOverride(UnityEngine.Object target)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(target)) PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }

        /// <summary>
        /// The root's MeshCollider (added when missing, recorded when present) set to
        /// <paramref name="mesh"/>. With the node the mesh comes from, refused (null, with a
        /// warning) when that node sits off the root's placement: on the root the mesh would
        /// land elsewhere.
        /// </summary>
        internal static MeshCollider AssignCollider(GameObject root, Mesh mesh, Transform from = null)
        {
            if (root == null || mesh == null) return null;
            string offset = from != null ? OffsetFromRoot(root.transform, from) : null;
            if (offset != null)
            {
                UvtLog.Warn($"'{mesh.name}' was not put on {root.name}'s MeshCollider: '{from.name}' sits off the root's placement ({offset}), "
                    + "so the collider would land elsewhere. Give that node its own MeshCollider, or bake its transform into the mesh.");
                return null;
            }
            var mc = root.GetComponent<MeshCollider>();
            if (mc == null)
            {
                mc = Undo.AddComponent<MeshCollider>(root);
                UvtLog.Info($"Added MeshCollider to {root.name}");
            }
            else
            {
                Undo.RecordObject(mc, "Assign Collision Mesh");
            }
            mc.sharedMesh = mesh;
            return mc;
        }
    }
}
