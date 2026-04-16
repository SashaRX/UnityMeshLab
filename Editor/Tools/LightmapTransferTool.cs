// LightmapTransferTool.cs — UV2 Lightmap Transfer tool for Mesh Lab.
// Setup → Repack → Transfer → Apply pipeline.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using UnityEditor.Formats.Fbx.Exporter;
#endif

namespace LightmapUvTool
{
    public class LightmapTransferTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        Action requestRepaint;

        public string ToolName  => "UV2 Transfer";
        public string ToolId    => "uv2_transfer";
        public int    ToolOrder => 0;
        public Action RequestRepaint { set => requestRepaint = value; }

        // ── Internal tab ──
        enum Tab { Setup, Repack, Transfer }
        Tab tab = Tab.Setup;

        // ── UV0 analysis ──
        Dictionary<int, Uv0Report> uv0Reports = new Dictionary<int, Uv0Report>();
        bool uv0Analyzed, uv0Welded;

        // ── Foldouts ──
        Dictionary<int, bool> lodFoldouts = new Dictionary<int, bool>();
        Dictionary<int, bool> transferLodFoldouts = new Dictionary<int, bool>();
        Dictionary<int, bool> reportLodFoldouts = new Dictionary<int, bool>();
        bool foldOutput = true;
        bool foldUv0Analysis, foldRepackSettings = true;
        bool splitTargetsInSymmetryStep;
        SymmetrySplitShells.ThresholdMode symSplitThresholdMode = SymmetrySplitShells.ThresholdMode.LegacyFixed;
        HashSet<int> lastSymmetrySplitLods = new HashSet<int>();
        Vector2 reportScroll;

        // ── LOD generation ──
        int generateLodCount = 2;
        float[] generateLodRatios = { 0.5f, 0.25f, 0.125f, 0.0625f };
        float generateTargetError = 0.01f;
        float generateUv2Weight = 100f;
        float generateNormalWeight = 1f;
        bool generateLockBorder = true;
        bool generateAddToLodGroup = true;

        // ── Sidecar ──
        string selectedSidecarPath, selectedFbxPath, selectedResetLabel;
        int setupLodSelectionId = -1;
        int setupRendererSelectionId = -1;
        bool setupSelectionHasRenderers;
        List<(GameObject go, int lodIndex, int rendererCount, int triangleCount)> cachedSetupDetectedLods =
            new List<(GameObject, int, int, int)>();

        // ── Transfer cache ──
        Dictionary<int, GroupedShellTransfer.SourceShellInfo[]> shellTransformCache =
            new Dictionary<int, GroupedShellTransfer.SourceShellInfo[]>();
        List<GroupedShellTransfer.OverlapSourceHint> accumulatedOverlapHints =
            new List<GroupedShellTransfer.OverlapSourceHint>();
        List<GroupedShellTransfer.CrossLodMatchHint> accumulatedMatchHints =
            new List<GroupedShellTransfer.CrossLodMatchHint>();

        // ── Preview ──
        // Three mutually-exclusive preview modes. Only one should be active at a time.
        // lightmapBackups stores original renderer materials for restoration when
        // lightmap preview is active.
        bool checkerEnabled, shellColorPreviewEnabled;
        readonly ShellColorModelPreview.PreviewShellCache shellColorPreviewCache =
            new ShellColorModelPreview.PreviewShellCache();
        string previewConflictNotice;
        Material lightmapPreviewMat;
        bool lightmapPreviewActive;
        readonly Dictionary<Renderer, Material[]> lightmapBackups = new Dictionary<Renderer, Material[]>();

        // ── Scene ──
        double sceneSpotLastRaycastTime;
        const double sceneSpotThrottleSec = 0.033;

