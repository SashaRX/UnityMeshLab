// LightmapTransferTool.cs — UV2 Lightmap Transfer tool.
// Migrated from UvTransferWindow: Setup → Repack → Transfer → Apply pipeline.
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
        bool foldProjection = true, foldBorderRepair, foldOutput = true;
        bool foldUv0Analysis, foldRepackSettings = true;
        bool splitTargetsInSymmetryStep;
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

        // ── Transfer cache ──
        Dictionary<int, GroupedShellTransfer.SourceShellInfo[]> shellTransformCache =
            new Dictionary<int, GroupedShellTransfer.SourceShellInfo[]>();
        List<GroupedShellTransfer.OverlapSourceHint> accumulatedOverlapHints =
            new List<GroupedShellTransfer.OverlapSourceHint>();

        // ── Preview ──
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

            if (ctx.LodGroup == null) { EditorGUILayout.HelpBox("Assign LODGroup or select a GameObject.", MessageType.Info); return; }

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
                    EditorGUILayout.LabelField("V:" + m.vertexCount + " T:" + (m.triangles.Length / 3), EditorStyles.miniLabel, GUILayout.Width(80));
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
            ColorBtn(new Color(.2f,.75f,.95f), "Run Full Pipeline", 30, ExecFullPipeline);
            splitTargetsInSymmetryStep = EditorGUILayout.ToggleLeft("SymSplit target LODs (advanced)", splitTargetsInSymmetryStep);

            EditorGUILayout.Space(6);
            H("Pipeline Settings");
            ctx.PipeSettings.sourceUvChannel = EditorGUILayout.IntPopup("Source UV", ctx.PipeSettings.sourceUvChannel, new[]{"UV0","UV2"}, new[]{0,1});
            ctx.PipeSettings.targetUvChannel = EditorGUILayout.IntPopup("Target UV", ctx.PipeSettings.targetUvChannel, new[]{"UV0","UV2"}, new[]{0,1});

            foldProjection = EditorGUILayout.Foldout(foldProjection, "Projection", true);
            if (foldProjection)
            {
                EditorGUI.indentLevel++;
                ctx.PipeSettings.maxProjectionDistance = EditorGUILayout.FloatField("Max Dist", ctx.PipeSettings.maxProjectionDistance);
                ctx.PipeSettings.maxNormalAngle = EditorGUILayout.Slider("Normal Angle", ctx.PipeSettings.maxNormalAngle, 10, 180);
                ctx.PipeSettings.filterBySubmesh = EditorGUILayout.Toggle("Submesh Filter", ctx.PipeSettings.filterBySubmesh);
                EditorGUI.indentLevel--;
            }

            foldBorderRepair = EditorGUILayout.Foldout(foldBorderRepair, "Border Repair", true);
            if (foldBorderRepair)
            {
                EditorGUI.indentLevel++;
                ctx.PipeSettings.enableBorderRepair = EditorGUILayout.Toggle("Enable", ctx.PipeSettings.enableBorderRepair);
                if (ctx.PipeSettings.enableBorderRepair)
                {
                    ctx.PipeSettings.perimeterTolerance = EditorGUILayout.FloatField("Perim Tol", ctx.PipeSettings.perimeterTolerance);
                    ctx.PipeSettings.borderFuseTolerance = EditorGUILayout.FloatField("Fuse Tol", ctx.PipeSettings.borderFuseTolerance);
                }
                EditorGUI.indentLevel--;
            }

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

                // ── Save / Export after Weld (before Repack) ──
                bool anyWelded = ctx.MeshEntries.Any(e => e.wasWelded || e.wasEdgeWelded || e.wasSymmetrySplit);
                if (anyWelded)
                {
                    EditorGUILayout.Space(6);
                    H("Save / Export (post-weld)");
                    ColorBtn(new Color(.3f,.85f,.4f), "Apply UV2 to FBX", 24, ApplyUv2ToFbx);
                    EditorGUILayout.Space(2);
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                    ColorBtn(new Color(.4f,.7f,.95f), "Export as New FBX", 22, () => ExportFbx(false));
                    EditorGUILayout.Space(2);
                    ColorBtn(new Color(.95f,.6f,.2f), "Overwrite Source FBX", 22, () => ExportFbx(true));
#else
                    EditorGUILayout.HelpBox("Install com.unity.formats.fbx for FBX export.", MessageType.Info);
#endif
                    EditorGUILayout.Space(2);
                    ColorBtn(new Color(.6f,.5f,.3f), "Save All Mesh Assets", 22, SaveAll);
                    if (GUILayout.Button("Update LODGroup Refs", EditorStyles.miniButton)) UpdateRefs();
                }
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
                    if (!ee.Any(e => e.shellTransferResult != null || e.report.HasValue)) continue;
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

        void ExecSymmetrySplit(bool includeTargets)
        {
            if (ctx.LodGroup == null) return;
            lastSymmetrySplitLods.Clear();
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include) continue;
                if (!includeTargets && e.lodIndex != ctx.SourceLodIndex) continue;
                if (e.originalMesh == e.fbxMesh)
                {
                    e.originalMesh = UvCanvasView.MakeReadableCopy(e.fbxMesh);
                    e.originalMesh.name = e.fbxMesh.name + "_wc";
                }
                var uv0 = e.originalMesh.uv;
                if (uv0 == null || uv0.Length == 0) continue;
                var shells = UvShellExtractor.Extract(uv0, e.originalMesh.triangles);
                int split = SymmetrySplitShells.Split(e.originalMesh, shells);
                if (split > 0) { e.wasSymmetrySplit = true; lastSymmetrySplitLods.Add(e.lodIndex); UvtLog.Info($"[SymSplit] '{e.originalMesh.name}' LOD{e.lodIndex}: {split} shells split"); }
            }
            ctx.ClearAllCaches();
            requestRepaint?.Invoke();
        }

        void ExecFullPipeline()
        {
            if (ctx.LodGroup == null) return;
            UvtLog.Info("[Pipeline] Starting full pipeline...");

            // 1. Analyze
            ExecAnalyzeUv0();

            // 2. Weld
            ExecWeldUv0();

            // 3. SymSplit
            ExecSymmetrySplit(splitTargetsInSymmetryStep);

            // 4. Repack
            var src = ctx.ForLod(ctx.SourceLodIndex);
            if (ctx.RepackPerMesh) ExecRepackPerMesh(src);
            else ExecRepack(src);

            // 5. Transfer
            if (ctx.HasRepack) ExecTransferAll();

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
                    accumulatedOverlapHints.Count > 0 ? accumulatedOverlapHints : null);
                if (tr.uv2 == null) { UvtLog.Warn($"[Transfer] Failed for '{tgt.renderer.name}'"); continue; }

                // Accumulate overlap hints for subsequent LODs
                if (tr.overlapHints != null && tr.overlapHints.Count > 0)
                    accumulatedOverlapHints.AddRange(tr.overlapHints);

                // Border repair (modifies tr.uv2 in-place)
                tgt.borderRepairReport = null;
                if (ctx.PipeSettings.enableBorderRepair)
                {
                    var brSettings = new BorderRepairAdapter.Settings
                    {
                        perimeterTolerance = ctx.PipeSettings.perimeterTolerance,
                        borderFuseTolerance = ctx.PipeSettings.borderFuseTolerance,
                        maxNormalAngle = ctx.PipeSettings.maxNormalAngle
                    };
                    tgt.borderRepairReport = BorderRepairAdapter.Repair(tgtMesh, srcMesh, tr.uv2, brSettings);
                }

                // Build output mesh with UV2 applied
                var om = UnityEngine.Object.Instantiate(tgtMesh);
                om.name = tgtMesh.name + "_uvTransfer";
                om.SetUVs(ctx.PipeSettings.targetUvChannel, new List<Vector2>(tr.uv2));
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
            if (ctx.LodGroup == null) return;
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

                var uv2List = new List<Vector2>();
                resultMesh.GetUVs(ctx.PipeSettings.targetUvChannel, uv2List);
                if (uv2List.Count == 0) continue;

                var positions = resultMesh.vertices;
                var uv0List = new List<Vector2>();
                (e.originalMesh ?? resultMesh).GetUVs(0, uv0List);

                if (!fbxGroups.ContainsKey(fbxPath))
                    fbxGroups[fbxPath] = new List<MeshUv2Entry>();

                string meshName = e.fbxMesh != null ? e.fbxMesh.name : e.originalMesh.name;
                MeshFingerprint fp = e.fbxMesh != null ? MeshFingerprint.Compute(e.fbxMesh) : null;

                fbxGroups[fbxPath].Add(new MeshUv2Entry
                {
                    meshName = meshName,
                    uv2 = uv2List.ToArray(),
                    welded = e.wasWelded,
                    edgeWelded = e.wasEdgeWelded,
                    vertPositions = positions,
                    vertUv0 = uv0List.ToArray(),
                    schemaVersion = Uv2DataAsset.CurrentSchemaVersion,
                    toolVersion = Uv2DataAsset.ToolVersionStr,
                    sourceFingerprint = fp,
                    targetUvChannel = ctx.PipeSettings.targetUvChannel,
                    stepMeshopt = e.wasWelded,
                    stepEdgeWeld = e.wasEdgeWelded,
                    stepSymmetrySplit = e.wasSymmetrySplit,
                    stepRepack = (e.lodIndex == ctx.SourceLodIndex),
                    stepTransfer = (e.lodIndex != ctx.SourceLodIndex),
                });
            }

            if (fbxGroups.Count == 0) { UvtLog.Warn("[Apply] No meshes with UV2 data."); return; }

            // Save sidecar assets
            foreach (var kv in fbxGroups)
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

                // Reimport FBX so the postprocessor replays UV2
                AssetDatabase.ImportAsset(kv.Key);
            }

            UvtLog.Info($"[Apply] Done — {fbxGroups.Count} FBX(es) updated.");
            SwitchToPostApplyView();
            SaveSettingsToSidecar();
        }

        Mesh GetResultMesh(MeshEntry e)
        {
            Mesh m = e.lodIndex == ctx.SourceLodIndex ? e.repackedMesh : e.transferredMesh;
            if (m != null) return m;
            if (e.wasWelded || e.wasEdgeWelded || e.wasSymmetrySplit)
                return e.originalMesh;
            return null;
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
                // Skip if export mesh already has this channel
                var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
                if (exportMesh.HasVertexAttribute(attr)) continue;
                if (!sourceMesh.HasVertexAttribute(attr)) continue;

                var uv = new List<Vector2>();
                sourceMesh.GetUVs(ch, uv);
                if (uv.Count == 0) continue;

                // Skip trivial data: all zeros or all ones
                bool allZero = true, allOne = true;
                for (int i = 0; i < uv.Count; i++)
                {
                    var v = uv[i];
                    if (v.x != 0f || v.y != 0f) allZero = false;
                    if (v.x != 1f || v.y != 1f) allOne = false;
                    if (!allZero && !allOne) break;
                }
                if (allZero || allOne) continue;

                exportMesh.SetUVs(ch, uv);
            }
        }

        public void ExportFbxPublic(bool overwriteSource) => ExportFbx(overwriteSource);

        void ExportFbx(bool overwriteSource)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (ctx.LodGroup == null) { UvtLog.Error("[FBX Export] No LODGroup loaded."); return; }

            var fbxGroups = new Dictionary<string, List<(MeshEntry entry, Mesh resultMesh)>>();
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include) continue;
                Mesh resultMesh = GetResultMesh(e);
                if (resultMesh == null) continue;
                Mesh pathMesh = e.fbxMesh ?? e.originalMesh;
                string fbxPath = pathMesh != null ? AssetDatabase.GetAssetPath(pathMesh) : null;
                if (string.IsNullOrEmpty(fbxPath)) continue;
                if (!fbxGroups.ContainsKey(fbxPath))
                    fbxGroups[fbxPath] = new List<(MeshEntry, Mesh)>();
                fbxGroups[fbxPath].Add((e, resultMesh));
            }
            if (fbxGroups.Count == 0) { UvtLog.Error("[FBX Export] No processed meshes to export."); return; }

            foreach (var kv in fbxGroups)
            {
                string sourceFbxPath = kv.Key;
                var entries = kv.Value;
                string exportPath;
                if (overwriteSource)
                {
                    if (!EditorUtility.DisplayDialog("Overwrite Source FBX",
                        "This will overwrite:\n" + sourceFbxPath + "\n\nA backup (.fbx.bak) will be created. Continue?",
                        "Overwrite", "Cancel")) continue;
                    exportPath = sourceFbxPath;
                    string fullSource = System.IO.Path.GetFullPath(sourceFbxPath);
                    string fullMeta = fullSource + ".meta";
                    try
                    {
                        System.IO.File.Copy(fullSource, fullSource + ".bak", true);
                        if (System.IO.File.Exists(fullMeta))
                            System.IO.File.Copy(fullMeta, fullSource + ".meta.bak", true);
                    }
                    catch (Exception ex) { UvtLog.Error("[FBX Export] Backup failed: " + ex.Message); continue; }
                }
                else
                {
                    string dir = System.IO.Path.GetDirectoryName(sourceFbxPath);
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(sourceFbxPath);
                    exportPath = EditorUtility.SaveFilePanel("Export FBX", dir, baseName + "_uv2.fbx", "fbx");
                    if (string.IsNullOrEmpty(exportPath)) continue;
                    string dataPath = Application.dataPath;
                    if (exportPath.StartsWith(dataPath))
                        exportPath = "Assets" + exportPath.Substring(dataPath.Length);
                }

                // Clone original FBX hierarchy and replace only the meshes
                var fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourceFbxPath);
                if (fbxPrefab == null) { UvtLog.Error("[FBX Export] Cannot load FBX prefab: " + sourceFbxPath); continue; }
                var tempRoot = UnityEngine.Object.Instantiate(fbxPrefab);
                tempRoot.name = fbxPrefab.name;
                try
                {
                    // Build lookup: original mesh name -> export mesh
                    var meshReplacements = new Dictionary<string, Mesh>();
                    foreach (var (entry, resultMesh) in entries)
                    {
                        var exportMesh = UnityEngine.Object.Instantiate(resultMesh);
                        Mesh srcUvMesh = entry.fbxMesh ?? entry.originalMesh;
                        if (srcUvMesh != null)
                            PreserveUvChannels(exportMesh, srcUvMesh);
                        string meshName = entry.fbxMesh != null ? entry.fbxMesh.name : resultMesh.name;
                        meshReplacements[meshName] = exportMesh;
                    }

                    // Replace meshes in cloned hierarchy
                    foreach (var mf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.sharedMesh != null && meshReplacements.TryGetValue(mf.sharedMesh.name, out var replacement))
                            mf.sharedMesh = replacement;
                    }
                    foreach (var smr in tempRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (smr.sharedMesh != null && meshReplacements.TryGetValue(smr.sharedMesh.name, out var replacement))
                            smr.sharedMesh = replacement;
                    }

                    var exportOptions = new ExportModelOptions { ExportFormat = ExportFormat.Binary };
                    ModelExporter.ExportObjects(exportPath, new UnityEngine.Object[] { tempRoot }, exportOptions);
                    UvtLog.Info("[FBX Export] Exported (binary) " + entries.Count + " mesh(es) -> " + exportPath);

                    // Restore original .meta and clean up .bak files
                    if (overwriteSource)
                    {
                        string fullPath = System.IO.Path.GetFullPath(sourceFbxPath);
                        string metaBak = fullPath + ".meta.bak";
                        if (System.IO.File.Exists(metaBak))
                        {
                            System.IO.File.Copy(metaBak, fullPath + ".meta", true);
                            System.IO.File.Delete(metaBak);
                        }
                        string fbxBak = fullPath + ".bak";
                        if (System.IO.File.Exists(fbxBak))
                            System.IO.File.Delete(fbxBak);
                        // Delete auto-created .bak.meta files
                        string fbxBakMeta = fbxBak + ".meta";
                        if (System.IO.File.Exists(fbxBakMeta))
                            System.IO.File.Delete(fbxBakMeta);
                        string metaBakMeta = metaBak + ".meta";
                        if (System.IO.File.Exists(metaBakMeta))
                            System.IO.File.Delete(metaBakMeta);
                    }
                }
                catch (Exception ex) { UvtLog.Error("[FBX Export] Export failed: " + ex); }
                finally { UnityEngine.Object.DestroyImmediate(tempRoot); }
            }
            AssetDatabase.Refresh();
