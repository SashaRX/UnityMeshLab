// LightmapTransferTool.cs — UV2 Lightmap Transfer tool.
// Migrated from UvTransferWindow: Setup → Repack → Transfer → Apply pipeline.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
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
                int welded = Uv0Analyzer.WeldFalseSeams(e.originalMesh, e.originalMesh);
                if (welded > 0) { e.wasWelded = true; UvtLog.Info($"[Weld] '{e.originalMesh.name}' LOD{e.lodIndex}: welded {welded} verts"); }
            }

            // Source-guided edge weld for target LODs
            var srcEntries = ctx.ForLod(ctx.SourceLodIndex);
            if (srcEntries.Count > 0)
            {
                foreach (var e in ctx.MeshEntries)
                {
                    if (!e.include || e.lodIndex == ctx.SourceLodIndex) continue;
                    var srcEntry = srcEntries.FirstOrDefault(s => (s.meshGroupKey ?? s.renderer.name) == (e.meshGroupKey ?? e.renderer.name));
                    if (srcEntry == null) srcEntry = srcEntries[0];
                    Mesh srcMesh = srcEntry.originalMesh;
                    if (srcMesh == null) continue;
                    int edgeWelded = Uv0Analyzer.SourceGuidedEdgeWeld(e.originalMesh, srcMesh);
                    if (edgeWelded > 0) { e.wasEdgeWelded = true; UvtLog.Info($"[EdgeWeld] '{e.originalMesh.name}' LOD{e.lodIndex}: {edgeWelded} edges"); }
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
                int split = SymmetrySplitShells.Split(e.originalMesh);
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
            var meshes = new List<Mesh>();
            foreach (var e in entries)
            {
                if (e.originalMesh == e.fbxMesh)
                {
                    e.originalMesh = UvCanvasView.MakeReadableCopy(e.fbxMesh);
                    e.originalMesh.name = e.fbxMesh.name + "_wc";
                }
                meshes.Add(e.originalMesh);
            }

            var result = XatlasRepack.Repack(meshes, ctx.AtlasResolution, ctx.ShellPaddingPx, ctx.BorderPaddingPx);
            if (result == null || result.Count != entries.Count) { UvtLog.Warn("[Repack] Failed"); return; }
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].repackedMesh = result[i];
                entries[i].repackedMesh.name = entries[i].originalMesh.name + "_repack";
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
                var srcEntry = sources.FirstOrDefault(s => (s.meshGroupKey ?? s.renderer.name) == (tgt.meshGroupKey ?? tgt.renderer.name));
                if (srcEntry == null) srcEntry = sources[0];

                Mesh srcMesh = srcEntry.repackedMesh ?? srcEntry.originalMesh;
                int srcId = srcMesh.GetInstanceID();

                if (!shellTransformCache.TryGetValue(srcId, out var srcInfos))
                {
                    srcInfos = GroupedShellTransfer.BuildSourceInfo(srcMesh, ctx.PipeSettings.sourceUvChannel);
                    if (srcInfos != null) shellTransformCache[srcId] = srcInfos;
                }

                var transferResult = GroupedShellTransfer.Transfer(
                    srcMesh, tgt.originalMesh,
                    srcInfos,
                    ctx.PipeSettings.sourceUvChannel,
                    ctx.PipeSettings.targetUvChannel,
                    ctx.PipeSettings.maxProjectionDistance,
                    ctx.PipeSettings.maxNormalAngle,
                    ctx.PipeSettings.filterBySubmesh,
                    srcEntry.renderer?.transform,
                    tgt.renderer?.transform);

                if (transferResult == null) { UvtLog.Warn($"[Transfer] Failed for '{tgt.renderer.name}'"); continue; }

                tgt.transferredMesh = transferResult.resultMesh;
                tgt.transferredMesh.name = tgt.originalMesh.name + "_transferred";
                tgt.shellTransferResult = transferResult;

                // Validation
                tgt.validationReport = TransferValidator.Validate(tgt.transferredMesh, ctx.PipeSettings.targetUvChannel, ctx.AtlasResolution);

                // Border repair
                if (ctx.PipeSettings.enableBorderRepair)
                {
                    tgt.borderRepairReport = BorderRepairAdapter.Repair(
                        tgt.transferredMesh, srcMesh,
                        ctx.PipeSettings.targetUvChannel,
                        ctx.PipeSettings.perimeterTolerance,
                        ctx.PipeSettings.borderFuseTolerance);
                }

                float pct = transferResult.verticesTotal > 0 ? transferResult.verticesTransferred * 100f / transferResult.verticesTotal : 0;
                UvtLog.Info($"[Transfer] '{tgt.renderer.name}' LOD{tLod}: {transferResult.shellsMatched} shells, {pct:F0}% coverage");
            }
        }

        void ApplyUv2ToFbx()
        {
            if (ctx.LodGroup == null) return;
            UvtLog.Info("[Apply] Applying UV2 to FBX...");
            UvTransferPipeline.ApplyUv2(ctx.MeshEntries, ctx.SourceLodIndex, ctx.PipeSettings,
                ctx.AtlasResolution, ctx.ShellPaddingPx, ctx.BorderPaddingPx, ctx.RepackPerMesh);
            SwitchToPostApplyView();
            SaveSettingsToSidecar();
        }

        void ExportFbx(bool overwriteSource)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            UvTransferPipeline.ExportFbx(ctx.MeshEntries, ctx.SourceLodIndex, overwriteSource);
#endif
        }

        void GenerateLods()
        {
            if (ctx.LodGroup == null) return;
            UvTransferPipeline.GenerateLods(ctx.LodGroup, ctx.ForLod(ctx.SourceLodIndex),
                generateLodCount, generateLodRatios, generateTargetError,
                generateUv2Weight, generateNormalWeight, generateLockBorder, generateAddToLodGroup);
            ctx.Refresh(ctx.LodGroup);
            OnRefresh();
            requestRepaint?.Invoke();
        }

        void SaveAll()
        {
            UvTransferPipeline.SaveMeshAssets(ctx.MeshEntries, ctx.PipeSettings.savePath);
        }

        void UpdateRefs()
        {
            UvTransferPipeline.UpdateLodGroupRefs(ctx.LodGroup, ctx.MeshEntries, ctx.SourceLodIndex);
        }

        // ════════════════════════════════════════════════════════════
        //  Reset Methods
        // ════════════════════════════════════════════════════════════

        void ResetWorkingCopies()
        {
            RestoreAllPreviews();
            foreach (var e in ctx.MeshEntries)
            {
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
            if (checkerEnabled) { CheckerTexturePreview.Restore(); checkerEnabled = false; }
            if (shellColorPreviewEnabled) { ShellColorModelPreview.Restore(); shellColorPreviewEnabled = false; }
            if (lightmapPreviewActive) RestoreLightmapPreview();
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
            // Scene raycast for spot mode delegated to UvTransferWindow (kept for now)
            // Full implementation requires porting the raycast + shell overlay logic
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
