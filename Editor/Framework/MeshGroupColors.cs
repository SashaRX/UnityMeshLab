// MeshGroupColors.cs — deterministic per-mesh-group palette.
// Both the Prefab Builder hierarchy and the UV2 Transfer per-mesh repack
// panel surface "mesh groups" identified by a stripped base-name key
// (UvToolContext.ExtractGroupKey). Routing both surfaces through the same
// hash → palette lookup means a given group always gets the same colour
// across the whole tool, so the user can visually correlate the group
// they're picking in the hierarchy with the group they're packing in
// UV2 Transfer.

using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class MeshGroupColors
    {
        // Twelve hand-tuned hues that read distinctly on the dark editor
        // background and don't clash with the status colours we already
        // reserve for fresh / stale / pending-delete row tints (orange,
        // amber, red).
        static readonly Color[] palette =
        {
            new Color(0.55f, 0.85f, 1.00f), // sky
            new Color(0.65f, 1.00f, 0.65f), // mint
            new Color(0.85f, 0.65f, 1.00f), // lavender
            new Color(0.55f, 0.85f, 0.85f), // teal
            new Color(1.00f, 0.85f, 0.55f), // sand
            new Color(0.85f, 1.00f, 0.55f), // lime
            new Color(0.65f, 0.85f, 1.00f), // periwinkle
            new Color(1.00f, 0.65f, 0.95f), // bubble
            new Color(0.65f, 1.00f, 0.95f), // aqua
            new Color(0.95f, 0.95f, 0.55f), // butter
            new Color(1.00f, 0.75f, 0.65f), // coral
            new Color(0.75f, 0.95f, 0.75f), // sage
        };

        /// <summary>
        /// Stable colour for a mesh-group key (e.g. "Stove_Base"). Same
        /// input always produces the same output — no per-session state,
        /// no hashing surprises across reloads. Empty / null keys fall
        /// back to a neutral grey so the caller can still draw the swatch
        /// without branching.
        /// </summary>
        public static Color GetColor(string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey))
                return new Color(0.6f, 0.6f, 0.6f);
            int hash = 17;
            for (int i = 0; i < groupKey.Length; i++)
                hash = unchecked(hash * 31 + groupKey[i]);
            int idx = (hash & int.MaxValue) % palette.Length;
            return palette[idx];
        }
    }
}
