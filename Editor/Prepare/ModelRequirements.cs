// ModelRequirements.cs — what a model must satisfy, and how hard: each check is Off, a
// Warning (shown, Apply runs) or an Error (blocks Apply). Serializable so a recipe can
// carry its own set; Default is the static-prop rule set.

using System;

namespace SashaRX.UnityMeshLab
{
    /// <summary>How a failed requirement is reported.</summary>
    internal enum RequirementLevel { Off, Warning, Error }

    [Serializable]
    internal sealed class ModelRequirements
    {
        // ── Geometry ──

        /// <summary>NaN or infinity in a position, normal, tangent or UV.</summary>
        public RequirementLevel finiteValues = RequirementLevel.Error;

        /// <summary>A face with a zero-length edge or no area (its height under a millionth of its longest edge).</summary>
        public RequirementLevel degenerateFaces = RequirementLevel.Error;

        /// <summary>A vertex in no face.</summary>
        public RequirementLevel unusedVertices = RequirementLevel.Warning;

        /// <summary>An edge (by position) shared by more than two faces.</summary>
        public RequirementLevel nonManifoldEdges = RequirementLevel.Warning;

        /// <summary>A face whose three vertex normals all point against its winding.</summary>
        public RequirementLevel flippedNormals = RequirementLevel.Warning;

        // ── Channels (render meshes; collision meshes need none) ──

        /// <summary>The level of a missing required channel below.</summary>
        public RequirementLevel channels = RequirementLevel.Error;
        public bool requireUv0 = true;
        public bool requireLightmapUv = true;
        public bool requireNormals = true;
        public bool requireTangents = true;

        /// <summary>A lightmap UV (UV1) outside [0,1]: it samples another chart's texels.</summary>
        public RequirementLevel lightmapInsideUnit = RequirementLevel.Error;

        internal static ModelRequirements Default => new ModelRequirements();
    }
}
