// UvHashUtil.cs — Shared hashing helpers for shell/face colour assignment.
// Extracted from UvCanvasView and ShellColorModelPreview so the 2D canvas and
// the 3D model preview cannot drift apart on how a key maps to a palette slot.

using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class UvHashUtil
    {
        /// <summary>
        /// Maps an arbitrary hash to a non-negative palette index source.
        /// <c>Mathf.Abs(int.MinValue)</c> overflows back to <c>int.MinValue</c>,
        /// which would produce a negative modulo and index outside the palette,
        /// so that one value is folded to <see cref="int.MaxValue"/> instead.
        /// </summary>
        internal static int NonNegativeColorKey(int colorKey)
        {
            return colorKey == int.MinValue ? int.MaxValue : Mathf.Abs(colorKey);
        }
    }
}