#endif
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
                        uvChannel    = ctx.PipeSettings.targetUvChannel
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

            ctx.Refresh(ctx.LodGroup);
            OnRefresh();
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
                e.borderRepairReport = null;
                e.wasWelded = e.wasEdgeWelded = e.wasSymmetrySplit = false;
                e.report = null;
            }
            ctx.HasRepack = ctx.HasTransfer = false;
            uv0Analyzed = uv0Welded = false;
            uv0Reports.Clear();
            ctx.ClearAllCaches();
            shellTransformCache.Clear();
            canvas.ClearHoverState(false);
            requestRepaint?.Invoke();
        }

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

        void SwitchToPostApplyView()
        {
            ctx.Refresh(ctx.LodGroup);
            OnRefresh();
            canvas.FillAlpha = 0.15f;
            canvas.ActiveFillModeIndex = 0; // Shells
            requestRepaint?.Invoke();
        }

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
            ctx.SourceLodIndex = Mathf.Clamp(s.sourceLodIndex, 0, Mathf.Max(0, ctx.LodCount - 1));
            ctx.PipeSettings.sourceUvChannel = s.sourceUvChannel;
            ctx.PipeSettings.targetUvChannel = s.targetUvChannel;
            ctx.PipeSettings.maxProjectionDistance = s.maxProjectionDistance;
            ctx.PipeSettings.maxNormalAngle = s.maxNormalAngle;
            ctx.PipeSettings.filterBySubmesh = s.filterBySubmesh;
            ctx.PipeSettings.enableBorderRepair = s.enableBorderRepair;
            ctx.PipeSettings.perimeterTolerance = s.perimeterTolerance;
            ctx.PipeSettings.borderFuseTolerance = s.borderFuseTolerance;
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
            s.sourceLodIndex = ctx.SourceLodIndex;
            s.sourceUvChannel = ctx.PipeSettings.sourceUvChannel;
            s.targetUvChannel = ctx.PipeSettings.targetUvChannel;
            s.maxProjectionDistance = ctx.PipeSettings.maxProjectionDistance;
            s.maxNormalAngle = ctx.PipeSettings.maxNormalAngle;
            s.filterBySubmesh = ctx.PipeSettings.filterBySubmesh;
            s.enableBorderRepair = ctx.PipeSettings.enableBorderRepair;
            s.perimeterTolerance = ctx.PipeSettings.perimeterTolerance;
            s.borderFuseTolerance = ctx.PipeSettings.borderFuseTolerance;
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
