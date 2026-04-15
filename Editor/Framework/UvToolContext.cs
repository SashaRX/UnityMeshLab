// UvToolContext.cs — Shared state container accessible by all tools.
// Holds LODGroup, mesh entries, pipeline settings, and shared caches.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    /// <summary>
    /// Shared state container — one instance per <see cref="UvToolHub"/> window.
    /// Created in <c>OnEnable</c>, populated by <see cref="Refresh"/>.
    /// All tools receive a reference via <see cref="IUvTool.OnActivate"/>.
    /// </summary>
    public class UvToolContext
    {
        // ── Selection ──
        public LODGroup LodGroup;
        public int SourceLodIndex;
        public List<MeshEntry> MeshEntries = new List<MeshEntry>();

        // ── Settings ──
        public UvTransferPipeline.PipelineSettings PipeSettings =
            UvTransferPipeline.PipelineSettings.Default;

        public int AtlasResolution = 256;
        public int ShellPaddingPx  = 2;
        public int BorderPaddingPx = 0;
        public bool RepackPerMesh;
        public int IsolatedMeshGroup = -1;
        public string IsolatedMeshGroupKey;

        // ── Canvas state ──
        public int PreviewUvChannel = 1;
        public int PreviewLod;

        // ── Caches (shared across tools) ──
        // All caches are keyed by mesh instance ID or composite key (ID + channel).
        // They become stale when mesh topology changes (weld, symmetry-split,
        // repack, transfer) or when the LODGroup selection changes.
        // ClearAllCaches() is the safe invalidation path after any mesh mutation;
        // Refresh() also clears all caches as part of full re-initialization.
        public readonly FaceToShellCache UvPreviewShellCache = new FaceToShellCache();
        public readonly Dictionary<int, SourceMeshData> SrcCache = new Dictionary<int, SourceMeshData>();
        public readonly Dictionary<int, int[]> BoundaryEdgeCache = new Dictionary<int, int[]>();
        public readonly Dictionary<long, PreviewShellData> PreviewShellDataCache = new Dictionary<long, PreviewShellData>();
        public readonly Dictionary<long, HashSet<Vector2Int>> OccupiedTilesPerMesh = new Dictionary<long, HashSet<Vector2Int>>();
        public readonly Dictionary<long, int> ShellColorKeyCache = new Dictionary<long, int>();
        public readonly Dictionary<int, (int[] vertToShell, ShellDescriptor[] descs)> Uv0ShellMapCache
            = new Dictionary<int, (int[] vertToShell, ShellDescriptor[] descs)>();

        /// <summary>Lazy-invalidation flag for shell color fill mode. Set true on any cache clear; checked by the canvas fill renderer.</summary>
        public bool ShellColorKeyCacheDirty = true;
        public bool PostResetColoring;

        // ── Pipeline state ──
        /// <summary>True after successful UV2 repack of the source LOD. Enables the Transfer tab and affects <see cref="DMesh"/> selection. Reset on Refresh or ResetPipelineState.</summary>
        public bool HasRepack;
        /// <summary>True after successful UV2 transfer to target LODs. Enables the Apply button. Reset on Refresh or ResetPipelineState.</summary>
        public bool HasTransfer;

        /// <summary>
        /// Source FBX asset path resolved during Refresh. Persists across mesh modifications
        /// (merge, split, weld) so export can always find the target FBX file.
        /// </summary>
        public string SourceFbxPath;

        // ── Events ──
        public event Action OnMeshEntriesChanged;
        public event Action OnSelectionChanged;

        // ── Helpers ──
        public List<MeshEntry> ForLod(int li) => MeshEntries.Where(e => e.lodIndex == li && e.include).ToList();
        public int LodCount => LodGroup != null ? LodGroup.GetLODs().Length : StandaloneMesh ? 1 : 0;

        /// <summary>True when displaying a standalone MeshRenderer without LODGroup.</summary>
        public bool StandaloneMesh;

        /// <summary>
        /// Strip trailing LOD/COL suffixes to get a stable group key.
        /// </summary>
        public static string ExtractGroupKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return System.Text.RegularExpressions.Regex.Replace(
                name,
                @"(?:[_\-\s]+(?:LOD\d+|COL(?:_Hull\d+)?|Collider|Collision))+$",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Remove LOD slots whose renderers are all null or empty.
        /// Also strips null renderers from remaining slots.
        /// </summary>
        public static bool CompactLodArray(LODGroup lodGroup)
        {
            if (lodGroup == null) return false;
            var lods = lodGroup.GetLODs();
            var compacted = new List<LOD>();
            bool changed = false;
            foreach (var lod in lods)
            {
                if (lod.renderers == null || lod.renderers.Length == 0)
                { changed = true; continue; }
                var valid = lod.renderers.Where(r => r != null).ToArray();
                if (valid.Length == 0)
                { changed = true; continue; }
                if (valid.Length != lod.renderers.Length) changed = true;
                compacted.Add(new LOD(lod.screenRelativeTransitionHeight, valid));
            }
            if (changed)
            {
                Undo.RecordObject(lodGroup, "Compact LOD Array");
                lodGroup.SetLODs(compacted.ToArray());
                UvtLog.Info($"[Context] Compacted LOD array: {lods.Length} → {compacted.Count} slots.");
            }
            return changed;
        }

        public void Refresh(LODGroup lodGroup)
        {
            MeshEntries.Clear();
            HasRepack = HasTransfer = false;
            SrcCache.Clear();
            BoundaryEdgeCache.Clear();
            UvPreviewShellCache.Clear();
            PreviewShellDataCache.Clear();
            OccupiedTilesPerMesh.Clear();
            ShellColorKeyCache.Clear();
            Uv0ShellMapCache.Clear();
            ShellColorKeyCacheDirty = true;

            LodGroup = lodGroup;
            StandaloneMesh = false;
            if (LodGroup == null) return;

            CompactLodArray(LodGroup);
            var lods = LodGroup.GetLODs();
            for (int li = 0; li < lods.Length; li++)
            {
                if (lods[li].renderers == null) continue;
                foreach (var r in lods[li].renderers)
                {
                    if (r == null) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var fbm = mf.sharedMesh;
                    var uv2Check = new List<Vector2>();
                    fbm.GetUVs(1, uv2Check);
                    MeshEntries.Add(new MeshEntry
                    {
                        lodIndex = li,
                        renderer = r,
                        meshFilter = mf,
                        originalMesh = fbm,
                        fbxMesh = fbm,
                        hasExistingUv2 = uv2Check.Count > 0,
                        meshGroupKey = ExtractGroupKey(r.name)
                    });
                }
            }

            // Resolve source FBX path (survives mesh modifications like merge/split)
            if (string.IsNullOrEmpty(SourceFbxPath) || !System.IO.File.Exists(SourceFbxPath))
            {
                SourceFbxPath = null;
                foreach (var e in MeshEntries)
                {
                    if (e.fbxMesh == null) continue;
                    string p = UnityEditor.AssetDatabase.GetAssetPath(e.fbxMesh);
                    if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    { SourceFbxPath = p; break; }
                }
                // Fallback: prefab source
                if (string.IsNullOrEmpty(SourceFbxPath))
                {
                    foreach (var r in LodGroup.GetComponentsInChildren<Renderer>(true))
                    {
                        var src = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(r);
                        if (src == null) continue;
                        string p = UnityEditor.AssetDatabase.GetAssetPath(src);
                        if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                        { SourceFbxPath = p; break; }
                    }
                }
            }

            OnMeshEntriesChanged?.Invoke();
        }

        /// <summary>
        /// Populate context from a single MeshRenderer without LODGroup (view-only UV inspection).
        /// </summary>
        public void RefreshStandalone(MeshRenderer mr)
        {
            MeshEntries.Clear();
            HasRepack = HasTransfer = false;
            SrcCache.Clear();
            BoundaryEdgeCache.Clear();
            UvPreviewShellCache.Clear();
            PreviewShellDataCache.Clear();
            OccupiedTilesPerMesh.Clear();
            ShellColorKeyCache.Clear();
            Uv0ShellMapCache.Clear();
            ShellColorKeyCacheDirty = true;

            LodGroup = null;
            StandaloneMesh = false;
            if (mr == null) return;

            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;

            var fbm = mf.sharedMesh;
            var uv2Check = new List<Vector2>();
            fbm.GetUVs(1, uv2Check);
            MeshEntries.Add(new MeshEntry
            {
                lodIndex = 0,
                renderer = mr,
                meshFilter = mf,
                originalMesh = fbm,
                fbxMesh = fbm,
                hasExistingUv2 = uv2Check.Count > 0,
                meshGroupKey = ExtractGroupKey(mr.name)
            });
            StandaloneMesh = true;
            PreviewLod = 0;
            SourceLodIndex = 0;

            // Resolve source FBX path
            if (string.IsNullOrEmpty(SourceFbxPath) || !System.IO.File.Exists(SourceFbxPath))
            {
                SourceFbxPath = null;
                string p = UnityEditor.AssetDatabase.GetAssetPath(fbm);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    SourceFbxPath = p;
            }

            OnMeshEntriesChanged?.Invoke();
        }

        public void ClearAllCaches()
        {
            SrcCache.Clear();
            BoundaryEdgeCache.Clear();
            UvPreviewShellCache.Clear();
            PreviewShellDataCache.Clear();
            OccupiedTilesPerMesh.Clear();
            ShellColorKeyCache.Clear();
            Uv0ShellMapCache.Clear();
            ShellColorKeyCacheDirty = true;
        }

        /// <summary>Count unique mesh groups for a LOD level.</summary>
        public int MeshGroupCount(int lod)
        {
            var seen = new HashSet<string>();
            foreach (var e in MeshEntries)
                if (e.lodIndex == lod && e.include)
                    seen.Add(e.meshGroupKey ?? e.renderer.name);
            return seen.Count;
        }

        /// <summary>Build ordered list of unique mesh group keys for a LOD.</summary>
        public List<string> BuildGroupKeys(int lod)
        {
            var keys = new List<string>();
            foreach (var e in MeshEntries.Where(me => me.lodIndex == lod && me.include))
            {
                string key = e.meshGroupKey ?? e.renderer.name;
                if (!keys.Contains(key)) keys.Add(key);
            }
            return keys;
        }

        /// <summary>
        /// Returns the display mesh for a MeshEntry depending on pipeline state.
        /// Priority: repackedMesh for source LOD when viewing UV2, transferredMesh
        /// for target LODs when viewing UV2, otherwise originalMesh.
        /// </summary>
        public Mesh DMesh(MeshEntry e)
        {
            if (e.lodIndex == SourceLodIndex && e.repackedMesh != null && PreviewUvChannel == 1) return e.repackedMesh;
            if (e.lodIndex != SourceLodIndex && e.transferredMesh != null && PreviewUvChannel == 1) return e.transferredMesh;
            return e.originalMesh;
        }

        public void FireSelectionChanged() => OnSelectionChanged?.Invoke();
    }
}
