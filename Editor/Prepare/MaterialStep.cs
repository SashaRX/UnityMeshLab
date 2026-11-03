// MaterialStep.cs — the Materials step. Each material slot of a model that has no remap (a
// material Unity imports as the model's own) is remapped on the model's importer to the
// project's material of the same name (a .mat asset); of several, the one sharing the longest
// folder path with the model, the others listed. A slot with no such material keeps the
// model's own, with a warning. It changes the importer (IImporterStep), so it applies before
// the model's variant is opened; a prefab or a scene object is skipped, its materials are the
// model's. A slot already remapped is left as it is, so a second run changes nothing.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal sealed class MaterialStep : IImporterStep
    {
        internal const string StepName = "Materials";
        const string Check = "Material";

        public string Name => StepName;

        public string Purpose => "Each FBX material without a remap takes the project's material of the same name.";

        public string SkipReason(PrepareContext context)
        {
            if (context.Target.Kind != PrepareTargetKind.ModelAsset) return "materials are remapped on the model: prepare the FBX";
            return AssetImporter.GetAtPath(context.Target.AssetPath) is ModelImporter ? null : "the model has no model importer";
        }

        public void Plan(PrepareContext context, PrepareReport report) => Run(context, report, false);

        public void Apply(PrepareContext context, PrepareReport report) => Run(context, report, true);

        static void Run(PrepareContext context, PrepareReport report, bool apply)
        {
            string path = context.Target.AssetPath;
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            if (importer.materialImportMode == ModelImporterMaterialImportMode.None)
            {
                report.Add(PrepareLevel.Unchanged, $"'{path}'", Check, "the model imports no materials");
                return;
            }
            var remapped = new HashSet<string>(importer.GetExternalObjectMap().Keys
                .Where(k => k.type == typeof(Material)).Select(k => k.name), StringComparer.Ordinal);
            var slots = Slots(importer, path).Where(name => !remapped.Contains(name)).Distinct(StringComparer.Ordinal).ToList();
            if (slots.Count == 0)
            {
                report.Add(PrepareLevel.Unchanged, $"'{path}'", Check, remapped.Count > 0 ? "every material slot is remapped" : "the model has no material slots");
                return;
            }
            var remaps = new List<(string slot, Material material)>();
            foreach (string slot in slots)
            {
                var candidates = Nearest(ProjectMaterials(slot), path);
                if (candidates.Count == 0)
                {
                    report.Add(PrepareLevel.Warning, $"'{slot}'", Check, "no project material of that name: the slot keeps the model's own");
                    continue;
                }
                var material = AssetDatabase.LoadAssetAtPath<Material>(candidates[0]);
                report.Add(PrepareLevel.Changed, $"'{slot}'", Check, $"→ '{candidates[0]}'" + (candidates.Count > 1 ? $", the nearest of {candidates.Count}" : ""),
                    candidates.Count > 1 ? candidates : null);
                remaps.Add((slot, material));
            }
            if (!apply || remaps.Count == 0) return;
            foreach (var (slot, material) in remaps)
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), slot), material);
            importer.SaveAndReimport();
        }

        // The model's material slots by the names its importer remaps them by (the list its
        // inspector shows), else by the materials it imports as its own.
        static IEnumerable<string> Slots(ModelImporter importer, string path)
        {
            var list = new SerializedObject(importer).FindProperty("m_Materials");
            if (list == null || !list.isArray || list.arraySize == 0)
                return AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().Select(m => m.name);
            return Enumerable.Range(0, list.arraySize)
                .Select(i => list.GetArrayElementAtIndex(i).FindPropertyRelative("name")?.stringValue)
                .Where(name => !string.IsNullOrEmpty(name));
        }

        /// <summary>The paths of the project's material assets (.mat files) named exactly <paramref name="name"/>.</summary>
        static IEnumerable<string> ProjectMaterials(string name)
            => AssetDatabase.FindAssets($"t:Material {name}").Select(AssetDatabase.GUIDToAssetPath).Distinct(StringComparer.Ordinal)
                .Where(p => AssetDatabase.GetMainAssetTypeAtPath(p) == typeof(Material)
                    && string.Equals(System.IO.Path.GetFileNameWithoutExtension(p), name, StringComparison.Ordinal));

        /// <summary>The paths ordered by how many leading folders they share with <paramref name="near"/>, most first, then by path.</summary>
        internal static List<string> Nearest(IEnumerable<string> paths, string near)
            => paths.OrderByDescending(p => ComponentSurfaceBinding.SharedFolders(p, near)).ThenBy(p => p, StringComparer.Ordinal).ToList();
    }
}
