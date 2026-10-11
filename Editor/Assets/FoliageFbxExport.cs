using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Separate source-document variants, writing only foliage shading through the shared FBX core.</summary>
    internal static class FoliageFbxExport
    {
        internal static bool Available
        {
            get
            {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                return true;
#else
                return false;
#endif
            }
        }

        internal static string[] Save(IReadOnlyList<FoliageTargetSnapshot> targets, string folder)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            var groups = GroupSources(targets);
            if (groups.Count == 0) throw new InvalidOperationException("No foliage results to export.");
            if (!MeshAssetOperations.EnsureOutputFolder(folder)) throw new InvalidOperationException("Choose a writable folder under Assets.");
            var paths = new List<string>();
            UnityEditor.EditorApplication.LockReloadAssemblies();
            try
            {
                foreach (var group in groups) SaveGroup(group.Key, group.Value, folder, paths);
                return paths.ToArray();
            }
            catch
            {
                RemoveFailedOutputs(paths);
                throw;
            }
            finally { UnityEditor.EditorApplication.UnlockReloadAssemblies(); }
#else
            throw new InvalidOperationException("Install Unity FBX Exporter (com.unity.formats.fbx) to save a separate FBX.");
#endif
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        static Dictionary<string, List<FoliageTargetSnapshot>> GroupSources(IReadOnlyList<FoliageTargetSnapshot> targets)
        {
            var groups = new Dictionary<string, List<FoliageTargetSnapshot>>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                string source = AssetDatabase.GetAssetPath(target.source);
                ValidateSource(target, source);
                if (!groups.TryGetValue(source, out var group))
                {
                    group = new List<FoliageTargetSnapshot>();
                    groups.Add(source, group);
                }
                group.Add(target);
            }
            return groups;
        }

        static void ValidateSource(FoliageTargetSnapshot target, string source)
        {
            if (!SidecarStore.IsFbxPath(source) || !(AssetImporter.GetAtPath(source) is ModelImporter importer))
                throw new InvalidOperationException($"'{target.source.name}' is not an imported FBX mesh. Preserving its original FBX data requires an FBX source.");
            if (importer.importNormals != ModelImporterNormals.Import)
                throw new InvalidOperationException($"'{source}' calculates normals on import. Separate FBX export requires source Normals = Import to preserve excluded shading; recapture after changing that setting.");
            if (importer.meshCompression != ModelImporterMeshCompression.Off)
                UvtLog.Warn($"[Foliage] '{source}' uses lossy Mesh Compression. The FBX document preserves excluded normals, UVs and colours exactly; its copied Unity importer can quantize them on reimport. Disable Mesh Compression on the new FBX for uncompressed shading.");
        }

        static void SaveGroup(string source, List<FoliageTargetSnapshot> targets, string folder, List<string> paths)
        {
            string candidate = Path.Combine(folder, Path.GetFileNameWithoutExtension(source) + "_FoliageNormals.fbx").Replace('\\', '/');
            string output = AssetDatabase.GenerateUniqueAssetPath(candidate);
            if (string.IsNullOrEmpty(output) || string.Equals(Path.GetFullPath(output), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a separate FBX destination.");
            var entries = new List<MeshEntry>();
            var selection = new FbxExport.ShadingSelection();
            foreach (var target in targets)
            {
                if (selection.submeshes.TryGetValue(target.source.name, out var first) && !SameSelection(first, target.selectedSubmeshes))
                    throw new InvalidOperationException($"'{target.source.name}' is instanced with different foliage selections; export those instances separately.");
                selection.submeshes[target.source.name] = (bool[])target.selectedSubmeshes.Clone();
                entries.Add(new MeshEntry { fbxMesh = target.source, originalMesh = target.original, transferredMesh = target.working, include = true });
            }
            paths.Add(output);
            if (!FbxExport.WriteChannels(source, entries, FbxExportIntent.Normals | FbxExportIntent.Tangents, output, null, null, selection))
                throw new InvalidOperationException($"FBX export failed for '{source}'. See the Mesh Lab log; no result was accepted.");
        }

        static void RemoveFailedOutputs(List<string> paths)
        {
            foreach (string path in paths.Where(path => !AssetDatabase.DeleteAsset(path) && File.Exists(path)))
            {
                UvtLog.Error("[Foliage] Failed export could not be removed: " + path);
            }
        }

        static bool SameSelection(bool[] a, bool[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
#endif
    }
}
