// MeshAssetOperations.cs — context-owned saving/export workflows shared by all tools.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    public sealed class MeshAssetOperations
    {
        const string CancelButton = "Cancel";
        const string AssetPathPrefix = "Assets/";
        readonly UvToolContext ctx;
        internal Action BeforeWrite, AfterWrite, WriteFinished;
        int previewWriteDepth;
        internal Func<string, FbxExportIntent, bool, string> ToolExportPathChooser;
        public MeshAssetOperations(UvToolContext context) => ctx = context ?? throw new ArgumentNullException(nameof(context));

        internal IDisposable PreservePreviewDuringWrite() => new PreviewWriteScope(this);

        sealed class PreviewWriteScope : IDisposable
        {
            MeshAssetOperations operations;
            public PreviewWriteScope(MeshAssetOperations operations) { this.operations = operations; ++operations.previewWriteDepth; }
            public void Dispose()
            {
                var owner = operations; operations = null;
                if (owner != null && --owner.previewWriteDepth == 0) owner.WriteFinished?.Invoke();
            }
        }

        void RestoreAllPreviews() => BeforeWrite?.Invoke();
        void SwitchToPostApplyView(Dictionary<string, List<MeshUv2Entry>> applied = null)
        {
            if (applied != null)
                foreach (var entry in ctx.MeshEntries)
                {
                    if (entry?.sceneEditMesh == null) continue;
                    // The import that applied the channels may have recreated the imported mesh.
                    var imported = ImportOf(entry);
                    if (imported == null) continue;
                    string path = AssetDatabase.GetAssetPath(imported);
                    if (string.IsNullOrEmpty(path) || !applied.TryGetValue(path, out var saved)) continue;
                    var payload = saved.FirstOrDefault(item => item != null && item.meshName == imported.name);
                    if (payload == null) continue;
                    var intent = UvIntent(payload.targetUvChannel)
                        | (payload.auxiliaryUv != null ? UvIntent(payload.auxiliaryTargetUvChannel) : FbxExportIntent.None)
                        | (payload.optimizedColors != null ? FbxExportIntent.VertexColors : FbxExportIntent.None);
                    SyncSceneEditChannels(entry, GetResultMesh(entry), intent, "Apply UV2");
                }
            if (ctx.LodGroup != null) ctx.Refresh(ctx.LodGroup);
            else if (ctx.StandaloneMesh)
            {
                var renderer = ctx.MeshEntries.FirstOrDefault(e => e?.renderer is MeshRenderer)?.renderer as MeshRenderer;
                if (renderer != null) ctx.RefreshStandalone(renderer);
            }
            foreach (var entry in ctx.MeshEntries)
            {
                if (entry == null) continue;
                string path = entry.fbxMesh ? AssetDatabase.GetAssetPath(entry.fbxMesh) : null;
                if (applied == null || string.IsNullOrEmpty(path) || !applied.TryGetValue(path, out var saved)) continue;
                var payload = saved.FirstOrDefault(item => item != null && item.meshName == entry.fbxMesh.name);
                if (payload == null) continue;
                // Sidecar-off imports use a one-shot payload. Restore this result's
                // history after refresh, rather than loading an older disk sidecar.
                entry.reverseTransferJson = null;
                entry.repackedAtlasWidth = entry.repackedAtlasHeight = 0;
                SidecarStore.RestoreReverseProvenance(entry, payload);
            }
            AfterWrite?.Invoke();
        }

        void ApplyUv2ToFbx()
        {
            if (ctx?.MeshEntries == null || ctx.MeshEntries.Count == 0)
            {
                UvtLog.Warn("[Apply] No meshes loaded.");
                return;
            }
            RestoreAllPreviews();
            UvtLog.Info("[Apply] Applying UV2 to FBX...");

            ReimportRawSourceMeshes();
            var fbxGroups = BuildApplySidecarGroups();
            if (fbxGroups.Count == 0) { UvtLog.Warn("[Apply] No meshes with UV2 data."); return; }

            StoreAndImportSidecars(fbxGroups);
            UvtLog.Info($"[Apply] Done — {fbxGroups.Count} FBX(es) updated.");
            SwitchToPostApplyView(fbxGroups);
        }

        // Get the raw vertex order before building sidecars.
        void ReimportRawSourceMeshes()
        {
            // Pre-import pass: reimport FBXs with postprocessor bypassed to get raw vertex order
            var fbxPathSet = ApplySourceFbxPaths();
            if (fbxPathSet.Count > 0)
            {
                foreach (string p in fbxPathSet)
                {
                    var imp = AssetImporter.GetAtPath(p) as ModelImporter;
                    if (imp == null) continue;
                    if (imp.generateSecondaryUV) imp.generateSecondaryUV = false;
                    FbxExport.ReimportWithoutSidecars(p, imp.SaveAndReimport);
                }
                foreach (var e in ctx.MeshEntries)
                {
                    if (e.meshFilter == null || e.meshFilter.sharedMesh == null) continue;
                    // A scene edit on the renderer keeps the imported mesh, found again after the reimport, as the file reference.
                    var shown = e.meshFilter.sharedMesh;
                    e.fbxMesh = SceneEditRegistry.TryGetImported(e.meshFilter, shown, out var imported) ? imported : shown;
                }
            }
        }

        HashSet<string> ApplySourceFbxPaths()
        {
            var fbxPathSet = new HashSet<string>();
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include) continue;
                Mesh m = e.fbxMesh ?? e.originalMesh;
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(p)) fbxPathSet.Add(p);
            }
            return fbxPathSet;
        }

        Dictionary<string, List<MeshUv2Entry>> BuildApplySidecarGroups()
        {
            // Build sidecar entries
            var fbxGroups = new Dictionary<string, List<MeshUv2Entry>>();
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include) continue;
                Mesh resultMesh = GetResultMesh(e);
                if (resultMesh == null) continue;

                Mesh pathMesh = e.fbxMesh ?? e.originalMesh;
                string fbxPath = AssetDatabase.GetAssetPath(pathMesh);
                if (string.IsNullOrEmpty(fbxPath)) continue;

                if (TryBuildSidecarEntry(e, resultMesh, out var sidecarEntry))
                {
                    if (!fbxGroups.ContainsKey(fbxPath))
                        fbxGroups[fbxPath] = new List<MeshUv2Entry>();
                    fbxGroups[fbxPath].Add(sidecarEntry);
                }
            }

            return fbxGroups;
        }

        static void StoreAndImportSidecars(Dictionary<string, List<MeshUv2Entry>> fbxGroups)
        {
            // Store the entries (sidecar on disk, or a one-shot replay) and reimport so
            // the postprocessor applies the UV2.
            bool persistentSidecarMode = PostprocessorDefineManager.IsEnabled();
            foreach (var kv in fbxGroups)
            {
                SidecarStore.StoreForImport(kv.Key, kv.Value, persistentSidecarMode);
                bool reimported = Uv2AssetPostprocessor.PrepareImportSettings(kv.Key);
                if (!reimported)
                    AssetDatabase.ImportAsset(kv.Key, ImportAssetOptions.ForceUpdate);
            }

        }

        public Mesh GetResultMesh(MeshEntry e)
        {
            if (!string.IsNullOrEmpty(e.reverseTransferJson))
            {
                // A reverse transfer's result is its repacked or transferred mesh. Restored from the
                // sidecar after a refresh, the file has the result and an import has nothing to
                // write; a scene edit (checked against the record when it was restored) is still
                // the renderer's unsaved mesh.
                var reverse = e.repackedMesh ?? e.transferredMesh;
                if (reverse != null || e.sceneEditMesh == null) return reverse;
            }
            // Source LOD: prefer repacked mesh
            if (e.lodIndex == ctx.SourceLodIndex && e.repackedMesh != null)
                return e.repackedMesh;
            // Target LODs: prefer transferred mesh
            if (e.transferredMesh != null)
                return e.transferredMesh;
            // Welded/modified meshes
            if (e.wasWelded || e.wasEdgeWelded || e.wasSymmetrySplit)
                return e.originalMesh;
            // Generated LODs or any mesh that differs from the original FBX
            if (e.originalMesh != null && e.originalMesh != e.fbxMesh)
                return e.originalMesh;
            // Generated LODs: originalMesh == fbxMesh but it's not from a .fbx file
            if (e.originalMesh != null)
            {
                string path = AssetDatabase.GetAssetPath(e.originalMesh);
                // Mesh not from .fbx = generated in memory or .asset → include it
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    return e.originalMesh;
            }
            // Fallback: return original mesh as-is for clean re-export
            // (allows "Overwrite Source FBX" to fix FBX metadata like
            // material names and collider attributes without UV2 pipeline)
            return e.originalMesh;
        }

        public void ExportFbxPublic(bool overwriteSource) => ExportFbxPublic(overwriteSource, FbxExportIntent.All);
        public void ExportFbxPublic(bool overwriteSource, FbxExportIntent intent)
        { using var previewWrite = PreservePreviewDuringWrite(); if (!UvProgress.IsActive) ExportFbx(overwriteSource, intent); }
        /// <summary>Tool-owned results, independent of the hub's last preview selection.
        /// All groups share the standard path picker, channel writer and sidecar sync.
        /// The owner retains its meshes; unrelated context working copies are never released.</summary>
        public IReadOnlyList<string> ExportToolEntries(IEnumerable<MeshEntry> results, FbxExportIntent intent, bool overwriteSource,
            Func<string, FbxExportIntent, bool, string> choosePath = null)
            => ExportToolEntriesCore(results, intent, overwriteSource, choosePath, null);

        internal IReadOnlyList<string> ExportCollisionEntries(IEnumerable<MeshEntry> sources,
            IReadOnlyDictionary<string, Uv2DataAsset> prepared, bool overwriteSource)
            => ExportToolEntriesCore(sources, FbxExportIntent.Collision, overwriteSource, null, prepared);

        IReadOnlyList<string> ExportToolEntriesCore(IEnumerable<MeshEntry> results, FbxExportIntent intent, bool overwriteSource,
            Func<string, FbxExportIntent, bool, string> choosePath, IReadOnlyDictionary<string, Uv2DataAsset> collisions)
        {
            using var previewWrite = PreservePreviewDuringWrite();
            var written = new List<string>();
            choosePath ??= ToolExportPathChooser;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (UvProgress.IsActive || intent == FbxExportIntent.None) return written;
            var entries = results?.Where(e => e != null && e.include).ToList() ?? new List<MeshEntry>();
            var groups = FbxExport.GroupByFbx(entries, WorkingMesh, null);
            RestoreAllPreviews();
            foreach (var group in groups)
            {
                Uv2DataAsset collisionData = null;
                if (collisions != null && !collisions.TryGetValue(group.Key, out collisionData)) continue;
                string output;
                if (choosePath != null)
                {
                    output = choosePath(group.Key, intent, overwriteSource);
                    if (output == null) continue;
                }
                else if (!TryChooseNarrowExportPath(group.Key, intent, overwriteSource, out output)) continue;
                var copies = group.Value.Select(p => p.entry.PreviewCopy(p.resultMesh)).ToList();
                bool sourceSave = string.IsNullOrEmpty(output) || string.Equals(output, group.Key, StringComparison.OrdinalIgnoreCase);
                var bindings = sourceSave ? FbxExport.CaptureSceneMeshReferences(group.Key,
                    collisionData == null ? copies : Array.Empty<MeshEntry>()) : null;
                bool ok = ExportFbxIsolatedCore(group.Key, copies, intent, output, refreshContext: false, collisionData: collisionData);
                // After a failure past the replacement an importer restore may have imported the
                // written file: the renderers noted take its meshes, and keep theirs otherwise.
                bindings?.Relink(group.Key);
                if (!ok) continue;
                written.Add(group.Key);
            }
            UvtLog.Info($"[FBX Export] Tool results: {written.Count}/{groups.Count} file(s) written.");
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
#endif
            return written;
        }

        public void SaveToolMeshes(IEnumerable<MeshEntry> results)
        {
            // A separate context selects the supplied meshes through the same SaveAll implementation.
            var output = new UvToolContext { PipeSettings = ctx.PipeSettings };
            foreach (var entry in results.Where(e => e != null && e.include))
            {
                var copy = entry.PreviewCopy(WorkingMesh(entry));
                if (!string.IsNullOrEmpty(copy.reverseTransferJson)) copy.repackedMesh = copy.originalMesh;
                output.MeshEntries.Add(copy);
            }
            output.Assets.SaveAllPublic();
        }

        internal static string ResolveModelFbx(GameObject source)
        {
            if (source == null) return null;
            string prefab = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source);
            if (SidecarStore.IsFbxPath(prefab)) return prefab;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var filter in source.GetComponentsInChildren<MeshFilter>(true))
            {
                string path = AssetDatabase.GetAssetPath(filter.sharedMesh);
                if (SidecarStore.IsFbxPath(path)) paths.Add(path);
            }
            foreach (var skin in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string path = AssetDatabase.GetAssetPath(skin.sharedMesh);
                if (SidecarStore.IsFbxPath(path)) paths.Add(path);
            }
            return paths.Count == 1 ? paths.Single() : null;
        }

        /// <summary>New model geometry (Remesh): the same atomic FBX writer and import policy.</summary>
        internal static void WriteModelResult(string path, GameObject root, IReadOnlyList<Material> materials,
            bool normalizedTransforms, Action committed = null, Action<string, ImportAssetOptions> importAsset = null)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            FbxExport.WriteAtomic(path, root, normalizedTransforms, () => {
                // The rename already committed, even if a subsequent import fails.
                // Old UV2 replay must never be applied to this replacement geometry.
                try { SidecarStore.ClearUv2Entries(new[] { path }); }
                finally { committed?.Invoke(); }
            });
            importAsset ??= AssetDatabase.ImportAsset;
            FbxExport.ReimportWithoutSidecars(path, () => importAsset(path, ImportAssetOptions.ForceSynchronousImport));
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) throw new InvalidOperationException("FBX reimport failed: " + path);
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.Import;
            importer.generateSecondaryUV = false;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            foreach (var material in materials)
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), material.name), material);
            if (normalizedTransforms)
            {
                importer.globalScale = 1;
                importer.useFileScale = true;
                importer.bakeAxisConversion = true;
            }
            FbxExport.ReimportWithoutSidecars(path, importer.SaveAndReimport);
