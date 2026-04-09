// ImportSettingsAutoRepair.cs — Detects and restores FBX import settings
// that were silently changed by an older version of the package.
// Runs once on domain reload (package install/update) via [InitializeOnLoad].

using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    [InitializeOnLoad]
    static class ImportSettingsAutoRepair
    {
        static ImportSettingsAutoRepair()
        {
            // Run after current import batch finishes to avoid interfering with ongoing imports.
            EditorApplication.delayCall += RepairDamagedImportSettings;
        }

        static void RepairDamagedImportSettings()
        {
            var guids = AssetDatabase.FindAssets("t:Model");
            int repaired = 0;

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null) continue;

                bool damaged = false;
                if (!importer.weldVertices) { importer.weldVertices = true; damaged = true; }
                if (!importer.optimizeMeshPolygons) { importer.optimizeMeshPolygons = true; damaged = true; }
                if (!importer.optimizeMeshVertices) { importer.optimizeMeshVertices = true; damaged = true; }

                if (damaged)
                {
                    importer.SaveAndReimport();
                    repaired++;
                }
            }

            if (repaired > 0)
                UvtLog.Info($"[Auto-Repair] Restored default import settings on {repaired} FBX file(s) damaged by an older package version.");
        }
    }
}
