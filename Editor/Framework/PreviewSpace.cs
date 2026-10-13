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

        // Foliage may expand a selected LOD child to sibling LOD renderers.
        // Express those siblings in the selected child's frame without passing
        // through world space or its potentially singular scene ancestors.
        internal static Matrix4x4 LocalToRelatedRoot(Transform source, Transform root)
        {
            if (!source || !root || source.IsChildOf(root)) return LocalToRoot(source, root);
            var sourceToCommon = Matrix4x4.identity;
            for (var common = source; common; common = common.parent) {
                if (root.IsChildOf(common))
                    return LocalToRoot(root, common).inverse * sourceToCommon;
                sourceToCommon = Matrix4x4.TRS(common.localPosition, common.localRotation, common.localScale) * sourceToCommon;
            }
            return Matrix4x4.identity;
        }
    }
}