        // ════════════════════════════════════════════════════════════
        //  Lifecycle
        // ════════════════════════════════════════════════════════════

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            this.ctx = ctx;
            this.canvas = canvas;
            canvas.OnDoubleClickShell = FocusSceneViewOnSpot;
            UpdateSelectedSidecar();
            TryLoadSettingsFromSidecar();
            TryRestoreShellMatchFromSidecar();
        }

        public void OnDeactivate()
        {
            RestoreAllPreviews();
        }

        public void OnRefresh()
        {
            uv0Reports.Clear();
            uv0Analyzed = uv0Welded = false;
            shellTransformCache.Clear();
            setupLodSelectionId = -1;
            setupRendererSelectionId = -1;
            setupSelectionHasRenderers = false;
            cachedSetupDetectedLods.Clear();
            TryRestoreShellMatchFromSidecar();
            UpdateSelectedSidecar();
            TryLoadSettingsFromSidecar();
        }

        // ════════════════════════════════════════════════════════════
        //  Fill Modes
        // ════════════════════════════════════════════════════════════

        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes()
        {
            yield return new UvCanvasView.FillModeEntry { name = "Shells", drawCallback = DrawFillShells };
            yield return new UvCanvasView.FillModeEntry { name = "Status", drawCallback = DrawFillStatus };
            yield return new UvCanvasView.FillModeEntry { name = "Shell Match", drawCallback = DrawFillShellMatch };
            yield return new UvCanvasView.FillModeEntry { name = "Validation", drawCallback = DrawFillValidation };
            yield return new UvCanvasView.FillModeEntry { name = "None", drawCallback = null };
        }

        void DrawFillShells(UvCanvasView cv, float cx, float cy, float sz, Mesh mesh, MeshEntry entry)
        {
            var uvs = cv.RdUvCached(mesh, ctx.PreviewUvChannel);
            var tri = cv.GetTrianglesCached(mesh);
            if (uvs == null || tri == null) return;
            int uN = uvs.Length, fN = tri.Length / 3;

            // Lightmap UV transform
            Vector2[] displayUvs = uvs;
            if (canvas.CurrentPreviewMode == UvCanvasView.PreviewMode.Lightmap && ctx.PreviewUvChannel == 1 && entry.renderer != null && entry.renderer.lightmapIndex >= 0)
            {
                var so = entry.renderer.lightmapScaleOffset;
                displayUvs = new Vector2[uvs.Length];
                for (int vi = 0; vi < uvs.Length; vi++)
                    displayUvs[vi] = new Vector2(uvs[vi].x * so.x + so.z, uvs[vi].y * so.y + so.w);
            }

            int hoverShellId = canvas.HasHoveredShell && canvas.HoveredShell.meshEntry == entry ? canvas.HoveredShell.shellId : -1;
            int selectedShellId = canvas.HasSelectedShell && canvas.SelectedShell.meshEntry == entry ? canvas.SelectedShell.shellId : -1;
            cv.GlFillSh(ctx, cx, cy, sz, mesh, fN, uN, entry, hoverShellId, selectedShellId,
                canvas.CurrentPreviewMode == UvCanvasView.PreviewMode.Lightmap ? displayUvs : null);

            // Overlay validation problems on shell fill
            if (entry.validationReport?.perTriangle != null && entry.validationReport.perTriangle.Length > 0)
                cv.GlFillValidationOverlay(cx, cy, sz, displayUvs, tri, fN, uN, entry.validationReport.perTriangle);
        }

        void DrawFillStatus(UvCanvasView cv, float cx, float cy, float sz, Mesh mesh, MeshEntry entry)
        {
            var uvs = cv.RdUvCached(mesh, ctx.PreviewUvChannel);
            var tri = cv.GetTrianglesCached(mesh);
            if (uvs == null || tri == null) return;
            TriangleStatus[] stats = entry.transferState?.triangleStatus;
            if (stats == null || stats.Length == 0) return;
            cv.GlFillSt(cx, cy, sz, uvs, tri, tri.Length / 3, uvs.Length, stats);
        }

        void DrawFillShellMatch(UvCanvasView cv, float cx, float cy, float sz, Mesh mesh, MeshEntry entry)
        {
            var uvs = cv.RdUvCached(mesh, ctx.PreviewUvChannel);
            var tri = cv.GetTrianglesCached(mesh);
            if (uvs == null || tri == null) return;
            if (entry.shellTransferResult?.vertexToSourceShell == null) return;
            cv.GlFillShellMatch(cx, cy, sz, uvs, tri, tri.Length / 3, uvs.Length, entry.shellTransferResult.vertexToSourceShell);
        }

        void DrawFillValidation(UvCanvasView cv, float cx, float cy, float sz, Mesh mesh, MeshEntry entry)
        {
            var uvs = cv.RdUvCached(mesh, ctx.PreviewUvChannel);
            var tri = cv.GetTrianglesCached(mesh);
            if (uvs == null || tri == null) return;
            if (entry.validationReport?.perTriangle == null) return;
            cv.GlFillValidation(cx, cy, sz, uvs, tri, tri.Length / 3, uvs.Length, entry.validationReport.perTriangle);
        }

        // ════════════════════════════════════════════════════════════
        //  Sidebar
        // ════════════════════════════════════════════════════════════

        public void OnDrawSidebar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            TBtn("Setup", Tab.Setup);
            TBtn("Repack", Tab.Repack);
            TBtn("Transfer", Tab.Transfer);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
            switch (tab)
            {
                case Tab.Setup:    DrawSetup();    break;
                case Tab.Repack:   DrawRepack();   break;
                case Tab.Transfer: DrawTransfer(); break;
            }
        }

        void TBtn(string l, Tab t)
        {
            var bg = GUI.backgroundColor;
            if (tab == t) GUI.backgroundColor = new Color(.35f,.65f,1f);
            if (GUILayout.Button(l, EditorStyles.toolbarButton)) tab = t;
            GUI.backgroundColor = bg;
        }

        // ──────────────── Setup ────────────────

        void DrawSetup()
        {
            EditorGUI.BeginChangeCheck();
            ctx.LodGroup = (LODGroup)EditorGUILayout.ObjectField("LODGroup", ctx.LodGroup, typeof(LODGroup), true);
            if (EditorGUI.EndChangeCheck()) { ctx.Refresh(ctx.LodGroup); OnRefresh(); }

            if (ctx.LodGroup == null)
            {
                var selected = Selection.activeGameObject;
                var siblings = LodGenerationTool.FindLodSiblings(selected);

                if (siblings != null && siblings.Count > 0)
                {
                    RefreshSetupSelectionCache(selected, siblings);
                    EditorGUILayout.HelpBox(
                        "LOD objects detected but no LODGroup assigned. Create one to continue.",
                        MessageType.Info);
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Detected LODs", EditorStyles.boldLabel);
                    foreach (var (go, lodIndex, rendererCount, triangleCount) in cachedSetupDetectedLods)
                    {
                        EditorGUILayout.LabelField(
                            $"  LOD{lodIndex}: {go.name}  ({rendererCount} renderer{(rendererCount != 1 ? "s" : "")}, {triangleCount:N0} tris)",
                            EditorStyles.miniLabel);
                    }

                    EditorGUILayout.Space(6);
                    var bgc = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(.4f, .8f, .4f);
                    if (GUILayout.Button("Add LOD Group", GUILayout.Height(28)))
                    {
                        var lodGroup = LodGenerationTool.CreateLodGroupStatic(siblings);
                        ctx.Refresh(lodGroup);
                        OnRefresh();
                        requestRepaint?.Invoke();
                    }
                    GUI.backgroundColor = bgc;
                }
                else if (selected != null && SetupSelectionHasRenderers(selected))
                {
                    EditorGUILayout.HelpBox(
                        "No LOD naming detected, but child renderers found.\n" +
                        "Create a LODGroup with all renderers as LOD0.",
                        MessageType.Info);
                    EditorGUILayout.Space(6);
                    var bgc = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(.4f, .8f, .4f);
                    if (GUILayout.Button("Add LOD Group", GUILayout.Height(28)))
                    {
                        var lodGroup = LodGenerationTool.CreateLodGroupFromRenderers(selected);
                        if (lodGroup != null)
                        {
                            ctx.Refresh(lodGroup);
                            OnRefresh();
                            requestRepaint?.Invoke();
                        }
                    }
                    GUI.backgroundColor = bgc;
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Assign LODGroup or select a GameObject.",
                        MessageType.Info);
                }
                return;
            }

            ctx.SourceLodIndex = EditorGUILayout.IntSlider("Source LOD", ctx.SourceLodIndex, 0, ctx.LodCount - 1);

            EditorGUILayout.Space(2);
            for (int li = 0; li < ctx.LodCount; li++)
            {
                var ee = ctx.MeshEntries.Where(e => e.lodIndex == li).ToList();
                if (ee.Count == 0) continue;
                bool src = li == ctx.SourceLodIndex;
                var c = GUI.contentColor;
                if (src) GUI.contentColor = new Color(.4f,.85f,1f);
                string header = (src ? "LOD " + li + " (Source)" : "LOD " + li + " (Target)") + "  [" + ee.Count + "]";
                if (!lodFoldouts.ContainsKey(li)) lodFoldouts[li] = false;
                lodFoldouts[li] = EditorGUILayout.Foldout(lodFoldouts[li], header, true);
                GUI.contentColor = c;
                if (!lodFoldouts[li]) continue;
                foreach (var e in ee)
                {
                    EditorGUILayout.BeginHorizontal();
                    e.include = EditorGUILayout.Toggle(e.include, GUILayout.Width(14));
                    string badge = e.repackedMesh != null ? "[R]" : e.transferredMesh != null ? "[T]" : e.wasWelded ? "[W]" : e.hasExistingUv2 ? "[UV2]" : "";
                    string name = e.renderer.name;
                    if (name.Length > 22) name = name.Substring(0, 20) + "..";
                    EditorGUILayout.LabelField(badge + name, EditorStyles.miniLabel, GUILayout.MinWidth(60));
                    var m = e.originalMesh;
                    EditorGUILayout.LabelField("V:" + m.vertexCount + " T:" + GetTriangleCount(m), EditorStyles.miniLabel, GUILayout.Width(80));
                    EditorGUILayout.EndHorizontal();
                }
            }

            if (selectedSidecarPath != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox("UV2 applied: " + selectedResetLabel + "\n" + selectedSidecarPath, MessageType.Info);
            }

            bool anyModified = ctx.MeshEntries.Any(e => e.wasWelded || e.repackedMesh != null || e.transferredMesh != null);
            if (anyModified)
            {
                EditorGUILayout.Space(2);
                ColorBtn(new Color(.9f,.35f,.35f), "Reset All Working Copies", 20, ResetWorkingCopies);
            }

            EditorGUILayout.Space(4);
            H("Repack");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Resolution", GUILayout.Width(66));
            ctx.AtlasResolution = EditorGUILayout.IntField(ctx.AtlasResolution, GUILayout.Width(60));
            GUILayout.Space(8);
            EditorGUILayout.LabelField("Pad", GUILayout.Width(26));
            ctx.ShellPaddingPx = EditorGUILayout.IntField(ctx.ShellPaddingPx, GUILayout.Width(30));
            GUILayout.Space(8);
            EditorGUILayout.LabelField("Bdr", GUILayout.Width(24));
            ctx.BorderPaddingPx = EditorGUILayout.IntField(ctx.BorderPaddingPx, GUILayout.Width(30));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);
            ctx.RepackPerMesh = EditorGUILayout.ToggleLeft("Per-mesh repack (each group -> [0,1])", ctx.RepackPerMesh);
            symSplitThresholdMode = (SymmetrySplitShells.ThresholdMode)EditorGUILayout.EnumPopup(
                "SymSplit thresholds", symSplitThresholdMode);
            SymmetrySplitShells.CurrentThresholdMode = symSplitThresholdMode;
            ColorBtn(new Color(.2f,.75f,.95f), "Run Full Pipeline", 30, ExecFullPipeline);
            splitTargetsInSymmetryStep = EditorGUILayout.ToggleLeft("SymSplit target LODs (advanced)", splitTargetsInSymmetryStep);

            EditorGUILayout.Space(6);
            H("Pipeline Settings");

            foldOutput = EditorGUILayout.Foldout(foldOutput, "Output", true);
            if (foldOutput)
            {
                EditorGUI.indentLevel++;
                ctx.PipeSettings.saveNewMeshAssets = EditorGUILayout.Toggle("Save Assets", ctx.PipeSettings.saveNewMeshAssets);
                if (ctx.PipeSettings.saveNewMeshAssets)
                    ctx.PipeSettings.savePath = EditorGUILayout.TextField("Path", ctx.PipeSettings.savePath);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4);
            foldUv0Analysis = EditorGUILayout.Foldout(foldUv0Analysis, "UV0 Analysis & Fix", true);
            if (foldUv0Analysis)
            {
                ColorBtn(new Color(.5f,.7f,.9f), "Analyze UV0", 22, ExecAnalyzeUv0);
                if (uv0Analyzed)
                {
                    bool anyIssues = false;
                    foreach (var kv in uv0Reports)
                    {
                        var r = kv.Value;
                        EditorGUILayout.LabelField(r.meshName + ": " + r.totalShells + " shells", EditorStyles.miniLabel);
                        if (r.falseSeamPairs > 0) { anyIssues = true; EditorGUILayout.LabelField($"  {r.falseSeamPairs} false seams", EditorStyles.miniLabel); }
                        if (!r.HasIssues) EditorGUILayout.LabelField("  No issues", EditorStyles.miniLabel);
                    }
                    bool hasTargetLods = ctx.MeshEntries.Any(e => e.include && e.lodIndex != ctx.SourceLodIndex);
                    if ((anyIssues || hasTargetLods) && !uv0Welded)
                        ColorBtn(new Color(.9f,.7f,.2f), "Weld (false seams + source-guided)", 22, ExecWeldUv0);
                    else if (uv0Welded)
                        EditorGUILayout.LabelField("UV0 welded", EditorStyles.miniLabel);
                }

                // Save/Export buttons are at the top of Setup tab
            }
        }

        // ──────────────── Repack ────────────────

        void DrawRepack()
        {
            H("xatlas Repack (LOD0 -> UV2)");
            if (ctx.LodGroup == null) { Warn("Set LODGroup first."); return; }
            foldRepackSettings = EditorGUILayout.Foldout(foldRepackSettings, "Settings", true);
            if (foldRepackSettings)
            {
                EditorGUI.indentLevel++;
                ctx.AtlasResolution = EditorGUILayout.IntField("Resolution", ctx.AtlasResolution);
                ctx.ShellPaddingPx = EditorGUILayout.IntSlider("Shell Padding", ctx.ShellPaddingPx, 0, 16);
                ctx.BorderPaddingPx = EditorGUILayout.IntSlider("Border Padding", ctx.BorderPaddingPx, 0, 16);
                EditorGUI.indentLevel--;
            }
            var src = ctx.ForLod(ctx.SourceLodIndex);
            EditorGUILayout.Space(4);
            ctx.RepackPerMesh = EditorGUILayout.ToggleLeft("Per-mesh repack", ctx.RepackPerMesh);
            symSplitThresholdMode = (SymmetrySplitShells.ThresholdMode)EditorGUILayout.EnumPopup(
                "SymSplit thresholds", symSplitThresholdMode);
            SymmetrySplitShells.CurrentThresholdMode = symSplitThresholdMode;
            ColorBtn(new Color(.3f,.8f,.4f), "Repack All", 26, () => {
                if (ctx.RepackPerMesh) ExecRepackPerMesh(src);
                else ExecRepack(src);
            });
            if (ctx.HasRepack) EditorGUILayout.HelpBox("Repack done. Preview UV1, then Transfer.", MessageType.Info);
        }

        // ──────────────── Transfer ────────────────

        void DrawTransfer()
        {
            H("UV Transfer (Source -> Targets)");
            if (ctx.LodGroup == null) { Warn("Set LODGroup first."); return; }
            if (!ctx.HasRepack) { Warn("Run Repack first."); return; }

            for (int li = 0; li < ctx.LodCount; li++)
            {
                if (li == ctx.SourceLodIndex) continue;
                var ee = ctx.ForLod(li);
                if (ee.Count == 0) continue;
                bool done = ee.All(e => e.transferredMesh != null);
                if (!transferLodFoldouts.ContainsKey(li)) transferLodFoldouts[li] = false;
                transferLodFoldouts[li] = EditorGUILayout.Foldout(transferLodFoldouts[li], (done ? "V" : "O") + " LOD" + li, true);
                if (!transferLodFoldouts[li]) continue;
                foreach (var e in ee)
                {
                    string extra = "";
                    if (e.shellTransferResult != null)
                    {
                        var r = e.shellTransferResult;
                        float p = r.verticesTotal > 0 ? r.verticesTransferred * 100f / r.verticesTotal : 0;
                        extra = $" ({r.shellsMatched}sh, {p:F0}%)";
                    }
                    EditorGUILayout.LabelField("  " + (e.transferredMesh != null ? "V" : "O") + " " + e.renderer.name + extra, EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.Space(6);
            ColorBtn(new Color(.3f,.6f,1f), "Transfer All Targets", 26, ExecTransferAll);

            if (ctx.HasTransfer)
            {
                EditorGUILayout.Space(8);
                H("Quality Report");
                reportScroll = EditorGUILayout.BeginScrollView(reportScroll, GUILayout.MaxHeight(250));
                for (int li = 0; li < ctx.LodCount; li++)
                {
                    if (li == ctx.SourceLodIndex) continue;
                    var ee = ctx.ForLod(li);
                    if (!ee.Any(e => e.shellTransferResult != null)) continue;
                    if (!reportLodFoldouts.ContainsKey(li)) reportLodFoldouts[li] = false;
                    reportLodFoldouts[li] = EditorGUILayout.Foldout(reportLodFoldouts[li], "LOD" + li, true);
                    if (!reportLodFoldouts[li]) continue;
                    foreach (var e in ee)
                    {
                        if (e.shellTransferResult != null)
                        {
                            var r = e.shellTransferResult;
                            EditorGUILayout.LabelField("  " + e.renderer.name, EditorStyles.miniLabel);
                            Bar("OK", r.verticesTransferred, r.verticesTotal, UvCanvasView.cAccept);
                            Bar("Miss", r.verticesTotal - r.verticesTransferred, r.verticesTotal, UvCanvasView.cReject);
                            var vr = e.validationReport;
                            if (vr != null)
                            {
                                Bar("Clean", vr.cleanCount + vr.invertedCount, vr.totalTriangles, UvCanvasView.cValClean);
                                if (vr.stretchedCount > 0) Bar("Str", vr.stretchedCount, vr.totalTriangles, UvCanvasView.cValStretch);
                                if (vr.zeroAreaCount > 0) Bar("0A", vr.zeroAreaCount, vr.totalTriangles, UvCanvasView.cValZero);
                                if (vr.oobCount > 0) Bar("OB", vr.oobCount, vr.totalTriangles, UvCanvasView.cValOOB);
                                if (vr.overlapShellPairs > 0) Bar("Ov", vr.overlapTriangleCount, vr.totalTriangles, UvCanvasView.cValOverlap);
                            }
                        }
                        EditorGUILayout.Space(2);
                    }
                }
                EditorGUILayout.EndScrollView();

                EditorGUILayout.Space(6);
                H("Apply UV2");
                ColorBtn(new Color(.3f,.85f,.4f), "Apply UV2 to FBX", 26, ApplyUv2ToFbx);
                EditorGUILayout.Space(2);
                ColorBtn(new Color(.9f,.3f,.3f), "Reset UV2 (delete sidecar)", 20, ResetUv2FromFbx);
                EditorGUILayout.Space(2);
                ColorBtn(new Color(.5f,.15f,.15f), "Reset Pipeline State", 20, ResetPipelineState);
                EditorGUILayout.Space(2);
                ColorBtn(new Color(.6f,.5f,.8f), "Save FBX from main (_main)", 20, RestoreFbxFromGitMain);

                EditorGUILayout.Space(4);
                H("FBX Export");
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                ColorBtn(new Color(.4f,.7f,.95f), "Export as New FBX", 24, () => ExportFbx(false));
                EditorGUILayout.Space(2);
                ColorBtn(new Color(.95f,.6f,.2f), "Overwrite Source FBX", 24, () => ExportFbx(true));
#else
                EditorGUILayout.HelpBox("Install com.unity.formats.fbx for FBX export.", MessageType.Info);
#endif

                EditorGUILayout.Space(4);
                H("Generate LODs");
                generateLodCount = EditorGUILayout.IntSlider("LOD Count", generateLodCount, 1, 4);
                for (int i = 0; i < generateLodCount && i < generateLodRatios.Length; i++)
                    generateLodRatios[i] = EditorGUILayout.Slider("  LOD" + (i+1) + " ratio", generateLodRatios[i], 0.01f, 0.99f);
                generateTargetError = EditorGUILayout.Slider("Target Error", generateTargetError, 0.001f, 0.5f);
                generateUv2Weight = EditorGUILayout.Slider("UV2 Weight", generateUv2Weight, 0f, 500f);
                generateNormalWeight = EditorGUILayout.Slider("Normal Weight", generateNormalWeight, 0f, 10f);
                generateLockBorder = EditorGUILayout.Toggle("Lock Border", generateLockBorder);
                generateAddToLodGroup = EditorGUILayout.Toggle("Add to LODGroup", generateAddToLodGroup);
                ColorBtn(new Color(.7f,.4f,.95f), "Generate LODs", 26, GenerateLods);
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Execution Methods
        // ════════════════════════════════════════════════════════════

        void ExecAnalyzeUv0()
        {
            if (ctx.LodGroup == null) return;
            uv0Reports.Clear();
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.originalMesh == null) continue;
                var report = Uv0Analyzer.Analyze(e.originalMesh);
                uv0Reports[e.originalMesh.GetInstanceID()] = report;
            }
            uv0Analyzed = true;
            requestRepaint?.Invoke();
        }

        void ExecWeldUv0()
        {
            if (ctx.LodGroup == null) return;
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.originalMesh == null) continue;
                if (e.originalMesh == e.fbxMesh)
                {
                    e.originalMesh = UvCanvasView.MakeReadableCopy(e.fbxMesh);
                    e.originalMesh.name = e.fbxMesh.name + "_wc";
                }
                var optResult = MeshOptimizer.Optimize(e.originalMesh);
                if (optResult.ok) { e.wasWelded = true; UvtLog.Info($"[Weld] '{e.originalMesh.name}' LOD{e.lodIndex}: meshopt optimized"); }
            }

            // UV edge weld for all meshes
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.originalMesh == null) continue;
                var welded = Uv0Analyzer.UvEdgeWeld(e.originalMesh);
                if (welded != null && welded != e.originalMesh)
                {
                    e.originalMesh = welded;
                    e.wasEdgeWelded = true;
                    UvtLog.Info($"[EdgeWeld] '{e.originalMesh.name}' LOD{e.lodIndex}: edge welded");
                }
            }

            uv0Welded = true;
            ctx.ClearAllCaches();
            requestRepaint?.Invoke();
        }

        void ExecSymmetrySplit(bool includeTargets, float separationThreshold = 0.10f)
        {
            if (ctx.LodGroup == null) return;
            SymmetrySplitShells.CurrentThresholdMode = symSplitThresholdMode;
            lastSymmetrySplitLods.Clear();

            // Phase 1: Split source LOD and capture parameters for coordinated LOD splitting
            var splitParamsByGroup = new Dictionary<string, List<SymmetrySplitShells.SplitParams>>();

            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.lodIndex != ctx.SourceLodIndex) continue;
                if (e.originalMesh == e.fbxMesh)
                {
                    e.originalMesh = UvCanvasView.MakeReadableCopy(e.fbxMesh);
                    e.originalMesh.name = e.fbxMesh.name + "_wc";
                }
                var uv0 = e.originalMesh.uv;
                if (uv0 == null || uv0.Length == 0) continue;
                var shells = UvShellExtractor.Extract(uv0, e.originalMesh.triangles);
                int split = SymmetrySplitShells.Split(e.originalMesh, shells, out var splitParams, separationThreshold);
                if (split > 0)
                {
                    e.wasSymmetrySplit = true;
                    lastSymmetrySplitLods.Add(e.lodIndex);
                    UvtLog.Info($"[SymSplit] '{e.originalMesh.name}' LOD{e.lodIndex}: {split} shells split");
                    // Store params keyed by mesh group for target LOD propagation
                    string key = e.meshGroupKey ?? e.renderer.name;
                    splitParamsByGroup[key] = splitParams;
                }
            }

            // Phase 2: Apply same split parameters to target LODs (coordinated)
            if (includeTargets)
            {
                foreach (var e in ctx.MeshEntries)
                {
                    if (!e.include || e.lodIndex == ctx.SourceLodIndex) continue;
                    if (e.originalMesh == e.fbxMesh)
                    {
                        e.originalMesh = UvCanvasView.MakeReadableCopy(e.fbxMesh);
                        e.originalMesh.name = e.fbxMesh.name + "_wc";
                    }
                    var uv0 = e.originalMesh.uv;
                    if (uv0 == null || uv0.Length == 0) continue;
                    var shells = UvShellExtractor.Extract(uv0, e.originalMesh.triangles);

                    // Try coordinated split with source LOD parameters
                    string key = e.meshGroupKey ?? e.renderer.name;
                    int split = 0;
                    if (splitParamsByGroup.TryGetValue(key, out var prescribed) && prescribed.Count > 0)
                    {
                        split = SymmetrySplitShells.SplitWithParams(e.originalMesh, shells, prescribed);
                        if (split > 0)
                            UvtLog.Info($"[SymSplit] '{e.originalMesh.name}' LOD{e.lodIndex}: {split} shells split (coordinated)");
                    }
                    // Fallback to independent detection if no prescribed params
                    if (split == 0)
                    {
                        split = SymmetrySplitShells.Split(e.originalMesh, shells, separationThreshold);
                        if (split > 0)
                            UvtLog.Info($"[SymSplit] '{e.originalMesh.name}' LOD{e.lodIndex}: {split} shells split (independent)");
                    }
                    if (split > 0) { e.wasSymmetrySplit = true; lastSymmetrySplitLods.Add(e.lodIndex); }
                }
            }

            ctx.ClearAllCaches();
            requestRepaint?.Invoke();
        }

        void ExecFullPipeline()
        {
            if (ctx.LodGroup == null) return;
            string version = UnityEditor.PackageManager.PackageInfo
                .FindForAssembly(typeof(LightmapTransferTool).Assembly)?.version ?? "0.0.0";
            UvtLog.Info($"[Pipeline] Starting full pipeline... (v{version})");

            // 1. Analyze
            ExecAnalyzeUv0();

            // 2. Weld
            ExecWeldUv0();

            // ── Auto-tune: try multiple SymSplit configs, pick best ──
            // Save working copies so we can restore between attempts.
            var savedMeshes = new Dictionary<MeshEntry, Mesh>();
            foreach (var e in ctx.MeshEntries)
                if (e.originalMesh != null)
                    savedMeshes[e] = UnityEngine.Object.Instantiate(e.originalMesh);

            float[] separationConfigs = { 0.10f, 0.05f, 0.20f };
            int bestRejected = int.MaxValue;
            float bestCoverage = 0f;
            int bestConfigIdx = 0;
            var bestMeshes = new Dictionary<MeshEntry, Mesh>();
            var bestTransfers = new Dictionary<MeshEntry, (Mesh transferred, GroupedShellTransfer.TransferResult tr)>();

            bool cancelled = false;
            try
            {
            for (int ci = 0; ci < separationConfigs.Length; ci++)
            {
                float sepThresh = separationConfigs[ci];

                if (EditorUtility.DisplayCancelableProgressBar(
                    "Auto-tune Pipeline",
                    $"Config {ci + 1}/{separationConfigs.Length} (separation={sepThresh:P0})",
                    (float)ci / separationConfigs.Length))
                {
                    UvtLog.Warn("[Pipeline] Auto-tune cancelled by user.");
                    cancelled = true;
                    break;
                }

                if (ci > 0)
                {
                    UvtLog.Info($"[Pipeline] Auto-tune retry #{ci} (separation={sepThresh:P0})...");
                    // Restore saved meshes
                    foreach (var kv in savedMeshes)
                    {
                        kv.Key.originalMesh = UnityEngine.Object.Instantiate(kv.Value);
                        kv.Key.originalMesh.name = kv.Value.name;
                        kv.Key.wasSymmetrySplit = false;
                        kv.Key.repackedMesh = null;
                        kv.Key.transferredMesh = null;
                        kv.Key.shellTransferResult = null;
                    }
                    ctx.ClearAllCaches();
                    accumulatedOverlapHints.Clear();
                    shellTransformCache.Clear();
                    ctx.HasRepack = false;
                    ctx.HasTransfer = false;
                }

                // 3. SymSplit
                ExecSymmetrySplit(splitTargetsInSymmetryStep, sepThresh);

                // 4. Repack
                var src = ctx.ForLod(ctx.SourceLodIndex);
                if (ctx.RepackPerMesh) ExecRepackPerMesh(src);
                else ExecRepack(src);

                // 5. Transfer
                if (ctx.HasRepack) ExecTransferAll();

                // Evaluate quality
                int totalRejected = 0;
                int totalOverlaps = 0;
                int totalVerts = 0;
                int totalTransferred = 0;
                foreach (var e in ctx.MeshEntries)
                {
                    if (e.shellTransferResult == null) continue;
                    totalRejected += e.shellTransferResult.shellsRejected;
                    totalOverlaps += e.shellTransferResult.shellsOverlapFixed;
                    totalVerts += e.shellTransferResult.verticesTotal;
                    totalTransferred += e.shellTransferResult.verticesTransferred;
                }
                float coverage = totalVerts > 0 ? (float)totalTransferred / totalVerts : 0f;
                int totalIssues = totalRejected + totalOverlaps;

                UvtLog.Info($"[Pipeline] Config #{ci} (sep={sepThresh:P0}): " +
                    $"rejected={totalRejected}, overlaps={totalOverlaps}, coverage={coverage:P0}");

                bool better = false;
                if (totalIssues < bestRejected)
                    better = true;
                else if (totalIssues == bestRejected && coverage > bestCoverage)
                    better = true;

                if (better)
                {
                    bestRejected = totalIssues;
                    bestCoverage = coverage;
                    bestConfigIdx = ci;
                    // Save best meshes
                    foreach (var m in bestMeshes.Values) UnityEngine.Object.DestroyImmediate(m);
                    bestMeshes.Clear();
                    bestTransfers.Clear();
                    foreach (var e in ctx.MeshEntries)
                    {
                        if (e.originalMesh != null)
                            bestMeshes[e] = UnityEngine.Object.Instantiate(e.originalMesh);
                        if (e.transferredMesh != null)
                            bestTransfers[e] = (UnityEngine.Object.Instantiate(e.transferredMesh),
                                e.shellTransferResult);
                    }
                }

                // Early exit if perfect
                if (totalIssues == 0 && coverage >= 0.99f)
                {
                    if (ci > 0) UvtLog.Info($"[Pipeline] Perfect result on config #{ci}, stopping.");
                    break;
                }
            }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            // Restore best config if not the last one tested
            if (bestMeshes.Count > 0 && !cancelled)
            {
                foreach (var kv in bestMeshes)
                {
                    kv.Key.originalMesh = kv.Value;
                    kv.Key.originalMesh.name = kv.Value.name;
                }
                foreach (var kv in bestTransfers)
                {
                    kv.Key.transferredMesh = kv.Value.transferred;
                    kv.Key.shellTransferResult = kv.Value.tr;
                }
            }

            // Cleanup saved copies
            foreach (var m in savedMeshes.Values)
                UnityEngine.Object.DestroyImmediate(m);

            if (cancelled)
            {
                requestRepaint?.Invoke();
                return;
            }

            if (separationConfigs.Length > 1 && bestConfigIdx > 0)
                UvtLog.Info($"[Pipeline] Auto-tune: selected config #{bestConfigIdx} " +
                    $"(sep={separationConfigs[bestConfigIdx]:P0})");

            UvtLog.Info("[Pipeline] Complete.");
            requestRepaint?.Invoke();
        }

        void ExecRepack(List<MeshEntry> entries)
        {
            if (entries.Count == 0) return;
            UvtLog.Info($"[Repack] {entries.Count} meshes, res={ctx.AtlasResolution}, pad={ctx.ShellPaddingPx}, bdr={ctx.BorderPaddingPx}");
            var validEntries = new List<MeshEntry>();
            var meshCopies = new List<Mesh>();
            foreach (var e in entries)
            {
                if (e.originalMesh == null) continue;
                var uv0 = e.originalMesh.uv;
                if (uv0 == null || uv0.Length == 0) { UvtLog.Warn("[Repack] " + e.renderer.name + ": no UV0"); continue; }
                var cp = UnityEngine.Object.Instantiate(e.originalMesh);
                cp.name = e.originalMesh.name + "_repack";
                validEntries.Add(e);
                meshCopies.Add(cp);
            }
            if (meshCopies.Count == 0) return;

            var opts = RepackOptions.Default;
            opts.resolution = (uint)ctx.AtlasResolution;
            opts.padding = (uint)ctx.ShellPaddingPx;
            opts.borderPadding = (uint)ctx.BorderPaddingPx;

            var results = XatlasRepack.RepackMulti(meshCopies.ToArray(), opts);
            for (int i = 0; i < validEntries.Count; i++)
            {
                if (!results[i].ok)
                {
                    UvtLog.Error("[Repack] " + validEntries[i].renderer.name + ": " + results[i].error);
                    UnityEngine.Object.DestroyImmediate(meshCopies[i]);
                    continue;
                }
                validEntries[i].repackedMesh = meshCopies[i];
            }

            ctx.HasRepack = true;
            ctx.ClearAllCaches();
            requestRepaint?.Invoke();
        }

        void ExecRepackPerMesh(List<MeshEntry> entries)
        {
            var groups = new Dictionary<string, List<MeshEntry>>();
            foreach (var e in entries)
            {
                string key = e.meshGroupKey ?? e.renderer.name;
                if (!groups.ContainsKey(key)) groups[key] = new List<MeshEntry>();
                groups[key].Add(e);
            }
            foreach (var kv in groups)
                ExecRepack(kv.Value);
        }

        void ExecTransferAll()
        {
            accumulatedOverlapHints.Clear();
            accumulatedMatchHints.Clear();
            for (int li = 0; li < ctx.LodCount; li++)
            {
                if (li == ctx.SourceLodIndex) continue;
                ExecTransferLod(li);
            }
            ctx.HasTransfer = true;
            requestRepaint?.Invoke();
        }

        void ExecTransferLod(int tLod)
        {
            var targets = ctx.ForLod(tLod);
            if (targets.Count == 0) return;
            var sources = ctx.ForLod(ctx.SourceLodIndex);
            if (sources.Count == 0) return;

            foreach (var tgt in targets)
            {
                if (tgt.originalMesh == tgt.fbxMesh)
                {
                    tgt.originalMesh = UvCanvasView.MakeReadableCopy(tgt.fbxMesh);
                    tgt.originalMesh.name = tgt.fbxMesh.name + "_wc";
                }

                // Find matching source by mesh group key
                MeshEntry srcEntry = null;
                if (!string.IsNullOrEmpty(tgt.meshGroupKey))
                    srcEntry = sources.FirstOrDefault(s => s.meshGroupKey == tgt.meshGroupKey);
                if (srcEntry == null)
                    srcEntry = sources[0];

                Mesh srcMesh = srcEntry.repackedMesh ?? srcEntry.originalMesh;
                Mesh tgtMesh = tgt.originalMesh;
                if (srcMesh == null || tgtMesh == null) continue;

                int srcId = srcMesh.GetInstanceID();
                if (!shellTransformCache.TryGetValue(srcId, out var srcInfos))
                {
                    srcInfos = GroupedShellTransfer.AnalyzeSource(srcMesh);
                    if (srcInfos != null) shellTransformCache[srcId] = srcInfos;
                }
                if (srcInfos == null) continue;

                var tr = GroupedShellTransfer.Transfer(tgtMesh, srcMesh,
                    accumulatedOverlapHints.Count > 0 ? accumulatedOverlapHints : null,
                    accumulatedMatchHints.Count > 0 ? accumulatedMatchHints : null);
                if (tr.uv2 == null) { UvtLog.Warn($"[Transfer] Failed for '{tgt.renderer.name}'"); continue; }

                // Accumulate overlap hints for subsequent LODs
                if (tr.overlapHints != null && tr.overlapHints.Count > 0)
                    accumulatedOverlapHints.AddRange(tr.overlapHints);
                // Replace match hints with this LOD's matches (latest LOD drives
                // next LOD's hint-guided matching; stale hints from older LODs
                // could conflict with changing geometry)
                accumulatedMatchHints.Clear();
                if (tr.matchHints != null && tr.matchHints.Count > 0)
                    accumulatedMatchHints.AddRange(tr.matchHints);

                // Build output mesh with UV2 applied
                var om = UnityEngine.Object.Instantiate(tgtMesh);
                om.name = tgtMesh.name + "_uvTransfer";
                om.SetUVs(1, new List<Vector2>(tr.uv2));
                tgt.transferredMesh = om;
                tgt.shellTransferResult = tr;

                // Validation
                tgt.validationReport = TransferValidator.Validate(tgtMesh, tr.uv2, tr);

                float pct = tr.verticesTotal > 0 ? tr.verticesTransferred * 100f / tr.verticesTotal : 0;
                UvtLog.Info($"[Transfer] '{tgt.renderer.name}' LOD{tLod}: {tr.shellsMatched} shells, {pct:F0}% coverage");
            }
        }

        void ApplyUv2ToFbx()
        {
            if (ctx?.MeshEntries == null || ctx.MeshEntries.Count == 0)
            {
                UvtLog.Warn("[Apply] No meshes loaded.");
                return;
            }
            UvtLog.Info("[Apply] Applying UV2 to FBX...");

            // Pre-import pass: reimport FBXs with postprocessor bypassed to get raw vertex order
            var fbxPathSet = new HashSet<string>();
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include) continue;
                Mesh m = e.fbxMesh ?? e.originalMesh;
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(p)) fbxPathSet.Add(p);
            }
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

            if (fbxGroups.Count == 0) { UvtLog.Warn("[Apply] No meshes with UV2 data."); return; }

            // Save sidecar assets
            bool persistentSidecarMode = PostprocessorDefineManager.IsEnabled();
            foreach (var kv in fbxGroups)
            {
                if (persistentSidecarMode)
                {
                    string sidecarPath = Uv2DataAsset.GetSidecarPath(kv.Key);
                    var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
                    if (data == null)
                    {
                        data = ScriptableObject.CreateInstance<Uv2DataAsset>();
                        AssetDatabase.CreateAsset(data, sidecarPath);
                    }
                    foreach (var entry in kv.Value)
                        data.Set(entry);
                    EditorUtility.SetDirty(data);
                    AssetDatabase.SaveAssets();
                }
                else
                {
                    Uv2AssetPostprocessor.SetTransientReplayEntries(kv.Key, kv.Value);
                }

                // Prepare import settings and reimport FBX so the postprocessor replays UV2
                Uv2AssetPostprocessor.managedImportPaths.Add(kv.Key);
                if (!persistentSidecarMode)
                    Uv2AssetPostprocessor.transientReplayPaths.Add(kv.Key);

                bool reimported = Uv2AssetPostprocessor.PrepareImportSettings(kv.Key);
                if (!reimported)
                    AssetDatabase.ImportAsset(kv.Key, ImportAssetOptions.ForceUpdate);
            }

            UvtLog.Info($"[Apply] Done — {fbxGroups.Count} FBX(es) updated.");
            SwitchToPostApplyView();
            SaveSettingsToSidecar();
        }

        Mesh GetResultMesh(MeshEntry e)
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

        static string ResolveExportMeshName(MeshEntry entry, Mesh resultMesh)
        {
            if (entry?.fbxMesh != null && !string.IsNullOrEmpty(entry.fbxMesh.name))
                return entry.fbxMesh.name;

            string fallback = entry?.originalMesh != null ? entry.originalMesh.name : null;
            if (string.IsNullOrEmpty(fallback) && resultMesh != null)
                fallback = resultMesh.name;

            // Guard against transient preview/internal names leaking into exported FBX nodes.
            if (!string.IsNullOrEmpty(fallback) &&
                (fallback.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase) ||
                 fallback.StartsWith("Hidden_", StringComparison.OrdinalIgnoreCase)))
            {
                if (entry?.renderer != null && !string.IsNullOrEmpty(entry.renderer.name))
                    return entry.renderer.name;
            }

            if (!string.IsNullOrEmpty(fallback))
                return fallback;

            if (entry?.renderer != null && !string.IsNullOrEmpty(entry.renderer.name))
                return entry.renderer.name;

            return "Mesh";
        }

        /// <summary>
        /// Copy non-trivial UV channels from source mesh to export mesh.
        /// Preserves channels that have meaningful data (not empty, not all 0, not all 1).
        /// Only copies channels missing from exportMesh; does not overwrite existing data.
        /// </summary>
        static void PreserveUvChannels(Mesh exportMesh, Mesh sourceMesh)
        {
            if (sourceMesh.vertexCount != exportMesh.vertexCount) return;
            for (int ch = 0; ch < 8; ch++)
            {
                var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
                if (exportMesh.HasVertexAttribute(attr)) continue;
                if (!sourceMesh.HasVertexAttribute(attr)) continue;

                int dim = sourceMesh.GetVertexAttributeDimension(attr);
                if (dim <= 2)
                {
                    var uv = new List<Vector2>();
                    sourceMesh.GetUVs(ch, uv);
                    if (uv.Count == 0) continue;
                    bool allZero = true;
                    for (int i = 0; i < uv.Count; i++)
                        if (uv[i].x != 0f || uv[i].y != 0f) { allZero = false; break; }
                    if (allZero) continue;
                    exportMesh.SetUVs(ch, uv);
                }
                else if (dim == 3)
                {
                    var uv = new List<Vector3>();
                    sourceMesh.GetUVs(ch, uv);
                    if (uv.Count > 0) exportMesh.SetUVs(ch, uv);
                }
                else
                {
                    var uv = new List<Vector4>();
                    sourceMesh.GetUVs(ch, uv);
                    if (uv.Count > 0) exportMesh.SetUVs(ch, uv);
                }
            }
        }

        static void OverwriteUvChannel(Mesh exportMesh, Mesh sourceMesh, int channel)
        {
            if (exportMesh == null || sourceMesh == null) return;
            if (channel < 0 || channel > 7) return;
            if (sourceMesh.vertexCount != exportMesh.vertexCount) return;
            var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
            if (!sourceMesh.HasVertexAttribute(attr)) return;

            int dim = sourceMesh.GetVertexAttributeDimension(attr);
            if (dim <= 2)
            {
                var uv = new List<Vector2>();
                sourceMesh.GetUVs(channel, uv);
                if (uv.Count == exportMesh.vertexCount)
                    exportMesh.SetUVs(channel, uv);
            }
            else if (dim == 3)
            {
                var uv = new List<Vector3>();
                sourceMesh.GetUVs(channel, uv);
                if (uv.Count == exportMesh.vertexCount)
                    exportMesh.SetUVs(channel, uv);
            }
            else
            {
                var uv = new List<Vector4>();
                sourceMesh.GetUVs(channel, uv);
                if (uv.Count == exportMesh.vertexCount)
                    exportMesh.SetUVs(channel, uv);
            }
        }

        static bool TryGetAppliedAoUvTarget(out int uvChannel, out int uvComponent)
        {
            uvChannel = -1;
            uvComponent = 0;

            var ch = VertexAOTool.LastAppliedTargetChannel;
            if (!ch.HasValue) return false;

            int v = (int)ch.Value;
            if (v < (int)AOTargetChannel.UV0_X) return false; // AO was stored in vertex color

            uvChannel = (v - (int)AOTargetChannel.UV0_X) / 2;
            uvComponent = (v - (int)AOTargetChannel.UV0_X) % 2; // 0=X, 1=Y
            // UV1 (Unity UV set index 1) is reserved for lightmap transfer data.
            // Never merge AO into this channel during FBX export.
            if (uvChannel == 1) return false;
            return true;
        }

        static void MergeUvComponentFromDonor(Mesh exportMesh, Mesh donorMesh, int uvChannel, int uvComponent)
        {
            if (exportMesh == null || donorMesh == null) return;
            if (exportMesh.vertexCount != donorMesh.vertexCount) return;
            if (uvChannel < 0 || uvChannel > 7) return;
            if (uvComponent < 0 || uvComponent > 1) return;

            var donorUv = new List<Vector2>();
            donorMesh.GetUVs(uvChannel, donorUv);
            if (donorUv.Count != exportMesh.vertexCount) return;

            var exportUv = new List<Vector2>();
            exportMesh.GetUVs(uvChannel, exportUv);
            if (exportUv.Count != exportMesh.vertexCount)
                exportUv = new List<Vector2>(donorUv);

            for (int i = 0; i < exportUv.Count; i++)
            {
                var src = donorUv[i];
                var dst = exportUv[i];
                exportUv[i] = uvComponent == 0
                    ? new Vector2(src.x, dst.y)
                    : new Vector2(dst.x, src.y);
            }

            exportMesh.SetUVs(uvChannel, exportUv);
        }

        static bool HasUvChannelData(Mesh mesh, int channel)
        {
            if (mesh == null || channel < 0 || channel > 7) return false;
            var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
            if (!mesh.HasVertexAttribute(attr)) return false;

            int dim = mesh.GetVertexAttributeDimension(attr);
            int vCount = mesh.vertexCount;
            if (dim <= 2)
            {
                var uv = new List<Vector2>();
                mesh.GetUVs(channel, uv);
                return uv.Count == vCount;
            }
            if (dim == 3)
            {
                var uv = new List<Vector3>();
                mesh.GetUVs(channel, uv);
                return uv.Count == vCount;
            }

            var uv4 = new List<Vector4>();
            mesh.GetUVs(channel, uv4);
            return uv4.Count == vCount;
        }

        static Mesh SelectUv2Donor(MeshEntry entry, Mesh resultMesh, int uvChannel)
        {
            // AO is written into selected UV component by VertexAOTool.ApplyToMesh,
            // usually on original/fbx-backed working meshes.
            // Keep transferred mesh last
            // so UV1 transfer result stays authoritative while AO comes from AO donor.
            var candidates = new[] { entry?.originalMesh, entry?.fbxMesh, entry?.repackedMesh, entry?.transferredMesh, resultMesh };
            for (int i = 0; i < candidates.Length; i++)
            {
                var m = candidates[i];
                if (HasUvChannelData(m, uvChannel)) return m;
            }
            return null;
        }

        bool TryBuildSidecarEntry(MeshEntry entry, Mesh resultMesh, out MeshUv2Entry sidecarEntry)
        {
            sidecarEntry = null;
            if (entry == null || resultMesh == null)
                return false;

            bool hasAppliedAoTarget = TryGetAppliedAoUvTarget(out int aoUvChannel, out int aoUvComponent);
            var sidecarMesh = UnityEngine.Object.Instantiate(resultMesh);
            sidecarMesh.name = resultMesh.name;
            try
            {
                if (entry.fbxMesh != null)
                    PreserveUvChannels(sidecarMesh, entry.fbxMesh);
                if (entry.originalMesh != null && entry.originalMesh != entry.fbxMesh)
                {
                    PreserveUvChannels(sidecarMesh, entry.originalMesh);
                    OverwriteUvChannel(sidecarMesh, entry.originalMesh, 1);
                }

                Vector2[] auxiliaryUv = null;
                int auxiliaryTargetUvChannel = -1;
                if (hasAppliedAoTarget && aoUvChannel != 1)
                {
                    var uvDonor = SelectUv2Donor(entry, resultMesh, aoUvChannel);
                    if (uvDonor != null)
                    {
                        MergeUvComponentFromDonor(sidecarMesh, uvDonor, aoUvChannel, aoUvComponent);
                        var auxiliaryUvList = new List<Vector2>();
                        sidecarMesh.GetUVs(aoUvChannel, auxiliaryUvList);
                        if (auxiliaryUvList.Count == sidecarMesh.vertexCount)
                        {
                            auxiliaryUv = auxiliaryUvList.ToArray();
                            auxiliaryTargetUvChannel = aoUvChannel;
                        }
                    }
                }

                var primaryUvList = new List<Vector2>();
                sidecarMesh.GetUVs(1, primaryUvList);
                Vector2[] primaryUv = primaryUvList.Count == sidecarMesh.vertexCount
                    ? primaryUvList.ToArray()
                    : null;
                int primaryTargetUvChannel = 1;
                if (primaryUv == null && auxiliaryUv != null)
                {
                    primaryUv = auxiliaryUv;
                    primaryTargetUvChannel = auxiliaryTargetUvChannel;
                    auxiliaryUv = null;
                    auxiliaryTargetUvChannel = -1;
                }

                if (primaryUv == null)
                    return false;

                var positions = sidecarMesh.vertices;
                var uv0List = new List<Vector2>();
                (entry.originalMesh ?? resultMesh).GetUVs(0, uv0List);

                string meshName = entry.fbxMesh != null
                    ? entry.fbxMesh.name
                    : (entry.originalMesh != null ? entry.originalMesh.name : resultMesh.name);
                MeshFingerprint fp = entry.fbxMesh != null ? MeshFingerprint.Compute(entry.fbxMesh) : null;

                sidecarEntry = new MeshUv2Entry
                {
                    meshName = meshName,
                    uv2 = primaryUv,
                    welded = entry.wasWelded,
                    edgeWelded = entry.wasEdgeWelded,
                    vertPositions = positions,
                    vertUv0 = uv0List.ToArray(),
                    schemaVersion = Uv2DataAsset.CurrentSchemaVersion,
                    toolVersion = Uv2DataAsset.ToolVersionStr,
                    sourceFingerprint = fp,
                    targetUvChannel = primaryTargetUvChannel,
                    auxiliaryUv = auxiliaryUv,
                    auxiliaryTargetUvChannel = auxiliaryTargetUvChannel,
                    stepMeshopt = entry.wasWelded,
                    stepEdgeWeld = entry.wasEdgeWelded,
                    stepSymmetrySplit = entry.wasSymmetrySplit,
                    stepRepack = (entry.lodIndex == ctx.SourceLodIndex),
                    stepTransfer = (entry.lodIndex != ctx.SourceLodIndex),
                };
                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sidecarMesh);
            }
        }

        public void ExportFbxPublic(bool overwriteSource) => ExportFbx(overwriteSource);
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

            // Find source FBX path
            string sourceFbxPath = ctx.SourceFbxPath;
            if (string.IsNullOrEmpty(sourceFbxPath))
            {
                foreach (var e in ctx.MeshEntries)
                {
                    if (e.fbxMesh == null) continue;
                    string p = AssetDatabase.GetAssetPath(e.fbxMesh);
                    if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    { sourceFbxPath = p; break; }
                }
            }
            if (string.IsNullOrEmpty(sourceFbxPath))
            {
                UvtLog.Error("[FBX Export] Cannot find source FBX path.");
                return;
            }

            if (!EditorUtility.DisplayDialog("Overwrite FBX (Vertex Colors)",
                $"Overwrite '{System.IO.Path.GetFileName(sourceFbxPath)}' with current vertex colors?\n\n" +
                "Only vertex colors will be updated. UV2 and mesh topology stay unchanged.",
                "Overwrite", "Cancel"))
                return;

            // Make FBX readable if needed
            var srcImporter = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
            bool madeReadable = false;
            if (srcImporter != null && !srcImporter.isReadable)
            {
                srcImporter.isReadable = true;
                Uv2AssetPostprocessor.bypassPaths.Add(sourceFbxPath);
                srcImporter.SaveAndReimport();
                madeReadable = true;
            }

            // Clone the FBX prefab
            var fbxAsset = AssetDatabase.LoadMainAssetAtPath(sourceFbxPath) as GameObject;
            if (fbxAsset == null)
            {
                UvtLog.Error($"[FBX Export] Cannot load FBX asset at '{sourceFbxPath}'.");
                return;
            }

            var tempRoot = UnityEngine.Object.Instantiate(fbxAsset);
            tempRoot.name = fbxAsset.name;

            try
            {
                // Determine which channel AO targets (vertex color or UV)
                int aoUvIdx = -1; // -1 = vertex colors, 0-7 = UV channel index
                var aoChannel = VertexAOTool.LastAppliedTargetChannel;
                if (aoChannel.HasValue)
                {
                    int ch = (int)aoChannel.Value;
                    if (ch > (int)AOTargetChannel.VertexColorA)
                        aoUvIdx = (ch - (int)AOTargetChannel.UV0_X) / 2;
                }

                // Copy vertex data from scene meshes onto FBX clone meshes by name
                int updated = 0;
                foreach (var e in ctx.MeshEntries)
                {
                    if (!e.include) continue;
                    Mesh sceneMesh = e.originalMesh ?? e.fbxMesh;
                    if (sceneMesh == null) continue;

                    // Find matching mesh in clone by name
                    foreach (var cloneMf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (cloneMf == null || cloneMf.sharedMesh == null) continue;
                        if (cloneMf.sharedMesh.name != sceneMesh.name) continue;

                        // Clone the mesh to avoid modifying the FBX sub-asset
                        var cloneMesh = UnityEngine.Object.Instantiate(cloneMf.sharedMesh);
                        cloneMesh.name = cloneMf.sharedMesh.name;

                        // Copy vertex colors (when AO targets vertex color channel)
                        if (sceneMesh.colors32 != null && sceneMesh.colors32.Length == cloneMesh.vertexCount)
                        {
                            cloneMesh.colors32 = sceneMesh.colors32;
                            updated++;
                        }
                        else if (sceneMesh.colors != null && sceneMesh.colors.Length == cloneMesh.vertexCount)
                        {
                            cloneMesh.colors = sceneMesh.colors;
                            updated++;
                        }

                        // Copy the UV channel where AO was applied
                        if (aoUvIdx >= 0 && sceneMesh.vertexCount == cloneMesh.vertexCount)
                        {
                            var uvs = new List<Vector2>();
                            sceneMesh.GetUVs(aoUvIdx, uvs);
                            if (uvs.Count == cloneMesh.vertexCount)
                            {
                                cloneMesh.SetUVs(aoUvIdx, uvs);
                                updated++;
                            }
                        }

                        cloneMf.sharedMesh = cloneMesh;
                        break;
                    }
                }

                if (updated == 0)
                {
                    UvtLog.Warn("[FBX Export] No vertex data found to export.");
                    return;
                }

                // Normalize hierarchy (bake transforms, rename LODs)
                var renameMap = NormalizeExportHierarchy(tempRoot);

                // Assign LOD0 material to collision nodes
                Material colFallbackMat = null;
                foreach (var e in ctx.MeshEntries)
                {
                    if (e.renderer != null && e.renderer.sharedMaterial != null &&
                        !CheckerTexturePreview.IsPreviewShader(e.renderer.sharedMaterial.shader.name))
                    {
                        colFallbackMat = e.renderer.sharedMaterial;
                        break;
                    }
                }
                foreach (var colMf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (colMf == null || colMf.sharedMesh == null) continue;
                    if (!MeshHygieneUtility.IsCollisionNodeName(colMf.gameObject.name)) continue;
                    var colMr = colMf.GetComponent<MeshRenderer>();
                    if (colFallbackMat != null)
                    {
                        if (colMr == null)
                            colMr = colMf.gameObject.AddComponent<MeshRenderer>();
                        colMr.sharedMaterials = new[] { colFallbackMat };
                    }
                    else if (colMr != null)
                        UnityEngine.Object.DestroyImmediate(colMr);
                }

                // Trim material arrays
                foreach (var mr in tempRoot.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (mr == null) continue;
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mats = mr.sharedMaterials;
                    if (mats.Length > mf.sharedMesh.subMeshCount)
                    {
                        var trimmed = new Material[mf.sharedMesh.subMeshCount];
                        System.Array.Copy(mats, trimmed, trimmed.Length);
                        mr.sharedMaterials = trimmed;
                    }
                }

                // Backup .meta to temp dir (avoid Unity scanning .bak in Assets)
                string fullPath = System.IO.Path.GetFullPath(sourceFbxPath);
                string metaBak = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    System.IO.Path.GetFileName(fullPath) + ".meta.bak");
                if (System.IO.File.Exists(fullPath + ".meta"))
                    System.IO.File.Copy(fullPath + ".meta", metaBak, true);

                // Export
                var exportOptions = new UnityEditor.Formats.Fbx.Exporter.ExportModelOptions
                {
                    ExportFormat = UnityEditor.Formats.Fbx.Exporter.ExportFormat.Binary
                };
                UnityEditor.Formats.Fbx.Exporter.ModelExporter.ExportObjects(
                    sourceFbxPath, new UnityEngine.Object[] { tempRoot }, exportOptions);

                UvtLog.Info($"[FBX Export] Exported vertex colors ({updated} mesh(es)) -> {sourceFbxPath}");

                // Restore .meta
                if (System.IO.File.Exists(metaBak))
                {
                    System.IO.File.Copy(metaBak, fullPath + ".meta", true);
                    System.IO.File.Delete(metaBak);
                }
                string fbxBak = fullPath + ".bak";
                if (System.IO.File.Exists(fbxBak))
                    System.IO.File.Delete(fbxBak);

                AssetDatabase.Refresh();

                // Re-link scene mesh references
                if (ctx.LodGroup != null)
                    RelinkSceneMeshReferences(sourceFbxPath, renameMap.Count > 0 ? renameMap : null, ctx.LodGroup);
            }
            catch (Exception ex)
            {
                UvtLog.Error("[FBX Export] Vertex color export failed: " + ex);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tempRoot);
            }

            // Restore readability
            if (madeReadable && srcImporter != null)
            {
                srcImporter.isReadable = false;
                Uv2AssetPostprocessor.bypassPaths.Add(sourceFbxPath);
                srcImporter.SaveAndReimport();
            }

            if (ctx.LodGroup != null) ctx.Refresh(ctx.LodGroup);
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
#endif
        }

        void ExportFbx(bool overwriteSource)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (ctx?.MeshEntries == null || ctx.MeshEntries.Count == 0)
            {
                UvtLog.Error("[FBX Export] No meshes loaded.");
                return;
            }

            // Restore any active preview (checker, AO, shell colors) before export
            // so that original materials are captured, not preview materials.
            RestoreAllPreviews();

            // Find the source FBX path from source LOD entries
            string sourceFbxFile = null;
            foreach (var e in ctx.MeshEntries)
            {
                if (e.fbxMesh == null) continue;
                string p = AssetDatabase.GetAssetPath(e.fbxMesh);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                { sourceFbxFile = p; break; }
            }
            // Fallback: try prefab/model source of the LODGroup GameObject
            if (string.IsNullOrEmpty(sourceFbxFile) && ctx.LodGroup != null)
            {
                var prefabSrc = PrefabUtility.GetCorrespondingObjectFromSource(ctx.LodGroup.gameObject);
                if (prefabSrc != null)
                {
                    string p = AssetDatabase.GetAssetPath(prefabSrc);
                    if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                        sourceFbxFile = p;
                }
                // Also try child renderers
                if (string.IsNullOrEmpty(sourceFbxFile))
                {
                    foreach (var r in ctx.LodGroup.GetComponentsInChildren<Renderer>(true))
                    {
                        var rSrc = PrefabUtility.GetCorrespondingObjectFromSource(r);
                        if (rSrc == null) continue;
                        string p = AssetDatabase.GetAssetPath(rSrc);
                        if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                        { sourceFbxFile = p; break; }
                    }
                }
            }

            // Last resort: use cached FBX path from context (set during initial Refresh)
            if (string.IsNullOrEmpty(sourceFbxFile))
                sourceFbxFile = ctx.SourceFbxPath;

            var fbxGroups = new Dictionary<string, List<(MeshEntry entry, Mesh resultMesh)>>();
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include) continue;
                Mesh resultMesh = GetResultMesh(e);
                if (resultMesh == null) continue;
                // Use source FBX path for all entries, not per-entry path
                // (generated LODs have .asset paths, not FBX)
                Mesh pathMesh = e.fbxMesh ?? e.originalMesh;
                string fbxPath = pathMesh != null ? AssetDatabase.GetAssetPath(pathMesh) : null;
                if (string.IsNullOrEmpty(fbxPath) || !fbxPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    fbxPath = sourceFbxFile; // fallback to source FBX
                if (string.IsNullOrEmpty(fbxPath)) continue;
                if (!fbxGroups.ContainsKey(fbxPath))
                    fbxGroups[fbxPath] = new List<(MeshEntry, Mesh)>();
                fbxGroups[fbxPath].Add((e, resultMesh));
            }
            if (fbxGroups.Count == 0) { UvtLog.Error("[FBX Export] No processed meshes to export."); return; }

            bool allGroupsSucceeded = true;
            var overwrittenFbxPaths = new HashSet<string>();
            var transientReplayEntriesByPath = new Dictionary<string, List<MeshUv2Entry>>();
            // Collected node renames from NormalizeExportHierarchy, per FBX path.
            // Used after reimport to re-link scene mesh references.
            var meshRenamesByFbx = new Dictionary<string, Dictionary<string, string>>();
            foreach (var kv in fbxGroups)
            {
                string sourceFbxPath = kv.Key;
                var entries = kv.Value;
                string exportPath;
                bool groupSucceeded = false;
                bool persistentSidecarMode = PostprocessorDefineManager.IsEnabled();
                string tempDir = System.IO.Path.GetTempPath();
                string fbxBakName = System.IO.Path.GetFileName(
                    System.IO.Path.GetFullPath(sourceFbxPath));
                if (overwriteSource)
                {
                    if (!EditorUtility.DisplayDialog("Overwrite Source FBX",
                        "This will overwrite:\n" + sourceFbxPath + "\n\nA backup (.fbx.bak) will be created. Continue?",
                        "Overwrite", "Cancel"))
                    {
                        allGroupsSucceeded = false;
                        continue;
                    }
                    exportPath = sourceFbxPath;
                    string fullSource = System.IO.Path.GetFullPath(sourceFbxPath);
                    string fullMeta = fullSource + ".meta";
                    try
                    {
                        System.IO.File.Copy(fullSource, System.IO.Path.Combine(tempDir, fbxBakName + ".bak"), true);
                        if (System.IO.File.Exists(fullMeta))
                            System.IO.File.Copy(fullMeta, System.IO.Path.Combine(tempDir, fbxBakName + ".meta.bak"), true);
                    }
                    catch (Exception ex) { UvtLog.Error("[FBX Export] Backup failed: " + ex.Message); allGroupsSucceeded = false; continue; }
                }
                else
                {
                    string dir = System.IO.Path.GetDirectoryName(sourceFbxPath);
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(sourceFbxPath);
                    exportPath = EditorUtility.SaveFilePanel("Export FBX", dir, baseName + "_uv2.fbx", "fbx");
                    if (string.IsNullOrEmpty(exportPath))
                    {
                        allGroupsSucceeded = false;
                        continue;
                    }
                    string dataPath = Application.dataPath;
                    if (exportPath.StartsWith(dataPath))
                        exportPath = "Assets" + exportPath.Substring(dataPath.Length);
                }

                // For overwrite flow, lock import settings BEFORE export.
                // This avoids an extra post-export reimport that can let third-party
                // importers (e.g. Bakery) touch UV2 again before user validation.
                if (overwriteSource)
                    Uv2AssetPostprocessor.PrepareImportSettings(sourceFbxPath, force: true);

                // Ensure FBX meshes are readable so the FBX Exporter can access
                // vertex data (especially for _COL meshes without sidecar data).
                var srcImporter = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
                bool madeReadable = false;
                if (!overwriteSource && srcImporter != null && !srcImporter.isReadable)
                {
                    srcImporter.isReadable = true;
                    Uv2AssetPostprocessor.bypassPaths.Add(sourceFbxPath);
                    srcImporter.SaveAndReimport();
                    madeReadable = true;
                }

                // Clone original FBX hierarchy and replace only the meshes
                var fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourceFbxPath);
                if (fbxPrefab == null) { UvtLog.Error("[FBX Export] Cannot load FBX prefab: " + sourceFbxPath); allGroupsSucceeded = false; continue; }
                var tempRoot = UnityEngine.Object.Instantiate(fbxPrefab);
                tempRoot.name = fbxPrefab.name;
                PromoteRootMeshToLod0Child(tempRoot);

                try
                {
                    var lastLodRendererTemplate = FindLastLodRenderer(entries);

                    // Build lookup: original mesh name -> export mesh
                    var meshReplacements = new Dictionary<string, Mesh>();
                    var meshRendererTemplates = new Dictionary<string, Renderer>();
                    foreach (var (entry, resultMesh) in entries)
                    {
                        var exportMesh = UnityEngine.Object.Instantiate(resultMesh);
                        // Copy UV channels from fbxMesh first (base UVs),
                        // then from originalMesh (has AO and other tool modifications).
                        if (entry.fbxMesh != null)
                            PreserveUvChannels(exportMesh, entry.fbxMesh);
                        if (entry.originalMesh != null && entry.originalMesh != entry.fbxMesh)
                        {
                            PreserveUvChannels(exportMesh, entry.originalMesh);
                            // Only overwrite UV1 from originalMesh when there is no
                            // repack/transfer result — otherwise the repacked lightmap
                            // UV in channel 1 takes priority over the pre-pipeline data.
                            if (entry.repackedMesh == null && entry.transferredMesh == null)
                                OverwriteUvChannel(exportMesh, entry.originalMesh, 1);
                        }
                        // AO often writes into UV2 components. Source meshes may not
                        // have UV2 at all, so pick the best available donor.
                        if (TryGetAppliedAoUvTarget(out int aoUvChannel, out int aoUvComponent))
                        {
                            var uv2Donor = SelectUv2Donor(entry, resultMesh, aoUvChannel);
                            if (uv2Donor != null)
                                MergeUvComponentFromDonor(exportMesh, uv2Donor, aoUvChannel, aoUvComponent);
                        }
                        string meshName = ResolveExportMeshName(entry, resultMesh);
                        meshReplacements[meshName] = exportMesh;
                        if (entry.renderer != null)
                            meshRendererTemplates[meshName] = entry.renderer;
                    }

                    // Replace meshes in cloned hierarchy
                    var replaced = new HashSet<string>();
                    foreach (var mf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.sharedMesh != null && meshReplacements.TryGetValue(mf.sharedMesh.name, out var replacement))
                        {
                            string meshName = mf.sharedMesh.name;
                            replaced.Add(meshName);
                            mf.sharedMesh = replacement;
                            if (meshRendererTemplates.TryGetValue(meshName, out var srcRenderer))
                            {
                                var dstRenderer = mf.GetComponent<MeshRenderer>();
                                if (dstRenderer != null)
                                    CopyRendererSettings(srcRenderer, dstRenderer);
                            }
                        }
                    }
                    foreach (var smr in tempRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (smr.sharedMesh != null && meshReplacements.TryGetValue(smr.sharedMesh.name, out var replacement))
                        {
                            string meshName = smr.sharedMesh.name;
                            replaced.Add(meshName);
                            smr.sharedMesh = replacement;
                            if (meshRendererTemplates.TryGetValue(meshName, out var srcRenderer))
                                CopyRendererSettings(srcRenderer, smr);
                        }
                    }

                    // Add meshes that weren't found in the clone (new LODs from generation)
                    foreach (var (entry, resultMesh) in entries)
                    {
                        string meshName = ResolveExportMeshName(entry, resultMesh);
                        if (replaced.Contains(meshName)) continue;
                        // Remove existing child with same name (from previous export)
                        for (int ci = tempRoot.transform.childCount - 1; ci >= 0; ci--)
                        {
                            var ch = tempRoot.transform.GetChild(ci);
                            if (ch.name == meshName) UnityEngine.Object.DestroyImmediate(ch.gameObject);
                        }
                        var child = new GameObject(meshName);
                        child.transform.SetParent(tempRoot.transform, false);
                        if (entry.renderer != null)
                        {
                            child.transform.localPosition = entry.renderer.transform.localPosition;
                            child.transform.localRotation = entry.renderer.transform.localRotation;
                            child.transform.localScale = entry.renderer.transform.localScale;
                        }
                        var newMf = child.AddComponent<MeshFilter>();
                        var exportMesh = UnityEngine.Object.Instantiate(resultMesh);
                        if (entry.fbxMesh != null)
                            PreserveUvChannels(exportMesh, entry.fbxMesh);
                        if (entry.originalMesh != null && entry.originalMesh != entry.fbxMesh)
                        {
                            PreserveUvChannels(exportMesh, entry.originalMesh);
                            if (entry.repackedMesh == null && entry.transferredMesh == null)
                                OverwriteUvChannel(exportMesh, entry.originalMesh, 1);
                        }
                        if (TryGetAppliedAoUvTarget(out int aoUvChannel, out int aoUvComponent))
                        {
                            var uv2Donor = SelectUv2Donor(entry, resultMesh, aoUvChannel);
                            if (uv2Donor != null)
                                MergeUvComponentFromDonor(exportMesh, uv2Donor, aoUvChannel, aoUvComponent);
                        }
                        newMf.sharedMesh = exportMesh;
                        var mr = child.AddComponent<MeshRenderer>();
                        if (lastLodRendererTemplate != null)
                        {
                            CopyRendererSettings(lastLodRendererTemplate, mr);
                            GameObjectUtility.SetStaticEditorFlags(child, GameObjectUtility.GetStaticEditorFlags(lastLodRendererTemplate.gameObject));
                        }
                        else if (entry.renderer != null)
                        {
                            CopyRendererSettings(entry.renderer, mr);
                            GameObjectUtility.SetStaticEditorFlags(child, GameObjectUtility.GetStaticEditorFlags(entry.renderer.gameObject));
                        }
                    }

                    // ── Remove stale children from cloned FBX ──
                    // For full LOD workflows we prune renderable leftovers that no longer
                    // belong to the export set. For standalone/partial FBX overwrite we
                    // must preserve untouched siblings and only replace the selected mesh.
                    // Must run BEFORE NormalizeExportHierarchy (which renames LOD0).
                    if (!(ctx != null && ctx.StandaloneMesh))
                    {
                        var validMeshNames = new HashSet<string>();
                        foreach (var (entry, resultMesh) in entries)
                        {
                            string meshName = ResolveExportMeshName(entry, resultMesh);
                            validMeshNames.Add(meshName);
                        }

                        // Protect meshes referenced by MeshCollider components.
                        // Some projects keep collision nodes without strict _COL naming,
                        // and there can be multiple colliders in the hierarchy.
                        var colliderMeshNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var colliderRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var mc in tempRoot.GetComponentsInChildren<MeshCollider>(true))
                        {
                            if (mc == null) continue;
                            colliderRootNames.Add(mc.gameObject.name);
                            if (mc.sharedMesh != null && !string.IsNullOrEmpty(mc.sharedMesh.name))
                                colliderMeshNames.Add(mc.sharedMesh.name);
                        }

                        for (int ci = tempRoot.transform.childCount - 1; ci >= 0; ci--)
                        {
                            var ch = tempRoot.transform.GetChild(ci);
                            // Preserve existing collision nodes from source FBX even when
                            // they are not part of mesh transfer entries.
                            if (MeshHygieneUtility.IsCollisionNodeName(ch.name))
                                continue;
                            if (colliderRootNames.Contains(ch.name))
                                continue;
                            var chMf = ch.GetComponent<MeshFilter>();
                            if (chMf != null && chMf.sharedMesh != null &&
                                colliderMeshNames.Contains(chMf.sharedMesh.name))
                                continue;
                            var chSmr = ch.GetComponent<SkinnedMeshRenderer>();
                            bool hasRenderableMesh =
                                (chMf != null && chMf.sharedMesh != null) ||
                                (chSmr != null && chSmr.sharedMesh != null);
                            // Keep structural/container nodes (no direct mesh on node).
                            // Removing them flattens FBX hierarchy and can break prefabs.
                            if (!hasRenderableMesh)
                                continue;
                            string childMeshName = null;
                            if (chMf != null && chMf.sharedMesh != null)
                                childMeshName = chMf.sharedMesh.name;
                            else if (chSmr != null && chSmr.sharedMesh != null)
                                childMeshName = chSmr.sharedMesh.name;

                            // Keep nodes when either the node name OR its bound mesh name
                            // is part of the export set. Some DCC/Unity imports keep node
                            // names different from mesh names (especially for root LOD0),
                            // and pruning by node name alone can drop valid LOD content.
                            bool keepByNodeName = validMeshNames.Contains(ch.name);
                            bool keepByMeshName = !string.IsNullOrEmpty(childMeshName) &&
                                                  validMeshNames.Contains(childMeshName);
                            if (!keepByNodeName && !keepByMeshName)
                            {
                                UvtLog.Verbose($"[FBX Export] Pruning stale child '{ch.name}'");
                                UnityEngine.Object.DestroyImmediate(ch.gameObject);
                            }
                        }
                    }
                    else
                    {
                        UvtLog.Verbose("[FBX Export] Standalone overwrite: preserving untouched sibling meshes in source FBX.");
                    }

                    // ── Normalize FBX hierarchy ──
                    // Ensure root is a clean pivot (identity transform, no mesh)
                    // and LOD0 child named same as root gets _LOD0 suffix.
                    // Returns a map of oldNodeName → newNodeName for mesh re-linking.
                    var nodeRenameMap = NormalizeExportHierarchy(tempRoot);
                    if (nodeRenameMap.Count > 0)
                        meshRenamesByFbx[sourceFbxPath] = nodeRenameMap;

                    // Add collision meshes from sidecar (if any).
                    // When sidecar provides collision data, remove existing _COL
                    // children first to avoid duplicates.
                    var collisionData = CollisionMeshTool.GetCollisionMeshesFromSidecar(sourceFbxPath);
                    if (collisionData.Count > 0)
                    {
                        for (int ci = tempRoot.transform.childCount - 1; ci >= 0; ci--)
                        {
                            var ch = tempRoot.transform.GetChild(ci);
                            if (MeshHygieneUtility.IsCollisionNodeName(ch.name))
                                UnityEngine.Object.DestroyImmediate(ch.gameObject);
                        }
                    }
                    int collisionMeshCount = 0;
                    foreach (var (colMeshName, colMeshes, isConvex) in collisionData)
                    {
                        if (colMeshes.Count == 1 && !isConvex)
                        {
                            // Simplified: single _COL child (no MeshRenderer — avoids stale material)
                            var colChild = new GameObject(colMeshName + "_COL");
                            colChild.transform.SetParent(tempRoot.transform, false);
                            colChild.AddComponent<MeshFilter>().sharedMesh = colMeshes[0];
                            collisionMeshCount++;
                        }
                        else
                        {
                            // Convex: container with hull children
                            var container = new GameObject(colMeshName + "_COL");
                            container.transform.SetParent(tempRoot.transform, false);
                            for (int hi = 0; hi < colMeshes.Count; hi++)
                            {
                                var hullChild = new GameObject($"{colMeshName}_COL_Hull{hi}");
                                hullChild.transform.SetParent(container.transform, false);
                                hullChild.AddComponent<MeshFilter>().sharedMesh = colMeshes[hi];
                                collisionMeshCount++;
                            }
                        }
                    }

                    if (collisionMeshCount > 0)
                        UvtLog.Verbose($"[FBX Export] Added {collisionMeshCount} collision mesh(es) from sidecar");

                    // Strip _COL meshes to bare minimum: vertices + triangles +
                    // averaged normals + tangents. No UVs, colors, or other channels.
                    // Assign a real material from LOD0 render mesh to prevent FBX
                    // Exporter from writing a default "Lit" material on collision nodes.
                    Material colFallbackMat = null;
                    foreach (var (entry, _) in entries)
                    {
                        if (entry.renderer != null && entry.renderer.sharedMaterial != null &&
                            !CheckerTexturePreview.IsPreviewShader(entry.renderer.sharedMaterial.shader.name))
                        {
                            colFallbackMat = entry.renderer.sharedMaterial;
                            break;
                        }
                    }

                    foreach (var colMf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (colMf == null || colMf.sharedMesh == null) continue;
                        if (!MeshHygieneUtility.IsCollisionNodeName(colMf.gameObject.name)) continue;

                        // Assign LOD0 material to collision renderer so FBX Exporter
                        // doesn't create a stale "Lit" default. If no render material
                        // is available, destroy the renderer as fallback.
                        var colMr = colMf.GetComponent<MeshRenderer>();
                        if (colFallbackMat != null)
                        {
                            if (colMr == null)
                                colMr = colMf.gameObject.AddComponent<MeshRenderer>();
                            colMr.sharedMaterials = new[] { colFallbackMat };
                        }
                        else if (colMr != null)
                        {
                            UnityEngine.Object.DestroyImmediate(colMr);
                        }

                        var srcCol = colMf.sharedMesh;
                        if (srcCol.isReadable)
                        {
                            var stripped = new Mesh { name = srcCol.name };
                            stripped.SetVertices(srcCol.vertices);
                            for (int s = 0; s < srcCol.subMeshCount; s++)
                                stripped.SetTriangles(srcCol.GetTriangles(s), s);
                            stripped.RecalculateNormals();
                            // Generate tangents from normals (no UVs to derive from)
                            var normals = stripped.normals;
                            var tangents = new Vector4[normals.Length];
                            for (int ti = 0; ti < normals.Length; ti++)
                            {
                                Vector3 n = normals[ti];
                                Vector3 t = Vector3.Cross(n, Vector3.up);
                                if (t.sqrMagnitude < 0.001f)
                                    t = Vector3.Cross(n, Vector3.right);
                                t.Normalize();
                                tangents[ti] = new Vector4(t.x, t.y, t.z, 1f);
                            }
                            stripped.tangents = tangents;
                            stripped.RecalculateBounds();
                            colMf.sharedMesh = stripped;
                        }
                        else
                        {
                            // Non-readable FBX sub-asset — can't strip attributes and
                            // the FBX Exporter can't export it either. Log a warning;
                            // the collision data should normally come from the sidecar.
                            UvtLog.Warn($"[FBX Export] Collision mesh '{srcCol.name}' is not readable — " +
                                        "skipping strip. Re-save collision to sidecar to fix.");
                        }
                    }

                    // Trim material arrays to match submesh count — prevents
                    // FBX Exporter from creating spurious default "Lit" material entries.
                    foreach (var mr in tempRoot.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        var mesh = mr.GetComponent<MeshFilter>()?.sharedMesh;
                        if (mesh == null) continue;
                        var mats = mr.sharedMaterials;
                        if (mats.Length > mesh.subMeshCount)
                        {
                            UvtLog.Verbose($"[FBX Export] Trimming materials on '{mr.gameObject.name}': " +
                                $"{mats.Length} → {mesh.subMeshCount}");
                            var trimmed = new Material[mesh.subMeshCount];
                            System.Array.Copy(mats, trimmed, trimmed.Length);
                            mr.sharedMaterials = trimmed;
                        }
                    }

                    var exportOptions = new ExportModelOptions { ExportFormat = ExportFormat.Binary };
                    ModelExporter.ExportObjects(exportPath, new UnityEngine.Object[] { tempRoot }, exportOptions);
                    int totalExported = entries.Count + collisionMeshCount;
                    UvtLog.Info("[FBX Export] Exported (binary) " + totalExported + " mesh(es) -> " + exportPath);
                    groupSucceeded = true;
                    // Restore original .meta from temp backup
                    if (overwriteSource)
                    {
                        string fullPath = System.IO.Path.GetFullPath(sourceFbxPath);
                        string metaBak = System.IO.Path.Combine(tempDir, fbxBakName + ".meta.bak");
                        if (System.IO.File.Exists(metaBak))
                        {
                            System.IO.File.Copy(metaBak, fullPath + ".meta", true);
                            System.IO.File.Delete(metaBak);
                        }
                        string fbxBak = System.IO.Path.Combine(tempDir, fbxBakName + ".bak");
                        if (System.IO.File.Exists(fbxBak))
                            System.IO.File.Delete(fbxBak);
                        overwrittenFbxPaths.Add(sourceFbxPath);
                    }
                }
                catch (Exception ex) { UvtLog.Error("[FBX Export] Export failed: " + ex); allGroupsSucceeded = false; }
                finally { UnityEngine.Object.DestroyImmediate(tempRoot); }

                // Restore isReadable if we changed it (non-overwrite path only;
                // overwrite path restores .meta from backup automatically).
                if (madeReadable && !overwriteSource && srcImporter != null)
                {
                    srcImporter.isReadable = false;
                    Uv2AssetPostprocessor.bypassPaths.Add(sourceFbxPath);
                    srcImporter.SaveAndReimport();
                }

                if (!groupSucceeded)
                    allGroupsSucceeded = false;

                // Save sidecar entries so our postprocessor (order=10000) can
                // re-apply UV2 after third-party postprocessors (e.g. Bakery auto-unwrap).
                // If Sidecar UV2 Mode is off, mark the path for one-shot replay and
                // cleanup after the current import finishes.
                if (overwriteSource)
                {
                    var sidecarEntries = BuildSidecarEntriesForExport(entries);
                    if (persistentSidecarMode)
                    {
                        SaveSidecarForExport(sourceFbxPath, sidecarEntries);
                    }
                    else
                    {
                        transientReplayEntriesByPath[sourceFbxPath] = sidecarEntries;
                        ArmTransientReplayForOverwrite(sourceFbxPath, transientReplayEntriesByPath);
                    }

                    Uv2AssetPostprocessor.managedImportPaths.Add(sourceFbxPath);
                    if (!persistentSidecarMode)
                        Uv2AssetPostprocessor.transientReplayPaths.Add(sourceFbxPath);
                }

                // Always disable generateSecondaryUV after overwriting FBX with
                // transferred UV2. This is a post-export fallback in case importer
                // state changed during export/reimport despite the pre-lock above.
                if (overwriteSource && groupSucceeded)
                {
                    var fbxImp = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
                    if (fbxImp != null && fbxImp.generateSecondaryUV)
                    {
                        if (!persistentSidecarMode)
                            ArmTransientReplayForOverwrite(sourceFbxPath, transientReplayEntriesByPath);
                        fbxImp.generateSecondaryUV = false;
                        fbxImp.SaveAndReimport();
                        UvtLog.Info($"[FBX Export] Disabled generateSecondaryUV on '{sourceFbxPath}'");
                    }
                }
            }

            // Clean up scene-generated LOD objects from LodGenerationTool.
            // These are now embedded in the exported FBX and would duplicate on reimport.
            if (overwriteSource && allGroupsSucceeded)
                LodGenerationTool.ActiveInstance?.ClearGeneratedLods();

            // UV2 is baked into the FBX AND saved in sidecar (for re-application after
            // third-party postprocessors like Bakery). Don't clear sidecar entries.

            AssetDatabase.Refresh();

            // Re-link scene mesh references after FBX reimport.
            // Unity recreates sub-asset meshes on reimport; old MeshFilter
            // references go Missing even when names didn't change.
            // Always re-link for every overwritten FBX.
            if (overwriteSource && allGroupsSucceeded && ctx?.LodGroup != null)
            {
                foreach (string fbxPath in overwrittenFbxPaths)
                {
                    Dictionary<string, string> renameMap = null;
                    meshRenamesByFbx.TryGetValue(fbxPath, out renameMap);
                    RelinkSceneMeshReferences(fbxPath, renameMap, ctx.LodGroup);
                }
            }

            // Remove stale material remaps created by FBX importer defaults on
            // collision-only nodes. This clears unwanted "Lit"/"No Name" entries
            // that should not survive an overwrite export.
            if (overwriteSource && allGroupsSucceeded)
            {
                foreach (string fbxPath in overwrittenFbxPaths)
                {
                    var imp = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                    if (imp == null) continue;
                    var map = imp.GetExternalObjectMap();
                    var toRemove = new List<AssetImporter.SourceAssetIdentifier>();
                    foreach (var kvp in map)
                    {
                        if (kvp.Key.type != typeof(Material)) continue;
                        if (kvp.Key.name == "Lit" || kvp.Key.name == "No Name")
                            toRemove.Add(kvp.Key);
                    }
                    if (toRemove.Count > 0)
                    {
                        if (transientReplayEntriesByPath.Count > 0)
                            ArmTransientReplayForOverwrite(fbxPath, transientReplayEntriesByPath);
                        foreach (var key in toRemove)
                            imp.RemoveRemap(key);
                        imp.SaveAndReimport();
                    }
                }
            }

            if (allGroupsSucceeded)
                SwitchToPostApplyView();
