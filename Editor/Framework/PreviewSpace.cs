using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Model coordinates for the isolated canvas. The selected root and
    /// all its scene ancestors contribute no translation, rotation or scale.</summary>
    internal static class PreviewSpace
    {
        internal static Matrix4x4 LocalToRoot(Transform source, Transform root)
        {
            if (!source || !root) return Matrix4x4.identity;
            var matrix = Matrix4x4.identity;
            var current = source;
            while (current && current != root) {
                matrix = Matrix4x4.TRS(current.localPosition, current.localRotation, current.localScale) * matrix;
                current = current.parent;
            }
            // A renderer outside the selected model has no shared model frame.
            return current == root ? matrix : Matrix4x4.identity;
        }
    }
}
