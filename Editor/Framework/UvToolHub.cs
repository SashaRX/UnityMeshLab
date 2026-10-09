// UvToolHub.cs — Main EditorWindow for Mesh Lab.
// Toolbar at top selects the active IUvTool; sidebar shows that tool's controls.
// UV canvas is a shared UvCanvasView component.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    public partial class UvToolHub : EditorWindow
    {
        const string WindowTitle = nameof(UvToolHub);
        const string WindowBrand = "Mesh Lab";

        // ── Tool registry ──
        List<IUvTool> tools;
        int activeToolIndex;
        bool reloadModules;
        MeshLabModuleRegistry modules;
        IUvTool ActiveTool => tools != null && activeToolIndex >= 0 && activeToolIndex < tools.Count ? tools[activeToolIndex] : null;

        /// <summary>Find a registered tool by type. Returns null if not found.</summary>
        public T FindTool<T>() where T : class, IUvTool
        {
            if (tools == null) return null;
            foreach (var t in tools)
                if (t is T result) return result;
            return null;
        }

        // ── Shared components ──
        UvToolContext ctx;
        internal UvToolContext DiagnosticContext => ctx;
        internal UvTransferWorkflow DiagnosticWorkflow => tools?.OfType<UvTransferWorkflow>().FirstOrDefault();
        UvCanvasView canvas;
        // The shared 3D viewport behind the canvas's UV | 3D switch. One instance per
        // window; every tool draws into it through IUvTool3D.
        MeshViewport3D viewport;
        UvLayer3D uvLayer;
        MeshInspection inspection;
        Action<Mesh> meshInvalidated;
        bool inspectMesh;
        int inspectedItem, planarProjection;
        Vector2 inspectionScroll;
        readonly Dictionary<(int, int), MeshEntry> inspectionEntries = new Dictionary<(int, int), MeshEntry>();
        bool canvas3D;
        const string Canvas3DPref = "MeshLab.Canvas3D";
        readonly List<MeshViewport3D.Item> viewportItems = new List<MeshViewport3D.Item>();
        readonly List<MeshEntry> viewportEntries = new List<MeshEntry>();   // parallel to viewportItems; null for tool content
        readonly List<MeshEntry> uvContentEntries = new List<MeshEntry>();  // a tool's own UV canvas content (IUvToolUvContent)
        int uvContentKey;
        int previewCacheVersion = -1;
        UvCanvasView.PreviewMode? suspendedPreviewMode;
        bool reapplyPreview;        // re-apply the preview once the entries list the newly selected model/LOD
        int preferredUvChannel = 1; // the channel the user picked; a LOD switch returns to it when the LOD has it
        Vector2 viewportSpotPointer;
        bool viewportSpotPointerValid, selectViewportSpotWhenReady;
        Rect canvasArea;   // the canvas column's content rect, from the last repaint

        // ── Layout ──
        float sideW = 300f;
        bool sideDragging;
        Vector2 sideScroll;
        // Right sidebar — only rendered when ActiveTool implements
        // IUvToolRightSidebar. Reapportioned by the right-edge resize handle.
        float rightSideW = 360f;
        bool rightSideDragging;
        float resizeStartX, resizeStartWidth;
        Vector2 rightSideScroll;
        Vector2 toolTabsScroll;
        int _cachedLodCount;
        int _cachedRendererCount;
        int cachedLodRendererKey;
        int _checkerUvChannel = 1;
        bool _checkerColorMode;
        bool _checkerShowR = true;
        bool _checkerShowG = true;

        // ── Selection tracking ──
        string selectedSidecarPath;
        string selectedFbxPath;
        string selectedResetLabel;
        string pendingToolId;
        string windowDebugTag;

        // The window opener and the validator submenu must share the same
        // top-level "Tools/Mesh Lab" namespace, otherwise Unity collapses the
        // action item into the submenu and the entry to open the window
        // disappears (only "Validators ▸" remains visible).
        [MenuItem("Tools/Mesh Lab/Open Mesh Lab", false, 0)]
        static void Open()
        {
            OpenWithTool(null);
        }

        static void OpenWithTool(string toolId)
        {
            var w = GetWindow<UvToolHub>(WindowTitle);
            w.minSize = new Vector2(800, 500);
            w.pendingToolId = toolId;
            if (w.tools != null && w.tools.Count > 0)
                w.SelectToolById(toolId);
        }

        void OnEnable()
        {
            wantsMouseMove = true;
            windowDebugTag = BuildWindowDebugTag();
            // Keep the actual dock/window title stable and aligned with the
            // EditorWindow type name. Unity can log "Invalid editor window"
            // on maximize/minimize for custom windows with a different title.
            titleContent = new GUIContent(WindowTitle, BuildWindowBrandText());

            // Safety: if the window was closed while a preview was active (e.g., checker
            // materials on renderers), the static IsActive flag persists across editor
            // sessions via SessionState. Restore here cleans up orphaned materials.
            if (CheckerTexturePreview.IsActive) CheckerTexturePreview.Restore();
            if (ShellColorModelPreview.IsActive) ShellColorModelPreview.Restore();

            ctx = new UvToolContext();
            ctx.BeforeRefresh += PrepareModelSelection;
            ctx.OnMeshEntriesChanged += OnContextEntriesChanged;
            canvas = new UvCanvasView();
            canvas.Init();
            canvas.RequestRepaint = Repaint;
            canvas.PreviewReady = OnPreviewReady;
            viewport = new MeshViewport3D { RequestRepaint = Repaint };
            uvLayer = new UvLayer3D();
            inspection = new MeshInspection();
            // The context's per-mesh caches are keyed by instance ID and would outlive
            // an in-place rewrite; drop them on the same notification the views use.
            meshInvalidated = _ => ctx.ClearAllCaches();
            VertexChannels.Changed += meshInvalidated;
            canvas3D = EditorPrefs.GetBool(Canvas3DPref, false);

            ctx.Assets.BeforeWrite = BeforeAssetWrite;
            ctx.Assets.AfterWrite = AfterAssetWrite;
            ctx.Assets.WriteFinished = FinishAssetWrite;
            MeshLabProjectSettings.ModulesChanged -= OnModulesChanged;
            MeshLabProjectSettings.ModulesChanged += OnModulesChanged;
            ConfigureModules();
            RestoreWindowSettings();
            SelectToolById(pendingToolId);

            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.undoRedoPerformed += OnUndoRedo;

            UvProgress.OnChanged -= OnProgressChanged;
            UvProgress.OnChanged += OnProgressChanged;
        }

        void OnProgressChanged()
        {
            // Snapshot updates arrive from the main thread (xatlas pack
            // polling, transfer phase reports). Just queue a repaint — when
            // the editor next pumps OnGUI it picks up UvProgress.Current.
            Repaint();
        }

        void SelectToolById(string toolId)
        {
            if (string.IsNullOrEmpty(toolId) || tools == null || tools.Count == 0) return;
            int idx = tools.FindIndex(t => t.ToolId == toolId);
            if (idx >= 0 && idx != activeToolIndex)
                SwitchTool(idx);
            pendingToolId = null;
        }

        void OnDisable()
        {
            OnLostFocus();
            // Cleanup order matters:
            // 1. Deactivate active tool (lets it restore its own preview state)
            // 2. Restore all remaining previews (checker, shell color, lightmap)
            // 3. Reset LOD forcing so the scene renders normally
            // 4. Cleanup canvas GL resources
            // 5. Destroy working meshes and restore fbxMesh on MeshFilter

            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            UvProgress.OnChanged -= OnProgressChanged;

            MeshLabProjectSettings.ModulesChanged -= OnModulesChanged;
            DeactivateTool();
            if (ctx != null) {
                ctx.BeforeRefresh -= PrepareModelSelection;
                ctx.OnMeshEntriesChanged -= OnContextEntriesChanged;
                ctx.Assets.BeforeWrite = null; ctx.Assets.AfterWrite = null; ctx.Assets.WriteFinished = null;
            }

            // Restore all previews
            if (canvas != null && (canvas.CheckerEnabled || CheckerTexturePreview.IsActive))
            {
                canvas.CheckerEnabled = false;
                CheckerTexturePreview.Restore();
            }
            if (ShellColorModelPreview.IsActive)
                ShellColorModelPreview.Restore();
            RestoreLightmapPreview();
            if (canvas != null) canvas.CurrentPreviewMode = UvCanvasView.PreviewMode.Off;

            if (ctx?.LodGroup != null)
                ctx.LodGroup.ForceLOD(-1);

            canvas?.Cleanup();
            viewport?.Dispose(); viewport = null;
            uvLayer?.Dispose(); uvLayer = null;
            inspection?.Dispose(); inspection = null;
            inspectionEntries.Clear();
            VertexChannels.Changed -= meshInvalidated; meshInvalidated = null;

            RestoreWorkingMeshes();
        }

        /// <summary>
        /// Restore fbxMesh on MeshFilter and destroy temporary working meshes.
        /// Must be called before clearing or switching MeshEntries.
        /// </summary>
        void RestoreWorkingMeshes()
        {
            if (ctx?.MeshEntries == null) return;
            foreach (var e in ctx.MeshEntries)
            {
                if (e.meshFilter != null && e.fbxMesh != null)
                    e.meshFilter.sharedMesh = e.fbxMesh;
                if (e.transferredMesh != null) { DestroyImmediate(e.transferredMesh); e.transferredMesh = null; }
                if (e.repackedMesh != null) { DestroyImmediate(e.repackedMesh); e.repackedMesh = null; }
                e.repackedAtlasWidth = 0;
                e.repackedAtlasHeight = 0;
                if (e.originalMesh != null && e.originalMesh != e.fbxMesh) { DestroyImmediate(e.originalMesh); e.originalMesh = null; }
            }
        }

        static int CountValidRenderers(LODGroup lg)
        {
            if (lg == null) return 0;
            int count = 0;
            foreach (var lod in lg.GetLODs())
                if (lod.renderers != null)
                    foreach (var r in lod.renderers)
                        if (r != null) count++;
            return count;
        }

        void OnSelectionChange()
        {
            if (ctx == null) return;

            var go = Selection.activeGameObject;
            if (go != null)
            {
                var lg = go.GetComponentInParent<LODGroup>();
                if (lg != null && lg != ctx.LodGroup)
                {
                    PrepareModelSelection();
                    ctx.Refresh(lg);
                    _cachedLodCount = ctx.LodCount;
                    _cachedRendererCount = CountValidRenderers(lg);
                    ctx.PreviewLod = Mathf.Clamp(ctx.PreviewLod, 0, Mathf.Max(0, ctx.LodCount - 1));
                    ActiveTool?.OnRefresh(); InvalidateViewportCaches();
                }
                else if (lg == null)
                {
                    // Selected object has no LODGroup — check for standalone mesh
                    // or LOD siblings so clicking a Light/Camera doesn't disrupt workflow.
                    var mr = go.GetComponent<MeshRenderer>();
                    var mf = go.GetComponent<MeshFilter>();
                    bool isStandaloneMesh = mr != null && mf != null && mf.sharedMesh != null;

                    // Skip if already showing this standalone renderer
                    bool alreadyShown = isStandaloneMesh && ctx.StandaloneMesh
                        && ctx.MeshEntries.Count == 1 && ctx.MeshEntries[0].renderer == mr;
                    if (alreadyShown) { /* no-op */ }
                    else
                    {
                        bool hasMeshRelevance = isStandaloneMesh
                                             || go.GetComponentInChildren<MeshFilter>() != null
                                             || go.GetComponentInChildren<MeshRenderer>() != null
                                             || LodGroupUtility.FindLodSiblings(go) != null;
                        if (hasMeshRelevance)
                        {
                            PrepareModelSelection();

                            if (isStandaloneMesh)
                            {
                                ctx.RefreshStandalone(mr);
                                _cachedLodCount = 1;
                                _cachedRendererCount = 1;
                            }
                            else
                            {
                                ctx.Refresh(null);
                                _cachedLodCount = 0;
                                _cachedRendererCount = 0;
                            }
                            ActiveTool?.OnRefresh(); InvalidateViewportCaches();
                        }
                    }
                }
            }

            UpdateSelectedSidecar();
            Repaint();
        }

        void PrepareModelSelection()
        {
            // Restore the old model before refreshing entries, retaining the user's display mode.
            RestorePreviewOverrides();
            if (ctx.LodGroup != null) ctx.LodGroup.ForceLOD(-1);
            RestoreWorkingMeshes();
            reapplyPreview = true;
        }

        void OnContextEntriesChanged()
        {
            _cachedLodCount = ctx.LodCount;
            _cachedRendererCount = ctx.LodGroup ? CountValidRenderers(ctx.LodGroup) : ctx.MeshEntries.Count;
            cachedLodRendererKey = LodRendererKey(ctx.LodGroup);
            reapplyPreview = true;
        }

        static int LodRendererKey(LODGroup group)
        {
            int key = 0;
            if (!group) return key;
            unchecked {
                foreach (var lod in group.GetLODs()) {
                    key = key * 31 + 1;
                    if (lod.renderers == null) continue;
                    foreach (var renderer in lod.renderers) key = key * 31 + (renderer ? renderer.GetInstanceID() : 0);
                }
            }
            return key;
        }

        void RefreshLodStructure()
        {
            if (!ctx.LodGroup) return;
            int rendererCount = CountValidRenderers(ctx.LodGroup);
            if (ctx.LodCount == _cachedLodCount && rendererCount == _cachedRendererCount
                && LodRendererKey(ctx.LodGroup) == cachedLodRendererKey) return;
            ctx.Refresh(ctx.LodGroup);
            _cachedLodCount = ctx.LodCount;
            _cachedRendererCount = CountValidRenderers(ctx.LodGroup);
            ctx.PreviewLod = Mathf.Clamp(ctx.PreviewLod, 0, Mathf.Max(0, ctx.LodCount - 1));
            ActiveTool?.OnRefresh(); InvalidateViewportCaches();
        }

        void OnUndoRedo()
        {
            if (ctx == null) return;

            // Preview modes can temporarily replace MeshFilter.sharedMesh. Restore
            // those swaps and the imported meshes before rebuilding MeshEntries,
            // otherwise Refresh can cache a preview mesh as the new baseline.
            RestorePreviewOverrides();
            RestoreWorkingMeshes();
            reapplyPreview = true;

            if (ctx.LodGroup != null)
            {
                ctx.Refresh(ctx.LodGroup);
                _cachedLodCount = ctx.LodCount;
                _cachedRendererCount = CountValidRenderers(ctx.LodGroup);
                ctx.PreviewLod = Mathf.Clamp(ctx.PreviewLod, 0, Mathf.Max(0, ctx.LodCount - 1));
            }
            else if (ctx.StandaloneMesh)
            {
                if (ctx.MeshEntries.Count > 0 && ctx.MeshEntries[0].renderer != null)
                    ctx.RefreshStandalone(ctx.MeshEntries[0].renderer as MeshRenderer);
                else
                {
                    ctx.Refresh(null);
                    _cachedLodCount = 0;
                    _cachedRendererCount = 0;
                }
            }

            ActiveTool?.OnRefresh(); InvalidateViewportCaches();
            Repaint();
        }

        void OnGUI()
        {
            if (ctx == null || canvas == null) return;
            if (reloadModules) { reloadModules = false; ConfigureModules(); }
            if (tools == null || tools.Count == 0)
                EditorGUILayout.HelpBox("No tools enabled. Mesh inspection remains available; enable tools and their required libraries in Project Settings > Mesh Lab.", MessageType.Info);

            // Detect LODGroup structural changes (e.g. LOD deleted externally, renderer removed)
            RefreshLodStructure();

            DrawHubToolbar();

            var rightSidebar = ActiveTool as IUvToolRightSidebar;
            var widths = ResolveColumnWidths(position.width, sideW, rightSideW, rightSidebar != null);
            float handleW = SplitterWidth(position.width, rightSidebar != null);
            float toolbarHeight = EditorGUIUtility.singleLineHeight + 2;
            var body = new Rect(0, toolbarHeight, position.width, Mathf.Max(0, position.height - toolbarHeight - kProgressStripHeight));
            // Areas isolate content's minimum widths (toolbars, long labels and
            // scroll views) from adjacent columns. Always use the window width,
            // even if the toolbar's GUILayout group requests a wider root.
            body.x = 0; body.width = position.width;
            var leftRect = new Rect(body.x, body.y, widths.x, body.height);
            var canvasRect = new Rect(leftRect.xMax + handleW, body.y, widths.y, body.height);
            var rightRect = new Rect(canvasRect.xMax + handleW, body.y, widths.z, body.height);

            // ── Left sidebar ���─
            GUILayout.BeginArea(leftRect);
            sideScroll = EditorGUILayout.BeginScrollView(sideScroll);
            ActiveTool?.OnDrawSidebar();
            EditorGUILayout.EndScrollView();
            DrawSidebarFooter();
            GUILayout.EndArea();

            DrawResizeHandle(new Rect(leftRect.xMax, body.y, handleW, body.height), false, widths, rightSidebar != null);

            // Middle column with explicit Width so it's the first thing to
            // shrink when the window is too narrow.
            GUILayout.BeginArea(canvasRect);
            CollectCanvasEntries();
            DrawCanvasToolbar();
            DrawInspectionToolbar();
            canvas.InspectionShading = viewport.Mode;

            bool showGroupPanel = ctx.RepackPerMesh && ctx.MeshGroupCount(ctx.PreviewLod) > 1;
            if (showGroupPanel)
                EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical();
            if (canvas3D || UseGeometry2D)
            {
                DrawViewport();
            }
            else
            {
                // The canvas area always fills the column (the canvas itself draws only a
                // help box when it has nothing to show), so the switch has a place in every
                // state. Its click is taken before the canvas (spot mode eats mouse-downs)
                // and it paints after the canvas blit.
                HandleCanvasModeSwitchInput(canvasArea);
                HandleCanvasModeKey(canvasArea);
                EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                canvas.OnGUI(ctx,
                    ActiveTool != null ? (Action<UvCanvasView, float, float, float>)ActiveTool.OnDrawCanvasOverlay : null);
                EditorGUILayout.EndVertical();
                if (Event.current.type == EventType.Repaint) canvasArea = GUILayoutUtility.GetLastRect();
                DrawCanvasModeSwitch(canvasArea);
            }
            DrawStatusBar();
            EditorGUILayout.EndVertical();

            if (showGroupPanel)
            {
                DrawMeshGroupPanel();
                EditorGUILayout.EndHorizontal();
            }

            GUILayout.EndArea();

            // Right sidebar (opt-in via IUvToolRightSidebar). The clamp + width
            // computation already happened at the top of OnGUI so we just
            // render at the resolved rightSideW here.
            if (rightSidebar != null)
            {
                DrawResizeHandle(new Rect(canvasRect.xMax, body.y, handleW, body.height), true, widths, true);
                GUILayout.BeginArea(rightRect);
                rightSideScroll = EditorGUILayout.BeginScrollView(rightSideScroll);
                rightSidebar.OnDrawRightSidebar();
                EditorGUILayout.EndScrollView();
                GUILayout.EndArea();
            }

            // Progress strip sits at the very bottom of the window — it is a
            // status-bar-style row that doesn't displace the toolbar / sub-tabs
            // layout when the active state toggles. The reserved height is
            // unconditional so appearing / disappearing also doesn't shift.
            GUILayout.BeginArea(new Rect(0, position.height - kProgressStripHeight, position.width, kProgressStripHeight));
            DrawProgressStrip();
            GUILayout.EndArea();
        }

        internal static float SplitterWidth(float width, bool right)
            => Mathf.Min(6f, Mathf.Max(0, width) / (right ? 2 : 1));

        internal static Vector3 ResolveColumnWidths(float width, float left, float right, bool hasRight)
        {
            float available = Mathf.Max(0, width - SplitterWidth(width, hasRight) * (hasRight ? 2 : 1));
            float minimum = hasRight ? 560f : 340f;
            if (available < minimum) {
                float scale = available / minimum;
                return new Vector3(220 * scale, 120 * scale, hasRight ? 220 * scale : 0);
            }
            left = Mathf.Clamp(left, 220, Mathf.Min(900, available - 120 - (hasRight ? 220 : 0)));
            right = hasRight ? Mathf.Clamp(right, 220, Mathf.Min(700, available - left - 120)) : 0;
            return new Vector3(left, available - left - right, right);
        }

        internal static Vector3 ResizeColumns(float width, Vector3 columns, float requested, bool right, bool hasRight)
        {
            float available = width - SplitterWidth(width, hasRight) * (hasRight ? 2 : 1);
            // Growing the right panel must also be able to shrink an overwide
            // left panel, rather than leaving the right splitter stuck at its minimum.
            float left = right ? Mathf.Min(columns.x, available - 120 - Mathf.Clamp(requested, 220, 700)) : requested;
            return ResolveColumnWidths(width, left, right ? requested : columns.z, hasRight);
        }

        void DrawResizeHandle(Rect r, bool right, Vector3 widths, bool hasRight)
        {
            EditorGUI.DrawRect(r, new Color(.13f, .13f, .13f));
            EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeHorizontal);
            int id = GUIUtility.GetControlID(right ? 0x4d5202 : 0x4d5201, FocusType.Passive);
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition)) {
                GUIUtility.hotControl = id;
                rightSideDragging = right; sideDragging = !right;
                resizeStartX = e.mousePosition.x; resizeStartWidth = right ? widths.z : widths.x;
                e.Use();
            }
            if (GUIUtility.hotControl != id) return;
            if (e.type == EventType.MouseDrag) {
                float requested = resizeStartWidth + (right ? -1 : 1) * (e.mousePosition.x - resizeStartX);
                var resolved = ResizeColumns(position.width, widths, requested, right, hasRight);
                sideW = resolved.x; if (hasRight) rightSideW = resolved.z;
                e.Use(); Repaint();
            }
            if (e.rawType == EventType.MouseUp) {
                GUIUtility.hotControl = 0; rightSideDragging = sideDragging = false; e.Use();
            }
        }

        void OnLostFocus()
        {
            SaveWindowSettings();
            if (sideDragging || rightSideDragging) GUIUtility.hotControl = 0;
            sideDragging = rightSideDragging = false;
        }

        void OnSceneGUI(SceneView sv)
        {
            ActiveTool?.OnSceneGUI(sv);
        }

        // ════════════════════════════════════════════════════════════
        //  Hub Toolbar (tool selector)
        // ══════��═══════════��═════════════════════════════════════════

        void DrawHubToolbar()
        {
            float height = EditorGUIUtility.singleLineHeight + 2;
            var bar = new Rect(0, 0, position.width, height);
            GUI.Box(bar, GUIContent.none, EditorStyles.toolbar);

            // Debug-only tabs (IUvToolDebugOnly) show with Show Debug UI on; when the
            // setting goes off while one is active, the hub falls back to the first tab.
            bool debug = DebugUi.Enabled;
            if (!debug && activeToolIndex >= 0 && activeToolIndex < tools.Count && tools[activeToolIndex] is IUvToolDebugOnly)
                SwitchTool(tools.FindIndex(t => !(t is IUvToolDebugOnly)));
            float brandWidth = position.width >= 1100 ? 220 : 0;
            float controlsWidth = 154 + brandWidth;
            float tabsWidth = Mathf.Max(0, position.width - controlsWidth);
            float contentWidth = 0;
            foreach (var tool in tools)
                if (debug || !(tool is IUvToolDebugOnly)) contentWidth += Mathf.Max(80, EditorStyles.toolbarButton.CalcSize(new GUIContent(tool.ToolName)).x);
            toolTabsScroll = GUI.BeginScrollView(new Rect(0, 0, tabsWidth, height), toolTabsScroll,
                new Rect(0, 0, contentWidth, height), false, false, GUIStyle.none, GUIStyle.none);
            float x = 0;
            for (int i = 0; i < tools.Count; i++)
            {
                if (!debug && tools[i] is IUvToolDebugOnly) continue;
                var bg = GUI.backgroundColor;
                if (i == activeToolIndex)
                    GUI.backgroundColor = new Color(.35f, .65f, 1f);
                float width = Mathf.Max(80, EditorStyles.toolbarButton.CalcSize(new GUIContent(tools[i].ToolName)).x);
                if (GUI.Button(new Rect(x, 0, width, height), tools[i].ToolName, EditorStyles.toolbarButton))
                {
                    if (i != activeToolIndex)
                        SwitchTool(i);
                }
                GUI.backgroundColor = bg;
                x += width;
            }
            GUI.EndScrollView();
            x = tabsWidth;
            if (brandWidth > 0) {
                GUI.Label(new Rect(x, 0, brandWidth, height), BuildWindowBrandText(), EditorStyles.miniLabel);
                x += brandWidth;
            }
            if (GUI.Button(new Rect(x, 0, 60, height), "Modules", EditorStyles.toolbarButton))
                SettingsService.OpenProjectSettings("Project/Mesh Lab");

            // ── Log level ──
            GUI.Label(new Rect(x + 62, 0, 24, height), "Log:", EditorStyles.miniLabel);
            var lvl = (UvtLog.Level)EditorGUI.EnumPopup(new Rect(x + 86, 0, 64, height), UvtLog.Current, EditorStyles.toolbarPopup);
            if (lvl != UvtLog.Current) UvtLog.Current = lvl;
        }

        // ════════════════════════════════════════════════════════════
        //  Inline progress strip — sits at the very bottom of the window
        //  (status-bar pattern). The full row width is reserved
        //  unconditionally so toggling the active state doesn't displace
        //  any layout above. When idle the strip blends into the chrome
        //  with no text or animation. When active it shows: bold primary
        //  (title · phase) on the left, optional detail right-aligned next
        //  to it, elapsed in its own column, and a properly sized Cancel
        //  button flush with the right edge.
        // ════════════════════════════════════════════════════════════
        const float kProgressStripHeight = 22f;

        void DrawProgressStrip()
        {
            var rect = GUILayoutUtility.GetRect(0, kProgressStripHeight,
                GUILayout.ExpandWidth(true), GUILayout.Height(kProgressStripHeight));

            var snap = UvProgress.Current;

            // Background — matches the toolbar so the bottom strip visually
            // mirrors the top one. Active state darkens to draw attention.
            Color toolbarTint = EditorGUIUtility.isProSkin
                ? new Color(0.235f, 0.235f, 0.235f)
                : new Color(0.78f, 0.78f, 0.78f);
            Color activeTint = EditorGUIUtility.isProSkin
                ? new Color(0.16f, 0.16f, 0.16f)
                : new Color(0.70f, 0.70f, 0.70f);
            EditorGUI.DrawRect(rect, snap.active ? activeTint : toolbarTint);

            // Top hairline — separates the strip from the content above.
            // (Bottom is the window edge so no separator needed there.)
            var sep = new Rect(rect.x, rect.y, rect.width, 1);
            EditorGUI.DrawRect(sep, new Color(0, 0, 0, EditorGUIUtility.isProSkin ? 0.45f : 0.25f));

            if (!snap.active)
            {
                // Idle — show last operation outcome if we have one.
                var last = UvProgress.Last;
                if (!last.valid) return;
                string symbol;
                Color symbolColor;
                switch (last.status)
                {
                    case UnityEditor.Progress.Status.Succeeded:
                        symbol = "✓"; symbolColor = new Color(0.5f, 0.95f, 0.55f); break;
                    case UnityEditor.Progress.Status.Canceled:
                        symbol = "✗"; symbolColor = new Color(1f, 0.75f, 0.30f); break;
                    case UnityEditor.Progress.Status.Failed:
                        symbol = "✗"; symbolColor = new Color(0.95f, 0.45f, 0.45f); break;
                    default:
                        symbol = "·"; symbolColor = new Color(0.75f, 0.75f, 0.75f); break;
                }
                var symRect = new Rect(rect.x + 8f, rect.y, 14f, rect.height);
                var symStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = symbolColor },
                };
                GUI.Label(symRect, symbol, symStyle);
                var idleStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = new Color(0.75f, 0.75f, 0.75f) },
                };
                var idleTextRect = new Rect(rect.x + 26f, rect.y,
                                            rect.width - 32f, rect.height);
                string idleText = $"{last.title}  ·  {last.duration:0.0}s";
                GUI.Label(idleTextRect, idleText, idleStyle);
                return;
            }

            // Fill — determinate fraction or animated marquee for indeterminate.
            var innerRect = new Rect(rect.x, rect.y + 1, rect.width, rect.height - 1);
            float frac = snap.fraction;
            Color fillColor = snap.cancelRequested
                ? new Color(0.95f, 0.55f, 0.20f, 0.50f)
                : new Color(0.30f, 0.60f, 0.95f, 0.50f);
            if (frac >= 0f)
            {
                var fill = innerRect;
                fill.width *= Mathf.Clamp01(frac);
                EditorGUI.DrawRect(fill, fillColor);
            }
            else
            {
                // Marquee — band sweeps left→right, eased so the edges of
                // the band slide off-screen rather than snapping at the rim.
                double cycle = (EditorApplication.timeSinceStartup * 0.55) % 1.0;
                float t = (float)cycle;
                float bandW = innerRect.width * 0.22f;
                float x = innerRect.x + (innerRect.width + bandW) * t - bandW;
                var fill = new Rect(x, innerRect.y, bandW, innerRect.height);
                fillColor.a = 0.38f;
                EditorGUI.DrawRect(fill, fillColor);
                Repaint(); // keep the marquee moving.
            }

            // Compose primary (title · phase) and optional detail. Drop
            // detail when it duplicates the phase verbatim.
            string primary = snap.title ?? string.Empty;
            if (!string.IsNullOrEmpty(snap.phase))
            {
                if (!string.IsNullOrEmpty(primary)) primary += " · ";
                primary += snap.phase;
            }
            string detail = snap.detail;
            if (!string.IsNullOrEmpty(detail) && !string.IsNullOrEmpty(snap.phase)
                && detail.StartsWith(snap.phase, System.StringComparison.Ordinal))
                detail = null;
            string elapsed = $"{snap.Elapsed:0.0}s";

            // Column geometry. Cancel button gets its own slot pinned to the
            // right edge; elapsed sits to its left; primary/detail share the
            // remaining width with detail right-aligned.
            const float kBtnW = 60f;
            const float kBtnPad = 6f;
            const float kElapsedW = 44f;
            const float kColGap = 8f;
            bool showCancel = snap.cancelable || snap.cancelRequested;
            float rightReserve = showCancel ? (kBtnW + kBtnPad) : kBtnPad;

            var elapsedRect = new Rect(rect.xMax - rightReserve - kElapsedW, rect.y,
                                       kElapsedW, rect.height);
            float primaryRight = elapsedRect.x - kColGap;
            var primaryRect = new Rect(rect.x + 8f, rect.y,
                                       primaryRight - (rect.x + 8f), rect.height);

            var primaryStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white },
            };
            var detailStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) },
            };
            var elapsedStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.75f, 0.85f, 1f) },
            };

            if (!string.IsNullOrEmpty(detail))
                GUI.Label(primaryRect, detail, detailStyle);
            GUI.Label(primaryRect, primary, primaryStyle);
            GUI.Label(elapsedRect, elapsed, elapsedStyle);

            // Cancel control — vertically centred against the strip and
            // sized to the strip's height (minus 4px breathing room) so it
            // sits cleanly inside the row instead of overflowing.
            if (snap.cancelable && !snap.cancelRequested)
            {
                float btnH = rect.height - 4f;
                var btnRect = new Rect(rect.xMax - kBtnW - kBtnPad,
                                       rect.y + (rect.height - btnH) * 0.5f,
                                       kBtnW, btnH);
                if (GUI.Button(btnRect, "Cancel", EditorStyles.miniButton))
                    UvProgress.RequestCancel();
            }
            else if (snap.cancelRequested)
            {
                var labelRect2 = new Rect(rect.xMax - kBtnW - kBtnPad, rect.y,
                                          kBtnW, rect.height);
                var cancelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = new Color(1f, 0.7f, 0.2f) },
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Italic,
                };
                GUI.Label(labelRect2, "cancelling…", cancelStyle);
            }
        }

        string BuildWindowBrandText()
        {
            string text = WindowBrand + " v" + Uv2DataAsset.ToolVersionStr;
            if (!string.IsNullOrEmpty(windowDebugTag))
                text += " [" + windowDebugTag + "]";
            return text;
        }

        static string BuildWindowDebugTag()
        {
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UvToolHub).Assembly);
                string resolvedPath = package?.resolvedPath;
                if (string.IsNullOrEmpty(resolvedPath))
                    return null;

                string leaf = Path.GetFileName(resolvedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                int atIndex = leaf.LastIndexOf('@');
                if (atIndex >= 0 && atIndex < leaf.Length - 1)
                {
                    string revision = leaf.Substring(atIndex + 1);
                    if (!string.IsNullOrEmpty(revision))
                        return revision.Length > 8 ? revision.Substring(0, 8) : revision;
                }

                return package?.version;
            }
            catch
            {
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Canvas Toolbar (LOD, UV channel, spot, zoom, etc.)
        // ���═══════════════��═════════════════════════════════════════��═

        void DrawCanvasToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            DrawLodButtons();
            DrawUvCanvasToolbarBody();
        }

        // The LOD row shared by the UV and 3D canvas toolbars.
        void DrawLodButtons()
        {
            // ── LOD buttons ──
            if (ctx.LodCount > 0)
            {
                for (int i = 0; i < ctx.LodCount; i++)
                {
                    string label = i == ctx.SourceLodIndex ? "LOD" + i + "(S)" : "LOD" + i;
                    var bg = GUI.backgroundColor;
                    if (ctx.PreviewLod == i) GUI.backgroundColor = new Color(.35f, .65f, 1f);
                    else GUI.backgroundColor = new Color(.75f, .85f, .95f);
                    if (GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(58)))
                        SetPreviewLod(i);
                    GUI.backgroundColor = bg;
                }

                GUILayout.Space(8);
                var sep = GUILayoutUtility.GetRect(1, 18, GUILayout.Width(1));
                EditorGUI.DrawRect(sep, new Color(.5f, .5f, .5f, .6f));
                GUILayout.Space(4);

                // ── LOD tri count ──
                int lodTris = 0;
                foreach (var e in ctx.ForLod(ctx.PreviewLod))
                {
                    Mesh m = e.repackedMesh ?? e.originalMesh ?? e.fbxMesh;
                    if (m != null) lodTris += m.triangles.Length / 3;
                }
                var triStyle = new GUIStyle(EditorStyles.toolbarButton) {
                    normal = { textColor = new Color(.3f, 1f, .5f) },
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter
                };
                GUILayout.Label("▲ " + lodTris.ToString("N0"), triStyle, GUILayout.Width(72));
                GUILayout.Space(4);
            }
        }

        void DrawUvCanvasToolbarBody()
        {
            DrawUvChannelToggle();
            DrawSpotControls();

            // ── Zoom + Fit ──
            if (canvas3D || UseGeometry2D) {
                viewport.Lit = GUILayout.Toggle(viewport.Lit, "Lit", EditorStyles.toolbarButton, GUILayout.Width(30));
                viewport.ShowGrid = GUILayout.Toggle(viewport.ShowGrid,
                    new GUIContent(canvas3D ? "Floor" : "Grid", "Show or hide the reference grid in the preview."),
                    EditorStyles.toolbarButton, GUILayout.Width(40));
                if (canvas3D)
                    viewport.Up = (MeshViewport3D.UpAxis)EditorGUILayout.Popup((int)viewport.Up, UpAxisLabels,
                        EditorStyles.toolbarPopup, GUILayout.Width(60));
                if (GUILayout.Button("Frame", EditorStyles.toolbarButton, GUILayout.Width(44))) viewport.FrameContent();
            }
            else {
                canvas.Zoom = EditorGUILayout.Slider(canvas.Zoom, .01f, 20f, GUILayout.Width(90));
                if (GUILayout.Button("Fit", EditorStyles.toolbarButton, GUILayout.Width(28))) canvas.FitToUvBounds(ctx);
            }

            DrawToolbarTail();
        }

        static readonly GUIContent[] UpAxisLabels = {
            new GUIContent("X Up", "Use X as vertical for the perspective camera and floor."),
            new GUIContent("Y Up", "Use Y as vertical for the perspective camera and floor (Unity convention)."),
            new GUIContent("Z Up", "Use Z as vertical for the perspective camera and floor (3ds Max convention).")
        };

        void DrawUvChannelToggle()
        {
            // ── UV channel toggle ──
            {
                var bg = GUI.backgroundColor;
                for (int ch = 0; ch < 8; ch++)
                {
                    if (!canvas.HasPreviewChannel(ctx, ch)) continue;
                    bool active = ctx.PreviewUvChannel == ch;
                    GUI.backgroundColor = active ? new Color(.4f, .55f, 1f) : new Color(.65f, .65f, .7f);
                    string lbl = "UV" + ch;
                    if (GUILayout.Button(lbl, EditorStyles.toolbarButton, GUILayout.Width(34)))
                    {
                        if (!active) OnPreviewChannelChanged(ch);
                    }
                }
                GUI.backgroundColor = bg;
            }

            GUILayout.Space(6);
        }

        void DrawSpotControls()
        {
            // ── Spot / Lock / Clear ──
            bool spotNext = GUILayout.Toggle(canvas.SpotMode, "Spot", EditorStyles.toolbarButton, GUILayout.Width(52));
            if (spotNext != canvas.SpotMode)
            {
                canvas.SpotMode = spotNext;
                if (!canvas.SpotMode) canvas.ClearHoverState();
                SceneView.RepaintAll();
            }
            bool lockNext = GUILayout.Toggle(canvas.LockSelection, "Lock", EditorStyles.toolbarButton, GUILayout.Width(40));
            if (lockNext != canvas.LockSelection) canvas.SetSelectionLock(lockNext);
            using (new EditorGUI.DisabledScope(!canvas.HasSelectedShell))
            {
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(42)))
                {
                    canvas.ClearSpotSelection();
                }
            }

            GUILayout.Space(6);
        }

        void DrawToolbarTail()
        {
            // ── Tool extra toolbar ──
            ActiveTool?.OnDrawToolbarExtra();

            // ── Right side ──
            GUILayout.FlexibleSpace();

            // ── Reset UV2 for selected model ──
            if (selectedSidecarPath != null)
            {
                var bg3 = GUI.backgroundColor;
                GUI.backgroundColor = new Color(.95f, .35f, .3f);
                if (GUILayout.Button("Reset UV2: " + selectedResetLabel, EditorStyles.toolbarButton))
                    ResetSelectedUv2();
                GUI.backgroundColor = bg3;
                GUILayout.Space(6);
            }

            EditorGUILayout.EndHorizontal();
        }

        // ════════════════════════════════════════════════════════════
        //  3D canvas (shared viewport)
        // ════════════════════════════════════════════════════════════

        // ── In-canvas controls: a segmented pill (dark track, accent on the active
        //    segment). Input is taken BEFORE the canvas or viewport consumes the event,
        //    the visual is painted AFTER their blit, so both halves read the same rect. ──

        static readonly Color SegmentTrack = new Color(0.10f, 0.11f, 0.13f, 0.92f);
        static readonly Color SegmentAccent = new Color(0.96f, 0.52f, 0.14f, 1f);
        static readonly Color SegmentText = new Color(0.82f, 0.84f, 0.88f, 1f);
        static readonly Color SegmentTextActive = new Color(0.12f, 0.10f, 0.08f, 1f);

        static int SegmentHit(Rect rect, int count, Vector2 mouse)
        {
            if (count <= 0 || !rect.Contains(mouse)) return -1;
            return Mathf.Clamp((int)((mouse.x - rect.x) / rect.width * count), 0, count - 1);
        }

        static void DrawSegmented(Rect rect, string[] labels, int active, bool mini = false)
        {
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(rect, SegmentTrack);
            float w = rect.width / labels.Length;
            var style = new GUIStyle(mini ? EditorStyles.miniLabel : EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            for (int i = 0; i < labels.Length; i++)
            {
                var cell = new Rect(rect.x + i * w, rect.y, w, rect.height);
                if (i == active) EditorGUI.DrawRect(new Rect(cell.x + 2, cell.y + 2, cell.width - 4, cell.height - 4), SegmentAccent);
                style.normal.textColor = i == active ? SegmentTextActive : SegmentText;
                GUI.Label(cell, labels[i], style);
            }
            var outline = new Color(0f, 0f, 0f, 0.5f);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), outline);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), outline);
        }

        /// <summary>The UV | 3D switch, floating at the bottom centre of the canvas.</summary>
        static Rect CanvasModeSwitchRect(Rect canvasRect) => new Rect(canvasRect.center.x - 90f, canvasRect.yMax - 32f, 180f, 24f);
        static bool CanvasModeSwitchFits(Rect canvasRect) => canvasRect.width >= 200f && canvasRect.height >= 60f;
        static readonly string[] CanvasModeLabels = { "UV", "3D" };

        void HandleCanvasModeSwitchInput(Rect canvasRect)
        {
            if (!CanvasModeSwitchFits(canvasRect)) return;
            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0) return;
            int hit = SegmentHit(CanvasModeSwitchRect(canvasRect), 2, e.mousePosition);
            if (hit < 0) return;
            SetCanvas3D(hit == 1);
            e.Use();
        }

        // Tab over the canvas flips the view, unless a text field has the keyboard.
        void HandleCanvasModeKey(Rect canvasRect)
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || e.keyCode != KeyCode.Tab || GUIUtility.keyboardControl != 0) return;
            if (!canvasRect.Contains(e.mousePosition)) return;
            SetCanvas3D(!canvas3D);
            e.Use();
        }

        void DrawCanvasModeSwitch(Rect canvasRect)
        {
            if (!CanvasModeSwitchFits(canvasRect)) return;
            var rect = CanvasModeSwitchRect(canvasRect);
            DrawSegmented(rect, CanvasModeLabels, canvas3D ? 1 : 0);
            if (Event.current.type == EventType.Repaint)
                GUI.Label(new Rect(rect.xMax + 6, rect.y + 4, 60, 16), "Tab", new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.6f, 0.62f, 0.66f) } });
        }

        bool UseGeometry2D => planarProjection > 0 || !canvas.HasPreviewChannel(ctx, ctx.PreviewUvChannel) ||
            viewportItems.Any(item => item.mesh && !MeshInspection.HasOnlyTriangles(item.mesh));

        void DrawInspectionToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            // Offer only the shading modes the shown meshes actually have data for; the
            // active mode stays listed (and visibly unsupported) instead of vanishing.
            var modes = new List<MeshViewport3D.Shading>();
            var names = new List<string>(MeshViewport3D.ShadingNames.Length);
            for (int mode = 0; mode < MeshViewport3D.ShadingNames.Length; ++mode) {
                var shading = (MeshViewport3D.Shading)mode;
                int present = viewportItems.Count(item => MeshInspection.Supports(item.mesh, shading));
                if (present == 0 && shading != viewport.Mode) continue;
                string label = MeshViewport3D.ShadingNames[mode];
                // The (n/m) count marks partial support across several meshes; the
                // active-but-unsupported mode gets its own marker instead.
                if (viewportItems.Count > 1 && present > 0 && present < viewportItems.Count) label += $" ({present}/{viewportItems.Count})";
                else if (present == 0 && viewportItems.Count > 0) label += " (no data)";
                modes.Add(shading); names.Add(label);
            }
            int selected = Mathf.Max(0, modes.IndexOf(viewport.Mode));
            var next = modes[EditorGUILayout.Popup(selected, names.ToArray(), EditorStyles.toolbarPopup, GUILayout.Width(150))];
            if (next != viewport.Mode) {
                viewport.Mode = next;
                if (next >= MeshViewport3D.Shading.UV0 && next <= MeshViewport3D.Shading.UV7 && canvas.HasPreviewChannel(ctx, next - MeshViewport3D.Shading.UV0))
                    OnPreviewChannelChanged(next - MeshViewport3D.Shading.UV0);
                Repaint();
            }
            inspectMesh = GUILayout.Toggle(inspectMesh, "Inspect", EditorStyles.toolbarButton, GUILayout.Width(55));
            if (!canvas3D) planarProjection = EditorGUILayout.Popup(planarProjection, new[] { "UV layout", "XY", "XZ", "YZ" }, EditorStyles.toolbarPopup, GUILayout.Width(85));
            if (UseGeometry2D && !canvas3D && planarProjection == 0)
                GUILayout.Label("UV layout unavailable · XY geometry", EditorStyles.miniLabel);
            else if (!canvas3D && planarProjection == 0) {
                int mapped = viewportItems.Count(item => MeshInspection.Supports(item.mesh, MeshViewport3D.Shading.UV0 + ctx.PreviewUvChannel));
                if (mapped < viewportItems.Count) GUILayout.Label($"UV{ctx.PreviewUvChannel}: {mapped}/{viewportItems.Count} meshes", EditorStyles.miniLabel);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            if (!inspectMesh || viewportItems.Count == 0) return;
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            inspectedItem = Mathf.Clamp(inspectedItem, 0, viewportItems.Count - 1);
            if (viewportItems.Count > 1) {
                var meshes = viewportItems.Select(item => item.mesh.name).ToArray();
                inspectedItem = EditorGUILayout.Popup(inspectedItem, meshes, EditorStyles.toolbarPopup);
            }
            else
                GUILayout.Label(viewportItems[inspectedItem].mesh.name, EditorStyles.miniBoldLabel);
            EditorGUILayout.EndHorizontal();
            var mesh = viewportItems[inspectedItem].mesh;
            inspectionScroll = EditorGUILayout.BeginScrollView(inspectionScroll, GUILayout.Height(145));
            string text = inspection.Report(mesh);
            EditorGUILayout.SelectableLabel(text, EditorStyles.miniLabel, GUILayout.Height(Mathf.Max(140, text.Count(c => c == '\n') * 15 + 20)));
            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField("Ctrl+click a surface to inspect that mesh. Values are in mesh local space.", EditorStyles.miniLabel);
        }

        void SetCanvas3D(bool on)
        {
            if (canvas3D == on) return;
            canvas3D = on;
            EditorPrefs.SetBool(Canvas3DPref, on);
            viewportSpotPointerValid = false; selectViewportSpotWhenReady = false;
            canvas.ForgetUvSpotPointer();
            // Hover belongs to the view it was picked in; selection carries over.
            canvas.HasHoveredShell = false; canvas.HoveredShellDebug = null; canvas.HoverHitValid = false;
            Repaint(); SceneView.RepaintAll();
        }

        /// <summary>Content for the 3D canvas: the active tool's, or the context's preview
        /// LOD meshes with their scene materials (the same meshes the UV canvas shows).</summary>
        List<MeshViewport3D.Item> CollectViewportItems()
        {
            // Recollect from canonical entries, never from the output list that this
            // method clears. Preview changes can require two collections in one frame.
            canvas.EntriesOverride = toolOwnsUvContent ? uvContentEntries : null;
            viewportItems.Clear(); viewportEntries.Clear();
            canvas.DisplayMeshes.Clear();
            if (ActiveTool is IUvTool3D tool3D && tool3D.Get3DContent(viewportItems))
            {
                viewportItems.RemoveAll(item => !item.mesh);
                // A tool item showing the same mesh as one of the tool's UV entries gets
                // that entry, so the UV layer (fill, island borders) and spot picking work
                // on it in 3D exactly as on a context mesh; other items only get the wire.
                for (int i = 0; i < viewportItems.Count; i++)
                {
                    MeshEntry match = null;
                    foreach (var e in canvas.Entries(ctx))
                        if (viewportItems[i].mesh != null && ctx.DMesh(e) == viewportItems[i].mesh) { match = e; break; }
                    viewportEntries.Add(match);
                }
                PrepareInspectionEntries();
                canvas.EntriesOverride = viewportEntries;
                return viewportItems;
            }
            viewportItems.Clear();
            var worldToPreview = ContextWorldToPreview();
            List<string> groupKeys = canvas.EntriesOverride == null && ctx.RepackPerMesh && ctx.IsolatedMeshGroup >= 0 ? ctx.BuildGroupKeys(ctx.PreviewLod) : null;
            foreach (var e in canvas.Entries(ctx))
            {
                if (groupKeys != null && ctx.IsolatedMeshGroup < groupKeys.Count)
                {
                    string key = e.meshGroupKey ?? (e.renderer != null ? e.renderer.name : null);
                    if (key != groupKeys[ctx.IsolatedMeshGroup]) continue;
                }
                Mesh mesh = ctx.DMesh(e);
                if (mesh == null) continue;
                var matrix = e.renderer != null ? worldToPreview * e.renderer.localToWorldMatrix : Matrix4x4.identity;
                viewportItems.Add(new MeshViewport3D.Item(mesh, matrix, e.renderer != null ? e.renderer.sharedMaterials : null));
                viewportEntries.Add(e);
            }
            PrepareInspectionEntries();
            canvas.EntriesOverride = viewportEntries;
            return viewportItems;
        }

        // The selected object's origin belongs at zero in preview space. Remove
        // only its scene translation; retain rotation, scale and child placement.
        Matrix4x4 ContextWorldToPreview()
        {
            Transform root = null;
            if (ctx.LodGroup) root = ctx.LodGroup.transform;
            else if (ctx.StandaloneMesh && ctx.MeshEntries.Count > 0 && ctx.MeshEntries[0].renderer)
                root = ctx.MeshEntries[0].renderer.transform;
            return root ? Matrix4x4.Translate(-root.position) : Matrix4x4.identity;
        }

        bool toolOwnsUvContent;

        // The canvas entries for this frame: a tool's own UV canvas content (the Remesh &
        // Bake result) or the preview LOD's renderers; resolved every frame so it follows
        // the tool's stages, tab switches and the LOD row. A LOD switch lands here on the
        // frame after the click, once the list holds the renderers now shown: the channel
        // the user picked comes back when the new LOD has it, and the active 3D preview
        // moves to those renderers (applied from the click itself it would land on the
        // previous LOD's, which ForceLOD just hid).
        void CollectCanvasEntries()
        {
            uvContentEntries.Clear();
            toolOwnsUvContent = ActiveTool is IUvToolUvContent uvContent && uvContent.GetUvContent(uvContentEntries);
            canvas.EntriesOverride = toolOwnsUvContent ? uvContentEntries : null;
            bool dataChanged = previewCacheVersion != ctx.PreviewCacheVersion;
            if (reapplyPreview || dataChanged) RestorePreferredChannel();
            CollectViewportItems();
            canvas.EntriesOverride = viewportEntries;
            int contentKey = PreviewContentKey();
            bool contentChanged = uvContentKey != contentKey;
            if (contentChanged || dataChanged) {
                int channel = ctx.PreviewUvChannel;
                RestorePreferredChannel();
                if (channel != ctx.PreviewUvChannel) CollectViewportItems();
                canvas.ClearHoverState(); canvas.ClearFrameCaches();
                InvalidateViewportCaches(false);
            }
            bool channelSwitched = canvas.EnsurePreviewChannel(ctx);
            var mode = canvas.CurrentPreviewMode;
            if (!suspendedPreviewMode.HasValue && mode != UvCanvasView.PreviewMode.Off
                && (reapplyPreview || contentChanged || dataChanged || channelSwitched)) {
                ApplyPreviewMode(mode);
                // Shell/lightmap overrides replace and release materials. Collect their
                // new references before this frame renders, rather than one frame later.
                CollectViewportItems();
            }
            uvContentKey = PreviewContentKey();
            previewCacheVersion = ctx.PreviewCacheVersion;
            reapplyPreview = false;
        }

        int PreviewContentKey()
        {
            int key = toolOwnsUvContent ? 1 : 0;
            var lightmaps = canvas.CurrentPreviewMode == UvCanvasView.PreviewMode.Lightmap ? LightmapSettings.lightmaps : null;
            unchecked {
                key = (key * 31 + ctx.SourceLodIndex) * 31 + ctx.PreviewUvChannel;
                foreach (var entry in viewportEntries) {
                    var mesh = canvas.DisplayMesh(ctx, entry);
                    key = (key * 31 + (mesh ? mesh.GetInstanceID() : 0)) * 31 + (entry?.GetHashCode() ?? 0);
                    if (lightmaps != null && entry?.renderer != null) {
                        int index = entry.renderer.lightmapIndex;
                        key = (key * 31 + index) * 31 + entry.renderer.lightmapScaleOffset.GetHashCode();
                        var texture = index >= 0 && index < lightmaps.Length ? lightmaps[index]?.lightmapColor : null;
                        key = key * 31 + (texture ? texture.GetInstanceID() : 0);
                        if (texture) key = key * 31 + (int)texture.updateCount;
                    }
                }
            }
            return key;
        }

        // Back to the channel the user picked when the entries now shown carry it. Judged
        // on the mesh the context displays AT that channel — a repacked or transferred
        // mesh holds the UV1 its original lacks — and before this frame's items are
        // collected, so they are collected from that mesh.
        void RestorePreferredChannel()
        {
            if (ctx.PreviewUvChannel == preferredUvChannel) return;
            int fallback = ctx.PreviewUvChannel;
            ctx.PreviewUvChannel = preferredUvChannel;
            foreach (var entry in canvas.Entries(ctx))
            {
                var mesh = entry != null ? ctx.DMesh(entry) : null;
                if (mesh != null && canvas.RdUvCached(mesh, preferredUvChannel) != null) { canvas.ClearHoverState(); return; }
            }
            ctx.PreviewUvChannel = fallback;
        }

        void PrepareInspectionEntries()
        {
            var active = new HashSet<(int, int)>();
            for (int i = 0; i < viewportItems.Count; ++i) {
                var item = viewportItems[i]; var entry = viewportEntries[i];
                if (!item.mesh) continue;
                viewportEntries[i] = PrepareInspectionEntry(item, entry, active, out var readable);
                item.mesh = readable; viewportItems[i] = item;
            }
            foreach (var key in inspectionEntries.Keys.Where(key => !active.Contains(key)).ToArray()) inspectionEntries.Remove(key);
            inspection.Prune(viewportItems);
        }

        MeshEntry PrepareInspectionEntry(MeshViewport3D.Item item, MeshEntry entry, HashSet<(int, int)> active, out Mesh readable)
        {
            var key = (item.mesh.GetInstanceID(), entry?.GetHashCode() ?? 0);
            readable = inspection.Readable(item.mesh);
            if (entry != null) {
                canvas.DisplayMeshes[entry] = readable;
                return entry;
            }
            active.Add(key);
            if (!inspectionEntries.TryGetValue(key, out var proxy)) {
                proxy = new MeshEntry();
                inspectionEntries[key] = proxy;
            }
            proxy.originalMesh = readable;
            proxy.previewTexture = null;
            if (item.materials != null)
                foreach (var material in item.materials)
                    if (material && material.HasProperty("_MainTex") && material.mainTexture) { proxy.previewTexture = material.mainTexture; break; }
            return proxy;
        }

        void InvalidateViewportCaches(bool clearInspection = true)
        {
            viewportSpotPointerValid = false; selectViewportSpotWhenReady = false;
            canvas?.ClearFrameCaches();
            uvLayer?.Invalidate();
            viewport?.InvalidateCaches();
            if (clearInspection) inspection?.Clear(); else inspection?.InvalidateData();
            canvas?.ClearInspectionCache();
        }

        // Spot mode in 3D: the face under the mouse (camera ray against the context
        // meshes) feeds the same hover/selection state the UV canvas and the tools read.
        void OnPreviewReady()
        {
            if (canvas3D && canvas.SpotMode && viewportSpotPointerValid && !canvas.SpotSelectionLocked)
                RefreshViewportSpot();
        }

        void RefreshViewportSpot()
        {
            if (canvas.SpotSelectionLocked) return;
            bool found = uvLayer.Pick(viewport, canvas, ctx, viewportSpotPointer, viewportItems, viewportEntries,
                out var hit, out var debug, out var world);
            canvas.ApplySpotHit(found, hit, debug, world);
            if (selectViewportSpotWhenReady && found) {
                canvas.SelectSpotHover(); selectViewportSpotWhenReady = false;
            }
        }

        void HandleViewportSpot(Rect rect, Event e)
        {
            if (!canvas.SpotMode) { viewportSpotPointerValid = false; return; }
            if (!UvCanvasView.IsSpotInput(e.type)) return;
            viewport.PrepareRect(rect, e.type);
            if (!rect.Contains(e.mousePosition)) {
                viewportSpotPointerValid = false; selectViewportSpotWhenReady = false;
                if (!canvas.SpotSelectionLocked) canvas.ClearSpotHover();
                return;
            }
            bool select = e.type == EventType.MouseDown && e.button == 0 && !e.alt;
            if (!canvas.SpotSelectionLocked) {
                viewportSpotPointer = e.mousePosition; viewportSpotPointerValid = true;
                selectViewportSpotWhenReady = select;
                RefreshViewportSpot();
                Repaint(); SceneView.RepaintAll();
            }
            if (select) {
                if (e.clickCount == 2 && canvas.HasSelectedShell) {
                    try { canvas.OnDoubleClickShell?.Invoke(canvas.SelectedShell); }
                    catch (Exception ex) { UvtLog.Error($"[3D] Double-click shell focus failed: {ex.Message}"); }
                }
                // Surface selection consumes the left click before orbit input sees it.
                e.Use();
            }
        }

        void DrawViewport()
        {
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            var items = viewportItems;
            viewport.ViewProjection = canvas3D ? MeshViewport3D.Projection.Perspective : (MeshViewport3D.Projection)Mathf.Max(1, planarProjection);
            HandleCanvasModeSwitchInput(rect);
            HandleCanvasModeKey(rect);
            if (items.Count == 0)
            {
                EditorGUI.DrawRect(rect, viewport.Background);
                GUI.Label(rect, "No meshes for this LOD.", new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleCenter });
            }
            else
            {
                var e = Event.current;
                viewport.PrepareRect(rect, e.type);
                if (inspectMesh && e.type == EventType.MouseDown && e.button == 0 && e.control && rect.Contains(e.mousePosition) &&
                    viewport.TryScreenRay(e.mousePosition, out var origin, out var direction)) {
                    if (inspection.Pick(items, origin, direction, out int item)) { inspectedItem = item; Repaint(); }
                    e.Use();
                }
                HandleViewportSpot(rect, e);
                var tool3D = ActiveTool as IUvTool3D;
                viewport.Draw(rect, items, view =>
                {
                    uvLayer.Draw(view, canvas, ctx, viewportItems, viewportEntries);
                    tool3D?.OnDraw3D(view);
                });
                if (canvas.SpotMode) canvas.DrawShellInfoPanel(rect);
            }
            DrawCanvasModeSwitch(rect);
            if (Event.current.type == EventType.Repaint) canvasArea = rect;
        }

        // ═���════════════════════════════════���═════════════════════════
        //  Status Bar
        // ═══���═════════════════════════════════════════════════���══════

        void DrawStatusBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // ── Fill mode dropdown ──
            var modes = canvas.FillModes;
            if (modes != null && modes.Count > 0)
            {
                var names = modes.Select(m => m.name).ToArray();
                int idx = canvas.ActiveFillModeIndex;
                if (idx < 0 || idx >= names.Length) idx = 0;
                int newIdx = EditorGUILayout.Popup(idx, names, EditorStyles.toolbarPopup, GUILayout.Width(80));
                if (newIdx != canvas.ActiveFillModeIndex)
                    canvas.ActiveFillModeIndex = newIdx;

                // Show/hide toggle
                bool fillVisible = !canvas.FillHidden;
                bool next = GUILayout.Toggle(fillVisible, fillVisible ? "\u25C9" : "\u25CB", EditorStyles.toolbarButton, GUILayout.Width(22));
                if (next != fillVisible)
                    canvas.FillHidden = !next;
            }

            GUILayout.Space(2);

            // ── Wire / Bdr toggles ──
            canvas.ShowWireframe = GUILayout.Toggle(canvas.ShowWireframe, "Wire", EditorStyles.toolbarButton, GUILayout.Width(36));
            canvas.ShowBorder = GUILayout.Toggle(canvas.ShowBorder, new GUIContent("Borders", "UV shell boundaries and seams; turn Wire off to show only these."), EditorStyles.toolbarButton, GUILayout.Width(52));

            GUILayout.Space(4);

            // ── Fill alpha slider ──
            if (!canvas.FillHidden)
            {
                var alphaRect = GUILayoutUtility.GetRect(80, 14, GUILayout.Width(80));
                alphaRect.y += 2f;
                alphaRect.height = 12f;
                EditorGUI.DrawRect(alphaRect, new Color(0.15f, 0.15f, 0.15f));
                var fillRect = new Rect(alphaRect.x, alphaRect.y, alphaRect.width * Mathf.InverseLerp(0.05f, 0.6f, canvas.FillAlpha), alphaRect.height);
                EditorGUI.DrawRect(fillRect, new Color(0.35f, 0.55f, 0.85f, 0.7f));
                var labelStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
                GUI.Label(alphaRect, canvas.FillAlpha.ToString("F2"), labelStyle);
                var ev = Event.current;
                if ((ev.type == EventType.MouseDown || ev.type == EventType.MouseDrag) && alphaRect.Contains(ev.mousePosition))
                {
                    canvas.FillAlpha = Mathf.Lerp(0.05f, 0.6f, Mathf.Clamp01((ev.mousePosition.x - alphaRect.x) / alphaRect.width));
                    ev.Use();
                    Repaint();
                }
            }

            GUILayout.Space(4);

            // ── Preview mode dropdown ──
            {
                string[] previewModeLabels = { "Off", "Checker", "3D Shells", "Lightmap" };
                var bg2 = GUI.backgroundColor;
                if (canvas.CurrentPreviewMode != UvCanvasView.PreviewMode.Off)
                {
                    if (canvas.CurrentPreviewMode == UvCanvasView.PreviewMode.Checker) GUI.backgroundColor = new Color(1f, .4f, .3f);
                    else if (canvas.CurrentPreviewMode == UvCanvasView.PreviewMode.Lightmap) GUI.backgroundColor = new Color(.4f, .7f, 1f);
                    else GUI.backgroundColor = new Color(.35f, .85f, .4f);
                }
                var newMode = (UvCanvasView.PreviewMode)EditorGUILayout.Popup((int)canvas.CurrentPreviewMode, previewModeLabels, EditorStyles.toolbarPopup, GUILayout.Width(80));
                GUI.backgroundColor = bg2;
                if (newMode != canvas.CurrentPreviewMode)
                    ApplyPreviewMode(newMode);
            }

            // ── Checker color mode ──
            if (canvas.CurrentPreviewMode == UvCanvasView.PreviewMode.Checker)
            {
                GUILayout.Space(2);
                var bgCh = GUI.backgroundColor;
                // Color/Checker mode toggle
                GUI.backgroundColor = _checkerColorMode ? new Color(.3f, .9f, .5f) : new Color(.65f, .65f, .7f);
                if (GUILayout.Button(_checkerColorMode ? "Color" : "Grid", EditorStyles.toolbarButton, GUILayout.Width(38)))
                {
                    _checkerColorMode = !_checkerColorMode;
                    ApplyPreviewMode(UvCanvasView.PreviewMode.Off);
                    ApplyPreviewMode(UvCanvasView.PreviewMode.Checker);
                }
                GUI.backgroundColor = bgCh;

                // R/G channel mask (only in color mode)
                if (_checkerColorMode)
                {
                    GUI.backgroundColor = _checkerShowR ? new Color(1f, .3f, .3f) : new Color(.4f, .4f, .4f);
                    if (GUILayout.Button("R", EditorStyles.toolbarButton, GUILayout.Width(20)))
                    {
                        _checkerShowR = !_checkerShowR;
                        if (!_checkerShowR && !_checkerShowG) _checkerShowG = true;
                        ApplyPreviewMode(UvCanvasView.PreviewMode.Off);
                        ApplyPreviewMode(UvCanvasView.PreviewMode.Checker);
                    }
                    GUI.backgroundColor = _checkerShowG ? new Color(.3f, 1f, .3f) : new Color(.4f, .4f, .4f);
                    if (GUILayout.Button("G", EditorStyles.toolbarButton, GUILayout.Width(20)))
                    {
                        _checkerShowG = !_checkerShowG;
                        if (!_checkerShowR && !_checkerShowG) _checkerShowR = true;
                        ApplyPreviewMode(UvCanvasView.PreviewMode.Off);
                        ApplyPreviewMode(UvCanvasView.PreviewMode.Checker);
                    }
                    GUI.backgroundColor = bgCh;
                }
            }

            // ── Lightmap exposure slider ──
            if (canvas.CurrentPreviewMode == UvCanvasView.PreviewMode.Lightmap)
            {
                GUILayout.Space(2);
                var expRect = GUILayoutUtility.GetRect(60, 14, GUILayout.Width(60));
                expRect.y += 2f;
                expRect.height = 12f;
                EditorGUI.DrawRect(expRect, new Color(0.15f, 0.15f, 0.15f));
                var expFillRect = new Rect(expRect.x, expRect.y, expRect.width * Mathf.InverseLerp(0f, 2f, canvas.LmExposure), expRect.height);
                EditorGUI.DrawRect(expFillRect, new Color(0.85f, 0.7f, 0.3f, 0.7f));
                var expLabelStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
                GUI.Label(expRect, "E:" + canvas.LmExposure.ToString("F2"), expLabelStyle);
                var ev2 = Event.current;
                if ((ev2.type == EventType.MouseDown || ev2.type == EventType.MouseDrag) && expRect.Contains(ev2.mousePosition))
                {
                    canvas.LmExposure = Mathf.Lerp(0f, 2f, Mathf.Clamp01((ev2.mousePosition.x - expRect.x) / expRect.width));
                    ev2.Use();
                    Repaint();
                }
            }

            GUILayout.Space(6);

            // ── Status info ──
            var ee = canvas.Entries(ctx);
            int tV = 0, tT = 0;
            foreach (var e in ee)
            {
                Mesh m = canvas.DisplayMesh(ctx, e);
                if (m == null)
                {
                    continue;
                }

                tV += m.vertexCount;
                tT += m.triangles.Length / 3;
            }
            string hoverInfo = canvas.HoverHitValid
                ? $" | UV:{canvas.UvSpot.x:F3},{canvas.UvSpot.y:F3} S:{canvas.HoveredShellId}"
                : (canvas.SpotMode ? " | UV:--" : string.Empty);
            string what = toolOwnsUvContent ? (ActiveTool?.ToolName ?? "Mesh") + " result" : "LOD" + ctx.PreviewLod + " " + ee.Count + "m";
            EditorGUILayout.LabelField(what + " V:" + tV + " T:" + tT + " " + ("UV" + ctx.PreviewUvChannel) + hoverInfo, EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            // ── Tool status ──
            ActiveTool?.OnDrawStatusBar();

            EditorGUILayout.EndHorizontal();
        }

        // ════���═════════════════════════════════��═════════════════════
        //  Mesh Group Panel
        // ═══════��═══════════��════════════════════════════════════════

        Vector2 meshGroupScroll;
        const float meshGroupPanelW = 160f;

        void DrawMeshGroupPanel()
        {
            var groupKeys = ctx.BuildGroupKeys(ctx.PreviewLod);
            if (groupKeys.Count <= 1) { ctx.IsolatedMeshGroup = -1; return; }

            // Resolve stored group key → index for current LOD
            if (!string.IsNullOrEmpty(ctx.IsolatedMeshGroupKey))
            {
                int idx = groupKeys.IndexOf(ctx.IsolatedMeshGroupKey);
                ctx.IsolatedMeshGroup = idx; // -1 if not found → shows "All"
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MaxWidth(meshGroupPanelW), GUILayout.MinWidth(40));

            var bg = GUI.backgroundColor;
            if (ctx.IsolatedMeshGroup < 0) GUI.backgroundColor = new Color(.35f, .65f, 1f);
            if (GUILayout.Button("All", EditorStyles.miniButton))
            {
                ctx.IsolatedMeshGroup = -1;
                ctx.IsolatedMeshGroupKey = null;
            }
            GUI.backgroundColor = bg;

            meshGroupScroll = EditorGUILayout.BeginScrollView(meshGroupScroll);
            for (int i = 0; i < groupKeys.Count; i++)
            {
                bool active = ctx.IsolatedMeshGroup == i;
                // Tint each group button with the same per-group palette
                // entry the Prefab Builder hierarchy uses, so the user can
                // visually correlate the chain they see in the hierarchy
                // with the group they're selecting here. Active selection
                // overrides with the green highlight; non-selected buttons
                // get the group's hue at full strength.
                Color groupColor = MeshGroupColors.GetColor(groupKeys[i]);
                GUI.backgroundColor = active
                    ? new Color(.35f, .85f, .4f)
                    : groupColor;
                if (GUILayout.Button(groupKeys[i], EditorStyles.miniButton))
                {
                    if (active)
                    {
                        ctx.IsolatedMeshGroup = -1;
                        ctx.IsolatedMeshGroupKey = null;
                    }
                    else
                    {
                        ctx.IsolatedMeshGroup = i;
                        ctx.IsolatedMeshGroupKey = groupKeys[i];
                    }
                }
                GUI.backgroundColor = bg;
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════��═════════════════════════
        //  Tool Switching
        // ══════════════════════════���═════════════════════════���═══════

        void OnModulesChanged() { reloadModules = true; Repaint(); }

        void ConfigureModules()
        {
            var previous = ActiveTool;
            var settings = MeshLabProjectSettings.Instance;
            var registry = new MeshLabModuleRegistry(settings.disabledToolIds, settings.disabledLibraryIds);
            var next = registry.CreateTools(MeshLabModuleRegistry.ToolTypes(), Repaint, tools);
            bool keepActive = previous != null && next.Contains(previous) && (DebugUi.Enabled || !(previous is IUvToolDebugOnly));
            if (!keepActive) DeactivateTool();
            modules = registry;
            tools = next;
            activeToolIndex = keepActive ? tools.IndexOf(previous) : tools.FindIndex(t => DebugUi.Enabled || !(t is IUvToolDebugOnly));
            if (!keepActive) ActivateTool();
        }

        void BeforeAssetWrite()
        {
            if (!suspendedPreviewMode.HasValue) suspendedPreviewMode = canvas.CurrentPreviewMode;
            (ActiveTool as IUvToolAssetLifecycle)?.BeforeAssetWrite();
            RestorePreviewOverrides();
            canvas.CurrentPreviewMode = suspendedPreviewMode.Value;
        }

        void AfterAssetWrite()
        {
            if (ActiveTool is IUvToolAssetLifecycle lifecycle) lifecycle.AfterAssetWrite();
            else ActiveTool?.OnRefresh();
            InvalidateViewportCaches();
            Repaint();
        }

        void FinishAssetWrite()
        {
            if (!suspendedPreviewMode.HasValue) return;
            canvas.CurrentPreviewMode = suspendedPreviewMode.Value;
            suspendedPreviewMode = null;
            reapplyPreview = true;
            InvalidateViewportCaches();
            Repaint();
        }

        void DeactivateTool()
        {
            SaveFillPreference();
            try { ActiveTool?.OnDeactivate(); }
            catch (Exception ex) { UvtLog.Warn("[Modules] Deactivation failed: " + ex.Message); }
            if (canvas == null) return;
            ApplyPreviewMode(UvCanvasView.PreviewMode.Off);
            canvas.OnDoubleClickShell = null;
            canvas.EntriesOverride = null;
            canvas.SetFillModes(new List<UvCanvasView.FillModeEntry>());
            canvas.ClearHoverState(); canvas.ClearFrameCaches();
            InvalidateViewportCaches();
        }

        void ActivateTool()
        {
            if (ActiveTool == null) return;
            try
            {
                ActiveTool.OnActivate(ctx, canvas);
                canvas.SetFillModes(ActiveTool.GetFillModes()?.ToList() ?? new List<UvCanvasView.FillModeEntry>());
                RestoreFillPreference();
            }
            catch (Exception ex)
            {
                UvtLog.Warn("[Modules] Activation failed for " + ActiveTool.ToolId + ": " + ex.Message);
                DeactivateTool();
                // Remove only the failed module and continue with another available tool.
                tools.RemoveAt(activeToolIndex);
                activeToolIndex = tools.FindIndex(t => DebugUi.Enabled || !(t is IUvToolDebugOnly));
                ActivateTool();
            }
        }

        void SwitchTool(int index)
        {
            if (index == activeToolIndex) return;
            DeactivateTool();
            activeToolIndex = index;
            ActivateTool();
            Repaint();
        }

        // ════��═══════════════════════════════��═══════════════════════
        //  Helpers
        // ════════════════════════════════════════════════════════════

        void DrawSidebarFooter()
        {
            bool hasMeshEntries = ctx != null && ctx.MeshEntries != null && ctx.MeshEntries.Count > 0;
            bool hasFbxWorkflow = ctx != null && !string.IsNullOrEmpty(ctx.SourceFbxPath);
            if (!hasMeshEntries || modules.LibraryUnavailableReason(MeshLabLibraries.Assets) != null) return;

            EditorGUILayout.Space(2);
            var r = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(.3f, .3f, .3f));
            EditorGUILayout.Space(2);

            if (GUILayout.Button("Save Mesh Assets", EditorStyles.miniButton))
            {
                ctx.Assets.SaveAllPublic();
            }

            if (!hasFbxWorkflow) return;

            EditorGUILayout.Space(4);

            var bg = GUI.backgroundColor;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (!PostprocessorDefineManager.IsEnabled())
            {
                EditorGUILayout.HelpBox(
                    "Sidecar UV2 Mode is OFF. FBX overwrite/apply will use temporary sidecar replay for this import only.",
                    MessageType.Info);
            }

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = new Color(.95f, .6f, .2f);
            if (GUILayout.Button("Overwrite FBX", GUILayout.Height(24)))
            {
                ctx.Assets.ExportFbxPublic(true);
            }
            GUI.backgroundColor = new Color(.4f, .7f, .95f);
            if (GUILayout.Button("Export New FBX", GUILayout.Height(24)))
            {
                ctx.Assets.ExportFbxPublic(false);
            }
            GUI.backgroundColor = bg;
            EditorGUILayout.EndHorizontal();
#else
            EditorGUILayout.HelpBox("Install com.unity.formats.fbx for FBX export.", MessageType.Info);
#endif

            if (GUILayout.Button("Apply UV2 (sidecar)", EditorStyles.miniButton))
            {
                ctx.Assets.ApplyUv2Public();
            }

            if (!string.IsNullOrEmpty(ctx.SourceFbxPath)
                && GUILayout.Button("Backup from main", EditorStyles.miniButton))
            {
                BackupFbxFromGitMain(ctx.SourceFbxPath);
            }
        }

        internal static void BackupFbxFromGitMain(string assetPath)
        {
            try
            {
                string repoRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
                string fullPath = System.IO.Path.GetFullPath(assetPath);
                string relativePath = assetPath.Replace('\\', '/');
                // Validate before the path becomes part of a git revision: the
                // backup only ever reads repo-relative Assets/ paths. A
                // validated "main:<path>" operand can never be parsed by git
                // as an option, so nothing user-controlled can alter the
                // command's meaning.
                if (!relativePath.StartsWith("Assets/", StringComparison.Ordinal) || relativePath.Contains(".."))
                {
                    UvtLog.Error($"[Backup] '{relativePath}' is not a repo-relative Assets path.");
                    return;
                }

                string dir = System.IO.Path.GetDirectoryName(fullPath);
                string name = System.IO.Path.GetFileNameWithoutExtension(fullPath);
                string ext = System.IO.Path.GetExtension(fullPath);
                string backupPath = System.IO.Path.Combine(dir, name + "_main" + ext);
                string backupAssetPath = (System.IO.Path.GetDirectoryName(assetPath) + "/" + name + "_main" + ext)
                    .Replace('\\', '/');
                string tempBackupPath = backupPath + ".tmp";

                if (!RunGit(repoRoot, new[] { "cat-file", "-e", "main:" + relativePath }, out _, out string existsErr))
                {
                    UvtLog.Error($"[Backup] '{relativePath}' does not exist on branch 'main'. {existsErr.Trim()}");
                    return;
                }

                using (var proc = StartGitBinary(repoRoot, "cat-file", "--filters", "main:" + relativePath))
                {
                    var stderrBuf = new System.Text.StringBuilder();
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuf.AppendLine(e.Data); };
                    proc.BeginErrorReadLine();
                    using (var fs = new System.IO.FileStream(tempBackupPath,
                        System.IO.FileMode.Create, System.IO.FileAccess.Write))
                    {
                        proc.StandardOutput.BaseStream.CopyTo(fs);
                    }
                    proc.WaitForExit();
                    string err = stderrBuf.ToString();

                    if (proc.ExitCode != 0)
                    {
                        UvtLog.Error($"[Backup] Failed to extract '{relativePath}' from main: {err.Trim()}");
                        if (System.IO.File.Exists(tempBackupPath))
                            System.IO.File.Delete(tempBackupPath);
                        return;
                    }
                }

                var tempInfo = new System.IO.FileInfo(tempBackupPath);
                if (!tempInfo.Exists || tempInfo.Length == 0)
                {
                    if (System.IO.File.Exists(tempBackupPath))
                        System.IO.File.Delete(tempBackupPath);
                    UvtLog.Error($"[Backup] Extracted '{relativePath}' from main, but the result is empty.");
                    return;
                }

                if (System.IO.File.Exists(backupPath))
                    System.IO.File.Delete(backupPath);
                System.IO.File.Move(tempBackupPath, backupPath);

                AssetDatabase.Refresh();
                UvtLog.Info($"[Backup] Saved main branch version → {backupAssetPath}");
            }
            catch (System.Exception ex)
            {
                UvtLog.Error($"[Backup] Failed: {ex.Message}");
            }
        }

        // The only git options this package ever passes; anything else that
        // starts with '-' is refused by StartGitBinary below.
        static readonly System.Collections.Generic.HashSet<string> AllowedGitOptions =
            new System.Collections.Generic.HashSet<string> { "-e", "--filters" };

        internal static System.Diagnostics.Process StartGitBinary(string workingDirectory, params string[] arguments)
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("git")
                {
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            // One verbatim token per git argument via ArgumentList — no
            // command-line string is assembled. Options are allow-listed and
            // callers validate their operands, so no argument can smuggle an
            // extra git option or command.
            foreach (var argument in arguments)
            {
                if (argument.StartsWith("-", StringComparison.Ordinal) && !AllowedGitOptions.Contains(argument))
                    throw new System.ArgumentException("Refusing unexpected git option: " + argument);
                process.StartInfo.ArgumentList.Add(argument);
            }
            process.Start();
            return process;
        }

        internal static bool RunGit(string workingDirectory, string[] arguments, out string stdout, out string stderr)
        {
            using (var proc = StartGitBinary(workingDirectory, arguments))
            {
                var stderrBuf = new System.Text.StringBuilder();
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuf.AppendLine(e.Data); };
                proc.BeginErrorReadLine();
                stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                stderr = stderrBuf.ToString();
                return proc.ExitCode == 0;
            }
        }

        internal static void NukeAllSidecarsStatic() => NukeAllSidecars();

        static void NukeAllSidecars()
        {
            // Find and delete all sidecar assets
            var sidecarGuids = AssetDatabase.FindAssets("_uv2data t:Uv2DataAsset");
            var sidecarPaths = new List<string>();
            foreach (var guid in sidecarGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && path.EndsWith("_uv2data.asset", System.StringComparison.OrdinalIgnoreCase))
                    sidecarPaths.Add(path);
            }

            if (sidecarPaths.Count == 0)
            {
                EditorUtility.DisplayDialog("Nothing to clean",
                    "No sidecar assets found in the project.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("Delete All Sidecars",
                $"This will delete {sidecarPaths.Count} sidecar asset(s).\n\n" +
                "Import settings (weldVertices etc.) are left as-is —\n" +
                "they do not break meshes.\n\nThis cannot be undone.",
                "Delete", "Cancel"))
                return;

            int deleted = 0;
            foreach (var sp in sidecarPaths)
            {
                if (AssetDatabase.DeleteAsset(sp))
                    deleted++;
            }

            // Offer to disable persistent sidecar mode if active
            if (PostprocessorDefineManager.IsEnabled())
            {
                if (EditorUtility.DisplayDialog("Disable Sidecar Mode?",
                    "Sidecar UV2 Mode is currently enabled.\n" +
                    "Disable it to stop persistent UV replay on future reimports?\n" +
                    "One-shot replay during overwrite/apply will still work.",
                    "Disable", "Keep Enabled"))
                    PostprocessorDefineManager.SetEnabled(false);
            }

            UvtLog.Info($"[Cleanup] Deleted {deleted} sidecar(s).");
            EditorUtility.DisplayDialog("Done", $"Deleted {deleted} sidecar(s).", "OK");
        }

        void SetPreviewLod(int lodIndex)
        {
            if (ctx.LodCount <= 0) return;
            int clamped = Mathf.Clamp(lodIndex, 0, ctx.LodCount - 1);
            if (ctx.PreviewLod == clamped) return;
            ctx.PreviewLod = clamped;
            canvas.ClearHoverState();
            if (ctx.LodGroup != null) ctx.LodGroup.ForceLOD(clamped);
            // The canvas still lists the previous LOD's entries: CollectCanvasEntries
            // re-applies the active 3D preview next frame, to the renderers now shown.
            reapplyPreview = true;
            Repaint();
        }

        // ── Lightmap preview state ──
        struct LightmapBackup
        {
            public Renderer renderer;
            public Material[] origMaterials;
            public MeshFilter meshFilter;
            public Mesh origMesh;
            public Mesh tempMesh;
            public Material tempMat;
        }
        readonly List<LightmapBackup> lightmapBackups = new List<LightmapBackup>();
        Material lightmapPreviewMat;

        // Read authored materials without suspending the user's preview or replacing
        // renderer references. Texture metrics must not use checker/lightmap textures.
        internal static Material[] OriginalPreviewMaterials(Renderer renderer)
        {
            if (CheckerTexturePreview.TryGetOriginalMaterials(renderer, out var materials)
                || ShellColorModelPreview.TryGetOriginalMaterials(renderer, out materials))
                return materials;
            foreach (var hub in Resources.FindObjectsOfTypeAll<UvToolHub>()) {
                foreach (var backup in hub.lightmapBackups)
                    if (backup.renderer == renderer) return backup.origMaterials;
                if (hub.ActiveTool is UvTransferWorkflow workflow
                    && workflow.TryGetOriginalLightmapMaterials(renderer, out materials))
                    return materials;
            }
            return renderer.sharedMaterials;
        }

        // ── Shell color palette ──
        static readonly Color32[] shellPalette = {
            new Color(.20f,.60f,1f),  new Color(1f,.40f,.20f),
            new Color(.30f,.85f,.40f),new Color(.90f,.25f,.60f),
            new Color(.95f,.85f,.20f),new Color(.55f,.30f,.90f),
            new Color(0f,.80f,.80f),  new Color(.85f,.55f,.20f),
            new Color(.60f,.90f,.20f),new Color(.90f,.20f,.20f),
            new Color(.40f,.40f,.90f),new Color(.90f,.70f,.40f),
        };

        void RestorePreviewOverrides()
        {
            canvas.CheckerEnabled = false;
            if (CheckerTexturePreview.IsActive) CheckerTexturePreview.Restore();
            if (ShellColorModelPreview.IsActive)
                ShellColorModelPreview.Restore();
            RestoreLightmapPreview();
        }

        void ApplyPreviewMode(UvCanvasView.PreviewMode newMode)
        {
            if (suspendedPreviewMode.HasValue) {
                suspendedPreviewMode = newMode;
                canvas.CurrentPreviewMode = newMode;
                return;
            }
            RestorePreviewOverrides();

            canvas.CurrentPreviewMode = newMode;

            // Ensure fill is visible in preview modes
            if (newMode != UvCanvasView.PreviewMode.Off)
            {
                canvas.FillHidden = false;
                if (canvas.FillAlpha < 0.3f) canvas.FillAlpha = 0.45f;
            }

            switch (newMode)
            {
                case UvCanvasView.PreviewMode.Checker:
                    _checkerUvChannel = ctx.PreviewUvChannel;
                    canvas.CheckerColorMode = _checkerColorMode;
                    canvas.CheckerShowR = _checkerShowR;
                    canvas.CheckerShowG = _checkerShowG;
                    var checkerEntries = new List<(Renderer renderer, Mesh meshWithUv2)>();
                    bool hasCheckerUv = canvas.HasPreviewChannel(ctx, _checkerUvChannel);
                    foreach (var e in canvas.Entries(ctx))
                    {
                        if (e == null || !e.include || e.renderer == null) continue;
                        Mesh uvMesh = ctx.DMesh(e);
                        var readable = canvas.DisplayMesh(ctx, e);
                        if (uvMesh != null && readable != null && canvas.RdUvCached(readable, _checkerUvChannel) != null)
                            checkerEntries.Add((e.renderer, uvMesh));
                    }
                    if (hasCheckerUv)
                    {
                        canvas.CheckerEnabled = true;
                        if (checkerEntries.Count > 0) CheckerTexturePreview.Apply(checkerEntries, _checkerUvChannel,
                            _checkerColorMode, _checkerShowR, _checkerShowG);
                    }
                    else
                    {
                        UvtLog.Warn($"[Checker] No meshes with UV data on channel {_checkerUvChannel}.");
                    }
                    break;

                case UvCanvasView.PreviewMode.Shells3D:
                {
                    var shellEntries = new List<(Renderer renderer, Mesh sourceMesh)>();
                    bool hasShellUv = canvas.HasPreviewChannel(ctx, ctx.PreviewUvChannel);
                    foreach (var e in canvas.Entries(ctx))
                    {
                        if (e == null || !e.include || e.renderer == null) continue;
                        Mesh mesh = ctx.DMesh(e);
                        if (mesh != null) shellEntries.Add((e.renderer, mesh));
                    }
                    if (hasShellUv)
                    {
                        if (shellEntries.Count > 0) {
                            var cache = new ShellColorModelPreview.PreviewShellCache(ctx.PreviewUvChannel);
                            ShellColorModelPreview.Apply(shellEntries, shellPalette, cache);
                        }
                    }
                    else
                    {
                        UvtLog.Warn($"[Shells3D] No meshes with UV{ctx.PreviewUvChannel}.");
                    }
                    break;
                }

                case UvCanvasView.PreviewMode.Lightmap:
                {
                    foreach (var e in ctx.ForLod(ctx.PreviewLod))
                    {
                        if (e.renderer == null) continue;
                        int lmIdx = e.renderer.lightmapIndex;
                        if (lmIdx < 0 || lmIdx >= LightmapSettings.lightmaps.Length) continue;
                        var lmData = LightmapSettings.lightmaps[lmIdx];
                        if (lmData.lightmapColor == null) continue;
                        var so = e.renderer.lightmapScaleOffset;

                        if (lightmapPreviewMat == null)
                        {
                            var shader = Shader.Find("Unlit/Texture");
                            if (shader == null) continue;
                            lightmapPreviewMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                        }
                        var mat = new Material(lightmapPreviewMat) { hideFlags = HideFlags.HideAndDontSave };
                        mat.mainTexture = lmData.lightmapColor;
                        mat.mainTextureScale = Vector2.one;
                        mat.mainTextureOffset = Vector2.zero;

                        var mf = e.renderer.GetComponent<MeshFilter>();
                        Mesh srcMesh = ctx.DMesh(e);
                        Mesh tempMesh = null;
                        if (srcMesh != null)
                        {
                            tempMesh = MeshAccess.ReadableCopy(srcMesh);
                            tempMesh.name = srcMesh.name + "_LmPreview";
                            tempMesh.hideFlags = HideFlags.HideAndDontSave;
                            var uv1 = new List<Vector2>();
                            tempMesh.GetUVs(1, uv1);
                            if (uv1.Count == tempMesh.vertexCount)
                            {
                                var lmUvs = new Vector2[uv1.Count];
                                for (int i = 0; i < uv1.Count; i++)
                                    lmUvs[i] = new Vector2(uv1[i].x * so.x + so.z, uv1[i].y * so.y + so.w);
                                tempMesh.uv = lmUvs;
                            }
                        }

                        lightmapBackups.Add(new LightmapBackup
                        {
                            renderer = e.renderer,
                            origMaterials = e.renderer.sharedMaterials,
                            meshFilter = mf,
                            origMesh = mf != null ? mf.sharedMesh : null,
                            tempMesh = tempMesh,
                            tempMat = mat
                        });
                        if (mf != null && tempMesh != null) mf.sharedMesh = tempMesh;
                        var mats = new Material[e.renderer.sharedMaterials.Length];
                        for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                        e.renderer.sharedMaterials = mats;
                    }
                    if (lightmapBackups.Count == 0)
                    {
                        UvtLog.Warn("[Lightmap] No lightmapped meshes found.");
                    }
                    break;
                }

                case UvCanvasView.PreviewMode.Off:
                    break;
            }
            Repaint();
            SceneView.RepaintAll();
        }

        internal void RestoreLightmapPreviewSafe() => RestoreLightmapPreview();

        void RestoreLightmapPreview()
        {
            foreach (var b in lightmapBackups)
            {
                if (b.renderer != null) b.renderer.sharedMaterials = b.origMaterials;
                if (b.meshFilter != null && b.origMesh != null) b.meshFilter.sharedMesh = b.origMesh;
                if (b.tempMesh != null) DestroyImmediate(b.tempMesh);
                if (b.tempMat != null) DestroyImmediate(b.tempMat);
            }
            lightmapBackups.Clear();
        }

        void OnPreviewChannelChanged(int newChannel)
        {
            ctx.PreviewUvChannel = preferredUvChannel = newChannel;
            reapplyPreview = true;
            canvas.ClearHoverState();
            canvas.HoveredShellDebug = null;
            canvas.SelectedShellDebug = null;
            Repaint();
            SceneView.RepaintAll();
        }

        void UpdateSelectedSidecar()
        {
            selectedSidecarPath = selectedFbxPath = selectedResetLabel = null;
            // The loaded entries' FBX files, else those under the selected object.
            var fbxPaths = SidecarStore.FbxPaths(ctx?.MeshEntries);
            if (fbxPaths.Count == 0)
                fbxPaths = SidecarStore.FbxPaths(Selection.activeGameObject);
            if (!SidecarStore.TryFindFirst(fbxPaths, out selectedFbxPath, out selectedSidecarPath))
                return;
            selectedResetLabel = System.IO.Path.GetFileNameWithoutExtension(selectedFbxPath);
        }

        void ResetSelectedUv2()
        {
            if (string.IsNullOrEmpty(selectedSidecarPath) || string.IsNullOrEmpty(selectedFbxPath))
                return;

            if (!EditorUtility.DisplayDialog("Reset UV2",
                $"Delete UV2 sidecar for '{selectedResetLabel}'?\nFBX will be reimported without UV2.",
                "Delete", "Cancel"))
                return;

            using var previewWrite = ctx.Assets.PreservePreviewDuringWrite();
            BeforeAssetWrite();
            SidecarStore.Delete(new[] { selectedFbxPath });
            AssetDatabase.Refresh();

            {
                var imp = AssetImporter.GetAtPath(selectedFbxPath) as ModelImporter;
                if (imp != null)
                {
                    if (imp.generateSecondaryUV) imp.generateSecondaryUV = false;
                    if (imp.isReadable) imp.isReadable = false;
                }
            }

            AssetDatabase.ImportAsset(selectedFbxPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            UvtLog.Info($"[Reset] Deleted sidecar for '{selectedResetLabel}', reimported FBX");

            if (ctx?.LodGroup != null)
            {
                ctx.Refresh(ctx.LodGroup);
                ActiveTool?.OnRefresh(); InvalidateViewportCaches();
            }

            ctx.PostResetColoring = true;
            ctx.ShellColorKeyCache.Clear();
            ctx.ShellColorKeyCacheDirty = true;

            UpdateSelectedSidecar();
            Repaint();
        }
    }
}
