// ModelFileChecks.cs — the checks of a model as a whole, against ModelRequirements: the
// importer's scale settings and the file's units, every node at scale 1 (none mirrored),
// the model's size in metres, and smoothing groups in the FBX (read from the file's own
// bytes by FbxUvSetNames, no FBX SDK). Collision meshes need no smoothing and do not
// count towards the size.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class ModelFileChecks
    {
        /// <summary>How far a scale may lie from 1 (FBX stores 0.99999994 for 1).</summary>
        internal const float ScaleTolerance = 1e-4f;

        /// <summary>The most nodes or meshes a line names; the rest are counted.</summary>
        const int Named = 5;

        /// <summary>Scale Factor and Convert Units, then the file's units.</summary>
        internal static void CheckImporter(ModelImporter importer, string subject, ModelRequirements requirements, PrepareReport report)
        {
            foreach (var finding in ImportScaleFindings(importer.globalScale, importer.useFileScale))
                report.Add(requirements.importScale, subject, "Import scale", finding);
            if (!IsOne(importer.fileScale))
                report.Add(requirements.fileUnits, subject, "Units",
                    $"the file is in {UnitName(importer.fileScale)} (file scale {importer.fileScale.ToString("G6", CultureInfo.InvariantCulture)}), not metres; export it in metres");
        }

        /// <summary>The import scale settings that differ from Scale Factor 1 with Convert Units on.</summary>
        internal static List<string> ImportScaleFindings(float globalScale, bool useFileScale)
        {
            var findings = new List<string>();
            if (!IsOne(globalScale))
                findings.Add($"Scale Factor is {globalScale.ToString("G6", CultureInfo.InvariantCulture)}, not 1");
            if (!useFileScale) findings.Add("Convert Units is off");
            return findings;
        }

        /// <summary>The unit a file scale stands for, in words.</summary>
        internal static string UnitName(float fileScale)
        {
            if (Near(fileScale, .01f)) return "centimetres";
            if (Near(fileScale, .001f)) return "millimetres";
            if (Near(fileScale, .0254f)) return "inches";
            if (Near(fileScale, .3048f)) return "feet";
            if (Near(fileScale, 1000f)) return "kilometres";
            return $"units of {fileScale.ToString("G6", CultureInfo.InvariantCulture)} m";
        }

        /// <summary>
        /// Nodes not at scale 1 or mirrored. A scene object's own root is placed, not modelled:
        /// <paramref name="includeRoot"/> is false for it.
        /// </summary>
        internal static void CheckNodeScales(GameObject root, bool includeRoot, ModelRequirements requirements, PrepareReport report)
        {
            if (requirements.nodeScale == RequirementLevel.Off) return;
            var found = new List<string>();
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                if (node == root.transform && !includeRoot) continue;
                string finding = ScaleFinding(node.localScale);
                if (finding != null) found.Add($"'{node.name}' {finding}");
            }
            if (found.Count > 0)
                report.Add(requirements.nodeScale, $"'{root.name}'", "Node scale",
                    $"{MeshValidation.Plural(found.Count, "node is", "nodes are")} not at scale 1: {List(found)}; apply the scale in the DCC");
        }

        /// <summary>"mirrored (x, y, z)" for a negative axis, "at (x, y, z)" for another scale, null at 1.</summary>
        internal static string ScaleFinding(Vector3 scale)
        {
            string value = $"({F(scale.x)}, {F(scale.y)}, {F(scale.z)})";
            if (scale.x < 0f || scale.y < 0f || scale.z < 0f) return $"mirrored {value}";
            return IsOne(scale.x) && IsOne(scale.y) && IsOne(scale.z) ? null : $"at {value}";
        }

        /// <summary>The largest extent of the render meshes, in the root's space, against the size range.</summary>
        internal static void CheckSize(GameObject root, ModelRequirements requirements, PrepareReport report)
        {
            if (requirements.modelSize == RequirementLevel.Off) return;
            float? size = RenderSize(root);
            if (size == null) return;
            if (size < requirements.minSize || size > requirements.maxSize)
                report.Add(requirements.modelSize, $"'{root.name}'", "Size",
                    $"the model is {F(size.Value)} m at its largest, outside {F(requirements.minSize)}–{F(requirements.maxSize)} m; check the units");
        }

        /// <summary>
        /// The largest extent of the bounds of every render (not collision) mesh, static or
        /// skinned (in its bind pose), in the root's space; null without one.
        /// </summary>
        internal static float? RenderSize(GameObject root)
        {
            Bounds? all = null;
            var toRoot = root.transform.worldToLocalMatrix;
            var meshes = root.GetComponentsInChildren<MeshFilter>(true).Select(f => (node: f.transform, mesh: f.sharedMesh))
                .Concat(root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => (node: r.transform, mesh: r.sharedMesh)));
            foreach (var (node, mesh) in meshes)
            {
                if (mesh == null || MeshNaming.IsCollision(node.name) || MeshNaming.IsCollision(mesh.name)) continue;
                var bounds = MeshGeometry.TransformBounds(mesh.bounds, toRoot * node.localToWorldMatrix);
                if (all is Bounds b) { b.Encapsulate(bounds); all = b; } else all = bounds;
            }
            if (all == null) return null;
            var extent = all.Value.size;
            return Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z));
        }

        /// <summary>
        /// The FBX meshes without smoothing groups, from the file at <paramref name="assetPath"/>.
        /// <paramref name="collisionOnly"/> names the meshes the model uses for collision alone
        /// (a <c>_COL</c> node's mesh of any name), which need none.
        /// </summary>
        internal static void CheckSmoothing(string assetPath, ModelRequirements requirements, PrepareReport report, ICollection<string> collisionOnly = null)
        {
            if (requirements.smoothingGroups == RequirementLevel.Off
                || !assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return;
            Dictionary<string, bool> smoothing;
            try { smoothing = FbxUvSetNames.ReadLayers(AssetFilePath.Physical(assetPath)).smoothing; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is IndexOutOfRangeException)
            {
                report.Add(PrepareLevel.Warning, assetPath, "Smoothing groups", $"the file could not be read: {error.Message}");
                return;
            }
            var missing = WithoutSmoothing(smoothing, collisionOnly);
            if (missing.Count > 0)
                report.Add(requirements.smoothingGroups, assetPath, "Smoothing groups",
                    $"{MeshValidation.Plural(missing.Count, "mesh has", "meshes have")} no smoothing groups in the FBX: {List(missing.Select(n => $"'{n}'").ToList())}; export with smoothing groups");
        }

        /// <summary>
        /// The render meshes whose geometry has no smoothing element, by name: neither named as
        /// collision nor in <paramref name="collisionOnly"/>.
        /// </summary>
        internal static List<string> WithoutSmoothing(Dictionary<string, bool> smoothing, ICollection<string> collisionOnly = null) => smoothing
            .Where(kv => !kv.Value && !MeshNaming.IsCollision(kv.Key) && !(collisionOnly?.Contains(kv.Key) ?? false))
            .Select(kv => kv.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        static string List(List<string> items) => items.Count <= Named
            ? string.Join(", ", items)
            : $"{string.Join(", ", items.Take(Named))} and {items.Count - Named} more";

        static bool IsOne(float value) => Math.Abs(value - 1f) <= ScaleTolerance;
        static bool Near(float value, float target) => Math.Abs(value - target) <= target * 1e-3f;
        static string F(float value) => value.ToString("G4", CultureInfo.InvariantCulture);
    }
}
