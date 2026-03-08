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
        bool lockSelection;
        bool spotMode;
        bool hoverHitValid;
        int hoveredShellId = -1;
        Vector2 uvSpot;
        Vector3 hoverWorldPos;
        bool canvasSpotValid;
        Vector2 canvasSpotUv;

        // Checker mode (user toggle, independent from CheckerTexturePreview.IsActive)
        bool checkerEnabled;
        bool shellColorPreviewEnabled;
        readonly ShellColorModelPreview.PreviewShellCache shellColorPreviewCache =
            new ShellColorModelPreview.PreviewShellCache();

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
        bool foldShellDebug = false;

        class ShellDebugHit
        {
            public MeshEntry entry;
            public Mesh mesh;
            public UvShell shell;
            public int shellId;
            public int uvChannel;
            public Vector2 hoverUv;
            public int tileU;
            public int tileV;
            public Vector2 localUv;
            public int drawIndex;
        }

        ShellDebugHit hoveredShellDebug;
        ShellDebugHit selectedShellDebug;

        // Sidebar
        Vector2 sideScroll, reportScroll;
        float sideW = 300f;
        bool sideDragging;

        Material glMat;
        Material texMat;
        Material spotMat;

        string previewConflictNotice;

        // Preview cache: mesh instanceID -> boundary edge index pairs (a0,b0,a1,b1,...)
        readonly Dictionary<int, int[]> boundaryEdgeCache = new Dictionary<int, int[]>();
        readonly FaceToShellCache uvPreviewShellCache = new FaceToShellCache();
        readonly Dictionary<long, PreviewShellData> previewShellDataCache = new Dictionary<long, PreviewShellData>();

        // Per-frame mesh data cache to avoid repeated allocations from mesh.triangles / GetUVs
        readonly Dictionary<int, int[]> cachedTriangles = new Dictionary<int, int[]>();
        readonly Dictionary<long, Vector2[]> cachedUvs = new Dictionary<long, Vector2[]>();

        // Shell color key cache: (meshInstanceId, shellId) -> colorKey
        readonly Dictionary<long, int> shellColorKeyCache = new Dictionary<long, int>();
        bool shellColorKeyCacheDirty = true;

        // UDIM tile cache: meshInstanceId+channel -> tiles
        readonly Dictionary<long, HashSet<Vector2Int>> occupiedTilesPerMesh = new Dictionary<long, HashSet<Vector2Int>>();

        ShellUvHit hoveredShell;
        ShellUvHit selectedShell;
        bool hasHoveredShell;
        bool hasSelectedShell;
        int lastHitMeshId = -1;
        int lastHitShellId = -1;

        const int TRI_PICK_BUDGET = 6000;

        class FaceToShellCache
        {
            readonly Dictionary<long, int[]> faceToShellByMeshAndChannel = new Dictionary<long, int[]>();

            public void Clear() => faceToShellByMeshAndChannel.Clear();

            public int[] GetFaceToShell(Mesh mesh, int channel, Vector2[] uv, int[] triangles)
            {
                if (mesh == null || uv == null || triangles == null) return null;
                long key = (((long)mesh.GetInstanceID()) << 8) ^ (uint)channel;
                if (faceToShellByMeshAndChannel.TryGetValue(key, out var cached))
                    return cached;

                int faceCount = triangles.Length / 3;
                var faceToShell = new int[faceCount];
                for (int i = 0; i < faceToShell.Length; i++) faceToShell[i] = -1;

                try
                {
                    var shells = UvShellExtractor.Extract(uv, triangles);
                    foreach (var shell in shells)
                    {
                        if (shell?.faceIndices == null) continue;
                        foreach (int f in shell.faceIndices)
                            if (f >= 0 && f < faceToShell.Length)
                                faceToShell[f] = shell.shellId;
                    }
                }
                catch
                {
                    // Keep -1 mapping when shell extraction fails.
                }

                faceToShellByMeshAndChannel[key] = faceToShell;
                return faceToShell;
            }
        }

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
            /// <summary>Source shell descriptors restored from sidecar, for stable ShellMatch coloring.</summary>
            public ShellDescriptor[] restoredSourceDescriptors;
        }

        struct ShellUvHit
        {
            public MeshEntry meshEntry;
            public int shellId;
            public int faceIndex;
            public Vector2 uvHit;
            public Vector3 barycentric;
        }

        class PreviewShellData
        {
            public List<UvShell> shells;
            public Dictionary<int, int> faceToShell;
            public Dictionary<int, UvShell> shellById;
            public Bounds[] shellBounds;
            public int[] triangles;
            public Vector2[] uvs;
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
        const int BATCH = 4000;
        // Preview draw budget per mesh. 12k was too low for complex LOD0 meshes and
        // caused truncated fill/wire while boundary-only overlay still looked complete.
        const int MAX_TRI = 500000;

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
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;

            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return;
            glMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            glMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            glMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            glMat.SetInt("_Cull",     (int)CullMode.Off);
            glMat.SetInt("_ZWrite",   0);

            var texShader = Shader.Find("Unlit/Transparent");
            if (texShader != null)
                texMat = new Material(texShader) { hideFlags = HideFlags.HideAndDontSave };

            var spotShader = Shader.Find("Hidden/LightmapUvTool/SpotProjection");
            if (spotShader != null)
                spotMat = new Material(spotShader) { hideFlags = HideFlags.HideAndDontSave };
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            if (checkerEnabled) CheckerTexturePreview.Restore();
            checkerEnabled = false;
            if (lodGroup != null) lodGroup.ForceLOD(-1);
            CleanupWorkingMeshes();
            boundaryEdgeCache.Clear();
            uvPreviewShellCache.Clear();
            previewShellDataCache.Clear();
            occupiedTilesPerMesh.Clear();
            shellColorPreviewCache.Clear();
            shellColorKeyCache.Clear();
            ClearHoverState(false);
            if (canvasRT) { canvasRT.Release(); DestroyImmediate(canvasRT); canvasRT = null; }
            if (glMat) DestroyImmediate(glMat);
            if (texMat) DestroyImmediate(texMat);
            if (spotMat) DestroyImmediate(spotMat);
            glMat = null;
            texMat = null;
            spotMat = null;
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

            if (shellColorPreviewEnabled)
                ReapplyShellColorPreview();

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
            RestoreAllPreviews();
            meshEntries.Clear();
            hasRepack = hasTransfer = false;
            srcCache.Clear();
            shellTransformCache.Clear();
            uv0Reports.Clear();
            boundaryEdgeCache.Clear();
            uvPreviewShellCache.Clear();
            previewShellDataCache.Clear();
            occupiedTilesPerMesh.Clear();
            shellColorPreviewCache.Clear();
            shellColorKeyCache.Clear();
            uv0Analyzed = false;
            uv0Welded = false;
            ClearHoverState(false);
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

            if (LodN > 0) SetPreviewLod(pvLod);
            else pvLod = 0;

            TryRestoreShellMatchFromSidecar();
            UpdateSelectedSidecar();
        }

        /// <summary>
        /// Try to restore ShellMatch data from sidecar for entries that don't have
        /// an in-memory shellTransferResult. This allows ShellMatch view to work
        /// after reopening the window without re-running transfer.
        /// </summary>
        void TryRestoreShellMatchFromSidecar()
        {
            // Collect FBX paths → sidecar assets
            var sidecarCache = new Dictionary<string, Uv2DataAsset>();
            foreach (var e in meshEntries)
            {
                if (e.shellTransferResult != null) continue; // already has in-memory data
                if (e.fbxMesh == null) continue;

                string fbxPath = AssetDatabase.GetAssetPath(e.fbxMesh);
                if (string.IsNullOrEmpty(fbxPath)) continue;

                if (!sidecarCache.TryGetValue(fbxPath, out var sidecar))
                {
                    string sidecarPath = Uv2DataAsset.GetSidecarPath(fbxPath);
                    sidecar = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
                    sidecarCache[fbxPath] = sidecar; // null is fine — caches miss
                }
                if (sidecar == null) continue;

                string meshName = e.fbxMesh.name;
                var entry = sidecar.Find(meshName);
                if (entry == null) continue;

                // Restore shell match from persisted data
                if (entry.vertexToSourceShellDescriptor != null && entry.vertexToSourceShellDescriptor.Length > 0)
                {
                    var tr = new GroupedShellTransfer.TransferResult();
                    tr.vertexToSourceShell = entry.vertexToSourceShellDescriptor;
                    tr.targetShellToSourceShell = entry.targetShellToSourceShellDescriptor;
                    tr.verticesTotal = e.fbxMesh.vertexCount;

                    // Count transferred vertices
                    int transferred = 0;
                    for (int i = 0; i < tr.vertexToSourceShell.Length; i++)
                        if (tr.vertexToSourceShell[i] >= 0) transferred++;
                    tr.verticesTransferred = transferred;

                    e.shellTransferResult = tr;

                    // Also store source descriptors for stable ShellMatch coloring
                    e.restoredSourceDescriptors = entry.sourceShellDescriptors;
                }
            }
        }

        List<MeshEntry> ForLod(int li) => meshEntries.Where(e => e.lodIndex == li && e.include).ToList();
        int LodN => lodGroup != null ? lodGroup.GetLODs().Length : 0;

        void SetPreviewLod(int lodIndex)
        {
            if (LodN <= 0) return;

            int clamped = Mathf.Clamp(lodIndex, 0, LodN - 1);
            if (pvLod == clamped)
                return;

            pvLod = clamped;
            ClearHoverState(false);

            // Keep scene model LOD in sync with window preview LOD.
            if (lodGroup != null)
                lodGroup.ForceLOD(clamped);

            if (shellColorPreviewEnabled)
                ReapplyShellColorPreview();

            Repaint();
        }

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
            if (!string.IsNullOrEmpty(previewConflictNotice))
                EditorGUILayout.HelpBox(previewConflictNotice, MessageType.Info);
            DrawCanvas();
            DrawStatusBar();
            DrawShellDebugPanel();
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
                EditorGUILayout.HelpBox("Repack done. Preview UV1 (Lightmap), then Transfer.", MessageType.Info);
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
                                if (vr.overlapSameSrcPairs > 0)
                                {
                                    var r2 = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.miniLabel, GUILayout.Height(14));
                                    EditorGUI.LabelField(r2, $"Ov(same-src): {vr.overlapSameSrcTriCount} ({vr.overlapSameSrcPairs} pairs, ok)",
                                        EditorStyles.miniLabel);
                                }
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
                        SetPreviewLod(i);
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
                ci = GUILayout.Toolbar(ci, new[]{"UV0 (MainTex)","UV1 (Lightmap)"}, EditorStyles.toolbarButton, GUILayout.Width(220));
                int newChannel = ci == 0 ? 0 : 1;
                if (newChannel != pvChannel)
                    OnPreviewChannelChanged(newChannel);
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
            bool spotNext = GUILayout.Toggle(spotMode, "Spot", EditorStyles.toolbarButton, GUILayout.Width(52));
            if (spotNext != spotMode)
            {
                spotMode = spotNext;
                if (!spotMode) ClearHoverState();
                SceneView.RepaintAll();
            }

            GUILayout.Space(4);
            lockSelection = GUILayout.Toggle(lockSelection, "Lock", EditorStyles.toolbarButton, GUILayout.Width(40));
            using (new EditorGUI.DisabledScope(!hasSelectedShell))
            {
                if (GUILayout.Button("Clear Selection", EditorStyles.toolbarButton, GUILayout.Width(96)))
                    hasSelectedShell = false;
            }

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

            GUILayout.Space(4);

            {
                var bg4 = GUI.backgroundColor;
                if (shellColorPreviewEnabled) GUI.backgroundColor = new Color(.35f,.85f,.4f);
                string shellLbl = shellColorPreviewEnabled ? "■ Color Shells on Model" : "▶ Color Shells on Model";
                if (GUILayout.Button(shellLbl, EditorStyles.toolbarButton, GUILayout.Width(162)))
                    ToggleShellColorPreview();
                GUI.backgroundColor = bg4;
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
            if (ee.Count == 0) { EditorGUILayout.HelpBox("No meshes for this LOD.", MessageType.Info); hoveredShellDebug = null; return; }

            var draws = new List<ValueTuple<Mesh, MeshEntry, int>>();
            for (int i = 0; i < ee.Count; i++)
            {
                Mesh m = DMesh(ee[i]);
                if (m != null) draws.Add(new ValueTuple<Mesh, MeshEntry, int>(m, ee[i], i));
            }
            if (draws.Count == 0) { hoveredShellDebug = null; return; }

            var canvasRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            lastCanvasRect = canvasRect;

            float baseSz = Mathf.Max(64, Mathf.Min(canvasRect.width, canvasRect.height));
            float sz = baseSz * canvasZoom;

            float cx = (canvasRect.width - sz) * 0.5f + canvasPan.x;
            float cy = (canvasRect.height - sz) * 0.5f + canvasPan.y;

            hoveredShellDebug = FindShellAtMouse(draws, canvasRect, cx, cy, sz);
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

                    var occupiedTiles = GetOccupiedUdimTiles(draws, pvChannel);

                    // Draw background quads for all occupied tiles
                    GL.Begin(GL.QUADS);
                    GL.Color(new Color(.12f,.12f,.12f));
                    foreach (var tile in occupiedTiles)
                    {
                        float tx = cx + tile.x * sz, ty = cy - tile.y * sz;
                        GL.Vertex3(tx, ty, 0); GL.Vertex3(tx + sz, ty, 0);
                        GL.Vertex3(tx + sz, ty + sz, 0); GL.Vertex3(tx, ty + sz, 0);
                    }
                    GL.End();

                    Texture bgTex = ResolveUvPreviewBackgroundTexture(draws);
                    if (bgTex != null)
                    {
                        float bgAlpha = checkerEnabled ? 0.33333f : 0.95f;
                        GlTextureBg(cx, cy, sz, bgTex, new Vector2(1f, 1f), Vector2.zero, bgAlpha, occupiedTiles);
                        glMat.SetPass(0);
                    }

                    if (bgTex == null && (checkerEnabled || fillMode != FillMode.None || pvChannel == 1))
                    {
                        float baseAlpha = pvChannel == 1
                            ? 0.24f
                            : (checkerEnabled ? 0.33333f : fillAlpha * 0.45f);
                        float checkerAlpha = Mathf.Clamp(baseAlpha, 0.06f, 0.33333f);
                        GlCheckerBg(cx, cy, sz, 8, checkerAlpha, pvChannel == 1, occupiedTiles);
                    }

                    GlGrid(cx, cy, sz, occupiedTiles);

                    ClearFrameCaches();
                    shellColorKeyCacheDirty = false;
                    foreach (var item in draws)
                    {
                        Mesh mesh = item.Item1;
                        MeshEntry entry = item.Item2;
                        var uvs = RdUvCached(mesh, pvChannel);
                        var tri = GetTrianglesCached(mesh);
                        if (uvs == null || tri == null) continue;
                        int uN = uvs.Length, fN = tri.Length / 3;

                        TriangleStatus[] stats = entry.transferState?.triangleStatus;
                        HashSet<int> bdr = showBorder ? entry.transferState?.borderPrimitiveIds : null;
                        bool hasStatus = stats != null && stats.Length > 0;
                        bool hasShellMatch = entry.shellTransferResult?.vertexToSourceShell != null
                                             && entry.shellTransferResult.vertexToSourceShell.Length > 0;
                        bool hasValidation = entry.validationReport?.perTriangle != null
                                             && entry.validationReport.perTriangle.Length > 0;

                        // Fill
                        int hoverShellId = hasHoveredShell && hoveredShell.meshEntry == entry ? hoveredShell.shellId : -1;
                        int selectedShellId = hasSelectedShell && selectedShell.meshEntry == entry ? selectedShell.shellId : -1;

                        switch (fillMode)
                        {
                            case FillMode.ShellMatch when hasShellMatch:
                                GlFillShellMatch(cx,cy,sz, uvs,tri,fN,uN, entry.shellTransferResult.vertexToSourceShell, GetSourceDescriptors(entry));
                                break;
                            case FillMode.Validation when hasValidation:
                                GlFillValidation(cx,cy,sz, uvs,tri,fN,uN, entry.validationReport.perTriangle);
                                break;
                            case FillMode.Status when hasStatus:
                                GlFillSt(cx,cy,sz, uvs,tri,fN,uN, stats);
                                break;
                            case FillMode.Shells:
                                GlFillSh(cx,cy,sz, mesh, fN, uN, entry, hoverShellId, selectedShellId);
                                break;
                            case FillMode.None:
                                break;
                            default:
                                if (fillMode != FillMode.None)
                                    GlFillSh(cx,cy,sz, mesh, fN, uN, entry, hoverShellId, selectedShellId);
                                break;
                        }

                        if (showBorder)
                        {
                            if (bdr != null && bdr.Count > 0) GlBdr(cx,cy,sz, uvs,tri,fN,uN, bdr);
                            else GlUvBoundary(cx, cy, sz, mesh, uvs, tri, uN);
                        }
                        if (showWire) GlWr(cx,cy,sz, uvs,tri,fN,uN);
                    }
                    if (spotMode)
                        GlDrawUvSpot(cx, cy, sz);
                }
                catch (Exception ex) { UvtLog.Warn("[UV] GL: " + ex.Message); }
                finally { if (push) GL.PopMatrix(); }

                RenderTexture.active = prevRT;
                GUI.DrawTexture(canvasRect, canvasRT, ScaleMode.StretchToFill, false);
            }
        }

        void OnSceneGUI(SceneView sv)
        {
            if (!spotMode || sv == null)
            {
                if (hoverHitValid) ClearHoverState();
                return;
            }

            Event e = Event.current;
            if (e == null)
                return;

            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            bool hadHit = hoverHitValid;
            Vector2 prevUv = uvSpot;
            int prevShell = hoveredShellId;

            hoverHitValid = TryRaycastPreview(ray, out var hit);
            if (hoverHitValid)
            {
                hoverWorldPos = hit.worldPos;
                uvSpot = hit.uv;
                hoveredShellId = hit.shellId;
            }
            else
            {
                hoveredShellId = -1;
            }

            if (!hoverHitValid && hadHit)
                Repaint();
            else if (hoverHitValid && (!hadHit || prevShell != hoveredShellId || (prevUv - uvSpot).sqrMagnitude > 1e-8f))
                Repaint();

            // Determine which UV to project: 3D hover > selected shell > hovered shell > canvas spot
            Vector2 projUv;
            MeshEntry projEntry = null;
            bool hasProj;
            if (hoverHitValid)
            {
                projUv = uvSpot;
                projEntry = hit.meshEntry;
                hasProj = true;
            }
            else if (hasSelectedShell)
            {
                projUv = selectedShell.uvHit;
                projEntry = selectedShell.meshEntry;
                hasProj = true;
            }
            else if (hasHoveredShell)
            {
                projUv = hoveredShell.uvHit;
                projEntry = hoveredShell.meshEntry;
                hasProj = true;
            }
            else if (canvasSpotValid)
            {
                projUv = canvasSpotUv;
                projEntry = null; // no specific entry — draw on all
                hasProj = true;
            }
            else
            {
                projUv = default;
                hasProj = false;
            }

            if (!hasProj)
                return;

            DrawSpotProjectionInScene(projUv, projEntry);
        }

        void DrawSpotProjectionInScene(Vector2 projUv, MeshEntry limitEntry = null)
        {
            if (spotMat == null || Event.current.type != EventType.Repaint)
                return;

            spotMat.SetVector("_SpotUv", new Vector4(projUv.x, projUv.y, 0f, 0f));
            spotMat.SetFloat("_SpotRadius", 0.012f);
            spotMat.SetColor("_SpotColor", new Color32(0xFF, 0xBC, 0x51, 0xFF));
            spotMat.SetFloat("_UseUv2", pvChannel == 1 ? 1f : 0f);

            var entries = ForLod(pvLod);
            foreach (var entry in entries)
            {
                if (limitEntry != null && entry != limitEntry) continue;

                var mesh = DMesh(entry);
                if (mesh == null) continue;

                Matrix4x4 l2w;
                if (entry.renderer != null) l2w = entry.renderer.localToWorldMatrix;
                else if (entry.meshFilter != null) l2w = entry.meshFilter.transform.localToWorldMatrix;
                else continue;

                spotMat.SetPass(0);
                Graphics.DrawMeshNow(mesh, l2w);
            }
        }

        struct HoverHit
        {
            public float distance;
            public Vector3 worldPos;
            public Vector2 uv;
            public int shellId;
            public MeshEntry meshEntry;
        }

        bool TryRaycastPreview(Ray ray, out HoverHit bestHit)
        {
            bestHit = default;
            bestHit.distance = float.PositiveInfinity;

            var entries = ForLod(pvLod);
            bool found = false;
            foreach (var entry in entries)
            {
                Mesh mesh = DMesh(entry);
                if (mesh == null) continue;

                Matrix4x4 l2w;
                if (entry.renderer != null) l2w = entry.renderer.localToWorldMatrix;
                else if (entry.meshFilter != null) l2w = entry.meshFilter.transform.localToWorldMatrix;
                else continue;

                Bounds worldBounds = TransformBounds(mesh.bounds, l2w);
                if (!worldBounds.IntersectRay(ray, out float aabbDistance)) continue;
                if (aabbDistance > bestHit.distance) continue;

                Vector3[] v = mesh.vertices;
                int[] tri = GetTrianglesCached(mesh);
                Vector2[] uv = RdUvCached(mesh, pvChannel);
                if (v == null || tri == null || uv == null) continue;
                int[] faceToShell = uvPreviewShellCache.GetFaceToShell(mesh, pvChannel, uv, tri);

                for (int f = 0; f + 2 < tri.Length; f += 3)
                {
                    int i0 = tri[f];
                    int i1 = tri[f + 1];
                    int i2 = tri[f + 2];
                    if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= v.Length || i1 >= v.Length || i2 >= v.Length) continue;
                    if (i0 >= uv.Length || i1 >= uv.Length || i2 >= uv.Length) continue;

                    Vector3 p0 = l2w.MultiplyPoint3x4(v[i0]);
                    Vector3 p1 = l2w.MultiplyPoint3x4(v[i1]);
                    Vector3 p2 = l2w.MultiplyPoint3x4(v[i2]);

                    if (!RayTriangleMollerTrumbore(ray, p0, p1, p2, out float t, out float b1, out float b2))
                        continue;
                    if (t < 0f || t >= bestHit.distance)
                        continue;

                    float b0 = 1f - b1 - b2;
                    Vector2 hitUv = uv[i0] * b0 + uv[i1] * b1 + uv[i2] * b2;

                    bestHit.distance = t;
                    bestHit.worldPos = ray.origin + ray.direction * t;
                    bestHit.uv = hitUv;
                    bestHit.shellId = (faceToShell != null && (f / 3) < faceToShell.Length) ? faceToShell[f / 3] : -1;
                    bestHit.meshEntry = entry;
                    found = true;
                }
            }

            return found;
        }

        static Bounds TransformBounds(Bounds localBounds, Matrix4x4 l2w)
        {
            Vector3 c = localBounds.center;
            Vector3 e = localBounds.extents;
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            for (int ix = -1; ix <= 1; ix += 2)
            for (int iy = -1; iy <= 1; iy += 2)
            for (int iz = -1; iz <= 1; iz += 2)
            {
                Vector3 corner = c + Vector3.Scale(e, new Vector3(ix, iy, iz));
                Vector3 wc = l2w.MultiplyPoint3x4(corner);
                min = Vector3.Min(min, wc);
                max = Vector3.Max(max, wc);
            }

            Bounds b = new Bounds((min + max) * 0.5f, max - min);
            return b;
        }

        static bool RayTriangleMollerTrumbore(Ray ray, Vector3 v0, Vector3 v1, Vector3 v2, out float t, out float u, out float v)
        {
            t = 0f; u = 0f; v = 0f;
            const float eps = 1e-7f;
            Vector3 e1 = v1 - v0;
            Vector3 e2 = v2 - v0;
            Vector3 p = Vector3.Cross(ray.direction, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < eps) return false;

            float invDet = 1f / det;
            Vector3 s = ray.origin - v0;
            u = Vector3.Dot(s, p) * invDet;
            if (u < 0f || u > 1f) return false;

            Vector3 q = Vector3.Cross(s, e1);
            v = Vector3.Dot(ray.direction, q) * invDet;
            if (v < 0f || (u + v) > 1f) return false;

            t = Vector3.Dot(e2, q) * invDet;
            return t >= 0f;
        }

        void ClearHoverState(bool repaint = true)
        {
            hoverHitValid = false;
            hoveredShellId = -1;
            uvSpot = Vector2.zero;
            hoverWorldPos = Vector3.zero;
            canvasSpotValid = false;
            canvasSpotUv = Vector2.zero;
            if (repaint) Repaint();
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
                var uvs = RdUvCached(mesh, pvChannel);
                if (uvs == null) continue;
                foreach (var uv in uvs)
                {
                    if (!UOk(uv)) continue;
                    if (!any) { minU = maxU = uv.x; minV = maxV = uv.y; any = true; }
                    else { if (uv.x < minU) minU = uv.x; if (uv.x > maxU) maxU = uv.x; if (uv.y < minV) minV = uv.y; if (uv.y > maxV) maxV = uv.y; }
                }
            }
            FocusUvBounds(minU, minV, maxU, maxV);
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

            if (!spotMode)
                return;

            if (!canvasRect.Contains(e.mousePosition))
            {
                if (!lockSelection)
                    hasHoveredShell = false;
                canvasSpotValid = false;
                return;
            }

            Vector2 localPos = e.mousePosition - canvasRect.position;
            // mouse -> uv: u = (x-cx)/sz, v = 1-(y-cy)/sz
            Vector2 uv = new Vector2((localPos.x - cx) / sz, 1f - ((localPos.y - cy) / sz));
            canvasSpotUv = uv;
            canvasSpotValid = true;

            if (!lockSelection)
            {
                hasHoveredShell = TryPickUvHit(uv, ref hoveredShell);
                if (!hasHoveredShell)
                {
                    hoveredShell.uvHit = uv;
                    hoveredShell.barycentric = new Vector3(1f / 3f, 1f / 3f, 1f / 3f);
                }
            }

            Repaint();
            SceneView.RepaintAll();

            if (spotMode && e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                if (hasHoveredShell)
                {
                    selectedShell = hoveredShell;
                    hasSelectedShell = true;
                }
                else if (!lockSelection)
                {
                    hasSelectedShell = false;
                }
                e.Use();
                Repaint();
                SceneView.RepaintAll();
            }
        }

        void OnPreviewChannelChanged(int newChannel)
        {
            pvChannel = newChannel;

            ClearHoverState(false);

            hasHoveredShell = false;
            hasSelectedShell = false;
            hoveredShell = default;
            selectedShell = default;

            lastHitMeshId = -1;
            lastHitShellId = -1;

            hoveredShellDebug = null;
            selectedShellDebug = null;

            Repaint();
            SceneView.RepaintAll();
        }

        ShellDebugHit FindShellAtMouse(List<ValueTuple<Mesh, MeshEntry, int>> draws, Rect canvasRect, float cx, float cy, float sz)
        {
            var mouse = Event.current.mousePosition;
            if (!canvasRect.Contains(mouse)) return null;

            Vector2 local = mouse - canvasRect.position;
            var uvPoint = new Vector2((local.x - cx) / sz, 1f - ((local.y - cy) / sz));

            foreach (var item in draws)
            {
                Mesh mesh = item.Item1;
                if (uvPoint.x < UV_LO || uvPoint.x > UV_HI || uvPoint.y < UV_LO || uvPoint.y > UV_HI) continue;

                var cache = GetPreviewShellCache(mesh, pvChannel);
                if (cache == null || cache.shells == null) continue;
                var uvs = cache.uvs;
                var tri = cache.triangles;

                for (int f = 0; f < tri.Length / 3; f++)
                {
                    int a = tri[f * 3];
                    int b = tri[f * 3 + 1];
                    int c = tri[f * 3 + 2];
                    if (!TOk(uvs, uvs.Length, a, b, c)) continue;
                    if (!PointInTriangle(uvPoint, uvs[a], uvs[b], uvs[c])) continue;

                    if (!cache.faceToShell.TryGetValue(f, out int shellId)) return null;
                    if (!cache.shellById.TryGetValue(shellId, out var shell)) return null;

                    return BuildHit(item.Item2, mesh, shell, uvPoint, item.Item3);
                }
            }
            return null;
        }

        ShellDebugHit BuildHit(MeshEntry entry, Mesh mesh, UvShell shell, Vector2 uvPoint, int drawIndex)
        {
            int tu = Mathf.FloorToInt(uvPoint.x);
            int tv = Mathf.FloorToInt(uvPoint.y);
            return new ShellDebugHit
            {
                entry = entry,
                mesh = mesh,
                shell = shell,
                shellId = shell.shellId,
                uvChannel = pvChannel,
                hoverUv = uvPoint,
                tileU = tu,
                tileV = tv,
                localUv = new Vector2(uvPoint.x - tu, uvPoint.y - tv),
                drawIndex = drawIndex
            };
        }

        static ShellDebugHit CloneHit(ShellDebugHit src)
        {
            if (src == null) return null;
            return new ShellDebugHit
            {
                entry = src.entry,
                mesh = src.mesh,
                shell = src.shell,
                shellId = src.shellId,
                uvChannel = src.uvChannel,
                hoverUv = src.hoverUv,
                tileU = src.tileU,
                tileV = src.tileV,
                localUv = src.localUv,
                drawIndex = src.drawIndex
            };
        }


        static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float s1 = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
            float s2 = (c.x - b.x) * (p.y - b.y) - (c.y - b.y) * (p.x - b.x);
            float s3 = (a.x - c.x) * (p.y - c.y) - (a.y - c.y) * (p.x - c.x);
            bool hasNeg = (s1 < 0f) || (s2 < 0f) || (s3 < 0f);
            bool hasPos = (s1 > 0f) || (s2 > 0f) || (s3 > 0f);
            return !(hasNeg && hasPos);
        }

        void FocusUvBounds(float minU, float minV, float maxU, float maxV)
        {
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

        Vector2[] RdUvCached(Mesh m, int ch)
        {
            long key = ((long)m.GetInstanceID() << 8) ^ (uint)ch;
            if (cachedUvs.TryGetValue(key, out var uv)) return uv;
            uv = RdUv(m, ch);
            cachedUvs[key] = uv;
            return uv;
        }

        int[] GetTrianglesCached(Mesh m)
        {
            int id = m.GetInstanceID();
            if (cachedTriangles.TryGetValue(id, out var tri)) return tri;
            tri = m.triangles;
            cachedTriangles[id] = tri;
            return tri;
        }

        void ClearFrameCaches()
        {
            cachedTriangles.Clear();
            cachedUvs.Clear();
        }
        static bool UOk(Vector2 u) => u.x >= UV_LO && u.x <= UV_HI && u.y >= UV_LO && u.y <= UV_HI && !float.IsNaN(u.x) && !float.IsNaN(u.y) && !float.IsInfinity(u.x) && !float.IsInfinity(u.y);
        static bool TOk(Vector2[] u, int n, int a, int b, int c) => a>=0&&a<n&&b>=0&&b<n&&c>=0&&c<n && UOk(u[a])&&UOk(u[b])&&UOk(u[c]);
        static void Vx(float ox, float oy, float sz, Vector2 u) => GL.Vertex3(ox+u.x*sz, oy+(1f-u.y)*sz, 0);

        HashSet<Vector2Int> GetOccupiedUdimTiles(List<ValueTuple<Mesh, MeshEntry, int>> draws, int channel)
        {
            var tiles = new HashSet<Vector2Int>();
            foreach (var item in draws)
            {
                var mesh = item.Item1;
                long key = ((long)mesh.GetInstanceID() << 8) ^ (uint)channel;
                if (occupiedTilesPerMesh.TryGetValue(key, out var cached))
                {
                    foreach (var t in cached) tiles.Add(t);
                    continue;
                }
                var perMesh = new HashSet<Vector2Int>();
                var uvs = RdUvCached(mesh, channel);
                if (uvs != null)
                {
                    for (int i = 0; i < uvs.Length; i++)
                    {
                        var u = uvs[i];
                        if (!UOk(u)) continue;
                        var tile = new Vector2Int(Mathf.FloorToInt(u.x), Mathf.FloorToInt(u.y));
                        perMesh.Add(tile);
                        tiles.Add(tile);
                    }
                }
                occupiedTilesPerMesh[key] = perMesh;
            }
            return tiles;
        }

        void GlGrid(float ox, float oy, float sz, HashSet<Vector2Int> occupiedTiles = null)
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
            // Adjacent UDIM tiles (faded outlines) – only occupied
            GL.Color(new Color(.3f,.3f,.3f,.4f));
            if (occupiedTiles != null)
            {
                foreach (var tile in occupiedTiles)
                {
                    if (tile.x == 0 && tile.y == 0) continue;
                    float tx = ox + tile.x * sz, ty = oy - tile.y * sz;
                    GL.Vertex3(tx,ty,0); GL.Vertex3(tx+sz,ty,0);
                    GL.Vertex3(tx+sz,ty,0); GL.Vertex3(tx+sz,ty+sz,0);
                    GL.Vertex3(tx+sz,ty+sz,0); GL.Vertex3(tx,ty+sz,0);
                    GL.Vertex3(tx,ty+sz,0); GL.Vertex3(tx,ty,0);
                }
            }
            GL.End();
        }


        Texture ResolveUvPreviewBackgroundTexture(List<ValueTuple<Mesh, MeshEntry, int>> draws)
        {
            if (checkerEnabled)
                return CheckerTexturePreview.GetCheckerTexture();

            if (pvChannel == 1)
                return null;

            foreach (var item in draws)
            {
                var renderer = item.Item2.renderer;
                if (renderer == null) continue;
                var mats = renderer.sharedMaterials;
                if (mats == null) continue;

                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null || !mat.HasProperty("_MainTex")) continue;
                    var tex = mat.mainTexture;
                    if (tex != null) return tex;
                }
            }

            return null;
        }

        void GlCheckerBg(float ox, float oy, float sz, int cells, float alpha, bool neutralGray = false, HashSet<Vector2Int> occupiedTiles = null)
        {
            if (cells <= 0 || alpha <= 0f) return;
            float cell = sz / cells;
            GL.Begin(GL.QUADS);
            Color darkColor = neutralGray ? new Color(.24f,.24f,.24f,alpha) : new Color(.20f,.20f,.20f,alpha);
            Color lightColor = neutralGray ? new Color(.32f,.32f,.32f,alpha) : new Color(.38f,.38f,.38f,alpha);

            var tilesToDraw = occupiedTiles ?? new HashSet<Vector2Int> { new Vector2Int(0, 0) };
            foreach (var tile in tilesToDraw)
            {
                float tox = ox + tile.x * sz;
                float toy = oy - tile.y * sz;
                for (int y = 0; y < cells; y++)
                {
                    for (int x = 0; x < cells; x++)
                    {
                        bool dark = ((x + y) & 1) == 0;
                        GL.Color(dark ? darkColor : lightColor);
                        float x0 = tox + x * cell;
                        float y0 = toy + y * cell;
                        GL.Vertex3(x0, y0, 0);
                        GL.Vertex3(x0 + cell, y0, 0);
                        GL.Vertex3(x0 + cell, y0 + cell, 0);
                        GL.Vertex3(x0, y0 + cell, 0);
                    }
                }
            }
            GL.End();
        }

        void GlTextureBg(float ox, float oy, float sz, Texture tex, Vector2 tiling, Vector2 offset, float alpha, HashSet<Vector2Int> occupiedTiles = null)
        {
            if (tex == null || texMat == null) return;

            texMat.mainTexture = tex;
            texMat.SetColor("_Color", new Color(1f, 1f, 1f, Mathf.Clamp01(alpha)));
            texMat.SetPass(0);

            GL.Begin(GL.QUADS);
            if (occupiedTiles != null)
            {
                foreach (var tile in occupiedTiles)
                {
                    int tu = tile.x, tv = tile.y;
                    float tx = ox + tu * sz;
                    float ty = oy - tv * sz;

                    float u0 = (tu + offset.x) * tiling.x;
                    float u1 = (tu + 1 + offset.x) * tiling.x;
                    float v0 = (tv + offset.y) * tiling.y;
                    float v1 = (tv + 1 + offset.y) * tiling.y;

                    GL.TexCoord2(u0, v1); GL.Vertex3(tx, ty, 0);
                    GL.TexCoord2(u1, v1); GL.Vertex3(tx + sz, ty, 0);
                    GL.TexCoord2(u1, v0); GL.Vertex3(tx + sz, ty + sz, 0);
                    GL.TexCoord2(u0, v0); GL.Vertex3(tx, ty + sz, 0);
                }
            }
            else
            {
                float tx = ox, ty = oy;
                GL.TexCoord2(offset.x * tiling.x, (1 + offset.y) * tiling.y); GL.Vertex3(tx, ty, 0);
                GL.TexCoord2((1 + offset.x) * tiling.x, (1 + offset.y) * tiling.y); GL.Vertex3(tx + sz, ty, 0);
                GL.TexCoord2((1 + offset.x) * tiling.x, offset.y * tiling.y); GL.Vertex3(tx + sz, ty + sz, 0);
                GL.TexCoord2(offset.x * tiling.x, offset.y * tiling.y); GL.Vertex3(tx, ty + sz, 0);
            }
            GL.End();
        }

        void GlDrawUvSpot(float ox, float oy, float sz)
        {
            // Приоритет как в OnSceneGUI: 3D hover > selected shell > hovered shell > canvas spot.
            // Это важно при overlap/ре-проекции, чтобы UV spot не пропадал из-за устаревшего selected/hovered.
            Vector2 drawUv;
            if (hoverHitValid)
            {
                drawUv = uvSpot;
            }
            else if (hasSelectedShell)
            {
                drawUv = selectedShell.uvHit;
            }
            else if (hasHoveredShell)
            {
                drawUv = hoveredShell.uvHit;
            }
            else if (canvasSpotValid)
            {
                drawUv = canvasSpotUv;
            }
            else
            {
                return;
            }

            float px = ox + drawUv.x * sz;
            float py = oy + (1f - drawUv.y) * sz;
            float crossR = Mathf.Max(0.012f * sz, 4f);
            float spotOuterR = crossR * 1.35f; // spot гарантированно больше перекрестья
            float spotInnerR = crossR * 0.55f;
            float crossHalfW = Mathf.Max(1.5f, crossR * 0.12f); // толщина перекрестья в UV как в сцене

            Color markerColor = new Color32(0xFF, 0xBC, 0x51, 0xFF); // #FFBC51
            Color spotCenter = new Color(markerColor.r, markerColor.g, markerColor.b, 0.42f);
            Color spotOuter = new Color(markerColor.r, markerColor.g, markerColor.b, 0f);

            // Spot (мягкий круг) — больше перекрестья
            int segments = 48;
            GL.Begin(GL.TRIANGLES);
            for (int i = 0; i < segments; i++)
            {
                float a0 = (i / (float)segments) * Mathf.PI * 2f;
                float a1 = ((i + 1) / (float)segments) * Mathf.PI * 2f;
                Vector2 d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0));
                Vector2 d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));

                Vector3 c = new Vector3(px, py, 0f);
                Vector3 i0 = new Vector3(px + d0.x * spotInnerR, py + d0.y * spotInnerR, 0f);
                Vector3 i1 = new Vector3(px + d1.x * spotInnerR, py + d1.y * spotInnerR, 0f);
                Vector3 o0 = new Vector3(px + d0.x * spotOuterR, py + d0.y * spotOuterR, 0f);
                Vector3 o1 = new Vector3(px + d1.x * spotOuterR, py + d1.y * spotOuterR, 0f);

                // inner core
                GL.Color(spotCenter); GL.Vertex(c);
                GL.Color(spotCenter); GL.Vertex(i0);
                GL.Color(spotCenter); GL.Vertex(i1);

                // feather to outer radius
                GL.Color(spotCenter); GL.Vertex(i0);
                GL.Color(spotOuter);  GL.Vertex(o0);
                GL.Color(spotOuter);  GL.Vertex(o1);

                GL.Color(spotCenter); GL.Vertex(i0);
                GL.Color(spotOuter);  GL.Vertex(o1);
                GL.Color(spotCenter); GL.Vertex(i1);
            }
            GL.End();

            // Crosshair (той же толщины и цвета, что и в shader)
            GL.Begin(GL.QUADS);
            GL.Color(markerColor);
            // horizontal
            GL.Vertex3(px - crossR, py - crossHalfW, 0);
            GL.Vertex3(px + crossR, py - crossHalfW, 0);
            GL.Vertex3(px + crossR, py + crossHalfW, 0);
            GL.Vertex3(px - crossR, py + crossHalfW, 0);
            // vertical
            GL.Vertex3(px - crossHalfW, py - crossR, 0);
            GL.Vertex3(px + crossHalfW, py - crossR, 0);
            GL.Vertex3(px + crossHalfW, py + crossR, 0);
            GL.Vertex3(px - crossHalfW, py + crossR, 0);
            GL.End();
        }

        PreviewShellData GetPreviewShellCache(Mesh mesh, int channel)
        {
            if (mesh == null) return null;
            long key = ((long)mesh.GetInstanceID() << 8) ^ (uint)channel;
            if (previewShellDataCache.TryGetValue(key, out var cached))
                return cached;

            var uv = RdUvCached(mesh, channel);
            var triangles = GetTrianglesCached(mesh);
            if (uv == null || triangles == null || triangles.Length < 3) return null;

            List<UvShell> shells;
            try { shells = UvShellExtractor.Extract(uv, triangles, computeDescriptors: true); }
            catch { return null; }

            var faceToShell = new Dictionary<int, int>(triangles.Length / 3);
            var shellById = new Dictionary<int, UvShell>(shells.Count);
            var bounds = new Bounds[shells.Count];
            for (int i = 0; i < shells.Count; i++)
            {
                var shell = shells[i];
                shellById[shell.shellId] = shell;
                bool hasPoint = false;
                Bounds b = new Bounds(Vector3.zero, Vector3.zero);
                foreach (int fi in shell.faceIndices)
                {
                    faceToShell[fi] = shell.shellId;
                    int t0 = fi * 3;
                    if (t0 + 2 >= triangles.Length) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = triangles[t0 + k];
                        if (vi < 0 || vi >= uv.Length) continue;
                        Vector3 p = uv[vi];
                        if (!hasPoint) { b = new Bounds(p, Vector3.zero); hasPoint = true; }
                        else b.Encapsulate(p);
                    }
                }
                bounds[i] = b;
            }

            cached = new PreviewShellData { shells = shells, faceToShell = faceToShell, shellById = shellById, shellBounds = bounds, triangles = triangles, uvs = uv };
            previewShellDataCache[key] = cached;
            return cached;
        }

        bool TryPickUvHit(Vector2 uv, ref ShellUvHit hit)
        {
            var ee = ForLod(pvLod);
            int checkedTri = 0;
            bool fallbackAssigned = false;
            ShellUvHit fallback = default;

            foreach (var entry in ee)
            {
                Mesh mesh = DMesh(entry);
                if (mesh == null) continue;

                var cache = GetPreviewShellCache(mesh, pvChannel);
                if (cache == null || cache.shells == null) continue;

                if (mesh.GetInstanceID() == lastHitMeshId && lastHitShellId >= 0)
                {
                    if (TryPickInShell(entry, cache, uv, lastHitShellId, ref checkedTri, ref hit))
                        return true;
                }

                for (int si = 0; si < cache.shells.Count; si++)
                {
                    var sb = cache.shellBounds[si];
                    if (sb.size == Vector3.zero) continue;
                    if (uv.x < sb.min.x || uv.x > sb.max.x || uv.y < sb.min.y || uv.y > sb.max.y)
                        continue;

                    int shellId = cache.shells[si].shellId;
                    if (TryPickInShell(entry, cache, uv, shellId, ref checkedTri, ref hit))
                        return true;

                    if (!fallbackAssigned)
                    {
                        fallbackAssigned = true;
                        fallback = new ShellUvHit
                        {
                            meshEntry = entry,
                            shellId = shellId,
                            faceIndex = -1,
                            uvHit = uv,
                            barycentric = new Vector3(1f / 3f, 1f / 3f, 1f / 3f)
                        };
                    }
                }

                if (checkedTri >= TRI_PICK_BUDGET)
                    break;
            }

            if (fallbackAssigned)
            {
                hit = fallback;
                lastHitMeshId = fallback.meshEntry != null && DMesh(fallback.meshEntry) != null ? DMesh(fallback.meshEntry).GetInstanceID() : -1;
                lastHitShellId = fallback.shellId;
                return true;
            }

            return false;
        }

        bool TryPickInShell(MeshEntry entry, PreviewShellData cache, Vector2 uv, int shellId, ref int checkedTri, ref ShellUvHit hit)
        {
            if (!cache.shellById.TryGetValue(shellId, out var shell)) return false;

            foreach (int fi in shell.faceIndices)
            {
                if (checkedTri++ >= TRI_PICK_BUDGET)
                    return false;

                int t0 = fi * 3;
                if (t0 + 2 >= cache.triangles.Length) continue;
                int a = cache.triangles[t0];
                int b = cache.triangles[t0 + 1];
                int c = cache.triangles[t0 + 2];
                if (!TOk(cache.uvs, cache.uvs.Length, a, b, c)) continue;
                if (TryBarycentric(uv, cache.uvs[a], cache.uvs[b], cache.uvs[c], out Vector3 bary))
                {
                    hit = new ShellUvHit { meshEntry = entry, shellId = shellId, faceIndex = fi, uvHit = uv, barycentric = bary };
                    var m = DMesh(entry);
                    lastHitMeshId = m != null ? m.GetInstanceID() : -1;
                    lastHitShellId = shellId;
                    return true;
                }
            }
            return false;
        }

        bool TryBarycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 bary)
        {
            Vector2 v0 = b - a;
            Vector2 v1 = c - a;
            Vector2 v2 = p - a;
            float d00 = Vector2.Dot(v0, v0);
            float d01 = Vector2.Dot(v0, v1);
            float d11 = Vector2.Dot(v1, v1);
            float d20 = Vector2.Dot(v2, v0);
            float d21 = Vector2.Dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denom) < 1e-8f)
            {
                bary = default;
                return false;
            }
            float v = (d11 * d20 - d01 * d21) / denom;
            float w = (d00 * d21 - d01 * d20) / denom;
            float u = 1f - v - w;
            bary = new Vector3(u, v, w);
            const float eps = -1e-4f;
            return u >= eps && v >= eps && w >= eps;
        }

        void GlOutlineShell(float ox, float oy, float sz, Vector2[] uv, int[] t, int uN, PreviewShellData cache, int shellId, Color color, float width)
        {
            if (cache == null || cache.shellById == null) return;
            if (!cache.shellById.TryGetValue(shellId, out var shell)) return;

            GL.Begin(GL.LINES);
            GL.Color(color);
            foreach (int fi in shell.faceIndices)
            {
                int a0=t[fi*3],a1=t[fi*3+1],a2=t[fi*3+2];
                if (!TOk(uv,uN,a0,a1,a2)) continue;
                Vx(ox,oy,sz,uv[a0]); Vx(ox,oy,sz,uv[a1]);
                Vx(ox,oy,sz,uv[a1]); Vx(ox,oy,sz,uv[a2]);
                Vx(ox,oy,sz,uv[a2]); Vx(ox,oy,sz,uv[a0]);
            }
            GL.End();
        }


        /// <summary>Get source shell descriptors for ShellMatch coloring. Returns null if unavailable.</summary>
        ShellDescriptor[] GetSourceDescriptors(MeshEntry entry)
        {
            if (entry == null) return null;

            // Restored from sidecar
            if (entry.restoredSourceDescriptors != null)
                return entry.restoredSourceDescriptors;

            // Live: compute from source mesh and cache on entry
            if (entry.lodIndex == sourceLodIndex) return null; // source LOD doesn't have "source descriptors"

            var srcEntries = ForLod(sourceLodIndex);
            MeshEntry se = null;
            if (!string.IsNullOrEmpty(entry.meshGroupKey))
                se = srcEntries.FirstOrDefault(s => s.meshGroupKey == entry.meshGroupKey);
            if (se == null)
            {
                int ti = ForLod(entry.lodIndex).IndexOf(entry);
                se = ti < srcEntries.Count ? srcEntries[ti] : (srcEntries.Count > 0 ? srcEntries[0] : null);
            }
            if (se == null) return null;

            Mesh srcMesh = se.repackedMesh ?? se.originalMesh;
            if (srcMesh == null) return null;

            var srcUv0 = new List<Vector2>();
            srcMesh.GetUVs(0, srcUv0);
            if (srcUv0.Count != srcMesh.vertexCount) return null;

            try
            {
                var srcShells = UvShellExtractor.Extract(srcUv0.ToArray(), srcMesh.triangles, computeDescriptors: true);
                var descs = new ShellDescriptor[srcShells.Count];
                for (int i = 0; i < srcShells.Count; i++)
                    descs[i] = srcShells[i].descriptor;
                entry.restoredSourceDescriptors = descs; // cache for next frame
                return descs;
            }
            catch { return null; }
        }

        int GetShellColorKey(UvShell shell, MeshEntry entry)
        {
            int meshId = 0;
            var mesh = entry != null ? DMesh(entry) : null;
            if (mesh != null) meshId = mesh.GetInstanceID();

            long cacheKey = ((long)meshId << 32) | (uint)shell.shellId;
            if (!shellColorKeyCacheDirty && shellColorKeyCache.TryGetValue(cacheKey, out int cached))
                return cached;

            int result;

            // Priority 1: Source shell mapping — ensures fragments of split shells
            // share the same color as their source. Uses source descriptor hash
            // when available for stability across reimports.
            var map = entry?.shellTransferResult?.vertexToSourceShell;
            if (map != null && shell?.vertexIndices != null && shell.vertexIndices.Count > 0)
            {
                // Find most frequent source shell without LINQ
                int bestKey = -1, bestCount = 0;
                var freq = new Dictionary<int, int>();
                foreach (int v in shell.vertexIndices)
                {
                    if (v < 0 || v >= map.Length) continue;
                    int srcShell = map[v];
                    if (srcShell < 0) continue;
                    freq.TryGetValue(srcShell, out int c);
                    c++;
                    freq[srcShell] = c;
                    if (c > bestCount || (c == bestCount && srcShell < bestKey))
                    {
                        bestCount = c;
                        bestKey = srcShell;
                    }
                }
                if (bestKey >= 0)
                {
                    // Map source shell index → source descriptor hash for stability
                    var srcDescs = GetSourceDescriptors(entry);
                    result = (srcDescs != null && bestKey < srcDescs.Length)
                        ? Mathf.Abs(srcDescs[bestKey].stableHash)
                        : bestKey;
                }
                else
                {
                    result = shell.hasDescriptor
                        ? Mathf.Abs(shell.descriptor.stableHash)
                        : Mathf.Abs((shell.shellId * 73856093) ^ (meshId * 19349663));
                }
            }
            // Priority 2: Own descriptor hash (source LOD, or no transfer data)
            else if (shell.hasDescriptor)
            {
                result = Mathf.Abs(shell.descriptor.stableHash);
            }
            // Fallback: hash-based on shellId
            else
            {
                result = Mathf.Abs((shell.shellId * 73856093) ^ (meshId * 19349663));
            }

            shellColorKeyCache[cacheKey] = result;
            return result;
        }

        void GlFillSh(float ox, float oy, float sz, Mesh mesh, int fN, int uN, MeshEntry entry, int hoverShellId, int selectedShellId)
        {
            var cache = GetPreviewShellCache(mesh, pvChannel);
            if (cache == null || cache.shells == null) return;
            var uv = cache.uvs;
            var t = cache.triangles;

            int tot=0, b=0;
            GL.Begin(GL.TRIANGLES);
            foreach (var s in cache.shells)
            {
                if (tot>=MAX_TRI) break;
                int colorKey = GetShellColorKey(s, entry);
                Color c = pal[colorKey % pal.Length];
                if (s.shellId == selectedShellId)
                    c = Color.Lerp(c, Color.white, 0.45f);
                c.a = s.shellId == selectedShellId ? Mathf.Clamp01(fillAlpha * 1.85f) : fillAlpha;
                GL.Color(c);
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

            if (selectedShellId >= 0)
                GlOutlineShell(ox, oy, sz, uv, t, uN, cache, selectedShellId, new Color(1f, .95f, .2f, .95f), 2f);
            if (hoverShellId >= 0 && hoverShellId != selectedShellId)
                GlOutlineShell(ox, oy, sz, uv, t, uN, cache, hoverShellId, new Color(.25f, 1f, .95f, .85f), 1.2f);
        }

        void GlFillSt(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, TriangleStatus[] st)
        {
            int tot=0,b=0; GL.Begin(GL.TRIANGLES);
            for (int f=0; f<fN&&tot<MAX_TRI; f++)
            {
                int a0=t[f*3],a1=t[f*3+1],a2=t[f*3+2];
                if (!TOk(uv,uN,a0,a1,a2)) continue;
                GL.Color(f<st.Length ? SC(st[f]) : cAccept);
                Vx(ox,oy,sz,uv[a0]); Vx(ox,oy,sz,uv[a1]); Vx(ox,oy,sz,uv[a2]);
                tot++; b++; if (b>=BATCH){GL.End();GL.Begin(GL.TRIANGLES);b=0;}
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

        void GlUvBoundary(float ox, float oy, float sz, Mesh mesh, Vector2[] uv, int[] tri, int uN)
        {
            if (mesh == null || uv == null || tri == null || tri.Length < 3) return;

            int id = mesh.GetInstanceID();
            if (!boundaryEdgeCache.TryGetValue(id, out int[] pairs))
            {
                pairs = BuildBoundaryEdgePairs(tri);
                boundaryEdgeCache[id] = pairs;
            }
            if (pairs == null || pairs.Length == 0) return;

            GL.Begin(GL.LINES);
            GL.Color(new Color(1f, .35f, .05f, .9f));
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                int a = pairs[i], b = pairs[i + 1];
                if (a < 0 || b < 0 || a >= uN || b >= uN) continue;
                if (!UOk(uv[a]) || !UOk(uv[b])) continue;
                Vx(ox, oy, sz, uv[a]);
                Vx(ox, oy, sz, uv[b]);
            }
            GL.End();
        }

        static int[] BuildBoundaryEdgePairs(int[] tri)
        {
            if (tri == null || tri.Length < 3) return Array.Empty<int>();

            var counts = new Dictionary<ulong, int>(tri.Length);
            var orient = new Dictionary<ulong, (int a, int b)>(tri.Length);

            for (int i = 0; i + 2 < tri.Length; i += 3)
            {
                AddEdge(tri[i], tri[i + 1], counts, orient);
                AddEdge(tri[i + 1], tri[i + 2], counts, orient);
                AddEdge(tri[i + 2], tri[i], counts, orient);
            }

            var result = new List<int>();
            foreach (var kv in counts)
            {
                if (kv.Value != 1) continue;
                var e = orient[kv.Key];
                result.Add(e.a);
                result.Add(e.b);
            }
            return result.ToArray();
        }

        static void AddEdge(int a, int b, Dictionary<ulong, int> counts, Dictionary<ulong, (int a, int b)> orient)
        {
            if (a == b) return;
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            ulong key = ((ulong)(uint)lo << 32) | (uint)hi;

            counts.TryGetValue(key, out int c);
            counts[key] = c + 1;
            if (!orient.ContainsKey(key)) orient[key] = (a, b);
        }

        void GlFillShellMatch(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, int[] vertShellMap, ShellDescriptor[] sourceDescriptors = null)
        {
            if (vertShellMap == null) return;
            int tot = 0, b = 0;
            GL.Begin(GL.TRIANGLES);
            for (int f = 0; f < fN && tot < MAX_TRI; f++)
            {
                int a0 = t[f*3], a1 = t[f*3+1], a2 = t[f*3+2];
                if (!TOk(uv, uN, a0, a1, a2)) continue;
                int sh = (a0 < vertShellMap.Length) ? vertShellMap[a0] : -1;
                Color nc;
                if (sh < 0)
                {
                    nc = new Color(0.3f, 0.3f, 0.3f, fillAlpha);
                }
                else
                {
                    // Use source descriptor hash for stable color when available
                    int colorKey = (sourceDescriptors != null && sh < sourceDescriptors.Length)
                        ? Mathf.Abs(sourceDescriptors[sh].stableHash)
                        : sh;
                    Color pc = pal[colorKey % pal.Length];
                    nc = new Color(pc.r, pc.g, pc.b, fillAlpha * 1.5f);
                }
                GL.Color(nc);
                Vx(ox, oy, sz, uv[a0]); Vx(ox, oy, sz, uv[a1]); Vx(ox, oy, sz, uv[a2]);
                tot++; b++;
                if (b >= BATCH) { GL.End(); GL.Begin(GL.TRIANGLES); b = 0; }
            }
            GL.End();
        }

        void GlFillValidation(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, TransferValidator.TriIssue[] perTri)
        {
            if (perTri == null) return;
            int tot = 0, b = 0;
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
                GL.Color(nc);
                Vx(ox, oy, sz, uv[a0]); Vx(ox, oy, sz, uv[a1]); Vx(ox, oy, sz, uv[a2]);
                tot++; b++;
                if (b >= BATCH) { GL.End(); GL.Begin(GL.TRIANGLES); b = 0; }
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
            string hoverInfo = hoverHitValid
                ? $" | Hover UV: {uvSpot.x:F4},{uvSpot.y:F4} Shell:{hoveredShellId}"
                : (spotMode ? " | Hover UV: --" : string.Empty);
            EditorGUILayout.LabelField("LOD" + pvLod + " | " + ee.Count + " mesh | V:" + tV + " T:" + tT + " | " + (pvChannel == 0 ? "UV0 (MainTex)" : "UV1 (Lightmap)") + hoverInfo, EditorStyles.miniLabel);

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

        void DrawShellDebugPanel()
        {
            foldShellDebug = EditorGUILayout.Foldout(foldShellDebug, "Shell Debug", true);
            if (!foldShellDebug) return;

            EditorGUI.indentLevel++;
            DrawShellHitInfo("Hovered", hoveredShellDebug);
            GUILayout.Space(4);
            DrawShellHitInfo("Selected", selectedShellDebug);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select hovered"))
            {
                if (hoveredShellDebug != null) selectedShellDebug = CloneHit(hoveredShellDebug);
            }
            if (GUILayout.Button("Clear selection"))
                selectedShellDebug = null;
            using (new EditorGUI.DisabledScope(selectedShellDebug == null || selectedShellDebug.shell == null))
            {
                if (GUILayout.Button("Focus") && selectedShellDebug?.shell != null)
                {
                    var bmin = selectedShellDebug.shell.boundsMin;
                    var bmax = selectedShellDebug.shell.boundsMax;
                    FocusUvBounds(bmin.x, bmin.y, bmax.x, bmax.y);
                }
            }
            EditorGUILayout.EndHorizontal();

            if (fillMode == FillMode.ShellMatch && selectedShellDebug?.entry?.shellTransferResult?.vertexToSourceShell != null && selectedShellDebug.shell != null)
            {
                var map = selectedShellDebug.entry.shellTransferResult.vertexToSourceShell;
                var freq = new Dictionary<int, int>();
                int total = 0;
                foreach (int v in selectedShellDebug.shell.vertexIndices)
                {
                    if (v < 0 || v >= map.Length) continue;
                    int src = map[v];
                    if (!freq.ContainsKey(src)) freq[src] = 0;
                    freq[src]++;
                    total++;
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("ShellMatch top-3 source shells:", EditorStyles.miniBoldLabel);
                if (total <= 0)
                {
                    EditorGUILayout.LabelField("Нет данных по вершинам.", EditorStyles.miniLabel);
                }
                else
                {
                    foreach (var kv in freq.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Take(3))
                    {
                        float pct = kv.Value * 100f / total;
                        EditorGUILayout.LabelField($"sourceShellId={kv.Key}: {kv.Value} ({pct:F1}%)", EditorStyles.miniLabel);
                    }
                }
            }

            if (selectedShellDebug?.entry?.validationReport?.perTriangle != null && selectedShellDebug.shell != null)
            {
                int issues = 0;
                var perTri = selectedShellDebug.entry.validationReport.perTriangle;
                foreach (int f in selectedShellDebug.shell.faceIndices)
                {
                    if (f < 0 || f >= perTri.Length) continue;
                    if (perTri[f] != TransferValidator.TriIssue.None) issues++;
                }
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField($"Проблемных трис в selected shell: {issues}", EditorStyles.miniLabel);
            }
            EditorGUI.indentLevel--;
        }

        void DrawShellHitInfo(string label, ShellDebugHit hit)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);

            bool hasHit = hit != null && hit.shell != null;
            var shell = hasHit ? hit.shell : null;
            var bmin = hasHit ? shell.boundsMin : Vector2.zero;
            var bmax = hasHit ? shell.boundsMax : Vector2.zero;

            EditorGUILayout.LabelField($"shellId: {(hasHit ? hit.shellId.ToString() : "—")}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"mesh: {(hasHit ? (hit.mesh?.name ?? "<null>") : "—")}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"uv channel: {(hasHit ? "UV" + hit.uvChannel : "—")}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                hasHit
                    ? $"boundsMin/boundsMax: ({bmin.x:F3}, {bmin.y:F3}) / ({bmax.x:F3}, {bmax.y:F3})"
                    : "boundsMin/boundsMax: —",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"bboxArea: {(hasHit ? shell.bboxArea.ToString("F6") : "—")}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"faces count: {(hasHit ? shell.faceIndices.Count.ToString() : "—")}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"vertices count: {(hasHit ? shell.vertexIndices.Count.ToString() : "—")}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                hasHit ? $"UDIM tile: ({hit.tileU}, {hit.tileV})" : "UDIM tile: —",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                hasHit ? $"local in tile: ({hit.localUv.x:F3}, {hit.localUv.y:F3})" : "local in tile: —",
                EditorStyles.miniLabel);
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
                // Switch to first target LOD for preview (and scene model)
                for (int i = 0; i < LodN; i++)
                    if (i != sourceLodIndex && ForLod(i).Count > 0) { SetPreviewLod(i); break; }

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

            accumulatedOverlapHints.Clear();
            for (int li = 0; li < LodN; li++)
                if (li != sourceLodIndex) ExecTransferLod(li);
        }

        Dictionary<int, GroupedShellTransfer.SourceShellInfo[]> shellTransformCache =
            new Dictionary<int, GroupedShellTransfer.SourceShellInfo[]>();

        // Cross-LOD overlap hints: accumulated from previous LODs for consistent source selection.
        List<GroupedShellTransfer.OverlapSourceHint> accumulatedOverlapHints =
            new List<GroupedShellTransfer.OverlapSourceHint>();

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

                    var tr = GroupedShellTransfer.Transfer(tM, sM,
                        accumulatedOverlapHints.Count > 0 ? accumulatedOverlapHints : null);
                    if (tr.uv2 == null) continue;

                    // Accumulate overlap hints for subsequent LODs
                    if (tr.overlapHints != null && tr.overlapHints.Count > 0)
                        accumulatedOverlapHints.AddRange(tr.overlapHints);

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
            previewConflictNotice = null;

            if (checkerEnabled)
            {
                checkerEnabled = false;
                CheckerTexturePreview.Restore();
                Repaint();
                return;
            }

            if (shellColorPreviewEnabled)
            {
                shellColorPreviewEnabled = false;
                ShellColorModelPreview.Restore();
                previewConflictNotice = "Checker включен: Color Shells on Model временно отключен (взаимоисключающие превью).";
                UvtLog.Info("[Preview] Checker enabled, shell-color preview disabled.");
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
                checkerEnabled = false;
                UvtLog.Warn("[Checker] No meshes with UV2. Run Repack first.");
                return;
            }
            CheckerTexturePreview.Apply(entries);
            UpdateSelectedSidecar();
            Repaint();
        }

        void ToggleShellColorPreview()
        {
            previewConflictNotice = null;

            if (shellColorPreviewEnabled)
            {
                shellColorPreviewEnabled = false;
                ShellColorModelPreview.Restore();
                Repaint();
                return;
            }

            if (checkerEnabled)
            {
                checkerEnabled = false;
                CheckerTexturePreview.Restore();
                previewConflictNotice = "Color Shells on Model включен: Checker временно отключен (взаимоисключающие превью).";
                UvtLog.Info("[Preview] Shell-color preview enabled, checker disabled.");
            }

            shellColorPreviewEnabled = true;
            ReapplyShellColorPreview();
            Repaint();
        }

        void ReapplyShellColorPreview()
        {
            if (!shellColorPreviewEnabled) return;

            var entries = new List<(Renderer renderer, Mesh sourceMesh)>();
            foreach (var e in ForLod(pvLod))
            {
                if (!e.include || e.renderer == null) continue;
                Mesh mesh = e.transferredMesh ?? e.repackedMesh ?? e.originalMesh ?? e.fbxMesh;
                if (mesh == null) continue;
                entries.Add((e.renderer, mesh));
            }

            if (entries.Count == 0)
            {
                UvtLog.Warn("[ShellColorPreview] No renderers found for current LOD.");
                shellColorPreviewEnabled = false;
                ShellColorModelPreview.Restore();
                return;
            }

            var palette = new Color32[pal.Length];
            for (int i = 0; i < pal.Length; i++) palette[i] = pal[i];
            ShellColorModelPreview.Apply(entries, palette, shellColorPreviewCache);
        }

        void RestoreAllPreviews()
        {
            if (checkerEnabled || CheckerTexturePreview.IsActive)
                CheckerTexturePreview.Restore();
            if (shellColorPreviewEnabled || ShellColorModelPreview.IsActive)
                ShellColorModelPreview.Restore();

            checkerEnabled = false;
            shellColorPreviewEnabled = false;
            previewConflictNotice = null;
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
            public Vector3[] normals;
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
            // Ground-truth optimized vertex data
            public Vector3[] optimizedPositions;
            public Vector3[] optimizedNormals;
            public Vector4[] optimizedTangents;
            // Shell descriptors (v0.14.0+)
            public ShellDescriptor[] shellDescriptors;
            public int[] vertexToSourceShellDescriptor;
            public int[] targetShellToSourceShellDescriptor;
            public ShellDescriptor[] sourceShellDescriptors;
        }

        void ApplyUv2ToFbx()
        {
            if (lodGroup == null) return;

            // ── Pre-import pass: get raw FBX meshes ──
            // On repeat runs, e.fbxMesh = mf.sharedMesh may be the postprocessed mesh
            // (ReplayOptimization changed vertex count/order). We MUST build the remap
            // from the raw FBX mesh to get correct results. Reimport all FBXs with the
            // postprocessor bypassed so e.fbxMesh has the true raw FBX vertex order.
            {
                var fbxPathSet = new HashSet<string>();
                foreach (var e in meshEntries)
                {
                    if (!e.include) continue;
                    Mesh m = e.fbxMesh ?? e.originalMesh;
                    if (m == null) continue;
                    string p = AssetDatabase.GetAssetPath(m);
                    if (!string.IsNullOrEmpty(p)) fbxPathSet.Add(p);
                }

                if (fbxPathSet.Count > 0)
                {
                    UvtLog.Info($"[Apply] Pre-import: reimporting {fbxPathSet.Count} FBX(es) with bypass to get raw vertex order");
                    foreach (string p in fbxPathSet)
                    {
                        var imp = AssetImporter.GetAtPath(p) as ModelImporter;
                        if (imp == null) continue;

                        if (imp.generateSecondaryUV)
                            imp.generateSecondaryUV = false;

                        // Bypass postprocessor so we get the unmodified FBX mesh
                        Uv2AssetPostprocessor.bypassPaths.Add(p);
                        imp.SaveAndReimport();
                    }

                    // Re-read meshes from reimported FBXs so e.fbxMesh has the raw vertex order
                    foreach (var e in meshEntries)
                    {
                        if (e.meshFilter != null && e.meshFilter.sharedMesh != null)
                            e.fbxMesh = e.meshFilter.sharedMesh;
                    }

                    // Safety: clear any leftover bypass entries
                    Uv2AssetPostprocessor.bypassPaths.Clear();
                }
            }

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

                // Fingerprint: e.fbxMesh is guaranteed to be the raw FBX mesh
                // (pre-import pass reimports with postprocessor bypassed).
                MeshFingerprint fp = null;
                if (e.fbxMesh != null)
                {
                    fp = MeshFingerprint.Compute(e.fbxMesh);
                }

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
                    sourceFingerprint = fp,
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

                    // Store optimized mesh geometry as ground truth for positions/normals/tangents.
                    // These don't change during weld (meshopt dedup is byte-exact, EdgeWeld only
                    // removes vertices at same position). UV0 is NOT stored here because weld
                    // modifies UV0 seams — UV0 must come from raw FBX via remap.
                    sidecar.optimizedPositions = optimizedMesh.vertices;
                    sidecar.optimizedNormals = optimizedMesh.normals;
                    sidecar.optimizedTangents = optimizedMesh.tangents;

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

                    // Keep raw FBX positions + UV0 even with replay data — needed to
                    // rebuild the remap if vertex order changes between runs (stale fingerprint).
                    // positions/uv0 here are from e.originalMesh (optimized); we need the RAW FBX ones.
                    if (sidecar.hasReplayData)
                    {
                        sidecar.positions = e.fbxMesh.vertices;
                        sidecar.normals = e.fbxMesh.normals;
                        var rawUv0List = new List<Vector2>();
                        e.fbxMesh.GetUVs(0, rawUv0List);
                        sidecar.uv0 = rawUv0List.Count == e.fbxMesh.vertexCount ? rawUv0List.ToArray() : null;
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

                // ── Shell descriptors: compute and persist stable shell identity ──
                try
                {
                    // Compute target shell descriptors from the result mesh UV0
                    var descUv0 = new List<Vector2>();
                    resultMesh.GetUVs(0, descUv0);
                    if (descUv0.Count == resultMesh.vertexCount)
                    {
                        var descTris = resultMesh.triangles;
                        var descShells = UvShellExtractor.Extract(descUv0.ToArray(), descTris, computeDescriptors: true);
                        sidecar.shellDescriptors = new ShellDescriptor[descShells.Count];
                        for (int si = 0; si < descShells.Count; si++)
                            sidecar.shellDescriptors[si] = descShells[si].descriptor;

                        UvtLog.Verbose($"[Apply] '{meshName}': {descShells.Count} shell descriptors computed");
                    }

                    // Persist shell match mapping from transfer result (target LODs only)
                    if (e.shellTransferResult != null)
                    {
                        var tr = e.shellTransferResult;

                        // vertexToSourceShell → vertexToSourceShellDescriptor
                        if (tr.vertexToSourceShell != null)
                            sidecar.vertexToSourceShellDescriptor = (int[])tr.vertexToSourceShell.Clone();

                        // targetShellToSourceShell → targetShellToSourceShellDescriptor
                        if (tr.targetShellToSourceShell != null)
                            sidecar.targetShellToSourceShellDescriptor = (int[])tr.targetShellToSourceShell.Clone();

                        // Compute source shell descriptors from the source mesh used for transfer
                        MeshEntry se = null;
                        var srcE = ForLod(sourceLodIndex);
                        if (!string.IsNullOrEmpty(e.meshGroupKey))
                            se = srcE.FirstOrDefault(s => s.meshGroupKey == e.meshGroupKey);
                        if (se == null)
                        {
                            int ti2 = ForLod(e.lodIndex).IndexOf(e);
                            se = ti2 < srcE.Count ? srcE[ti2] : (srcE.Count > 0 ? srcE[0] : null);
                        }
                        if (se != null)
                        {
                            Mesh srcMesh = se.repackedMesh ?? se.originalMesh;
                            if (srcMesh != null)
                            {
                                var srcUv0 = new List<Vector2>();
                                srcMesh.GetUVs(0, srcUv0);
                                if (srcUv0.Count == srcMesh.vertexCount)
                                {
                                    var srcTris = srcMesh.triangles;
                                    var srcShells = UvShellExtractor.Extract(srcUv0.ToArray(), srcTris, computeDescriptors: true);
                                    sidecar.sourceShellDescriptors = new ShellDescriptor[srcShells.Count];
                                    for (int si = 0; si < srcShells.Count; si++)
                                        sidecar.sourceShellDescriptors[si] = srcShells[si].descriptor;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    UvtLog.Warn($"[Apply] '{meshName}': shell descriptor computation failed: {ex.Message}");
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
                            vertNormals = entry.normals,
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
                            optimizedPositions = entry.optimizedPositions,
                            optimizedNormals = entry.optimizedNormals,
                            optimizedTangents = entry.optimizedTangents,
                            shellDescriptors = entry.shellDescriptors,
                            vertexToSourceShellDescriptor = entry.vertexToSourceShellDescriptor,
                            targetShellToSourceShellDescriptor = entry.targetShellToSourceShellDescriptor,
                            sourceShellDescriptors = entry.sourceShellDescriptors,
                        });
                        totalMeshes++;
                    }

                    EditorUtility.SetDirty(data);
                    AssetDatabase.SaveAssets();

                    // Disable Read/Write — mesh data is accessible during OnPostprocessModel
                    // regardless of isReadable (import context). ReplayOptimization avoids
                    // mesh.Clear() to preserve this context. This frees CPU RAM in builds.
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
                    used[candidates[0]] = true;
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
                    if (bestIdx >= 0) { remap[i] = bestIdx; used[bestIdx] = true; matched++; }
                }
                else
                {
                    // No UV0 — pick first candidate
                    remap[i] = candidates[0];
                    used[candidates[0]] = true;
                    matched++;
                }
            }

            // Pass 2: nearest-neighbor fallback for bucket boundary misses
            // and edge-welded vertices whose positions were averaged during optimization.
            // IMPORTANT: only map to optimized indices NOT already covered by pass 1,
            // otherwise the approximate match would overwrite correct vertex data during replay.
            if (matched < rawCount)
            {
                for (int i = 0; i < rawCount; i++)
                {
                    if (remap[i] >= 0) continue;
                    float bestDist = float.MaxValue;
                    int bestIdx = -1;
                    for (int j = 0; j < optCount; j++)
                    {
                        if (used[j]) continue; // skip opt indices already covered by pass 1
                        float d = Vector3.SqrMagnitude(rawPos[i] - optPos[j]);
                        if (d < bestDist) { bestDist = d; bestIdx = j; }
                    }
                    if (bestIdx >= 0 && bestDist < 1e-2f)
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
            RestoreAllPreviews();
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
                e.restoredSourceDescriptors = null;
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
            uvPreviewShellCache.Clear();
            previewShellDataCache.Clear();
            occupiedTilesPerMesh.Clear();
            shellColorPreviewCache.Clear();
            shellColorKeyCache.Clear();
            ClearHoverState(false);
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
            if (checkerEnabled) ReapplyCheckerToSelection();
            if (shellColorPreviewEnabled) ReapplyShellColorPreview();

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

            // Delete sidecars first (all of them before any reimport)
            int deleted = 0;
            foreach (string fbxPath in fbxPaths)
            {
                string sp = Uv2DataAsset.GetSidecarPath(fbxPath);
                if (AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sp) != null)
                {
                    AssetDatabase.DeleteAsset(sp);
                    deleted++;
                }
            }

            // Flush asset database so postprocessor won't find cached sidecars
            if (deleted > 0)
                AssetDatabase.Refresh();

            // Sync import settings with Apply — ensures meshes have the same vertex
            // order as they will during Apply. Without this, vertex order can change
            // between Reset and Apply, causing stale fingerprints and remap mismatches.
            foreach (string fbxPath in fbxPaths)
            {
                var imp = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                if (imp == null) continue;
                bool changed = false;
                if (imp.generateSecondaryUV)
                {
                    imp.generateSecondaryUV = false;
                    UvtLog.Verbose($"[Reset] Disabled generateSecondaryUV on '{fbxPath}'");
                    changed = true;
                }
                if (imp.isReadable)
                {
                    imp.isReadable = false;
                    UvtLog.Verbose($"[Reset] Disabled Read/Write on '{fbxPath}'");
                    changed = true;
                }
                _ = changed; // settings applied during reimport below
            }

            // Now reimport FBXs — postprocessor will find no sidecars → no UV2 injection
            foreach (string fbxPath in fbxPaths)
                AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);

            // Reset working copies
            ResetWorkingCopies();

            // Clear checker
            RestoreAllPreviews();

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
            }

            // Flush asset database so postprocessor won't find cached sidecars
            if (deleted > 0)
                AssetDatabase.Refresh();

            foreach (string fbxPath in fbxPaths)
                AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);

            AssetDatabase.Refresh();
            UvtLog.Info($"[Reset] Deleted {deleted} sidecar(s), reimported {fbxPaths.Count} FBX");
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
