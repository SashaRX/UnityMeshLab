// LodGroupUtility.cs — Prefab-aware LODGroup rebuild + transition normalization.
// Shared by LodGenerationTool and PrefabBuilderTool so both end up with a clean
// component state after generation / edit (recreates the component instead of
// mutating in place, which sidesteps stale override tracking on prefab
// instances) and monotonically-decreasing transition heights.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    internal static class LodGroupUtility
    {
        /// <summary>
        /// Destroy the current LODGroup (if any), re-add a fresh one, copy the
        /// preserved settings, and assign <paramref name="newLods"/>. Returns
        /// the new component.
        ///
        /// Doing destroy+add instead of in-place <c>SetLODs</c> avoids two
        /// common prefab-instance headaches:
        ///   1) Old LOD slot overrides layered on fresh geometry produce
        ///      phantom renderers after re-import.
        ///   2) Re-serialization of a mutated LODGroup with stale renderer
        ///      refs sometimes hides prefab children on next domain reload.
        /// Undo.DestroyObjectImmediate + Undo.AddComponent tracks the swap as
        /// removed-then-added overrides, which the prefab system handles.
        /// </summary>
        internal static LODGroup Rebuild(GameObject root, LOD[] newLods)
        {
            if (root == null) return null;

            // Preserve user-facing LODGroup settings so the rebuild is
            // transparent.
            var old = root.GetComponent<LODGroup>();
            var size = 1f;
            var fadeMode = LODFadeMode.None;
            var animateCrossFading = false;
            var localReferencePoint = Vector3.zero;
            bool wasEnabled = true;
            if (old != null)
            {
                size = old.size;
                fadeMode = old.fadeMode;
                animateCrossFading = old.animateCrossFading;
                localReferencePoint = old.localReferencePoint;
                wasEnabled = old.enabled;
                Undo.DestroyObjectImmediate(old);
            }

            var lg = Undo.AddComponent<LODGroup>(root);
            lg.size = size;
            lg.fadeMode = fadeMode;
            lg.animateCrossFading = animateCrossFading;
            lg.localReferencePoint = localReferencePoint;
            lg.enabled = wasEnabled;

            if (newLods != null && newLods.Length > 0)
                lg.SetLODs(NormalizeTransitions(newLods));

            // Mark LODGroup settings as modified so prefab-instance overrides
            // for size / fade / lods are persisted.
            if (PrefabUtility.IsPartOfPrefabInstance(lg))
                PrefabUtility.RecordPrefabInstancePropertyModifications(lg);

            return lg;
        }

        /// <summary>
        /// Apply <paramref name="newLods"/> to <paramref name="lg"/> in-place,
        /// normalising transitions. Use when the LODGroup component doesn't
        /// need to be recreated (e.g. transition-only edits).
        /// </summary>
        internal static void ApplyLods(LODGroup lg, LOD[] newLods)
        {
            if (lg == null || newLods == null) return;
            Undo.RecordObject(lg, "Update LODs");
            lg.SetLODs(NormalizeTransitions(newLods));
            if (PrefabUtility.IsPartOfPrefabInstance(lg))
                PrefabUtility.RecordPrefabInstancePropertyModifications(lg);
        }

        /// <summary>
        /// Returns a LOD[] copy with strictly decreasing transition heights.
        /// Unity requires LOD[i].screenRelativeTransitionHeight &gt;
        /// LOD[i+1].screenRelativeTransitionHeight; out-of-order entries cause
        /// silent LOD flicker or the wrong mesh rendering. Preserves the
        /// first value and nudges later ones down when needed.
        /// </summary>
        internal static LOD[] NormalizeTransitions(LOD[] lods)
        {
            if (lods == null || lods.Length == 0) return lods;
            var copy = new LOD[lods.Length];
            System.Array.Copy(lods, copy, lods.Length);

            // Clamp the first into (0,1].
            if (copy[0].screenRelativeTransitionHeight <= 0f || copy[0].screenRelativeTransitionHeight > 1f)
                copy[0] = new LOD(Mathf.Clamp(copy[0].screenRelativeTransitionHeight, 0.01f, 1f), copy[0].renderers);

            const float minStep = 0.001f;
            for (int i = 1; i < copy.Length; i++)
            {
                float prev = copy[i - 1].screenRelativeTransitionHeight;
                float h = copy[i].screenRelativeTransitionHeight;
                if (h >= prev - minStep)
                {
                    // Pull the current entry below the previous by at least
                    // minStep. Falling back to half of prev when the current
                    // value was absurd (>=prev) or missing.
                    h = h < prev - minStep ? h : Mathf.Max(prev * 0.5f, minStep);
                    copy[i] = new LOD(h, copy[i].renderers);
                }
                // Clamp lower bound.
                if (copy[i].screenRelativeTransitionHeight < minStep)
                    copy[i] = new LOD(minStep, copy[i].renderers);
            }
            return copy;
        }
        internal static List<(GameObject go, int lodIndex)> FindLodSiblings(GameObject go)
        {
            if (go == null) return null;
            if (!MeshNaming.HasLodSuffix(go.name))
                return FindNamedLodChildren(go.transform, null);
            // An invalid suffix cannot become an index in the LODGroup.
            if (!MeshNaming.TryParseLod(go.name, out string baseName, out _)) return null;
            return FindNamedLodChildren(go.transform.parent, baseName);
        }

        internal static bool HasAmbiguousLodChains(GameObject root)
        {
            if (root == null || MeshNaming.HasLodSuffix(root.name)) return false;
            var names = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < root.transform.childCount; ++i)
                if (MeshNaming.TryParseLod(root.transform.GetChild(i).name, out var name, out _)) names.Add(name);
            return names.Count > 1;
        }

        static List<(GameObject go, int lodIndex)> FindNamedLodChildren(Transform parent, string baseName)
        {
            if (parent == null) return null;
            var results = new List<(GameObject, int)>();
            string chain = baseName;
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i).gameObject;
                if (!MeshNaming.TryParseLod(child.name, out string childBase, out int lodIndex)) continue;
                if (baseName != null && !string.Equals(childBase, baseName, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (chain != null && baseName == null && !string.Equals(childBase, chain, System.StringComparison.OrdinalIgnoreCase)) return null;
                chain = childBase;
                results.Add((child, lodIndex));
            }
            results.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return results.Count > 0 ? results : null;
        }

        internal static LODGroup CreateLodGroupStatic(List<(GameObject go, int lodIndex)> siblings)
            => LodHierarchy.CreateFromSiblings(siblings);

        internal static void NormalizeSingleLodTransitionForGeneration(List<LOD> lods, int startLod)
        {
            if (startLod != 1 || lods.Count != 1 ||
                !Mathf.Approximately(lods[0].screenRelativeTransitionHeight, 0.01f))
                return;

            lods[0] = new LOD(0.5f, lods[0].renderers);
        }

        internal static LODGroup CreateLodGroupFromRenderers(GameObject root)
            => LodHierarchy.CreateFromRenderers(root, 0.01f, requireRenderers: true, out _);

        static bool IsMeshReferenced(Mesh mesh)
        {
            foreach (var filter in Resources.FindObjectsOfTypeAll<MeshFilter>())
                if (filter.sharedMesh == mesh) return true;
            foreach (var renderer in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
                if (renderer.sharedMesh == mesh) return true;
            return false;
        }

        /// <summary>
        /// After generated LODs were written into the FBX and it was reimported: every LOD slot
        /// holding a generated scene object takes the imported renderer of the same name under
        /// the group instead (transitions kept), and those generated objects are destroyed. A
        /// generated object with no imported counterpart (the scene object is not an instance
        /// of the model, or its file is rebuilt later) or several keeps its slot. Returns the
        /// renderers adopted.
        /// </summary>
        internal static int AdoptImportedLods(UvToolContext ctx)
        {
            if (ctx?.LodGroup == null || ctx.GeneratedLodObjects.Count == 0) return 0;
            var generated = new HashSet<GameObject>();
            foreach (var go in ctx.GeneratedLodObjects)
                if (go != null) generated.Add(go);
            // Counterparts by name; a name more than one imported renderer carries is ambiguous.
            var imported = new Dictionary<string, Renderer>();
            var ambiguous = new HashSet<string>();
            foreach (var r in ctx.LodGroup.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || generated.Contains(r.gameObject)) continue;
                if (imported.ContainsKey(r.name)) ambiguous.Add(r.name);
                else imported[r.name] = r;
            }

            var lods = ctx.LodGroup.GetLODs();
            int adopted = 0;
            var replaced = new HashSet<GameObject>();
            foreach (var lod in lods)
            {
                if (lod.renderers == null) continue;
                for (int i = 0; i < lod.renderers.Length; i++)
                {
                    var r = lod.renderers[i];
                    if (r == null || !generated.Contains(r.gameObject)) continue;
                    if (!imported.TryGetValue(r.name, out var written) || ambiguous.Contains(r.name))
                    {
                        UvtLog.Warn(ambiguous.Contains(r.name)
                            ? $"[LOD] Several imported renderers under '{ctx.LodGroup.name}' are named '{r.name}'; its generated LOD object stays."
                            : $"[LOD] '{r.name}' has no imported counterpart under '{ctx.LodGroup.name}'; its generated LOD object stays.");
                        continue;
                    }
                    lod.renderers[i] = written;
                    KeepMaterials(r, written);
                    replaced.Add(r.gameObject);
                    adopted++;
                }
            }
            if (adopted == 0) return 0;
            int undoGroup = Undo.GetCurrentGroup();
            Undo.RecordObject(ctx.LodGroup, "Adopt Written LODs");
            ctx.LodGroup.SetLODs(lods);
            ctx.LodGroup.RecalculateBounds();
            if (PrefabUtility.IsPartOfPrefabInstance(ctx.LodGroup))
                PrefabUtility.RecordPrefabInstancePropertyModifications(ctx.LodGroup);
            // Only the objects no slot holds any more go; the rest stay generated.
            foreach (var lod in lods)
                foreach (var r in lod.renderers ?? System.Array.Empty<Renderer>())
                    if (r != null) replaced.Remove(r.gameObject);
            DestroyGeneratedLodObjects(ctx, replaced);
            ctx.GeneratedLodObjects.RemoveAll(replaced.Contains);
            Undo.CollapseUndoOperations(undoGroup);
            return adopted;
        }

        // The imported renderer shows what the generated one did: materials the import does not
        // give it (material import off, or a slot the save could not map) stay as an override.
        static void KeepMaterials(Renderer generated, Renderer imported)
        {
            var materials = generated.sharedMaterials;
            if (materials.SequenceEqual(imported.sharedMaterials)) return;
            Undo.RecordObject(imported, "Adopt Written LODs");
            imported.sharedMaterials = materials;
            if (PrefabUtility.IsPartOfPrefabInstance(imported))
                PrefabUtility.RecordPrefabInstancePropertyModifications(imported);
        }

        internal static void ClearGeneratedLods(UvToolContext ctx, bool refreshContext = true)
        {
            if (ctx.GeneratedLodObjects.Count == 0) return;
            int undoGroup = Undo.GetCurrentGroup();
            RemoveGeneratedLodSlots(ctx);
            DestroyGeneratedLodObjects(ctx);
            Undo.CollapseUndoOperations(undoGroup);
            ctx.GeneratedLodObjects.Clear();
            if (ctx.LodGroup == null) return;
            if (refreshContext) ctx.Refresh(ctx.LodGroup);
            else
            {
                // Regeneration keeps the source entries and their edited working
                // channels, which also key the already validated preflight data.
                ctx.MeshEntries.RemoveAll(entry => entry.renderer == null);
                ctx.ClearAllCaches();
            }
        }

        static void RemoveGeneratedLodSlots(UvToolContext ctx)
        {
            if (ctx.LodGroup == null) return;
            var generatedSet = new HashSet<GameObject>();
            foreach (var go in ctx.GeneratedLodObjects)
                if (go != null) generatedSet.Add(go);
            if (generatedSet.Count == 0) return;
            var cleanedLods = new List<LOD>();
            foreach (var lod in ctx.LodGroup.GetLODs())
            {
                var remaining = RemainingLodRenderers(lod, generatedSet);
                if (remaining.Count > 0)
                    cleanedLods.Add(new LOD(lod.screenRelativeTransitionHeight, remaining.ToArray()) { fadeTransitionWidth = lod.fadeTransitionWidth });
            }
            Undo.RecordObject(ctx.LodGroup, "Clear Generated LODs");
            ctx.LodGroup.SetLODs(cleanedLods.ToArray());
            if (PrefabUtility.IsPartOfPrefabInstance(ctx.LodGroup))
                PrefabUtility.RecordPrefabInstancePropertyModifications(ctx.LodGroup);
        }

        static List<Renderer> RemainingLodRenderers(LOD lod, HashSet<GameObject> generatedSet)
        {
            var remaining = new List<Renderer>();
            if (lod.renderers == null) return remaining;
            foreach (var renderer in lod.renderers)
                if (renderer != null && !generatedSet.Contains(renderer.gameObject)) remaining.Add(renderer);
            return remaining;
        }

        static void DestroyGeneratedLodObjects(UvToolContext ctx) => DestroyGeneratedLodObjects(ctx, ctx.GeneratedLodObjects);

        static void DestroyGeneratedLodObjects(UvToolContext ctx, IEnumerable<GameObject> objects)
        {
            var ownedMeshes = new HashSet<Mesh>();
            foreach (var go in objects)
            {
                if (ctx.GeneratedLodMeshes.TryGetValue(go, out var mesh))
                {
                    if (mesh && !EditorUtility.IsPersistent(mesh)) ownedMeshes.Add(mesh);
                    ctx.GeneratedLodMeshes.Remove(go);
                }
                if (go != null) Undo.DestroyObjectImmediate(go);
            }
            foreach (var mesh in ownedMeshes)
                if (!IsMeshReferenced(mesh)) Undo.DestroyObjectImmediate(mesh);
        }

    }
}
