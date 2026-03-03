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
using UnityEngine.Rendering;
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

        // UV0 analysis
        Dictionary<int, Uv0Report> uv0Reports = new Dictionary<int, Uv0Report>();
        bool uv0Analyzed = false;
        bool uv0Welded = false;

        // Source analysis cache (mesh instanceID → data)
        Dictionary<int, SourceMeshData> srcCache = new Dictionary<int, SourceMeshData>();

        // UI
        enum Tab { Setup, Repack, Transfer }
        Tab tab = Tab.Setup;
        bool hasRepack, hasTransfer;
        bool splitTargetsInSymmetryStep;
        HashSet<int> lastSymmetrySplitLods = new HashSet<int>();

        // Canvas — fill mode (mutually exclusive overlays)
        enum FillMode { Shells, Status, ShellMatch, Validation, None }
        FillMode fillMode = FillMode.Shells;

        float canvasZoom = 1f;
        Vector2 canvasPan;
        bool canvasPanning;
        Rect lastCanvasRect;
        RenderTexture canvasRT;
        int  pvChannel = 1;
        int  pvLod     = 0;
        bool showWire = true, showBorder;
        float fillAlpha = 0.25f;

        // Checker mode (user toggle, independent from CheckerTexturePreview.IsActive)
        bool checkerEnabled;

        // Selection tracking — UV2 reset for arbitrary selected model
        string selectedSidecarPath;
        string selectedFbxPath;
        string selectedResetLabel;

        // Sidebar foldouts
        bool foldProjection = true;
        bool foldBorderRepair = false;
        bool foldOutput = true;
        bool foldUv0Analysis = false;
        bool foldRepackSettings = true;

        // Sidebar
        Vector2 sideScroll, reportScroll;
        float sideW = 300f;
        bool sideDragging;

        Material glMat;

        // ─── Mesh Entry ───
        class MeshEntry
        {
            public int lodIndex;
            public Renderer renderer;
            public MeshFilter meshFilter;
            public Mesh originalMesh;
            public Mesh fbxMesh;
            public bool include = true;
            public bool wasWelded;
            public bool wasEdgeWelded;
            public bool wasSymmetrySplit;
            public Mesh repackedMesh;
            public Mesh transferredMesh;
            public TargetTransferState transferState;
            public TransferQualityEvaluator.TransferReport? report;
            public GroupedShellTransfer.TransferResult shellTransferResult;
            public TransferValidator.ValidationReport validationReport;
            public bool hasExistingUv2; // cached: FBX mesh already has UV2 (post-Apply)
            /// <summary>
            /// Name with LOD/COL suffixes stripped — used to isolate source↔target
            /// matching when multiple sub-mesh groups share the same LODGroup.
            /// E.g. "InnerDoor_A_01_Base_LOD0" → "InnerDoor_A_01_Base"
            /// </summary>
            public string meshGroupKey;
        }

        /// <summary>
        /// Strip trailing LOD/COL suffixes to get a stable group key.
        /// Handles: _LOD0..9, _LOD0..LOD99, _COL, _COLL, _Collision (case-insensitive).
        /// </summary>
        static string ExtractGroupKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return System.Text.RegularExpressions.Regex.Replace(
                name,
                @"[_\-\s]+(LOD\d+|COL\w*|Collision)$",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // ════════════════════════════════════════════════════════════
        //  Constants & Palette
        // ════════════════════════════════════════════════════════════

        const float UV_LO = -4f, UV_HI = 5f;
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
        static readonly Color cMis    = new Color(.9f,.15f,.15f,.5f);
        static readonly Color cReject = new Color(.4f,.4f,.4f,.5f);
        static readonly Color cNone   = new Color(.3f,.3f,.3f,.3f);

        static readonly Color cValClean    = new Color(.2f, .85f, .3f, .4f);
        static readonly Color cValStretch  = new Color(.95f, .85f, .15f, .5f);
        static readonly Color cValZero     = new Color(.7f, .2f, .9f, .5f);
        static readonly Color cValOOB      = new Color(1f, .5f, .1f, .5f);
        static readonly Color cValOverlap  = new Color(1f, .1f, .9f, .55f);
        static readonly Color cValTexel    = new Color(.1f, .7f, .9f, .5f);

        // Fill mode labels for dropdown
        static readonly string[] fillModeLabels = { "Shells", "Status", "Shell Match", "Validation", "None" };

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
            glMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            glMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            glMat.SetInt("_Cull",     (int)CullMode.Off);
            glMat.SetInt("_ZWrite",   0);
        }

        void OnDisable()
        {
            if (checkerEnabled) CheckerTexturePreview.Restore();
            checkerEnabled = false;
            CleanupWorkingMeshes();
            if (canvasRT) { canvasRT.Release(); DestroyImmediate(canvasRT); canvasRT = null; }
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

            // Re-apply checker to new selection
            if (checkerEnabled)
                ReapplyCheckerToSelection();

            UpdateSelectedSidecar();
            Repaint();
        }

        /// <summary>
        /// Check if the currently selected object has a UV2 sidecar asset.
        /// Works for any model, not just the loaded LODGroup.
        /// </summary>
        void UpdateSelectedSidecar()
        {
            selectedSidecarPath = null;
            selectedFbxPath = null;
            selectedResetLabel = null;

            // Collect FBX paths from loaded meshEntries (always available when LODGroup assigned)
            var fbxPaths = new HashSet<string>();
            foreach (var e in meshEntries)
            {
                Mesh m = e.fbxMesh ?? e.originalMesh;
                if (m == null) continue;
                string path = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    fbxPaths.Add(path);
            }

            // Fallback: scan selected object if no LODGroup loaded
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

            // Find the first FBX that has a sidecar
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
                    var fbm = mf.sharedMesh;
                    var uv2Check = new List<Vector2>();
                    fbm.GetUVs(1, uv2Check);
                    meshEntries.Add(new MeshEntry {
                        lodIndex = li, renderer = r, meshFilter = mf,
                        originalMesh = fbm,
                        fbxMesh = fbm,
                        hasExistingUv2 = uv2Check.Count > 0,
                        meshGroupKey = ExtractGroupKey(r.name) });
                }
            }
            UpdateSelectedSidecar();
        }

        List<MeshEntry> ForLod(int li) => meshEntries.Where(e => e.lodIndex == li && e.include).ToList();
        int LodN => lodGroup != null ? lodGroup.GetLODs().Length : 0;

        // ════════════════════════════════════════════════════════════
        //  OnGUI
        // ════════════════════════════════════════════════════════════

        void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();

            // ── Left sidebar ──
            EditorGUILayout.BeginVertical(GUILayout.Width(sideW));
            sideScroll = EditorGUILayout.BeginScrollView(sideScroll);
            DrawSidebar();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            DrawResizeHandle();

            // ── Right: toolbar + canvas + status ──
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
            { sideW = Mathf.Clamp(Event.current.mousePosition.x, 200, 520); Event.current.Use(); Repaint(); }
            if (Event.current.rawType == EventType.MouseUp && sideDragging)
            { sideDragging = false; GUIUtility.hotControl = 0; }
        }

        // ════════════════════════════════════════════════════════════
        //  Sidebar
        // ════════════════════════════════════════════════════════════

        void DrawSidebar()
        {
            // Tab bar
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
            // ── Input ──
            EditorGUI.BeginChangeCheck();
            lodGroup = (LODGroup)EditorGUILayout.ObjectField("LODGroup", lodGroup, typeof(LODGroup), true);
            if (EditorGUI.EndChangeCheck()) Refresh();

            if (lodGroup == null) { EditorGUILayout.HelpBox("Assign LODGroup or select a GameObject.", MessageType.Info); return; }

            sourceLodIndex = EditorGUILayout.IntSlider("Source LOD", sourceLodIndex, 0, LodN - 1);

            // ── Meshes (compact) ──
            EditorGUILayout.Space(2);
            for (int li = 0; li < LodN; li++)
            {
                var ee = meshEntries.Where(e => e.lodIndex == li).ToList();
                if (ee.Count == 0) continue;
                bool src = li == sourceLodIndex;
                var c = GUI.contentColor;
                if (src) GUI.contentColor = new Color(.4f,.85f,1f);
                string header = (src ? "LOD " + li + " (Source)" : "LOD " + li + " (Target)") + "  [" + ee.Count + "]";
                EditorGUILayout.LabelField(header, EditorStyles.miniBoldLabel);
                GUI.contentColor = c;

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

            // ── UV2 sidecar detected ──
            if (selectedSidecarPath != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    "UV2 applied: " + selectedResetLabel + "\n" + selectedSidecarPath,
                    MessageType.Info);
                ColorBtn(new Color(.95f,.35f,.3f), "Reset UV2 (delete sidecar)", 22, ResetSelectedUv2);
            }

            // ── Reset working copies ──
            bool anyModified = meshEntries.Any(e =>
                e.wasWelded || e.repackedMesh != null || e.transferredMesh != null);
            if (anyModified)
            {
                EditorGUILayout.Space(2);
                ColorBtn(new Color(.9f,.35f,.35f), "Reset All Working Copies", 20, ResetWorkingCopies);
            }

            // ── Repack Settings (quick access) ──
            EditorGUILayout.Space(4);
            H("Repack");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Resolution", GUILayout.Width(66));
            atlasResolution = EditorGUILayout.IntField(atlasResolution, GUILayout.Width(60));
            GUILayout.Space(8);
            EditorGUILayout.LabelField("Pad", GUILayout.Width(26));
            shellPaddingPx = EditorGUILayout.IntField(shellPaddingPx, GUILayout.Width(30));
            GUILayout.Space(8);
            EditorGUILayout.LabelField("Bdr", GUILayout.Width(24));
            borderPaddingPx = EditorGUILayout.IntField(borderPaddingPx, GUILayout.Width(30));
            EditorGUILayout.EndHorizontal();

            // ── Run Full Pipeline ──
            EditorGUILayout.Space(6);
            ColorBtn(new Color(.2f,.75f,.95f), "▶  Run Full Pipeline", 30, ExecFullPipeline);
            splitTargetsInSymmetryStep = EditorGUILayout.ToggleLeft(
                "SymSplit target LODs (advanced / risky)",
                splitTargetsInSymmetryStep);
            if (splitTargetsInSymmetryStep)
                EditorGUILayout.HelpBox("Внимание: split на target LOD может менять топологию и ухудшать стабильность сопоставления при Transfer/Apply.", MessageType.Warning);
            EditorGUILayout.LabelField("Analyze → Weld → SymSplit(Source by default) → Repack → Transfer → Review", EditorStyles.miniLabel);

            // ── Pipeline Settings ──
            EditorGUILayout.Space(6);
            H("Pipeline Settings");
            pipeSettings.sourceUvChannel = EditorGUILayout.IntPopup("Source UV", pipeSettings.sourceUvChannel, new[]{"UV0","UV2"}, new[]{0,1});
            pipeSettings.targetUvChannel = EditorGUILayout.IntPopup("Target UV", pipeSettings.targetUvChannel, new[]{"UV0","UV2"}, new[]{0,1});

            foldProjection = EditorGUILayout.Foldout(foldProjection, "Projection", true);
            if (foldProjection)
            {
                EditorGUI.indentLevel++;
                pipeSettings.maxProjectionDistance = EditorGUILayout.FloatField("Max Dist", pipeSettings.maxProjectionDistance);
                pipeSettings.maxNormalAngle = EditorGUILayout.Slider("Normal Angle", pipeSettings.maxNormalAngle, 10, 180);
                pipeSettings.filterBySubmesh = EditorGUILayout.Toggle("Submesh Filter", pipeSettings.filterBySubmesh);
                EditorGUI.indentLevel--;
            }

            foldBorderRepair = EditorGUILayout.Foldout(foldBorderRepair, "Border Repair", true);
            if (foldBorderRepair)
            {
                EditorGUI.indentLevel++;
                pipeSettings.enableBorderRepair = EditorGUILayout.Toggle("Enable", pipeSettings.enableBorderRepair);
                if (pipeSettings.enableBorderRepair)
                {
                    pipeSettings.perimeterTolerance = EditorGUILayout.FloatField("Perim Tol", pipeSettings.perimeterTolerance);
                    pipeSettings.borderFuseTolerance = EditorGUILayout.FloatField("Fuse Tol", pipeSettings.borderFuseTolerance);
                }
                EditorGUI.indentLevel--;
            }

            foldOutput = EditorGUILayout.Foldout(foldOutput, "Output", true);
            if (foldOutput)
            {
                EditorGUI.indentLevel++;
                pipeSettings.saveNewMeshAssets = EditorGUILayout.Toggle("Save Assets", pipeSettings.saveNewMeshAssets);
                if (pipeSettings.saveNewMeshAssets)
                    pipeSettings.savePath = EditorGUILayout.TextField("Path", pipeSettings.savePath);
                EditorGUI.indentLevel--;
            }

            // ── UV0 Analysis ──
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

                        if (r.falseSeamPairs > 0)
                        {
                            var cc = GUI.contentColor; GUI.contentColor = new Color(1f,.7f,.2f);
                            EditorGUILayout.LabelField($"  {r.falseSeamPairs} false seams ({r.falseSeamVertices} weldable)", EditorStyles.miniLabel);
                            GUI.contentColor = cc;
                            anyIssues = true;
                        }
                        if (r.degenerateTriangles > 0)
                        {
                            var cc = GUI.contentColor; GUI.contentColor = new Color(1f,.5f,.3f);
                            EditorGUILayout.LabelField($"  {r.degenerateTriangles} degenerate tris", EditorStyles.miniLabel);
                            GUI.contentColor = cc;
                        }
                        if (r.flippedTriangles > 0)
                        {
                            EditorGUILayout.LabelField($"  {r.flippedTriangles} minor winding inconsistencies", EditorStyles.miniLabel);
                        }
                        if (!r.HasIssues)
                        {
                            var cc = GUI.contentColor; GUI.contentColor = new Color(.3f,.9f,.3f);
                            EditorGUILayout.LabelField("  No issues", EditorStyles.miniLabel);
                            GUI.contentColor = cc;
                        }
                    }

                    bool hasTargetLods = meshEntries.Any(e => e.include && e.lodIndex != sourceLodIndex);
                    if ((anyIssues || hasTargetLods) && !uv0Welded)
                        ColorBtn(new Color(.9f,.7f,.2f), "Weld (false seams + source-guided)", 22, ExecWeldUv0);
                    else if (uv0Welded)
                    {
                        var cc = GUI.contentColor; GUI.contentColor = new Color(.3f,.9f,.3f);
                        EditorGUILayout.LabelField("UV0 welded — working copies ready", EditorStyles.miniLabel);
                        GUI.contentColor = cc;
                    }
                }
            }
        }

        // ──────────────── Repack ────────────────

        void DrawRepack()
        {
            H("xatlas Repack (LOD0 → UV2)");
            if (lodGroup == null) { Warn("Set LODGroup first."); return; }

            foldRepackSettings = EditorGUILayout.Foldout(foldRepackSettings, "Settings", true);
            if (foldRepackSettings)
            {
                EditorGUI.indentLevel++;
                atlasResolution = EditorGUILayout.IntField("Resolution", atlasResolution);
                shellPaddingPx  = EditorGUILayout.IntSlider("Shell Padding", shellPaddingPx, 0, 16);
                borderPaddingPx = EditorGUILayout.IntSlider("Border Padding", borderPaddingPx, 0, 16);
                if (borderPaddingPx == 0)
                    EditorGUILayout.LabelField("Border=0: UVs to atlas edges (Clamp)", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }

            var src = ForLod(sourceLodIndex);

            EditorGUILayout.Space(4);
            ColorBtn(new Color(.3f,.8f,.4f), "Repack All", 26, () => ExecRepack(src));

            if (src.Count > 1)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Per-mesh:", EditorStyles.miniLabel);
                foreach (var e in src)
                    if (GUILayout.Button("  " + e.renderer.name, EditorStyles.miniButton))
                        ExecRepack(new List<MeshEntry>{e});
            }

            if (hasRepack)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox("Repack done. Preview UV2, then Transfer.", MessageType.Info);
            }
        }

        // ──────────────── Transfer + Review (merged) ────────────────

        void DrawTransfer()
        {
            H("UV Transfer (Source → Targets)");
            if (lodGroup == null) { Warn("Set LODGroup first."); return; }
            if (!hasRepack) { Warn("Run Repack first."); return; }

            // Status per LOD
            for (int li = 0; li < LodN; li++)
            {
                if (li == sourceLodIndex) continue;
                var ee = ForLod(li);
                if (ee.Count == 0) continue;
                bool done = ee.All(e => e.transferredMesh != null);
                var cc = GUI.contentColor;
                if (done) GUI.contentColor = new Color(.3f,.9f,.3f);
                EditorGUILayout.LabelField((done ? "✓" : "○") + " LOD" + li + ": " + ee.Count + " mesh", EditorStyles.miniBoldLabel);
                GUI.contentColor = cc;

                foreach (var e in ee)
                {
                    string extra = "";
                    if (e.shellTransferResult != null)
                    {
                        var r = e.shellTransferResult;
                        float p = r.verticesTotal > 0 ? r.verticesTransferred * 100f / r.verticesTotal : 0;
                        extra = $" ({r.shellsMatched}sh, {p:F0}%)";
                    }
                    EditorGUILayout.LabelField("  " + (e.transferredMesh != null ? "✓" : "○") + " " + e.renderer.name + extra, EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.Space(6);
            ColorBtn(new Color(.3f,.6f,1f), "Transfer All Targets", 26, ExecTransferAll);

            // ── Validation Report (inline) ──
            if (hasTransfer)
            {
                EditorGUILayout.Space(8);
                H("Quality Report");
                reportScroll = EditorGUILayout.BeginScrollView(reportScroll, GUILayout.MaxHeight(250));

                for (int li = 0; li < LodN; li++)
                {
                    if (li == sourceLodIndex) continue;
                    var ee = ForLod(li);
                    if (!ee.Any(e => e.shellTransferResult != null || e.report.HasValue)) continue;

                    EditorGUILayout.LabelField("LOD" + li, EditorStyles.miniBoldLabel);
                    foreach (var e in ee)
                    {
                        if (e.shellTransferResult != null)
                        {
                            var r = e.shellTransferResult;
                            EditorGUILayout.LabelField("  " + e.renderer.name, EditorStyles.miniLabel);
                            Bar("OK", r.verticesTransferred, r.verticesTotal, cAccept);
                            Bar("Miss", r.verticesTotal - r.verticesTransferred, r.verticesTotal, cReject);

                            var vr = e.validationReport;
                            if (vr != null)
                            {
                                Bar("Clean", vr.cleanCount + vr.invertedCount, vr.totalTriangles, cValClean);
                                if (vr.stretchedCount > 0) Bar("Str", vr.stretchedCount, vr.totalTriangles, cValStretch);
                                if (vr.zeroAreaCount > 0) Bar("0A", vr.zeroAreaCount, vr.totalTriangles, cValZero);
                                if (vr.oobCount > 0) Bar("OB", vr.oobCount, vr.totalTriangles, cValOOB);
                                if (vr.texelDensityBadCount > 0) Bar("Txl", vr.texelDensityBadCount, vr.totalTriangles, cValTexel);
                                if (vr.overlapShellPairs > 0) Bar("Ov", vr.overlapTriangleCount, vr.totalTriangles, cValOverlap);
                            }
                        }
                        else if (e.report.HasValue)
                        {
                            var rp = e.report.Value;
                            EditorGUILayout.LabelField("  " + e.renderer.name + " (legacy)", EditorStyles.miniLabel);
                            Bar("OK", rp.accepted, rp.totalTriangles, cAccept);
                            Bar("Ambig", rp.ambiguous, rp.totalTriangles, cAmbig);
                            Bar("Miss", rp.unavoidableMismatch, rp.totalTriangles, cMis);
                            Bar("Rej", rp.rejected, rp.totalTriangles, cReject);
                        }
                        EditorGUILayout.Space(2);
                    }
                }
                EditorGUILayout.EndScrollView();

                // ── Apply to FBX ──
                EditorGUILayout.Space(6);
                H("Apply UV2");
                ColorBtn(new Color(.3f,.85f,.4f), "Apply UV2 to FBX", 26, ApplyUv2ToFbx);
                EditorGUILayout.Space(2);
                ColorBtn(new Color(.9f,.3f,.3f), "Reset UV2 (delete sidecar)", 20, ResetUv2FromFbx);
                EditorGUILayout.Space(2);
                ColorBtn(new Color(.5f,.15f,.15f), "Reset Pipeline State", 20, ResetPipelineState);

                EditorGUILayout.Space(4);
                H("Legacy Save");
                pipeSettings.savePath = EditorGUILayout.TextField("Path", pipeSettings.savePath);
                ColorBtn(new Color(.6f,.5f,.3f), "Save All Mesh Assets", 22, SaveAll);
                if (GUILayout.Button("Update LODGroup Refs", EditorStyles.miniButton)) UpdateRefs();
            }
        }

        void Bar(string label, int n, int total, Color col)
        {
            float pct = total > 0 ? (float)n / total : 0;
            var r = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(.15f,.15f,.15f));
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width * pct, r.height), col);
            var s = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft };
            s.normal.textColor = Color.white;
            EditorGUI.LabelField(new Rect(r.x + 4, r.y, r.width, r.height), label + ": " + n + " (" + (pct * 100).ToString("F0") + "%)", s);
        }

        // ════════════════════════════════════════════════════════════
        //  Canvas Toolbar
        // ════════════════════════════════════════════════════════════

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // ── LOD buttons ──
            if (LodN > 0)
            {
                for (int i = 0; i < LodN; i++)
                {
                    string label = i == sourceLodIndex ? "LOD" + i + "(S)" : "LOD" + i;
                    var bg = GUI.backgroundColor;
                    if (pvLod == i) GUI.backgroundColor = new Color(.35f,.65f,1f);
                    else            GUI.backgroundColor = new Color(.75f,.85f,.95f);
                    if (GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(58)))
                        pvLod = i;
                    GUI.backgroundColor = bg;
                }

                // ── Separator ──
                GUILayout.Space(8);
                var sep = GUILayoutUtility.GetRect(1, 18, GUILayout.Width(1));
                EditorGUI.DrawRect(sep, new Color(.5f,.5f,.5f,.6f));
                GUILayout.Space(8);
            }

            // ── UV channel toggle ──
            {
                var bg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(.85f,.75f,.95f);
                int ci = pvChannel == 0 ? 0 : 1;
                ci = GUILayout.Toolbar(ci, new[]{"UV0","UV2"}, EditorStyles.toolbarButton, GUILayout.Width(80));
                pvChannel = ci == 0 ? 0 : 1;
                GUI.backgroundColor = bg;
            }

            GUILayout.Space(6);

            // ── Fill mode dropdown ──
            EditorGUILayout.LabelField("Fill:", EditorStyles.miniLabel, GUILayout.Width(24));
            fillMode = (FillMode)EditorGUILayout.Popup((int)fillMode, fillModeLabels, EditorStyles.toolbarPopup, GUILayout.Width(80));

            GUILayout.Space(4);

            // ── Overlay toggles ──
            showWire   = GUILayout.Toggle(showWire,   "Wire", EditorStyles.toolbarButton, GUILayout.Width(36));
            showBorder = GUILayout.Toggle(showBorder, "Bdr",  EditorStyles.toolbarButton, GUILayout.Width(30));

            GUILayout.Space(6);

            // ── Zoom + alpha ──
            canvasZoom = EditorGUILayout.Slider(canvasZoom, .1f, 20f, GUILayout.Width(90));
            if (GUILayout.Button("Fit", EditorStyles.toolbarButton, GUILayout.Width(28)))
                FitToUvBounds();
            if (fillMode != FillMode.None)
                fillAlpha = EditorGUILayout.Slider(fillAlpha, .05f, .6f, GUILayout.Width(70));

            GUILayout.Space(6);

            // ── Checker ──
            {
                var bg2 = GUI.backgroundColor;
                if (checkerEnabled) GUI.backgroundColor = new Color(1f,.4f,.3f);
                string ckLbl = checkerEnabled ? "■ Checker" : "▶ Checker";
                if (GUILayout.Button(ckLbl, EditorStyles.toolbarButton, GUILayout.Width(66)))
                    ToggleChecker();
                GUI.backgroundColor = bg2;
            }

            // ── Right side ──
            GUILayout.FlexibleSpace();

            // ── Reset UV2 for selected model (visible when checker active + sidecar found) ──
            if (selectedSidecarPath != null)
            {
                var bg3 = GUI.backgroundColor;
                GUI.backgroundColor = new Color(.95f, .35f, .3f);
                if (GUILayout.Button("Reset UV2: " + selectedResetLabel, EditorStyles.toolbarButton, GUILayout.Width(140)))
                    ResetSelectedUv2();
                GUI.backgroundColor = bg3;
                GUILayout.Space(6);
            }

            // ── Log level ──
            EditorGUILayout.LabelField("Log:", EditorStyles.miniLabel, GUILayout.Width(24));
            var lvl = (UvtLog.Level)EditorGUILayout.EnumPopup(UvtLog.Current, EditorStyles.toolbarPopup, GUILayout.Width(64));
            if (lvl != UvtLog.Current) UvtLog.Current = lvl;

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

            var canvasRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            lastCanvasRect = canvasRect;

            float baseSz = Mathf.Max(64, Mathf.Min(canvasRect.width, canvasRect.height));
            float sz = baseSz * canvasZoom;

            float cx = (canvasRect.width - sz) * 0.5f + canvasPan.x;
            float cy = (canvasRect.height - sz) * 0.5f + canvasPan.y;

            HandleCanvasInput(canvasRect, baseSz, sz, cx, cy);

            if (Event.current.type == EventType.Repaint && glMat != null)
            {
                EditorGUI.DrawRect(canvasRect, new Color(.08f,.08f,.08f));

                int rtW = Mathf.Max(1, (int)canvasRect.width);
                int rtH = Mathf.Max(1, (int)canvasRect.height);
                if (canvasRT == null || canvasRT.width != rtW || canvasRT.height != rtH)
                {
                    if (canvasRT) { canvasRT.Release(); DestroyImmediate(canvasRT); }
                    canvasRT = new RenderTexture(rtW, rtH, 0, RenderTextureFormat.ARGB32);
                    canvasRT.hideFlags = HideFlags.HideAndDontSave;
                }

                var prevRT = RenderTexture.active;
                RenderTexture.active = canvasRT;
                GL.Clear(true, true, new Color(.08f,.08f,.08f, 1f));

                bool push = false;
                try
                {
                    glMat.SetPass(0);
                    GL.PushMatrix(); push = true;
                    GL.LoadPixelMatrix(0, rtW, rtH, 0);

                    GL.Begin(GL.QUADS);
                    GL.Color(new Color(.12f,.12f,.12f));
                    GL.Vertex3(cx, cy, 0); GL.Vertex3(cx + sz, cy, 0);
                    GL.Vertex3(cx + sz, cy + sz, 0); GL.Vertex3(cx, cy + sz, 0);
                    GL.End();

                    // UDIM tile backgrounds (slightly darker)
                    GL.Begin(GL.QUADS);
                    GL.Color(new Color(.10f,.10f,.10f));
                    for (int tu = -1; tu <= 3; tu++)
                    for (int tv = -1; tv <= 3; tv++)
                    {
                        if (tu == 0 && tv == 0) continue;
                        float tx = cx + tu * sz, ty = cy - tv * sz;
                        GL.Vertex3(tx, ty, 0); GL.Vertex3(tx + sz, ty, 0);
                        GL.Vertex3(tx + sz, ty + sz, 0); GL.Vertex3(tx, ty + sz, 0);
                    }
                    GL.End();

                    GlGrid(cx, cy, sz);

                    foreach (var item in draws)
                    {
                        Mesh mesh = item.Item1;
                        MeshEntry entry = item.Item2;
                        int idx = item.Item3;

                        var uvs = RdUv(mesh, pvChannel);
                        var tri = mesh.triangles;
                        if (uvs == null || tri == null) continue;
                        int uN = uvs.Length, fN = tri.Length / 3;

                        TriangleStatus[] stats = entry.transferState?.triangleStatus;
                        HashSet<int> bdr = showBorder ? entry.transferState?.borderPrimitiveIds : null;

                        // Fill
                        switch (fillMode)
                        {
                            case FillMode.ShellMatch when entry.shellTransferResult?.vertexToSourceShell != null:
                                GlFillShellMatch(cx,cy,sz, uvs,tri,fN,uN, entry.shellTransferResult.vertexToSourceShell);
                                break;
                            case FillMode.Validation when entry.validationReport?.perTriangle != null:
                                GlFillValidation(cx,cy,sz, uvs,tri,fN,uN, entry.validationReport.perTriangle);
                                break;
                            case FillMode.Status when stats != null:
                                GlFillSt(cx,cy,sz, uvs,tri,fN,uN, stats);
                                break;
                            case FillMode.Shells:
                                GlFillSh(cx,cy,sz, uvs,tri,fN,uN, idx);
                                break;
                            case FillMode.None:
                                break;
                            default:
                                // fallback: show shells if requested mode has no data
                                if (fillMode != FillMode.None)
                                    GlFillSh(cx,cy,sz, uvs,tri,fN,uN, idx);
                                break;
                        }

                        if (bdr != null && bdr.Count > 0) GlBdr(cx,cy,sz, uvs,tri,fN,uN, bdr);
                        if (showWire) GlWr(cx,cy,sz, uvs,tri,fN,uN);
                    }
                }
                catch (Exception ex) { UvtLog.Warn("[UV] GL: " + ex.Message); }
                finally { if (push) GL.PopMatrix(); }

                RenderTexture.active = prevRT;
                GUI.DrawTexture(canvasRect, canvasRT, ScaleMode.StretchToFill, false);
            }
        }

        void FitToUvBounds()
        {
            var ee = ForLod(pvLod);
            float minU = 0f, maxU = 1f, minV = 0f, maxV = 1f;
            bool any = false;
            foreach (var entry in ee)
            {
                var mesh = DMesh(entry);
                if (mesh == null) continue;
                var uvs = RdUv(mesh, pvChannel);
                if (uvs == null) continue;
                foreach (var uv in uvs)
                {
                    if (!UOk(uv)) continue;
                    if (!any) { minU = maxU = uv.x; minV = maxV = uv.y; any = true; }
                    else { if (uv.x < minU) minU = uv.x; if (uv.x > maxU) maxU = uv.x; if (uv.y < minV) minV = uv.y; if (uv.y > maxV) maxV = uv.y; }
                }
            }
            float pad = 0.05f;
            minU -= pad; maxU += pad; minV -= pad; maxV += pad;
            float rangeU = Mathf.Max(maxU - minU, 0.1f);
            float rangeV = Mathf.Max(maxV - minV, 0.1f);
            float W = lastCanvasRect.width, H = lastCanvasRect.height;
            if (W < 1 || H < 1) { canvasZoom = 1f; canvasPan = Vector2.zero; return; }
            float baseSz = Mathf.Max(64, Mathf.Min(W, H));
            canvasZoom = Mathf.Clamp(Mathf.Min(W / (baseSz * rangeU), H / (baseSz * rangeV)), 0.1f, 20f);
            float sz = baseSz * canvasZoom;
            float centerU = (minU + maxU) * 0.5f;
            float centerV = (minV + maxV) * 0.5f;
            canvasPan.x = sz * (0.5f - centerU);
            canvasPan.y = sz * (centerV - 0.5f);
            Repaint();
        }

        void HandleCanvasInput(Rect canvasRect, float baseSz, float sz, float cx, float cy)
        {
            var e = Event.current;

            if (e.type == EventType.ScrollWheel && canvasRect.Contains(e.mousePosition))
            {
                float oldZoom = canvasZoom;
                float oldSz = baseSz * oldZoom;
                float factor = e.delta.y > 0 ? 0.9f : 1.1f;
                canvasZoom = Mathf.Clamp(canvasZoom * factor, 0.1f, 20f);
                float newSz = baseSz * canvasZoom;

                Vector2 local = e.mousePosition - canvasRect.position;
                float mx = local.x - cx;
                float my = local.y - cy;
                float newCx = local.x - mx * (newSz / oldSz);
                float newCy = local.y - my * (newSz / oldSz);
                canvasPan.x = newCx - (canvasRect.width - newSz) * 0.5f;
                canvasPan.y = newCy - (canvasRect.height - newSz) * 0.5f;

                e.Use(); Repaint();
            }

            bool startPan = canvasRect.Contains(e.mousePosition) &&
                            ((e.type == EventType.MouseDown && e.button == 2) ||
                             (e.type == EventType.MouseDown && e.button == 0 && e.alt));
            if (startPan) { canvasPanning = true; e.Use(); }
            if (e.type == EventType.MouseDrag && canvasPanning) { canvasPan += e.delta; e.Use(); Repaint(); }
            if (e.rawType == EventType.MouseUp && (e.button == 2 || e.button == 0)) canvasPanning = false;

            if (e.type == EventType.MouseDown && e.button == 2 && e.clickCount == 2 && canvasRect.Contains(e.mousePosition))
            { FitToUvBounds(); e.Use(); }
        }

        // ── GL helpers ──

        static Mesh MakeReadableCopy(Mesh src)
        {
            var dst = new Mesh();
            dst.indexFormat = src.indexFormat;
            dst.SetVertices(new List<Vector3>(src.vertices));
            if (src.normals  != null && src.normals.Length  > 0) dst.SetNormals(new List<Vector3>(src.normals));
            if (src.tangents != null && src.tangents.Length > 0) dst.SetTangents(new List<Vector4>(src.tangents));
            if (src.colors   != null && src.colors.Length   > 0) dst.SetColors(new List<Color>(src.colors));
            if (src.boneWeights != null && src.boneWeights.Length > 0) dst.boneWeights = src.boneWeights;
            if (src.bindposes   != null && src.bindposes.Length   > 0) dst.bindposes   = src.bindposes;
            for (int ch = 0; ch < 8; ch++)
            {
                if (ch == 2) continue;
                var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
                if (!src.HasVertexAttribute(attr)) continue;
                int dim = src.GetVertexAttributeDimension(attr);
                if (dim <= 2)      { var uv = new List<Vector2>(); src.GetUVs(ch, uv); if (uv.Count > 0) dst.SetUVs(ch, uv); }
                else if (dim == 3) { var uv = new List<Vector3>(); src.GetUVs(ch, uv); if (uv.Count > 0) dst.SetUVs(ch, uv); }
                else               { var uv = new List<Vector4>(); src.GetUVs(ch, uv); if (uv.Count > 0) dst.SetUVs(ch, uv); }
            }
            dst.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++)
                dst.SetTriangles(src.GetTriangles(s), s);
            dst.bounds = src.bounds;
            return dst;
        }

        static Vector2[] RdUv(Mesh m, int ch) { var l = new List<Vector2>(); m.GetUVs(ch, l); return l.Count > 0 ? l.ToArray() : null; }
        static bool UOk(Vector2 u) => u.x >= UV_LO && u.x <= UV_HI && u.y >= UV_LO && u.y <= UV_HI && !float.IsNaN(u.x) && !float.IsNaN(u.y) && !float.IsInfinity(u.x) && !float.IsInfinity(u.y);
        static bool TOk(Vector2[] u, int n, int a, int b, int c) => a>=0&&a<n&&b>=0&&b<n&&c>=0&&c<n && UOk(u[a])&&UOk(u[b])&&UOk(u[c]);
        static void Vx(float ox, float oy, float sz, Vector2 u) => GL.Vertex3(ox+u.x*sz, oy+(1f-u.y)*sz, 0);

        void GlGrid(float ox, float oy, float sz)
        {
            // Main 0-1 tile grid
            GL.Begin(GL.LINES);
            GL.Color(new Color(.25f,.25f,.25f));
            for (int g = 0; g <= 4; g++) { float p = g*.25f*sz; GL.Vertex3(ox+p,oy,0); GL.Vertex3(ox+p,oy+sz,0); GL.Vertex3(ox,oy+p,0); GL.Vertex3(ox+sz,oy+p,0); }
            GL.Color(new Color(.5f,.5f,.5f));
            GL.Vertex3(ox,oy,0); GL.Vertex3(ox+sz,oy,0);
            GL.Vertex3(ox+sz,oy,0); GL.Vertex3(ox+sz,oy+sz,0);
            GL.Vertex3(ox+sz,oy+sz,0); GL.Vertex3(ox,oy+sz,0);
            GL.Vertex3(ox,oy+sz,0); GL.Vertex3(ox,oy,0);
            // Adjacent UDIM tiles (faded outlines)
            GL.Color(new Color(.3f,.3f,.3f,.4f));
            for (int tu = -1; tu <= 3; tu++)
            for (int tv = -1; tv <= 3; tv++)
            {
                if (tu == 0 && tv == 0) continue;
                float tx = ox + tu * sz, ty = oy - tv * sz;
                GL.Vertex3(tx,ty,0); GL.Vertex3(tx+sz,ty,0);
                GL.Vertex3(tx+sz,ty,0); GL.Vertex3(tx+sz,ty+sz,0);
                GL.Vertex3(tx+sz,ty+sz,0); GL.Vertex3(tx,ty+sz,0);
                GL.Vertex3(tx,ty+sz,0); GL.Vertex3(tx,ty,0);
            }
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

        void GlFillShellMatch(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, int[] vertShellMap)
        {
            if (vertShellMap == null) return;
            int tot = 0, b = 0; Color cc = Color.clear;
            GL.Begin(GL.TRIANGLES);
            for (int f = 0; f < fN && tot < MAX_TRI; f++)
            {
                int a0 = t[f*3], a1 = t[f*3+1], a2 = t[f*3+2];
                if (!TOk(uv, uN, a0, a1, a2)) continue;
                int sh = (a0 < vertShellMap.Length) ? vertShellMap[a0] : -1;
                Color nc = sh < 0 ? new Color(0.3f, 0.3f, 0.3f, fillAlpha) :
                    new Color(pal[sh % pal.Length].r, pal[sh % pal.Length].g, pal[sh % pal.Length].b, fillAlpha * 1.5f);
                if (nc.r != cc.r || nc.g != cc.g || nc.b != cc.b)
                { GL.End(); GL.Begin(GL.TRIANGLES); cc = nc; GL.Color(cc); b = 0; }
                Vx(ox, oy, sz, uv[a0]); Vx(ox, oy, sz, uv[a1]); Vx(ox, oy, sz, uv[a2]);
                tot++; b++;
                if (b >= BATCH) { GL.End(); GL.Begin(GL.TRIANGLES); GL.Color(cc); b = 0; }
            }
            GL.End();
        }

        void GlFillValidation(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, TransferValidator.TriIssue[] perTri)
        {
            if (perTri == null) return;
            int tot = 0, b = 0; Color cc = Color.clear;
            GL.Begin(GL.TRIANGLES);
            for (int f = 0; f < fN && tot < MAX_TRI; f++)
            {
                int a0 = t[f*3], a1 = t[f*3+1], a2 = t[f*3+2];
                if (!TOk(uv, uN, a0, a1, a2)) continue;
                var fl = (f < perTri.Length) ? perTri[f] : TransferValidator.TriIssue.None;
                Color nc;
                if      ((fl & TransferValidator.TriIssue.ZeroArea) != 0)  nc = cValZero;
                else if ((fl & TransferValidator.TriIssue.Stretched) != 0) nc = cValStretch;
                else if ((fl & TransferValidator.TriIssue.Overlap) != 0)   nc = cValOverlap;
                else if ((fl & TransferValidator.TriIssue.OutOfBounds) != 0) nc = cValOOB;
                else if ((fl & TransferValidator.TriIssue.TexelDensity) != 0) nc = cValTexel;
                else nc = cValClean;
                if (nc.r != cc.r || nc.g != cc.g || nc.b != cc.b)
                { GL.End(); GL.Begin(GL.TRIANGLES); cc = nc; GL.Color(cc); b = 0; }
                Vx(ox, oy, sz, uv[a0]); Vx(ox, oy, sz, uv[a1]); Vx(ox, oy, sz, uv[a2]);
                tot++; b++;
                if (b >= BATCH) { GL.End(); GL.Begin(GL.TRIANGLES); GL.Color(cc); b = 0; }
            }
            GL.End();
        }

        static Color SC(TriangleStatus s)
        {
            switch (s)
            {
                case TriangleStatus.Accepted: return cAccept;
                case TriangleStatus.Ambiguous: return cAmbig;
                case TriangleStatus.BorderRisk: return new Color(1f,.5f,.1f,.5f);
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
            var ee = ForLod(pvLod);
            int tV = 0, tT = 0;
            foreach (var e in ee) { Mesh m = DMesh(e); if (m == null) continue; tV += m.vertexCount; tT += m.triangles.Length / 3; }
            EditorGUILayout.LabelField("LOD" + pvLod + " | " + ee.Count + " mesh | V:" + tV + " T:" + tT + " | " + (pvChannel == 0 ? "UV0" : "UV2"), EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            switch (fillMode)
            {
                case FillMode.Validation:
                    Sw("✓", cValClean); Sw("Str", cValStretch); Sw("0A", cValZero); Sw("OB", cValOOB); Sw("Txl", cValTexel); Sw("Ov", cValOverlap);
                    break;
                case FillMode.ShellMatch:
                    EditorGUILayout.LabelField("Shell Match: color = source shell", EditorStyles.miniLabel);
                    break;
                case FillMode.Status:
                    Sw("Ok", cAccept); Sw("Am", cAmbig); Sw("Mi", cMis); Sw("Rj", cReject);
                    break;
            }
            EditorGUILayout.EndHorizontal();
        }

        void Sw(string l, Color c)
        {
            var r = GUILayoutUtility.GetRect(9, 9, GUILayout.Width(9));
            EditorGUI.DrawRect(r, c);
            EditorGUILayout.LabelField(l, EditorStyles.miniLabel, GUILayout.Width(18));
        }

        Mesh DMesh(MeshEntry e)
        {
            if (e.lodIndex == sourceLodIndex && e.repackedMesh != null && pvChannel == 1) return e.repackedMesh;
            if (e.lodIndex != sourceLodIndex && e.transferredMesh != null && pvChannel == 1) return e.transferredMesh;
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
                if (report.flippedTriangles > 0 || report.degenerateTriangles > 0)
                    UvtLog.Info($"[UV0Analyze]   '{e.originalMesh.name}' LOD{e.lodIndex}: {report.flippedTriangles} flipped, {report.degenerateTriangles} degen, {report.totalShells} shells, {e.originalMesh.vertexCount} verts");
            }
            uv0Analyzed = true;
            int totalFalseSeams = 0, totalDegen = 0, totalFlipped = 0;
            foreach (var kv in uv0Reports)
            {
                totalFalseSeams += kv.Value.falseSeamPairs;
                totalDegen += kv.Value.degenerateTriangles;
                totalFlipped += kv.Value.flippedTriangles;
            }
            UvtLog.Info($"[UV0Analyze] {uv0Reports.Count} meshes: {totalFalseSeams} false seams, {totalDegen} degenerate, {totalFlipped} flipped");
            Repaint();
        }

        void ExecWeldUv0()
        {
            if (lodGroup == null) return;

            int meshoptOptimized = 0;
            foreach (var e in meshEntries)
            {
                if (!e.include || e.originalMesh == null) continue;
                var copy = MakeReadableCopy(e.originalMesh);
                copy.name = e.originalMesh.name + "_optimized";
                var optResult = MeshOptimizer.Optimize(copy);
                if (optResult.ok)
                {
                    e.originalMesh = copy;
                    e.wasWelded = true;
                    meshoptOptimized++;
                }
                else
                {
                    DestroyImmediate(copy);
                }
            }

            int edgeWelded = 0;
            foreach (var e in meshEntries)
            {
                if (!e.include || e.originalMesh == null) continue;
                var welded = Uv0Analyzer.UvEdgeWeld(e.originalMesh);
                if (welded != null && welded != e.originalMesh)
                {
                    e.originalMesh = welded;
                    e.wasEdgeWelded = true;
                    edgeWelded++;
                }
            }

            uv0Welded = meshoptOptimized > 0 || edgeWelded > 0;
            UvtLog.Info($"[UV0Fix] Optimized: {meshoptOptimized} meshopt, {edgeWelded} UV edge weld");

            ExecAnalyzeUv0();
        }

        void ExecSymmetrySplit(bool includeTargets)
        {
            lastSymmetrySplitLods.Clear();
            int totalSplit = 0;
            int sourceSplit = 0;
            int targetSplit = 0;
            foreach (var e in meshEntries)
            {
                if (!e.include || e.originalMesh == null) continue;
                e.wasSymmetrySplit = false;
                bool isSource = e.lodIndex == sourceLodIndex;
                if (!isSource && !includeTargets) continue;
                var mesh = e.originalMesh;
                var uv0 = mesh.uv;
                if (uv0 == null || uv0.Length == 0) continue;
                var shells = UvShellExtractor.Extract(uv0, mesh.triangles);
                int n = SymmetrySplitShells.Split(mesh, shells);
                if (n <= 0) continue;
                e.wasSymmetrySplit = true;
                totalSplit += n;
                if (isSource) sourceSplit += n;
                else targetSplit += n;
                lastSymmetrySplitLods.Add(e.lodIndex);
            }

            if (totalSplit > 0)
            {
                if (includeTargets)
                    UvtLog.Info($"[Pipeline] Symmetry split: source LOD{sourceLodIndex}={sourceSplit}, targets={targetSplit}, total={totalSplit}; modified LODs: {string.Join(",", lastSymmetrySplitLods.OrderBy(i => i))}");
                else
                    UvtLog.Info($"[Pipeline] Symmetry split: source LOD{sourceLodIndex}={sourceSplit}, targets=0 (disabled), total={totalSplit}; modified LODs: {string.Join(",", lastSymmetrySplitLods.OrderBy(i => i))}");
            }
            else
            {
                UvtLog.Info($"[Pipeline] Symmetry split: no shell splits (mode: {(includeTargets ? "source+targets" : "source only")}).");
            }

            if (includeTargets && targetSplit > 0)
                UvtLog.Warn("[Pipeline] Target LOD topology was modified by symmetry split; verify Transfer/Apply result stability.");
        }

        // ════════════════════════════════════════════════════════════
        //  Full Pipeline
        // ════════════════════════════════════════════════════════════

        void ExecFullPipeline()
        {
            if (lodGroup == null) { UvtLog.Warn("[Pipeline] No LODGroup assigned."); return; }
            if (meshEntries.Count == 0) { UvtLog.Warn("[Pipeline] No meshes found."); return; }

            try
            {
                EditorUtility.DisplayProgressBar("Full Pipeline", "Step 1/5: Analyze UV0...", 0.05f);
                ExecAnalyzeUv0();

                EditorUtility.DisplayProgressBar("Full Pipeline", "Step 2/5: Weld...", 0.15f);
                bool hasTargetLods = meshEntries.Any(e => e.include && e.lodIndex != sourceLodIndex);
                bool anyIssues = uv0Reports.Values.Any(r => r.falseSeamPairs > 0);
                if ((anyIssues || hasTargetLods) && !uv0Welded)
                    ExecWeldUv0();

                string splitModeLabel = splitTargetsInSymmetryStep ? "source + targets" : "source only";
                EditorUtility.DisplayProgressBar("Full Pipeline", "Step 3/5: Symmetry split (" + splitModeLabel + ")...", 0.25f);
                ExecSymmetrySplit(splitTargetsInSymmetryStep);

                EditorUtility.DisplayProgressBar("Full Pipeline", "Step 4/5: Repack...", 0.40f);
                var src = ForLod(sourceLodIndex);
                ExecRepack(src);

                if (!hasRepack)
                {
                    UvtLog.Error("[Pipeline] Repack failed — aborting.");
                    return;
                }

                EditorUtility.DisplayProgressBar("Full Pipeline", "Step 5/5: Transfer...", 0.6f);
                ExecTransferAll();

                tab = Tab.Transfer;
                pvChannel = 1;
                fillMode = FillMode.Validation;
                // Switch to first target LOD for preview
                for (int i = 0; i < LodN; i++)
                    if (i != sourceLodIndex && ForLod(i).Count > 0) { pvLod = i; break; }

                UvtLog.Info("[Pipeline] Full pipeline complete.");
            }
            catch (Exception ex)
            {
                UvtLog.Error("[Pipeline] " + ex);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Pipeline Steps
        // ════════════════════════════════════════════════════════════

        void ExecRepack(List<MeshEntry> entries)
        {
            try
            {
                var validEntries = new List<MeshEntry>();
                var meshCopies = new List<Mesh>();
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i]; var mesh = e.originalMesh; if (mesh == null) continue;
                    var uv0 = mesh.uv;
                    if (uv0 == null || uv0.Length == 0) { UvtLog.Warn("[Repack] " + e.renderer.name + ": no UV0"); continue; }
                    var cp = Instantiate(mesh); cp.name = mesh.name + "_repack";
                    validEntries.Add(e);
                    meshCopies.Add(cp);
                }
                if (meshCopies.Count == 0) return;

                EditorUtility.DisplayProgressBar("Repack", "Packing " + meshCopies.Count + " meshes...", 0.5f);

                var opts = RepackOptions.Default;
                opts.resolution = (uint)atlasResolution;
                opts.padding = (uint)shellPaddingPx;
                opts.borderPadding = (uint)borderPaddingPx;

                var results = XatlasRepack.RepackMulti(meshCopies.ToArray(), opts);

                for (int i = 0; i < validEntries.Count; i++)
                {
                    var e = validEntries[i];
                    if (!results[i].ok)
                    {
                        UvtLog.Error("[Repack] " + e.renderer.name + ": " + results[i].error);
                        DestroyImmediate(meshCopies[i]);
                        continue;
                    }
                    e.repackedMesh = meshCopies[i];
                }
                hasRepack = ForLod(sourceLodIndex).Any(e => e.repackedMesh != null);
            }
            catch (Exception ex) { UvtLog.Error("[Repack] " + ex); }
            finally { EditorUtility.ClearProgressBar(); }
            Repaint();
        }

        void ExecTransferAll()
        {
            var src = ForLod(sourceLodIndex).Where(e => e.include).ToList();
            int srcWithRepack = src.Count(e => e.repackedMesh != null);
            UvtLog.Info($"[Transfer] Source layout: LOD{sourceLodIndex}, repacked {srcWithRepack}/{src.Count} mesh(es).");
            if (splitTargetsInSymmetryStep && lastSymmetrySplitLods.Any(i => i != sourceLodIndex))
                UvtLog.Warn($"[Transfer] Target LODs modified by SymSplit: {string.Join(",", lastSymmetrySplitLods.Where(i => i != sourceLodIndex).OrderBy(i => i))}. Mapping stability may be lower.");

            for (int li = 0; li < LodN; li++)
                if (li != sourceLodIndex) ExecTransferLod(li);
        }

        Dictionary<int, GroupedShellTransfer.SourceShellInfo[]> shellTransformCache =
            new Dictionary<int, GroupedShellTransfer.SourceShellInfo[]>();

        void ExecTransferLod(int tLod)
        {
            var srcE = ForLod(sourceLodIndex); var tgtE = ForLod(tLod);
            if (srcE.Count == 0 || tgtE.Count == 0) return;
            try
            {
                for (int ti = 0; ti < tgtE.Count; ti++)
                {
                    var te = tgtE[ti];
                    // Match source by group key first (isolates InnerDoor_A_01 from InnerDoor_A_02
                    // when they share the same LODGroup). Fall back to index if key is missing.
                    MeshEntry se = null;
                    if (!string.IsNullOrEmpty(te.meshGroupKey))
                        se = srcE.FirstOrDefault(s => s.meshGroupKey == te.meshGroupKey);
                    if (se == null)
                        se = ti < srcE.Count ? srcE[ti] : srcE[0];
                    Mesh sM = se.repackedMesh ?? se.originalMesh; Mesh tM = te.originalMesh;
                    if (sM == null || tM == null) continue;

                    if (te.transferredMesh != null && te.transferredMesh.vertexCount != tM.vertexCount)
                        UvtLog.Warn($"[Transfer] LOD{tLod}/{te.renderer.name}: previous transferred mesh vertex count differs from target source mesh.");

                    EditorUtility.DisplayProgressBar("Transfer", "LOD" + tLod + ": " + te.renderer.name, .1f + .8f * ti / tgtE.Count);

                    int id = sM.GetInstanceID();
                    if (!shellTransformCache.TryGetValue(id, out var srcInfos))
                    {
                        srcInfos = GroupedShellTransfer.AnalyzeSource(sM);
                        if (srcInfos != null) shellTransformCache[id] = srcInfos;
                    }
                    if (srcInfos == null) continue;

                    var tr = GroupedShellTransfer.Transfer(tM, sM);
                    if (tr.uv2 == null) continue;

                    var om = Instantiate(tM); om.name = tM.name + "_uvTransfer";
                    om.SetUVs(pipeSettings.targetUvChannel, new List<Vector2>(tr.uv2));
                    te.transferredMesh = om;
                    te.transferState = null;
                    te.report = null;
                    te.shellTransferResult = tr;

                    te.validationReport = TransferValidator.Validate(tM, tr.uv2, tr);
                    TransferValidator.DetectUv2Overlaps(tM, tr.uv2, te.validationReport, tr);
                }
                hasTransfer = tgtE.Any(e => e.transferredMesh != null);
            }
            catch (Exception ex) { UvtLog.Error("[Transfer] LOD" + tLod + ": " + ex); }
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
            int n = 0;
            foreach (var e in meshEntries)
            {
                Mesh m = e.lodIndex == sourceLodIndex ? e.repackedMesh : e.transferredMesh;
                if (m == null) continue;
                string ap = AssetDatabase.GenerateUniqueAssetPath(p + "/" + m.name + ".asset");
                AssetDatabase.CreateAsset(m, ap); n++;
            }
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            UvtLog.Info("[Save] " + n + " assets -> " + p);
        }

        void UpdateRefs()
        {
            if (lodGroup == null) return; int n = 0;
            foreach (var e in meshEntries)
            {
                Mesh m = e.lodIndex == sourceLodIndex ? e.repackedMesh : e.transferredMesh;
                if (m == null || e.meshFilter == null) continue;
                Undo.RecordObject(e.meshFilter, "UV Transfer"); e.meshFilter.sharedMesh = m; n++;
            }
            UvtLog.Info("[Save] " + n + " refs updated");
        }

        // ════════════════════════════════════════════════════════════
        //  Checker Preview
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Re-apply checker to the currently selected object.
        /// First tries meshEntries (loaded LODGroup with working copies),
        /// then falls back to scanning the selected GameObject directly
        /// for FBX meshes that already have UV2 baked in (post-Apply).
        /// </summary>
        void ReapplyCheckerToSelection()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;

            var entries = new List<(Renderer renderer, Mesh meshWithUv2)>();

            // Check if selected object belongs to the loaded LODGroup
            bool inLoadedGroup = lodGroup != null &&
                (go.transform == lodGroup.transform || go.transform.IsChildOf(lodGroup.transform));

            // 1) From meshEntries — working copies with UV2 (repack/transfer/applied FBX)
            if (inLoadedGroup)
            {
                foreach (var e in meshEntries)
                {
                    if (!e.include || e.renderer == null) continue;
                    Mesh uvMesh = e.transferredMesh ?? e.repackedMesh;
                    if (uvMesh == null)
                    {
                        Mesh fallback = e.originalMesh ?? e.fbxMesh;
                        if (fallback != null)
                        {
                            var testUv2 = new List<Vector2>();
                            fallback.GetUVs(1, testUv2);
                            if (testUv2.Count > 0) uvMesh = fallback;
                        }
                    }
                    if (uvMesh != null) entries.Add((e.renderer, uvMesh));
                }
            }

            // 2) Scan selected object directly — for objects not in loaded LODGroup,
            //    or if meshEntries had no UV2 (e.g. fresh Refresh after LODGroup switch)
            if (entries.Count == 0)
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var testUv2 = new List<Vector2>();
                    mf.sharedMesh.GetUVs(1, testUv2);
                    if (testUv2.Count > 0)
                        entries.Add((r, null)); // null = keep current mesh, just swap material
                }
            }

            // Always restore old visuals first
            CheckerTexturePreview.Restore();

            // Apply to new selection if it has UV2; otherwise just clear visuals
            // (checkerEnabled stays true — user must toggle off manually)
            if (entries.Count > 0)
                CheckerTexturePreview.Apply(entries);
        }

        void ToggleChecker()
        {
            if (checkerEnabled)
            {
                checkerEnabled = false;
                CheckerTexturePreview.Restore();
                Repaint();
                return;
            }

            checkerEnabled = true;

            var entries = new List<(Renderer renderer, Mesh meshWithUv2)>();
            foreach (var e in meshEntries)
            {
                if (!e.include || e.renderer == null) continue;
                Mesh uvMesh = e.transferredMesh ?? e.repackedMesh;
                if (uvMesh == null)
                {
                    Mesh fallback = e.originalMesh ?? e.fbxMesh;
                    if (fallback != null)
                    {
                        var testUv2 = new List<Vector2>();
                        fallback.GetUVs(1, testUv2);
                        if (testUv2.Count > 0) uvMesh = fallback;
                    }
                }
                if (uvMesh != null) entries.Add((e.renderer, uvMesh));
            }

            if (entries.Count == 0)
            {
                UvtLog.Warn("[Checker] No meshes with UV2. Run Repack first.");
                return;
            }
            CheckerTexturePreview.Apply(entries);
            UpdateSelectedSidecar();
            Repaint();
        }

        // ════════════════════════════════════════════════════════════
        //  Apply UV2 to FBX
        // ════════════════════════════════════════════════════════════

        // ── Sidecar entry for ApplyUv2ToFbx ──
        struct SidecarEntry
        {
            public string name;
            public Vector2[] uv2;
            public bool welded, edgeWelded;
            public bool symmetrySplit;
            public Vector3[] positions;
            public Vector2[] uv0;
            // Deterministic replay (variant B)
            public int[] vertexRemap;
            public int optimizedVertexCount;
            public int[] optimizedTriangles;
            public int[] submeshTriangleCounts;
            // Schema & provenance (v0.12.0+)
            public int schemaVersion;
            public string toolVersion;
            public MeshFingerprint sourceFingerprint;
            public int targetUvChannel;
            public bool stepMeshopt;
            public bool stepEdgeWeld;
            public bool stepSymmetrySplit;
            public bool stepRepack;
            public bool stepTransfer;
            public bool hasReplayData;
            // Orphan vertex data (boundary verts with no raw FBX counterpart)
            public int[] orphanIndices;
            public Vector3[] orphanPositions;
            public Vector3[] orphanNormals;
            public Vector4[] orphanTangents;
            public Vector2[] orphanUv0;
        }

        void ApplyUv2ToFbx()
        {
            if (lodGroup == null) return;

            var fbxGroups = new Dictionary<string, List<SidecarEntry>>();

            foreach (var e in meshEntries)
            {
                if (!e.include) continue;
                Mesh resultMesh = e.lodIndex == sourceLodIndex ? e.repackedMesh : e.transferredMesh;
                if (resultMesh == null) continue;

                Mesh pathMesh = e.fbxMesh ?? e.originalMesh;
                string fbxPath = AssetDatabase.GetAssetPath(pathMesh);
                if (string.IsNullOrEmpty(fbxPath)) continue;

                var uv2List = new List<Vector2>();
                resultMesh.GetUVs(pipeSettings.targetUvChannel, uv2List);
                if (uv2List.Count == 0) continue;

                var positions = resultMesh.vertices;
                var uv0List = new List<Vector2>();
                (e.originalMesh ?? resultMesh).GetUVs(0, uv0List);

                if (!fbxGroups.ContainsKey(fbxPath))
                    fbxGroups[fbxPath] = new List<SidecarEntry>();

                string meshName = e.fbxMesh != null ? e.fbxMesh.name : e.originalMesh.name;

                var sidecar = new SidecarEntry
                {
                    name = meshName,
                    uv2 = uv2List.ToArray(),
                    welded = e.wasWelded,
                    edgeWelded = e.wasEdgeWelded,
                    symmetrySplit = e.wasSymmetrySplit,
                    positions = positions,
                    uv0 = uv0List.ToArray(),
                    // Schema & provenance
                    schemaVersion = Uv2DataAsset.CurrentSchemaVersion,
                    toolVersion = Uv2DataAsset.ToolVersionStr,
                    sourceFingerprint = e.fbxMesh != null ? MeshFingerprint.Compute(e.fbxMesh) : null,
                    targetUvChannel = pipeSettings.targetUvChannel,
                    stepMeshopt = e.wasWelded,
                    stepEdgeWeld = e.wasEdgeWelded,
                    stepSymmetrySplit = e.wasSymmetrySplit,
                    stepRepack = (e.lodIndex == sourceLodIndex),
                    stepTransfer = (e.lodIndex != sourceLodIndex),
                };

                // Build deterministic replay data when topology-affecting steps were applied
                if ((e.wasWelded || e.wasEdgeWelded || e.wasSymmetrySplit) && e.fbxMesh != null)
                {
                    // optimizedMesh = the mesh after MeshOptimizer + UvEdgeWeld
                    // For source LOD: repackedMesh has UV2 written on top of optimized geometry
                    // For target LODs: transferredMesh has UV2 on top of optimized geometry
                    // In both cases, vertex layout matches e.originalMesh (the optimized working copy)
                    Mesh optimizedMesh = e.originalMesh;

                    sidecar.vertexRemap = BuildVertexRemap(e.fbxMesh, optimizedMesh);
                    sidecar.optimizedVertexCount = optimizedMesh.vertexCount;
                    BuildTriangleData(optimizedMesh, out sidecar.optimizedTriangles, out sidecar.submeshTriangleCounts);
                    sidecar.hasReplayData = (sidecar.vertexRemap != null);

                    // ── Detect orphan vertices (optimized indices not covered by any remap entry) ──
                    if (sidecar.vertexRemap != null && sidecar.optimizedVertexCount > 0)
                    {
                        int optVCount = sidecar.optimizedVertexCount;
                        var covered = new bool[optVCount];
                        for (int i = 0; i < sidecar.vertexRemap.Length; i++)
                        {
                            int dst = sidecar.vertexRemap[i];
                            if (dst >= 0 && dst < optVCount)
                                covered[dst] = true;
                        }

                        var orphanList = new List<int>();
                        for (int j = 0; j < optVCount; j++)
                            if (!covered[j]) orphanList.Add(j);

                        if (orphanList.Count > 0)
                        {
                            var optPos = optimizedMesh.vertices;
                            var optNorm = optimizedMesh.normals;
                            var optTan = optimizedMesh.tangents;
                            var optUv0List = new List<Vector2>();
                            optimizedMesh.GetUVs(0, optUv0List);

                            sidecar.orphanIndices = orphanList.ToArray();
                            sidecar.orphanPositions = new Vector3[orphanList.Count];
                            sidecar.orphanNormals = optNorm != null && optNorm.Length > 0 ? new Vector3[orphanList.Count] : null;
                            sidecar.orphanTangents = optTan != null && optTan.Length > 0 ? new Vector4[orphanList.Count] : null;
                            sidecar.orphanUv0 = optUv0List.Count > 0 ? new Vector2[orphanList.Count] : null;

                            for (int k = 0; k < orphanList.Count; k++)
                            {
                                int idx = orphanList[k];
                                sidecar.orphanPositions[k] = optPos[idx];
                                if (sidecar.orphanNormals != null) sidecar.orphanNormals[k] = optNorm[idx];
                                if (sidecar.orphanTangents != null) sidecar.orphanTangents[k] = optTan[idx];
                                if (sidecar.orphanUv0 != null && idx < optUv0List.Count) sidecar.orphanUv0[k] = optUv0List[idx];
                            }

                            UvtLog.Info($"[Apply] '{meshName}': {orphanList.Count} orphan vertices stored");
                        }
                    }

                    // Skip large position/UV0 arrays when replay data is present —
                    // ReplayOptimization + orphan fill handles all vertex mapping deterministically.
                    if (sidecar.hasReplayData)
                    {
                        sidecar.positions = null;
                        sidecar.uv0 = null;
                    }

                    UvtLog.Verbose($"[Apply] '{meshName}': remap {e.fbxMesh.vertexCount}→{optimizedMesh.vertexCount} " +
                                  $"({sidecar.optimizedTriangles.Length / 3} tris, {optimizedMesh.subMeshCount} submeshes), " +
                                  $"replaySteps=meshopt:{e.wasWelded}, edgeWeld:{e.wasEdgeWelded}, symmetrySplit:{e.wasSymmetrySplit}");
                }

                // Replay-mandatory warning: modified mesh without replay data
                if ((e.wasWelded || e.wasEdgeWelded || e.wasSymmetrySplit) && sidecar.vertexRemap == null)
                {
                    UvtLog.Warn($"[Apply] '{meshName}': mesh was modified (welded={e.wasWelded}, edgeWelded={e.wasEdgeWelded}, symmetrySplit={e.wasSymmetrySplit}) but no replay data — " +
                                "reimport will use non-deterministic legacy path!");
                }

                fbxGroups[fbxPath].Add(sidecar);
            }

            if (fbxGroups.Count == 0)
            {
                UvtLog.Warn("[Apply] No meshes with UV2 data.");
                return;
            }

            int totalMeshes = 0;
            try
            {
                foreach (var kv in fbxGroups)
                {
                    string fbxPath = kv.Key;
                    string sidecarPath = Uv2DataAsset.GetSidecarPath(fbxPath);

                    var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
                    if (data == null)
                    {
                        data = ScriptableObject.CreateInstance<Uv2DataAsset>();
                        AssetDatabase.CreateAsset(data, sidecarPath);
                    }

                    foreach (var entry in kv.Value)
                    {
                        data.Set(new MeshUv2Entry {
                            meshName = entry.name,
                            uv2 = entry.uv2,
                            welded = entry.welded,
                            edgeWelded = entry.edgeWelded,
                            stepSymmetrySplit = entry.stepSymmetrySplit || entry.symmetrySplit,
                            vertPositions = entry.positions,
                            vertUv0 = entry.uv0,
                            vertexRemap = entry.vertexRemap,
                            optimizedVertexCount = entry.optimizedVertexCount,
                            optimizedTriangles = entry.optimizedTriangles,
                            submeshTriangleCounts = entry.submeshTriangleCounts,
                            schemaVersion = entry.schemaVersion,
                            toolVersion = entry.toolVersion,
                            sourceFingerprint = entry.sourceFingerprint,
                            targetUvChannel = entry.targetUvChannel,
                            stepMeshopt = entry.stepMeshopt,
                            stepEdgeWeld = entry.stepEdgeWeld,
                            stepRepack = entry.stepRepack,
                            stepTransfer = entry.stepTransfer,
                            hasReplayData = entry.hasReplayData,
                            orphanIndices = entry.orphanIndices,
                            orphanPositions = entry.orphanPositions,
                            orphanNormals = entry.orphanNormals,
                            orphanTangents = entry.orphanTangents,
                            orphanUv0 = entry.orphanUv0,
                        });
                        totalMeshes++;
                    }

                    EditorUtility.SetDirty(data);
                    AssetDatabase.SaveAssets();

                    // Disable Read/Write — mesh data is accessible during import regardless,
                    // and this frees CPU RAM after import finalization.
                    var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                    if (importer != null && importer.isReadable)
                    {
                        importer.isReadable = false;
                        UvtLog.Verbose($"[Apply] Disabled Read/Write on '{fbxPath}' to save RAM");
                    }

                    AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);
                }
            }
            catch (Exception ex) { UvtLog.Error("[Apply] " + ex); }

            AssetDatabase.Refresh();
            UvtLog.Info($"[Apply] Done: {totalMeshes} mesh(es) across {fbxGroups.Count} FBX file(s)");
            CleanupWorkingMeshes();
            Refresh();
            Repaint();
        }

        /// <summary>
        /// Build a vertex remap table: rawFbxVertex[i] → optimizedVertex[remap[i]].
        /// Uses quantized position + UV0 matching (same as RemapUv2IfNeeded but computed once at pipeline time).
        /// </summary>
        static int[] BuildVertexRemap(Mesh rawFbx, Mesh optimized)
        {
            var rawPos = rawFbx.vertices;
            var optPos = optimized.vertices;
            int rawCount = rawPos.Length;
            int optCount = optPos.Length;

            var rawUv0 = new List<Vector2>(); rawFbx.GetUVs(0, rawUv0);
            var optUv0 = new List<Vector2>(); optimized.GetUVs(0, optUv0);
            bool hasUv0 = rawUv0.Count == rawCount && optUv0.Count == optCount;

            // Build quantized position lookup for optimized vertices
            var posLookup = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < optCount; i++)
            {
                var key = QuantizePos(optPos[i]);
                if (!posLookup.TryGetValue(key, out var list))
                {
                    list = new List<int>(2);
                    posLookup[key] = list;
                }
                list.Add(i);
            }

            var remap = new int[rawCount];
            for (int i = 0; i < rawCount; i++) remap[i] = -1;

            var used = new bool[optCount];
            int matched = 0;

            // Pass 1: exact quantized position match
            for (int i = 0; i < rawCount; i++)
            {
                var key = QuantizePos(rawPos[i]);
                if (!posLookup.TryGetValue(key, out var candidates)) continue;

                if (candidates.Count == 1)
                {
                    remap[i] = candidates[0];
                    // Don't mark as used — multiple raw verts can map to same optimized vert (dedup)
                    matched++;
                }
                else if (hasUv0)
                {
                    float bestDist = float.MaxValue;
                    int bestIdx = -1;
                    foreach (int ci in candidates)
                    {
                        float d = Vector2.SqrMagnitude(rawUv0[i] - optUv0[ci]);
                        if (d < bestDist) { bestDist = d; bestIdx = ci; }
                    }
                    if (bestIdx >= 0) { remap[i] = bestIdx; matched++; }
                }
                else
                {
                    // No UV0 — pick first candidate
                    remap[i] = candidates[0];
                    matched++;
                }
            }

            // Pass 2: nearest-neighbor fallback for bucket boundary misses
            if (matched < rawCount)
            {
                for (int i = 0; i < rawCount; i++)
                {
                    if (remap[i] >= 0) continue;
                    float bestDist = float.MaxValue;
                    int bestIdx = -1;
                    for (int j = 0; j < optCount; j++)
                    {
                        float d = Vector3.SqrMagnitude(rawPos[i] - optPos[j]);
                        if (d < bestDist) { bestDist = d; bestIdx = j; }
                    }
                    if (bestIdx >= 0 && bestDist < 1e-4f)
                    {
                        remap[i] = bestIdx;
                        matched++;
                    }
                }
            }

            if (matched < rawCount)
                UvtLog.Warn($"[Apply] BuildVertexRemap: {matched}/{rawCount} mapped ({rawCount - matched} unmapped)");

            return remap;
        }

        /// <summary>Extract all triangle indices + per-submesh counts from a mesh.</summary>
        static void BuildTriangleData(Mesh mesh, out int[] allTriangles, out int[] submeshCounts)
        {
            int subCount = mesh.subMeshCount;
            submeshCounts = new int[subCount];
            var allTris = new List<int>();
            for (int s = 0; s < subCount; s++)
            {
                int[] sub = mesh.GetTriangles(s);
                submeshCounts[s] = sub.Length;
                allTris.AddRange(sub);
            }
            allTriangles = allTris.ToArray();
        }

        static (int, int, int) QuantizePos(Vector3 pos)
        {
            return (
                Mathf.RoundToInt(pos.x * 10000f),
                Mathf.RoundToInt(pos.y * 10000f),
                Mathf.RoundToInt(pos.z * 10000f)
            );
        }

        // ════════════════════════════════════════════════════════════
        //  Reset
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Destroy all working mesh clones to free RAM.
        /// Called after Apply when mesh data has been persisted to sidecar.
        /// Does NOT destroy fbxMesh references (they are shared assets).
        /// </summary>
        void CleanupWorkingMeshes()
        {
            foreach (var e in meshEntries)
            {
                if (e.transferredMesh != null)
                { DestroyImmediate(e.transferredMesh); e.transferredMesh = null; }
                if (e.repackedMesh != null)
                { DestroyImmediate(e.repackedMesh); e.repackedMesh = null; }
                if (e.originalMesh != null && e.originalMesh != e.fbxMesh)
                { DestroyImmediate(e.originalMesh); e.originalMesh = null; }
            }
        }

        void ResetWorkingCopies()
        {
            foreach (var e in meshEntries)
            {
                // Destroy working mesh clones before resetting references
                if (e.transferredMesh != null)
                { DestroyImmediate(e.transferredMesh); e.transferredMesh = null; }
                if (e.repackedMesh != null)
                { DestroyImmediate(e.repackedMesh); e.repackedMesh = null; }
                if (e.originalMesh != null && e.originalMesh != e.fbxMesh)
                    DestroyImmediate(e.originalMesh);

                if (e.fbxMesh != null) e.originalMesh = e.fbxMesh;
                e.shellTransferResult = null;
                e.wasWelded = false;
                e.wasEdgeWelded = false;
                e.wasSymmetrySplit = false;
                e.report = null;
            }
            hasRepack = hasTransfer = false;
            uv0Analyzed = uv0Welded = false;
            uv0Reports.Clear();
            srcCache.Clear();
            shellTransformCache.Clear();
            UvtLog.Info("[Reset] All working copies destroyed and restored to FBX originals");
            Repaint();
        }

        /// <summary>
        /// Reset UV2 for the currently selected model (detected via toolbar).
        /// Independent from the loaded LODGroup — works on any selected object.
        /// </summary>
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

            AssetDatabase.ImportAsset(selectedFbxPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            UvtLog.Info($"[Reset] Deleted sidecar for '{selectedResetLabel}', reimported FBX");

            // Update checker if it's still active
            if (checkerEnabled)
            {
                CheckerTexturePreview.Restore();
                ReapplyCheckerToSelection();
            }

            // Refresh loaded LODGroup if it references the same FBX
            bool touchesLoaded = meshEntries.Any(e =>
                e.fbxMesh != null && AssetDatabase.GetAssetPath(e.fbxMesh) == selectedFbxPath);
            if (touchesLoaded) Refresh();

            UpdateSelectedSidecar();
            Repaint();
        }

        /// <summary>
        /// Full pipeline state reset: delete all sidecars, reset working copies,
        /// clear caches, restore checker textures. The nuclear option.
        /// </summary>
        void ResetPipelineState()
        {
            if (lodGroup == null) return;

            if (!EditorUtility.DisplayDialog("Reset Pipeline State",
                "This will:\n• Delete all UV2 sidecars\n• Reset working copies\n• Clear caches\n• Restore checker textures\n\nThis cannot be undone.",
                "Reset Everything", "Cancel"))
                return;

            // Collect all FBX paths
            var fbxPaths = new HashSet<string>();
            foreach (var e in meshEntries)
            {
                Mesh m = e.fbxMesh ?? e.originalMesh;
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    fbxPaths.Add(p);
            }

            // Delete sidecars + reimport
            int deleted = 0;
            foreach (string fbxPath in fbxPaths)
            {
                string sp = Uv2DataAsset.GetSidecarPath(fbxPath);
                if (AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sp) != null)
                {
                    AssetDatabase.DeleteAsset(sp);
                    deleted++;
                }
                AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);
            }

            // Reset working copies
            ResetWorkingCopies();

            // Clear checker
            if (checkerEnabled) { CheckerTexturePreview.Restore(); checkerEnabled = false; }

            AssetDatabase.Refresh();
            UvtLog.Info($"[Reset] Full pipeline state reset: {deleted} sidecar(s) deleted, {fbxPaths.Count} FBX reimported");
            Refresh();
            Repaint();
        }

        void ResetUv2FromFbx()
        {
            if (lodGroup == null) return;

            var fbxPaths = new HashSet<string>();
            foreach (var e in meshEntries)
            {
                Mesh m = e.fbxMesh ?? e.originalMesh;
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    fbxPaths.Add(p);
            }
            if (fbxPaths.Count == 0) return;

            if (!EditorUtility.DisplayDialog("Reset UV2",
                $"Delete UV2 sidecar data for {fbxPaths.Count} FBX file(s)?",
                "Delete", "Cancel"))
                return;

            int deleted = 0;
            foreach (string fbxPath in fbxPaths)
            {
                string sidecarPath = Uv2DataAsset.GetSidecarPath(fbxPath);
                if (AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath) != null)
                {
                    AssetDatabase.DeleteAsset(sidecarPath);
                    deleted++;
                }
                AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);
            }

            AssetDatabase.Refresh();
            UvtLog.Info($"[Reset] Deleted {deleted} sidecar(s), reimported {fbxPaths.Count} FBX");
            if (checkerEnabled) CheckerTexturePreview.Restore();
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