#else
            throw new InvalidOperationException("FBX Exporter package not installed.");
#endif
        }

        public void ApplyUv2Public()
        { using var previewWrite = PreservePreviewDuringWrite(); ApplyUv2ToFbx(); }
        public void SaveAllPublic()
        { using var previewWrite = PreservePreviewDuringWrite(); SaveAll(); }

        /// <summary>
        /// Export only vertex colors (e.g. baked AO) to FBX without running the
        /// UV2 pipeline. Copies vertex colors from scene meshes onto the FBX clone
        /// and overwrites the source FBX. Only updates included mesh entries.
        /// </summary>
        public void ExportVertexColorsToFbx()
        {
            using var previewWrite = PreservePreviewDuringWrite();
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (ctx?.MeshEntries == null || ctx.MeshEntries.Count == 0)
            {
                UvtLog.Error("[FBX Export] No meshes loaded.");
                return;
            }

            RestoreAllPreviews();

            string sourceFbxPath = ResolveFbxPath();
            if (string.IsNullOrEmpty(sourceFbxPath))
            {
                UvtLog.Error("[FBX Export] Cannot find source FBX path.");
                return;
            }

            if (!EditorUtility.DisplayDialog("Overwrite FBX (Vertex Colors)",
                $"Overwrite '{System.IO.Path.GetFileName(sourceFbxPath)}' with current vertex colors?\n\n" +
                "Only vertex colors will be updated. UV2 and mesh topology stay unchanged.",
                "Overwrite", CancelButton))
                return;

            ExportVertexColorsToFbxCore(sourceFbxPath, ctx.MeshEntries);
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
#endif
        }

        // Hierarchy-mode entry point: export a specific FBX using a filtered
        // entry list. Caller (VertexColorBakingTool) owns user confirmation. The FBX
        // structure is preserved as-is (no LOD-style hierarchy normalization)
        // so unrelated submeshes / instanced refs are not mutated.
        public void ExportVertexColorsToFbx(string sourceFbxPath, IEnumerable<MeshEntry> entries, int uvChannelOverride = -1)
        {
            using var previewWrite = PreservePreviewDuringWrite();
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (string.IsNullOrEmpty(sourceFbxPath))
            {
                UvtLog.Error("[FBX Export] Missing source FBX path.");
                return;
            }
            var list = entries?.ToList();
            if (list == null || list.Count == 0)
            {
                UvtLog.Warn($"[FBX Export] No entries for '{sourceFbxPath}'.");
                return;
            }

            RestoreAllPreviews();
            ExportVertexColorsToFbxCore(sourceFbxPath, list, uvChannelOverride);
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
#endif
        }

        // Variant export: write painted meshes into a NEW FBX next to the
        // source (or any caller-chosen path) without mutating the source FBX
        // importer settings, scene mesh bindings, or working copies. Caller
        // (VariantExportPipeline) owns suffix validation and conflict policy.
        public bool ExportVertexColorsToFbxAs(
            string sourceFbxPath,
            string outputFbxPath,
            IEnumerable<MeshEntry> entries,
            int uvChannelOverride = -1)
        {
            using var previewWrite = PreservePreviewDuringWrite();
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (string.IsNullOrEmpty(sourceFbxPath) || string.IsNullOrEmpty(outputFbxPath))
            {
                UvtLog.Error("[FBX Export] Variant export needs both source and output paths.");
                return false;
            }
            var list = entries?.ToList();
            if (list == null || list.Count == 0)
            {
                UvtLog.Warn($"[FBX Export] No entries for variant export to '{outputFbxPath}'.");
                return false;
            }

            RestoreAllPreviews();
            // Vcolor shim never sets the Hierarchy bit: VariantExportPipeline
            // matches new-FBX sub-meshes to source-prefab MeshFilters by
            // sub-asset name, and hierarchy normalization (rename to
            // baseName_LOD{N}) would break that matching. The variant FBX
            // must mirror the source FBX's sub-mesh naming so prefab clones
            // can swap mesh refs cleanly.
            return ExportVertexColorsToFbxCore(
                sourceFbxPath, list,
                uvChannelOverride,
                outputFbxPathOverride: outputFbxPath);
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
            return false;
#endif
        }

        // Resolve the legacy vcolor flow's "AO target UV channel".
        // Used by the vcolor wrappers to fold their args into a
        // FbxExportIntent for the unified isolated-export core.
        static int ResolveLegacyAoUvChannel(int uvChannelOverride)
        {
            if (uvChannelOverride >= 0) return uvChannelOverride;
            var aoChannel = VertexChannels.LastAppliedTargetChannel;
            return aoChannel.HasValue ? VertexChannels.UvChannel(aoChannel.Value) : -1;
        }

        // Legacy shim. The implementation has been folded into
        // ExportFbxIsolatedCore — this method only computes the
        // FbxExportIntent for vcolor + optional AO-UV and delegates.
        // Public wrappers (ExportVertexColorsToFbx*) keep their
        // signatures so external callers (VariantExportPipeline,
        // VertexColorBakingTool, UvPackHierarchyTool) are unaffected.
        bool ExportVertexColorsToFbxCore(
            string sourceFbxPath,
            IEnumerable<MeshEntry> entries,
            int uvChannelOverride = -1,
            string outputFbxPathOverride = null)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            var intent = FbxExportIntent.VertexColors;
            int aoUvIdx = ResolveLegacyAoUvChannel(uvChannelOverride);
            if (aoUvIdx >= 0 && aoUvIdx <= 7)
                intent |= (FbxExportIntent)(1 << aoUvIdx);
            return ExportFbxIsolatedCore(sourceFbxPath, entries, intent, outputFbxPathOverride);
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
            return false;
