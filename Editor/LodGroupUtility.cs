// LodGroupUtility.cs — Prefab-aware LODGroup rebuild + transition normalization.
// Shared by LodGenerationTool and PrefabBuilderTool so both end up with a clean
// component state after generation / edit (recreates the component instead of
// mutating in place, which sidesteps stale override tracking on prefab
// instances) and monotonically-decreasing transition heights.

using System.Collections.Generic;
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
    }
}
