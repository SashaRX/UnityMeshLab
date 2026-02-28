// UvTransferWindow.cs — Main Editor Window for Lightmap UV Transfer Tool
// Workflow: Setup → Repack (xatlas) → Transfer (shell-aware) → Review
// Supports multiple meshes per LOD (shared atlas), GL UV preview,
// transfer status visualization, selective stage re-run.
// Menu: Tools → Lightmap UV Tool
//
// Layout:
//   Left sidebar: LODGroup picker, mesh list, parameters, actions
//   Center: UV canvas (all meshes in atlas, color-coded by shell or status)
//   Bottom: Status bar + legend

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    public class UvTransferWindow : EditorWindow
    {
        // ════════════════════════════════════════════════════════════
        //  State
        // ════════════════════════════════════════════════════════════

        LODGroup lodGroup;
        int sourceLodIndex;
        List<MeshEntry> meshEntries = new List<MeshEntry>();

        UvTransferPipeline.PipelineSettings pipeSettings =
            UvTransferPipeline.PipelineSettings.Default;

        int atlasResolution = 1024;
        int shellPaddingPx  = 2;
        int borderPaddingPx = 0;
        bool rotateCharts   = true;

        // UV0 analysis
        Dictionary<int, Uv0Report> uv0Reports = new Dictionary<int, Uv0Report>();
        bool uv0Analyzed = false;
        bool uv0Welded = false;

        // Source analysis cache (mesh instanceID → data)
        Dictionary<int, SourceMeshData> srcCache = new Dictionary<int, SourceMeshData>();

        // UI
        enum Tab { Setup, Repack, Transfer, Review }
        Tab tab = Tab.Setup;
        bool hasRepack, hasTransfer;

        // Canvas
        Vector2 canvasScroll;
        float zoom = 1f;
        int  pvChannel = 2;
        int  pvLod     = 0;
        bool showFill = true, showWire = true, showBorder, showStatus;
        float fillAlpha = 0.25f;

        // Sidebar
        Vector2 sideScroll, reportScroll;
        float sideW = 320f;
        bool sideDragging;

        Material glMat;

        // ─── Mesh Entry ───
        class MeshEntry
        {
            public int lodIndex;
            public Renderer renderer;
            public MeshFilter meshFilter;
            public Mesh originalMesh;     // working copy — may be replaced by weld
            public Mesh fbxMesh;          // always the FBX asset mesh (for path lookup)
            public bool include = true;
            public bool wasWelded;        // true if originalMesh was replaced by weld
            public Mesh repackedMesh;
            public Mesh transferredMesh;
            public TargetTransferState transferState;
            public TransferQualityEvaluator.TransferReport? report;
            public GroupedShellTransfer.TransferResult shellTransferResult;
        }

        // ════════════════════════════════════════════════════════════
        //  Constants & Palette
        // ════════════════════════════════════════════════════════════

        const float UV_LO = -0.5f, UV_HI = 1.5f;
        const int BATCH = 800, MAX_TRI = 12000;

        static readonly Color[] pal = {
            new Color(.20f,.60f,1f),  new Color(1f,.40f,.20f),
            new Color(.30f,.85f,.40f),new Color(.90f,.25f,.60f),
            new Color(.95f,.85f,.20f),new Color(.55f,.30f,.90f),
            new Color(0f,.80f,.80f),  new Color(.85f,.55f,.20f),
            new Color(.60f,.90f,.20f),new Color(.90f,.20f,.20f),
            new Color(.40f,.40f,.90f),new Color(.90f,.70f,.40f),
        };

        static readonly Color cAccept = new Color(.2f,.85f,.3f,.5f);
        static readonly Color cAmbig  = new Color(.95f,.85f,.2f,.5f);
        static readonly Color cBorder = new Color(1f,.5f,.1f,.5f);
        static readonly Color cMis    = new Color(.9f,.15f,.15f,.5f);
        static readonly Color cReject = new Color(.4f,.4f,.4f,.5f);
        static readonly Color cNone   = new Color(.3f,.3f,.3f,.3f);

        // ════════════════════════════════════════════════════════════
        //  Lifecycle
        // ════════════════════════════════════════════════════════════

        [MenuItem("Tools/Lightmap UV Tool")]
        static void Open()
        {
            var w = GetWindow<UvTransferWindow>("Lightmap UV Tool");
            w.minSize = new Vector2(800, 500);
        }

        void OnEnable()
        {
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return;
            glMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            glMat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
            glMat.SetInt("_ZWrite",   0);
        }

        void OnDisable()
        {
            if (CheckerTexturePreview.IsActive) CheckerTexturePreview.Restore();
            if (glMat) DestroyImmediate(glMat);
            glMat = null;
        }

        void OnSelectionChange()
        {
            var go = Selection.activeGameObject;
            if (go != null)
            {
                var lg = go.GetComponentInParent<LODGroup>();
                if (lg != null && lg != lodGroup) { lodGroup = lg; Refresh(); }
            }
            Repaint();
        }

        // ════════════════════════════════════════════════════════════
        //  Mesh Collection
        // ════════════════════════════════════════════════════════════

        void Refresh()
        {
            meshEntries.Clear();
            hasRepack = hasTransfer = false;
            srcCache.Clear();
            shellTransformCache.Clear();
            uv0Reports.Clear();
            uv0Analyzed = false;
            uv0Welded = false;
            if (lodGroup == null) return;
            var lods = lodGroup.GetLODs();
            for (int li = 0; li < lods.Length; li++)
            {
                if (lods[li].renderers == null) continue;
                foreach (var r in lods[li].renderers)
                {
                    if (r == null) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    meshEntries.Add(new MeshEntry {
                        lodIndex = li, renderer = r, meshFilter = mf,
                        originalMesh = mf.sharedMesh,
                        fbxMesh = mf.sharedMesh });
                }
            }
        }

        List<MeshEntry> ForLod(int li) => meshEntries.Where(e => e.lodIndex == li && e.include).ToList();
        int LodN => lodGroup != null ? lodGroup.GetLODs().Length : 0;

        // ════════════════════════════════════════════════════════════
        //  OnGUI
        // ════════════════════════════════════════════════════════════

        void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.Width(sideW));
            sideScroll = EditorGUILayout.BeginScrollView(sideScroll);
            DrawSidebar();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            DrawResizeHandle();

            EditorGUILayout.BeginVertical();
            DrawToolbar();
            DrawCanvas();
            DrawStatusBar();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        void DrawResizeHandle()
        {
            var r = GUILayoutUtility.GetRect(4, 4, GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(r, new Color(.13f,.13f,.13f));
            EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeHorizontal);
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
            { GUIUtility.hotControl = id; sideDragging = true; Event.current.Use(); }
            if (sideDragging && Event.current.type == EventType.MouseDrag)
            { sideW = Mathf.Clamp(Event.current.mousePosition.x, 220, 520); Event.current.Use(); Repaint(); }
            if (Event.current.rawType == EventType.MouseUp && sideDragging)
            { sideDragging = false; GUIUtility.hotControl = 0; }
        }

        // ════════════════════════════════════════════════════════════
        //  Sidebar Tabs
        // ════════════════════════════════════════════════════════════

        void DrawSidebar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            TBtn("Setup",Tab.Setup); TBtn("Repack",Tab.Repack);
            TBtn("Transfer",Tab.Transfer); TBtn("Review",Tab.Review);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
            switch(tab)
            {
                case Tab.Setup:    DrawSetup();    break;
                case Tab.Repack:   DrawRepack();   break;
                case Tab.Transfer: DrawTransfer(); break;
                case Tab.Review:   DrawReview();   break;
            }
        }

        void TBtn(string l, Tab t)
        {
            var bg = GUI.backgroundColor;
            if (tab==t) GUI.backgroundColor = new Color(.35f,.65f,1f);
            if (GUILayout.Button(l, EditorStyles.toolbarButton)) tab = t;
            GUI.backgroundColor = bg;
        }

        // ──────────────── Setup ────────────────

        void DrawSetup()
        {
            H("Input");
            EditorGUI.BeginChangeCheck();
            lodGroup = (LODGroup)EditorGUILayout.ObjectField("LODGroup", lodGroup, typeof(LODGroup), true);
            if (EditorGUI.EndChangeCheck()) Refresh();

            if (lodGroup == null) { EditorGUILayout.HelpBox("Assign a LODGroup or select a GameObject with one.", MessageType.Info); return; }

            sourceLodIndex = EditorGUILayout.IntSlider("Source LOD", sourceLodIndex, 0, LodN - 1);
            EditorGUILayout.Space(4);
            H("Meshes (" + meshEntries.Count + " renderers)");

            for (int li = 0; li < LodN; li++)
            {
                var ee = meshEntries.Where(e => e.lodIndex == li).ToList();
                if (ee.Count == 0) continue;
                bool src = li == sourceLodIndex;
                var c = GUI.contentColor;
                if (src) GUI.contentColor = new Color(.4f,.85f,1f);
                EditorGUILayout.LabelField("LOD " + li + (src ? " (Source)" : " (Target)") + "  [" + ee.Count + "]", EditorStyles.boldLabel);
                GUI.contentColor = c;

                EditorGUI.indentLevel++;
                foreach (var e in ee)
                {
                    EditorGUILayout.BeginHorizontal();
                    e.include = EditorGUILayout.Toggle(e.include, GUILayout.Width(16));
                    string badge = e.repackedMesh != null ? " [R]" : e.transferredMesh != null ? " [T]" : "";
                    EditorGUILayout.LabelField(e.renderer.name + badge, GUILayout.Width(150));
                    var m = e.originalMesh;
                    EditorGUILayout.LabelField("V:" + m.vertexCount + " T:" + (m.triangles.Length/3), EditorStyles.miniLabel);
                    if (GUILayout.Button("Eye", EditorStyles.miniButton, GUILayout.Width(30)))
                    { pvLod = li; Repaint(); }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUI.indentLevel--;
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.Space(8);
            H("Pipeline Settings");
            pipeSettings.sourceUvChannel = EditorGUILayout.IntPopup("Source UV", pipeSettings.sourceUvChannel, new[]{"UV0","UV2"}, new[]{0,2});
            pipeSettings.targetUvChannel = EditorGUILayout.IntPopup("Target UV", pipeSettings.targetUvChannel, new[]{"UV0","UV2"}, new[]{0,2});

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Projection", EditorStyles.miniLabel);
            pipeSettings.maxProjectionDistance = EditorGUILayout.FloatField("  Max Dist", pipeSettings.maxProjectionDistance);
            pipeSettings.maxNormalAngle = EditorGUILayout.Slider("  Normal Angle", pipeSettings.maxNormalAngle, 10, 180);
            pipeSettings.filterBySubmesh = EditorGUILayout.Toggle("  Submesh Filter", pipeSettings.filterBySubmesh);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Border Repair", EditorStyles.miniLabel);
            pipeSettings.enableBorderRepair = EditorGUILayout.Toggle("  Enable", pipeSettings.enableBorderRepair);
            if (pipeSettings.enableBorderRepair)
            {
                pipeSettings.perimeterTolerance = EditorGUILayout.FloatField("  Perim Tol", pipeSettings.perimeterTolerance);
                pipeSettings.borderFuseTolerance = EditorGUILayout.FloatField("  Fuse Tol", pipeSettings.borderFuseTolerance);
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Output", EditorStyles.miniLabel);
            pipeSettings.saveNewMeshAssets = EditorGUILayout.Toggle("  Save Assets", pipeSettings.saveNewMeshAssets);
            if (pipeSettings.saveNewMeshAssets)
                pipeSettings.savePath = EditorGUILayout.TextField("  Path", pipeSettings.savePath);

            // ── UV0 Analysis ──
            EditorGUILayout.Space(8);
            H("UV0 Analysis & Fix");
            EditorGUILayout.HelpBox(
                "Detects false UV seams (weld candidates), degenerate triangles, " +
                "flipped triangles, and overlapping shells in UV0.",
                MessageType.Info);

            ColorBtn(new Color(.5f,.7f,.9f), "Analyze UV0", 24, ExecAnalyzeUv0);

            if (uv0Analyzed)
            {
                bool anyIssues = false;
                foreach (var kv in uv0Reports)
                {
                    var r = kv.Value;
                    EditorGUILayout.LabelField(r.meshName, EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(
                        $"  {r.totalShells} shells, {r.totalVertices} verts, {r.totalTriangles} tris",
                        EditorStyles.miniLabel);

                    if (r.falseSeamPairs > 0)
                    {
                        var c = GUI.contentColor;
                        GUI.contentColor = new Color(1f,.7f,.2f);
                        EditorGUILayout.LabelField(
                            $"  ⚠ {r.falseSeamPairs} false seam pairs ({r.falseSeamVertices} weldable verts)",
                            EditorStyles.miniLabel);
                        GUI.contentColor = c;
                        anyIssues = true;
                    }
                    if (r.degenerateTriangles > 0)
                    {
                        var c = GUI.contentColor;
                        GUI.contentColor = new Color(1f,.5f,.3f);
                        EditorGUILayout.LabelField(
                            $"  ⚠ {r.degenerateTriangles} degenerate UV triangles",
                            EditorStyles.miniLabel);
                        GUI.contentColor = c;
                    }
                    if (r.flippedTriangles > 0)
                    {
                        var c = GUI.contentColor;
                        GUI.contentColor = new Color(1f,.4f,.4f);
                        EditorGUILayout.LabelField(
                            $"  ⚠ {r.flippedTriangles} flipped UV triangles",
                            EditorStyles.miniLabel);
                        GUI.contentColor = c;
                    }
                    if (r.overlapGroups > 0)
                    {
                        EditorGUILayout.LabelField(
                            $"  {r.overlapGroups} overlap groups ({r.overlappingShells} shells)",
                            EditorStyles.miniLabel);
                    }
                    if (!r.HasIssues)
                    {
                        var c = GUI.contentColor;
                        GUI.contentColor = new Color(.3f,.9f,.3f);
                        EditorGUILayout.LabelField("  ✓ No issues", EditorStyles.miniLabel);
                        GUI.contentColor = c;
                    }
                }

                if (anyIssues && !uv0Welded)
                {
                    EditorGUILayout.Space(4);
                    ColorBtn(new Color(.9f,.7f,.2f), "Weld False Seams (all meshes)", 24, ExecWeldUv0);
                }
                else if (uv0Welded)
                {
                    var c = GUI.contentColor;
                    GUI.contentColor = new Color(.3f,.9f,.3f);
                    EditorGUILayout.LabelField("✓ UV0 welded — working copies ready", EditorStyles.miniLabel);
                    GUI.contentColor = c;
                }
            }
        }

        // ──────────────── Repack ────────────────

        void DrawRepack()
        {
            H("xatlas Repack  (LOD0 → UV2)");
            if (lodGroup == null) { Warn("Set LODGroup first."); return; }

            atlasResolution = EditorGUILayout.IntField("Resolution", atlasResolution);
            shellPaddingPx  = EditorGUILayout.IntSlider("Shell Padding (px)", shellPaddingPx, 0, 16);
            borderPaddingPx = EditorGUILayout.IntSlider("Border Padding (px)", borderPaddingPx, 0, 16);
            if (borderPaddingPx == 0)
                EditorGUILayout.LabelField("  Border=0: UVs extend to atlas edges (Clamp mode)", EditorStyles.miniLabel);
            rotateCharts    = EditorGUILayout.Toggle("Rotate", rotateCharts);

            var src = ForLod(sourceLodIndex);
            EditorGUILayout.Space(2);
            foreach (var e in src)
                EditorGUILayout.LabelField("  " + (e.repackedMesh != null ? "V" : "O") + " " + e.renderer.name, EditorStyles.miniLabel);

            EditorGUILayout.Space(6);
            ColorBtn(new Color(.3f,.8f,.4f), "Repack All", 28, () => ExecRepack(src));

            if (src.Count > 1)
            {
                EditorGUILayout.Space(2);
                foreach (var e in src)
                    if (GUILayout.Button("Repack: " + e.renderer.name, EditorStyles.miniButton))
                        ExecRepack(new List<MeshEntry>{e});
            }

            if (hasRepack)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox("Repack done. Preview UV2, then Transfer.", MessageType.Info);
                if (GUILayout.Button("Preview UV2"))
                { pvChannel = 2; pvLod = sourceLodIndex; showStatus = false; Repaint(); }
            }
        }

        // ──────────────── Transfer ────────────────

        void DrawTransfer()
        {
            H("UV Transfer  (Source → Targets)");
            if (lodGroup == null) { Warn("Set LODGroup first."); return; }
            if (!hasRepack) { Warn("Run Repack first."); return; }

            for (int li = 0; li < LodN; li++)
            {
                if (li == sourceLodIndex) continue;
                var ee = ForLod(li);
                if (ee.Count == 0) continue;
                bool done = ee.All(e => e.transferredMesh != null);
                bool part = ee.Any(e => e.transferredMesh != null);
                string ico = done ? "V" : part ? "~" : "O";
                var c = GUI.contentColor;
                if (done) GUI.contentColor = new Color(.3f,.9f,.3f);
                EditorGUILayout.LabelField(ico + " LOD" + li + ": " + ee.Count + " mesh", EditorStyles.boldLabel);
                GUI.contentColor = c;

                EditorGUI.indentLevel++;
                foreach (var e in ee)
                {
                    string st = e.transferredMesh != null ? "V" : "O";
                    string extra = "";
                    if (e.shellTransferResult != null)
                    {
                        var r = e.shellTransferResult;
                        float p = r.verticesTotal > 0 ? r.verticesTransferred*100f/r.verticesTotal : 0;
                        extra = $" ({r.shellsMatched}sh, {p:F0}% verts)";
                    }
                    else if (e.report.HasValue)
                    {
                        var r = e.report.Value;
                        float p = r.totalTriangles > 0 ? r.accepted*100f/r.totalTriangles : 0;
                        extra = " (" + p.ToString("F0") + "% ok)";
                    }
                    EditorGUILayout.LabelField("  " + st + " " + e.renderer.name + extra, EditorStyles.miniLabel);
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(8);
            ColorBtn(new Color(.3f,.6f,1f), "Transfer All Targets", 28, () => ExecTransferAll());

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Per-LOD re-run:", EditorStyles.miniLabel);
            for (int li = 0; li < LodN; li++)
            {
                if (li == sourceLodIndex || ForLod(li).Count == 0) continue;
                if (GUILayout.Button("Re-transfer LOD" + li, EditorStyles.miniButton))
                    ExecTransferLod(li);
            }

            if (hasTransfer)
            {
                EditorGUILayout.Space(6);
                if (GUILayout.Button("Preview Result"))
                {
                    for (int i = 0; i < LodN; i++)
                        if (i != sourceLodIndex && ForLod(i).Count > 0) { pvLod = i; break; }
                    pvChannel = 2; showStatus = true; Repaint();
                }
                if (GUILayout.Button("Go to Review")) tab = Tab.Review;
            }
        }

        // ──────────────── Review ────────────────

        void DrawReview()
        {
            H("Quality Report");
            if (!hasTransfer) { EditorGUILayout.HelpBox("Run Transfer first.", MessageType.Info); return; }

            reportScroll = EditorGUILayout.BeginScrollView(reportScroll);

            var srcE = ForLod(sourceLodIndex);
            EditorGUILayout.LabelField("-- Source --", EditorStyles.boldLabel);
            foreach (var e in srcE)
            {
                if (e.repackedMesh == null) continue;
                var uvl = new List<Vector2>();
                e.repackedMesh.GetUVs(2, uvl);
                var sh = UvShellExtractor.Extract(uvl.ToArray(), e.repackedMesh.triangles);
                EditorGUILayout.LabelField("  " + e.renderer.name + ": " + sh.Count + " shells", EditorStyles.miniLabel);
            }
            EditorGUILayout.Space(4);

            for (int li = 0; li < LodN; li++)
            {
                if (li == sourceLodIndex) continue;
                var ee = ForLod(li);
                if (!ee.Any(e => e.report.HasValue || e.shellTransferResult != null)) continue;
                EditorGUILayout.LabelField("-- LOD" + li + " --", EditorStyles.boldLabel);
                foreach (var e in ee)
                {
                    // New pipeline: GroupedShellTransfer results
                    if (e.shellTransferResult != null)
                    {
                        var r = e.shellTransferResult;
                        EditorGUILayout.LabelField("  " + e.renderer.name + " (shell transform)", EditorStyles.miniBoldLabel);
                        Bar("Verts OK", r.verticesTransferred, r.verticesTotal, cAccept);
                        Bar("Verts Miss", r.verticesTotal - r.verticesTransferred, r.verticesTotal, cReject);
                        EditorGUILayout.LabelField($"  Shells: {r.shellsMatched} matched, {r.shellsUnmatched} unmatched", EditorStyles.miniLabel);
                        EditorGUILayout.Space(4);
                        continue;
                    }
                    // Legacy pipeline: old report
                    if (!e.report.HasValue) continue;
                    var rp = e.report.Value;
                    EditorGUILayout.LabelField("  " + e.renderer.name, EditorStyles.miniBoldLabel);
                    Bar("Accepted",  rp.accepted, rp.totalTriangles, cAccept);
                    Bar("Ambiguous", rp.ambiguous, rp.totalTriangles, cAmbig);
                    Bar("BdrRisk",   rp.borderRisk, rp.totalTriangles, cBorder);
                    Bar("Mismatch",  rp.unavoidableMismatch, rp.totalTriangles, cMis);
                    Bar("Rejected",  rp.rejected, rp.totalTriangles, cReject);
                    EditorGUILayout.LabelField("  Err mean=" + rp.meanError.ToString("F5") + " max=" + rp.maxError.ToString("F5") + "  Conf=" + rp.meanConfidence.ToString("F3"), EditorStyles.miniLabel);
                    if (rp.borderReport.totalBorderPrims > 0)
                        EditorGUILayout.LabelField("  Border: " + rp.borderReport.repairedCount + " fix, " + rp.borderReport.skippedAlreadyMatching + " ok, " + rp.borderReport.markedBorderRisk + " risk", EditorStyles.miniLabel);
                    EditorGUILayout.Space(4);
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            H("3D Preview");
            {
                var bg = GUI.backgroundColor;
                if (CheckerTexturePreview.IsActive) GUI.backgroundColor = new Color(1f,.4f,.3f);
                string cLbl = CheckerTexturePreview.IsActive ? "■ Disable Checker" : "▶ Checker Preview (UV2)";
                if (GUILayout.Button(cLbl, GUILayout.Height(24)))
                    ToggleChecker();
                GUI.backgroundColor = bg;
            }

            EditorGUILayout.Space(6);
            H("Apply UV2 to FBX (Postprocessor)");
            EditorGUILayout.HelpBox(
                "Saves UV2 as sidecar asset beside the FBX. On every FBX reimport " +
                "the postprocessor injects UV2 — just like Unity's Generate Lightmap UVs.",
                MessageType.Info);
            ColorBtn(new Color(.3f,.85f,.4f), "Apply UV2 to FBX", 28, ApplyUv2ToFbx);

            EditorGUILayout.Space(4);
            ColorBtn(new Color(.9f,.3f,.3f), "Reset UV2 (delete sidecar)", 24, ResetUv2FromFbx);

            EditorGUILayout.Space(8);
            H("Legacy Save (mesh .asset copies)");
            pipeSettings.savePath = EditorGUILayout.TextField("Path", pipeSettings.savePath);
            ColorBtn(new Color(.6f,.5f,.3f), "Save All Mesh Assets", 24, SaveAll);
            if (GUILayout.Button("Update LODGroup Refs")) UpdateRefs();
        }

        void Bar(string label, int n, int total, Color col)
        {
            float pct = total > 0 ? (float)n / total : 0;
            var r = GUILayoutUtility.GetRect(0, 15, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(.15f,.15f,.15f));
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width * pct, r.height), col);
            var s = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft };
            s.normal.textColor = Color.white;
            EditorGUI.LabelField(new Rect(r.x+4, r.y, r.width, r.height), label + ": " + n + " (" + (pct*100).ToString("F1") + "%)", s);
        }

        // ════════════════════════════════════════════════════════════
        //  Canvas Toolbar
        // ════════════════════════════════════════════════════════════

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (LodN > 0)
            {
                var names = new string[LodN];
                for (int i = 0; i < LodN; i++)
                    names[i] = i == sourceLodIndex ? "LOD" + i + "(S)" : "LOD" + i;
                pvLod = EditorGUILayout.Popup(pvLod, names, EditorStyles.toolbarPopup, GUILayout.Width(80));
            }
            int ci = pvChannel == 0 ? 0 : 1;
            ci = GUILayout.Toolbar(ci, new[]{"UV0","UV2"}, EditorStyles.toolbarButton, GUILayout.Width(90));
            pvChannel = ci == 0 ? 0 : 2;

            GUILayout.Space(4);
            showFill   = GUILayout.Toggle(showFill,   "Fl", EditorStyles.toolbarButton, GUILayout.Width(22));
            showWire   = GUILayout.Toggle(showWire,   "Wr", EditorStyles.toolbarButton, GUILayout.Width(24));
            showBorder = GUILayout.Toggle(showBorder, "Bd", EditorStyles.toolbarButton, GUILayout.Width(22));
            showStatus = GUILayout.Toggle(showStatus, "St", EditorStyles.toolbarButton, GUILayout.Width(22));
            GUILayout.Space(4);
            zoom = EditorGUILayout.Slider(zoom, .5f, 4f, GUILayout.Width(100));
            if (showFill) fillAlpha = EditorGUILayout.Slider(fillAlpha, .05f, .6f, GUILayout.Width(80));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ════════════════════════════════════════════════════════════
        //  UV Canvas
        // ════════════════════════════════════════════════════════════

        void DrawCanvas()
        {
            var ee = ForLod(pvLod);
            if (ee.Count == 0) { EditorGUILayout.HelpBox("No meshes for this LOD.", MessageType.Info); return; }

            var draws = new List<ValueTuple<Mesh, MeshEntry, int>>();
            for (int i = 0; i < ee.Count; i++)
            {
                Mesh m = DMesh(ee[i]);
                if (m != null) draws.Add(new ValueTuple<Mesh, MeshEntry, int>(m, ee[i], i));
            }
            if (draws.Count == 0) return;

            float avW = position.width - sideW - 24;
            float avH = position.height - 105;
            float sz = Mathf.Max(64, Mathf.Min(avW, avH) * zoom);

            canvasScroll = EditorGUILayout.BeginScrollView(canvasScroll);
            var rect = GUILayoutUtility.GetRect(sz + 20, sz + 20);
            float ox = rect.x + 10, oy = rect.y + 10;

            if (Event.current.type == EventType.Repaint && glMat != null)
            {
                EditorGUI.DrawRect(new Rect(ox, oy, sz, sz), new Color(.12f,.12f,.12f));
                bool push = false;
                try
                {
                    glMat.SetPass(0);
                    GL.PushMatrix(); push = true;
                    GL.LoadPixelMatrix();
                    GlGrid(ox, oy, sz);

                    foreach (var item in draws)
                    {
                        Mesh mesh = item.Item1;
                        MeshEntry entry = item.Item2;
                        int idx = item.Item3;

                        var uvs = RdUv(mesh, pvChannel);
                        var tri = mesh.triangles;
                        if (uvs == null || tri == null) continue;
                        int uN = uvs.Length, fN = tri.Length / 3;

                        TriangleStatus[] stats = showStatus && entry.transferState != null ? entry.transferState.triangleStatus : null;
                        HashSet<int> bdr = showBorder && entry.transferState != null ? entry.transferState.borderPrimitiveIds : null;

                        if (showFill)
                        {
                            if (stats != null) GlFillSt(ox,oy,sz, uvs,tri,fN,uN, stats);
                            else               GlFillSh(ox,oy,sz, uvs,tri,fN,uN, idx);
                        }
                        if (bdr != null && bdr.Count > 0) GlBdr(ox,oy,sz, uvs,tri,fN,uN, bdr);
                        if (showWire) GlWr(ox,oy,sz, uvs,tri,fN,uN);
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[UV] GL: " + ex.Message); }
                finally { if (push) GL.PopMatrix(); }
            }
            EditorGUILayout.EndScrollView();
        }

        // ── GL ──

        static Vector2[] RdUv(Mesh m, int ch) { var l = new List<Vector2>(); m.GetUVs(ch, l); return l.Count > 0 ? l.ToArray() : null; }

        static bool UOk(Vector2 u) => u.x >= UV_LO && u.x <= UV_HI && u.y >= UV_LO && u.y <= UV_HI && !float.IsNaN(u.x) && !float.IsNaN(u.y) && !float.IsInfinity(u.x) && !float.IsInfinity(u.y);

        static bool TOk(Vector2[] u, int n, int a, int b, int c) => a>=0&&a<n&&b>=0&&b<n&&c>=0&&c<n && UOk(u[a])&&UOk(u[b])&&UOk(u[c]);

        static void Vx(float ox, float oy, float sz, Vector2 u) => GL.Vertex3(ox+u.x*sz, oy+(1f-u.y)*sz, 0);

        void GlGrid(float ox, float oy, float sz)
        {
            GL.Begin(GL.LINES);
            GL.Color(new Color(.25f,.25f,.25f));
            for (int g = 0; g <= 4; g++) { float p = g*.25f*sz; GL.Vertex3(ox+p,oy,0); GL.Vertex3(ox+p,oy+sz,0); GL.Vertex3(ox,oy+p,0); GL.Vertex3(ox+sz,oy+p,0); }
            GL.Color(new Color(.5f,.5f,.5f));
            GL.Vertex3(ox,oy,0); GL.Vertex3(ox+sz,oy,0);
            GL.Vertex3(ox+sz,oy,0); GL.Vertex3(ox+sz,oy+sz,0);
            GL.Vertex3(ox+sz,oy+sz,0); GL.Vertex3(ox,oy+sz,0);
            GL.Vertex3(ox,oy+sz,0); GL.Vertex3(ox,oy,0);
            GL.End();
        }

        void GlFillSh(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, int off)
        {
            List<UvShell> sh; try { sh = UvShellExtractor.Extract(uv, t); } catch { return; }
            int tot=0, b=0;
            GL.Begin(GL.TRIANGLES);
            foreach (var s in sh)
            {
                if (tot>=MAX_TRI) break;
                Color c = pal[(s.shellId+off*5)%pal.Length]; c.a = fillAlpha; GL.Color(c);
                foreach (int f in s.faceIndices)
                {
                    if (tot>=MAX_TRI) break;
                    int a0=t[f*3],a1=t[f*3+1],a2=t[f*3+2];
                    if (!TOk(uv,uN,a0,a1,a2)) continue;
                    Vx(ox,oy,sz,uv[a0]); Vx(ox,oy,sz,uv[a1]); Vx(ox,oy,sz,uv[a2]);
                    tot++; b++; if (b>=BATCH){GL.End();GL.Begin(GL.TRIANGLES);GL.Color(c);b=0;}
                }
            }
            GL.End();
        }

        void GlFillSt(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, TriangleStatus[] st)
        {
            int tot=0,b=0; Color cc=cNone; GL.Begin(GL.TRIANGLES); GL.Color(cc);
            for (int f=0; f<fN&&tot<MAX_TRI; f++)
            {
                int a0=t[f*3],a1=t[f*3+1],a2=t[f*3+2];
                if (!TOk(uv,uN,a0,a1,a2)) continue;
                Color nc = f<st.Length ? SC(st[f]) : cAccept;
                if (nc.r!=cc.r||nc.g!=cc.g||nc.b!=cc.b) { GL.End(); GL.Begin(GL.TRIANGLES); cc=nc; GL.Color(cc); b=0; }
                Vx(ox,oy,sz,uv[a0]); Vx(ox,oy,sz,uv[a1]); Vx(ox,oy,sz,uv[a2]);
                tot++; b++; if (b>=BATCH){GL.End();GL.Begin(GL.TRIANGLES);GL.Color(cc);b=0;}
            }
            GL.End();
        }

        void GlBdr(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, HashSet<int> bp)
        {
            int b=0; Color c=new Color(1f,.3f,0f,.35f);
            GL.Begin(GL.TRIANGLES); GL.Color(c);
            foreach (int f in bp)
            {
                if (f>=fN) continue;
                int a0=t[f*3],a1=t[f*3+1],a2=t[f*3+2];
                if (!TOk(uv,uN,a0,a1,a2)) continue;
                Vx(ox,oy,sz,uv[a0]); Vx(ox,oy,sz,uv[a1]); Vx(ox,oy,sz,uv[a2]);
                b++; if (b>=BATCH){GL.End();GL.Begin(GL.TRIANGLES);GL.Color(c);b=0;}
            }
            GL.End();
        }

        void GlWr(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN)
        {
            int tot=0,b=0; Color c=new Color(.3f,.8f,1f,.35f);
            GL.Begin(GL.LINES); GL.Color(c);
            for (int f=0; f<fN&&tot<MAX_TRI; f++)
            {
                int a0=t[f*3],a1=t[f*3+1],a2=t[f*3+2];
                if (!TOk(uv,uN,a0,a1,a2)) continue;
                Vx(ox,oy,sz,uv[a0]); Vx(ox,oy,sz,uv[a1]);
                Vx(ox,oy,sz,uv[a1]); Vx(ox,oy,sz,uv[a2]);
                Vx(ox,oy,sz,uv[a2]); Vx(ox,oy,sz,uv[a0]);
                tot++; b++; if (b>=BATCH){GL.End();GL.Begin(GL.LINES);GL.Color(c);b=0;}
            }
            GL.End();
        }

        static Color SC(TriangleStatus s)
        {
            switch(s)
            {
                case TriangleStatus.Accepted: return cAccept;
                case TriangleStatus.Ambiguous: return cAmbig;
                case TriangleStatus.BorderRisk: return cBorder;
                case TriangleStatus.UnavoidableMismatch: return cMis;
                case TriangleStatus.Rejected: return cReject;
                default: return cNone;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Status Bar
        // ════════════════════════════════════════════════════════════

        void DrawStatusBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            var ee = ForLod(pvLod); int tV=0,tT=0;
            foreach (var e in ee) { Mesh m=DMesh(e); if (m==null) continue; tV+=m.vertexCount; tT+=m.triangles.Length/3; }
            EditorGUILayout.LabelField("LOD" + pvLod + " | " + ee.Count + " mesh | V:" + tV + " T:" + tT + " | " + (pvChannel==0?"UV0":"UV2"), EditorStyles.miniLabel);
            if (showStatus) { GUILayout.FlexibleSpace(); Sw("Ok",cAccept);Sw("Am",cAmbig);Sw("Bd",cBorder);Sw("Mi",cMis);Sw("Rj",cReject); }
            EditorGUILayout.EndHorizontal();
        }

        void Sw(string l, Color c) { var r=GUILayoutUtility.GetRect(9,9,GUILayout.Width(9)); EditorGUI.DrawRect(r,c); EditorGUILayout.LabelField(l,EditorStyles.miniLabel,GUILayout.Width(18)); }

        Mesh DMesh(MeshEntry e)
        {
            if (e.lodIndex==sourceLodIndex && e.repackedMesh!=null && pvChannel==2) return e.repackedMesh;
            if (e.lodIndex!=sourceLodIndex && e.transferredMesh!=null && pvChannel==2) return e.transferredMesh;
            return e.originalMesh;
        }

        // ════════════════════════════════════════════════════════════
        //  UV0 Analysis & Weld
        // ════════════════════════════════════════════════════════════

        void ExecAnalyzeUv0()
        {
            if (lodGroup == null) return;
            uv0Reports.Clear();

            foreach (var e in meshEntries)
            {
                if (!e.include || e.originalMesh == null) continue;
                var report = Uv0Analyzer.Analyze(e.originalMesh);
                uv0Reports[e.originalMesh.GetInstanceID()] = report;
            }

            uv0Analyzed = true;
            int totalFalseSeams = 0, totalDegen = 0, totalFlipped = 0;
            foreach (var kv in uv0Reports)
            {
                totalFalseSeams += kv.Value.falseSeamPairs;
                totalDegen += kv.Value.degenerateTriangles;
                totalFlipped += kv.Value.flippedTriangles;
            }
            Debug.Log($"[UV0Analyze] {uv0Reports.Count} meshes: " +
                      $"{totalFalseSeams} false seams, {totalDegen} degenerate, {totalFlipped} flipped");
            Repaint();
        }

        void ExecWeldUv0()
        {
            if (lodGroup == null) return;

            int totalWelded = 0;
            foreach (var e in meshEntries)
            {
                if (!e.include || e.originalMesh == null) continue;

                // Check if this mesh has false seams
                int id = e.originalMesh.GetInstanceID();
                if (!uv0Reports.TryGetValue(id, out var report)) continue;
                if (report.falseSeamPairs == 0) continue;

                // Weld: creates new mesh with merged indices
                var welded = Uv0Analyzer.WeldUv0(e.originalMesh);
                if (welded != null && welded != e.originalMesh)
                {
                    e.originalMesh = welded;
                    e.wasWelded = true;
                    totalWelded++;
                }
            }

            uv0Welded = totalWelded > 0;
            Debug.Log($"[UV0Fix] Welded {totalWelded} meshes (working copies only, FBX untouched)");

            // Re-analyze to show updated state
            ExecAnalyzeUv0();
        }

        // ════════════════════════════════════════════════════════════
        //  Pipeline
        // ════════════════════════════════════════════════════════════

        void ExecRepack(List<MeshEntry> entries)
        {
            try
            {
                // Filter valid entries and create mesh copies
                var validEntries = new List<MeshEntry>();
                var meshCopies = new List<Mesh>();
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i]; var mesh = e.originalMesh; if (mesh == null) continue;
                    var uv0 = mesh.uv;
                    if (uv0 == null || uv0.Length == 0) { Debug.LogWarning("[Repack] " + e.renderer.name + ": no UV0"); continue; }
                    var cp = Instantiate(mesh); cp.name = mesh.name + "_repack";
                    validEntries.Add(e);
                    meshCopies.Add(cp);
                }

                if (meshCopies.Count == 0) return;

                EditorUtility.DisplayProgressBar("Repack", "Packing " + meshCopies.Count + " meshes into shared atlas...", 0.5f);

                var opts = RepackOptions.Default;
                opts.resolution = (uint)atlasResolution;
                opts.padding = (uint)shellPaddingPx;
                opts.borderPadding = (uint)borderPaddingPx;

                // Pack all meshes into a single shared atlas
                var results = XatlasRepack.RepackMulti(meshCopies.ToArray(), opts);

                for (int i = 0; i < validEntries.Count; i++)
                {
                    var e = validEntries[i];
                    if (!results[i].ok)
                    {
                        Debug.LogError("[Repack] " + e.renderer.name + ": " + results[i].error);
                        DestroyImmediate(meshCopies[i]);
                        continue;
                    }
                    e.repackedMesh = meshCopies[i];
                    Debug.Log("[Repack] " + e.renderer.name + ": " + results[i].shellCount + " shells, " +
                              results[i].overlapGroupCount + " overlap, atlas=" + results[i].atlasWidth + "x" + results[i].atlasHeight);
                }
                hasRepack = ForLod(sourceLodIndex).Any(e => e.repackedMesh != null);
            }
            catch (Exception ex) { Debug.LogError("[Repack] " + ex); }
            finally { EditorUtility.ClearProgressBar(); }
            Repaint();
        }

        void ExecTransferAll() { for (int li=0; li<LodN; li++) if (li!=sourceLodIndex) ExecTransferLod(li); }

        // ── Source shell analysis cache (mesh instanceID → shell infos) ──
        Dictionary<int, GroupedShellTransfer.SourceShellInfo[]> shellTransformCache =
            new Dictionary<int, GroupedShellTransfer.SourceShellInfo[]>();

        void ExecTransferLod(int tLod)
        {
            var srcE = ForLod(sourceLodIndex); var tgtE = ForLod(tLod);
            if (srcE.Count==0||tgtE.Count==0) return;
            try
            {
                for (int ti=0; ti<tgtE.Count; ti++)
                {
                    var te = tgtE[ti]; var se = ti<srcE.Count ? srcE[ti] : srcE[0];
                    Mesh sM = se.repackedMesh ?? se.originalMesh; Mesh tM = te.originalMesh;
                    if (sM==null||tM==null) continue;
                    EditorUtility.DisplayProgressBar("Transfer", "LOD"+tLod+": "+te.renderer.name, .1f+.8f*ti/tgtE.Count);

                    // Analyze source: extract UV0 shells and compute UV0→UV2 transforms
                    int id = sM.GetInstanceID();
                    if (!shellTransformCache.TryGetValue(id, out var srcInfos))
                    {
                        srcInfos = GroupedShellTransfer.AnalyzeSource(sM);
                        if (srcInfos != null) shellTransformCache[id] = srcInfos;
                    }
                    if (srcInfos == null) continue;

                    // Transfer: match target UV0 shells → source, apply transforms
                    var tr = GroupedShellTransfer.Transfer(tM, srcInfos);
                    if (tr.uv2 == null) continue;

                    var om = Instantiate(tM); om.name = tM.name+"_uvTransfer";
                    om.SetUVs(pipeSettings.targetUvChannel, new List<Vector2>(tr.uv2));
                    te.transferredMesh = om;
                    te.transferState = null;
                    te.report = null;
                    te.shellTransferResult = tr;

                    Debug.Log($"[Transfer] {te.renderer.name}: " +
                              $"{tr.shellsMatched} shells matched, {tr.shellsUnmatched} unmatched, " +
                              $"{tr.verticesTransferred}/{tr.verticesTotal} verts");
                }
                hasTransfer = tgtE.Any(e => e.transferredMesh!=null);
            }
            catch (Exception ex) { Debug.LogError("[Transfer] LOD"+tLod+": "+ex); }
            finally { EditorUtility.ClearProgressBar(); }
            Repaint();
        }

        // ════════════════════════════════════════════════════════════
        //  Save
        // ════════════════════════════════════════════════════════════

        void SaveAll()
        {
            string p = pipeSettings.savePath;
            if (string.IsNullOrEmpty(p)) p = "Assets/LightmapUvTool_Output";
            if (!AssetDatabase.IsValidFolder(p))
            {
                var par = System.IO.Path.GetDirectoryName(p);
                var fld = System.IO.Path.GetFileName(p);
                if (!string.IsNullOrEmpty(par)) AssetDatabase.CreateFolder(par, fld);
            }
            int n=0;
            foreach (var e in meshEntries)
            {
                Mesh m = e.lodIndex==sourceLodIndex ? e.repackedMesh : e.transferredMesh;
                if (m==null) continue;
                string ap = AssetDatabase.GenerateUniqueAssetPath(p+"/"+m.name+".asset");
                AssetDatabase.CreateAsset(m, ap); Debug.Log("[Save] "+ap); n++;
            }
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            Debug.Log("[Save] "+n+" assets -> "+p);
        }

        void UpdateRefs()
        {
            if (lodGroup==null) return; int n=0;
            foreach (var e in meshEntries)
            {
                Mesh m = e.lodIndex==sourceLodIndex ? e.repackedMesh : e.transferredMesh;
                if (m==null||e.meshFilter==null) continue;
                Undo.RecordObject(e.meshFilter, "UV Transfer"); e.meshFilter.sharedMesh = m; n++;
            }
            Debug.Log("[Save] "+n+" refs updated");
        }

        // ════════════════════════════════════════════════════════════
        //  Checker Preview
        // ════════════════════════════════════════════════════════════

        void ToggleChecker()
        {
            if (CheckerTexturePreview.IsActive)
            {
                CheckerTexturePreview.Restore();
                Repaint();
                return;
            }

            // Collect renderers + their UV2 working copies
            var entries = new List<(Renderer renderer, Mesh meshWithUv2)>();
            foreach (var e in meshEntries)
            {
                if (!e.include || e.renderer == null) continue;
                Mesh uvMesh = e.lodIndex == sourceLodIndex ? e.repackedMesh : e.transferredMesh;

                // Fallback: if no working copy, use original/FBX mesh if it has UV2
                if (uvMesh == null)
                {
                    Mesh fallback = e.originalMesh ?? e.fbxMesh;
                    if (fallback != null)
                    {
                        var testUv2 = new List<Vector2>();
                        fallback.GetUVs(2, testUv2);
                        if (testUv2.Count > 0) uvMesh = fallback;
                    }
                }

                if (uvMesh != null)
                    entries.Add((e.renderer, uvMesh));
            }

            if (entries.Count == 0)
            {
                Debug.LogWarning("[Checker] No meshes with UV2. Run Repack + Transfer first.");
                return;
            }

            CheckerTexturePreview.Apply(entries);
            Repaint();
        }

        // ════════════════════════════════════════════════════════════
        //  Apply UV2 to FBX (postprocessor sidecar)
        // ════════════════════════════════════════════════════════════

        void ApplyUv2ToFbx()
        {
            if (lodGroup == null) return;

            // Group mesh entries by source FBX path
            var fbxGroups = new Dictionary<string, List<(string name, Vector2[] uv2, bool welded)>>();

            foreach (var e in meshEntries)
            {
                if (!e.include) continue;
                Mesh resultMesh = e.lodIndex == sourceLodIndex ? e.repackedMesh : e.transferredMesh;
                if (resultMesh == null) continue;

                // Get FBX path from the immutable FBX mesh ref (survives weld)
                Mesh pathMesh = e.fbxMesh != null ? e.fbxMesh : e.originalMesh;
                string fbxPath = AssetDatabase.GetAssetPath(pathMesh);
                if (string.IsNullOrEmpty(fbxPath)) continue;

                // Read UV2 from our result mesh
                var uv2List = new List<Vector2>();
                resultMesh.GetUVs(pipeSettings.targetUvChannel, uv2List);
                if (uv2List.Count == 0) continue;

                if (!fbxGroups.ContainsKey(fbxPath))
                    fbxGroups[fbxPath] = new List<(string, Vector2[], bool)>();

                // Use FBX mesh name (postprocessor matches by this name)
                string meshName = e.fbxMesh != null ? e.fbxMesh.name : e.originalMesh.name;
                fbxGroups[fbxPath].Add((meshName, uv2List.ToArray(), e.wasWelded));
            }

            if (fbxGroups.Count == 0)
            {
                Debug.LogWarning("[Apply] No meshes with UV2 data to apply.");
                return;
            }

            int totalMeshes = 0;
            try
            {
                foreach (var kv in fbxGroups)
                {
                    string fbxPath = kv.Key;
                    string sidecarPath = Uv2DataAsset.GetSidecarPath(fbxPath);

                    // Load or create sidecar
                    var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
                    if (data == null)
                    {
                        data = ScriptableObject.CreateInstance<Uv2DataAsset>();
                        AssetDatabase.CreateAsset(data, sidecarPath);
                    }

                    // Write UV2 entries (with weld flag)
                    foreach (var entry in kv.Value)
                    {
                        data.Set(entry.name, entry.uv2, entry.welded);
                        totalMeshes++;
                    }

                    EditorUtility.SetDirty(data);
                    AssetDatabase.SaveAssets();

                    Debug.Log($"[Apply] Sidecar '{sidecarPath}': {kv.Value.Count} mesh(es)");

                    // Reimport FBX to trigger postprocessor
                    AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[Apply] " + ex);
            }

            AssetDatabase.Refresh();
            Debug.Log($"[Apply] Done: {totalMeshes} mesh(es) across {fbxGroups.Count} FBX file(s)");

            // Clear working copies — the FBX meshes now have UV2 baked in
            Refresh();
            Repaint();
        }

        // ════════════════════════════════════════════════════════════
        //  Reset UV2 (delete sidecar + reimport)
        // ════════════════════════════════════════════════════════════

        void ResetUv2FromFbx()
        {
            if (lodGroup == null) return;

            // Collect unique FBX paths
            var fbxPaths = new HashSet<string>();
            foreach (var e in meshEntries)
            {
                if (e.originalMesh == null) continue;
                string p = AssetDatabase.GetAssetPath(e.originalMesh);
                if (!string.IsNullOrEmpty(p)) fbxPaths.Add(p);
            }

            if (fbxPaths.Count == 0) return;

            if (!EditorUtility.DisplayDialog("Reset UV2",
                $"Delete UV2 sidecar data for {fbxPaths.Count} FBX file(s)?\n" +
                "This will remove all transferred UV2 on next reimport.",
                "Delete", "Cancel"))
                return;

            int deleted = 0;
            foreach (string fbxPath in fbxPaths)
            {
                string sidecarPath = Uv2DataAsset.GetSidecarPath(fbxPath);
                if (AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath) != null)
                {
                    AssetDatabase.DeleteAsset(sidecarPath);
                    Debug.Log($"[Reset] Deleted '{sidecarPath}'");
                    deleted++;
                }

                // Reimport FBX — mesh will no longer have UV2
                AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);
            }

            AssetDatabase.Refresh();
            Debug.Log($"[Reset] Deleted {deleted} sidecar(s), reimported {fbxPaths.Count} FBX");

            if (CheckerTexturePreview.IsActive) CheckerTexturePreview.Restore();
            Refresh();
            Repaint();
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
    }
}