#endif
        }

        // Narrow-intent group dispatcher for ExportFbx. One core call
        // per source FBX, reusing the standard "overwrite vs save-as"
        // dialog flow but routing the actual write through the safe
        // atomic core. Save-as without a project-relative path
        // gracefully degrades to the absolute path the user picked
        // (Unity's FBX exporter accepts both).
        void ExportNarrowIntentGroups(
            Dictionary<string, List<(MeshEntry entry, Mesh resultMesh)>> fbxGroups,
            FbxExportIntent intent,
            bool overwriteSource)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            int okCount = 0;
            int totalCount = 0;
            BeginSourceSaves();
            try
            {
                foreach (var kv in fbxGroups)
                {
                    totalCount++;
                    string sourceFbxPath = kv.Key;
                    var entries = kv.Value.Select(p => p.entry.PreviewCopy(p.resultMesh)).ToList();
                    if (!TryChooseNarrowExportPath(sourceFbxPath, intent, overwriteSource, out string outputFbxPath))
                    {
                        MarkUnsaved(kv.Value);
                        continue;
                    }

                    RestoreAllPreviews();
                    if (ExportFbxIsolatedCore(sourceFbxPath, entries, intent, outputFbxPath))
                        okCount++;
                    else
                        MarkUnsaved(kv.Value);
                }
            }
            finally { EndSourceSaves(); }
            UvtLog.Info($"[FBX Export] Narrow-intent export: {okCount}/{totalCount} group(s) succeeded.");
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
#endif
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        // A persistent sidecar re-applies its UV2 on every import; after a save wrote the
        // meshes' UVs into the file, its entries must hold the same UVs or the next import
        // would bring the old ones back. The entries are built before the write, from the
        // meshes the file was read with (the import that follows recreates them), and stored
        // only once the write succeeded.
        sealed class SidecarSync
        {
            public string fbxPath;
            public List<MeshUv2Entry> entries;
            public List<string> dropped;
        }

        SidecarSync PrepareSidecarSync(string fbxPath, List<(MeshEntry entry, Mesh resultMesh)> group)
        {
            if (!PostprocessorDefineManager.IsEnabled() || SidecarStore.Load(fbxPath) == null) return null;
            // A UV1 the working mesh dropped leaves the file too; its entry would bring it back.
            const UnityEngine.Rendering.VertexAttribute uv1 = UnityEngine.Rendering.VertexAttribute.TexCoord1;
            var dropped = group.Where(p => p.entry.fbxMesh != null && p.resultMesh != null && p.resultMesh != p.entry.fbxMesh
                && p.entry.fbxMesh.HasVertexAttribute(uv1) && !p.resultMesh.HasVertexAttribute(uv1)).ToList();
            return new SidecarSync
            {
                fbxPath = fbxPath,
                entries = BuildSidecarEntriesForExport(group.Except(dropped).ToList()),
                dropped = dropped.Select(p => p.entry.fbxMesh.name).ToList(),
            };
        }

        static void ApplySidecarSync(SidecarSync sync)
        {
            if (sync == null) return;
            int removed = SidecarStore.RemoveEntries(sync.fbxPath, sync.dropped);
            int saved = SidecarStore.SaveEntries(sync.fbxPath, sync.entries);
            if (saved + removed > 0)
                UvtLog.Info($"[FBX Export] Updated {saved} and removed {removed} UV2 entr(ies) in '{SidecarStore.PathFor(sync.fbxPath)}' to match the saved FBX.");
        }

        // A save whose file is written but whose steps after the replacement failed (media, .meta,
        // housekeeping): the import and the relink did not run. The persistent sidecar must still
        // hold the written UVs, or an import after the one the marker covers would replay the old
        // ones over them; the scene keeps its working copies, and a save once the cause is fixed
        // finishes the import and the relink.
        static void AfterPostCommitFailure(string writtenFbxPath, FbxExport.PostCommitException ex, SidecarSync sidecar)
        {
            UvtLog.Error($"[FBX Export] {ex.Message} The import of the written file and the scene relink did not run; fix the cause and save '{writtenFbxPath}' again.");
            UvtLog.Verbose(ex.ToString());
            ApplySidecarSync(sidecar);
        }

        static bool TryChooseNarrowExportPath(string sourceFbxPath, FbxExportIntent intent, bool overwriteSource, out string outputFbxPath)
        {
            outputFbxPath = null;

            if (overwriteSource)
            {
                if (!EditorUtility.DisplayDialog(
                        "Overwrite Source FBX",
                        $"Re-save '{System.IO.Path.GetFileName(sourceFbxPath)}' with intent {intent}?\n\n" +
                        "Channels not in the intent are preserved from the source FBX. " +
                        "Atomic write — original is untouched if export fails.",
                        "Overwrite", CancelButton))
                    return false;
            }
            else
            {
                string dir = System.IO.Path.GetDirectoryName(sourceFbxPath);
                string baseName = System.IO.Path.GetFileNameWithoutExtension(sourceFbxPath);
                string suffix = "_isolated";
                if ((intent & FbxExportIntent.AnyUv) != 0) suffix = "_uv";
                else if ((intent & FbxExportIntent.VertexColors) != 0) suffix = "_vcolor";
                string picked = EditorUtility.SaveFilePanel(
                    "Export FBX (isolated)", dir, baseName + suffix + ".fbx", "fbx");
                if (string.IsNullOrEmpty(picked)) return false;
                string dataPath = Application.dataPath;
                if (picked.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                    outputFbxPath = "Assets" + picked.Substring(dataPath.Length);
                else
                    outputFbxPath = picked;
            }

            return true;
        }
#endif

        /// <summary>
        /// Re-save the FBX at <paramref name="sourceFbxPath"/> overwriting
        /// only the per-vertex channels listed in <paramref name="intent"/>.
        /// Mesh names, hierarchy, transforms, material assignments, and all
        /// untouched per-vertex channels are preserved from the source FBX.
        /// </summary>
        /// <param name="sourceFbxPath">Project path to the FBX to overwrite.</param>
        /// <param name="entries">Mesh entries supplying source data. Matched
        /// against the FBX clone by sub-asset name.</param>
        /// <param name="intent">Channels the caller is allowed to write.
        /// <see cref="FbxExportIntent.None"/> is a no-op (logged + returns false).</param>
        public bool ExportIsolatedChannelsToFbx(
            string sourceFbxPath,
            IEnumerable<MeshEntry> entries,
            FbxExportIntent intent)
        {
            using var previewWrite = PreservePreviewDuringWrite();
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (intent == FbxExportIntent.None)
            {
                UvtLog.Warn("[FBX Export] ExportIsolatedChannelsToFbx called with FbxExportIntent.None — nothing to write.");
                return false;
            }
            if (string.IsNullOrEmpty(sourceFbxPath))
            {
                UvtLog.Error("[FBX Export] ExportIsolatedChannelsToFbx: missing source FBX path.");
                return false;
            }
            var list = entries?.ToList();
            if (list == null || list.Count == 0)
            {
                UvtLog.Warn($"[FBX Export] ExportIsolatedChannelsToFbx: no entries for '{sourceFbxPath}'.");
                return false;
            }
            RestoreAllPreviews();
            return ExportFbxIsolatedCore(sourceFbxPath, list, intent, outputFbxPathOverride: null);
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
            return false;
#endif
        }

        // Every in-tool FBX re-save goes through FbxExport.WriteChannels — there is no
        // parallel "destructive" pipeline. Hierarchy / Materials / Collision mutations are
        // wider FbxExportIntent bits, gated inside the core. A new caller-side
        // ModelExporter.ExportObjects call is a checklist violation (§12). The tool's part:
        // previews are restored by the callers before, the scene is refreshed and the
        // working copies put back after a source re-save.
        bool ExportFbxIsolatedCore(
            string sourceFbxPath,
            IEnumerable<MeshEntry> entries,
            FbxExportIntent intent,
            string outputFbxPathOverride,
            bool refreshContext = true,
            Uv2DataAsset collisionData = null)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            bool isVariantExport = !string.IsNullOrEmpty(outputFbxPathOverride)
                && !string.Equals(outputFbxPathOverride, sourceFbxPath, StringComparison.OrdinalIgnoreCase);
            var list = entries as IList<MeshEntry> ?? entries.ToList();
            // Every UV1 save leaves a persistent sidecar replaying the same: the source's, or a
            // variant's written into the project that may already have one of its own.
            string sidecarFbx = intent.IncludesUv(1) ? SidecarFbxPath(sourceFbxPath, outputFbxPathOverride, isVariantExport) : null;
            var sidecar = sidecarFbx == null ? null
                : PrepareSidecarSync(sidecarFbx, list.Select(e => (e, WorkingMesh(e))).Where(p => p.Item2 != null).ToList());
            if (!isVariantExport && refreshContext) PrepareStandaloneRelink(sourceFbxPath);
            // The written file's collision is what the sidecar must hold from here on.
            string collisionTarget = isVariantExport ? outputFbxPathOverride : sourceFbxPath;
            void SaveCollisionSidecar()
            {
                if (collisionData == null || !collisionTarget.StartsWith(AssetPathPrefix, StringComparison.Ordinal)) return;
                var saved = SidecarStore.LoadOrCreate(collisionTarget);
                Undo.RecordObject(saved, "Save Collision Sidecar");
                saved.collisionEntries = new List<CollisionMeshEntry>(collisionData.collisionEntries);
                SidecarStore.Save(saved);
            }
            bool exported;
            try
            {
                if (collisionData != null)
                {
                    using var plan = FbxStructureWrite.CollisionPlan(collisionData);
                    try { exported = FbxChannelWrite.Write(sourceFbxPath, Array.Empty<MeshEntry>(), FbxExportIntent.None, outputFbxPathOverride, null, plan); }
                    catch (Exception ex) when (!(ex is FbxExport.PostCommitException))
                    {
                        UvtLog.Error($"[FBX Export] '{sourceFbxPath}' was not written: {ex.Message}");
                        UvtLog.Verbose(ex.ToString());
                        exported = false;
                    }
                }
                else exported = FbxExport.WriteChannels(
                    sourceFbxPath, list, intent, outputFbxPathOverride,
                    FbxExport.FirstRealMaterial(refreshContext ? ctx?.MeshEntries : list),
                    isVariantExport || !refreshContext ? null : ctx?.LodGroup);
            }
            catch (FbxExport.PostCommitException ex)
            {
                // The file is written: the sidecars take what is in it; the scene follows the save
                // that finishes the job. The clone path's importer restore may have imported the
                // written file on the way out: the renderers noted take its meshes then, and keep
                // theirs when no import ran.
                AfterPostCommitFailure(collisionTarget, ex, sidecar);
                SaveCollisionSidecar();
                RelinkStandalone();
                if (!isVariantExport && refreshContext && ctx?.LodGroup != null)
                    FbxExport.RelinkSceneMeshReferences(sourceFbxPath, null, ctx.LodGroup);
                exported = false;
            }
            if (!exported) { standaloneRelinks.Clear(); return false; }
            ApplySidecarSync(sidecar);
            if (!isVariantExport)
                foreach (var e in list) SyncSceneEditChannels(e, WorkingMesh(e), intent, "FBX Export");
            SaveCollisionSidecar();
            if (!isVariantExport && refreshContext) AfterSourceSave();
            return true;
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
            return false;
#endif
        }

        // A source re-save leaves the entries holding copies of what the file now has: the
        // copies are freed and the scene reloaded. Within a save of several files that waits
        // for the last one, since the groups still to be written hold the same entries.
        // A file of the batch that was not written (cancelled, refused, failed) while its
        // entries hold working copies keeps them: they are the only record of that work, so
        // the batch then ends without freeing, adopting or reloading anything.
        bool sourceSavesOpen, sceneReloadPending, lodAdoptionPending, workLeftUnsaved;

        // The scene edits (MeshEntry.sceneEditMesh) whose geometry a save writes into the file, with
        // the file, local id and name that find the written mesh after the import. They are marked
        // before the write, while the imported mesh is still there to name, and unmarked when the
        // write does not happen. Only these are released after the save: a save of channels alone
        // leaves an edit the only record of its faces, so it stays on the renderer.
        readonly List<(MeshEntry entry, string fbxPath, string meshName, long localId)> savedSceneEdits = new List<(MeshEntry, string, string, long)>();

        void BeginSourceSaves()
        {
            sourceSavesOpen = true;
            sceneReloadPending = lodAdoptionPending = workLeftUnsaved = false;
            savedSceneEdits.Clear();
        }

        /// <summary>Records, before the write, that a save writes the scene edits of the <paramref name="entries"/> that <paramref name="written"/> accepts into <paramref name="fbxPath"/>.</summary>
        internal void MarkSceneEditsSaved(string fbxPath, IEnumerable<MeshEntry> entries, Func<MeshEntry, bool> written)
        {
            foreach (var e in entries)
            {
                if (e?.sceneEditMesh == null || e.fbxMesh == null || !written(e) || savedSceneEdits.Any(saved => saved.entry == e)) continue;
                long localId = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(e.fbxMesh, out string _, out long id) ? id : 0;
                savedSceneEdits.Add((e, fbxPath, e.fbxMesh.name, localId));
            }
        }

        /// <summary>Forgets the marks of <paramref name="fbxPath"/>: its write did not happen.</summary>
        internal void UnmarkSceneEditsSaved(string fbxPath)
            => savedSceneEdits.RemoveAll(saved => string.Equals(saved.fbxPath, fbxPath, StringComparison.OrdinalIgnoreCase));

        void MarkUnsaved(List<(MeshEntry entry, Mesh resultMesh)> group)
        {
            if (group.Any(p => HoldsWorkingCopy(p.entry))) workLeftUnsaved = true;
        }

        static bool HoldsWorkingCopy(MeshEntry e)
            => e != null && (e.repackedMesh != null || e.transferredMesh != null
                || (e.originalMesh != null && e.originalMesh != e.fbxMesh && !EditorUtility.IsPersistent(e.originalMesh)));

        void EndSourceSaves()
        {
            sourceSavesOpen = false;
            if (!sceneReloadPending) return;
            sceneReloadPending = false;
            if (workLeftUnsaved)
            {
                UvtLog.Warn("[FBX Export] Not every file of this save was written; the working copies stay loaded so nothing unsaved is lost. " +
                            "Save again to finish (the files already written keep what they got).");
                standaloneRelinks.Clear();
                lodAdoptionPending = false;
                ReleaseSavedSceneEdits();
                RestoreWorkingCopiesToScene();
                AfterWrite?.Invoke();
                return;
            }
            AfterSourceSave();
        }

        // adoptLods: the save wrote generated LODs, whose slots take the imported renderers.
        void AfterSourceSave(bool adoptLods = false)
        {
            lodAdoptionPending |= adoptLods;
            if (sourceSavesOpen) { sceneReloadPending = true; return; }
            if (ctx?.LodGroup != null) ReleaseWorkingMeshes();
            if (lodAdoptionPending) LodGroupUtility.AdoptImportedLods(ctx);
            lodAdoptionPending = false;
            if (ctx?.LodGroup != null) ctx.Refresh(ctx.LodGroup);
            else if (ctx != null && ctx.StandaloneMesh)
            {
                // No LODGroup for the write to relink: the renderer takes its reimported mesh here.
                var renderer = ctx.MeshEntries.FirstOrDefault(e => e?.renderer is MeshRenderer)?.renderer as MeshRenderer;
                ReleaseWorkingMeshes();
                RelinkStandalone();
                if (renderer != null) ctx.RefreshStandalone(renderer);
            }
            RestoreWorkingCopiesToScene();
            AfterWrite?.Invoke();
        }

        // A standalone renderer's filter, with the FBX and mesh name it shows, taken before the
        // write: the import that follows recreates the mesh it points at.
        readonly List<(MeshFilter filter, string fbxPath, string meshName)> standaloneRelinks = new List<(MeshFilter, string, string)>();

        void PrepareStandaloneRelink(string fbxPath)
        {
            if (ctx?.LodGroup != null || ctx == null || !ctx.StandaloneMesh || ctx.MeshEntries == null) return;
            foreach (var e in ctx.MeshEntries)
                if (e?.meshFilter != null && e.fbxMesh != null
                    && string.Equals(AssetDatabase.GetAssetPath(e.fbxMesh), fbxPath, StringComparison.OrdinalIgnoreCase))
                    standaloneRelinks.Add((e.meshFilter, fbxPath, e.fbxMesh.name));
        }

        void RelinkStandalone()
        {
            foreach (var (filter, fbxPath, meshName) in standaloneRelinks)
            {
                if (filter == null) continue;
                var fresh = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Mesh>().FirstOrDefault(m => m.name == meshName);
                // A scene edit the save did not write stays the renderer's mesh, as does a working copy of one.
                if (fresh == null || filter.sharedMesh == fresh || SceneEditRegistry.KeepsRenderer(filter, filter.sharedMesh)) continue;
                Undo.RecordObject(filter, "Relink Saved Mesh");
                filter.sharedMesh = fresh;
            }
            standaloneRelinks.Clear();
        }

        static Mesh WorkingMesh(MeshEntry e) => e.repackedMesh ?? e.transferredMesh ?? e.originalMesh ?? e.fbxMesh;

        // After a source re-save the working copies are in the file, and the refresh that
        // follows drops the entries holding them: give the renderers their baseline meshes
        // back and free the copies instead of leaking them. A scene edit is a baseline, not a
        // copy: it goes only when this save wrote its geometry (MarkSceneEditsSaved).
        internal void ReleaseWorkingMeshes()
        {
            if (ctx?.MeshEntries == null) return;
            foreach (var e in ctx.MeshEntries)
            {
                // The import the save recreated took the entry's baseline with it: a working copy
                // on the renderer that the registry knows still names what it is released to.
                var baseline = e.Baseline;
                if (baseline == null) baseline = SceneEditRegistry.BaselineBehindWorkingCopy(e);
                var copies = new[] { e.transferredMesh, e.repackedMesh,
                    e.originalMesh != e.fbxMesh && e.originalMesh != e.sceneEditMesh ? e.originalMesh : null };
                foreach (var copy in copies)
                {
                    if (copy == null || EditorUtility.IsPersistent(copy)) continue;
                    if (e.meshFilter != null && baseline != null && e.meshFilter.sharedMesh == copy) e.meshFilter.sharedMesh = baseline;
                    UnityEngine.Object.DestroyImmediate(copy);
                }
                e.transferredMesh = e.repackedMesh = null;
            }
            ReleaseSavedSceneEdits();
        }

        // The scene edits a save wrote into their file go the way of every saved working copy:
        // every renderer showing the edit takes the file's mesh and the edit is destroyed, both
        // through Undo like the edit itself; the registry keeps the edit's links to the written
        // mesh, so an Undo of the save brings the edit back as what it was. An edit whose written
        // mesh cannot be found stays, and says so.
        void ReleaseSavedSceneEdits()
        {
            foreach (var (entry, fbxPath, meshName, localId) in savedSceneEdits)
            {
                var edit = entry.sceneEditMesh;
                // The import may have recreated the sub-asset: find it again by its identity.
                var fresh = entry.fbxMesh != null ? entry.fbxMesh : SceneEditRegistry.FindImported(fbxPath, localId, meshName);
                if (fresh == null)
                {
                    UvtLog.Warn($"[FBX Export] '{meshName}' was not found in '{fbxPath}' after the save; the scene keeps its edited mesh.");
                    continue;
                }
                if (edit != null)
                {
                    // Every renderer showing the edit takes the file's mesh (a duplicated object
                    // shares it), each with its link for the Undo of the save; then the edit goes.
                    foreach (var filter in LodGroupUtility.FiltersShowing(edit))
                    {
                        Undo.RecordObject(filter, "Save FBX");
                        filter.sharedMesh = fresh;
                        if (PrefabUtility.IsPartOfPrefabInstance(filter))
                            PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
                        SceneEditRegistry.Register(filter, fresh, edit);
                    }
                    SceneEditRegistry.Register(entry.meshFilter, fresh, edit);
                }
                entry.sceneEditMesh = null;
                if (entry.originalMesh == edit || entry.originalMesh == null) entry.originalMesh = fresh;
                entry.fbxMesh = fresh;
                if (edit != null && !EditorUtility.IsPersistent(edit)) Undo.DestroyObjectImmediate(edit);
            }
            savedSceneEdits.Clear();
        }

        // A scene edit kept through a save (its faces were not written) takes the channels the
        // save wrote, from the result they were taken from, while the result still has the edit's
        // vertices: otherwise the renderer would show, and the next save write, the channels the
        // save has just replaced. A result with other vertices (a weld or a split ran) cannot be
        // mapped back and says so; the file has the channels, the edit keeps its own.
        internal static void SyncSceneEditChannels(MeshEntry e, Mesh written, FbxExportIntent intent, string label)
        {
            var edit = e?.sceneEditMesh;
            intent &= FbxExportIntent.AnyUv | FbxExportIntent.VertexColors | FbxExportIntent.Normals | FbxExportIntent.Tangents;
            if (edit == null || written == null || written == edit || intent == FbxExportIntent.None) return;
            if (written.vertexCount != edit.vertexCount || !written.vertices.SequenceEqual(edit.vertices))
            {
                UvtLog.Warn($"[{label}] '{edit.name}': the scene edit keeps its vertex channels; the ones written were made for other vertices (the file has them). Save the FBX document to write the edited faces with them.");
                return;
            }
            Undo.RecordObject(edit, label);
            var uvs = new List<Vector4>();
            for (int channel = 0; channel < 8; ++channel)
            {
                if (!intent.IncludesUv(channel)) continue;
                written.GetUVs(channel, uvs);
                edit.SetUVs(channel, uvs);
            }
            if ((intent & FbxExportIntent.VertexColors) != 0) VertexChannels.SetColors(edit, written.colors);
            if ((intent & FbxExportIntent.Normals) != 0) edit.normals = written.normals;
            if ((intent & FbxExportIntent.Tangents) != 0) edit.tangents = written.tangents;
            VertexChannels.RaiseChanged(edit);
        }

        static FbxExportIntent UvIntent(int channel) => (uint)channel > 7u ? FbxExportIntent.None : (FbxExportIntent)(1 << channel);

        // The entry's file reference, found again through its scene edit's link when a reimport the
        // entry has not been refreshed for recreated the imported mesh.
        static Mesh ImportOf(MeshEntry entry)
        {
            if (entry.fbxMesh == null && entry.sceneEditMesh != null
                && SceneEditRegistry.TryGetImported(entry.meshFilter, entry.sceneEditMesh, out var imported))
                entry.fbxMesh = imported;
            return entry.fbxMesh;
        }

        string ResolveFbxPath()
            => !string.IsNullOrEmpty(ctx.SourceFbxPath) ? ctx.SourceFbxPath
             : FbxExport.ResolveSourceFbxPath(ctx.MeshEntries, null, null);

        void RestoreWorkingCopiesToScene()
        {
            if (ctx?.MeshEntries == null) return;
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.meshFilter == null) continue;
                if (e.originalMesh != null && e.meshFilter.sharedMesh != e.originalMesh)
                    e.meshFilter.sharedMesh = e.originalMesh;
            }
        }


        // ExportFbx with intent. Narrow intent (no Hierarchy and no LodGroup bits)
        // delegates per group to the isolated channel re-save (atomic write, preflight,
        // nothing but the intended channels touched). The hub's All edits the FBX document
        // (channels plus generated LODs, sidecar collision and edited faces as nodes next to
        // their source). Other wide intents (Prefab Builder's explicit Hierarchy / LodGroup)
        // are the LOD-rebuild pipeline: mesh replacement by name, new LOD children, stale-
        // child pruning, hierarchy normalisation, collision injection from the sidecar.
        // The mechanics live in FbxExport; the policy — dialogs, backups, importer lock,
        // sidecar storage, relink, what to refresh — stays here. Migrating the wide path
        // to the atomic write is a follow-up; it keeps the direct overwrite that existing
        // tooling's sequencing depends on.
        void ExportFbx(bool overwriteSource, FbxExportIntent intent)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (intent == FbxExportIntent.None)
            {
                UvtLog.Warn("[FBX Export] ExportFbx called with FbxExportIntent.None — nothing to write.");
                return;
            }
            if (ctx?.MeshEntries == null || ctx.MeshEntries.Count == 0)
            {
                UvtLog.Error("[FBX Export] No meshes loaded.");
                return;
            }

            // Restore any active preview (checker, AO, shell colors) before export
            // so that original materials are captured, not preview materials.
            RestoreAllPreviews();

            // The source FBX: the entries' own, else the LODGroup's prefab source, else
            // the path cached at Refresh. Generated LODs (.asset paths) export into it.
            string sourceFbxFile = FbxExport.ResolveSourceFbxPath(ctx.MeshEntries, ctx.LodGroup, ctx.SourceFbxPath);
            var fbxGroups = FbxExport.GroupByFbx(ctx.MeshEntries, GetResultMesh, sourceFbxFile);
            if (fbxGroups.Count == 0) { UvtLog.Error("[FBX Export] No processed meshes to export."); return; }

            // Narrow-intent fast path: no hierarchy / LOD-chain mutation asked for, so
            // every group goes through the safe core. This is the path UV2 transfer, UV
            // pack and vertex color baking take — node names, transforms, materials and
            // untouched per-vertex channels come through byte-for-byte (modulo what
            // Unity's FBX Exporter itself rewrites at the document level).
            if ((intent & (FbxExportIntent.Hierarchy | FbxExportIntent.LodGroup)) == 0)
            {
                ExportNarrowIntentGroups(fbxGroups, intent, overwriteSource);
                return;
            }

            // "Save everything" (the hub's Overwrite / Export New FBX): changed UV sets and
            // vertex colours, generated LODs, sidecar collision and edited faces all go into the
            // FBX document itself (FbxChannelWrite + FbxStructureWrite). A rebuild through
            // Unity's FBX Exporter runs only when that is refused and the user picks it.
            if (intent == FbxExportIntent.All)
            {
                ExportDocumentGroups(fbxGroups, overwriteSource);
                return;
            }

            bool allGroupsSucceeded = true;
            var batch = new HierarchyExportBatch();
            foreach (var kv in fbxGroups)
                if (!ExportHierarchyGroup(kv.Key, kv.Value, overwriteSource, batch)) allGroupsSucceeded = false;

            FinishHierarchyExports(batch, overwriteSource, allGroupsSucceeded);
