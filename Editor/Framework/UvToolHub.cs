// UvToolHub.cs — Main EditorWindow for Mesh Lab.
// Toolbar at top selects the active IUvTool; sidebar shows that tool's controls.
// UV canvas is a shared UvCanvasView component.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    public class UvToolHub : EditorWindow
    {
        const string WindowTitle = nameof(UvToolHub);
        const string WindowBrand = "Mesh Lab";

        // ── Tool registry ──
        List<IUvTool> tools;
        int activeToolIndex;
        IUvTool ActiveTool => tools != null && activeToolIndex >= 0 && activeToolIndex < tools.Count ? tools[activeToolIndex] : null;

        // ── Shared components ──
        UvToolContext ctx;
        UvCanvasView canvas;

        // ── Layout ──
        float sideW = 300f;
        bool sideDragging;
        Vector2 sideScroll;
        int _cachedLodCount;
        int _cachedRendererCount;
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

        [MenuItem("Tools/Mesh Lab")]
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
            canvas = new UvCanvasView();
            canvas.Init();
            canvas.RequestRepaint = Repaint;

            // Discover all IUvTool implementations via reflection
            tools = new List<IUvTool>();
            var toolTypes = UnityEditor.TypeCache.GetTypesDerivedFrom<IUvTool>()
                .Where(t => !t.IsAbstract && !t.IsInterface);
            foreach (var type in toolTypes)
            {
                try
                {
                    var tool = (IUvTool)Activator.CreateInstance(type);
                    tool.RequestRepaint = Repaint;
                    tools.Add(tool);
                }
                catch (Exception ex)
                {
                    UvtLog.Warn($"[Hub] Failed to create tool {type.Name}: {ex.Message}");
                }
            }
            tools = tools.OrderBy(t => t.ToolOrder).ToList();

            activeToolIndex = 0;
            if (ActiveTool != null)
            {
                ActiveTool.OnActivate(ctx, canvas);
                var modes = ActiveTool.GetFillModes();
                canvas.SetFillModes(modes != null ? modes.ToList() : new List<UvCanvasView.FillModeEntry>());
            }
            SelectToolById(pendingToolId);

            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.undoRedoPerformed += OnUndoRedo;
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
            // Cleanup order matters:
            // 1. Deactivate active tool (lets it restore its own preview state)
            // 2. Restore all remaining previews (checker, shell color, lightmap)
            // 3. Reset LOD forcing so the scene renders normally
            // 4. Cleanup canvas GL resources
            // 5. Destroy working meshes and restore fbxMesh on MeshFilter

            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;

            ActiveTool?.OnDeactivate();

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
                    // Restore preview before switching LODGroup
                    if (canvas.CurrentPreviewMode != UvCanvasView.PreviewMode.Off)
                        ApplyPreviewMode(UvCanvasView.PreviewMode.Off);
                    RestoreWorkingMeshes();
                    ctx.Refresh(lg);
                    _cachedLodCount = ctx.LodCount;
                    _cachedRendererCount = CountValidRenderers(lg);
                    ActiveTool?.OnRefresh();
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
                                             || LodGenerationTool.FindLodSiblings(go) != null;
                        if (hasMeshRelevance)
                        {
                            if (canvas.CurrentPreviewMode != UvCanvasView.PreviewMode.Off)
                                ApplyPreviewMode(UvCanvasView.PreviewMode.Off);
                            if (ctx.LodGroup != null)
                                ctx.LodGroup.ForceLOD(-1);
                            RestoreWorkingMeshes();

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
                            ActiveTool?.OnRefresh();
                        }
                    }
                }
            }

            UpdateSelectedSidecar();
            Repaint();
        }

        void OnUndoRedo()
        {
            if (ctx == null) return;

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

            ActiveTool?.OnRefresh();
            Repaint();
        }

        void OnGUI()
        {
            if (tools == null || tools.Count == 0)
            {
                EditorGUILayout.HelpBox("No UV tools found. Ensure IUvTool implementations exist.", MessageType.Warning);
                return;
            }

            // Detect LODGroup structural changes (e.g. LOD deleted externally, renderer removed)
            if (ctx.LodGroup != null)
            {
                int rendererCount = CountValidRenderers(ctx.LodGroup);
                if (ctx.LodCount != _cachedLodCount || rendererCount != _cachedRendererCount)
                {
                    ctx.Refresh(ctx.LodGroup);
                    _cachedLodCount = ctx.LodCount;
                    _cachedRendererCount = CountValidRenderers(ctx.LodGroup);
                    ctx.PreviewLod = Mathf.Clamp(ctx.PreviewLod, 0, Mathf.Max(0, ctx.LodCount - 1));
                    ActiveTool?.OnRefresh();
                }
            }

            DrawHubToolbar();

            EditorGUILayout.BeginHorizontal();

            // ── Left sidebar ���─
            EditorGUILayout.BeginVertical(GUILayout.Width(sideW));
            sideScroll = EditorGUILayout.BeginScrollView(sideScroll);
            ActiveTool?.OnDrawSidebar();
            EditorGUILayout.EndScrollView();
            DrawSidebarFooter();
            EditorGUILayout.EndVertical();

            DrawResizeHandle();

            // ── Right: canvas toolbar + canvas + status ──
            EditorGUILayout.BeginVertical();
            DrawCanvasToolbar();

            bool showGroupPanel = ctx.RepackPerMesh && ctx.MeshGroupCount(ctx.PreviewLod) > 1;
            if (showGroupPanel)
                EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical();
            canvas.OnGUI(ctx,
                ActiveTool != null ? (Action<UvCanvasView, float, float, float>)ActiveTool.OnDrawCanvasOverlay : null);
            DrawStatusBar();
            EditorGUILayout.EndVertical();

            if (showGroupPanel)
            {
                DrawMeshGroupPanel();
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
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
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            for (int i = 0; i < tools.Count; i++)
            {
                var bg = GUI.backgroundColor;
                if (i == activeToolIndex)
                    GUI.backgroundColor = new Color(.35f, .65f, 1f);
                if (GUILayout.Button(tools[i].ToolName, EditorStyles.toolbarButton, GUILayout.MinWidth(80)))
                {
                    if (i != activeToolIndex)
                        SwitchTool(i);
                }
                GUI.backgroundColor = bg;
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField(BuildWindowBrandText(),
                EditorStyles.miniLabel, GUILayout.Width(220));
            GUILayout.Space(6);

            // ── Log level ──
            EditorGUILayout.LabelField("Log:", EditorStyles.miniLabel, GUILayout.Width(24));
            var lvl = (UvtLog.Level)EditorGUILayout.EnumPopup(UvtLog.Current, EditorStyles.toolbarPopup, GUILayout.Width(64));
            if (lvl != UvtLog.Current) UvtLog.Current = lvl;

            EditorGUILayout.EndHorizontal();
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

            // ── UV channel toggle ──
            {
                var bg = GUI.backgroundColor;
                for (int ch = 0; ch < 2; ch++)
                {
                    bool active = ctx.PreviewUvChannel == ch;
                    GUI.backgroundColor = active ? new Color(.4f, .55f, 1f) : new Color(.65f, .65f, .7f);
                    string lbl = ch == 0 ? "UV0" : "UV1";
                    if (GUILayout.Button(lbl, EditorStyles.toolbarButton, GUILayout.Width(34)))
                    {
                        if (!active) OnPreviewChannelChanged(ch);
                    }
                }
                GUI.backgroundColor = bg;
            }

            GUILayout.Space(6);

            // ── Spot / Lock / Clear ──
            bool spotNext = GUILayout.Toggle(canvas.SpotMode, "Spot", EditorStyles.toolbarButton, GUILayout.Width(52));
            if (spotNext != canvas.SpotMode)
            {
                canvas.SpotMode = spotNext;
                if (!canvas.SpotMode) canvas.ClearHoverState();
                SceneView.RepaintAll();
            }
            canvas.LockSelection = GUILayout.Toggle(canvas.LockSelection, "Lock", EditorStyles.toolbarButton, GUILayout.Width(40));
            using (new EditorGUI.DisabledScope(!canvas.HasSelectedShell))
            {
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(42)))
                {
                    canvas.HasSelectedShell = false;
                    canvas.SelectedShellDebug = null;
                }
            }

            GUILayout.Space(6);

            // ── Zoom + Fit ──
            canvas.Zoom = EditorGUILayout.Slider(canvas.Zoom, .01f, 20f, GUILayout.Width(90));
            if (GUILayout.Button("Fit", EditorStyles.toolbarButton, GUILayout.Width(28)))
                canvas.FitToUvBounds(ctx);

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
            canvas.ShowBorder = GUILayout.Toggle(canvas.ShowBorder, "Bdr", EditorStyles.toolbarButton, GUILayout.Width(30));

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

            // ── Checker UV channel selector + color mode ──
            if (canvas.CurrentPreviewMode == UvCanvasView.PreviewMode.Checker)
            {
                GUILayout.Space(2);
                var bgCh = GUI.backgroundColor;
                for (int ch = 0; ch < 8; ch++)
                {
                    // Check if any mesh has data on this channel
                    bool hasData = false;
                    foreach (var e in ctx.MeshEntries)
                    {
                        if (!e.include) continue;
                        Mesh m = e.transferredMesh ?? e.repackedMesh ?? e.originalMesh ?? e.fbxMesh;
                        if (m == null) continue;
                        var test = new List<Vector2>();
                        m.GetUVs(ch, test);
                        if (test.Count > 0) { hasData = true; break; }
                    }
                    if (!hasData) continue;

                    bool active = _checkerUvChannel == ch;
                    GUI.backgroundColor = active ? new Color(1f, .45f, .3f) : new Color(.65f, .65f, .7f);
                    if (GUILayout.Button("UV" + ch, EditorStyles.toolbarButton, GUILayout.Width(30)))
                    {
                        if (!active)
                        {
                            _checkerUvChannel = ch;
                            _checkerColorMode = ch >= 2;
                            ApplyPreviewMode(UvCanvasView.PreviewMode.Off);
                            ApplyPreviewMode(UvCanvasView.PreviewMode.Checker);
                        }
                    }
                }
                GUI.backgroundColor = bgCh;

                GUILayout.Space(4);

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
            var ee = ctx.ForLod(ctx.PreviewLod);
            int tV = 0, tT = 0;
            foreach (var e in ee) { Mesh m = ctx.DMesh(e); if (m == null) continue; tV += m.vertexCount; tT += m.triangles.Length / 3; }
            string hoverInfo = canvas.HoverHitValid
                ? $" | UV:{canvas.UvSpot.x:F3},{canvas.UvSpot.y:F3} S:{canvas.HoveredShellId}"
                : (canvas.SpotMode ? " | UV:--" : string.Empty);
            EditorGUILayout.LabelField("LOD" + ctx.PreviewLod + " " + ee.Count + "m V:" + tV + " T:" + tT + " " + (ctx.PreviewUvChannel == 0 ? "UV0" : "UV1") + hoverInfo, EditorStyles.miniLabel);

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
                if (active) GUI.backgroundColor = new Color(.35f, .85f, .4f);
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
                if (active) GUI.backgroundColor = bg;
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════��═════════════════════════
        //  Tool Switching
        // ══════════════════════════���═════════════════════════���═══════

        void SwitchTool(int index)
        {
            ActiveTool?.OnDeactivate();
            activeToolIndex = index;
            if (ActiveTool != null)
            {
                ActiveTool.OnActivate(ctx, canvas);
                var modes = ActiveTool.GetFillModes();
                canvas.SetFillModes(modes != null ? modes.ToList() : new List<UvCanvasView.FillModeEntry>());
            }
            Repaint();
        }

        // ════��═══════════════════════════════��═══════════════════════
        //  Helpers
        // ════════════════════════════════════════════════════════════

        void DrawSidebarFooter()
        {
            // Global cleanup — always visible, even without a LODGroup selected
            EditorGUILayout.Space(4);
            var bgNuke = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.7f, .15f, .15f);
            if (GUILayout.Button("Delete All Sidecars", GUILayout.Height(22)))
                NukeAllSidecars();
            GUI.backgroundColor = bgNuke;

            bool hasMeshEntries = ctx != null && ctx.MeshEntries != null && ctx.MeshEntries.Count > 0;
            bool hasFbxWorkflow = ctx != null && !string.IsNullOrEmpty(ctx.SourceFbxPath);
            if (!hasMeshEntries) return;
            EditorGUILayout.Space(2);
            var r = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(.3f, .3f, .3f));
            EditorGUILayout.Space(2);

            if (GUILayout.Button("Save Mesh Assets", EditorStyles.miniButton))
            {
                foreach (var tool in tools)
                    if (tool is LightmapTransferTool ltt) { ltt.SaveAllPublic(); break; }
            }

            if (!hasFbxWorkflow) return;

            EditorGUILayout.Space(4);

            var bg = GUI.backgroundColor;
            bool sidecarEnabled = PostprocessorDefineManager.IsEnabled();
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (!sidecarEnabled)
            {
                EditorGUILayout.HelpBox(
                    "Sidecar UV2 Mode is OFF. FBX overwrite/apply will use temporary sidecar replay for this import only, then UV sidecar entries will be removed again.",
                    MessageType.Info);
            }

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = new Color(.95f, .6f, .2f);
            if (GUILayout.Button("Overwrite FBX", GUILayout.Height(24)))
            {
                foreach (var tool in tools)
                    if (tool is LightmapTransferTool ltt) { ltt.ExportFbxPublic(true); break; }
            }
            GUI.backgroundColor = new Color(.4f, .7f, .95f);
            if (GUILayout.Button("Export New FBX", GUILayout.Height(24)))
            {
                foreach (var tool in tools)
                    if (tool is LightmapTransferTool ltt) { ltt.ExportFbxPublic(false); break; }
            }
            GUI.backgroundColor = bg;
            EditorGUILayout.EndHorizontal();
#else
            EditorGUILayout.HelpBox("Install com.unity.formats.fbx for FBX export.", MessageType.Info);
#endif
            EditorGUILayout.Space(4);
            // ── Sidecar UV2 mode ──
            EditorGUI.BeginChangeCheck();
            sidecarEnabled = EditorGUILayout.ToggleLeft("Sidecar UV2 Mode", sidecarEnabled);
            if (EditorGUI.EndChangeCheck())
                PostprocessorDefineManager.SetEnabled(sidecarEnabled);

            if (!sidecarEnabled)
                EditorGUILayout.LabelField("Persistent replay is OFF; one-shot FBX/apply replay still works.", EditorStyles.miniLabel);

            if (GUILayout.Button("Apply UV2 (sidecar)", EditorStyles.miniButton))
            {
                foreach (var tool in tools)
                    if (tool is LightmapTransferTool ltt) { ltt.ApplyUv2Public(); break; }
            }

            // Backup current FBX from git main branch
            if (!string.IsNullOrEmpty(ctx.SourceFbxPath)
                && GUILayout.Button("Backup from main", EditorStyles.miniButton))
            {
                BackupFbxFromGitMain(ctx.SourceFbxPath);
            }

        }

        static void BackupFbxFromGitMain(string assetPath)
        {
            try
            {
                string repoRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
                string fullPath = System.IO.Path.GetFullPath(assetPath);
                string relativePath = assetPath.Replace('\\', '/');

                string dir = System.IO.Path.GetDirectoryName(fullPath);
                string name = System.IO.Path.GetFileNameWithoutExtension(fullPath);
                string ext = System.IO.Path.GetExtension(fullPath);
                string backupPath = System.IO.Path.Combine(dir, name + "_main" + ext);
                string backupAssetPath = (System.IO.Path.GetDirectoryName(assetPath) + "/" + name + "_main" + ext)
                    .Replace('\\', '/');
                string tempBackupPath = backupPath + ".tmp";

                if (!RunGit(repoRoot, $"cat-file -e \"main:{relativePath}\"", out _, out string existsErr))
                {
                    UvtLog.Error($"[Backup] '{relativePath}' does not exist on branch 'main'. {existsErr.Trim()}");
                    return;
                }

                using (var proc = StartGitBinary(repoRoot, $"cat-file --filters \"main:{relativePath}\""))
                {
                    using (var fs = new System.IO.FileStream(tempBackupPath,
                        System.IO.FileMode.Create, System.IO.FileAccess.Write))
                    {
                        proc.StandardOutput.BaseStream.CopyTo(fs);
                    }
                    string err = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

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

        static System.Diagnostics.Process StartGitBinary(string workingDirectory, string arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            return System.Diagnostics.Process.Start(psi);
        }

        static bool RunGit(string workingDirectory, string arguments, out string stdout, out string stderr)
        {
            using (var proc = StartGitBinary(workingDirectory, arguments))
            {
                stdout = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return proc.ExitCode == 0;
            }
        }

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

        void DrawResizeHandle()
        {
            var r = GUILayoutUtility.GetRect(4, 4, GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(r, new Color(.13f, .13f, .13f));
            EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeHorizontal);
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
            { GUIUtility.hotControl = id; sideDragging = true; Event.current.Use(); }
            if (sideDragging && Event.current.type == EventType.MouseDrag)
            { sideW = Mathf.Clamp(Event.current.mousePosition.x, 200, 520); Event.current.Use(); Repaint(); }
            if (Event.current.rawType == EventType.MouseUp && sideDragging)
            { sideDragging = false; Event.current.Use(); }
        }

        void SetPreviewLod(int lodIndex)
        {
            if (ctx.LodCount <= 0) return;
            int clamped = Mathf.Clamp(lodIndex, 0, ctx.LodCount - 1);
            if (ctx.PreviewLod == clamped) return;
            ctx.PreviewLod = clamped;
            canvas.ClearHoverState();
            if (ctx.LodGroup != null) ctx.LodGroup.ForceLOD(clamped);
            // Reapply active 3D preview to new LOD's renderers
            if (canvas.CurrentPreviewMode != UvCanvasView.PreviewMode.Off)
                ApplyPreviewMode(canvas.CurrentPreviewMode);
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

        // ── Shell color palette ──
        static readonly Color32[] shellPalette = {
            new Color(.20f,.60f,1f),  new Color(1f,.40f,.20f),
            new Color(.30f,.85f,.40f),new Color(.90f,.25f,.60f),
            new Color(.95f,.85f,.20f),new Color(.55f,.30f,.90f),
            new Color(0f,.80f,.80f),  new Color(.85f,.55f,.20f),
            new Color(.60f,.90f,.20f),new Color(.90f,.20f,.20f),
            new Color(.40f,.40f,.90f),new Color(.90f,.70f,.40f),
        };

        void ApplyPreviewMode(UvCanvasView.PreviewMode newMode)
        {
            // Turn off current preview
            if (canvas.CheckerEnabled)
            {
                canvas.CheckerEnabled = false;
                CheckerTexturePreview.Restore();
            }
            if (ShellColorModelPreview.IsActive)
                ShellColorModelPreview.Restore();
            RestoreLightmapPreview();

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
                    var checkerEntries = new List<(Renderer renderer, Mesh meshWithUv2)>();
                    foreach (var e in ctx.MeshEntries)
                    {
                        if (!e.include || e.renderer == null) continue;
                        Mesh uvMesh = e.transferredMesh ?? e.repackedMesh;
                        if (uvMesh == null)
                        {
                            Mesh fallback = e.originalMesh ?? e.fbxMesh;
                            if (fallback != null)
                            {
                                var testUv = new List<Vector2>();
                                fallback.GetUVs(_checkerUvChannel, testUv);
                                if (testUv.Count > 0) uvMesh = fallback;
                            }
                        }
                        if (uvMesh != null) checkerEntries.Add((e.renderer, uvMesh));
                    }
                    if (checkerEntries.Count > 0)
                    {
                        canvas.CheckerEnabled = true;
                        CheckerTexturePreview.Apply(checkerEntries, _checkerUvChannel,
                            _checkerColorMode, _checkerShowR, _checkerShowG);
                    }
                    else
                    {
                        canvas.CurrentPreviewMode = UvCanvasView.PreviewMode.Off;
                        UvtLog.Warn($"[Checker] No meshes with UV data on channel {_checkerUvChannel}.");
                    }
                    break;

                case UvCanvasView.PreviewMode.Shells3D:
                {
                    var shellEntries = new List<(Renderer renderer, Mesh sourceMesh)>();
                    foreach (var e in ctx.ForLod(ctx.PreviewLod))
                    {
                        if (!e.include || e.renderer == null) continue;
                        Mesh mesh = e.transferredMesh ?? e.repackedMesh ?? e.originalMesh ?? e.fbxMesh;
                        if (mesh != null) shellEntries.Add((e.renderer, mesh));
                    }
                    if (shellEntries.Count > 0)
                    {
                        var cache = new ShellColorModelPreview.PreviewShellCache();
                        ShellColorModelPreview.Apply(shellEntries, shellPalette, cache);
                    }
                    else
                    {
                        canvas.CurrentPreviewMode = UvCanvasView.PreviewMode.Off;
                        UvtLog.Warn("[Shells3D] No meshes for current LOD.");
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
                        Mesh srcMesh = mf != null ? mf.sharedMesh : null;
                        Mesh tempMesh = null;
                        if (srcMesh != null)
                        {
                            tempMesh = Instantiate(srcMesh);
                            tempMesh.name = srcMesh.name + "_LmPreview";
                            tempMesh.hideFlags = HideFlags.HideAndDontSave;
                            var uv1 = new List<Vector2>();
                            srcMesh.GetUVs(1, uv1);
                            if (uv1.Count == srcMesh.vertexCount)
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
                        canvas.CurrentPreviewMode = UvCanvasView.PreviewMode.Off;
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
            ctx.PreviewUvChannel = newChannel;
            canvas.ClearHoverState();
            canvas.HoveredShellDebug = null;
            canvas.SelectedShellDebug = null;
            Repaint();
            SceneView.RepaintAll();
        }

        void UpdateSelectedSidecar()
        {
            selectedSidecarPath = null;
            selectedFbxPath = null;
            selectedResetLabel = null;

            var fbxPaths = new HashSet<string>();
            if (ctx?.MeshEntries != null)
            {
                foreach (var e in ctx.MeshEntries)
                {
                    Mesh m = e.fbxMesh ?? e.originalMesh;
                    if (m == null) continue;
                    string path = AssetDatabase.GetAssetPath(m);
                    if (!string.IsNullOrEmpty(path) && path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                        fbxPaths.Add(path);
                }
            }

            if (fbxPaths.Count == 0)
            {
                var go = Selection.activeGameObject;
                if (go == null) return;
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null) continue;
                    string path = AssetDatabase.GetAssetPath(mf.sharedMesh);
                    if (!string.IsNullOrEmpty(path) && path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                        fbxPaths.Add(path);
                }
            }

            foreach (string fbx in fbxPaths)
            {
                string sidecar = Uv2DataAsset.GetSidecarPath(fbx);
                if (AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecar) != null)
                {
                    selectedFbxPath = fbx;
                    selectedSidecarPath = sidecar;
                    selectedResetLabel = System.IO.Path.GetFileNameWithoutExtension(fbx);
                    return;
                }
            }
        }

        void ResetSelectedUv2()
        {
            if (string.IsNullOrEmpty(selectedSidecarPath) || string.IsNullOrEmpty(selectedFbxPath))
                return;

            if (!EditorUtility.DisplayDialog("Reset UV2",
                $"Delete UV2 sidecar for '{selectedResetLabel}'?\nFBX will be reimported without UV2.",
                "Delete", "Cancel"))
                return;

            if (AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(selectedSidecarPath) != null)
                AssetDatabase.DeleteAsset(selectedSidecarPath);

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
                ActiveTool?.OnRefresh();
            }

            ctx.PostResetColoring = true;
            ctx.ShellColorKeyCache.Clear();
            ctx.ShellColorKeyCacheDirty = true;

            UpdateSelectedSidecar();
            Repaint();
        }
    }
}
