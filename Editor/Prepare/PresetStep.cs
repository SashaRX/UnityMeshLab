// PresetStep.cs — the Preset step, first in the chain. The model importer preset of the
// model's recipe (Project Settings ▸ Mesh Lab ▸ Prepare: Recipes, SM_ models by default) is
// applied to the model, all but the properties that hold the model's own links: its material
// remaps (m_ExternalObjects), the IDs its prefabs and variants find its meshes by
// (m_InternalIDToNameTable, m_UsedFileIDs), its avatar's human description (m_HumanDescription)
// and, unless asked, its LOD screen percentages. The plan lists each
// setting that differs, was → becomes, as the line's details; Apply applies only those and
// reimports the model (IImporterStep: before its variant is opened). A model that already
// matches the preset is left as it is.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.Presets;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    internal sealed class PresetStep : IImporterStep
    {
        internal const string StepName = "Preset";
        const string Check = "Importer";
        const string LodScreenPercentages = "m_LODScreenPercentages";

        /// <summary>
        /// The importer properties a preset never sets: the model's remaps, the IDs its prefabs
        /// reference meshes by, and its avatar's human description (its own bones and scale).
        /// </summary>
        internal static readonly string[] Kept = { "m_ExternalObjects", "m_InternalIDToNameTable", "m_UsedFileIDs", "m_HumanDescription" };

        readonly Preset preset;
        readonly string[] excluded;

        /// <param name="preset">The model importer preset; none skips the step.</param>
        /// <param name="applyLodScreenPercentages">Whether the preset's LOD screen percentages are applied too.</param>
        internal PresetStep(Preset preset, bool applyLodScreenPercentages = false)
        {
            this.preset = preset;
            excluded = applyLodScreenPercentages ? Kept : Kept.Append(LodScreenPercentages).ToArray();
        }

        public string Name => StepName;

        public string Purpose => "The model's importer takes the recipe's preset, except its remaps, mesh IDs, avatar description and LOD percentages.";

        public string SkipReason(PrepareContext context)
        {
            if (context.Target.Kind != PrepareTargetKind.ModelAsset) return "an importer preset is applied to the model: prepare the FBX";
            if (preset == null) return "the recipe sets no importer preset (Project Settings ▸ Mesh Lab ▸ Prepare: Recipes)";
            if (!(AssetImporter.GetAtPath(context.Target.AssetPath) is ModelImporter importer)) return "the model has no model importer";
            return preset.CanBeAppliedTo(importer) ? null : $"'{preset.name}' is not a model importer preset";
        }

        public void Plan(PrepareContext context, PrepareReport report) => Run(context, report, false);

        public void Apply(PrepareContext context, PrepareReport report) => Run(context, report, true);

        void Run(PrepareContext context, PrepareReport report, bool apply)
        {
            string path = context.Target.AssetPath;
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            // The preset's own exclusions (its Inspector's) are never applied either.
            var differences = Differences(preset, importer, excluded.Concat(preset.excludedProperties ?? Array.Empty<string>()));
            if (differences.Count == 0)
            {
                report.Add(PrepareLevel.Unchanged, $"'{path}'", Check, $"the importer already matches '{preset.name}'");
                return;
            }
            report.Add(PrepareLevel.Changed, $"'{path}'", Check, $"{MeshValidation.Plural(differences.Count, "setting", "settings")} from '{preset.name}'",
                differences.Select(d => $"{d.path}: {d.was} → {d.becomes}").ToList());
            if (!apply) return;
            if (!preset.ApplyTo(importer, differences.Select(d => d.path).ToArray()))
            {
                report.Add(PrepareLevel.Warning, $"'{path}'", Check, $"'{preset.name}' could not be applied; the importer is as it was");
                return;
            }
            importer.SaveAndReimport();
        }

        /// <summary>
        /// The preset's properties whose value on <paramref name="target"/> differs from the
        /// preset's, with both values; a property under one of <paramref name="excluded"/> is left out.
        /// </summary>
        internal static List<(string path, string was, string becomes)> Differences(Preset preset, Object target, IEnumerable<string> excluded)
        {
            var kept = excluded.ToList();
            var current = new SerializedObject(target);
            var differences = new List<(string path, string was, string becomes)>();
            foreach (var modification in preset.PropertyModifications)
            {
                string path = modification.propertyPath;
                if (kept.Any(k => path == k || path.StartsWith(k + ".", StringComparison.Ordinal))) continue;
                var property = current.FindProperty(path);
                if (property != null && Same(property, modification)) continue;
                string becomes = modification.objectReference != null ? modification.objectReference.name : modification.value;
                differences.Add((path, property != null ? Value(property) : "(none)", becomes));
            }
            return differences;
        }

        static bool Same(SerializedProperty property, PropertyModification modification)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    return property.objectReferenceValue == modification.objectReference;
                case SerializedPropertyType.Float:
                    return double.TryParse(modification.value, NumberStyles.Float, CultureInfo.InvariantCulture, out double f)
                        && Math.Abs(property.doubleValue - f) <= 1e-6 * Math.Max(1, Math.Abs(f));
                case SerializedPropertyType.Boolean:
                    return (property.boolValue ? "1" : "0") == modification.value;
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.LayerMask:
                    return long.TryParse(modification.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && property.longValue == n;
                default:
                    return Value(property) == modification.value;
            }
        }

        static string Value(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.ObjectReference: return property.objectReferenceValue != null ? property.objectReferenceValue.name : "(none)";
                case SerializedPropertyType.Float: return property.doubleValue.ToString("G7", CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return property.boolValue ? "1" : "0";
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.LayerMask:
                    return property.longValue.ToString(CultureInfo.InvariantCulture);
                default: return property.propertyType.ToString();
            }
        }
    }
}