#endif
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        // The hub's full save, one FBX at a time, through the document edit.
        void ExportDocumentGroups(Dictionary<string, List<(MeshEntry entry, Mesh resultMesh)>> fbxGroups, bool overwriteSource)
        {
            int okCount = 0;
            // A rebuild picked for a refused file finishes after the loop, like the document saves.
            var rebuilds = new HierarchyExportBatch();
            BeginSourceSaves();
            try
            {
                foreach (var kv in fbxGroups)
                {
                    string sourceFbxPath = kv.Key;
                    // The plan reads renderer materials: previews (checker, AO, shells) must be off.
                    RestoreAllPreviews();
                    using var plan = FbxStructureWrite.Plan(sourceFbxPath, kv.Value, ctx.SourceLodIndex);
                    if (!TryChooseNarrowExportPath(sourceFbxPath, FbxExportIntent.All, overwriteSource, out string outputFbxPath))
                    {
                        MarkUnsaved(kv.Value);
                        continue;
                    }
                    bool isVariant = !string.IsNullOrEmpty(outputFbxPath)
                        && !string.Equals(outputFbxPath, sourceFbxPath, StringComparison.OrdinalIgnoreCase);
                    var entries = kv.Value.Select(p => p.entry.PreviewCopy(p.resultMesh)).ToList();
                    RestoreAllPreviews();
                    // The source's persistent sidecar, or a variant's written into the project that
                    // may already have one of its own.
                    string sidecarFbx = SidecarFbxPath(sourceFbxPath, outputFbxPath, isVariant);
                    var sidecar = sidecarFbx == null ? null : PrepareSidecarSync(sidecarFbx, kv.Value);
                    if (!isVariant) PrepareStandaloneRelink(sourceFbxPath);
                    // The structure step writes these meshes whole: their scene edits will be in the file.
                    // Marked now, while the import that follows the write cannot have replaced the
                    // meshes they are named after; the other scene edits take the channels the channel
                    // step writes, after it.
                    var reshaped = new HashSet<MeshEntry>(kv.Value.Select(p => p.entry)
                        .Where(e => e.fbxMesh != null && plan.reshaped.Any(r => r.name == e.fbxMesh.name)));
                    if (!isVariant) MarkSceneEditsSaved(sourceFbxPath, reshaped, _ => true);

                    bool written;
                    try
                    {
                        written = FbxChannelWrite.Write(sourceFbxPath, entries, FbxChannelWrite.Supported, outputFbxPath,
                            isVariant ? null : ctx?.LodGroup, plan);
                    }
                    catch (FbxStructureRefusalException refusal)
                    {
                        UnmarkSceneEditsSaved(sourceFbxPath);
                        written = ExportAfterRefusal(sourceFbxPath, kv.Value, entries, outputFbxPath, overwriteSource, refusal.Message, rebuilds);
                        if (written) okCount++;
                        else MarkUnsaved(kv.Value);
                        continue;
                    }
                    catch (FbxExport.PostCommitException ex)
                    {
                        // The file is written, its import and relink did not run: the sidecar takes
                        // the written UVs now, the scene keeps its working copies for the save that
                        // finishes the job.
                        AfterPostCommitFailure(isVariant ? outputFbxPath : sourceFbxPath, ex, sidecar);
                        written = false;
                    }
                    catch (Exception ex)
                    {
                        UnmarkSceneEditsSaved(sourceFbxPath);
                        UvtLog.Error($"[FBX Export] '{sourceFbxPath}' was not written: {ex.Message}");
                        UvtLog.Verbose(ex.ToString());
                        MarkUnsaved(kv.Value);
                        continue;
                    }
                    if (!written) { UnmarkSceneEditsSaved(sourceFbxPath); standaloneRelinks.Clear(); MarkUnsaved(kv.Value); continue; }
                    okCount++;
                    ApplySidecarSync(sidecar);
                    if (isVariant) continue;
                    foreach (var (entry, resultMesh) in kv.Value)
                        if (!reshaped.Contains(entry)) SyncSceneEditChannels(entry, resultMesh, FbxChannelWrite.Supported, "FBX Export");
                    AfterSourceSave(plan.lods.Count > 0);
                }
            }
            finally { EndSourceSaves(); }
            if (rebuilds.Groups > 0) FinishHierarchyExports(rebuilds, overwriteSource, rebuilds.AllSucceeded);
            UvtLog.Info($"[FBX Export] FBX document save: {okCount}/{fbxGroups.Count} file(s) written.");
        }

        // The document edit refused the structure (the file is untouched): say why and let the
        // user pick the rebuild, the channels alone, or nothing.
        // A rebuild goes into rebuilds; the caller finishes them after its last file.
        bool ExportAfterRefusal(string sourceFbxPath, List<(MeshEntry entry, Mesh resultMesh)> group, List<MeshEntry> entries,
            string outputFbxPath, bool overwriteSource, string reason, HierarchyExportBatch rebuilds)
        {
            UvtLog.Warn($"[FBX Export] '{sourceFbxPath}': {reason}.");
            int choice = EditorUtility.DisplayDialogComplex("Rebuild FBX?",
                $"'{System.IO.Path.GetFileName(sourceFbxPath)}' cannot take this save as an edit of the file:\n\n{reason}.\n\n" +
                "A rebuild re-exports every mesh from Unity's data through the FBX Exporter: polygons become triangles, " +
                "vertices are split at seams, vertex colours are stored as 8-bit, and the hierarchy is normalised.\n\n" +
                "'Save channels only' writes the changed UV sets and vertex colours into the existing file and leaves the rest of it untouched.",
                "Rebuild", CancelButton, "Save channels only");
            if (choice == 1) return false;
            if (choice == 2) return ExportFbxIsolatedCore(sourceFbxPath, entries, FbxChannelWrite.ChannelsOnly, outputFbxPath);
            bool ok = ExportHierarchyGroup(sourceFbxPath, group, overwriteSource, rebuilds);
            rebuilds.Groups++;
            rebuilds.AllSucceeded &= ok;
            return ok;
        }

        internal void FinishHierarchyExports(HierarchyExportBatch batch, bool overwriteSource, bool allGroupsSucceeded)
        {
            // Generated scene LOD objects are embedded in the exported FBX
            // now and would duplicate on reimport.
            if (overwriteSource && allGroupsSucceeded)
                LodGroupUtility.ClearGeneratedLods(ctx);

            // UV2 is baked into the FBX AND kept in the sidecar (for re-application after
            // third-party postprocessors like Bakery); the sidecar entries stay. A batch with a
            // failed group imports only the files whose write (and, for an overwrite, .meta
            // restore) succeeded: a file whose .meta could not be restored must not be imported
            // before it is, or it takes a new GUID.
            var ready = ImportHierarchyExports(batch, allGroupsSucceeded);

            var imported = ready.Where(batch.OverwrittenFbxPaths.Contains).ToList();
            // Stale "Lit" / "No Name" material remaps the importer created for collision-
            // only nodes must not survive an overwrite. Removing one reimports the file, so
            // this goes before the relink.
            if (overwriteSource)
            {
                RemoveHierarchyMaterialRemaps(batch, imported);
            }

            // Unity recreates sub-asset meshes on reimport; old MeshFilter references go
            // Missing even when names did not change. Every renderer noted before the write
            // takes the recreated mesh of the name it showed, or of the name the normalisation gave
            // it; under a LODGroup the by-name pass then covers the nodes the export renamed and
            // what the bindings could not place. Every overwritten file that was imported is
            // relinked, in a partly failed batch too, after the remap removal's reimport, which
            // recreates the meshes once more.
            if (overwriteSource)
            {
                foreach (string fbxPath in imported)
                {
                    batch.MeshRenamesByFbx.TryGetValue(fbxPath, out var renameMap);
                    if (batch.BindingsByFbx.TryGetValue(fbxPath, out var bindings)) bindings.Relink(fbxPath, renameMap);
                    if (ctx?.LodGroup == null) continue;
                    FbxExport.RelinkSceneMeshReferences(fbxPath, renameMap, ctx.LodGroup);
                }
            }

            // A rebuild writes every mesh from the scene: the scene edits of the files it
            // overwrote are in them, and their renderers take the written meshes.
            ReleaseSavedSceneEdits();

            if (allGroupsSucceeded)
                SwitchToPostApplyView();
        }

        void RemoveHierarchyMaterialRemaps(HierarchyExportBatch batch, List<string> imported)
        {
            foreach (string fbxPath in imported.ToList())
            {
                // One reimport that throws leaves the other files, and the relink below, to run;
                // the file itself waits for the reimport that finishes, with its scene edits.
                try { FbxExport.RemoveDefaultMaterialRemaps(fbxPath, () => batch.ReArm(fbxPath)); }
                catch (Exception ex)
                {
                    imported.Remove(fbxPath);
                    UnmarkSceneEditsSaved(fbxPath);
                    UvtLog.Error($"[FBX Export] '{fbxPath}': removing the default material remaps failed: {ex.Message} Reimport it once the cause is fixed.");
                    UvtLog.Verbose(ex.ToString());
                }
            }
        }

        static string SidecarFbxPath(string sourceFbxPath, string outputFbxPath, bool isVariant)
        {
            if (!isVariant) return sourceFbxPath;
            return outputFbxPath.StartsWith(AssetPathPrefix, StringComparison.Ordinal) ? outputFbxPath : null;
        }

        List<string> ImportHierarchyExports(HierarchyExportBatch batch, bool allGroupsSucceeded)
        {
            var ready = new List<string>();
            if (allGroupsSucceeded) { AssetDatabase.Refresh(); ready.AddRange(batch.ImportReadyFbxPaths); }
            else
                foreach (string fbxPath in batch.ImportReadyFbxPaths)
                {
                    // One import that throws (a third-party postprocessor) leaves the others to run.
                    try { AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate); ready.Add(fbxPath); }
                    catch (Exception ex)
                    {
                        // Its scene edits stay on their renderers: the release below would hand them
                        // the meshes of the import that did not happen.
                        UnmarkSceneEditsSaved(fbxPath);
                        UvtLog.Error($"[FBX Export] '{fbxPath}' was written, but its import failed: {ex.Message} Fix the cause and reimport it.");
                        UvtLog.Verbose(ex.ToString());
                    }
                }

            return ready;
        }

