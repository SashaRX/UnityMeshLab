using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Source-document shading exports through the shared FBX core.</summary>
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

        internal static string[] Overwrite(IReadOnlyList<FoliageTargetSnapshot> targets,
            Func<IReadOnlyList<string>, bool> confirm, Action<string, ImportAssetOptions> importAsset = null)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            var groups = GroupSources(targets);
            if (groups.Count == 0) throw new InvalidOperationException("No foliage results to export.");
            if (confirm == null || !confirm(groups.Keys.ToArray())) return Array.Empty<string>();
            // Validate every group before any source file is changed or imported.
            var prepared = groups.Select(group => {
                PrepareGroup(group.Key, group.Value, importAsset, out var entries, out var selection);
                return (source: group.Key, targets: group.Value, entries, selection);
            }).ToArray();
            foreach (var group in prepared) {
                group.selection.allowSourceOverwrite = true;
                if (!FbxChannelWrite.Write(group.source, group.entries, FbxExportIntent.Normals | FbxExportIntent.Tangents,
                    null, null, shadingSelection: group.selection, validationOnly: true))
                    throw new InvalidOperationException($"Foliage FBX preflight failed for '{group.source}'. No source was overwritten.");
            }
            var paths = new List<string>();
            EditorApplication.LockReloadAssemblies();
            try
            {
                foreach (var group in prepared)
                {
                    var selection = group.selection;
                    selection.allowSourceOverwrite = true;
                    var relinks = group.targets.Select(t => (t.filter, name: t.source.name)).ToArray();
                    var importer = selection.importAsset;
                    bool importAttempted = false;
                    selection.importAsset = (path, options) => { importAttempted = true; importer(path, options); };
                    bool written;
                    try { written = FbxExport.WriteChannels(group.source, group.entries, FbxExportIntent.Normals | FbxExportIntent.Tangents, null, null, null, selection); }
                    catch (FbxExport.PostCommitException ex) { UvtLog.Error("[FBX Export] " + ex.Message); written = false; }
                    finally { if (importAttempted) RelinkSourceMeshes(group.source, relinks); }
                    if (!written) throw new InvalidOperationException($"Foliage FBX overwrite did not complete for '{group.source}'. If import failed after writing, fix the import and reimport this FBX; see the Mesh Lab log.");
                    paths.Add(group.source);
                }
                return paths.ToArray();
            }
            finally { EditorApplication.UnlockReloadAssemblies(); }
#else
            throw new InvalidOperationException("Install Unity FBX Exporter (com.unity.formats.fbx) to overwrite FBX.");
