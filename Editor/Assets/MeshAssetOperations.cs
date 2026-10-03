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
        readonly UvToolContext ctx;
        internal Action BeforeWrite, AfterWrite;
        public MeshAssetOperations(UvToolContext context) => ctx = context ?? throw new ArgumentNullException(nameof(context));

        void RestoreAllPreviews() => BeforeWrite?.Invoke();
        void SwitchToPostApplyView()
        {
            if (ctx.LodGroup != null) ctx.Refresh(ctx.LodGroup);
            else if (ctx.StandaloneMesh)
            {
                var renderer = ctx.MeshEntries.FirstOrDefault(e => e?.renderer is MeshRenderer)?.renderer as MeshRenderer;
                if (renderer != null) ctx.RefreshStandalone(renderer);
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
            SwitchToPostApplyView();
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
                    Uv2AssetPostprocessor.bypassPaths.Add(p);
                    imp.SaveAndReimport();
                }
                foreach (var e in ctx.MeshEntries)
                {
                    if (e.meshFilter != null && e.meshFilter.sharedMesh != null)
                        e.fbxMesh = e.meshFilter.sharedMesh;
                }
                Uv2AssetPostprocessor.bypassPaths.Clear();
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

        public void ExportFbxPublic(bool overwriteSource) => ExportFbx(overwriteSource, FbxExportIntent.All);
        public void ExportFbxPublic(bool overwriteSource, FbxExportIntent intent) => ExportFbx(overwriteSource, intent);
        public void ApplyUv2Public() => ApplyUv2ToFbx();
        public void SaveAllPublic() => SaveAll();

        /// <summary>
        /// Export only vertex colors (e.g. baked AO) to FBX without running the
        /// UV2 pipeline. Copies vertex colors from scene meshes onto the FBX clone
        /// and overwrites the source FBX. Only updates included mesh entries.
        /// </summary>
        public void ExportVertexColorsToFbx()
        {
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
            foreach (var kv in fbxGroups)
            {
                totalCount++;
                string sourceFbxPath = kv.Key;
                var entries = kv.Value.Select(p => p.entry).ToList();
                if (!TryChooseNarrowExportPath(sourceFbxPath, intent, overwriteSource, out string outputFbxPath)) continue;

                RestoreAllPreviews();
                if (ExportFbxIsolatedCore(sourceFbxPath, entries, intent, outputFbxPath))
                    okCount++;
            }
            UvtLog.Info($"[FBX Export] Narrow-intent export: {okCount}/{totalCount} group(s) succeeded.");
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
#endif
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
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
            string outputFbxPathOverride)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            bool isVariantExport = !string.IsNullOrEmpty(outputFbxPathOverride)
                && !string.Equals(outputFbxPathOverride, sourceFbxPath, StringComparison.OrdinalIgnoreCase);
            bool exported = FbxExport.WriteChannels(
                sourceFbxPath, entries, intent, outputFbxPathOverride,
                FbxExport.FirstRealMaterial(ctx?.MeshEntries),
                isVariantExport ? null : ctx?.LodGroup);
            if (!exported) return false;
            if (!isVariantExport)
            {
                if (ctx?.LodGroup != null) ctx.Refresh(ctx.LodGroup);
                RestoreWorkingCopiesToScene();
                AfterWrite?.Invoke();
            }
            return true;
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
            return false;
#endif
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
        // nothing but the intended channels touched). Wide intent (Hierarchy or LodGroup)
        // is the LOD-rebuild pipeline: mesh replacement by name, new LOD children, stale-
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

            bool allGroupsSucceeded = true;
            var batch = new HierarchyExportBatch();
            foreach (var kv in fbxGroups)
                if (!ExportHierarchyGroup(kv.Key, kv.Value, overwriteSource, batch)) allGroupsSucceeded = false;

            FinishHierarchyExports(batch, overwriteSource, allGroupsSucceeded);
#endif
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        void FinishHierarchyExports(HierarchyExportBatch batch, bool overwriteSource, bool allGroupsSucceeded)
        {
            // Generated scene LOD objects are embedded in the exported FBX
            // now and would duplicate on reimport.
            if (overwriteSource && allGroupsSucceeded)
                LodGroupUtility.ClearGeneratedLods(ctx);

            // UV2 is baked into the FBX AND kept in the sidecar (for re-application after
            // third-party postprocessors like Bakery); the sidecar entries stay.
            AssetDatabase.Refresh();

            // Unity recreates sub-asset meshes on reimport; old MeshFilter references go
            // Missing even when names did not change. Relink every overwritten FBX.
            if (overwriteSource && allGroupsSucceeded && ctx?.LodGroup != null)
            {
                foreach (string fbxPath in batch.OverwrittenFbxPaths)
                {
                    batch.MeshRenamesByFbx.TryGetValue(fbxPath, out var renameMap);
                    FbxExport.RelinkSceneMeshReferences(fbxPath, renameMap, ctx.LodGroup);
                }
            }

            // Stale "Lit" / "No Name" material remaps the importer created for collision-
            // only nodes must not survive an overwrite.
            if (overwriteSource && allGroupsSucceeded)
            {
                foreach (string fbxPath in batch.OverwrittenFbxPaths)
                    FbxExport.RemoveDefaultMaterialRemaps(fbxPath, () => batch.ReArm(fbxPath));
            }

            if (allGroupsSucceeded)
                SwitchToPostApplyView();
        }
#endif

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        sealed class HierarchyExportBatch
        {
            public readonly HashSet<string> OverwrittenFbxPaths = new HashSet<string>();
            public readonly Dictionary<string, List<MeshUv2Entry>> TransientReplayEntriesByPath = new Dictionary<string, List<MeshUv2Entry>>();
            public readonly Dictionary<string, Dictionary<string, string>> MeshRenamesByFbx = new Dictionary<string, Dictionary<string, string>>();
            public readonly bool PersistentSidecarMode = PostprocessorDefineManager.IsEnabled();
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
            bool groupSucceeded = false;

            // Overwrite: lock the import settings BEFORE the export, so no extra
            // post-export reimport lets a third-party importer (Bakery) touch UV2
            // before the user validates. lockForFbxOverwrite normalises topology and
            // scale (keepQuads, useFileScale, globalScale=1.0) so the round-trip is
            // 1:1 metres and keeps quads.
            if (overwriteSource)
                Uv2AssetPostprocessor.PrepareImportSettings(sourceFbxPath, force: true, lockForFbxOverwrite: true);

            // The FBX Exporter needs readable meshes (notably _COL meshes without
            // sidecar data).
            var srcImporter = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
            bool madeReadable = false;
            if (!overwriteSource && srcImporter != null && !srcImporter.isReadable)
            {
                srcImporter.isReadable = true;
                Uv2AssetPostprocessor.bypassPaths.Add(sourceFbxPath);
                srcImporter.SaveAndReimport();
                madeReadable = true;
            }

            // Clone the FBX hierarchy and replace only the meshes.
            var fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourceFbxPath);
            if (fbxPrefab == null) { UvtLog.Error("[FBX Export] Cannot load FBX prefab: " + sourceFbxPath); return false; }
            var tempRoot = UnityEngine.Object.Instantiate(fbxPrefab);
            tempRoot.name = fbxPrefab.name;
            FbxExport.PromoteRootMeshToLod0Child(tempRoot);

            // Temporary meshes of this group — export copies, transform-baked copies,
            // stripped collision meshes, sidecar hulls. They only ever live on
            // tempRoot and are destroyed once the export finished.
            var tempMeshes = new List<Mesh>();
            try
            {
                int collisionMeshCount = PrepareExportHierarchy(tempRoot, sourceFbxPath, entries, tempMeshes, batch);

                FbxExport.Write(exportPath, tempRoot);
                int totalExported = entries.Count + collisionMeshCount;
                UvtLog.Info("[FBX Export] Exported (binary) " + totalExported + " mesh(es) -> " + exportPath);
                groupSucceeded = true;
                // Restore original .meta from temp backup
                if (overwriteSource)
                {
                    string metaBak = System.IO.Path.Combine(tempDir, fbxBakName + ".meta.bak");
                    if (System.IO.File.Exists(metaBak))
                    {
                        System.IO.File.Copy(metaBak, fullSourcePath + ".meta", true);
                        System.IO.File.Delete(metaBak);
                    }
                    string fbxBak = System.IO.Path.Combine(tempDir, fbxBakName + ".bak");
                    if (System.IO.File.Exists(fbxBak))
                        System.IO.File.Delete(fbxBak);
                    batch.OverwrittenFbxPaths.Add(sourceFbxPath);
                }
            }
            catch (Exception ex) { UvtLog.Error("[FBX Export] Export failed: " + ex); groupSucceeded = false; }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tempRoot);
                FbxExport.DestroyTempMeshes(tempMeshes);
            }

            // Restore isReadable if we changed it (non-overwrite path only;
            // overwrite path restores .meta from backup automatically).
            if (madeReadable && !overwriteSource && srcImporter != null)
            {
                srcImporter.isReadable = false;
                Uv2AssetPostprocessor.bypassPaths.Add(sourceFbxPath);
                srcImporter.SaveAndReimport();
            }

            if (overwriteSource) StoreHierarchyExportSidecars(sourceFbxPath, entries, groupSucceeded, batch);
            return groupSucceeded;
        }

        static bool TryPrepareHierarchyExportPath(string sourceFbxPath, bool overwriteSource,
            out string exportPath, out string backupDirectory, out string fullSourcePath, out string backupName)
        {
            exportPath = null;
            backupDirectory = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.dataPath), "Library", "MeshLab");
            // Hash the full path so two FBX files with the same filename
            // (e.g. Assets/A/Chair.fbx and Assets/B/Chair.fbx) get distinct
            // backup names and never overwrite each other.
            fullSourcePath = System.IO.Path.GetFullPath(sourceFbxPath);
            backupName = System.IO.Path.GetFileName(fullSourcePath) + "." +
                unchecked((uint)fullSourcePath.GetHashCode()).ToString("X8");
            if (overwriteSource)
            {
                if (!EditorUtility.DisplayDialog("Overwrite Source FBX",
                    "This will overwrite:\n" + sourceFbxPath + "\n\nA backup (.fbx.bak) will be created. Continue?",
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
                string dataPath = Application.dataPath;
                if (exportPath.StartsWith(dataPath))
                    exportPath = "Assets" + exportPath.Substring(dataPath.Length);
            }

            return true;
        }

        int PrepareExportHierarchy(GameObject tempRoot, string sourceFbxPath, List<(MeshEntry entry, Mesh resultMesh)> entries,
            List<Mesh> tempMeshes, HierarchyExportBatch batch)
        {
            var lastLodRendererTemplate = FbxExport.FindLastLodRenderer(entries);

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
            int collisionMeshCount = FbxExport.InjectCollisionMeshes(tempRoot, SidecarStore.CollisionMeshes(sourceFbxPath), tempMeshes);
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

        void SaveAll()
        {
            RestoreAllPreviews();
            string p = ctx.PipeSettings.savePath;
            if (string.IsNullOrEmpty(p)) p = "Assets/UnityMeshLab/Output";
            if (!AssetDatabase.IsValidFolder(p))
            {
                var par = System.IO.Path.GetDirectoryName(p);
                var fld = System.IO.Path.GetFileName(p);
                if (!string.IsNullOrEmpty(par)) AssetDatabase.CreateFolder(par, fld);
            }
            // CreateFolder only makes one level; a deeper missing savePath or a
            // locked parent leaves the folder absent and every generated path
            // invalid — bail out with a readable error instead of throwing
            // inside OnGUI.
            if (!AssetDatabase.IsValidFolder(p))
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
