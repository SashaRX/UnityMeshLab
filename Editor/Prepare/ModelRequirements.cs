// ModelRequirements.cs — what a model must satisfy, and how hard: each check is Off, a
// Warning (shown, Apply runs) or an Error (blocks Apply). Serializable so a recipe can
// carry its own set; Default is the static-prop rule set.

using System;
using System.Collections.Generic;

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

        /// <summary>A face with an area of at most <see cref="maxFaceArea"/> square metres (one with no area is a degenerate face).</summary>
        public RequirementLevel smallFaces = RequirementLevel.Warning;
        public float maxFaceArea = 1e-8f;

        /// <summary>An edge at most <see cref="maxEdgeLength"/> metres long.</summary>
        public RequirementLevel shortEdges = RequirementLevel.Warning;
        public float maxEdgeLength = 1e-4f;

        /// <summary>A polygon of the FBX (four corners or more) that is concave: it triangulates unevenly.</summary>
        public RequirementLevel concaveFaces = RequirementLevel.Warning;

        // ── Channels (render meshes; collision meshes need none) ──

        /// <summary>The level of a missing required channel below.</summary>
        public RequirementLevel channels = RequirementLevel.Error;
        public bool requireUv0 = true;
        public bool requireLightmapUv = true;
        public bool requireNormals = true;
        public bool requireTangents = true;

        /// <summary>A lightmap UV (UV1) outside [0,1]: it samples another chart's texels.</summary>
        public RequirementLevel lightmapInsideUnit = RequirementLevel.Error;

        // ── File: import settings, units, scale, smoothing ──

        /// <summary>The importer's Scale Factor at 1 and Convert Units on.</summary>
        public RequirementLevel importScale = RequirementLevel.Error;

        /// <summary>The file in metres (the importer's file scale is 1).</summary>
        public RequirementLevel fileUnits = RequirementLevel.Error;

        /// <summary>Every node at scale 1 (an asset's root too); a negative scale mirrors.</summary>
        public RequirementLevel nodeScale = RequirementLevel.Error;

        /// <summary>The model's largest extent between <see cref="minSize"/> and <see cref="maxSize"/> metres: outside, the units are likely wrong.</summary>
        public RequirementLevel modelSize = RequirementLevel.Warning;
        public float minSize = 0.01f;
        public float maxSize = 200f;

        /// <summary>Each render mesh of an FBX carries smoothing groups.</summary>
        public RequirementLevel smoothingGroups = RequirementLevel.Error;

        // ── LODs, limits, names ──

        /// <summary>A render mesh with more than <see cref="maxVertices"/> vertices (65535: 16-bit indices).</summary>
        public RequirementLevel vertexLimit = RequirementLevel.Error;
        public int maxVertices = 65535;

        /// <summary>A part's LOD with at least as many triangles as the LOD before it.</summary>
        public RequirementLevel lodReduction = RequirementLevel.Error;

        /// <summary>A part's LOD keeping more than <see cref="maxLodRatio"/> of the triangles of the LOD before it.</summary>
        public RequirementLevel lodRatio = RequirementLevel.Warning;
        public float maxLodRatio = .8f;

        /// <summary>A part missing a LOD level that it or another part has, or a LOD past the eight a LODGroup holds.</summary>
        public RequirementLevel lodChain = RequirementLevel.Warning;

        /// <summary>Sibling nodes with one name: steps that pair nodes by name cannot tell them apart.</summary>
        public RequirementLevel duplicateNames = RequirementLevel.Warning;

        /// <summary>
        /// The checks that are on, a line per group ("Geometry: finite values, degenerate faces
        /// (error); unused vertices (warning)"): what a model that passes was checked for.
        /// Without a model's file (<paramref name="file"/> false: a prefab or scene object not
        /// made from one) the checks that read the file are left out; smoothing groups are read
        /// from an FBX only (<paramref name="fbx"/>), and its concave polygons only where the
        /// FBX SDK is there to read them (<paramref name="polygons"/>).
        /// </summary>
        internal IReadOnlyList<string> Checked(bool file = true, bool fbx = true, bool polygons = true)
        {
            var off = RequirementLevel.Off;
            var channelNames = new List<string>();
            if (requireUv0) channelNames.Add("UV0");
            if (requireLightmapUv) channelNames.Add("UV1 (lightmap)");
            if (requireNormals) channelNames.Add("normals");
            if (requireTangents) channelNames.Add("tangents");
            var lines = new List<string>();
            Group(lines, "Geometry", ("finite values", finiteValues), ("degenerate faces", degenerateFaces), ("unused vertices", unusedVertices),
                ("non-manifold edges", nonManifoldEdges), ("flipped normals", flippedNormals), ("tiny faces", smallFaces),
                ("short edges", shortEdges), ("concave polygons", file && fbx && polygons ? concaveFaces : off));
            Group(lines, "Channels", (channelNames.Count > 0 ? "has " + string.Join(", ", channelNames) : null, channels),
                ("lightmap UV inside [0,1]", lightmapInsideUnit));
            Group(lines, file ? "File" : "Scale", ("import scale", file ? importScale : off), ("units", file ? fileUnits : off),
                ("node scale", nodeScale), ("size", modelSize), ("smoothing groups", file && fbx ? smoothingGroups : off));
            Group(lines, "LODs and names", ("vertex limit", vertexLimit), ("LOD reduction", lodReduction), ("LOD ratio", lodRatio),
                ("LOD chain", lodChain), ("duplicate names", duplicateNames));
            return lines;
        }

        // "Geometry: a, b (error); c (warning)"; nothing when every check of the group is off.
        static void Group(List<string> lines, string name, params (string check, RequirementLevel level)[] checks)
        {
            var parts = new List<string>();
            foreach (var level in new[] { RequirementLevel.Error, RequirementLevel.Warning })
            {
                var names = new List<string>();
                foreach (var (check, at) in checks)
                    if (at == level && check != null) names.Add(check);
                if (names.Count > 0) parts.Add($"{string.Join(", ", names)} ({(level == RequirementLevel.Error ? "error" : "warning")})");
            }
            if (parts.Count > 0) lines.Add($"{name}: {string.Join("; ", parts)}");
        }

        internal static ModelRequirements Default => new ModelRequirements();
    }
}
