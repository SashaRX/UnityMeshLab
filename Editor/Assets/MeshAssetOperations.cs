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
                    FbxExport.ReimportWithoutSidecars(p, imp.SaveAndReimport);
                }
                foreach (var e in ctx.MeshEntries)
                {
                    if (e.meshFilter != null && e.meshFilter.sharedMesh != null)
                        e.fbxMesh = e.meshFilter.sharedMesh;
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
                var entries = kv.Value.Select(p => p.entry.PreviewCopy(p.resultMesh)).ToList();
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
        // A persistent sidecar re-applies its UV2 on every import; after a save wrote the
        // meshes' UVs into the file, its entries must hold the same UVs or the next import
        // would bring the old ones back.
        void SyncPersistentSidecar(string sourceFbxPath, List<(MeshEntry entry, Mesh resultMesh)> group)
        {
            if (!PostprocessorDefineManager.IsEnabled() || SidecarStore.Load(sourceFbxPath) == null) return;
            int saved = SidecarStore.SaveEntries(sourceFbxPath, BuildSidecarEntriesForExport(group));
            if (saved > 0) UvtLog.Info($"[FBX Export] Updated {saved} UV2 entr(ies) in '{SidecarStore.PathFor(sourceFbxPath)}' to match the saved FBX.");
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
            foreach (var kv in fbxGroups)
            {
                string sourceFbxPath = kv.Key;
                using var plan = FbxStructureWrite.Plan(sourceFbxPath, kv.Value, ctx.SourceLodIndex);
                if (!TryChooseNarrowExportPath(sourceFbxPath, FbxExportIntent.All, overwriteSource, out string outputFbxPath)) continue;
                bool isVariant = !string.IsNullOrEmpty(outputFbxPath)
                    && !string.Equals(outputFbxPath, sourceFbxPath, StringComparison.OrdinalIgnoreCase);
                var entries = kv.Value.Select(p => p.entry.PreviewCopy(p.resultMesh)).ToList();
                RestoreAllPreviews();

                bool written;
                try
                {
                    written = FbxChannelWrite.Write(sourceFbxPath, entries, FbxChannelWrite.Supported, outputFbxPath,
                        isVariant ? null : ctx?.LodGroup, plan);
                }
                catch (FbxStructureRefusal refusal)
                {
                    written = ExportAfterRefusal(sourceFbxPath, kv.Value, entries, outputFbxPath, overwriteSource, refusal.Message);
                    if (written) okCount++;
                    continue;
                }
                catch (Exception ex)
                {
                    UvtLog.Error($"[FBX Export] '{sourceFbxPath}' was not written: {ex.Message}");
                    UvtLog.Verbose(ex.ToString());
                    continue;
                }
                if (!written) continue;
                okCount++;
                if (isVariant) continue;
                SyncPersistentSidecar(sourceFbxPath, kv.Value);
                if (plan.lods.Count > 0) LodGroupUtility.AdoptImportedLods(ctx);
                if (ctx?.LodGroup != null) ctx.Refresh(ctx.LodGroup);
                RestoreWorkingCopiesToScene();
                AfterWrite?.Invoke();
            }
            UvtLog.Info($"[FBX Export] FBX document save: {okCount}/{fbxGroups.Count} file(s) written.");
        }

        // The document edit refused the structure (the file is untouched): say why and let the
        // user pick the rebuild, the channels alone, or nothing.
        bool ExportAfterRefusal(string sourceFbxPath, List<(MeshEntry entry, Mesh resultMesh)> group, List<MeshEntry> entries,
            string outputFbxPath, bool overwriteSource, string reason)
        {
            UvtLog.Warn($"[FBX Export] '{sourceFbxPath}': {reason}.");
            int choice = EditorUtility.DisplayDialogComplex("Rebuild FBX?",
                $"'{System.IO.Path.GetFileName(sourceFbxPath)}' cannot take this save as an edit of the file:\n\n{reason}.\n\n" +
                "A rebuild re-exports every mesh from Unity's data through the FBX Exporter: polygons become triangles, " +
                "vertices are split at seams, vertex colours are stored as 8-bit, and the hierarchy is normalised.\n\n" +
                "'Save channels only' writes the changed UV sets and vertex colours into the existing file and leaves the rest of it untouched.",
                "Rebuild", CancelButton, "Save channels only");
            if (choice == 1) return false;
            if (choice == 2) return ExportFbxIsolatedCore(sourceFbxPath, entries, FbxChannelWrite.Supported, outputFbxPath);
            var batch = new HierarchyExportBatch();
            bool ok = ExportHierarchyGroup(sourceFbxPath, group, overwriteSource, batch);
            FinishHierarchyExports(batch, overwriteSource, ok);
            return ok;
        }

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
        internal sealed class HierarchyExportBatch
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
            return ExportHierarchyGroupPrepared(sourceFbxPath, entries, overwriteSource, batch, exportPath, tempDir, fullSourcePath, fbxBakName);
        }

        internal Action<string, GameObject> HierarchyWriter;
        internal Func<string, GameObject> HierarchyPrefabLoader;

        internal bool ExportHierarchyGroupPrepared(string sourceFbxPath, List<(MeshEntry entry, Mesh resultMesh)> entries,
            bool overwriteSource, HierarchyExportBatch batch, string exportPath, string tempDir, string fullSourcePath, string backupName)
        {
            bool written = false, failed = false, madeReadable = false;
            GameObject tempRoot = null;
            ModelImporter importer = null;
            var tempMeshes = new List<Mesh>();
            try
            {
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
                tempRoot = UnityEngine.Object.Instantiate(prefab);
                tempRoot.name = prefab.name;
                FbxExport.PromoteRootMeshToLod0Child(tempRoot);
                int collisions = PrepareExportHierarchy(tempRoot, sourceFbxPath, entries, tempMeshes, batch);
                WriteHierarchyExport(exportPath, tempRoot);
                written = true;
                if (overwriteSource) batch.OverwrittenFbxPaths.Add(sourceFbxPath);
                UvtLog.Info($"[FBX Export] Exported (binary) {entries.Count + collisions} mesh(es) -> {exportPath}");
            }
            catch (Exception ex) { UvtLog.Error("[FBX Export] Export failed: " + ex); failed = true; }
            finally
            {
                if (tempRoot != null) UnityEngine.Object.DestroyImmediate(tempRoot);
                FbxExport.DestroyTempMeshes(tempMeshes);
                if (overwriteSource)
                    failed |= !RestoreHierarchyBackup(tempDir, backupName, fullSourcePath, sourceFbxPath, written);
                if (madeReadable && importer != null)
                    failed |= !RestoreHierarchyReadability(importer, sourceFbxPath);
            }
            // A completed write needs replay even if restoring its importer metadata failed.
            if (overwriteSource && written) StoreHierarchyExportSidecars(sourceFbxPath, entries, true, batch);
            return written && !failed;
        }

        void WriteHierarchyExport(string path, GameObject root)
        {
            if (HierarchyWriter != null) HierarchyWriter(path, root);
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
                if (System.IO.File.Exists(metaBackup)) System.IO.File.Delete(metaBackup);
                if (System.IO.File.Exists(fbxBackup)) System.IO.File.Delete(fbxBackup);
                return true;
            }
            catch (Exception ex) {
                UvtLog.Error($"[FBX Export] Backup restore failed; copies kept in {directory}: {ex.Message}");
                return false;
            }
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
