// UvToolContext.cs — Shared state container accessible by all tools.
// Holds LODGroup, mesh entries, pipeline settings, and shared caches.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
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

        // ── Canvas state ──
        public int PreviewUvChannel = 1;
        public int PreviewLod;

        // ── Caches (shared across tools) ──
        public readonly FaceToShellCache UvPreviewShellCache = new FaceToShellCache();
        public readonly Dictionary<int, SourceMeshData> SrcCache = new Dictionary<int, SourceMeshData>();
        public readonly Dictionary<int, int[]> BoundaryEdgeCache = new Dictionary<int, int[]>();
        public readonly Dictionary<long, PreviewShellData> PreviewShellDataCache = new Dictionary<long, PreviewShellData>();
        public readonly Dictionary<long, HashSet<Vector2Int>> OccupiedTilesPerMesh = new Dictionary<long, HashSet<Vector2Int>>();
        public readonly Dictionary<long, int> ShellColorKeyCache = new Dictionary<long, int>();
        public readonly Dictionary<int, (int[] vertToShell, ShellDescriptor[] descs)> Uv0ShellMapCache
            = new Dictionary<int, (int[] vertToShell, ShellDescriptor[] descs)>();

        public bool ShellColorKeyCacheDirty = true;
        public bool PostResetColoring;

        // ── Pipeline state ──
        public bool HasRepack, HasTransfer;

        // ── Events ──
        public event Action OnMeshEntriesChanged;
        public event Action OnSelectionChanged;

        // ── Helpers ──
        public List<MeshEntry> ForLod(int li) => MeshEntries.Where(e => e.lodIndex == li && e.include).ToList();
        public int LodCount => LodGroup != null ? LodGroup.GetLODs().Length : 0;

        /// <summary>
        /// Strip trailing LOD/COL suffixes to get a stable group key.
        /// </summary>
        public static string ExtractGroupKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return System.Text.RegularExpressions.Regex.Replace(
                name,
                @"[_\-\s]+(LOD\d+|COL\w*|Collision)$",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
            if (LodGroup == null) return;

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

        /// <summary>Returns the display mesh for a MeshEntry depending on pipeline state.</summary>
        public Mesh DMesh(MeshEntry e)
        {
            if (e.lodIndex == SourceLodIndex && e.repackedMesh != null && PreviewUvChannel == 1) return e.repackedMesh;
            if (e.lodIndex != SourceLodIndex && e.transferredMesh != null && PreviewUvChannel == 1) return e.transferredMesh;
            return e.originalMesh;
        }

        public void FireSelectionChanged() => OnSelectionChanged?.Invoke();
    }
}