#endif
        }

        internal static string[] Save(IReadOnlyList<FoliageTargetSnapshot> targets, string folder, Action<string, ImportAssetOptions> importAsset = null)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            var groups = GroupSources(targets);
            if (groups.Count == 0) throw new InvalidOperationException("No foliage results to export.");
            if (!MeshAssetOperations.EnsureOutputFolder(folder)) throw new InvalidOperationException("Choose a writable folder under Assets.");
            var paths = new List<string>();
            var ownership = new List<FbxSourceDocument.MediaOwnership>();
            UnityEditor.EditorApplication.LockReloadAssemblies();
            try
            {
                foreach (var group in groups) SaveGroup(group.Key, group.Value, folder, paths, ownership, importAsset);
                return paths.ToArray();
            }
            catch
            {
                RemoveFailedOutputs(paths, ownership);
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
                throw new InvalidOperationException($"'{source}' calculates normals on import. Foliage FBX export requires source Normals = Import to preserve excluded shading; recapture after changing that setting.");
            if (importer.importTangents == ModelImporterTangents.None && target.working.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent))
                throw new InvalidOperationException($"'{source}' uses Tangents = None and would discard the result's tangent frame. Disable Recalculate tangents and rebuild, or enable source tangent import and recapture before exporting.");
            if (importer.meshCompression != ModelImporterMeshCompression.Off)
                UvtLog.Warn($"[Foliage] '{source}' uses lossy Mesh Compression. The FBX document preserves excluded normals, UVs and colours exactly; its Unity importer can quantize them on reimport. Disable Mesh Compression on the output FBX for uncompressed shading.");
        }

        static void SaveGroup(string source, List<FoliageTargetSnapshot> targets, string folder, List<string> paths, List<FbxSourceDocument.MediaOwnership> ownership, Action<string, ImportAssetOptions> importAsset)
        {
            string candidate = Path.Combine(folder, Path.GetFileNameWithoutExtension(source) + "_FoliageNormals.fbx").Replace('\\', '/');
            string output = AssetDatabase.GenerateUniqueAssetPath(candidate);
            if (string.IsNullOrEmpty(output) || string.Equals(Path.GetFullPath(output), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a separate FBX destination.");
            PrepareGroup(source, targets, importAsset, out var entries, out var selection);
            paths.Add(output);
            ownership.Add(selection.mediaOwnership);
            bool written;
            try { written = FbxExport.WriteChannels(source, entries, FbxExportIntent.Normals | FbxExportIntent.Tangents, output, null, null, selection); }
            catch (FbxExport.PostCommitException ex)
            {
                // The output is committed and owns its media: the failed-save cleanup leaves it
                // alone and removes only the groups before it.
                paths.RemoveAt(paths.Count - 1);
                ownership.RemoveAt(ownership.Count - 1);
                throw new InvalidOperationException($"FBX export for '{source}' wrote '{output}', but {ex.Message} Fix the cause and reimport it; earlier outputs of this save were removed.", ex);
            }
            if (!written)
                throw new InvalidOperationException($"FBX export failed for '{source}'. See the Mesh Lab log; no result was accepted.");
        }

        static void PrepareGroup(string source, List<FoliageTargetSnapshot> targets, Action<string, ImportAssetOptions> importAsset,
            out List<MeshEntry> entries, out FbxExport.ShadingSelection selection)
        {
            entries = new List<MeshEntry>();
            selection = new FbxExport.ShadingSelection();
            if (importAsset != null) selection.importAsset = importAsset;
            foreach (var target in targets)
            {
                if (selection.submeshes.TryGetValue(target.source.name, out var first) && !SameSelection(first, target.selectedSubmeshes))
                    throw new InvalidOperationException($"'{target.source.name}' is instanced with different foliage selections; export those instances separately.");
                selection.submeshes[target.source.name] = (bool[])target.selectedSubmeshes.Clone();
                CaptureSourceNode(source, target, selection);
                entries.Add(new MeshEntry { fbxMesh = target.source, originalMesh = target.original, transferredMesh = target.working, include = true });
            }
        }

        static void RelinkSourceMeshes(string source, (UnityEngine.MeshFilter filter, string name)[] relinks)
        {
            var meshes = AssetDatabase.LoadAllAssetsAtPath(source).OfType<UnityEngine.Mesh>().ToArray();
            foreach (var (filter, name) in relinks)
            {
                if (!filter || EditorUtility.IsPersistent(filter)) continue;
                var fresh = meshes.SingleOrDefault(mesh => mesh.name == name);
                if (!fresh || filter.sharedMesh == fresh) continue;
                Undo.RecordObject(filter, "Relink Foliage FBX");
                filter.sharedMesh = fresh;
                PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
            }
        }

        static void CaptureSourceNode(string source, FoliageTargetSnapshot target, FbxExport.ShadingSelection selection)
        {
            var node = PrefabUtility.GetCorrespondingObjectFromOriginalSource(target.renderer);
            if (node == null || !string.Equals(AssetDatabase.GetAssetPath(node), source, StringComparison.OrdinalIgnoreCase)) return;
            if (!selection.sourceNodes.TryGetValue(target.source.name, out var nodes))
            {
                nodes = new HashSet<string>(StringComparer.Ordinal);
                selection.sourceNodes.Add(target.source.name, nodes);
            }
            nodes.Add(node.name);
        }

        static void RemoveFailedOutputs(List<string> paths, List<FbxSourceDocument.MediaOwnership> ownership)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                try
                {
                    FbxExternalMedia.DeleteCreated(Path.GetFullPath(path));
                }
                catch (Exception error)
                {
                    UvtLog.Error("[Foliage] Failed export could not be removed: " + path + ". " + error.Message);
                    continue; // Its committed FBX still owns its media.
                }
                foreach (var error in ownership[i].Rollback())
                    UvtLog.Error("[Foliage] Failed export media could not be removed: " + error.Message);
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
