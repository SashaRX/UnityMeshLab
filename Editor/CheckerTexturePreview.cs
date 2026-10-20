// CheckerTexturePreview.cs — Procedural checker texture with cell labels + 3D preview.
// Generates a colored 8×8 grid with alphanumeric labels (A1, B2 ...) to verify
// UV2 mapping on 3D models. Applies via the CheckerUV2 shader using TEXCOORD2.
// Temporarily swaps both materials AND meshes (to inject UV2 working copies),
// restoring everything on disable.

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Safety hook: restores all preview materials on domain reload, play mode change,
    /// scene save, prefab save, and editor quit. Prevents checker/shell materials from
    /// leaking onto models in saved scenes or prefab assets.
    /// </summary>
    [InitializeOnLoad]
    static class PreviewSafetyGuard
    {
        static PreviewSafetyGuard()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            PrefabStage.prefabSaving += OnPrefabSaving;
            EditorApplication.quitting += OnQuitting;
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode ||
                state == PlayModeStateChange.ExitingPlayMode)
                RestoreAll();
        }

        static void OnBeforeAssemblyReload() => ReleaseResources();
        static void OnQuitting() => ReleaseResources();

        internal static void ReleaseResources()
        {
            var prefabPreview = PrefabBuilderPreview.ActiveInstance;
            RestoreAll();
            prefabPreview?.Dispose();
            CheckerTexturePreview.ReleaseResources();
            ShellColorModelPreview.ReleaseResources();
            UvPngWriter.ReleaseResources();
        }

        static void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            RestoreAll();
        }

        static void OnSceneClosing(UnityEngine.SceneManagement.Scene scene, bool removingScene)
        {
            RestoreAll();
        }

        // Saving a prefab in Prefab Mode does NOT trigger sceneSaving, so without
        // this hook the checker/shell preview material would get baked into the
        // saved prefab asset — visually "breaking" the mesh's material.
        static void OnPrefabSaving(GameObject prefabRoot)
        {
            RestoreAll();
        }

        internal static void RestoreAll()
        {
            if (CheckerTexturePreview.IsActive)
                CheckerTexturePreview.Restore();
            if (ShellColorModelPreview.IsActive)
                ShellColorModelPreview.Restore();

            // Prefab Builder preview is per-tool instance — static guard.
            PrefabBuilderPreview.RestoreIfActive();

            // Lightmap preview is per-window instance — find open Mesh Lab windows and restore.
            var hubs = Resources.FindObjectsOfTypeAll<UvToolHub>();
            foreach (var h in hubs)
                h.RestoreLightmapPreviewSafe();
        }

        /// <summary>Returns live renderer slots with this preview replaced by captured materials.</summary>
        /// <remarks>Preserves later assignments, null slots and changes to the live slot count.</remarks>
        internal static Material[] MaterialsForWrite(Renderer renderer, Material[] captured, Material preview)
            => MaterialsForWrite(renderer.sharedMaterials, captured, preview);

        /// <summary>Replaces matching preview slots in the supplied array with captured materials, in place.</summary>
        /// <returns>The supplied array, preserving slots no longer owned by this preview.</returns>
        internal static Material[] MaterialsForWrite(Material[] materials, Material[] captured, Material preview)
        {
            if (preview == null) return materials;
            for (int i = 0; i < materials.Length && i < captured.Length; i++)
                if (materials[i] == preview) materials[i] = captured[i];
            return materials;
        }

        /// <summary>
        /// A preview material and the slots it replaced, which may contain an older preview layer.
        /// </summary>
        internal readonly struct MaterialLayer
        {
            internal readonly Material preview;
            internal readonly Material[] captured;

            /// <summary>Pairs a preview material with the material slots captured before it was applied.</summary>
            /// <param name="preview">The material installed by this preview layer.</param>
            /// <param name="captured">The prior slots, including any underlying preview materials.</param>
            internal MaterialLayer(Material preview, Material[] captured)
            {
                this.preview = preview;
                this.captured = captured;
            }
        }

        /// <summary>
        /// Resolves stacked preview materials transitively without changing the input slots or backups.
        /// </summary>
        /// <param name="current">The renderer's live material slots.</param>
        /// <param name="layers">Known preview materials and their captured slots, in any apply order.</param>
        /// <param name="forAssetWrite">
        /// When true, starts from live slots to preserve later assignments; otherwise starts from an authored snapshot.
        /// </param>
        /// <returns>
        /// A resolved copy of the selected slots, or the current array when no layers or source slots are available.
        /// Slots without a captured counterpart are left unchanged.
        /// </returns>
        internal static Material[] ResolveOriginalMaterials(
            Material[] current, List<MaterialLayer> layers, bool forAssetWrite)
        {
            if (layers == null || layers.Count == 0) return current;

            Material[] source = forAssetWrite ? current : AuthoredSnapshot(current, layers);
            if (source == null) return current;

            var materials = (Material[])source.Clone();
            for (int pass = 0; pass <= layers.Count; pass++)
                if (!ReplacePreviewSlots(materials, layers)) break;
            return materials;
        }

        /// <summary>Replaces matching preview slots in place once for each known layer.</summary>
        /// <returns>True if any slot changed; further passes may be needed to unwind nested previews.</returns>
        static bool ReplacePreviewSlots(Material[] materials, List<MaterialLayer> layers)
        {
            bool changed = false;
            foreach (var layer in layers)
                changed |= ReplacePreviewSlots(materials, layer);
            return changed;
        }

        /// <summary>Replaces slots still owned by a layer with its captured values, leaving other slots intact.</summary>
        /// <returns>True if at least one slot changed; false for unusable or self-referencing captures.</returns>
        static bool ReplacePreviewSlots(Material[] materials, MaterialLayer layer)
        {
            if (layer.preview == null || layer.captured == null) return false;
            bool changed = false;
            int slots = Mathf.Min(materials.Length, layer.captured.Length);
            for (int i = 0; i < slots; i++)
            {
                if (materials[i] != layer.preview) continue;
                Material authored = layer.captured[i];
                if (authored == layer.preview) continue;
                materials[i] = authored;
                changed = true;
            }
            return changed;
        }

        static int lightmapPreviewLayers;

        /// <summary>
        /// Whether tracked lightmap layers require a global window scan when resolving preview materials.
        /// </summary>
        internal static bool HasLightmapPreviewLayers => lightmapPreviewLayers > 0;

        /// <summary>Registers one lightmap preview layer so material resolution scans open windows.</summary>
        internal static void AddLightmapPreviewLayer() => lightmapPreviewLayers++;

        /// <summary>Unregisters one lightmap preview layer without letting the count fall below zero.</summary>
        internal static void RemoveLightmapPreviewLayer()
        {
            if (lightmapPreviewLayers > 0) lightmapPreviewLayers--;
        }

        /// <summary>Selects the captured slots of the layer matching the most live material slots.</summary>
        /// <returns>
        /// The selected capture by reference, taking the first on ties. If no preview matches,
        /// returns the first non-null capture, or the current slots when no capture exists.
        /// </returns>
        static Material[] AuthoredSnapshot(Material[] current, List<MaterialLayer> layers)
        {
            // The top layer is the one whose preview material is still visible.
            // Its snapshot is one step below it; resolving every known preview
            // material walks the remaining layers back to the authored slots.
            Material[] source = null;
            int bestMatches = 0;
            foreach (var layer in layers)
            {
                if (layer.preview == null || layer.captured == null) continue;
                int matches = 0;
                for (int i = 0; i < current.Length; i++)
                    if (current[i] == layer.preview) matches++;
                if (matches > bestMatches)
                {
                    source = layer.captured;
                    bestMatches = matches;
                }
            }
            if (source != null) return source;
            int at = layers.FindIndex(layer => layer.captured != null);
            return at >= 0 ? layers[at].captured : current;
        }
    }

    public static class CheckerTexturePreview
    {
        // ── Shader name constants for preview material detection ──
        internal const string ToolShaderPrefix = "Hidden/UnityMeshLab/";
        internal const string VertexColorShader = "Hidden/Internal-Colored";

        /// <summary>
        /// Returns true if the shader name belongs to a preview material
        /// (tool overlays or vertex-color previews) that should not be
        /// serialized into scenes, prefabs, or FBX exports.
        /// </summary>
        internal static bool IsPreviewShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            return shaderName.Equals(VertexColorShader, System.StringComparison.OrdinalIgnoreCase)
                || shaderName.StartsWith(ToolShaderPrefix, System.StringComparison.OrdinalIgnoreCase);
        }

        // ── Generated assets ──
        static Texture2D checkerTex;
        static Material  checkerMat;

        // ── Backup for restore ──
        struct RendererBackup
        {
            public Renderer renderer;
            public Material[] origMaterials;
            public MeshFilter meshFilter;    // null for SkinnedMeshRenderer
            public Mesh origMesh;            // original mesh to restore
            public Mesh previewMesh;         // restore only while this override is still assigned
        }
        static List<RendererBackup> backups = new List<RendererBackup>();
        static bool isActive;

        public static bool IsActive => isActive;

        /// <summary>Tries to read the renderer's captured slots for the checker preview.</summary>
        /// <param name="renderer">The renderer whose preview backup is requested.</param>
        /// <param name="materials">Captured slots, or live slots with this preview removed for asset writes; null if no backup exists.</param>
        /// <param name="forAssetWrite">Whether to preserve assignments made after the preview was applied.</param>
        /// <returns>True if a backup exists for the renderer.</returns>
        internal static bool TryGetOriginalMaterials(Renderer renderer, out Material[] materials, bool forAssetWrite = false)
        {
            foreach (var backup in backups)
                if (backup.renderer == renderer)
                {
                    materials = forAssetWrite
                        ? PreviewSafetyGuard.MaterialsForWrite(renderer, backup.origMaterials, checkerMat)
                        : backup.origMaterials;
                    return true;
                }
            materials = null;
            return false;
        }

        /// <summary>Appends the checker preview layer captured for the renderer, if one exists.</summary>
        /// <param name="renderer">The renderer whose preview backup is requested.</param>
        /// <param name="layers">The destination list; existing entries are retained.</param>
        internal static void CollectMaterialLayers(
            Renderer renderer, List<PreviewSafetyGuard.MaterialLayer> layers)
        {
            foreach (var backup in backups)
                if (backup.renderer == renderer)
                {
                    layers.Add(new PreviewSafetyGuard.MaterialLayer(checkerMat, backup.origMaterials));
                    return;
                }
        }

        /// <summary>
        /// Returns the procedural checker texture used in Scene preview.
        /// Ensures texture is generated before returning.
        /// </summary>
        public static Texture2D GetCheckerTexture()
        {
            EnsureAssets();
            return checkerTex;
        }

        // ═══════════════════════════════════════════════════════════
        //  Apply / Restore
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Apply checker preview. Each entry = (renderer, meshWithUv2 or null to keep current).
        /// uvChannel selects which TEXCOORD the shader reads (0-7, default 1 = UV2).
        /// colorMode: true = display UV values as RGB color, false = checker texture.
        /// showR/showG: channel mask for color mode.
        /// </summary>
        public static void Apply(List<(Renderer renderer, Mesh meshWithUv2)> entries,
            int uvChannel = 1, bool colorMode = false, bool showR = true, bool showG = true)
        {
            if (isActive) Restore();

            EnsureAssets();
            backups.Clear();

            checkerMat.SetFloat("_UVChannel", uvChannel);
            checkerMat.SetFloat("_ColorMode", colorMode ? 1f : 0f);
            checkerMat.SetFloat("_ShowR", showR ? 1f : 0f);
            checkerMat.SetFloat("_ShowG", showG ? 1f : 0f);

            var captured = new Dictionary<Renderer, int>();
            foreach (var (r, uvMesh) in entries)
            {
                if (r == null) continue;

                bool alreadyCaptured = captured.TryGetValue(r, out int at);
                var backup = alreadyCaptured ? backups[at] : new RendererBackup
                {
                    renderer = r,
                    origMaterials = r.sharedMaterials
                };

                // Swap mesh if we have a UV2 working copy
                if (uvMesh != null)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null)
                    {
                        if (backup.meshFilter == null) backup.origMesh = mf.sharedMesh;
                        backup.meshFilter = mf;
                        backup.previewMesh = uvMesh;
                        mf.sharedMesh = uvMesh;
                    }
                }

                // A renderer can occur in more than one entry. Keep its first
                // authored state while the last supplied preview mesh wins.
                if (alreadyCaptured) backups[at] = backup;
                else { captured.Add(r, backups.Count); backups.Add(backup); }

                // Replace all material slots with checker
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = checkerMat;
                r.sharedMaterials = mats;
            }

            isActive = true;
            SceneView.RepaintAll();
        }

        public static void Restore()
        {
            foreach (var b in backups)
            {
                if (b.renderer != null)
                    b.renderer.sharedMaterials = b.origMaterials;
                if (b.meshFilter != null && b.origMesh != null && b.meshFilter.sharedMesh == b.previewMesh)
                    b.meshFilter.sharedMesh = b.origMesh;
            }
            backups.Clear();
            isActive = false;
            SceneView.RepaintAll();
        }

        // ═══════════════════════════════════════════════════════════
        //  Asset creation
        // ═══════════════════════════════════════════════════════════

        static void EnsureAssets()
        {
            if (checkerTex == null)
                checkerTex = GenerateCheckerTexture(1024, 8);

            if (checkerMat == null)
            {
                var sh = Shader.Find("Hidden/UnityMeshLab/CheckerUV2");
                if (sh == null)
                {
                    UvtLog.Error("[Checker] Shader 'Hidden/UnityMeshLab/CheckerUV2' not found");
                    return;
                }
                checkerMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                checkerMat.mainTexture = checkerTex;
            }
        }

        internal static void ReleaseResources()
        {
            Restore();
            if (checkerMat != null) Object.DestroyImmediate(checkerMat);
            if (checkerTex != null) Object.DestroyImmediate(checkerTex);
            checkerMat = null;
            checkerTex = null;
        }

        // ═══════════════════════════════════════════════════════════
        //  Texture Generation
        // ═══════════════════════════════════════════════════════════

        static readonly Color[] cellColors =
        {
            new Color(0.85f, 0.25f, 0.25f), // red
            new Color(0.25f, 0.65f, 0.25f), // green
            new Color(0.25f, 0.40f, 0.85f), // blue
            new Color(0.85f, 0.75f, 0.20f), // yellow
            new Color(0.70f, 0.30f, 0.80f), // purple
            new Color(0.20f, 0.75f, 0.80f), // cyan
            new Color(0.90f, 0.50f, 0.20f), // orange
            new Color(0.60f, 0.80f, 0.30f), // lime
            new Color(0.80f, 0.40f, 0.60f), // pink
            new Color(0.40f, 0.55f, 0.80f), // steel
            new Color(0.75f, 0.60f, 0.35f), // tan
            new Color(0.45f, 0.75f, 0.55f), // mint
        };

        static Texture2D GenerateCheckerTexture(int resolution, int gridSize)
        {
            var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            int cellPx = resolution / gridSize;
            const int fineCellsPerCell = 8;
            int fineCellPx = Mathf.Max(1, cellPx / fineCellsPerCell);
            var pixels = new Color32[resolution * resolution];

            // Fill cells
            for (int cy = 0; cy < gridSize; cy++)
            {
                for (int cx = 0; cx < gridSize; cx++)
                {
                    int idx = (cy * gridSize + cx) % cellColors.Length;
                    bool dark = (cx + cy) % 2 == 0;
                    Color baseCol = cellColors[idx] * 0.78f;
                    if (dark) baseCol *= 0.86f;
                    baseCol.a = 1f;
                    int x0 = cx * cellPx, y0 = cy * cellPx;

                    for (int py = y0; py < y0 + cellPx && py < resolution; py++)
                    {
                        for (int px = x0; px < x0 + cellPx && px < resolution; px++)
                        {
                            int localX = (px - x0) / fineCellPx;
                            int localY = (py - y0) / fineCellPx;
                            bool fineDark = ((localX + localY) & 1) == 0;
                            float fineMul = fineDark ? 0.9f : 1.06f;
                            Color c = baseCol * fineMul;
                            c.a = 1f;
                            pixels[py * resolution + px] = (Color32)c;
                        }
                    }

                    // Cell border (1px dark line)
                    Color32 border = new Color32(20, 20, 20, 255);
                    for (int px = x0; px < x0 + cellPx && px < resolution; px++)
                    {
                        pixels[y0 * resolution + px] = border;
                        int yEnd = Mathf.Min(y0 + cellPx - 1, resolution - 1);
                        pixels[yEnd * resolution + px] = border;
                    }
                    for (int py = y0; py < y0 + cellPx && py < resolution; py++)
                    {
                        pixels[py * resolution + x0] = border;
                        int xEnd = Mathf.Min(x0 + cellPx - 1, resolution - 1);
                        pixels[py * resolution + xEnd] = border;
                    }
                }
            }

            tex.SetPixels32(pixels);

            // Draw labels
            for (int cy = 0; cy < gridSize; cy++)
            {
                for (int cx = 0; cx < gridSize; cx++)
                {
                    char colChar = (char)('A' + cx);
                    char rowChar = (char)('1' + cy);
                    string label = "" + colChar + rowChar;

                    int x0 = cx * cellPx;
                    int y0 = cy * cellPx;
                    int scale = Mathf.Max(1, cellPx / 64);
                    // Center label in cell
                    int lx = x0 + cellPx / 2 - (label.Length * 6 * scale) / 2;
                    int ly = y0 + cellPx / 2 - 4 * scale;

                    DrawString(tex, label, lx, ly, scale, Color.white);
                }
            }

            tex.Apply();
            return tex;
        }

        // ═══════════════════════════════════════════════════════════
        //  Tiny bitmap font (5×7 per glyph)
        // ═══════════════════════════════════════════════════════════

        static readonly Dictionary<char, byte[]> font = new Dictionary<char, byte[]>
        {
            {'0', new byte[]{0x0E,0x11,0x13,0x15,0x19,0x11,0x0E}},
            {'1', new byte[]{0x04,0x0C,0x04,0x04,0x04,0x04,0x0E}},
            {'2', new byte[]{0x0E,0x11,0x01,0x02,0x04,0x08,0x1F}},
            {'3', new byte[]{0x1F,0x02,0x04,0x02,0x01,0x11,0x0E}},
            {'4', new byte[]{0x02,0x06,0x0A,0x12,0x1F,0x02,0x02}},
            {'5', new byte[]{0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E}},
            {'6', new byte[]{0x06,0x08,0x10,0x1E,0x11,0x11,0x0E}},
            {'7', new byte[]{0x1F,0x01,0x02,0x04,0x08,0x08,0x08}},
            {'8', new byte[]{0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E}},
            {'9', new byte[]{0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C}},
            {'A', new byte[]{0x0E,0x11,0x11,0x1F,0x11,0x11,0x11}},
            {'B', new byte[]{0x1E,0x11,0x11,0x1E,0x11,0x11,0x1E}},
            {'C', new byte[]{0x0E,0x11,0x10,0x10,0x10,0x11,0x0E}},
            {'D', new byte[]{0x1C,0x12,0x11,0x11,0x11,0x12,0x1C}},
            {'E', new byte[]{0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F}},
            {'F', new byte[]{0x1F,0x10,0x10,0x1E,0x10,0x10,0x10}},
            {'G', new byte[]{0x0E,0x11,0x10,0x17,0x11,0x11,0x0F}},
            {'H', new byte[]{0x11,0x11,0x11,0x1F,0x11,0x11,0x11}},
        };

        static void DrawString(Texture2D tex, string s, int x, int y, int scale, Color col)
        {
            int cx = x;
            foreach (char ch in s)
            {
                if (font.TryGetValue(ch, out var glyph))
                {
                    // Shadow
                    DrawGlyph(tex, glyph, cx + scale, y - scale, scale, Color.black);
                    // Foreground
                    DrawGlyph(tex, glyph, cx, y, scale, col);
                    cx += 6 * scale;
                }
                else cx += 4 * scale;
            }
        }

        static void DrawGlyph(Texture2D tex, byte[] glyph, int ox, int oy, int scale, Color col)
        {
            int w = tex.width, h = tex.height;
            for (int row = 0; row < 7; row++)
            {
                byte bits = glyph[row];
                for (int col_bit = 0; col_bit < 5; col_bit++)
                {
                    if ((bits & (1 << (4 - col_bit))) == 0) continue;
                    for (int sy = 0; sy < scale; sy++)
                    {
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int px = ox + col_bit * scale + sx;
                            // Flip Y: row 0 = top of glyph → high Y in texture
                            int py = oy + (6 - row) * scale + sy;
                            if (px >= 0 && px < w && py >= 0 && py < h)
                                tex.SetPixel(px, py, col);
                        }
                    }
                }
            }
        }
    }
}
