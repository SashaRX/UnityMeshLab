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

            var imported = paths.ToDictionary(path => path,
                path => AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToArray(), StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                var meshes = imported[binding.path];
                binding.mesh = meshes.FirstOrDefault(mesh =>
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string _, out long id) && id == binding.id);
                if (!binding.mesh)
                {
                    // Some importers regenerate sub-asset IDs after changing welding.
                    // A name fallback is allowed only when it is unique in this file.
                    var named = meshes.Where(mesh => mesh.name == binding.name).ToArray();
                    if (named.Length == 1) binding.mesh = named[0];
                }
                if (!binding.mesh)
                    throw new InvalidOperationException($"Cannot uniquely rebind UV input '{binding.name}' in '{binding.path}'. Run Setup again.");
                if (!(AssetImporter.GetAtPath(binding.path) is ModelImporter importer) || NeedsPreparation(importer))
                    throw new InvalidOperationException($"UV input '{binding.path}' is still compressed or optimized after reimport.");
            }
            // Resolve every binding before publishing any of them. Excluded entries in
            // the same FBX must also follow the replacement sub-assets.
            foreach (var binding in bindings)
            {
                var entry = binding.entry;
                entry.fbxMesh = entry.originalMesh = binding.mesh;
                if (entry.meshFilter) entry.meshFilter.sharedMesh = binding.mesh;
            }
        }
    }
}