#endif
        }

        static void ArmTransientReplayForOverwrite(
            string assetPath,
            Dictionary<string, List<MeshUv2Entry>> transientReplayEntriesByPath)
        {
            if (string.IsNullOrEmpty(assetPath) || transientReplayEntriesByPath == null)
                return;

            if (!transientReplayEntriesByPath.TryGetValue(assetPath, out var entries) ||
                entries == null || entries.Count == 0)
                return;

            Uv2AssetPostprocessor.SetTransientReplayEntries(assetPath, entries);
            Uv2AssetPostprocessor.managedImportPaths.Add(assetPath);
            Uv2AssetPostprocessor.transientReplayPaths.Add(assetPath);
        }

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

        /// <summary>
        /// Build and save sidecar UV2 entries from export data so the postprocessor
        /// can re-apply UV2 after third-party postprocessors (e.g. Bakery auto-unwrap).
        /// </summary>
        void SaveSidecarForExport(string fbxPath, List<MeshUv2Entry> sidecarEntries)
        {
            string sidecarPath = Uv2DataAsset.GetSidecarPath(fbxPath);
            var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<Uv2DataAsset>();
                AssetDatabase.CreateAsset(data, sidecarPath);
            }

            int saved = 0;
            foreach (var sidecarEntry in sidecarEntries)
            {
                data.Set(sidecarEntry);
                saved++;
            }

            if (saved > 0)
            {
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();
                UvtLog.Info($"[FBX Export] Saved {saved} UV2 entries to sidecar '{sidecarPath}' for post-import re-application");
            }
        }

        static void ClearUv2EntriesForFbxPaths(IEnumerable<string> fbxPaths)
        {
            int cleared = 0;
            foreach (var fbxPath in fbxPaths)
            {
                if (string.IsNullOrEmpty(fbxPath)) continue;
                string sidecarPath = Uv2DataAsset.GetSidecarPath(fbxPath);
                var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
                if (data == null) continue;

                bool hasCollision = data.collisionEntries != null && data.collisionEntries.Count > 0;
                bool hasUv2 = data.entries != null && data.entries.Count > 0;

                if (hasUv2)
                {
                    data.entries.Clear();
                    cleared++;
                }

                if (hasCollision)
                {
                    // Keep sidecar alive for collision data
                    EditorUtility.SetDirty(data);
                }
                else if (hasUv2)
                {
                    // No collision entries — sidecar is now empty, delete it
                    AssetDatabase.DeleteAsset(sidecarPath);
                }
            }
            if (cleared > 0)
            {
                AssetDatabase.SaveAssets();
                UvtLog.Info($"[FBX Export] Cleared UV2 entries from {cleared} sidecar(s) after overwrite (collision entries preserved).");
            }
        }

        static Renderer FindLastLodRenderer(List<(MeshEntry entry, Mesh resultMesh)> entries)
        {
            Renderer best = null;
            int bestLod = int.MinValue;
            foreach (var (entry, _) in entries)
            {
                if (entry == null || entry.renderer == null) continue;
                if (entry.lodIndex >= bestLod)
                {
                    bestLod = entry.lodIndex;
                    best = entry.renderer;
                }
            }
            return best;
        }

        static void CopyRendererSettings(Renderer src, Renderer dst)
        {
            if (src == null || dst == null) return;

            var srcMats = src.sharedMaterials;
            bool hasPreviewMat = false;
            for (int i = 0; i < srcMats.Length; i++)
            {
                var m = srcMats[i];
                string shaderName = m != null && m.shader != null ? m.shader.name : null;
                if (CheckerTexturePreview.IsPreviewShader(shaderName))
                {
                    hasPreviewMat = true;
                    break;
                }
            }
            if (!hasPreviewMat)
                dst.sharedMaterials = srcMats;
            dst.shadowCastingMode = src.shadowCastingMode;
            dst.receiveShadows = src.receiveShadows;
            dst.lightProbeUsage = src.lightProbeUsage;
            dst.reflectionProbeUsage = src.reflectionProbeUsage;
            dst.probeAnchor = src.probeAnchor;
            dst.motionVectorGenerationMode = src.motionVectorGenerationMode;
            dst.allowOcclusionWhenDynamic = src.allowOcclusionWhenDynamic;
            dst.renderingLayerMask = src.renderingLayerMask;
            dst.rendererPriority = src.rendererPriority;

            if (src is MeshRenderer srcMr && dst is MeshRenderer dstMr)
            {
                dstMr.receiveGI = srcMr.receiveGI;
                dstMr.scaleInLightmap = srcMr.scaleInLightmap;
                dstMr.stitchLightmapSeams = srcMr.stitchLightmapSeams;
                dstMr.lightmapScaleOffset = srcMr.lightmapScaleOffset;
                dstMr.realtimeLightmapScaleOffset = srcMr.realtimeLightmapScaleOffset;
                dstMr.lightmapIndex = srcMr.lightmapIndex;
                dstMr.realtimeLightmapIndex = srcMr.realtimeLightmapIndex;
            }
        }

        void RefreshSetupSelectionCache(GameObject selected, List<(GameObject go, int lodIndex)> siblings)
        {
            int selectionId = selected != null ? selected.GetInstanceID() : -1;
            if (selectionId == setupLodSelectionId && cachedSetupDetectedLods.Count == siblings.Count)
                return;

            setupLodSelectionId = selectionId;
            cachedSetupDetectedLods.Clear();
            foreach (var (go, lodIndex) in siblings)
            {
                var renderers = go.GetComponentsInChildren<Renderer>();
                int tris = 0;
                foreach (var r in renderers)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    tris += GetTriangleCount(mf != null ? mf.sharedMesh : null);
                }
                cachedSetupDetectedLods.Add((go, lodIndex, renderers.Length, tris));
            }
        }

        bool SetupSelectionHasRenderers(GameObject selected)
        {
            int selectionId = selected != null ? selected.GetInstanceID() : -1;
            if (selectionId != setupRendererSelectionId)
            {
                setupRendererSelectionId = selectionId;
                setupSelectionHasRenderers = selected != null && selected.GetComponentInChildren<Renderer>() != null;
            }
            return setupSelectionHasRenderers;
        }

        static int GetTriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            long indexCount = 0;
            int subMeshCount = mesh.subMeshCount;
            for (int i = 0; i < subMeshCount; i++)
                indexCount += mesh.GetIndexCount(i);
            return (int)(indexCount / 3L);
        }

        /// <summary>
        /// If the cloned source FBX carries its visible mesh on the root object,
        /// split it into a new child named after the mesh so the rest of the
        /// export pipeline treats it as a LOD entry. NormalizeExportHierarchy
        /// will then rename the new child to baseName_LOD0.
        /// </summary>
        static void PromoteRootMeshToLod0Child(GameObject tempRoot)
        {
            if (tempRoot == null) return;
            var rootMf = tempRoot.GetComponent<MeshFilter>();
            if (rootMf == null || rootMf.sharedMesh == null) return;
            var rootMr = tempRoot.GetComponent<MeshRenderer>();

            var rootMesh = rootMf.sharedMesh;

            // Skip if a direct child already holds this mesh — source FBX has
            // both a root-level mesh and a duplicate LOD child; we'd collide.
            for (int ci = 0; ci < tempRoot.transform.childCount; ci++)
            {
                var existing = tempRoot.transform.GetChild(ci).GetComponent<MeshFilter>();
                if (existing != null && existing.sharedMesh == rootMesh) return;
            }

            // Name the child after the mesh — the stale-child pruning keeps
            // children whose name matches a mesh-entry key, and
            // NormalizeExportHierarchy renames "direct child equal to root name"
            // to baseName_LOD0.
            string childName = rootMesh.name;
            if (string.IsNullOrEmpty(childName)) childName = tempRoot.name;

            var lod0 = new GameObject(childName);
            lod0.transform.SetParent(tempRoot.transform, false);
            lod0.transform.localPosition = Vector3.zero;
            lod0.transform.localRotation = Quaternion.identity;
            lod0.transform.localScale = Vector3.one;

            var newMf = lod0.AddComponent<MeshFilter>();
            newMf.sharedMesh = rootMesh;

            if (rootMr != null)
            {
                var newMr = lod0.AddComponent<MeshRenderer>();
                CopyRendererSettings(rootMr, newMr);
                GameObjectUtility.SetStaticEditorFlags(lod0,
                    GameObjectUtility.GetStaticEditorFlags(tempRoot));
                UnityEngine.Object.DestroyImmediate(rootMr);
            }
            UnityEngine.Object.DestroyImmediate(rootMf);
        }

        /// <summary>
        /// Normalizes the export hierarchy:
        /// - Root transform reset to identity (clean pivot at 0,0,0).
        /// - Direct child with same name as root (LOD0 without suffix) renamed to _LOD0.
        /// - Collision node transforms baked into vertices (pivot at origin).
        /// Does NOT move meshes off the root — preserves original FBX structure.
        /// </summary>
        /// <summary>
        /// Returns a dictionary of oldNodeName → newNodeName for nodes that were renamed.
        /// Used to re-link scene mesh references after FBX reimport.
        /// </summary>
        static Dictionary<string, string> NormalizeExportHierarchy(GameObject root)
        {
            var renameMap = new Dictionary<string, string>();
            string baseName = root.name;
            string sanitizedBaseName = MeshHygieneUtility.SanitizeName(baseName);
            if (string.IsNullOrEmpty(sanitizedBaseName))
                sanitizedBaseName = "Unnamed";

            // Reset root transform to identity (clean pivot at origin)
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            // Rename direct child that matches root name (LOD0 without suffix) to baseName_LOD0
            foreach (Transform child in root.transform)
            {
                if (child.name == baseName || child.name == sanitizedBaseName)
                {
                    string oldName = child.name;
                    child.name = sanitizedBaseName + "_LOD0";
                    if (oldName != child.name)
                        renameMap[oldName] = child.name;
                    break;
                }
            }

            // Normalize direct child LOD names to contiguous _LOD0.._LODN suffixes.
            // This prevents importer-side warnings ("_LOD1 found but no _LOD0")
            // when source names contained invalid characters (e.g. dots) and were
            // sanitized inconsistently across tools.
            var directLodChildren = new List<(Transform transform, int index)>();
            foreach (Transform child in root.transform)
            {
                if (MeshHygieneUtility.IsCollisionNodeName(child.name))
                    continue;

                var mf = child.GetComponent<MeshFilter>();
                var smr = child.GetComponent<SkinnedMeshRenderer>();
                bool hasMesh = (mf != null && mf.sharedMesh != null) ||
                               (smr != null && smr.sharedMesh != null);
                if (!hasMesh)
                    continue;

                var match = System.Text.RegularExpressions.Regex.Match(
                    child.name,
                    @"_LOD(\d+)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success)
                    continue;

                int parsedIndex;
                if (!int.TryParse(match.Groups[1].Value, out parsedIndex))
                    continue;

                directLodChildren.Add((child, parsedIndex));
            }

            if (directLodChildren.Count > 0)
            {
                directLodChildren.Sort((a, b) =>
                {
                    int cmp = a.index.CompareTo(b.index);
                    return cmp != 0 ? cmp : a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex());
                });

                for (int i = 0; i < directLodChildren.Count; i++)
                {
                    string normalizedName = sanitizedBaseName + "_LOD" + i;
                    string oldName = directLodChildren[i].transform.name;
                    if (oldName != normalizedName)
                    {
                        directLodChildren[i].transform.name = normalizedName;
                        renameMap[oldName] = normalizedName;
                    }
                }
            }

            // Bake non-identity transforms into mesh vertices for ALL children,
            // then reset to identity. This normalizes scale (common problem:
            // FBX imported at 0.01 with compensating 100x scale on node) and
            // ensures exported FBX has clean 1,1,1 scale on every node.
            foreach (var childMf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (childMf == null || childMf.sharedMesh == null) continue;
                // Skip root itself (already reset above)
                if (childMf.transform == root.transform) continue;

                var t = childMf.transform;
                if (t.localPosition == Vector3.zero &&
                    t.localRotation == Quaternion.identity &&
                    t.localScale == Vector3.one)
                    continue; // already at identity

                var mesh = childMf.sharedMesh;
                if (!mesh.isReadable) continue;

                BakeTransformIntoMesh(mesh, t);

                // Reset transform to identity
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }

            return renameMap;
        }

        /// <summary>
        /// Bake a Transform's local position/rotation/scale into mesh vertex data.
        /// After calling, the transform can be safely reset to identity without
        /// changing the visual result. Handles vertices, normals, and tangents.
        /// </summary>
        static void BakeTransformIntoMesh(Mesh mesh, Transform t)
        {
            if (mesh == null || !mesh.isReadable) return;

            var localMatrix = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);
            var verts = mesh.vertices;
            var normals = mesh.normals;

            for (int i = 0; i < verts.Length; i++)
            {
                verts[i] = localMatrix.MultiplyPoint3x4(verts[i]);
                if (normals != null && i < normals.Length)
                    normals[i] = localMatrix.MultiplyVector(normals[i]).normalized;
            }
            mesh.SetVertices(verts);
            if (normals != null && normals.Length > 0)
                mesh.SetNormals(normals);

            var tangents = mesh.tangents;
            if (tangents != null && tangents.Length > 0)
            {
                for (int i = 0; i < tangents.Length; i++)
                {
                    Vector3 tVec = localMatrix.MultiplyVector(
                        new Vector3(tangents[i].x, tangents[i].y, tangents[i].z)).normalized;
                    tangents[i] = new Vector4(tVec.x, tVec.y, tVec.z, tangents[i].w);
                }
                mesh.tangents = tangents;
            }
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// After FBX reimport, re-link scene MeshFilter/MeshCollider/SkinnedMeshRenderer
        /// references to the fresh sub-asset meshes. Unity recreates sub-assets on reimport
        /// so old references go Missing even when names didn't change.
        /// <paramref name="renameMap"/> is optional — provides oldName→newName for renamed nodes.
        /// </summary>
        static void RelinkSceneMeshReferences(
            string fbxPath,
            Dictionary<string, string> renameMap,
            LODGroup lodGroup)
        {
            if (lodGroup == null) return;

            // Load all mesh sub-assets from the reimported FBX keyed by name
            var subAssets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
            var meshByName = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in subAssets)
            {
                var mesh = asset as Mesh;
                if (mesh != null && !meshByName.ContainsKey(mesh.name))
                    meshByName[mesh.name] = mesh;
            }

            if (meshByName.Count == 0) return;

            int relinked = 0;
            var root = lodGroup.transform;

            // ── Re-link MeshFilter references ──
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null) continue;

                if (mf.sharedMesh != null)
                {
                    // Mesh reference exists — try to refresh to new sub-asset by name
                    string meshName = mf.sharedMesh.name;

                    // Check if this mesh was renamed
                    if (renameMap != null && renameMap.TryGetValue(meshName, out string newName))
                        meshName = newName;

                    if (meshByName.TryGetValue(meshName, out var freshMesh) && mf.sharedMesh != freshMesh)
                    {
                        Undo.RecordObject(mf, "Relink Mesh");
                        mf.sharedMesh = freshMesh;
                        relinked++;
                    }
                }
                else
                {
                    // Missing mesh — try to find by GameObject name
                    string goName = mf.gameObject.name;

                    // Direct match
                    if (meshByName.TryGetValue(goName, out var match))
                    {
                        Undo.RecordObject(mf, "Relink Mesh");
                        mf.sharedMesh = match;
                        relinked++;
                        continue;
                    }

                    // Try renamed name
                    if (renameMap != null)
                    {
                        foreach (var kvp in renameMap)
                        {
                            if (goName == kvp.Key && meshByName.TryGetValue(kvp.Value, out var renamedMesh))
                            {
                                Undo.RecordObject(mf, "Relink Mesh");
                                mf.sharedMesh = renamedMesh;
                                relinked++;
                                break;
                            }
                        }
                    }

                    // Fallback: fuzzy match by stripping LOD suffix from GO name
                    if (mf.sharedMesh == null)
                    {
                        foreach (var kvp in meshByName)
                        {
                            if (goName.Contains(kvp.Key) || kvp.Key.Contains(goName))
                            {
                                Undo.RecordObject(mf, "Relink Mesh");
                                mf.sharedMesh = kvp.Value;
                                relinked++;
                                break;
                            }
                        }
                    }
                }
            }

            // ── Re-link MeshCollider references ──
            foreach (var mc in root.GetComponentsInChildren<MeshCollider>(true))
            {
                if (mc == null) continue;

                if (mc.sharedMesh != null)
                {
                    string meshName = mc.sharedMesh.name;
                    if (renameMap != null && renameMap.TryGetValue(meshName, out string newName))
                        meshName = newName;

                    if (meshByName.TryGetValue(meshName, out var freshMesh) && mc.sharedMesh != freshMesh)
                    {
                        Undo.RecordObject(mc, "Relink Collider Mesh");
                        mc.sharedMesh = freshMesh;
                        relinked++;
                    }
                }
                else
                {
                    // Missing collider mesh — try by GO name
                    if (meshByName.TryGetValue(mc.gameObject.name, out var match))
                    {
                        Undo.RecordObject(mc, "Relink Collider Mesh");
                        mc.sharedMesh = match;
                        relinked++;
                    }
                }
            }

            // ── Rename scene GameObjects to match new FBX node names ──
            if (renameMap != null)
            {
                foreach (var kvp in renameMap)
                {
                    foreach (Transform child in root)
                    {
                        if (child.name == kvp.Key)
                        {
                            Undo.RecordObject(child.gameObject, "Rename to match FBX");
                            child.name = kvp.Value;
                            break;
                        }
                    }
                }
            }

            if (relinked > 0)
                UvtLog.Info($"[FBX Export] Relinked {relinked} mesh reference(s) after reimport.");
        }

        void GenerateLods()
        {
            if (ctx.LodGroup == null) return;

            var sourceMeshes = new List<(MeshEntry entry, Mesh mesh)>();
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.lodIndex != ctx.SourceLodIndex) continue;
                Mesh src = e.repackedMesh ?? e.originalMesh;
                if (src != null) sourceMeshes.Add((e, src));
            }
            if (sourceMeshes.Count == 0) { UvtLog.Error("[GenerateLOD] No source meshes found."); return; }

            string savePath = ctx.PipeSettings.savePath;
            if (string.IsNullOrEmpty(savePath)) savePath = "Assets/LightmapUvTool_Output";
            if (!AssetDatabase.IsValidFolder(savePath))
            {
                var par = System.IO.Path.GetDirectoryName(savePath);
                var fld = System.IO.Path.GetFileName(savePath);
                if (!string.IsNullOrEmpty(par)) AssetDatabase.CreateFolder(par, fld);
            }

            var lods = ctx.LodGroup.GetLODs();
            var newLods = new List<LOD>(lods);

            try
            {
                for (int lodIdx = 0; lodIdx < generateLodCount; lodIdx++)
                {
                    float ratio = generateLodRatios[lodIdx];
                    var settings = new MeshSimplifier.SimplifySettings
                    {
                        targetRatio  = ratio,
                        targetError  = generateTargetError,
                        uv2Weight    = generateUv2Weight,
                        normalWeight = generateNormalWeight,
                        lockBorder   = generateLockBorder,
                        uvChannel    = 1
                    };

                    float progress = (float)lodIdx / generateLodCount;
                    EditorUtility.DisplayProgressBar("Generate LODs",
                        $"LOD {lodIdx + 1}/{generateLodCount} (ratio {ratio:P0})", progress);

                    var lodRenderers = new List<Renderer>();
                    foreach (var (entry, srcMesh) in sourceMeshes)
                    {
                        var r = MeshSimplifier.Simplify(srcMesh, settings);
                        if (!r.ok) { UvtLog.Error($"[GenerateLOD] Failed on {srcMesh.name}: {r.error}"); continue; }

                        string baseName = entry.fbxMesh != null ? entry.fbxMesh.name : srcMesh.name;
                        baseName = System.Text.RegularExpressions.Regex.Replace(baseName, @"(_wc|_repack|_uvTransfer|_optimized|_LOD\d+)+$", "");
                        string meshName = baseName + "_LOD" + (ctx.SourceLodIndex + lodIdx + 1);
                        r.simplifiedMesh.name = meshName;
                        string assetPath = AssetDatabase.GenerateUniqueAssetPath(savePath + "/" + meshName + ".asset");
                        AssetDatabase.CreateAsset(r.simplifiedMesh, assetPath);
                        UvtLog.Info($"[GenerateLOD] {meshName}: {r.originalTriCount} → {r.simplifiedTriCount} tris, saved → {assetPath}");

                        if (generateAddToLodGroup && entry.renderer != null)
                        {
                            var go = new GameObject(meshName);
                            go.transform.SetParent(ctx.LodGroup.transform, false);
                            go.transform.localPosition = entry.renderer.transform.localPosition;
                            go.transform.localRotation = entry.renderer.transform.localRotation;
                            go.transform.localScale    = entry.renderer.transform.localScale;
                            var mf = go.AddComponent<MeshFilter>();
                            mf.sharedMesh = r.simplifiedMesh;
                            var mr = go.AddComponent<MeshRenderer>();
                            mr.sharedMaterials = entry.renderer.sharedMaterials;
                            Undo.RegisterCreatedObjectUndo(go, "Generate LOD");
                            lodRenderers.Add(mr);
                        }
                    }

                    if (generateAddToLodGroup && lodRenderers.Count > 0)
                    {
                        int newLodIdx = ctx.SourceLodIndex + lodIdx + 1;
                        float baseHeight = newLods.Count > 0 ? newLods[newLods.Count - 1].screenRelativeTransitionHeight : 0.5f;
                        float height = baseHeight * 0.5f;
                        var newLod = new LOD(height, lodRenderers.ToArray());
                        if (newLodIdx < newLods.Count) newLods.Insert(newLodIdx, newLod);
                        else newLods.Add(newLod);
                    }
                }

                if (generateAddToLodGroup)
                {
                    Undo.RecordObject(ctx.LodGroup, "Generate LODs");
                    ctx.LodGroup.SetLODs(newLods.ToArray());
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            finally { EditorUtility.ClearProgressBar(); }

            // Add new LOD entries without destroying pipeline state
            var currentLods2 = ctx.LodGroup.GetLODs();
            for (int li = 0; li < currentLods2.Length; li++)
            {
                if (ctx.MeshEntries.Any(e => e.lodIndex == li)) continue;
                if (currentLods2[li].renderers == null) continue;
                foreach (var r in currentLods2[li].renderers)
                {
                    if (r == null) continue;
                    var mf2 = r.GetComponent<MeshFilter>();
                    if (mf2 == null || mf2.sharedMesh == null) continue;
                    ctx.MeshEntries.Add(new MeshEntry
                    {
                        lodIndex = li, renderer = r, meshFilter = mf2,
                        originalMesh = mf2.sharedMesh, fbxMesh = mf2.sharedMesh,
                        meshGroupKey = UvToolContext.ExtractGroupKey(r.name)
                    });
                }
            }
            ctx.ClearAllCaches();
            requestRepaint?.Invoke();
        }

        void SaveAll()
        {
            string p = ctx.PipeSettings.savePath;
            if (string.IsNullOrEmpty(p)) p = "Assets/LightmapUvTool_Output";
            if (!AssetDatabase.IsValidFolder(p))
            {
                var par = System.IO.Path.GetDirectoryName(p);
                var fld = System.IO.Path.GetFileName(p);
                if (!string.IsNullOrEmpty(par)) AssetDatabase.CreateFolder(par, fld);
            }
            int n = 0;
            foreach (var e in ctx.MeshEntries)
            {
                Mesh m = GetResultMesh(e);
                if (m == null) continue;
                string ap = AssetDatabase.GenerateUniqueAssetPath(p + "/" + m.name + ".asset");
                AssetDatabase.CreateAsset(m, ap); n++;
            }
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            UvtLog.Info("[Save] " + n + " assets -> " + p);
        }

        void UpdateRefs()
        {
            if (ctx.LodGroup == null) return;
            int n = 0;
            foreach (var e in ctx.MeshEntries)
            {
                Mesh m = GetResultMesh(e);
                if (m == null || e.meshFilter == null) continue;
                Undo.RecordObject(e.meshFilter, "UV Transfer");
                e.meshFilter.sharedMesh = m; n++;
            }
            UvtLog.Info("[Save] " + n + " refs updated");
        }

        // ════════════════════════════════════════════════════════════
        //  Reset Methods
        // ════════════════════════════════════════════════════════════

        void ResetWorkingCopies()
        {
            RestoreAllPreviews();
            // Destroy all working mesh copies and restore fbxMesh on MeshFilters.
            // Does NOT delete sidecar assets — use ResetUv2FromFbx for that.
            foreach (var e in ctx.MeshEntries)
            {
                // Restore original mesh on MeshFilter before destroying working copies
                if (e.meshFilter != null && e.fbxMesh != null)
                    e.meshFilter.sharedMesh = e.fbxMesh;
                if (e.transferredMesh != null) { UnityEngine.Object.DestroyImmediate(e.transferredMesh); e.transferredMesh = null; }
                if (e.repackedMesh != null) { UnityEngine.Object.DestroyImmediate(e.repackedMesh); e.repackedMesh = null; }
                if (e.originalMesh != null && e.originalMesh != e.fbxMesh) UnityEngine.Object.DestroyImmediate(e.originalMesh);
                if (e.fbxMesh != null) e.originalMesh = e.fbxMesh;
                e.shellTransferResult = null;
                e.wasWelded = e.wasEdgeWelded = e.wasSymmetrySplit = false;
            }
            ctx.HasRepack = ctx.HasTransfer = false;
            uv0Analyzed = uv0Welded = false;
            uv0Reports.Clear();
            ctx.ClearAllCaches();
            shellTransformCache.Clear();
            canvas.ClearHoverState(false);
            requestRepaint?.Invoke();
        }

        /// <summary>
        /// Deletes sidecar assets (.uv2data) and reimports FBX files to restore
        /// original UV2 state. Triggers a full Refresh + OnRefresh cycle.
        /// </summary>
        void ResetUv2FromFbx()
        {
            if (ctx.LodGroup == null) return;
            var fbxPaths = new HashSet<string>();
            foreach (var e in ctx.MeshEntries)
            {
                Mesh m = e.fbxMesh ?? e.originalMesh;
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    fbxPaths.Add(p);
            }

            foreach (string fbx in fbxPaths)
            {
                string sp = Uv2DataAsset.GetSidecarPath(fbx);
                if (AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sp) != null)
                    AssetDatabase.DeleteAsset(sp);
            }
            AssetDatabase.Refresh();
            foreach (string fbx in fbxPaths)
                AssetDatabase.ImportAsset(fbx, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();

            ctx.Refresh(ctx.LodGroup);
            OnRefresh();
            requestRepaint?.Invoke();
        }

        void ResetPipelineState()
        {
            if (ctx.LodGroup == null) return;
            if (!EditorUtility.DisplayDialog("Reset Pipeline State", "Delete all sidecars and reset?", "Reset", "Cancel")) return;

            RestoreAllPreviews();
            var fbxPaths = new HashSet<string>();
            foreach (var e in ctx.MeshEntries)
            {
                Mesh m = e.fbxMesh ?? e.originalMesh;
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    fbxPaths.Add(p);
            }
            foreach (string fbx in fbxPaths)
            {
                string sp = Uv2DataAsset.GetSidecarPath(fbx);
                if (AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sp) != null)
                    AssetDatabase.DeleteAsset(sp);
            }
            AssetDatabase.Refresh();
            foreach (string fbx in fbxPaths)
                AssetDatabase.ImportAsset(fbx, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            ctx.Refresh(ctx.LodGroup);
            OnRefresh();
            requestRepaint?.Invoke();
        }

        void RestoreFbxFromGitMain()
        {
            var fbxPaths = new HashSet<string>();
            foreach (var e in ctx.MeshEntries)
            {
                Mesh m = e.fbxMesh ?? e.originalMesh;
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    fbxPaths.Add(p);
            }

            if (fbxPaths.Count == 0)
            {
                UvtLog.Warn("[Backup] No FBX paths found in mesh entries");
                return;
            }

            foreach (string fbx in fbxPaths)
                UvToolHub.BackupFbxFromGitMain(fbx);
        }

        void SwitchToPostApplyView()
        {
            if (ctx.LodGroup != null)
            {
                ctx.Refresh(ctx.LodGroup);
            }
            else if (ctx.StandaloneMesh)
            {
                var standaloneRenderer = ctx.MeshEntries
                    .FirstOrDefault(e => e?.renderer is MeshRenderer)?.renderer as MeshRenderer;
                if (standaloneRenderer != null)
                    ctx.RefreshStandalone(standaloneRenderer);
            }
            OnRefresh();
            canvas.FillAlpha = 0.15f;
            canvas.ActiveFillModeIndex = 0; // Shells
            requestRepaint?.Invoke();
        }

        /// <summary>
        /// Restores all three preview systems (checker, shell color, lightmap)
        /// and resets their flags. Safe to call even if no preview is active.
        /// </summary>
        void RestoreAllPreviews()
        {
            // Restore checker (may be activated from tool or from UvToolHub)
            if (checkerEnabled || canvas.CheckerEnabled || CheckerTexturePreview.IsActive)
            {
                CheckerTexturePreview.Restore();
                checkerEnabled = false;
                canvas.CheckerEnabled = false;
            }
            if (shellColorPreviewEnabled || ShellColorModelPreview.IsActive)
            {
                ShellColorModelPreview.Restore();
                shellColorPreviewEnabled = false;
            }
            if (lightmapPreviewActive) RestoreLightmapPreview();
            VertexAOTool.ActiveInstance?.RestorePreview();
            canvas.CurrentPreviewMode = UvCanvasView.PreviewMode.Off;
        }

        void RestoreLightmapPreview()
        {
            foreach (var kv in lightmapBackups)
                if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
            lightmapBackups.Clear();
            lightmapPreviewActive = false;
            if (lightmapPreviewMat != null) { UnityEngine.Object.DestroyImmediate(lightmapPreviewMat); lightmapPreviewMat = null; }
        }

        // ════════════════════════════════════════════════════════════
        //  Sidecar Management
        // ════════════════════════════════════════════════════════════

        void UpdateSelectedSidecar()
        {
            selectedSidecarPath = selectedFbxPath = selectedResetLabel = null;
            var fbxPaths = new HashSet<string>();
            if (ctx?.MeshEntries != null)
                foreach (var e in ctx.MeshEntries)
                {
                    Mesh m = e.fbxMesh ?? e.originalMesh;
                    if (m == null) continue;
                    string path = AssetDatabase.GetAssetPath(m);
                    if (!string.IsNullOrEmpty(path) && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                        fbxPaths.Add(path);
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

        void TryLoadSettingsFromSidecar()
        {
            if (string.IsNullOrEmpty(selectedSidecarPath)) return;
            var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(selectedSidecarPath);
            if (data?.toolSettings == null) return;
            var s = data.toolSettings;
            ctx.AtlasResolution = s.atlasResolution;
            ctx.ShellPaddingPx = s.shellPaddingPx;
            ctx.BorderPaddingPx = s.borderPaddingPx;
            ctx.RepackPerMesh = s.repackPerMesh;
            symSplitThresholdMode = Enum.IsDefined(typeof(SymmetrySplitShells.ThresholdMode), s.symmetrySplitThresholdMode)
                ? (SymmetrySplitShells.ThresholdMode)s.symmetrySplitThresholdMode
                : SymmetrySplitShells.ThresholdMode.LegacyFixed;
            SymmetrySplitShells.CurrentThresholdMode = symSplitThresholdMode;
            ctx.SourceLodIndex = Mathf.Clamp(s.sourceLodIndex, 0, Mathf.Max(0, ctx.LodCount - 1));
            ctx.PipeSettings.saveNewMeshAssets = s.saveNewMeshAssets;
            if (!string.IsNullOrEmpty(s.savePath)) ctx.PipeSettings.savePath = s.savePath;
        }

        void SaveSettingsToSidecar()
        {
            if (string.IsNullOrEmpty(selectedSidecarPath)) return;
            var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(selectedSidecarPath);
            if (data == null) return;
            if (data.toolSettings == null) data.toolSettings = new ToolSettings();
            var s = data.toolSettings;
            s.atlasResolution = ctx.AtlasResolution;
            s.shellPaddingPx = ctx.ShellPaddingPx;
            s.borderPaddingPx = ctx.BorderPaddingPx;
            s.repackPerMesh = ctx.RepackPerMesh;
            s.symmetrySplitThresholdMode = (int)symSplitThresholdMode;
            s.sourceLodIndex = ctx.SourceLodIndex;
            s.saveNewMeshAssets = ctx.PipeSettings.saveNewMeshAssets;
            s.savePath = ctx.PipeSettings.savePath;
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
        }

        void TryRestoreShellMatchFromSidecar()
        {
            var sidecarCache = new Dictionary<string, Uv2DataAsset>();
            foreach (var e in ctx.MeshEntries)
            {
                if (e.shellTransferResult != null || e.fbxMesh == null) continue;
                string fbxPath = AssetDatabase.GetAssetPath(e.fbxMesh);
                if (string.IsNullOrEmpty(fbxPath)) continue;
                if (!sidecarCache.TryGetValue(fbxPath, out var sidecar))
                {
                    string sp = Uv2DataAsset.GetSidecarPath(fbxPath);
                    sidecar = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sp);
                    sidecarCache[fbxPath] = sidecar;
                }
                if (sidecar == null) continue;
                var entry = sidecar.Find(e.fbxMesh.name);
                if (entry?.vertexToSourceShellDescriptor == null || entry.vertexToSourceShellDescriptor.Length == 0) continue;
                var tr = new GroupedShellTransfer.TransferResult();
                tr.vertexToSourceShell = entry.vertexToSourceShellDescriptor;
                tr.targetShellToSourceShell = entry.targetShellToSourceShellDescriptor;
                tr.verticesTotal = e.fbxMesh.vertexCount;
                int transferred = 0;
                for (int i = 0; i < tr.vertexToSourceShell.Length; i++)
                    if (tr.vertexToSourceShell[i] >= 0) transferred++;
                tr.verticesTransferred = transferred;
                e.shellTransferResult = tr;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Scene GUI
        // ════════════════════════════════════════════════════════════

        public void OnSceneGUI(SceneView sv)
        {
            if (!canvas.SpotMode || sv == null)
            {
                if (canvas.HoverHitValid) canvas.ClearHoverState();
                return;
            }

            Event e = Event.current;
            if (e == null) return;

            // Raycast on MouseMove/MouseDrag, throttled to ~30fps
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - sceneSpotLastRaycastTime >= sceneSpotThrottleSec)
                {
                    sceneSpotLastRaycastTime = now;
                    var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                    bool hadHit = canvas.HoverHitValid;
                    int prevShell = canvas.HoveredShellId;

                    canvas.HoverHitValid = TryRaycastPreview(ray, out var hit);
                    if (canvas.HoverHitValid)
                    {
                        canvas.HoverWorldPos = hit.worldPos;
                        canvas.UvSpot = hit.uv;
                        canvas.HoveredShellId = hit.shellId;
                        sceneSpotCachedEntry = hit.meshEntry;
                    }
                    else
                    {
                        canvas.HoveredShellId = -1;
                        sceneSpotCachedEntry = null;
                    }

                    if (canvas.HoverHitValid != hadHit || canvas.HoveredShellId != prevShell)
                        requestRepaint?.Invoke();
                    sv.Repaint();
                }
            }
            else if (e.type == EventType.MouseLeaveWindow && canvas.HoverHitValid)
            {
                canvas.HoverHitValid = false;
                canvas.HoveredShellId = -1;
                sceneSpotCachedEntry = null;
                requestRepaint?.Invoke();
            }

            if (e.type != EventType.Repaint) return;

            // Draw selected shell overlay in 3D
            DrawSelectedShellOverlay3D();

            // Draw spot projection on all meshes
            Vector2 projUv;
            MeshEntry projEntry = null;
            bool hasProj = false;

            if (canvas.HoverHitValid)
            {
                projUv = canvas.UvSpot; projEntry = sceneSpotCachedEntry; hasProj = true;
            }
            else if (canvas.HasSelectedShell)
            {
                projUv = canvas.SelectedShell.uvHit; projEntry = canvas.SelectedShell.meshEntry; hasProj = true;
            }
            else if (canvas.HasHoveredShell)
            {
                projUv = canvas.HoveredShell.uvHit; projEntry = canvas.HoveredShell.meshEntry; hasProj = true;
            }
            else if (canvas.CanvasSpotValid)
            {
                projUv = canvas.CanvasSpotUv; hasProj = true;
            }
            else projUv = default;

            if (hasProj) DrawSpotProjectionInScene(projUv, projEntry);
        }

        MeshEntry sceneSpotCachedEntry;

        // ── 3D Spot Projection ──
        void EnsureSpotMaterials()
        {
            if (spotMat == null)
            {
                var sh = Shader.Find("Hidden/LightmapUvTool/SpotProjection");
                if (sh != null) spotMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (shellOverlayMat == null)
            {
                var sh = Shader.Find("Hidden/Internal-Colored");
                if (sh != null)
                {
                    shellOverlayMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                    shellOverlayMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    shellOverlayMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    shellOverlayMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);
                    shellOverlayMat.SetInt("_ZWrite", 0);
                    shellOverlayMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                }
            }
        }

        Material spotMat, shellOverlayMat;

        void DrawSpotProjectionInScene(Vector2 projUv, MeshEntry limitEntry = null)
        {
            EnsureSpotMaterials();
            if (spotMat == null) return;

            spotMat.SetVector("_SpotUv", new Vector4(projUv.x, projUv.y, 0f, 0f));
            spotMat.SetFloat("_SpotRadius", 0.012f);
            spotMat.SetColor("_SpotColor", new Color32(0xFF, 0xBC, 0x51, 0xFF));
            spotMat.SetFloat("_UseUv2", ctx.PreviewUvChannel == 1 ? 1f : 0f);

            foreach (var entry in ctx.ForLod(ctx.PreviewLod))
            {
                if (limitEntry != null && entry != limitEntry) continue;
                var mesh = ctx.DMesh(entry);
                if (mesh == null) continue;
                if (entry.renderer == null) continue;
                spotMat.SetPass(0);
                Graphics.DrawMeshNow(mesh, entry.renderer.localToWorldMatrix);
            }
        }

        void DrawSelectedShellOverlay3D()
        {
            EnsureSpotMaterials();
            var hit = canvas.SelectedShellDebug;
            if (hit?.shell == null || hit.entry?.renderer == null || shellOverlayMat == null) return;

            var mesh = hit.mesh ?? hit.entry.originalMesh;
            if (mesh == null) return;

            var verts = mesh.vertices;
            var tris = mesh.triangles;

            shellOverlayMat.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(hit.entry.renderer.transform.localToWorldMatrix);

            GL.Begin(GL.TRIANGLES);
            GL.Color(new Color(0.2f, 0.6f, 1f, 0.25f));
            foreach (int face in hit.shell.faceIndices)
            {
                int i0 = face * 3;
                if (i0 + 2 >= tris.Length) continue;
                GL.Vertex(verts[tris[i0]]); GL.Vertex(verts[tris[i0 + 1]]); GL.Vertex(verts[tris[i0 + 2]]);
            }
            GL.End();

            GL.Begin(GL.LINES);
            GL.Color(new Color(0.1f, 0.4f, 1f, 0.7f));
            foreach (int face in hit.shell.faceIndices)
            {
                int i0 = face * 3;
                if (i0 + 2 >= tris.Length) continue;
                var a = verts[tris[i0]]; var b = verts[tris[i0 + 1]]; var c = verts[tris[i0 + 2]];
                GL.Vertex(a); GL.Vertex(b); GL.Vertex(b); GL.Vertex(c); GL.Vertex(c); GL.Vertex(a);
            }
            GL.End();

            GL.PopMatrix();
        }

        // ── Raycast ──
        struct SceneHit
        {
            public float distance;
            public Vector3 worldPos;
            public Vector2 uv;
            public int shellId;
            public MeshEntry meshEntry;
        }

        bool TryRaycastPreview(Ray ray, out SceneHit bestHit)
        {
            bestHit = default;
            bestHit.distance = float.PositiveInfinity;
            bool found = false;

            foreach (var entry in ctx.ForLod(ctx.PreviewLod))
            {
                Mesh mesh = ctx.DMesh(entry);
                if (mesh == null || entry.renderer == null) continue;

                Matrix4x4 l2w = entry.renderer.localToWorldMatrix;
                Bounds wb = TransformBounds(mesh.bounds, l2w);
                if (!wb.IntersectRay(ray, out float aabbDist) || aabbDist > bestHit.distance) continue;

                var v = mesh.vertices;
                var tri = canvas.GetTrianglesCached(mesh);
                var uv = canvas.RdUvCached(mesh, ctx.PreviewUvChannel);
                if (v == null || tri == null || uv == null) continue;
                int[] faceToShell = ctx.UvPreviewShellCache.GetFaceToShell(mesh, ctx.PreviewUvChannel, uv, tri);

                for (int f = 0; f + 2 < tri.Length; f += 3)
                {
                    int i0 = tri[f], i1 = tri[f + 1], i2 = tri[f + 2];
                    if (i0 >= v.Length || i1 >= v.Length || i2 >= v.Length) continue;
                    if (i0 >= uv.Length || i1 >= uv.Length || i2 >= uv.Length) continue;

                    Vector3 p0 = l2w.MultiplyPoint3x4(v[i0]);
                    Vector3 p1 = l2w.MultiplyPoint3x4(v[i1]);
                    Vector3 p2 = l2w.MultiplyPoint3x4(v[i2]);

                    if (!RayTriMT(ray, p0, p1, p2, out float t, out float b1, out float b2)) continue;
                    if (t < 0f || t >= bestHit.distance) continue;

                    float b0 = 1f - b1 - b2;
                    bestHit.distance = t;
                    bestHit.worldPos = ray.origin + ray.direction * t;
                    bestHit.uv = uv[i0] * b0 + uv[i1] * b1 + uv[i2] * b2;
                    bestHit.shellId = (faceToShell != null && f / 3 < faceToShell.Length) ? faceToShell[f / 3] : -1;
                    bestHit.meshEntry = entry;
                    found = true;
                }
            }
            return found;
        }

        static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            Vector3 c = b.center, e = b.extents;
            Vector3 mn = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 mx = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int ix = -1; ix <= 1; ix += 2)
            for (int iy = -1; iy <= 1; iy += 2)
            for (int iz = -1; iz <= 1; iz += 2)
            {
                Vector3 w = m.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3(ix, iy, iz)));
                mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w);
            }
            return new Bounds((mn + mx) * 0.5f, mx - mn);
        }

        static bool RayTriMT(Ray ray, Vector3 v0, Vector3 v1, Vector3 v2, out float t, out float u, out float v)
        {
            t = u = v = 0f;
            Vector3 e1 = v1 - v0, e2 = v2 - v0;
            Vector3 p = Vector3.Cross(ray.direction, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-7f) return false;
            float inv = 1f / det;
            Vector3 s = ray.origin - v0;
            u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            v = Vector3.Dot(ray.direction, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, q) * inv;
            return true;
        }

        // ── Focus SceneView on double-clicked shell ──
        void FocusSceneViewOnSpot(ShellUvHit uvHit)
        {
            if (uvHit.meshEntry?.renderer == null) return;
            var mesh = ctx.DMesh(uvHit.meshEntry);
            if (mesh == null) return;

            var verts = mesh.vertices;
            var tris = mesh.triangles;
            var renderer = uvHit.meshEntry.renderer;
            var tr = renderer.transform;
            var rendererBounds = renderer.bounds;

            Vector3 worldPos = rendererBounds.center;
            Vector3 faceNormal = tr.up.sqrMagnitude > 0.001f ? tr.up : Vector3.up;
            float idealDist = Mathf.Max(rendererBounds.extents.magnitude * 1.5f, 0.3f);

            // Shell bbox for ideal camera distance
            var cache = canvas.GetPreviewShellCache(ctx, mesh, ctx.PreviewUvChannel);
            if (cache?.shellById != null && cache.shellById.TryGetValue(uvHit.shellId, out var shell))
            {
                bool first = true;
                var sb = new Bounds();
                foreach (int face in shell.faceIndices)
                {
                    int fi = face * 3;
                    if (fi + 2 >= tris.Length) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = tris[fi + k];
                        if (vi >= verts.Length) continue;
                        var wp = tr.TransformPoint(verts[vi]);
                        if (first) { sb = new Bounds(wp, Vector3.zero); first = false; }
                        else sb.Encapsulate(wp);
                    }
                }
                if (!first)
                {
                    worldPos = sb.center;
                    idealDist = Mathf.Max(sb.extents.magnitude * 1.5f, 0.3f);
                }
            }

            if (uvHit.faceIndex >= 0)
            {
                int i0 = uvHit.faceIndex * 3;
                if (i0 + 2 < tris.Length)
                {
                    int vi0 = tris[i0], vi1 = tris[i0 + 1], vi2 = tris[i0 + 2];
                    if (vi0 >= 0 && vi1 >= 0 && vi2 >= 0 &&
                        vi0 < verts.Length && vi1 < verts.Length && vi2 < verts.Length)
                    {
                        var bary = uvHit.barycentric;
                        var localPos = verts[vi0] * bary.x + verts[vi1] * bary.y + verts[vi2] * bary.z;
                        worldPos = tr.TransformPoint(localPos);

                        var localEdge1 = verts[vi1] - verts[vi0];
                        var localEdge2 = verts[vi2] - verts[vi0];
                        var triNormal = Vector3.Cross(localEdge1, localEdge2);
                        if (triNormal.sqrMagnitude > 1e-8f)
                        {
                            faceNormal = tr.TransformDirection(triNormal.normalized).normalized;
                            if (faceNormal.sqrMagnitude < 0.5f)
                                faceNormal = tr.up.sqrMagnitude > 0.001f ? tr.up : Vector3.up;
                        }
                    }
                }
            }

            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
            sv.pivot = worldPos;
            sv.size = idealDist;
            sv.rotation = Quaternion.LookRotation(-faceNormal);
            sv.Repaint();
        }

        // ════════════════════════════════════════════════════════════
        //  Canvas Overlay & Status Bar
        // ════════════════════════════════════════════════════════════

        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz)
        {
            // Shell debug overlay is handled by canvas.DrawShellDebugOverlay
        }

        public void OnDrawToolbarExtra()
        {
            // No extra toolbar items for this tool
        }

        public void OnDrawStatusBar()
        {
            int fillIdx = canvas.ActiveFillModeIndex;
            if (canvas.FillModes.Count > fillIdx && fillIdx >= 0)
            {
                string mode = canvas.FillModes[fillIdx].name;
                if (mode == "Validation")
                {
                    Sw("\u2713", UvCanvasView.cValClean); Sw("Str", UvCanvasView.cValStretch);
                    Sw("0A", UvCanvasView.cValZero); Sw("OB", UvCanvasView.cValOOB);
                    Sw("Txl", UvCanvasView.cValTexel); Sw("Ov", UvCanvasView.cValOverlap);
                }
                else if (mode == "Status")
                {
                    Sw("Ok", UvCanvasView.cAccept); Sw("Am", UvCanvasView.cAmbig);
                    Sw("Mi", UvCanvasView.cMis); Sw("Rj", UvCanvasView.cReject);
                }
                else if (mode == "Shell Match")
                {
                    EditorGUILayout.LabelField("ShellMatch", EditorStyles.miniLabel, GUILayout.Width(70));
                }
            }
        }

        // ════════════════════════════════════════════════════════════
        //  UI Helpers
        // ════════════════════════════════════════════════════════════

        static void H(string t) { EditorGUILayout.Space(2); EditorGUILayout.LabelField(t, EditorStyles.boldLabel); }
        static void Warn(string t) { EditorGUILayout.HelpBox(t, MessageType.Warning); }

        void ColorBtn(Color col, string l, int h, Action a)
        {
            var b = GUI.backgroundColor; GUI.backgroundColor = col;
            if (GUILayout.Button(l, GUILayout.Height(h))) a();
            GUI.backgroundColor = b;
        }

        void Bar(string label, int n, int total, Color col)
        {
            float pct = total > 0 ? (float)n / total : 0;
            var r = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(.15f,.15f,.15f));
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width * pct, r.height), col);
            var s = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft };
            s.normal.textColor = Color.white;
            EditorGUI.LabelField(new Rect(r.x+4, r.y, r.width, r.height), label + ": " + n + " (" + (pct*100).ToString("F0") + "%)", s);
        }

        void Sw(string l, Color c)
        {
            var r = GUILayoutUtility.GetRect(30, 16, GUILayout.Width(30));
            EditorGUI.DrawRect(new Rect(r.x, r.y+2, 10, 12), c);
            var style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(.85f,.85f,.85f) } };
            GUI.Label(new Rect(r.x+12, r.y, 18, 16), l, style);
        }
    }
}