#endif

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        internal sealed class HierarchyExportBatch
        {
            public readonly HashSet<string> OverwrittenFbxPaths = new HashSet<string>();
            /// <summary>The project files whose write (and, for an overwrite, .meta restore) succeeded: the ones a partly failed batch imports and relinks.</summary>
            public readonly HashSet<string> ImportReadyFbxPaths = new HashSet<string>();
            /// <summary>The renderers showing each overwritten file's meshes, noted before the write that recreates them.</summary>
            public readonly Dictionary<string, FbxExport.SceneMeshBindings> BindingsByFbx = new Dictionary<string, FbxExport.SceneMeshBindings>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<MeshUv2Entry>> TransientReplayEntriesByPath = new Dictionary<string, List<MeshUv2Entry>>();
            public readonly Dictionary<string, Dictionary<string, string>> MeshRenamesByFbx = new Dictionary<string, Dictionary<string, string>>();
            public readonly bool PersistentSidecarMode = PostprocessorDefineManager.IsEnabled();
            /// <summary>Groups exported into this batch, and whether all of them succeeded.</summary>
            public int Groups;
            public bool AllSucceeded = true;
            public readonly SidecarStore.AoUvTarget Ao = AoTarget;
            public void ReArm(string path)
            {
                if (TransientReplayEntriesByPath.TryGetValue(path, out var entries)) SidecarStore.ArmTransientReplay(path, entries);
            }
        }

        bool ExportHierarchyGroup(string sourceFbxPath, List<(MeshEntry entry, Mesh resultMesh)> entries, bool overwriteSource, HierarchyExportBatch batch)
        {
            if (!TryPrepareHierarchyExportPath(sourceFbxPath, overwriteSource,
                    out string exportPath, out string tempDir, out string fullSourcePath, out string fbxBakName)) return false;
            return ExportHierarchyGroupPrepared(sourceFbxPath, entries, overwriteSource, batch, exportPath, tempDir, fullSourcePath, fbxBakName);
        }

        internal Action<string, GameObject> HierarchyWriter;
        internal Func<string, GameObject> HierarchyPrefabLoader;

        internal bool ExportHierarchyGroupPrepared(string sourceFbxPath, List<(MeshEntry entry, Mesh resultMesh)> entries,
            bool overwriteSource, HierarchyExportBatch batch, string exportPath, string tempDir, string fullSourcePath, string backupName)
        {
            bool written = false, failed = false, madeReadable = false, importReady = false;
            GameObject tempRoot = null;
            ModelImporter importer = null;
            var tempMeshes = new List<Mesh>();
            try
            {
                // Every import below recreates the file's meshes, the importer preparation's included:
                // the renderers showing them are noted first, by name, to take the recreated ones
                // after the last one (FinishHierarchyExports).
                if (overwriteSource) batch.BindingsByFbx[sourceFbxPath] = FbxExport.CaptureSceneMeshReferences(sourceFbxPath, entries.Select(p => p.entry));
                if (overwriteSource)
                    Uv2AssetPostprocessor.PrepareImportSettings(sourceFbxPath, force: true, lockForFbxOverwrite: true);
                importer = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
                if (!overwriteSource && importer != null && !importer.isReadable)
                {
                    madeReadable = true;
                    importer.isReadable = true;
                    FbxExport.ReimportWithoutSidecars(sourceFbxPath, importer.SaveAndReimport);
                }
                var prefab = HierarchyPrefabLoader != null ? HierarchyPrefabLoader(sourceFbxPath) : AssetDatabase.LoadAssetAtPath<GameObject>(sourceFbxPath);
                if (prefab == null) throw new InvalidOperationException("Cannot load FBX prefab: " + sourceFbxPath);
                // The rebuild writes every mesh from the scene: the scene edits of an overwritten file
                // are in it. Marked before the write, while their imported meshes are there to name.
                if (overwriteSource) MarkSceneEditsSaved(sourceFbxPath, entries.Select(p => p.entry), _ => true);
                tempRoot = UnityEngine.Object.Instantiate(prefab);
                tempRoot.name = prefab.name;
                FbxExport.PromoteRootMeshToLod0Child(tempRoot);
                int collisions = PrepareExportHierarchy(tempRoot, sourceFbxPath, entries, tempMeshes, batch);
                try { WriteHierarchyExport(exportPath, tempRoot, overwriteSource); }
                catch (FbxExport.PostCommitException ex)
                {
                    // The file is written; the .meta backup restored below covers a failed .meta restore.
                    UvtLog.Warn("[FBX Export] " + ex.Message);
                }
                written = true;
                if (overwriteSource) batch.OverwrittenFbxPaths.Add(sourceFbxPath);
                UvtLog.Info($"[FBX Export] Exported ({FbxWriteSettings.FromProject().FormatName}) {entries.Count + collisions} mesh(es) -> {exportPath}");
            }
            catch (Exception ex) { UvtLog.Error("[FBX Export] Export failed: " + ex); failed = true; }
            finally
            {
                if (tempRoot != null) UnityEngine.Object.DestroyImmediate(tempRoot);
                FbxExport.DestroyTempMeshes(tempMeshes);
                // An overwrite is ready for import once its .meta is back; a new file as soon as it
                // is written. The source importer's readability is restored separately: its failure
                // fails the group, not the file the group wrote.
                bool restored = true;
                if (overwriteSource)
                {
                    restored = RestoreHierarchyBackup(tempDir, backupName, fullSourcePath, sourceFbxPath, written);
                    failed |= !restored;
                }
                importReady = written && restored;
                if (madeReadable && importer != null)
                    failed |= !RestoreHierarchyReadability(importer, sourceFbxPath);
            }
            // A completed write needs replay even if restoring its importer metadata failed.
            if (overwriteSource && written) StoreHierarchyExportSidecars(sourceFbxPath, entries, true, batch);
            if (overwriteSource && !(written && !failed)) UnmarkSceneEditsSaved(sourceFbxPath);
            // A new file outside the project is not an asset; nothing imports it.
            if (importReady && IsProjectAssetPath(exportPath)) batch.ImportReadyFbxPaths.Add(exportPath);
            return written && !failed;
        }

        static bool IsProjectAssetPath(string path) =>
            path != null && (path.StartsWith(AssetPathPrefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages\\", StringComparison.OrdinalIgnoreCase));

        // A source is replaced in one rename once its new file is complete and checked
        // (FbxExport.WriteAtomic); a new file is written in place.
        void WriteHierarchyExport(string path, GameObject root, bool overwriteSource)
        {
            if (HierarchyWriter != null) HierarchyWriter(path, root);
            else if (overwriteSource) FbxExport.WriteAtomic(path, root);
            else FbxExport.Write(path, root);
        }

        static bool RestoreHierarchyReadability(ModelImporter importer, string path)
        {
            try {
                importer.isReadable = false;
                FbxExport.ReimportWithoutSidecars(path, importer.SaveAndReimport);
                return true;
            }
            catch (Exception ex) {
                UvtLog.Error("[FBX Export] Readability restore failed: " + ex.Message);
                return false;
            }
        }

        // After a write that did not complete the source and its .meta come back from the backup
        // (the atomic replace leaves the source as it was; the copy is a belt and braces) and the
        // backup goes; after a completed one the .meta is restored and the backup of the original
        // stays where the dialog said it would, one per source file, replaced by the next save.
        internal static bool RestoreHierarchyBackup(string directory, string name, string fullSourcePath, string assetPath, bool written)
        {
            string fbxBackup = System.IO.Path.Combine(directory, name + ".bak");
            string metaBackup = System.IO.Path.Combine(directory, name + ".meta.bak");
            try
            {
                if (!written) System.IO.File.Copy(fbxBackup, fullSourcePath, true);
                if (System.IO.File.Exists(metaBackup)) System.IO.File.Copy(metaBackup, fullSourcePath + ".meta", true);
                if (!written && !string.IsNullOrEmpty(assetPath))
                    FbxExport.ReimportWithoutSidecars(assetPath, () => AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate));
            }
            catch (Exception ex) {
                UvtLog.Error($"[FBX Export] Backup restore failed; copies kept in {directory}: {ex.Message}");
                return false;
            }
            // The source and its .meta are as they should be from here: a backup copy that cannot be
            // deleted is housekeeping, not a failed restore.
            try
            {
                if (System.IO.File.Exists(metaBackup)) System.IO.File.Delete(metaBackup);
                if (!written && System.IO.File.Exists(fbxBackup)) System.IO.File.Delete(fbxBackup);
            }
            catch (Exception ex) { UvtLog.Warn($"[FBX Export] Backup copies left in {directory}: {ex.Message}"); }
            if (written) UvtLog.Info($"[FBX Export] The original of '{assetPath}' is kept at '{fbxBackup}' until the next save of this file.");
            return true;
        }

        /// <summary>Eight hex digits of the path's SHA-256: the same in every process and runtime, unlike string.GetHashCode.</summary>
        internal static string StablePathHash(string fullPath)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var digest = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(fullPath));
            return System.BitConverter.ToString(digest, 0, 4).Replace("-", "");
        }

        static bool TryPrepareHierarchyExportPath(string sourceFbxPath, bool overwriteSource,
            out string exportPath, out string backupDirectory, out string fullSourcePath, out string backupName)
        {
            exportPath = null;
            backupDirectory = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.dataPath), "Library", "MeshLab");
            // Hash the full path so two FBX files with the same filename
            // (e.g. Assets/A/Chair.fbx and Assets/B/Chair.fbx) get distinct
            // backup names and never overwrite each other. The backup stays after the save,
            // so the hash is one that does not change between editor sessions.
            fullSourcePath = System.IO.Path.GetFullPath(sourceFbxPath);
            backupName = System.IO.Path.GetFileName(fullSourcePath) + "." + StablePathHash(fullSourcePath);
            if (overwriteSource)
            {
                if (!EditorUtility.DisplayDialog("Overwrite Source FBX",
                    "This will overwrite:\n" + sourceFbxPath + "\n\nThe original is kept as a .fbx.bak under Library/MeshLab until the next save of this file. Continue?",
                    "Overwrite", CancelButton))
                {
                    return false;
                }
                exportPath = sourceFbxPath;
                string fullMeta = fullSourcePath + ".meta";
                try
                {
                    System.IO.Directory.CreateDirectory(backupDirectory);
                    System.IO.File.Copy(fullSourcePath, System.IO.Path.Combine(backupDirectory, backupName + ".bak"), true);
                    if (System.IO.File.Exists(fullMeta))
                        System.IO.File.Copy(fullMeta, System.IO.Path.Combine(backupDirectory, backupName + ".meta.bak"), true);
                }
                catch (Exception ex) { UvtLog.Error("[FBX Export] Backup failed: " + ex.Message); return false; }
            }
            else
            {
                string dir = System.IO.Path.GetDirectoryName(sourceFbxPath);
                string baseName = System.IO.Path.GetFileNameWithoutExtension(sourceFbxPath);
                exportPath = EditorUtility.SaveFilePanel("Export FBX", dir, baseName + "_uv2.fbx", "fbx");
                if (string.IsNullOrEmpty(exportPath))
                {
                    return false;
                }
                // A destination inside the project (Assets, an embedded or local package) is an
                // asset path from here on; one outside stays a file path.
                string relative = FileUtil.GetProjectRelativePath(exportPath.Replace('\\', '/'));
                if (!string.IsNullOrEmpty(relative)) exportPath = relative;
            }

            return true;
        }

        int PrepareExportHierarchy(GameObject tempRoot, string sourceFbxPath, List<(MeshEntry entry, Mesh resultMesh)> entries,
            List<Mesh> tempMeshes, HierarchyExportBatch batch)
        {
            var lastLodRendererTemplate = FbxExport.FindLastLodRenderer(entries);
            // Sidecar positions belong to their source mesh, not the normalized
            // model root. Preserve the source frames before replacement and baking.
            var collisionSourceFrames = FbxExport.CollisionSourceFrames(tempRoot);

            // export mesh name → export mesh / scene renderer whose settings it takes
            var meshReplacements = new Dictionary<string, Mesh>();
            var meshRendererTemplates = new Dictionary<string, Renderer>();
            foreach (var (entry, resultMesh) in entries)
            {
                var exportMesh = FbxExport.BuildExportMesh(entry, resultMesh, batch.Ao, tempMeshes);
                meshReplacements[exportMesh.name] = exportMesh;
                if (entry.renderer != null)
                    meshRendererTemplates[exportMesh.name] = entry.renderer;
            }

            var replaced = FbxExport.ReplaceMeshes(tempRoot, meshReplacements, meshRendererTemplates);

            // Meshes the clone did not have (generated LODs) become new children.
            foreach (var (entry, resultMesh) in entries)
            {
                string meshName = FbxExport.ResolveExportMeshName(entry, resultMesh);
                if (replaced.Contains(meshName)) continue;
                FbxExport.AddLodChild(tempRoot, meshName, meshReplacements[meshName], entry.renderer, lastLodRendererTemplate);
            }

            // Full LOD workflows prune renderable leftovers outside the export set;
            // a standalone / partial overwrite keeps untouched siblings and only
            // replaces the selected mesh. Before NormalizeExportHierarchy, which
            // renames LOD0.
            if (!(ctx != null && ctx.StandaloneMesh))
                FbxExport.PruneStaleChildren(tempRoot, new HashSet<string>(meshReplacements.Keys));
            else
                UvtLog.Verbose("[FBX Export] Standalone overwrite: preserving untouched sibling meshes in source FBX.");

            // Clean root pivot, _LOD0 suffix on the root-named child, contiguous
            // LOD numbering, transforms baked into the meshes.
            var nodeRenameMap = FbxExport.NormalizeExportHierarchy(tempRoot, tempMeshes);
            if (nodeRenameMap.Count > 0)
                batch.MeshRenamesByFbx[sourceFbxPath] = nodeRenameMap;

            // Collision from the sidecar replaces the clone's _COL children; every
            // _COL mesh is then stripped to geometry and given a real material.
            int collisionMeshCount = FbxExport.InjectCollisionMeshes(tempRoot, SidecarStore.CollisionMeshes(sourceFbxPath), tempMeshes,
                collisionSourceFrames, SidecarStore.CollisionSources(sourceFbxPath));
            FbxExport.StripCollisionMeshes(tempRoot, FbxExport.FirstRealMaterial(entries.Select(p => p.entry)), tempMeshes);
            FbxExport.TrimMaterialArrays(tempRoot);

            return collisionMeshCount;
        }

        void StoreHierarchyExportSidecars(string sourceFbxPath, List<(MeshEntry entry, Mesh resultMesh)> entries,
                    bool groupSucceeded, HierarchyExportBatch batch)
                {
                    // Store the UV2 entries so our postprocessor (order=10000) re-applies UV2
                    // after third-party postprocessors (e.g. Bakery auto-unwrap): in the
                    // sidecar when Sidecar UV2 Mode is on, as a one-shot replay otherwise.
                    var sidecarEntries = BuildSidecarEntriesForExport(entries);
                    if (batch.PersistentSidecarMode)
                    {
                        int saved = SidecarStore.SaveEntries(sourceFbxPath, sidecarEntries);
                        if (saved > 0)
                            UvtLog.Info($"[FBX Export] Saved {saved} UV2 entries to sidecar '{SidecarStore.PathFor(sourceFbxPath)}' for post-import re-application");
                    }
                    else
                    {
                        batch.TransientReplayEntriesByPath[sourceFbxPath] = sidecarEntries;
                        batch.ReArm(sourceFbxPath);
                    }

                    Uv2AssetPostprocessor.managedImportPaths.Add(sourceFbxPath);
                    if (!batch.PersistentSidecarMode)
                        Uv2AssetPostprocessor.transientReplayPaths.Add(sourceFbxPath);

                    // Re-apply the UV2-friendly importer flags after the .meta backup was
                    // restored above: the backup predates the pre-export lock, so it carries
                    // the user's original settings (possibly keepQuads=false, globalScale=0.01,
                    // generateSecondaryUV=true) and restoring it as-is would undo every flag.
                    // peek mode skips the second SaveAndReimport when nothing drifted.
                    if (groupSucceeded &&
                        Uv2AssetPostprocessor.PrepareImportSettings(sourceFbxPath, force: true, peek: true, lockForFbxOverwrite: true))
                    {
                        if (!batch.PersistentSidecarMode) batch.ReArm(sourceFbxPath);
                        Uv2AssetPostprocessor.PrepareImportSettings(sourceFbxPath, force: true, lockForFbxOverwrite: true);
                    }
        }
