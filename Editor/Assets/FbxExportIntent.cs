// FbxExportIntent.cs — describes which mesh channels an FBX re-save is
// allowed to mutate. Other tools snapshot the source FBX and write only
// the channels listed in the intent on the cloned mesh, so an isolated
// UV2 export will not touch UV0, UV1, vertex colors, normals, tangents,
// material assignments, mesh names, hierarchy, or transforms.

using System;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Channels that an isolated FBX re-save is allowed to overwrite.
    /// Anything outside the intent is preserved from the source FBX
    /// asset on disk via snapshot-and-restore in the export pipeline.
    /// </summary>
    [Flags]
    public enum FbxExportIntent
    {
        None         = 0,

        UV0          = 1 << 0,
        UV1          = 1 << 1,
        UV2          = 1 << 2,
        UV3          = 1 << 3,
        UV4          = 1 << 4,
        UV5          = 1 << 5,
        UV6          = 1 << 6,
        UV7          = 1 << 7,

        VertexColors = 1 << 8,
        Normals      = 1 << 9,
        Tangents     = 1 << 10,

        // Non-per-vertex categories. These describe whole-asset mutations
        // that the wide LOD-rebuild path performs; the narrow isolated
        // re-save path requires all four to be clear so it can guarantee
        // node names, materials, transforms and the LODGroup component
        // come through byte-identical from the source FBX.
        Hierarchy    = 1 << 11,
        Materials    = 1 << 12,
        Collision    = 1 << 13,
        LodGroup     = 1 << 14,

        AnyUv        = UV0 | UV1 | UV2 | UV3 | UV4 | UV5 | UV6 | UV7,
        All          = AnyUv | VertexColors | Normals | Tangents
                       | Hierarchy | Materials | Collision | LodGroup,
    }

    internal static class FbxExportIntentExtensions
    {
        /// <summary>
        /// True if the intent includes the per-vertex UV channel
        /// <paramref name="channel"/> (Unity Mesh.uv index 0-7).
        /// </summary>
        public static bool IncludesUv(this FbxExportIntent intent, int channel)
        {
            if ((uint)channel > 7u) return false;
            return (intent & (FbxExportIntent)(1 << channel)) != 0;
        }

        /// <summary>
        /// True if the intent writes any per-vertex channel that depends on
        /// stable vertex order (any UV, vertex colors, normals, tangents).
        /// Used to decide whether the source FBX importer must lock weld /
        /// compression / mesh-optimization before the snapshot is taken.
        /// </summary>
        public static bool TouchesPerVertex(this FbxExportIntent intent)
        {
            return (intent & (FbxExportIntent.AnyUv
                              | FbxExportIntent.VertexColors
                              | FbxExportIntent.Normals
                              | FbxExportIntent.Tangents)) != 0;
        }
    }
}
