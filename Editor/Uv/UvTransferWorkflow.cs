// UvTransferWorkflow.cs — UV2 Lightmap Transfer tool for Mesh Lab.
// Setup → Repack → Transfer → Apply pipeline.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    public class UvTransferWorkflow : IBenchmarkHost
    {
        const string CancelButton = "Cancel";

        UvToolContext ctx;
        UvCanvasView canvas;

        // Surface-area scans materialize mesh vertex/index data and are far too
        // expensive to run on every OnGUI repaint. Cache the result keyed by the
        // exact mesh references it was computed from, and recompute only when
        // that set changes (or when an actual repack refreshes it).
        readonly List<Mesh> _areaPreviewMeshes = new List<Mesh>();
        double _areaPreview;
        bool _hasAreaPreview;

        public Action RequestRepaint { get; set; }

        internal static bool IsBruteForcePackAvailable(int internalOversample)
        {
            int oversample = internalOversample > 0 ? internalOversample : 1;
            return oversample <= 1;
        }

        internal static bool HasIncludedTransferTargets(IEnumerable<MeshEntry> entries, int sourceLodIndex)
        {
            if (entries == null) return false;
            foreach (var e in entries)
            {
                if (e == null) continue;
                if (!e.include) continue;
                if (e.lodIndex == sourceLodIndex) continue;
                if (e.originalMesh == null) continue;
                return true;
            }
            return false;
        }

        internal static bool CanApplyUv2(bool hasRepack, bool hasTransfer)
        {
            return hasRepack || hasTransfer;
        }

        List<Mesh> GetRepackSourceMeshes()
        {
            return ctx.ForLod(ctx.SourceLodIndex)
                .Where(e => e.originalMesh != null)
                .Select(e => e.originalMesh)
                .ToList();
        }

        bool TryGetAreaPreview(List<Mesh> meshes, out double area)
        {
            bool sameMeshes = _hasAreaPreview && meshes.Count == _areaPreviewMeshes.Count;
            for (int i = 0; sameMeshes && i < meshes.Count; i++)
                sameMeshes = ReferenceEquals(meshes[i], _areaPreviewMeshes[i]);

            area = sameMeshes ? _areaPreview : 0.0;
            return sameMeshes;
        }

        double CacheAreaPreview(List<Mesh> meshes, double area)
        {
            _areaPreview = area;
            _areaPreviewMeshes.Clear();
            _areaPreviewMeshes.AddRange(meshes);
            _hasAreaPreview = true;
            return _areaPreview;
        }

        /// <summary>
        /// Cached total 3D surface area of the source-LOD meshes. Recomputed only
        /// when the mesh set changes, so a repaint no longer copies every vertex
        /// and index array. Draws no controls — the IMGUI control count must not
        /// depend on cache state.
        /// </summary>
        double GetSourceAreaPreview()
        {
            var meshes = GetRepackSourceMeshes();
            if (TryGetAreaPreview(meshes, out double area)) return area;
            return CacheAreaPreview(meshes, MeshAreaHelper.ComputeTotal3DAreaMeters(meshes));
        }

        // ── Sweep / benchmark host (Editor/Bench): the runners drive this tab's pipeline ──
        UvToolContext ISweepHost.Context => ctx;
        public SymmetrySplitShells.ThresholdMode SymmetrySplitMode { get; set; } = SymmetrySplitShells.ThresholdMode.LegacyFixed;
        void ISweepHost.ResetWorkingCopies() => ResetWorkingCopies();
        void ISweepHost.RunPipeline(string runLabel) => ExecFullPipeline(runLabel);
        void IBenchmarkHost.Bind(LODGroup lodGroup) { ctx.Refresh(lodGroup); OnRefresh(); }

        // ── Internal tab ──
        enum Tab { Setup, Repack, Transfer }
        Tab tab = Tab.Setup;

        // ── UV0 analysis ──
        readonly Dictionary<int, Uv0Report> uv0Reports = new Dictionary<int, Uv0Report>();
        bool uv0Analyzed, uv0Welded;

        // ── Foldouts ──
        readonly Dictionary<int, bool> lodFoldouts = new Dictionary<int, bool>();
        readonly Dictionary<int, bool> transferLodFoldouts = new Dictionary<int, bool>();
        readonly Dictionary<int, bool> reportLodFoldouts = new Dictionary<int, bool>();
        bool foldUv0Analysis;
        bool foldValidationOverlay;
        bool splitTargetsInSymmetryStep;
        bool skipSymmetrySplitStep;
        readonly HashSet<int> lastSymmetrySplitLods = new HashSet<int>();

        // ── Pipeline stage toggles (Setup tab) ──
        // Each toggle controls whether the corresponding stage runs as part
        // of the Full Pipeline. They default ON; the user can deselect a
        // stage to skip it (useful for "transfer only" or "repack only"
        // runs without invoking Weld every time).
        bool stageRunAnalyzeUv0 = true;
        bool stageRunWeldUv0    = true;
        bool stageRunRepack     = true;
        bool stageRunTransfer   = true;

        // Weld stage sub-step: meshopt binary-equivalence dedup +
        // GPU cache/overdraw/fetch reorder. This is NOT a UV weld — it
        // removes vertices that are byte-identical in position + normal
        // + uv0 (a GPU optimisation per meshoptimizer's
        // generateVertexRemap, which the library docs explicitly warn
        // is unsuitable for attribute-seam handling). The actual UV-aware
        // seam weld is Uv0Analyzer.UvEdgeWeld. Kept ON by default to
        // preserve prior behaviour, but now a separate, clearly-labelled
        // toggle so the operator can run the pure UV weld alone.
        bool stageWeldRunMeshopt = true;

        // Per-stage outcome from the most recent ExecFullPipeline run.
        // Drawn as a small status icon at the right of each stage row.
        enum StageStatus { Idle, Running, Success, Failed, Skipped }
        const int kStageCount = 6; // 1..5 are used; index 0 unused for clarity
        readonly StageStatus[] stageOutcome = new StageStatus[kStageCount];

        // In-flight gate for fire-and-forget async pipeline operations.
        // The "Run Full Pipeline", "Run Repack only", "Run Transfer only",
        // "Repack All" (Repack tab), and "Transfer All Targets" (Transfer
        // tab) buttons all schedule async work that yields during native
        // pack / shell transfer. Without a gate, a second click while the
        // first run is in flight launches an interleaving second run that
        // mutates shared state (stageOutcome, MeshEntries, caches,
        // ctx.HasRepack/HasTransfer) and corrupts results. The buttons
        // wrap themselves in EditorGUI.DisabledScope on this flag AND the
        // FireAndForget helper short-circuits if it's already set, so even
        // a stale event reaching the click path can't double-trigger.
        bool _pipelineInFlight;

        /// <summary>
        /// Schedule a fire-and-forget async pipeline action with: (a) an
        /// in-flight gate that suppresses double-clicks, (b) Task fault
        /// observation that logs unhandled exceptions through UvtLog so the
        /// editor never silently aborts mid-pipeline, and (c) automatic UI
        /// state reset (UvProgress.Fail + Repaint) on failure.
        /// </summary>
        void FireAndForget(System.Func<Task> action, string label)
        {
            if (_pipelineInFlight)
            {
                UvtLog.Warn($"[Pipeline] '{label}' ignored — another pipeline operation is already running.");
                return;
            }
            _pipelineInFlight = true;
            try
            {
                var task = action();
                // Continuation runs on the editor main thread courtesy of
                // UnitySynchronizationContext, so it's safe to touch the
                // flag, UvProgress, and Repaint directly. The fall-back to
                // ExecuteSynchronously covers the case where the Task is
                // already complete at attachment time (sync path through
                // useAsync=false would land here).
                task.ContinueWith(t =>
                {
                    _pipelineInFlight = false;
                    if (t.IsFaulted)
                    {
                        var ex = t.Exception?.GetBaseException();
                        UvtLog.Error($"[Pipeline] '{label}' failed: {ex?.Message}");
                        if (ex != null) UvtLog.Error(ex.StackTrace);
                        // If the inner code didn't already close its
                        // UvProgress scope, fail it so the strip stops
                        // showing a stale "running…" state.
                        if (UvProgress.IsActive) UvProgress.Fail(ex?.Message ?? "error");
                    }
                    RequestRepaint?.Invoke();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (System.Exception ex)
            {
                // Synchronous throw before the Task even starts.
                _pipelineInFlight = false;
                UvtLog.Error($"[Pipeline] '{label}' failed to start: {ex.Message}");
                if (UvProgress.IsActive) UvProgress.Fail(ex.Message);
                RequestRepaint?.Invoke();
            }
        }
        Vector2 reportScroll;

        // ── Sidecar ──
        string selectedSidecarPath, selectedResetLabel;
        int setupLodSelectionId = -1;
        int setupRendererSelectionId = -1;
        bool setupSelectionHasRenderers;
        readonly List<(GameObject go, int lodIndex, int rendererCount, int triangleCount)> cachedSetupDetectedLods =
            new List<(GameObject, int, int, int)>();

        // ── Transfer cache ──
        readonly Dictionary<int, GroupedShellTransfer.SourceShellInfo[]> shellTransformCache =
            new Dictionary<int, GroupedShellTransfer.SourceShellInfo[]>();
        sealed class CrossLodHintState
        {
            public readonly List<GroupedShellTransfer.OverlapSourceHint> overlapHints =
                new List<GroupedShellTransfer.OverlapSourceHint>();
            public readonly List<GroupedShellTransfer.CrossLodMatchHint> matchHints =
                new List<GroupedShellTransfer.CrossLodMatchHint>();
        }

        // Shell indices are local to a source mesh. Keep cross-LOD hints isolated
        // to the source/mesh-group pair that produced them.
        readonly Dictionary<(MeshEntry source, string meshGroupKey), CrossLodHintState> crossLodHints =
            new Dictionary<(MeshEntry, string), CrossLodHintState>();

        // ── Preview ──
        // Three mutually-exclusive preview modes. Only one should be active at a time.
        // lightmapBackups stores original renderer materials for restoration when
        // lightmap preview is active.
        bool checkerEnabled, shellColorPreviewEnabled;
        Material lightmapPreviewMat;
        bool lightmapPreviewActive;
        readonly Dictionary<Renderer, Material[]> lightmapBackups = new Dictionary<Renderer, Material[]>();

        // ── Scene ──
        double sceneSpotLastRaycastTime;
        const double sceneSpotThrottleSec = 0.033;
        // Per-hover triangle budget for the SceneView pick. Sized to cover a typical
        // 20-50k tri LOD0 game mesh (and a few of them) so hover keeps working on
        // real assets, while still bounding the per-mousemove cost.
        const int sceneSpotTriangleBudget = 100000;
        double sceneSpotLastBudgetWarnTime;
        const double sceneSpotBudgetWarnIntervalSec = 5.0;

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

            if (ctx.LodGroup == null) { DrawMissingLodGroupSetup(); return; }

            ctx.SourceLodIndex = EditorGUILayout.IntSlider("Source LOD", ctx.SourceLodIndex, 0, ctx.LodCount - 1);

            EditorGUILayout.Space(2);
            DrawSetupLods();

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

            EditorGUILayout.Space(6);
            DrawPipelineSection();

            // ── Output (always visible, production setting) ──
            EditorGUILayout.Space(8);
            H("Output");
            EditorGUI.indentLevel++;
            ctx.PipeSettings.saveNewMeshAssets = EditorGUILayout.Toggle("Save Assets", ctx.PipeSettings.saveNewMeshAssets);
            if (ctx.PipeSettings.saveNewMeshAssets)
                ctx.PipeSettings.savePath = EditorGUILayout.TextField("Path", ctx.PipeSettings.savePath);
            EditorGUI.indentLevel--;

            // ── Debug / diagnostics ──
            // Hidden by default — toggleable from Project Settings ▸ Mesh Lab
            // ▸ Developer. Houses UV0 Analysis & Fix; the sweep, the benchmark
            // and the log filters live in the Diagnostics tab.
            if (DebugUi.Enabled)
                DrawSetupDebugSection();
        }

        void DrawMissingLodGroupSetup()
        {
            var selected = Selection.activeGameObject;
            var siblings = LodGroupUtility.FindLodSiblings(selected);

            if (siblings != null && siblings.Count > 0)
            {
                DrawDetectedLodGroupSetup(selected, siblings);
            }
            else if (LodGroupUtility.HasAmbiguousLodChains(selected))
            {
                EditorGUILayout.HelpBox("Multiple LOD chains detected. Select a mesh from the chain to create its LODGroup.", MessageType.Info);
            }
            else if (selected != null && SetupSelectionHasRenderers(selected))
            {
                DrawRendererLodGroupSetup(selected);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Assign LODGroup or select a GameObject.",
                    MessageType.Info);
            }
        }

        void DrawDetectedLodGroupSetup(GameObject selected, List<(GameObject go, int lodIndex)> siblings)
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
                var lodGroup = LodGroupUtility.CreateLodGroupStatic(siblings);
                ctx.Refresh(lodGroup);
                OnRefresh();
                RequestRepaint?.Invoke();
            }
            GUI.backgroundColor = bgc;
        }

        void DrawRendererLodGroupSetup(GameObject selected)
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
                var lodGroup = LodGroupUtility.CreateLodGroupFromRenderers(selected);
                if (lodGroup != null)
                {
                    ctx.Refresh(lodGroup);
                    OnRefresh();
                    RequestRepaint?.Invoke();
                }
            }
            GUI.backgroundColor = bgc;
        }

        void DrawSetupLods()
        {
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
                    DrawSetupMeshRow(e);
                }
            }

        }

        static void DrawSetupMeshRow(MeshEntry e)
        {
            EditorGUILayout.BeginHorizontal();
            e.include = EditorGUILayout.Toggle(e.include, GUILayout.Width(14));
            string badge = SetupMeshBadge(e);
            string name = e.renderer.name;
            if (name.Length > 22) name = name.Substring(0, 20) + "..";
            EditorGUILayout.LabelField(badge + name, EditorStyles.miniLabel, GUILayout.MinWidth(60));
            var m = e.originalMesh;
            EditorGUILayout.LabelField("V:" + m.vertexCount + " T:" + MeshHygieneUtility.GetTriangleCount(m), EditorStyles.miniLabel, GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();
        }

        static string SetupMeshBadge(MeshEntry entry)
        {
            if (entry.repackedMesh != null) return "[R]";
            if (entry.transferredMesh != null) return "[T]";
            if (entry.wasWelded) return "[W]";
            return entry.hasExistingUv2 ? "[UV2]" : "";
        }

        // ──────────────── Setup tab debug section ──────────────────────
        //
        // UV0 Analysis & Fix — a diagnostic that edits this tab's working
        // meshes, so it stays here. Gated by Show Debug UI (DebugUi.Enabled)
        // so shipping artists see a clean Setup tab.
        void DrawSetupDebugSection()
        {
            EditorGUILayout.Space(10);
            DebugUi.Banner();
            EditorGUILayout.Space(4);
            foldUv0Analysis = EditorGUILayout.Foldout(foldUv0Analysis, "UV0 Analysis & Fix", true);
            if (!foldUv0Analysis) return;
            ColorBtn(new Color(.5f,.7f,.9f), "Analyze UV0", 22, ExecAnalyzeUv0);
            if (!uv0Analyzed) return;
            bool anyIssues = DrawUv0Reports();
            bool hasTargetLods = ctx.MeshEntries.Any(e => e.include && e.lodIndex != ctx.SourceLodIndex);
            if ((anyIssues || hasTargetLods) && !uv0Welded)
                ColorBtn(new Color(.9f,.7f,.2f), "Weld (false seams + source-guided)", 22,
                    () => ExecWeldUv0(runMeshoptFirst: stageWeldRunMeshopt));
            else if (uv0Welded)
                EditorGUILayout.LabelField("UV0 welded", EditorStyles.miniLabel);
        }

        bool DrawUv0Reports()
        {
            bool anyIssues = false;
            foreach (var r in uv0Reports.Values)
            {
                EditorGUILayout.LabelField(r.meshName + ": " + r.totalShells + " shells", EditorStyles.miniLabel);
                if (r.falseSeamPairs > 0) { anyIssues = true; EditorGUILayout.LabelField($"  {r.falseSeamPairs} false seams", EditorStyles.miniLabel); }
                if (!r.HasIssues) EditorGUILayout.LabelField("  No issues", EditorStyles.miniLabel);
            }
            return anyIssues;
        }

        // ──────────────── Pipeline section (Setup tab) ────────────────
        //
        // Replaces the old freestanding "Repack" header + scattered SymSplit
        // toggles with a single stage-oriented panel. Each stage:
        //   • can be toggled on/off (skipped from the Full Pipeline run);
        //   • has its specific settings nested directly underneath when on;
        //   • shows a coloured stripe on the left for at-a-glance state.
        // The big "Run Full Pipeline" button at the bottom drives every
        // enabled stage in order: Analyze → Weld → SymSplit → Repack → Transfer.
        void DrawPipelineSection()
        {
            EditorGUILayout.LabelField("Pipeline", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Toggle stages to include in Run Full Pipeline. Stage-specific settings appear when enabled.",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(2);

            // 1. Analyze UV0 — diagnostic only, cheap.
            DrawStageRow(1, "Analyze UV0",
                "Diagnose UV0 seams and count shells. Cheap; recommended to leave on.",
                ref stageRunAnalyzeUv0, hasSettings: false, drawSettings: null);

            // 2. Weld UV0 — UV-aware false-seam weld (+ optional meshopt
            //    GPU dedup as a clearly-separated sub-step).
            DrawStageRow(2, "Weld UV0",
                "Merge false-seam vertices (UV0 verts that share a 3D edge + matching UV "
                + "but were split). Instance-pair guard blocks welds across mirror / N-fold "
                + "shells. Required for clean shell extraction in Repack and Transfer.",
                ref stageRunWeldUv0, hasSettings: true, drawSettings: DrawWeldStageSettings);

            // 3. Symmetry split — uses inverted skipSymmetrySplitStep field
            // so existing diagnostic flag continues to work elsewhere.
            bool runSym = !skipSymmetrySplitStep;
            DrawStageRow(3, "Symmetry Split",
                "Split mirrored / overlapping UV0 shells in the source so each "
                + "physical surface gets its own atlas tile. Auto-tunes the "
                + "separation threshold across a few values and picks the best.",
                ref runSym, hasSettings: true, drawSettings: DrawSymmetryStageSettings);
            skipSymmetrySplitStep = !runSym;

            // 4. Repack — main settings live here so users see resolution etc.
            // at the same place as the stage toggle.
            DrawStageRow(4, "Repack (xatlas)",
                "Pack source LOD UVs into a clean UV2 atlas using xatlas. "
                + "Auto-resolution from texel density is the default; the "
                + "Mode picker below switches to manual resolution.",
                ref stageRunRepack, hasSettings: true, drawSettings: DrawRepackStageSettings);

            // 5. Transfer.
            bool hasTargets = ctx.LodGroup != null && HasIncludedTransferTargets(ctx.MeshEntries, ctx.SourceLodIndex);
            DrawStageRow(5, "Transfer to LODs",
                hasTargets
                    ? "Project source UV2 onto every included target LOD."
                    : "No target LODs included — Transfer will skip even when enabled.",
                ref stageRunTransfer, hasSettings: false, drawSettings: null, dimmed: !hasTargets);

            DrawPipelineActions(hasTargets);
        }


        void DrawWeldStageSettings()
        {
            stageWeldRunMeshopt = EditorGUILayout.ToggleLeft(
                new GUIContent("Pre-optimize (meshopt dedup)",
                    "Run meshoptimizer's binary-equivalence dedup + GPU cache/"
                    + "overdraw/fetch reorder before the UV weld. This is a GPU "
                    + "optimisation, NOT a UV weld — it only removes byte-identical "
                    + "vertices (always safe). Turn OFF to run the pure UV-aware "
                    + "seam weld in isolation."),
                stageWeldRunMeshopt);
        }


        void DrawSymmetryStageSettings()
        {
            SymmetrySplitMode = (SymmetrySplitShells.ThresholdMode)EditorGUILayout.EnumPopup(
                new GUIContent("Threshold mode",
                    "Strategy for picking the SymSplit separation threshold. "
                    + "Legacy Fixed uses 0.10; Adaptive picks per-shell from area."),
                SymmetrySplitMode);
            SymmetrySplitShells.CurrentThresholdMode = SymmetrySplitMode;
            // Advanced / debug-only toggle — hidden from production UI.
            if (DebugUi.Enabled)
            {
                splitTargetsInSymmetryStep = EditorGUILayout.ToggleLeft(
                    new GUIContent("Apply to target LODs (advanced)",
                        "Run SymSplit on every included LOD instead of only the source. "
                        + "Coordinated across LODs so each surface keeps its identity."),
                    splitTargetsInSymmetryStep);
            }
        }


        void DrawRepackStageSettings()
        {
            // Vertical layout — sidebar is narrow and the previous
            // three-column row truncated labels ("Resolutior", "Pa",
            // "B "). One control per line, default labelWidth handles
            // alignment correctly even under indentLevel.

            // Mode picker — using friendly labels so the enum value
            // "AutoFromTexelDensity" doesn't show up as a run-on.
            int modeIdx = ctx.RepackResolutionMode == ResolutionMode.AutoFromTexelDensity ? 1 : 0;
            int newModeIdx = EditorGUILayout.Popup(
                new GUIContent("Mode",
                    "Manual: pick atlas resolution (px) — the tool reports effective texel density.\n"
                    + "Auto from texel density: pick target tex/m — the tool derives atlas size from "
                    + "total 3D area."),
                modeIdx,
                new[] { "Manual", "Auto from texel density" });
            ctx.RepackResolutionMode = newModeIdx == 1
                ? ResolutionMode.AutoFromTexelDensity
                : ResolutionMode.Manual;

            // Show only the active driver — opposite mode's field
            // would confuse the user (Manual Resolution staying on
            // screen while Auto-mode preview says "atlas 64 px" was
            // exactly the contradiction we just had).
            if (ctx.RepackResolutionMode == ResolutionMode.Manual)
            {
                ctx.AtlasResolution = EditorGUILayout.IntField(
                    new GUIContent("Resolution (px)",
                        "Atlas resolution in pixels. Power-of-two values recommended (64..4096)."),
                    ctx.AtlasResolution);
            }
            else
            {
                ctx.LightmapDensity = EditorGUILayout.Slider(
                    new GUIContent("Texels per meter",
                        "Target lightmap density. Atlas size = ceil_pow2(sqrt(area × density² / coverage))."),
                    ctx.LightmapDensity, 0.5f, 100f);
            }

            ctx.ShellPaddingPx = EditorGUILayout.IntSlider(
                new GUIContent("Shell padding (px)",
                    "Inter-shell padding in atlas pixels. Prevents bleed between neighbours."),
                ctx.ShellPaddingPx, 0, 16);
            ctx.BorderPaddingPx = EditorGUILayout.IntSlider(
                new GUIContent("Border padding (px)",
                    "Atlas-edge padding in pixels."),
                ctx.BorderPaddingPx, 0, 16);

            ctx.RepackPerMesh = EditorGUILayout.ToggleLeft(
                new GUIContent("Per-mesh repack (each group → [0,1])",
                    "Pack each mesh group into its own [0,1] atlas instead of sharing one."),
                ctx.RepackPerMesh);

            // Texel density preview — live summary of the resolved
            // atlas size so the user sees what xatlas will actually
            // pack into without having to switch to the Repack tab.
            double total3DArea = GetSourceAreaPreview();
            string previewLine;
            if (ctx.RepackResolutionMode == ResolutionMode.AutoFromTexelDensity)
            {
                uint autoRes = MeshAreaHelper.ComputeAutoResolution(
                    total3DArea, ctx.LightmapDensity, ctx.TargetUvCoverage);
                previewLine =
                    $"area {total3DArea:F2} m²  ·  density {ctx.LightmapDensity:F1} tex/m  " +
                    $"→  atlas {autoRes} px";
            }
            else
            {
                int resForDisplay = Mathf.Max(1, ctx.AtlasResolution);
                double effDensity = total3DArea > 0.0
                    ? resForDisplay / System.Math.Sqrt(total3DArea / Mathf.Max(0.0001f, ctx.TargetUvCoverage))
                    : 0.0;
                previewLine =
                    $"area {total3DArea:F2} m²  ·  atlas {resForDisplay} px  " +
                    $"→  effective ≈ {effDensity:F1} tex/m";
            }
            EditorGUILayout.LabelField(previewLine, EditorStyles.miniLabel);
        }

        void DrawPipelineActions(bool hasTargets)
        {
            // Primary action — wrapped in EditorGUI.DisabledScope on the
            // in-flight gate so the button visibly greys out while a run is
            // active. FireAndForget catches Task faults so an exception
            // mid-pipeline can't leave the strip stuck on a stale phase.
            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(_pipelineInFlight))
            {
                ColorBtn(new Color(.2f, .75f, .95f), "▶ Run Full Pipeline", 30,
                    () => FireAndForget(ExecFullPipelineAsync, "Run Full Pipeline"));

                // Step shortcuts for iterative work — bypasses the full
                // pipeline and runs only the named stage so the user can poke
                // at Repack (resolution / padding tweaks) or Transfer (LOD
                // inclusion tweaks) in a tight loop without re-running
                // Analyze / Weld / SymSplit every time.
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(ctx.LodGroup == null))
                    {
                        if (GUILayout.Button(new GUIContent("Run Repack only",
                                "Skip Analyze / Weld / SymSplit and run only Repack on the source LOD."),
                                GUILayout.Height(22)))
                        {
                            var src = ctx.ForLod(ctx.SourceLodIndex);
                            FireAndForget(
                                () => ctx.RepackPerMesh ? ExecRepackPerMeshAsync(src) : ExecRepackAsync(src),
                                "Run Repack only");
                        }
                    }
                    using (new EditorGUI.DisabledScope(!ctx.HasRepack || !hasTargets))
                    {
                        if (GUILayout.Button(new GUIContent("Run Transfer only",
                                "Re-run Transfer against the existing source UV2 (requires a prior Repack)."),
                                GUILayout.Height(22)))
                        {
                            FireAndForget(ExecTransferAllAsync, "Run Transfer only");
                        }
                    }
                }
            }
        }

        // Single pipeline stage row: ordinal badge + coloured state stripe +
        // toggle label + optional nested settings (drawn when enabled).
        void DrawStageRow(int ordinal, string title, string tooltip,
                          ref bool enabled, bool hasSettings, Action drawSettings,
                          bool dimmed = false)
        {
            // Reserve the row rect so we can paint a left stripe before the
            // controls. Default control height is the IMGUI single-line height.
            float lineH = EditorGUIUtility.singleLineHeight + 2f;
            var rowRect = GUILayoutUtility.GetRect(0, lineH, GUILayout.ExpandWidth(true));

            // Left stripe: green when enabled, grey when disabled.
            var stripeRect = new Rect(rowRect.x, rowRect.y + 1, 3f, rowRect.height - 2);
            Color stripeColor = new Color(0.40f, 0.40f, 0.40f);
            if (enabled) stripeColor = dimmed ? new Color(0.45f, 0.55f, 0.45f) : new Color(0.35f, 0.78f, 0.45f);
            EditorGUI.DrawRect(stripeRect, stripeColor);

            // Ordinal badge — small numbered chip on the left.
            var ordRect = new Rect(rowRect.x + 8f, rowRect.y, 18f, rowRect.height);
            var ordStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = enabled ? new Color(0.85f, 0.85f, 0.85f) : new Color(0.55f, 0.55f, 0.55f) },
            };
            GUI.Label(ordRect, ordinal.ToString(), ordStyle);

            // Status icon (right side) — outcome of the most recent run.
            const float iconW = 18f;
            var status = ordinal >= 0 && ordinal < stageOutcome.Length
                ? stageOutcome[ordinal] : StageStatus.Idle;
            string icon = null;
            Color iconColor = default;
            switch (status)
            {
                case StageStatus.Running:
                    icon = "…"; iconColor = new Color(0.45f, 0.75f, 1f); break;
                case StageStatus.Success:
                    icon = "✓"; iconColor = new Color(0.45f, 0.90f, 0.55f); break;
                case StageStatus.Failed:
                    icon = "✗"; iconColor = new Color(0.95f, 0.45f, 0.45f); break;
                case StageStatus.Skipped:
                    icon = "⏭"; iconColor = new Color(0.65f, 0.65f, 0.65f); break;
            }
            if (icon != null)
            {
                var iconRect = new Rect(rowRect.xMax - iconW - 4f, rowRect.y, iconW, rowRect.height);
                var iconStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = iconColor },
                };
                GUI.Label(iconRect, icon, iconStyle);
            }

            // Toggle + bold label.
            float toggleRightInset = (icon != null ? iconW + 8f : 4f);
            var toggleRect = new Rect(rowRect.x + 26f, rowRect.y,
                                      rowRect.width - 30f - toggleRightInset, rowRect.height);
            var oldColor = GUI.contentColor;
            if (dimmed) GUI.contentColor = new Color(1f, 1f, 1f, 0.6f);
            enabled = EditorGUI.ToggleLeft(toggleRect, new GUIContent(title, tooltip), enabled, EditorStyles.boldLabel);
            GUI.contentColor = oldColor;

            // Nested per-stage settings.
            if (hasSettings && enabled && drawSettings != null)
            {
                EditorGUI.indentLevel++;
                drawSettings();
                EditorGUI.indentLevel--;
                EditorGUILayout.Space(2);
            }
        }

        // ──────────────── Repack ────────────────
        //
        // Standalone Repack tab — surfaces every xatlas setting for the user
        // who wants to drive Repack on its own (e.g. iterating on resolution
        // / brute force / oversample without running Analyze + Weld + Transfer).
        // Settings are grouped into four collapsible sections so the panel
        // is navigable instead of one 25-row flat list:
        //   • Resolution      — atlas size, padding, density target
        //   • Pack Quality    — packer choice, rotation, oversample, max chart
        //   • Density         — per-shell normalisation, ARAP, coverage, clamp
        //   • Compression     — bilinear safety, block alignment
        //   • Advanced (debug)— manual texels-per-UV-unit, post-pack correction
        //                       (gated by Project Settings ▸ Mesh Lab ▸ Show
        //                       Debug UI — these are tuning knobs for the
        //                       package author, not production users).

        bool foldRepackResolution = true;
        bool foldRepackQuality    = true;
        bool foldRepackDensity    = true;
        bool foldRepackCompression;
        bool foldRepackAdvanced;

        void DrawRepack()
        {
            H("xatlas Repack");
            if (ctx.LodGroup == null) { Warn("Set LODGroup first."); return; }

            // ── Resolution & padding ───────────────────────────────────
            foldRepackResolution = EditorGUILayout.Foldout(foldRepackResolution, "Resolution", true);
            if (foldRepackResolution)
            {
                EditorGUI.indentLevel++;
                DrawRepackResolutionControls();
                EditorGUI.indentLevel--;
            }

            // ── Pack quality ───────────────────────────────────────────
            EditorGUILayout.Space(2);
            foldRepackQuality = EditorGUILayout.Foldout(foldRepackQuality, "Pack Quality", true);
            if (foldRepackQuality)
            {
                EditorGUI.indentLevel++;
                DrawRepackQualityControls();
                EditorGUI.indentLevel--;
            }

            // ── Density ────────────────────────────────────────────────
            EditorGUILayout.Space(2);
            foldRepackDensity = EditorGUILayout.Foldout(foldRepackDensity, "Density", true);
            if (foldRepackDensity)
            {
                EditorGUI.indentLevel++;
                DrawRepackDensityControls();
                EditorGUI.indentLevel--;
            }

            // ── Compression ────────────────────────────────────────────
            EditorGUILayout.Space(2);
            foldRepackCompression = EditorGUILayout.Foldout(foldRepackCompression, "Compression", true);
            if (foldRepackCompression)
            {
                EditorGUI.indentLevel++;
                DrawRepackCompressionControls();
                EditorGUI.indentLevel--;
            }

            // ── Advanced (debug only) ──────────────────────────────────
            if (DebugUi.Enabled)
            {
                EditorGUILayout.Space(2);
                foldRepackAdvanced = EditorGUILayout.Foldout(foldRepackAdvanced, "Advanced (debug)", true);
                if (foldRepackAdvanced)
                {
                    EditorGUI.indentLevel++;
                    DrawRepackAdvancedControls();
                    EditorGUI.indentLevel--;
                }
            }

            // ── Action ─────────────────────────────────────────────────
            EditorGUILayout.Space(6);
            var src = ctx.ForLod(ctx.SourceLodIndex);
            ctx.RepackPerMesh = EditorGUILayout.ToggleLeft(
                new GUIContent("Per-mesh repack",
                    "Pack each mesh group into its own [0,1] atlas instead of sharing one."),
                ctx.RepackPerMesh);
            using (new EditorGUI.DisabledScope(_pipelineInFlight))
            {
                ColorBtn(new Color(.3f,.8f,.4f), "Repack All", 26, () =>
                {
                    FireAndForget(
                        () => ctx.RepackPerMesh ? ExecRepackPerMeshAsync(src) : ExecRepackAsync(src),
                        "Repack All");
                });
            }
            if (ctx.HasRepack)
                EditorGUILayout.HelpBox("Repack done. Preview UV1, then Transfer.", MessageType.Info);
        }

        void DrawRepackResolutionControls()
        {
            // Friendly Popup instead of EnumPopup so "AutoFromTexelDensity"
            // doesn't show up as a run-on label. Order matches the enum.
            int modeIdx = ctx.RepackResolutionMode == ResolutionMode.AutoFromTexelDensity ? 1 : 0;
            int newModeIdx = EditorGUILayout.Popup(
                new GUIContent("Mode",
                    "Manual: pick atlas resolution (power of two), tool shows effective density.\n"
                    + "Auto from texel density: pick target texels/m, tool sizes atlas from total 3D area "
                    + "rounded up to next power of two, clamped to [64, 4096]."),
                modeIdx,
                new[] { "Manual", "Auto from texel density" });
            ctx.RepackResolutionMode = newModeIdx == 1
                ? ResolutionMode.AutoFromTexelDensity
                : ResolutionMode.Manual;

            double total3DArea = GetSourceAreaPreview();

            if (ctx.RepackResolutionMode == ResolutionMode.Manual)
            {
                ctx.AtlasResolution = EditorGUILayout.IntField(
                    new GUIContent("Resolution (px)",
                        "Atlas resolution in pixels. Power-of-two values recommended (64..4096)."),
                    ctx.AtlasResolution);
                int resForDisplay = Mathf.Max(1, ctx.AtlasResolution);
                double effDensity = total3DArea > 0.0
                    ? resForDisplay / System.Math.Sqrt(total3DArea / Mathf.Max(0.0001f, ctx.TargetUvCoverage))
                    : 0.0;
                EditorGUILayout.LabelField(" ",
                    $"area {total3DArea:F2} m² · effective ≈ {effDensity:F1} tex/m",
                    EditorStyles.miniLabel);
            }
            else
            {
                ctx.LightmapDensity = EditorGUILayout.Slider(
                    new GUIContent("Texels per meter",
                        "Target density. Atlas size = ceil_pow2(sqrt(area × density² / coverage)). "
                        + "Typical: 5–20 for props, 1–5 for large environment pieces."),
                    ctx.LightmapDensity, 0.5f, 100f);
                uint autoRes = MeshAreaHelper.ComputeAutoResolution(
                    total3DArea, ctx.LightmapDensity, ctx.TargetUvCoverage);
                EditorGUILayout.LabelField(" ",
                    $"area {total3DArea:F2} m² · computed {autoRes} px",
                    EditorStyles.miniLabel);
            }

            ctx.ShellPaddingPx = EditorGUILayout.IntSlider(
                new GUIContent("Shell Padding (px)",
                    "Inter-shell padding in atlas pixels. Prevents bleed between neighbours."),
                ctx.ShellPaddingPx, 0, 16);
            ctx.BorderPaddingPx = EditorGUILayout.IntSlider(
                new GUIContent("Border Padding (px)",
                    "Atlas-edge padding in pixels."),
                ctx.BorderPaddingPx, 0, 16);
        }

        void DrawRepackQualityControls()
        {
            bool bruteForceAvailable = IsBruteForcePackAvailable(ctx.InternalOversample);
            using (new EditorGUI.DisabledScope(!bruteForceAvailable))
            {
                ctx.XatlasBruteForce = EditorGUILayout.ToggleLeft(
                    new GUIContent("Brute force pack",
                        "Run xatlas's exhaustive packer (slower, tighter atlas). Only active when "
                        + "Internal pack oversample is 1×; 2×+ forces the heuristic packer."),
                    ctx.XatlasBruteForce);
            }
            if (!bruteForceAvailable)
                EditorGUILayout.LabelField(" ", "Heuristic packer forced by oversample > 1", EditorStyles.miniLabel);

            ctx.XatlasRotateCharts = EditorGUILayout.ToggleLeft(
                new GUIContent("Rotate charts",
                    "xatlas may rotate charts to fit better (recommended)."),
                ctx.XatlasRotateCharts);
            using (new EditorGUI.DisabledScope(!ctx.XatlasRotateCharts))
            {
                EditorGUI.indentLevel++;
                ctx.XatlasRotateChartsToAxis = EditorGUILayout.ToggleLeft(
                    new GUIContent("Snap rotation to axis",
                        "Constrain rotation to 0/90/180/270° (preserves texel alignment)."),
                    ctx.XatlasRotateChartsToAxis);
                EditorGUI.indentLevel--;
            }

            int[] osValues = { 1, 2, 4, 8, 16 };
            string[] osLabels = { "1× (off)", "2×", "4×", "8×", "16×" };
            int currentOs = Mathf.Max(1, ctx.InternalOversample);
            int osIdx = 0;
            for (int i = 0; i < osValues.Length; i++)
                if (osValues[i] == currentOs) { osIdx = i; break; }
            int newOsIdx = EditorGUILayout.Popup(
                new GUIContent("Internal oversample",
                    "Internal atlas = user resolution × this factor. Mitigates xatlas's per-chart "
                    + "ceil(extents) stretch that breaks uniform density for sub-pixel shells. "
                    + "4× cuts density spread from ~14× down to ~2×. 2×+ forces heuristic packer."),
                osIdx, osLabels);
            ctx.InternalOversample = osValues[Mathf.Clamp(newOsIdx, 0, osValues.Length - 1)];

            ctx.XatlasMaxChartSize = EditorGUILayout.IntField(
                new GUIContent("Max chart size (px)",
                    "Hard cap on individual chart dimension. 0 = unbounded — one huge chart could "
                    + "force atlas growth past requested resolution. Cap to atlas resolution or smaller."),
                ctx.XatlasMaxChartSize);
            if (ctx.XatlasMaxChartSize < 0) ctx.XatlasMaxChartSize = 0;
        }

        void DrawRepackDensityControls()
        {
            ctx.NormalizeTexelDensity = EditorGUILayout.ToggleLeft(
                new GUIContent("Normalize texel density",
                    "Per-shell UV0 rescale so UV-area is proportional to 3D surface area. "
                    + "Produces uniform texels-per-world-unit in the baked lightmap."),
                ctx.NormalizeTexelDensity);
            using (new EditorGUI.DisabledScope(!ctx.NormalizeTexelDensity))
            {
                EditorGUI.indentLevel++;
                ctx.ReparameterizeStretchedShells = EditorGUILayout.ToggleLeft(
                    new GUIContent("Auto-fix stretched shells (ARAP)",
                        "Measure Sander L² stretch per shell; re-parameterize via ARAP local-global "
                        + "for shells above the threshold."),
                    ctx.ReparameterizeStretchedShells);
                using (new EditorGUI.DisabledScope(!ctx.ReparameterizeStretchedShells))
                {
                    EditorGUI.indentLevel++;
                    ctx.StretchThreshold = EditorGUILayout.Slider(
                        new GUIContent("L² stretch threshold",
                            "1.0 = isometric, 1.5 = typical unwrap (default), 2.0 = noticeable, 3.0+ = severe."),
                        ctx.StretchThreshold, 1.0f, 3.0f);
                    ctx.ArapIterations = EditorGUILayout.IntSlider(
                        new GUIContent("ARAP iterations",
                            "Local-global iteration count. 50 default; 100–200 for highly twisted strips."),
                        ctx.ArapIterations, 10, 200);
                    EditorGUI.indentLevel--;
                }
                ctx.TargetUvCoverage = EditorGUILayout.Slider(
                    new GUIContent("UV coverage budget",
                        "Fraction of [0,1]² normalized UVs sum to. Lower → safer fit, smaller charts; "
                        + "higher → tighter pack but risk of overflow + downscale."),
                    ctx.TargetUvCoverage, 0.3f, 0.95f);
                EditorGUI.indentLevel--;
            }
            ctx.ClampLightmapToUnit = EditorGUILayout.ToggleLeft(
                new GUIContent("Clamp UV2 to [0,1]",
                    "Cheap safety net against verts pushed a fraction of a texel outside the unit square."),
                ctx.ClampLightmapToUnit);
        }

        void DrawRepackCompressionControls()
        {
            ctx.XatlasBilinear = EditorGUILayout.ToggleLeft(
                new GUIContent("Bilinear-safe padding",
                    "Pad each chart by 1 extra texel so bilinear sampling doesn't leak neighbours. "
                    + "Default ON for lightmap use."),
                ctx.XatlasBilinear);
            ctx.XatlasBlockAlign = EditorGUILayout.ToggleLeft(
                new GUIContent("Block-align (BC/DXT)",
                    "Snap chart placement to 4×4 texel blocks. Required for BC1/DXT compressed "
                    + "lightmaps to avoid color bleed across block boundaries. Costs ~3-8% packing."),
                ctx.XatlasBlockAlign);
            using (new EditorGUI.DisabledScope(!ctx.XatlasBlockAlign))
            {
                int[] blockSizes = { 4, 5, 6, 8, 10, 12 };
                string[] blockLabels = { "4×4 (BC/ETC/DXT)", "5×5 (ASTC)", "6×6 (ASTC)", "8×8 (ASTC)", "10×10 (ASTC)", "12×12 (ASTC)" };
                int currentIdx = System.Array.IndexOf(blockSizes, ctx.XatlasBlockSize);
                if (currentIdx < 0) currentIdx = 0;
                EditorGUI.indentLevel++;
                int newIdx = EditorGUILayout.Popup(
                    new GUIContent("Block size",
                        "Compression block size. 4×4 covers BC1/BC3/BC5/BC7/ETC2/DXT*."),
                    currentIdx, blockLabels);
                ctx.XatlasBlockSize = blockSizes[newIdx];
                EditorGUI.indentLevel--;
            }
        }

        void DrawRepackAdvancedControls()
        {
            ctx.PostPackDensityCorrection = EditorGUILayout.ToggleLeft(
                new GUIContent("Post-pack density correction (experimental)",
                    "After pack, shrink over-dense shells toward the median around each packed chart's UV2 centroid. "
                    + "Each chart stays inside its packed bounds. Shrink-only; leaves gaps."),
                ctx.PostPackDensityCorrection);
            ctx.XatlasTexelsPerUnit = EditorGUILayout.FloatField(
                new GUIContent("Texels per UV unit (manual)",
                    "Override xatlas's auto-derived texel density. 0 = auto-derive from atlas resolution. "
                    + "Manual value pins a fixed texels-per-UV-unit for cross-lightmap density parity."),
                ctx.XatlasTexelsPerUnit);
            if (ctx.XatlasTexelsPerUnit < 0f) ctx.XatlasTexelsPerUnit = 0f;
            // SymSplit thresholds shared with Setup tab — duplicated here for
            // convenience when iterating on Repack only.
            SymmetrySplitMode = (SymmetrySplitShells.ThresholdMode)EditorGUILayout.EnumPopup(
                new GUIContent("SymSplit thresholds",
                    "Shared with Setup tab. Strategy for picking the SymSplit separation threshold."),
                SymmetrySplitMode);
            SymmetrySplitShells.CurrentThresholdMode = SymmetrySplitMode;
        }

        // ──────────────── Transfer ────────────────

        void DrawTransfer()
        {
            H("UV Transfer (Source → Targets)");
            if (ctx.LodGroup == null) { Warn("Set LODGroup first."); return; }
            if (!ctx.HasRepack) { Warn("Run Repack first."); return; }

            // Per-LOD summary card. ✓ when every included entry in the LOD
            // has a transferredMesh, dimmed dot otherwise. Header carries
            // mesh count and aggregate vertex coverage so the user sees the
            // shape of the result without expanding each row.
            for (int li = 0; li < ctx.LodCount; li++)
            {
                if (li == ctx.SourceLodIndex) continue;
                var ee = ctx.ForLod(li);
                if (ee.Count == 0) continue;
                DrawTransferLodCard(li, ee);
            }

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(_pipelineInFlight))
            {
                ColorBtn(new Color(.3f,.6f,1f), "Transfer All Targets", 26,
                    () => FireAndForget(ExecTransferAllAsync, "Transfer All Targets"));
            }

            if (ctx.HasTransfer) DrawTransferQualityReport();
            if (CanApplyUv2(ctx.HasRepack, ctx.HasTransfer)) DrawApplyUv2Actions();
        }

        void DrawTransferLodCard(int li, List<MeshEntry> ee)
        {
            bool allDone = ee.All(e => e.transferredMesh != null);
            bool noneDone = ee.All(e => e.transferredMesh == null);

            var totals = GetTransferTotals(ee);
            float coverage = totals.vertices > 0 ? totals.transferred * 100f / totals.vertices : 0f;
            string headerIcon = "◐";
            if (allDone) headerIcon = "✓";
            else if (noneDone) headerIcon = "•";
            string plural = ee.Count == 1 ? "" : "es";
            string summary = $"   LOD{li}  ·  {ee.Count} mesh{plural}";
            if (totals.vertices > 0) summary += $"  ·  {coverage:F0}% verts";

            // Status colour on the icon glyph; the foldout label itself
            // stays the default colour so it remains readable.
            var oldContent = GUI.contentColor;
            GUI.contentColor = new Color(0.95f, 0.78f, 0.35f);
            if (allDone) GUI.contentColor = new Color(0.45f, 0.90f, 0.55f);
            else if (noneDone) GUI.contentColor = new Color(0.65f, 0.65f, 0.65f);
            if (!transferLodFoldouts.ContainsKey(li)) transferLodFoldouts[li] = false;
            transferLodFoldouts[li] = EditorGUILayout.Foldout(transferLodFoldouts[li], headerIcon + summary, true);
            GUI.contentColor = oldContent;
            if (!transferLodFoldouts[li]) return;

            EditorGUI.indentLevel++;
            foreach (var e in ee)
            {
                DrawTransferMeshRow(e, oldContent);
            }
            EditorGUI.indentLevel--;
        }

        static void DrawTransferMeshRow(MeshEntry e, Color oldContent)
        {
            string extra = "";
            if (e.shellTransferResult != null)
            {
                var r = e.shellTransferResult;
                float p = r.verticesTotal > 0 ? r.verticesTransferred * 100f / r.verticesTotal : 0f;
                extra = $"  ·  {r.shellsMatched} sh  ·  {p:F0}%";
            }
            string rowIcon = e.transferredMesh != null ? "✓" : "•";
            GUI.contentColor = e.transferredMesh != null
                ? new Color(0.45f, 0.90f, 0.55f)
                : new Color(0.65f, 0.65f, 0.65f);
            EditorGUILayout.LabelField(rowIcon + "  " + e.renderer.name + extra, EditorStyles.miniLabel);
            GUI.contentColor = oldContent;
        }

        void DrawTransferQualityReport()
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
                foreach (var e in ee) DrawTransferQualityRow(e);
            }
            EditorGUILayout.EndScrollView();

            DrawValidationOverlay();
        }

        void DrawTransferQualityRow(MeshEntry e)
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

        void DrawValidationOverlay()
        {
            EditorGUILayout.Space(4);
            foldValidationOverlay = EditorGUILayout.Foldout(foldValidationOverlay, "Validation Overlay", true);
            if (foldValidationOverlay)
            {
                EditorGUI.indentLevel++;
                var mask = canvas != null ? canvas.ValidationFilterMask : TransferValidator.TriIssue.None;
                bool changed = false;
                changed |= ToggleIssueBit(ref mask, TransferValidator.TriIssue.Inverted,    "Inverted");
                changed |= ToggleIssueBit(ref mask, TransferValidator.TriIssue.Stretched,   "Stretched");
                changed |= ToggleIssueBit(ref mask, TransferValidator.TriIssue.ZeroArea,    "ZeroArea");
                changed |= ToggleIssueBit(ref mask, TransferValidator.TriIssue.OutOfBounds, "OutOfBounds");
                changed |= ToggleIssueBit(ref mask, TransferValidator.TriIssue.Overlap,     "Overlap");
                changed |= ToggleIssueBit(ref mask, TransferValidator.TriIssue.TexelDensity,"TexelDensity");
                if (changed && canvas != null)
                {
                    canvas.ValidationFilterMask = mask;
                    RequestRepaint?.Invoke();
                }
                EditorGUILayout.LabelField(
                    mask == TransferValidator.TriIssue.None ? "(all triangles drawn)" : $"mask: {mask}",
                    EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }
        }

        void DrawApplyUv2Actions()
        {
            EditorGUILayout.Space(6);
            // The source LOD can be applied immediately after repack,
            // even when there are no included target LODs to transfer.
            H("Apply UV2");
            ColorBtn(new Color(.3f,.85f,.4f), "Apply UV2 to FBX", 26, ApplyUv2ToFbx);
            EditorGUILayout.Space(2);
            ColorBtn(new Color(.9f,.3f,.3f), "Reset UV2 (delete sidecar)", 20, ResetUv2FromFbx);
            EditorGUILayout.Space(2);
            ColorBtn(new Color(.5f,.15f,.15f), "Reset Pipeline State", 20, ResetPipelineState);
        }

        static (int vertices, int transferred, int rejected, int overlaps) GetTransferTotals(IEnumerable<MeshEntry> entries)
        {
            int vertices = 0, transferred = 0, rejected = 0, overlaps = 0;
            foreach (var entry in entries)
            {
                var result = entry.shellTransferResult;
                if (result == null) continue;
                vertices += result.verticesTotal;
                transferred += result.verticesTransferred;
                rejected += result.shellsRejected;
                overlaps += result.shellsOverlapFixed;
            }
            return (vertices, transferred, rejected, overlaps);
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
            RequestRepaint?.Invoke();
        }

        /// <summary>meshopt binary-equivalence dedup + GPU cache/overdraw/
        /// fetch reorder. NOT a UV weld — only removes vertices that are
        /// byte-identical in position + normal + uv0 (per meshoptimizer's
        /// generateVertexRemap; the library docs explicitly warn it is
        /// unsuitable for attribute-seam handling). Exact duplicates are
        /// always safe to merge, so no chart-awareness is needed here.
        /// The semantic UV-aware seam weld is <see cref="ExecWeldUv0"/>.
        /// </summary>
        void ExecMeshOptimize()
        {
            if (ctx.LodGroup == null) return;
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.originalMesh == null) continue;
                if (e.originalMesh == e.fbxMesh)
                {
                    e.originalMesh = MeshAccess.ReadableCopy(e.fbxMesh);
                    e.originalMesh.name = e.fbxMesh.name + "_wc";
                }
                var optResult = MeshOptimizer.Optimize(e.originalMesh);
                if (optResult.ok)
                {
                    e.wasWelded = true;
                    UvtLog.Info($"[MeshOpt] '{e.originalMesh.name}' LOD{e.lodIndex}: "
                        + $"meshopt dedup/reorder ({optResult.originalVertexCount} → "
                        + $"{optResult.optimizedVertexCount} verts)");
                }
            }
            ctx.ClearAllCaches();
            RequestRepaint?.Invoke();
        }

        /// <summary>UV-aware false-seam weld via Uv0Analyzer.UvEdgeWeld.
        /// Merges vertices that share a 3D edge AND matching UV0 on both
        /// endpoints (a real chart-interior false seam), with the
        /// instance-pair guard that blocks welds across mirror / N-fold
        /// instance shells. This is the actual "weld" — distinct from
        /// the meshopt GPU dedup in <see cref="ExecMeshOptimize"/>.
        ///
        /// When <paramref name="runMeshoptFirst"/> is true (pipeline
        /// default) the meshopt dedup runs first so exact duplicates are
        /// gone before the UV weld looks for seams. Set false to run the
        /// pure UV weld in isolation.</summary>
        void ExecWeldUv0(bool runMeshoptFirst = true)
        {
            if (ctx.LodGroup == null) return;

            if (runMeshoptFirst)
                ExecMeshOptimize();

            // UV-aware edge weld for all meshes.
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.originalMesh == null) continue;
                // Materialise a working copy if we skipped meshopt (which
                // is what normally clones the fbx mesh).
                if (e.originalMesh == e.fbxMesh)
                {
                    e.originalMesh = MeshAccess.ReadableCopy(e.fbxMesh);
                    e.originalMesh.name = e.fbxMesh.name + "_wc";
                }
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
            RequestRepaint?.Invoke();
        }

        void ExecSymmetrySplit(bool includeTargets, float separationThreshold = 0.10f)
        {
            if (ctx.LodGroup == null) return;
            SymmetrySplitShells.CurrentThresholdMode = SymmetrySplitMode;
            lastSymmetrySplitLods.Clear();

            // Phase 1: Split source LOD and capture parameters for coordinated LOD splitting
            var splitParamsByGroup = new Dictionary<string, List<SymmetrySplitShells.SplitParams>>();

            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.lodIndex != ctx.SourceLodIndex) continue;
                SplitSourceSymmetry(e, separationThreshold, splitParamsByGroup);
            }
            if (includeTargets)
                foreach (var e in ctx.MeshEntries)
                {
                    if (!e.include || e.lodIndex == ctx.SourceLodIndex) continue;
                    SplitTargetSymmetry(e, separationThreshold, splitParamsByGroup);
                }

            ctx.ClearAllCaches();
            RequestRepaint?.Invoke();
        }

        void SplitSourceSymmetry(MeshEntry e, float separationThreshold,
            Dictionary<string, List<SymmetrySplitShells.SplitParams>> splitParamsByGroup)
        {
            EnsureTransferWorkingMesh(e);
            var uv0 = e.originalMesh.uv;
            if (uv0 == null || uv0.Length == 0) return;
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

        static void EnsureTransferWorkingMesh(MeshEntry entry)
        {
            if (entry.originalMesh != entry.fbxMesh) return;
            entry.originalMesh = MeshAccess.ReadableCopy(entry.fbxMesh);
            entry.originalMesh.name = entry.fbxMesh.name + "_wc";
        }

        void SplitTargetSymmetry(MeshEntry e, float separationThreshold,
            Dictionary<string, List<SymmetrySplitShells.SplitParams>> splitParamsByGroup)
        {
            EnsureTransferWorkingMesh(e);
            var uv0 = e.originalMesh.uv;
            if (uv0 == null || uv0.Length == 0) return;
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

        // Sync entry — used by sweep loops where each cell runs end-to-end
        // before the loop moves on. Editor blocks for the cell duration.
        void ExecFullPipeline(string runLabel) => ExecFullPipelineImpl(runLabel, useAsync: false).GetAwaiter().GetResult();

        // Async entry — button-click path; editor main thread stays responsive.
        Task ExecFullPipelineAsync() => ExecFullPipelineImpl("FullPipeline", useAsync: true);

        async Task ExecFullPipelineImpl(string runLabel, bool useAsync)
        {
            if (ctx.LodGroup == null) return;
            using var _bench = BenchmarkRecorder.NewRun(ctx, runLabel,
                splitTargetsInSymmetryStep, SymmetrySplitMode);
            BenchmarkRecorder.Current?.StageBegin("pipeline");
            bool completedSuccessfully = false;
            try
            {
                completedSuccessfully = await ExecFullPipelineCoreImpl(useAsync);
            }
            finally
            {
                BenchmarkRecorder.Current?.StageEnd("pipeline");
                // When the pipeline aborts early (user-cancel or exception)
                // the per-mesh shellTransferResult / validation state is stale
                // from a previous run — recording it would emit misleading
                // metrics that taint sweep winners. Skip RecordMesh entirely
                // in that case; the sweep aggregator already treats cells
                // with no CSV row as failed.
                if (completedSuccessfully && BenchmarkRecorder.Current != null)
                    foreach (var e in ctx.MeshEntries)
                    {
                        // Skip excluded entries: a user-deselected mesh has
                        // stale TransferResult/ValidationReport from a prior
                        // run and would surface as a failed row in sweep
                        // aggregates even though the pipeline never touched it.
                        if (!e.include) continue;
                        BenchmarkRecorder.Current.RecordMesh(e);
                    }
            }
        }

        /// <summary>
        /// Rewind every entry's working mesh to a pristine state before a
        /// full-pipeline run so the run is idempotent. Working copies are
        /// materialised lazily by the individual stages (each does
        /// <c>if (e.originalMesh == e.fbxMesh) … MakeReadableCopy</c>), so it
        /// is enough to point <see cref="MeshEntry.originalMesh"/> back at
        /// <see cref="MeshEntry.fbxMesh"/>, destroy the stale clone, drop the
        /// derived (repacked / transferred) meshes, and clear the per-step
        /// flags. Mirrors the per-step reset done between auto-tune configs,
        /// but covers the whole entry set including non-included entries so
        /// nothing from a prior run leaks into this one.
        /// </summary>
        void ResetWorkingMeshesToFbx()
        {
            if (ctx?.MeshEntries == null) return;
            foreach (var e in ctx.MeshEntries)
            {
                if (e == null) continue;

                // Drop derived meshes produced by an earlier run.
                if (e.transferredMesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(e.transferredMesh);
                    e.transferredMesh = null;
                }
                if (e.repackedMesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(e.repackedMesh);
                    e.repackedMesh = null;
                }
                e.repackedAtlasWidth = 0;
                e.repackedAtlasHeight = 0;
                e.transferState = null;
                e.shellTransferResult = null;
                e.validationReport = null;

                // Rewind the working mesh to the imported fbx asset. The stale
                // working clone (weld / sym-split product) is destroyed — it is
                // ours, never the asset (fbxMesh is owned by the AssetDatabase).
                if (e.fbxMesh != null)
                {
                    if (e.originalMesh != null && e.originalMesh != e.fbxMesh)
                        UnityEngine.Object.DestroyImmediate(e.originalMesh);
                    e.originalMesh = e.fbxMesh;
                }

                e.wasWelded = false;
                e.wasEdgeWelded = false;
                e.wasSymmetrySplit = false;
            }

            ctx.ClearAllCaches();
            crossLodHints.Clear();
            shellTransformCache.Clear();
            ctx.HasRepack = false;
            ctx.HasTransfer = false;
            uv0Welded = false;
        }

        /// <summary>
        /// Run the auto-tune full pipeline. Returns <c>true</c> when the
        /// pipeline ran end-to-end and the in-memory per-mesh state reflects
        /// the just-completed run; returns <c>false</c> when the user
        /// cancelled mid-flight so the caller can skip artefact recording
        /// (stale state from a prior run would otherwise be written).
        /// </summary>
        async Task<bool> ExecFullPipelineCoreImpl(bool useAsync)
        {
            string version = UnityEditor.PackageManager.PackageInfo
                .FindForAssembly(typeof(UvTransferWorkflow).Assembly)?.version ?? "0.0.0";
            UvtLog.Info($"[Pipeline] Starting full pipeline... (v{version})");

            // Reset per-stage outcome state — fresh run, fresh icons.
            for (int i = 0; i < stageOutcome.Length; i++) stageOutcome[i] = StageStatus.Idle;

            // Idempotency: rewind every working mesh to the pristine fbx asset
            // before stage 1. Without this each re-run welds/sym-splits on top
            // of the PREVIOUS run's already-mutated working mesh — meshopt
            // re-dedups, weld re-merges, and sym-split re-cuts an already-cut
            // shell, so identical settings produce a different (degrading)
            // result every time. Resetting here makes a full-pipeline run a
            // pure function of (fbxMesh, settings).
            ResetWorkingMeshesToFbx();

            RunInitialPipelineStages();

            // ── Auto-tune: try multiple SymSplit configs, pick best ──
            // Save working copies so we can restore between attempts.
            var savedMeshes = new Dictionary<MeshEntry, Mesh>();
            foreach (var e in ctx.MeshEntries)
                if (e.originalMesh != null)
                    savedMeshes[e] = UnityEngine.Object.Instantiate(e.originalMesh);

            float[] separationConfigs = { 0.10f, 0.05f, 0.20f };
            bool hasTransferTargets = HasIncludedTransferTargets(ctx.MeshEntries, ctx.SourceLodIndex);
            if (!hasTransferTargets)
                UvtLog.Warn("[Pipeline] No included target LOD meshes; running source repack only and skipping transfer/auto-tune.");

            var best = new AutoTuneChoice();

            bool cancelled = false;
            UvProgress.Begin("Auto-tune Pipeline", cancelable: true);
            try
            {
                cancelled = await RunAutoTuneAttempts(separationConfigs, savedMeshes, best, hasTransferTargets, useAsync);
            }
            finally
            {
                if (cancelled) UvProgress.Cancel(); else UvProgress.End();
            }

            if (best.Meshes.Count > 0 && !cancelled) RestoreAutoTuneChoice(best);

            // Cleanup saved copies
            foreach (var m in savedMeshes.Values)
                UnityEngine.Object.DestroyImmediate(m);

            if (cancelled)
            {
                RequestRepaint?.Invoke();
                return false;
            }
            if (separationConfigs.Length > 1 && best.ConfigIndex > 0)
                UvtLog.Info($"[Pipeline] Auto-tune: selected config #{best.ConfigIndex} " +
                    $"(sep={separationConfigs[best.ConfigIndex]:P0})");

            UvtLog.Info("[Pipeline] Complete.");
            RequestRepaint?.Invoke();
            return true;
        }

        void RunInitialPipelineStages()
        {
            // 1. Analyze (skipped via Setup stage toggle)
            if (stageRunAnalyzeUv0)
            {
                stageOutcome[1] = StageStatus.Running;
                try { ExecAnalyzeUv0(); stageOutcome[1] = StageStatus.Success; }
                catch { stageOutcome[1] = StageStatus.Failed; throw; }
            }
            else
            {
                stageOutcome[1] = StageStatus.Skipped;
                UvtLog.Info("[Pipeline] Analyze UV0 stage SKIPPED by user toggle");
            }

            // 2. Weld (skipped via Setup stage toggle)
            if (stageRunWeldUv0)
            {
                stageOutcome[2] = StageStatus.Running;
                try { ExecWeldUv0(runMeshoptFirst: stageWeldRunMeshopt); stageOutcome[2] = StageStatus.Success; }
                catch { stageOutcome[2] = StageStatus.Failed; throw; }
            }
            else
            {
                stageOutcome[2] = StageStatus.Skipped;
                UvtLog.Info("[Pipeline] Weld UV0 stage SKIPPED by user toggle");
            }

        }

        void RunSymmetryStage(float sepThresh)
        {
            // 3. SymSplit (skipped via diagnostic toggle to isolate xatlas packing)
            if (!skipSymmetrySplitStep)
            {
                stageOutcome[3] = StageStatus.Running;
                try { ExecSymmetrySplit(splitTargetsInSymmetryStep, sepThresh); stageOutcome[3] = StageStatus.Success; }
                catch { stageOutcome[3] = StageStatus.Failed; throw; }
            }
            else
            {
                stageOutcome[3] = StageStatus.Skipped;
                UvtLog.Info(UvtLog.Category.SymSplit, "[Pipeline] SymSplit step SKIPPED by user toggle");
            }

        }

        async Task RunRepackStage(bool useAsync)
        {
            // 4. Repack (skipped via Setup stage toggle)
            if (stageRunRepack)
            {
                stageOutcome[4] = StageStatus.Running;
                try
                {
                    var src = ctx.ForLod(ctx.SourceLodIndex);
                    if (ctx.RepackPerMesh) await ExecRepackPerMeshImpl(src, useAsync);
                    else                   await ExecRepackImpl(src, useAsync);
                    stageOutcome[4] = ctx.HasRepack ? StageStatus.Success : StageStatus.Failed;
                }
                catch { stageOutcome[4] = StageStatus.Failed; throw; }
            }
            else
            {
                stageOutcome[4] = StageStatus.Skipped;
                UvtLog.Info("[Pipeline] Repack stage SKIPPED by user toggle");
            }

        }

        async Task RunTransferStage(bool useAsync, bool hasTransferTargets)
        {
            // 5. Transfer (skipped via Setup stage toggle)
            if (stageRunTransfer && ctx.HasRepack && hasTransferTargets)
            {
                stageOutcome[5] = StageStatus.Running;
                try
                {
                    await ExecTransferAllImpl(useAsync);
                    stageOutcome[5] = ctx.HasTransfer ? StageStatus.Success : StageStatus.Failed;
                }
                catch { stageOutcome[5] = StageStatus.Failed; throw; }
            }
            else if (ctx.HasRepack)
            {
                ctx.HasTransfer = false;
                stageOutcome[5] = StageStatus.Skipped;
                if (!stageRunTransfer)
                    UvtLog.Info("[Pipeline] Transfer stage SKIPPED by user toggle");
            }
            else
            {
                stageOutcome[5] = StageStatus.Skipped;
            }

        }

        void RestoreAutoTuneStartingMeshes(Dictionary<MeshEntry, Mesh> savedMeshes)
        {
            // Restore saved meshes
            foreach (var kv in savedMeshes)
            {
                kv.Key.originalMesh = UnityEngine.Object.Instantiate(kv.Value);
                kv.Key.originalMesh.name = kv.Value.name;
                kv.Key.wasSymmetrySplit = false;
                kv.Key.repackedMesh = null;
                kv.Key.repackedAtlasWidth = 0;
                kv.Key.repackedAtlasHeight = 0;
                kv.Key.transferredMesh = null;
                kv.Key.shellTransferResult = null;
            }
            ctx.ClearAllCaches();
            crossLodHints.Clear();
            shellTransformCache.Clear();
            ctx.HasRepack = false;
            ctx.HasTransfer = false;
        }

        sealed class AutoTuneChoice
        {
            internal int Issues = int.MaxValue;
            internal float Coverage;
            internal int ConfigIndex;
            internal readonly Dictionary<MeshEntry, Mesh> Meshes = new Dictionary<MeshEntry, Mesh>();
            internal readonly Dictionary<MeshEntry, (Mesh transferred, GroupedShellTransfer.TransferResult tr)> Transfers = new Dictionary<MeshEntry, (Mesh, GroupedShellTransfer.TransferResult)>();
        }

        async Task<bool> RunAutoTuneAttempts(float[] separationConfigs, Dictionary<MeshEntry, Mesh> savedMeshes,
            AutoTuneChoice best, bool hasTransferTargets, bool useAsync)
        {
            for (int ci = 0; ci < separationConfigs.Length; ci++)
            {
                float sepThresh = separationConfigs[ci];

                UvProgress.Report(
                    (float)ci / separationConfigs.Length,
                    $"Config {ci + 1}/{separationConfigs.Length} (separation={sepThresh:P0})");
                if (UvProgress.CancelRequested)
                {
                    UvtLog.Warn("[Pipeline] Auto-tune cancelled by user.");
                    return true;
                }

                if (ci > 0)
                {
                    UvtLog.Info($"[Pipeline] Auto-tune retry #{ci} (separation={sepThresh:P0})...");
                    RestoreAutoTuneStartingMeshes(savedMeshes);
                }

                RunSymmetryStage(sepThresh);
                await RunRepackStage(useAsync);
                await RunTransferStage(useAsync, hasTransferTargets);

                if (!hasTransferTargets)
                    break;

                if (EvaluateAutoTuneChoice(best, ci, sepThresh)) break;
            }
            return false;
        }

        bool EvaluateAutoTuneChoice(AutoTuneChoice best, int ci, float sepThresh)
        {
            // Evaluate quality
            var totals = GetTransferTotals(ctx.MeshEntries);
            int totalRejected = totals.rejected;
            int totalOverlaps = totals.overlaps;
            float coverage = totals.vertices > 0 ? (float)totals.transferred / totals.vertices : 0f;
            int totalIssues = totalRejected + totalOverlaps;

            UvtLog.Info($"[Pipeline] Config #{ci} (sep={sepThresh:P0}): " +
                $"rejected={totalRejected}, overlaps={totalOverlaps}, coverage={coverage:P0}");

            bool better = false;
            if (totalIssues < best.Issues)
                better = true;
            else if (totalIssues == best.Issues && coverage > best.Coverage)
                better = true;

            if (better)
            {
                best.Issues = totalIssues;
                best.Coverage = coverage;
                best.ConfigIndex = ci;
                CaptureAutoTuneChoice(best);
            }


            if (totalIssues != 0 || coverage < 0.99f) return false;
            if (ci > 0) UvtLog.Info($"[Pipeline] Perfect result on config #{ci}, stopping.");
            return true;
        }

        void CaptureAutoTuneChoice(AutoTuneChoice best)
        {
            // Save best meshes
            foreach (var m in best.Meshes.Values) UnityEngine.Object.DestroyImmediate(m);
            best.Meshes.Clear();
            best.Transfers.Clear();
            foreach (var e in ctx.MeshEntries)
            {
                if (e.originalMesh != null)
                    best.Meshes[e] = UnityEngine.Object.Instantiate(e.originalMesh);
                if (e.transferredMesh != null)
                    best.Transfers[e] = (UnityEngine.Object.Instantiate(e.transferredMesh),
                        e.shellTransferResult);
            }
        }

        static void RestoreAutoTuneChoice(AutoTuneChoice best)
        {
            foreach (var kv in best.Meshes)
            {
                kv.Key.originalMesh = kv.Value;
                kv.Key.originalMesh.name = kv.Value.name;
            }
            foreach (var kv in best.Transfers)
            {
                kv.Key.transferredMesh = kv.Value.transferred;
                kv.Key.shellTransferResult = kv.Value.tr;
            }
        }

        // Async entry — button-click path. Editor main thread is free during
        // xatlas pack so the inline progress strip keeps repainting and Unity
        // never shows the "Hold on / Waiting for Unity's code…" busy dialog.
        Task ExecRepackAsync(List<MeshEntry> entries) => ExecRepackImpl(entries, useAsync: true);

        async Task ExecRepackImpl(List<MeshEntry> entries, bool useAsync)
        {
            if (entries.Count == 0) return;
            using var _bench = BenchmarkRecorder.NewRun(ctx, "Repack",
                splitTargetsInSymmetryStep, SymmetrySplitMode);
            bool ownsSession = _bench is BenchmarkRecorder;
            bool ownsProgress = !UvProgress.IsActive;
            if (ownsProgress)
                UvProgress.Begin($"Repack ({entries.Count} mesh{(entries.Count == 1 ? "" : "es")})",
                                 cancelable: true);
            BenchmarkRecorder.Current?.StageBegin("repack");
            try { await ExecRepackCoreImpl(entries, useAsync); }
            finally
            {
                BenchmarkRecorder.Current?.StageEnd("repack");
                if (ownsSession && BenchmarkRecorder.Current != null)
                    foreach (var e in entries)
                        BenchmarkRecorder.Current.RecordMesh(e);
                if (ownsProgress) UvProgress.End();
            }
        }

        // Interactive work yields to the editor. Sweep/benchmark runs use an already
        // completed task so their synchronous host never waits on the editor context.
        static Task<RepackResult[]> RepackMeshes(Mesh[] meshes, RepackOptions options, bool useAsync)
        {
            if (useAsync) return XatlasRepack.RepackMultiAsync(meshes, options);
            return Task.FromResult(XatlasRepack.RepackMulti(meshes, options));
        }

        async Task ExecRepackCoreImpl(List<MeshEntry> entries, bool useAsync)
        {
            uint resolvedResolution = (uint)SanitizeAtlasResolution(ctx.AtlasResolution);
            if (ctx.RepackResolutionMode == ResolutionMode.AutoFromTexelDensity)
            {
                var areaMeshes = entries.Where(e => e.originalMesh != null)
                                        .Select(e => e.originalMesh).ToList();
                double area = CacheAreaPreview(
                    areaMeshes, MeshAreaHelper.ComputeTotal3DAreaMeters(areaMeshes));
                resolvedResolution = MeshAreaHelper.ComputeAutoResolution(
                    area, ctx.LightmapDensity, ctx.TargetUvCoverage);
                UvtLog.Info(
                    $"[Repack] Auto-resolution: area={area:F2} m², density={ctx.LightmapDensity:F2} tex/m, " +
                    $"coverage={ctx.TargetUvCoverage:F2} → {resolvedResolution} px");
            }
            // Stamp the recorder with the resolution xatlas will actually use,
            // not the raw UI value — relevant for AutoFromTexelDensity mode
            // where the resolved value can differ by an octave from the user
            // setting.
            BenchmarkRecorder.Current?.SetResolvedAtlasResolution((int)resolvedResolution);
            int safeShellPadding = SanitizePadding(ctx.ShellPaddingPx);
            int safeBorderPadding = SanitizePadding(ctx.BorderPaddingPx);
            UvtLog.Info($"[Repack] {entries.Count} meshes, res={resolvedResolution}, pad={safeShellPadding}, bdr={safeBorderPadding}");
            var validEntries = new List<MeshEntry>();
            var meshCopies = new List<Mesh>();
            foreach (var e in entries)
            {
                // Drop the previous run's output before producing a new one:
                // otherwise a failed re-run leaves a stale repackedMesh that
                // Apply UV2 would happily write to the FBX (and the successful
                // path used to overwrite the reference, leaking the old mesh).
                if (e.repackedMesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(e.repackedMesh);
                    e.repackedMesh = null;
                }
                e.repackedAtlasWidth = 0;
                e.repackedAtlasHeight = 0;

                if (e.originalMesh == null) continue;
                var uv0 = e.originalMesh.uv;
                if (uv0 == null || uv0.Length == 0) { UvtLog.Warn("[Repack] " + e.renderer.name + ": no UV0"); continue; }
                var cp = UnityEngine.Object.Instantiate(e.originalMesh);
                cp.name = e.originalMesh.name + "_repack";
                validEntries.Add(e);
                meshCopies.Add(cp);
            }
            if (meshCopies.Count == 0)
            {
                ctx.HasRepack = ctx.MeshEntries.Any(e => e.repackedMesh != null);
                return;
            }

            var opts = RepackOptions.Default;
            opts.resolution = resolvedResolution;
            opts.padding = (uint)safeShellPadding;
            opts.borderPadding = (uint)safeBorderPadding;
            opts.bruteForce = ctx.XatlasBruteForce;
            opts.rotateCharts = ctx.XatlasRotateCharts;
            opts.rotateChartsToAxis = ctx.XatlasRotateChartsToAxis;
            opts.normalizeTexelDensity = ctx.NormalizeTexelDensity;
            opts.reparameterizeStretchedShells = ctx.ReparameterizeStretchedShells;
            opts.stretchThreshold = ctx.StretchThreshold;
            opts.arapIterations = ctx.ArapIterations;
            opts.clampLightmapToUnit = ctx.ClampLightmapToUnit;
            opts.targetUvCoverage = ctx.TargetUvCoverage;
            opts.postPackDensityCorrection = ctx.PostPackDensityCorrection;
            opts.internalOversample = ctx.InternalOversample > 0 ? ctx.InternalOversample : 1;
            opts.maxChartSize = ctx.XatlasMaxChartSize;
            opts.bilinear = ctx.XatlasBilinear;
            opts.blockAlign = ctx.XatlasBlockAlign;
            opts.blockSize = ctx.XatlasBlockSize;
            opts.texelsPerUnit = ctx.XatlasTexelsPerUnit;

            var results = await RepackMeshes(meshCopies.ToArray(), opts, useAsync);
            for (int i = 0; i < validEntries.Count; i++)
            {
                if (!results[i].ok)
                {
                    UvtLog.Error("[Repack] " + validEntries[i].renderer.name + ": " + results[i].error);
                    UnityEngine.Object.DestroyImmediate(meshCopies[i]);
                    validEntries[i].repackedAtlasWidth = 0;
                    validEntries[i].repackedAtlasHeight = 0;
                    continue;
                }
                validEntries[i].repackedMesh = meshCopies[i];
                validEntries[i].repackedAtlasWidth = results[i].atlasWidth;
                validEntries[i].repackedAtlasHeight = results[i].atlasHeight;
            }

            // HasRepack gates the Apply UV2 UI, so it must mean "a repacked mesh
            // exists right now", not "a repack was attempted". Setting it
            // unconditionally let a failed or cancelled run leave the button
            // enabled, applying the original UV2 to the FBX. Derive it from the
            // entries instead — per-mesh grouping calls this once per group, so a
            // later failing group must not erase an earlier group's success.
            ctx.HasRepack = ctx.MeshEntries.Any(e => e.repackedMesh != null);
            ctx.ClearAllCaches();
            RequestRepaint?.Invoke();
        }

        Task ExecRepackPerMeshAsync(List<MeshEntry> entries) => ExecRepackPerMeshImpl(entries, useAsync: true);

        async Task ExecRepackPerMeshImpl(List<MeshEntry> entries, bool useAsync)
        {
            var groups = new Dictionary<string, List<MeshEntry>>();
            foreach (var e in entries)
            {
                string key = e.meshGroupKey ?? e.renderer.name;
                if (!groups.ContainsKey(key)) groups[key] = new List<MeshEntry>();
                groups[key].Add(e);
            }
            foreach (var kv in groups)
                await ExecRepackImpl(kv.Value, useAsync);
        }

        Task ExecTransferAllAsync() => ExecTransferAllImpl(useAsync: true);

        async Task ExecTransferAllImpl(bool useAsync)
        {
            using var _bench = BenchmarkRecorder.NewRun(ctx, "TransferAll",
                splitTargetsInSymmetryStep, SymmetrySplitMode);
            bool ownsSession = _bench is BenchmarkRecorder;
            bool ownsProgress = !UvProgress.IsActive;
            int targetLodCount = 0;
            for (int li = 0; li < ctx.LodCount; li++)
                if (li != ctx.SourceLodIndex) targetLodCount++;
            if (ownsProgress)
                UvProgress.Begin($"UV2 Transfer ({targetLodCount} target LOD{(targetLodCount == 1 ? "" : "s")})",
                                 cancelable: true);
            BenchmarkRecorder.Current?.StageBegin("transfer");
            // Mirrors the completedSuccessfully guard in ExecFullPipelineImpl:
            // when the transfer loop is cancelled or aborts early, LODs we
            // didn't actually process keep their stale shellTransferResult /
            // validation data from a prior run. RecordMesh on those entries
            // would emit benchmark rows that misrepresent this run. Set the
            // flag only after the loop reaches its natural end.
            bool completedSuccessfully = false;
            try
            {
                if (!HasIncludedTransferTargets(ctx.MeshEntries, ctx.SourceLodIndex))
                {
                    ctx.HasTransfer = false;
                    UvtLog.Warn("[Transfer] No included target LOD meshes; transfer skipped.");
                    RequestRepaint?.Invoke();
                    return;
                }

                crossLodHints.Clear();
                await TransferTargetLods(useAsync, targetLodCount);
                ctx.HasTransfer = !UvProgress.CancelRequested;
                completedSuccessfully = !UvProgress.CancelRequested;
                RequestRepaint?.Invoke();
            }
            finally
            {
                BenchmarkRecorder.Current?.StageEnd("transfer");
                if (completedSuccessfully && ownsSession) RecordIncludedTransferMeshes();
                if (ownsProgress)
                {
                    if (UvProgress.CancelRequested) UvProgress.Cancel();
                    else UvProgress.End();
                }
            }
        }

        async Task TransferTargetLods(bool useAsync, int targetLodCount)
        {
            int processed = 0;
            for (int li = 0; li < ctx.LodCount; li++)
            {
                if (li == ctx.SourceLodIndex) continue;
                if (UvProgress.CancelRequested)
                {
                    UvtLog.Warn("[Transfer] Cancelled by user — stopping after LOD" + li);
                    break;
                }
                UvProgress.SetPhase($"Transfer → LOD{li}",
                                    fraction: targetLodCount > 0 ? (float)processed / targetLodCount : 0f,
                                    detail: $"LOD{li}");
                await ExecTransferLodImpl(li, useAsync);
                processed++;
            }
        }

        void RecordIncludedTransferMeshes()
        {
            var recorder = BenchmarkRecorder.Current;
            if (recorder == null) return;
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include) continue;
                recorder.RecordMesh(e);
            }
        }

        static Task<GroupedShellTransfer.TransferResult> TransferMesh(Mesh target, Mesh source,
            List<GroupedShellTransfer.OverlapSourceHint> overlapHints,
            List<GroupedShellTransfer.CrossLodMatchHint> matchHints, int atlasWidth, int atlasHeight, bool useAsync)
        {
            if (useAsync)
                return GroupedShellTransfer.TransferAsync(target, source, overlapHints, matchHints, atlasWidth, atlasHeight);
            return Task.FromResult(GroupedShellTransfer.Transfer(target, source, overlapHints, matchHints, atlasWidth, atlasHeight));
        }

        async Task ExecTransferLodImpl(int tLod, bool useAsync)
        {
            var targets = ctx.ForLod(tLod);
            if (targets.Count == 0) return;
            var sources = ctx.ForLod(ctx.SourceLodIndex);
            if (sources.Count == 0) return;

            foreach (var tgt in targets)
            {
                if (UvProgress.CancelRequested) break;
                await TransferTargetMesh(tgt, sources, tLod, useAsync);
            }
        }

        async Task TransferTargetMesh(MeshEntry tgt, List<MeshEntry> sources, int tLod, bool useAsync)
        {
            EnsureTransferWorkingMesh(tgt);

            // Find matching source by mesh group key
            MeshEntry srcEntry = null;
            if (!string.IsNullOrEmpty(tgt.meshGroupKey))
                srcEntry = sources.FirstOrDefault(s => s.meshGroupKey == tgt.meshGroupKey);
            if (srcEntry == null)
                srcEntry = sources[0];

            Mesh srcMesh = srcEntry.repackedMesh ?? srcEntry.originalMesh;
            Mesh tgtMesh = tgt.originalMesh;
            if (srcMesh == null || tgtMesh == null) return;

            string meshGroupKey = tgt.meshGroupKey ?? tgt.renderer.name;
            var hintKey = (source: srcEntry, meshGroupKey: meshGroupKey);
            if (!crossLodHints.TryGetValue(hintKey, out var hintState))
            {
                hintState = new CrossLodHintState();
                crossLodHints.Add(hintKey, hintState);
            }

            if (!HasSourceShellTransforms(srcMesh)) return;

            UvProgress.Report(-1f, $"Transfer LOD{tLod} ← '{tgt.renderer.name}'");
            var tr = await TransferMesh(tgtMesh, srcMesh,
                hintState.overlapHints.Count > 0 ? hintState.overlapHints : null,
                hintState.matchHints.Count > 0 ? hintState.matchHints : null,
                srcEntry.repackedAtlasWidth > 0 ? (int)srcEntry.repackedAtlasWidth : 0,
                srcEntry.repackedAtlasHeight > 0 ? (int)srcEntry.repackedAtlasHeight : 0, useAsync);
            if (tr.uv2 == null) { UvtLog.Warn($"[Transfer] Failed for '{tgt.renderer.name}'"); return; }

            // Accumulate overlap hints for subsequent LODs
            if (tr.overlapHints != null && tr.overlapHints.Count > 0)
                hintState.overlapHints.AddRange(tr.overlapHints);
            // Replace match hints with this LOD's matches (latest LOD drives
            // next LOD's hint-guided matching; stale hints from older LODs
            // could conflict with changing geometry)
            hintState.matchHints.Clear();
            if (tr.matchHints != null && tr.matchHints.Count > 0)
                hintState.matchHints.AddRange(tr.matchHints);

            ApplyTransferResult(tgt, tgtMesh, tr, tLod);
        }

        bool HasSourceShellTransforms(Mesh srcMesh)
        {
            int srcId = srcMesh.GetInstanceID();
            if (!shellTransformCache.TryGetValue(srcId, out var srcInfos))
            {
                srcInfos = GroupedShellTransfer.AnalyzeSource(srcMesh);
                if (srcInfos != null) shellTransformCache[srcId] = srcInfos;
            }
            return srcInfos != null;

        }

        void ApplyTransferResult(MeshEntry tgt, Mesh tgtMesh, GroupedShellTransfer.TransferResult tr, int tLod)
        {
            // Build output mesh with UV2 applied
            var om = UnityEngine.Object.Instantiate(tgtMesh);
            om.name = tgtMesh.name + "_uvTransfer";
            if (ctx.ClampLightmapToUnit)
            {
                int clamped = XatlasRepack.ClampUvsToUnit(tr.uv2);
                if (clamped > 0)
                    UvtLog.Verbose(UvtLog.Category.Match,
                        $"Clamped {clamped} UV2 vert(s) into [0,1] on '{tgt.renderer.name}'");
            }
            om.SetUVs(1, new List<Vector2>(tr.uv2));
            tgt.transferredMesh = om;
            tgt.shellTransferResult = tr;

            // Validation
            BenchmarkRecorder.Current?.StageBegin("validate");
            tgt.validationReport = TransferValidator.Validate(tgtMesh, tr.uv2, tr);
            BenchmarkRecorder.Current?.StageEnd("validate");

            float pct = tr.verticesTotal > 0 ? tr.verticesTransferred * 100f / tr.verticesTotal : 0;
            UvtLog.Info($"[Transfer] '{tgt.renderer.name}' LOD{tLod}: {tr.shellsMatched} shells, {pct:F0}% coverage");
        }

        void ApplyUv2ToFbx() => ctx.Assets.ApplyUv2Public();
        public void ExportFbxPublic(bool overwriteSource) => ctx.Assets.ExportFbxPublic(overwriteSource);
        public void ExportFbxPublic(bool overwriteSource, FbxExportIntent intent) => ctx.Assets.ExportFbxPublic(overwriteSource, intent);
        public void ApplyUv2Public() => ctx.Assets.ApplyUv2Public();
        public void SaveAllPublic() => ctx.Assets.SaveAllPublic();
        public void ExportVertexColorsToFbx() => ctx.Assets.ExportVertexColorsToFbx();
        public void ExportVertexColorsToFbx(string path, IEnumerable<MeshEntry> entries, int uvChannelOverride = -1)
            => ctx.Assets.ExportVertexColorsToFbx(path, entries, uvChannelOverride);
        public bool ExportVertexColorsToFbxAs(string sourcePath, string outputPath, IEnumerable<MeshEntry> entries, int uvChannelOverride = -1)
            => ctx.Assets.ExportVertexColorsToFbxAs(sourcePath, outputPath, entries, uvChannelOverride);
        public void ExportIsolatedChannelsToFbx(string path, IEnumerable<MeshEntry> entries, FbxExportIntent intent)
            => ctx.Assets.ExportIsolatedChannelsToFbx(path, entries, intent);

        public void BeforeAssetWrite() => RestoreAllPreviews();
        public void AfterAssetWrite() { OnRefresh(); SaveSettingsToSidecar(); RequestRepaint?.Invoke(); }

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
                    tris += MeshHygieneUtility.GetTriangleCount(mf != null ? mf.sharedMesh : null);
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
                // Restore original mesh on MeshFilter before destroying working copies.
                // Recorded so the reset itself is one revertible step.
                if (e.meshFilter != null && e.fbxMesh != null)
                {
                    Undo.RecordObject(e.meshFilter, "Reset Working Copies");
                    e.meshFilter.sharedMesh = e.fbxMesh;
                }
                DestroyWorkingMesh(ref e.transferredMesh);
                DestroyWorkingMesh(ref e.repackedMesh);
                e.repackedAtlasWidth = 0;
                e.repackedAtlasHeight = 0;
                if (e.originalMesh != null && e.originalMesh != e.fbxMesh) DestroyWorkingMesh(ref e.originalMesh);
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
            RequestRepaint?.Invoke();
        }

        static void DestroyWorkingMesh(ref Mesh mesh)
        {
            if (mesh == null) return;
            UnityEngine.Object.DestroyImmediate(mesh);
            mesh = null;
        }

        void ResetUv2FromFbx()
        {
            if (ctx.LodGroup == null) return;
            var fbxPaths = SidecarStore.FbxPaths(ctx.MeshEntries);
            SidecarStore.Delete(fbxPaths);
            AssetDatabase.Refresh();
            foreach (string fbx in fbxPaths)
                AssetDatabase.ImportAsset(fbx, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();

            ctx.Refresh(ctx.LodGroup);
            OnRefresh();
            RequestRepaint?.Invoke();
        }

        void ResetPipelineState()
        {
            if (ctx.LodGroup == null) return;
            if (!EditorUtility.DisplayDialog("Reset Pipeline State", "Delete all sidecars and reset?", "Reset", CancelButton)) return;

            RestoreAllPreviews();
            var fbxPaths = SidecarStore.FbxPaths(ctx.MeshEntries);
            SidecarStore.Delete(fbxPaths);
            AssetDatabase.Refresh();
            foreach (string fbx in fbxPaths)
                AssetDatabase.ImportAsset(fbx, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            ctx.Refresh(ctx.LodGroup);
            OnRefresh();
            RequestRepaint?.Invoke();
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
            selectedSidecarPath = selectedResetLabel = null;
            if (!SidecarStore.TryFindFirst(SidecarStore.FbxPaths(ctx?.MeshEntries), out string selectedFbxPath, out selectedSidecarPath))
                return;
            selectedResetLabel = System.IO.Path.GetFileNameWithoutExtension(selectedFbxPath);
        }

        void TryLoadSettingsFromSidecar()
        {
            var s = SidecarStore.LoadSettings(selectedSidecarPath);
            if (s == null) return;
            ctx.AtlasResolution = SanitizeAtlasResolution(s.atlasResolution);
            ctx.ShellPaddingPx = SanitizePadding(s.shellPaddingPx);
            ctx.BorderPaddingPx = SanitizePadding(s.borderPaddingPx);
            ctx.RepackPerMesh = s.repackPerMesh;
            SymmetrySplitMode = Enum.IsDefined(typeof(SymmetrySplitShells.ThresholdMode), s.symmetrySplitThresholdMode)
                ? (SymmetrySplitShells.ThresholdMode)s.symmetrySplitThresholdMode
                : SymmetrySplitShells.ThresholdMode.LegacyFixed;
            SymmetrySplitShells.CurrentThresholdMode = SymmetrySplitMode;
            ctx.SourceLodIndex = Mathf.Clamp(s.sourceLodIndex, 0, Mathf.Max(0, ctx.LodCount - 1));
            ctx.PipeSettings.saveNewMeshAssets = s.saveNewMeshAssets;
            if (IsSafeAssetFolderPath(s.savePath)) ctx.PipeSettings.savePath = s.savePath;
        }

        // The UI exposes atlas resolution as a free IntField, so the ceiling is
        // only here to keep a forged sidecar (or a typo) from turning into an
        // absurd xatlas allocation. It is deliberately far above the 4096 the
        // presets offer so manually typed values are never silently reduced.
        internal static int SanitizeAtlasResolution(int resolution) => Mathf.Clamp(resolution, 64, 16384);

        internal static int SanitizePadding(int padding) => Mathf.Clamp(padding, 0, 16);

        internal static bool IsSafeAssetFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string normalized = path.Replace('\\', '/').TrimEnd('/');
            if (normalized != "Assets" && !normalized.StartsWith("Assets/", StringComparison.Ordinal))
                return false;

            var segments = normalized.Split('/');
            foreach (string segment in segments)
                if (segment.Length == 0 || segment == "." || segment == "..") return false;
            return true;
        }

        void SaveSettingsToSidecar()
        {
            SidecarStore.SaveSettings(selectedSidecarPath, s =>
            {
                s.atlasResolution = ctx.AtlasResolution;
                s.shellPaddingPx = ctx.ShellPaddingPx;
                s.borderPaddingPx = ctx.BorderPaddingPx;
                s.repackPerMesh = ctx.RepackPerMesh;
                s.symmetrySplitThresholdMode = (int)SymmetrySplitMode;
                s.sourceLodIndex = ctx.SourceLodIndex;
                s.saveNewMeshAssets = ctx.PipeSettings.saveNewMeshAssets;
                s.savePath = ctx.PipeSettings.savePath;
            });
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
                    sidecarCache[fbxPath] = sidecar = SidecarStore.Load(fbxPath);
                if (sidecar == null) continue;
                RestoreShellMatch(e, sidecar);
            }
        }

        static void RestoreShellMatch(MeshEntry e, Uv2DataAsset sidecar)
        {
            var entry = sidecar.Find(e.fbxMesh.name);
            if (entry?.vertexToSourceShellDescriptor == null || entry.vertexToSourceShellDescriptor.Length == 0) return;
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

            UpdateSceneSpot(sv, e);
            if (e.type != EventType.Repaint) return;

            // Draw selected shell overlay in 3D
            DrawSelectedShellOverlay3D();

            DrawSceneSpotProjection();
        }

        void UpdateSceneSpot(SceneView sv, Event e)
        {
            // Raycast on MouseMove/MouseDrag, throttled to ~30fps
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - sceneSpotLastRaycastTime >= sceneSpotThrottleSec)
                {
                    sceneSpotLastRaycastTime = now;
                    RefreshSceneSpotHit(sv, e.mousePosition);
                }
            }
            else if (e.type == EventType.MouseLeaveWindow && canvas.HoverHitValid)
            {
                canvas.HoverHitValid = false;
                canvas.HoveredShellId = -1;
                sceneSpotCachedEntry = null;
                RequestRepaint?.Invoke();
            }

        }

        void RefreshSceneSpotHit(SceneView sv, Vector2 mousePosition)
        {
            var ray = HandleUtility.GUIPointToWorldRay(mousePosition);
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
                RequestRepaint?.Invoke();
            sv.Repaint();
        }

        void DrawSceneSpotProjection()
        {
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
                var sh = Shader.Find("Hidden/UnityMeshLab/SpotProjection");
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

        // Hover runs every ~33 ms, so the skip notice is rate-limited — but it must
        // not be silent: a skipped mesh simply stops responding to SceneView hover.
        void WarnHoverBudgetSkip(Mesh mesh)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - sceneSpotLastBudgetWarnTime < sceneSpotBudgetWarnIntervalSec) return;
            sceneSpotLastBudgetWarnTime = now;
            UvtLog.Warn($"[SceneSpot] Hover pick skipped '{(mesh != null ? mesh.name : "<null>")}' — " +
                        $"exceeds the {sceneSpotTriangleBudget} triangle budget per hover. " +
                        "Use the UV canvas or a lower preview LOD to inspect it.");
        }

        bool TryRaycastPreview(Ray ray, out SceneHit bestHit)
        {
            bestHit = default;
            bestHit.distance = float.PositiveInfinity;
            bool found = false;
            int remainingTriangleBudget = sceneSpotTriangleBudget;

            foreach (var entry in ctx.ForLod(ctx.PreviewLod))
            {
                Mesh mesh = ctx.DMesh(entry);
                if (mesh == null || entry.renderer == null) continue;

                Matrix4x4 l2w = entry.renderer.localToWorldMatrix;
                Bounds wb = TransformBounds(mesh.bounds, l2w);
                if (!wb.IntersectRay(ray, out float aabbDist) || aabbDist > bestHit.distance) continue;

                if (!FitsSceneTriangleBudget(mesh, remainingTriangleBudget))
                {
                    WarnHoverBudgetSkip(mesh);
                    continue;
                }

                found |= RaycastPreviewMesh(entry, mesh, l2w, ray, ref remainingTriangleBudget, ref bestHit);
            }
            return found;
        }

        static bool FitsSceneTriangleBudget(Mesh mesh, int remainingTriangleBudget)
        {
            // Inspect index metadata before reading mesh arrays: those properties make
            // full managed copies and shell extraction is linear in the face count.
            // Skip a mesh rather than partially testing it, which could report a false hit.
            ulong meshIndexCount = 0;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                ulong indexCount = mesh.GetIndexCount(subMesh);
                if (indexCount > (ulong)remainingTriangleBudget * 3UL - meshIndexCount)
                {
                    meshIndexCount = ulong.MaxValue;
                    break;
                }
                meshIndexCount += indexCount;
            }
            return meshIndexCount != ulong.MaxValue;
        }

        bool RaycastPreviewMesh(MeshEntry entry, Mesh mesh, Matrix4x4 l2w, Ray ray,
            ref int remainingTriangleBudget, ref SceneHit bestHit)
        {
            bool found = false;
            var v = mesh.vertices;
            var tri = canvas.GetTrianglesCached(mesh);
            var uv = canvas.RdUvCached(mesh, ctx.PreviewUvChannel);
            if (v == null || tri == null || uv == null) return false;
            if (tri.Length / 3 > remainingTriangleBudget)
            {
                WarnHoverBudgetSkip(mesh);
                return false;
            }
            remainingTriangleBudget -= tri.Length / 3;
            int[] faceToShell = ctx.UvPreviewShellCache.GetFaceToShell(mesh, ctx.PreviewUvChannel, uv, tri);

            for (int f = 0; f + 2 < tri.Length; f += 3)
            {
                int i0 = tri[f], i1 = tri[f + 1], i2 = tri[f + 2];
                if (i0 >= uv.Length || i1 >= uv.Length || i2 >= uv.Length) continue;
                if (!RaycastSceneTriangle(ray, l2w, v, tri, f, out float t, out var barycentric)) continue;
                if (t < 0f || t >= bestHit.distance) continue;

                bestHit.distance = t;
                bestHit.worldPos = ray.origin + ray.direction * t;
                bestHit.uv = uv[i0] * barycentric.x + uv[i1] * barycentric.y + uv[i2] * barycentric.z;
                bestHit.shellId = (faceToShell != null && f / 3 < faceToShell.Length) ? faceToShell[f / 3] : -1;
                bestHit.meshEntry = entry;
                found = true;
            }
            return found;
        }

        static bool RaycastSceneTriangle(Ray ray, Matrix4x4 l2w, Vector3[] v, int[] tri, int f,
            out float t, out Vector3 barycentric)
        {
            t = 0; barycentric = default;
            int i0 = tri[f], i1 = tri[f + 1], i2 = tri[f + 2];
            if (i0 >= v.Length || i1 >= v.Length || i2 >= v.Length) return false;

            Vector3 p0 = l2w.MultiplyPoint3x4(v[i0]);
            Vector3 p1 = l2w.MultiplyPoint3x4(v[i1]);
            Vector3 p2 = l2w.MultiplyPoint3x4(v[i2]);

            if (!RayTriMT(ray, p0, p1, p2, out t, out float b1, out float b2)) return false;
            barycentric = new Vector3(1f - b1 - b2, b1, b2);
            return true;
        }

        static Bounds TransformBounds(Bounds b, Matrix4x4 m) => MeshGeometry.TransformBounds(b, m);

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

            var cache = canvas.GetPreviewShellCache(ctx, mesh, ctx.PreviewUvChannel);
            if (cache?.shellById != null && cache.shellById.TryGetValue(uvHit.shellId, out var shell)
                && TryShellWorldBounds(shell, tris, verts, tr, out var shellBounds))
            {
                worldPos = shellBounds.center;
                idealDist = Mathf.Max(shellBounds.extents.magnitude * 1.5f, 0.3f);
            }
            FocusFacePoint(uvHit, tris, verts, tr, ref worldPos, ref faceNormal);

            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
            sv.pivot = worldPos;
            sv.size = idealDist;
            sv.rotation = Quaternion.LookRotation(-faceNormal);
            sv.Repaint();
        }

        static bool TryShellWorldBounds(UvShell shell, int[] tris, Vector3[] verts, Transform tr, out Bounds sb)
        {
            bool first = true;
            sb = new Bounds();
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
            return !first;
        }

        static void FocusFacePoint(ShellUvHit uvHit, int[] tris, Vector3[] verts, Transform tr,
            ref Vector3 worldPos, ref Vector3 faceNormal)
        {
            if (uvHit.faceIndex < 0) return;
            int i0 = uvHit.faceIndex * 3;
            if (i0 + 2 >= tris.Length) return;
            int vi0 = tris[i0], vi1 = tris[i0 + 1], vi2 = tris[i0 + 2];
            if (vi0 < 0 || vi1 < 0 || vi2 < 0 || vi0 >= verts.Length || vi1 >= verts.Length || vi2 >= verts.Length) return;
            var bary = uvHit.barycentric;
            var localPos = verts[vi0] * bary.x + verts[vi1] * bary.y + verts[vi2] * bary.z;
            worldPos = tr.TransformPoint(localPos);
            var triNormal = Vector3.Cross(verts[vi1] - verts[vi0], verts[vi2] - verts[vi0]);
            if (!(triNormal.sqrMagnitude > 1e-8f)) return;
            faceNormal = tr.TransformDirection(triNormal.normalized).normalized;
            if (faceNormal.sqrMagnitude < 0.5f)
                faceNormal = tr.up.sqrMagnitude > 0.001f ? tr.up : Vector3.up;
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

        static bool ToggleIssueBit(ref TransferValidator.TriIssue mask, TransferValidator.TriIssue bit, string label)
        {
            bool on = (mask & bit) != 0;
            bool newOn = EditorGUILayout.ToggleLeft(label, on);
            if (newOn == on) return false;
            if (newOn) mask |= bit;
            else       mask &= ~bit;
            return true;
        }

        static void ColorBtn(Color col, string l, int h, Action a)
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