#endif

        // Where vertex AO was written, as the export and the sidecar need it.
        static SidecarStore.AoUvTarget AoTarget => SidecarStore.AoUvTarget.From(VertexChannels.LastAppliedTargetChannel);

        bool TryBuildSidecarEntry(MeshEntry entry, Mesh resultMesh, out MeshUv2Entry sidecarEntry)
            => SidecarStore.TryBuildEntry(entry, resultMesh, entry != null && entry.lodIndex == ctx.SourceLodIndex, AoTarget, out sidecarEntry);

        List<MeshUv2Entry> BuildSidecarEntriesForExport(List<(MeshEntry entry, Mesh resultMesh)> entries)
        {
            var sidecarEntries = new List<MeshUv2Entry>();
            foreach (var (e, resultMesh) in entries)
            {
                if (resultMesh == null) continue;
                if (!TryBuildSidecarEntry(e, resultMesh, out var sidecarEntry))
                    continue;

                sidecarEntries.Add(sidecarEntry);
            }

            return sidecarEntries;
        }

        internal static bool EnsureOutputFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string[] parts = path.Replace('\\', '/').TrimEnd('/').Split('/');
            if (parts[0] != "Assets") return false;
            foreach (string part in parts)
                if (string.IsNullOrEmpty(part) || part == "." || part == ".." ||
                    part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return false;
            string parent = "Assets";
            for (int i = 1; i < parts.Length; ++i)
            {
                string folder = parent + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    // Never accept CreateFolder's auto-renamed sibling when a
                    // file already occupies the requested output path.
                    if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(folder, AssetPathToGUIDOptions.OnlyExistingAssets))) return false;
                    string guid = AssetDatabase.CreateFolder(parent, parts[i]);
                    if (string.IsNullOrEmpty(guid) || AssetDatabase.GUIDToAssetPath(guid) != folder ||
                        !AssetDatabase.IsValidFolder(folder)) return false;
                }
                parent = folder;
            }
            return AssetDatabase.IsValidFolder(parent);
        }

        void SaveAll()
        {
            RestoreAllPreviews();
            string p = ctx.PipeSettings.savePath;
            if (string.IsNullOrEmpty(p)) p = "Assets/UnityMeshLab/Output";
            p = p.Replace('\\', '/').TrimEnd('/');
            if (!EnsureOutputFolder(p))
            {
                UvtLog.Error("[Save] Output folder does not exist and could not be created: " + p);
                return;
            }
            int n = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var e in ctx.MeshEntries)
                {
                    if (SaveMeshAsset(e, p)) n++;
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            UvtLog.Info("[Save] " + n + " assets -> " + p);
        }

        bool SaveMeshAsset(MeshEntry entry, string folder)
        {
            Mesh m = GetResultMesh(entry);
            if (m == null) return false;
            // Mesh names can carry characters that are invalid in file names
            // (':', '/', ...). GenerateUniqueAssetPath then returns an empty
            // string and CreateAsset throws mid-OnGUI ("path is empty").
            string clean = m.name;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) clean = clean.Replace(c, '_');
            if (string.IsNullOrWhiteSpace(clean)) clean = "Mesh";
            string ap = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + clean + ".asset");
            if (string.IsNullOrEmpty(ap))
            {
                UvtLog.Warn("[Save] Skipped '" + m.name + "': no valid asset path under " + folder);
                return false;
            }
            var saved = UnityEngine.Object.Instantiate(m);
            saved.name = m.name;
            saved.hideFlags = HideFlags.None;
            try
            {
                TangentValidator.EnforceTangentsMatchOriginal(saved, entry.fbxMesh, "SaveAll");
                AssetDatabase.CreateAsset(saved, ap);
            }
            catch
            {
                if (!EditorUtility.IsPersistent(saved)) UnityEngine.Object.DestroyImmediate(saved);
                throw;
            }
            return true;
        }

    }
}
