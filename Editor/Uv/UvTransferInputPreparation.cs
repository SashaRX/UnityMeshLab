using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Read from a fresh Unity import, not from buffers that were already quantized.
    // Only explicit UV stage actions call this; selection and package installation do not.
    internal static class UvTransferInputPreparation
    {
        internal static List<string> FindPaths(IEnumerable<MeshEntry> entries)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry == null || !entry.include || !entry.fbxMesh) continue;
                string path = AssetDatabase.GetAssetPath(entry.fbxMesh);
                if (AssetImporter.GetAtPath(path) is ModelImporter importer && NeedsPreparation(importer))
                    paths.Add(path);
            }
            return paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        static bool NeedsPreparation(ModelImporter importer) =>
            importer.meshCompression != ModelImporterMeshCompression.Off
            || importer.meshOptimizationFlags != 0 || importer.weldVertices;

        sealed class Binding
        {
            internal MeshEntry entry;
            internal string path, name;
            internal long id;
            internal Mesh mesh;
        }

        internal static void Reimport(List<MeshEntry> entries, List<string> paths)
        {
            var changed = new HashSet<string>(paths, StringComparer.Ordinal);
            var bindings = new List<Binding>();
            foreach (var entry in entries)
            {
                if (entry == null || !entry.fbxMesh) continue;
                string path = AssetDatabase.GetAssetPath(entry.fbxMesh);
                if (!changed.Contains(path)) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(entry.fbxMesh, out string _, out long id))
                    throw new InvalidOperationException($"Cannot identify UV input '{entry.fbxMesh.name}' in '{path}'.");
                bindings.Add(new Binding { entry = entry, path = path, name = entry.fbxMesh.name, id = id });
            }

            // Old sidecar topology/UV replay must not replace the fresh file input.
            // Keep the guard until the editing scope has drained its deferred imports.
            var bypassed = new List<string>();
            try
            {
                foreach (string path in paths)
                {
                    if (Uv2AssetPostprocessor.bypassPaths.Add(path)) bypassed.Add(path);
                }
                using (new AssetDatabase.AssetEditingScope())
                {
                    foreach (string path in paths)
                    {
                        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                        if (importer == null) throw new InvalidOperationException($"UV input importer disappeared: '{path}'.");
                        UvtLog.Info($"[UV Input] Reimport '{path}': compression {importer.meshCompression} → Off, "
                            + $"optimization {importer.meshOptimizationFlags} → None, weld {importer.weldVertices} → false.");
                        importer.meshCompression = ModelImporterMeshCompression.Off;
                        importer.meshOptimizationFlags = 0;
                        importer.weldVertices = false;
                        AssetDatabase.WriteImportSettingsIfDirty(path);
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    }
                }
            }
            finally
            {
                foreach (string path in bypassed) Uv2AssetPostprocessor.bypassPaths.Remove(path);
            }

            foreach (var binding in bindings)
            {
                // By local id, else by a name unique in the file: some importers regenerate
                // sub-asset ids after changing welding.
                binding.mesh = SceneEditRegistry.FindImported(binding.path, binding.id, binding.name);
                if (!binding.mesh)
                    throw new InvalidOperationException($"Cannot uniquely rebind UV input '{binding.name}' in '{binding.path}'. Run Setup again.");
                if (!(AssetImporter.GetAtPath(binding.path) is ModelImporter importer) || NeedsPreparation(importer))
                    throw new InvalidOperationException($"UV input '{binding.path}' is still compressed or optimized after reimport.");
            }
            // Resolve every binding before publishing any of them. Excluded entries in
            // the same FBX must also follow the replacement sub-assets.
            foreach (var binding in bindings) Rebind(binding.entry, binding.mesh);
        }

        /// <summary>
        /// Gives <paramref name="entry"/> the reimported <paramref name="imported"/> as its file
        /// reference and, while the renderer shows the imported mesh, as its working mesh and the
        /// renderer's. A scene edit (<see cref="MeshEntry.sceneEditMesh"/>) stays the renderer's
        /// mesh and the entry's baseline: the pipeline keeps working from the edited geometry, and
        /// the edit now stands for the reimported mesh.
        /// </summary>
        internal static void Rebind(MeshEntry entry, Mesh imported)
        {
            entry.fbxMesh = imported;
            if (entry.sceneEditMesh != null)
            {
                entry.originalMesh = entry.sceneEditMesh;
                SceneEditRegistry.Register(entry.meshFilter, imported, entry.sceneEditMesh);
                return;
            }
            entry.originalMesh = imported;
            if (entry.meshFilter && entry.meshFilter.sharedMesh != imported)
            {
                Undo.RecordObject(entry.meshFilter, "Prepare UV transfer input");
                entry.meshFilter.sharedMesh = imported;
                if (PrefabUtility.IsPartOfPrefabInstance(entry.meshFilter))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(entry.meshFilter);
            }
        }
    }
}
