// LightmapTransferTool.cs — UV2 Lightmap Transfer tool for Mesh Lab.
// Setup → Repack → Transfer → Apply pipeline.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using UnityEditor.Formats.Fbx.Exporter;
#endif

namespace SashaRX.UnityMeshLab
{
    public class LightmapTransferTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        Action requestRepaint;

        // Surface-area scans materialize mesh vertex/index data and are far too
        // expensive to run on every OnGUI repaint. Cache the result keyed by the
        // exact mesh references it was computed from, and recompute only when
        // that set changes (or when an actual repack refreshes it).
        readonly List<Mesh> _areaPreviewMeshes = new List<Mesh>();
        double _areaPreview;
        bool _hasAreaPreview;

        public string ToolName  => "UV2 Transfer";
        public string ToolId    => "uv2_transfer";
        public int    ToolOrder => 0;
        public Action RequestRepaint { set => requestRepaint = value; }

        static bool IsBruteForcePackAvailable(int internalOversample)
        {
            int oversample = internalOversample > 0 ? internalOversample : 1;
            return oversample <= 1;
        }

        static bool HasIncludedTransferTargets(IEnumerable<MeshEntry> entries, int sourceLodIndex)
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

        static bool CanApplyUv2(bool hasRepack, bool hasTransfer)
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

        const int MaxSweepValuesPerDimension = 16;
        const int MaxSweepCells = 256;

        static bool TryValidateSweep(TestSuiteAsset.SweepMatrix sm, UvToolContext context,
                                     out int cellCount, out string error)
        {
            cellCount = 0;
            error = null;
            if (sm == null || context == null)
            {
                error = "Sweep configuration is missing.";
                return false;
            }

            var resolutions = sm.atlasResolutions?.Length > 0
                ? sm.atlasResolutions : new[] { context.AtlasResolution };
            var shellPaddings = sm.shellPaddingPxVariants?.Length > 0
                ? sm.shellPaddingPxVariants : new[] { context.ShellPaddingPx };
            var borderPaddings = sm.borderPaddingPxVariants?.Length > 0
                ? sm.borderPaddingPxVariants : new[] { context.BorderPaddingPx };
            var arapIterations = sm.arapIterationsVariants?.Length > 0
                ? sm.arapIterationsVariants
                : new[] { context.ReparameterizeStretchedShells ? context.ArapIterations : 0 };
            var stretchThresholds = sm.stretchThresholdVariants?.Length > 0
                ? sm.stretchThresholdVariants : new[] { context.StretchThreshold };

            if (!ValidateSweepDimension(resolutions, 64, 4096, "atlas resolution", out error) ||
                !ValidateSweepDimension(shellPaddings, 0, 64, "shell padding", out error) ||
                !ValidateSweepDimension(borderPaddings, 0, 64, "border padding", out error) ||
                !ValidateSweepDimension(arapIterations, 0, 200, "ARAP iterations", out error) ||
                !ValidateSweepDimension(stretchThresholds, 1f, 3f, "stretch threshold", out error))
                return false;

            long total = (long)resolutions.Length * shellPaddings.Length * borderPaddings.Length
                       * arapIterations.Length * stretchThresholds.Length;
            if (total > MaxSweepCells)
            {
                error = $"Sweep has {total} cells; the maximum is {MaxSweepCells}.";
                return false;
            }

            cellCount = (int)total;
            return true;
        }

        static bool ValidateSweepDimension(int[] values, int min, int max, string label,
                                           out string error)
        {
            if (values.Length > MaxSweepValuesPerDimension)
            {
                error = $"{label} has {values.Length} values; the maximum is {MaxSweepValuesPerDimension}.";
                return false;
            }
            foreach (int value in values)
                if (value < min || value > max)
                {
                    error = $"Invalid {label} {value}; allowed range is {min}..{max}.";
                    return false;
                }
            error = null;
            return true;
        }

        static bool ValidateSweepDimension(float[] values, float min, float max, string label,
                                           out string error)
        {
            if (values.Length > MaxSweepValuesPerDimension)
            {
                error = $"{label} has {values.Length} values; the maximum is {MaxSweepValuesPerDimension}.";
                return false;
            }
            foreach (float value in values)
                if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max)
                {
                    error = $"Invalid {label} {value}; allowed range is {min}..{max}.";
                    return false;
                }
            error = null;
            return true;
        }

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
        bool foldUv0Analysis;
        bool foldLogFilters;
        bool foldValidationOverlay;
        bool splitTargetsInSymmetryStep;
        bool skipSymmetrySplitStep;
        SymmetrySplitShells.ThresholdMode symSplitThresholdMode = SymmetrySplitShells.ThresholdMode.LegacyFixed;
        HashSet<int> lastSymmetrySplitLods = new HashSet<int>();

        // ── Pipeline stage toggles (Setup tab) ──
        // Each toggle controls whether the corresponding stage runs as part
        // of the Full Pipeline. They default ON; the user can deselect a
        // stage to skip it (useful for "transfer only" or "repack only"
        // runs without invoking Weld every time).
        bool stageRunAnalyzeUv0 = true;
        bool stageRunWeldUv0    = true;
        bool stageRunRepack     = true;
        bool stageRunTransfer   = true;

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
                    requestRepaint?.Invoke();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (System.Exception ex)
            {
                // Synchronous throw before the Task even starts.
                _pipelineInFlight = false;
                UvtLog.Error($"[Pipeline] '{label}' failed to start: {ex.Message}");
                if (UvProgress.IsActive) UvProgress.Fail(ex.Message);
                requestRepaint?.Invoke();
            }
        }
        Vector2 reportScroll;
        TestSuiteAsset sweepSuite;

        // Cache of the filterable UvtLog categories — enumerated once on type init
        // to avoid per-repaint Enum.GetValues allocations inside the Log filters UI.
        // Composite flag UvtLog.Category.All is filtered out; only single bits remain.
        static readonly UvtLog.Category[] s_logCategories = BuildLogCategoryList();
        static UvtLog.Category[] BuildLogCategoryList()
        {
            var all = (UvtLog.Category[])Enum.GetValues(typeof(UvtLog.Category));
            var list = new List<UvtLog.Category>(all.Length);
            foreach (var c in all)
                if (c != UvtLog.Category.All) list.Add(c);
            return list.ToArray();
        }

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
        readonly ShellColorModelPreview.PreviewShellCache shellColorPreviewCache =
            new ShellColorModelPreview.PreviewShellCache();
        string previewConflictNotice;
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
            // ▸ Developer. Houses Parameter Sweep, Log Filters, and UV0
            // Analysis & Fix; production users see a clean Setup tab without
            // these benchmark / diagnostic blocks.
            if (MeshLabProjectSettings.Instance.showDebugUI)
                DrawSetupDebugSection();
        }

        // ──────────────── Setup tab debug section ──────────────────────
        //
        // Houses diagnostic and benchmarking blocks that are not part of
        // day-to-day production use: Parameter Sweep, Log Filters, UV0
        // Analysis & Fix. Gated by MeshLabProjectSettings.showDebugUI so
        // shipping artists see a clean Setup tab; developers flip the
        // toggle in Project Settings ▸ Mesh Lab ▸ Developer.
        void DrawSetupDebugSection()
        {
            EditorGUILayout.Space(10);
            // Banner so the debug block is unmistakably distinct from the
            // production sections above it.
            var bannerRect = GUILayoutUtility.GetRect(0, 20f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(bannerRect, new Color(0.55f, 0.35f, 0.10f, 0.30f));
            var bannerStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(1f, 0.85f, 0.55f) },
            };
            GUI.Label(new Rect(bannerRect.x + 6f, bannerRect.y, bannerRect.width - 12f, bannerRect.height),
                "DEBUG  ·  hide via Project Settings ▸ Mesh Lab ▸ Show Debug UI",
                bannerStyle);

            // ── Parameter Sweep ──
            EditorGUILayout.Space(6);
            H("Parameter Sweep");
            sweepSuite = (TestSuiteAsset)EditorGUILayout.ObjectField(
                "Sweep suite", sweepSuite, typeof(TestSuiteAsset), false);
            int cells = 0;
            string sweepError = null;
            if (sweepSuite != null && sweepSuite.sweep != null)
                TryValidateSweep(sweepSuite.sweep, ctx, out cells, out sweepError);
            if (!string.IsNullOrEmpty(sweepError))
                EditorGUILayout.HelpBox(sweepError, MessageType.Error);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(sweepSuite == null || cells == 0))
                {
                    if (GUILayout.Button($"Run Sweep ({cells})", GUILayout.Height(22)))
                        ExecSweep(sweepSuite.sweep);
                }
                if (GUILayout.Button(new GUIContent("Rebuild Report",
                        "Pick a BenchmarkReports/ folder and rebuild summary.csv / winner.json / index.html " +
                        "from the per-cell CSVs already on disk. Use this after a mid-sweep Unity crash."),
                        GUILayout.Height(22)))
                {
                    ExecRebuildSweepReport();
                }
            }

            // ── Log filters ──
            EditorGUILayout.Space(6);
            foldLogFilters = EditorGUILayout.Foldout(foldLogFilters, "Log filters", true);
            if (foldLogFilters)
            {
                EditorGUI.indentLevel++;
                UvtLog.Current = (UvtLog.Level)EditorGUILayout.EnumPopup("Level", UvtLog.Current);
                var enabled = UvtLog.EnabledCategories;
                for (int i = 0; i < s_logCategories.Length; i++)
                {
                    var cat = s_logCategories[i];
                    bool on = (enabled & cat) != 0;
                    bool newOn = EditorGUILayout.ToggleLeft(cat.ToString(), on);
                    if (newOn != on) UvtLog.SetCategoryEnabled(cat, newOn);
                }
                EditorGUI.indentLevel--;
            }

            // ── UV0 Analysis & Fix ──
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

            // 2. Weld UV0 — merges false-seam vertices using source-guided weld.
            DrawStageRow(2, "Weld UV0",
                "Merge false-seam vertices (UV0 verts that share position but were split). "
                + "Required for clean shell extraction in Repack and Transfer.",
                ref stageRunWeldUv0, hasSettings: false, drawSettings: null);

            // 3. Symmetry split — uses inverted skipSymmetrySplitStep field
            // so existing diagnostic flag continues to work elsewhere.
            bool runSym = !skipSymmetrySplitStep;
            DrawStageRow(3, "Symmetry Split",
                "Split mirrored / overlapping UV0 shells in the source so each "
                + "physical surface gets its own atlas tile. Auto-tunes the "
                + "separation threshold across a few values and picks the best.",
                ref runSym, hasSettings: true, drawSettings: () =>
                {
                    symSplitThresholdMode = (SymmetrySplitShells.ThresholdMode)EditorGUILayout.EnumPopup(
                        new GUIContent("Threshold mode",
                            "Strategy for picking the SymSplit separation threshold. "
                            + "Legacy Fixed uses 0.10; Adaptive picks per-shell from area."),
                        symSplitThresholdMode);
                    SymmetrySplitShells.CurrentThresholdMode = symSplitThresholdMode;
                    // Advanced / debug-only toggle — hidden from production UI.
                    if (MeshLabProjectSettings.Instance.showDebugUI)
                    {
                        splitTargetsInSymmetryStep = EditorGUILayout.ToggleLeft(
                            new GUIContent("Apply to target LODs (advanced)",
                                "Run SymSplit on every included LOD instead of only the source. "
                                + "Coordinated across LODs so each surface keeps its identity."),
                            splitTargetsInSymmetryStep);
                    }
                });
            skipSymmetrySplitStep = !runSym;

            // 4. Repack — main settings live here so users see resolution etc.
            // at the same place as the stage toggle.
            DrawStageRow(4, "Repack (xatlas)",
                "Pack source LOD UVs into a clean UV2 atlas using xatlas. "
                + "Auto-resolution from texel density is the default; the "
                + "Mode picker below switches to manual resolution.",
                ref stageRunRepack, hasSettings: true, drawSettings: () =>
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
                });

            // 5. Transfer.
            bool hasTargets = ctx.LodGroup != null && HasIncludedTransferTargets(ctx.MeshEntries, ctx.SourceLodIndex);
            DrawStageRow(5, "Transfer to LODs",
                hasTargets
                    ? "Project source UV2 onto every included target LOD."
                    : "No target LODs included — Transfer will skip even when enabled.",
                ref stageRunTransfer, hasSettings: false, drawSettings: null, dimmed: !hasTargets);

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
            Color stripeColor = enabled
                ? (dimmed ? new Color(0.45f, 0.55f, 0.45f) : new Color(0.35f, 0.78f, 0.45f))
                : new Color(0.40f, 0.40f, 0.40f);
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
            if (MeshLabProjectSettings.Instance.showDebugUI)
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
            symSplitThresholdMode = (SymmetrySplitShells.ThresholdMode)EditorGUILayout.EnumPopup(
                new GUIContent("SymSplit thresholds",
                    "Shared with Setup tab. Strategy for picking the SymSplit separation threshold."),
                symSplitThresholdMode);
            SymmetrySplitShells.CurrentThresholdMode = symSplitThresholdMode;
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
                bool allDone = ee.All(e => e.transferredMesh != null);
                bool noneDone = ee.All(e => e.transferredMesh == null);

                int totalV = 0, transferredV = 0;
                foreach (var e in ee)
                {
                    if (e.shellTransferResult == null) continue;
                    totalV += e.shellTransferResult.verticesTotal;
                    transferredV += e.shellTransferResult.verticesTransferred;
                }
                float coverage = totalV > 0 ? transferredV * 100f / totalV : 0f;

                string headerIcon = allDone ? "✓" : (noneDone ? "•" : "◐");
                string summary = totalV > 0
                    ? $"   LOD{li}  ·  {ee.Count} mesh{(ee.Count == 1 ? "" : "es")}  ·  {coverage:F0}% verts"
                    : $"   LOD{li}  ·  {ee.Count} mesh{(ee.Count == 1 ? "" : "es")}";

                // Status colour on the icon glyph; the foldout label itself
                // stays the default colour so it remains readable.
                var oldContent = GUI.contentColor;
                GUI.contentColor = allDone
                    ? new Color(0.45f, 0.90f, 0.55f)
                    : (noneDone ? new Color(0.65f, 0.65f, 0.65f) : new Color(0.95f, 0.78f, 0.35f));
                if (!transferLodFoldouts.ContainsKey(li)) transferLodFoldouts[li] = false;
                transferLodFoldouts[li] = EditorGUILayout.Foldout(transferLodFoldouts[li], headerIcon + summary, true);
                GUI.contentColor = oldContent;
                if (!transferLodFoldouts[li]) continue;

                EditorGUI.indentLevel++;
                foreach (var e in ee)
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
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(_pipelineInFlight))
            {
                ColorBtn(new Color(.3f,.6f,1f), "Transfer All Targets", 26,
                    () => FireAndForget(ExecTransferAllAsync, "Transfer All Targets"));
            }

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
                        requestRepaint?.Invoke();
                    }
                    EditorGUILayout.LabelField(
                        mask == TransferValidator.TriIssue.None ? "(all triangles drawn)" : $"mask: {mask}",
                        EditorStyles.miniLabel);
                    EditorGUI.indentLevel--;
                }
            }

            if (CanApplyUv2(ctx.HasRepack, ctx.HasTransfer))
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

        // Sync entry — used by sweep loops where each cell runs end-to-end
        // before the loop moves on. Editor blocks for the cell duration.
        void ExecFullPipeline() => ExecFullPipelineImpl("FullPipeline", useAsync: false).GetAwaiter().GetResult();
        void ExecFullPipeline(string runLabel) => ExecFullPipelineImpl(runLabel, useAsync: false).GetAwaiter().GetResult();

        // Async entry — button-click path; editor main thread stays responsive.
        Task ExecFullPipelineAsync() => ExecFullPipelineImpl("FullPipeline", useAsync: true);

        async Task ExecFullPipelineImpl(string runLabel, bool useAsync)
        {
            if (ctx.LodGroup == null) return;
            using var _bench = BenchmarkRecorder.NewRun(ctx, runLabel,
                splitTargetsInSymmetryStep, symSplitThresholdMode);
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
        /// Run the full pipeline once per cell of a sweep matrix (cartesian product of
        /// atlasResolutions × shellPaddingPxVariants × borderPaddingPxVariants ×
        /// arapIterationsVariants × stretchThresholdVariants). Each cell writes
        /// its own CSV/JSON with runLabel "sweep_res{R}_pad{S}_bdr{B}_arap{N}_stretch{T}".
        /// After the loop, if at least two cells completed, BenchmarkSweep.WriteAggregateReport
        /// is invoked to score the cells and write a sweep_<timestamp>/summary.csv +
        /// winner.json under BenchmarkReports/. Original ctx values are restored on exit.
        /// </summary>
        void ExecSweep(TestSuiteAsset.SweepMatrix sm)
        {
            if (ctx.LodGroup == null || sm == null) return;
            if (!TryValidateSweep(sm, ctx, out int total, out string validationError))
            {
                UvtLog.Error(UvtLog.Category.Benchmark, $"[Sweep] {validationError}");
                EditorUtility.DisplayDialog("Invalid sweep", validationError, "OK");
                return;
            }
            var resArr = (sm.atlasResolutions != null && sm.atlasResolutions.Length > 0)
                ? sm.atlasResolutions : new[] { ctx.AtlasResolution };
            var padArr = (sm.shellPaddingPxVariants != null && sm.shellPaddingPxVariants.Length > 0)
                ? sm.shellPaddingPxVariants : new[] { ctx.ShellPaddingPx };
            var bdrArr = (sm.borderPaddingPxVariants != null && sm.borderPaddingPxVariants.Length > 0)
                ? sm.borderPaddingPxVariants : new[] { ctx.BorderPaddingPx };
            var arapItersArr = (sm.arapIterationsVariants != null && sm.arapIterationsVariants.Length > 0)
                ? sm.arapIterationsVariants
                : new[] { ctx.ReparameterizeStretchedShells ? ctx.ArapIterations : 0 };
            var stretchArr = (sm.stretchThresholdVariants != null && sm.stretchThresholdVariants.Length > 0)
                ? sm.stretchThresholdVariants : new[] { ctx.StretchThreshold };

            // Snapshot ctx fields we mutate — restored unconditionally below.
            int   origRes         = ctx.AtlasResolution;
            int   origPad         = ctx.ShellPaddingPx;
            int   origBdr         = ctx.BorderPaddingPx;
            bool  origArapOn      = ctx.ReparameterizeStretchedShells;
            int   origArapIters   = ctx.ArapIterations;
            float origStretchThr  = ctx.StretchThreshold;
            // The sweep iterates an explicit atlasResolutions array. If the
            // user left AutoFromTexelDensity selected, ExecRepackCore would
            // overwrite ctx.AtlasResolution every cell and every row would
            // record the auto value — collapsing the resolution dimension of
            // the sweep. Force Manual for the duration of the sweep so each
            // cell's `r` is the resolution xatlas actually packs at.
            ResolutionMode origResMode = ctx.RepackResolutionMode;
            ctx.RepackResolutionMode = ResolutionMode.Manual;

            // Aligned lists: writtenCsvPaths[i] is the CSV path produced by
            // cellConfigs[i]. Passed to BenchmarkSweep after the loop completes.
            var writtenCsvPaths = new List<string>(total);
            var cellConfigs     = new List<BenchmarkSweep.CellConfig>(total);

            // Pre-create the sweep_<timestamp>/ directory so the incremental
            // aggregate after every successful cell can rewrite summary.csv /
            // winner.json / index.html into a stable path. The final aggregate
            // in the finally block uses the same directory.
            // Millisecond precision — second-level stamps collided when an
            // operator kicked off two sweeps in the same second (scripted
            // runs, quick UI re-clicks). Without ms the second sweep would
            // overwrite the first one's summary.csv / winner.json.
            string sweepStamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff",
                System.Globalization.CultureInfo.InvariantCulture);
            string projectRoot = System.IO.Directory.GetParent(Application.dataPath)?.FullName
                                 ?? Application.dataPath;
            string sweepDir = System.IO.Path.Combine(projectRoot, "BenchmarkReports",
                $"sweep_{sweepStamp}");
            try { System.IO.Directory.CreateDirectory(sweepDir); }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[Sweep] Could not pre-create sweep dir '{sweepDir}': {ex.Message}");
                sweepDir = null;
            }

            int done = 0;
            bool cancelled = false;
            UvProgress.Begin($"Pipeline Sweep ({total} cells)", cancelable: true);
            try
            {
                foreach (int r in resArr)
                {
                    if (cancelled) break;
                    foreach (int s in padArr)
                    {
                        if (cancelled) break;
                        foreach (int b in bdrArr)
                        {
                            if (cancelled) break;
                            foreach (int arapIters in arapItersArr)
                            {
                                if (cancelled) break;
                                foreach (float stretchThr in stretchArr)
                                {
                                    if (cancelled) break;
                                    UvProgress.Report(
                                        (float)done / Mathf.Max(1, total),
                                        $"cell {done + 1}/{total}: res={r}, shellPad={s}, borderPad={b}, " +
                                        $"arap={arapIters}, stretch={stretchThr:F2}");
                                    if (UvProgress.CancelRequested)
                                    {
                                        cancelled = true;
                                        break;
                                    }

                                    UvtLog.Verbose(UvtLog.Category.Benchmark,
                                        $"[Sweep] cell {done + 1}/{total}: GC heap " +
                                        $"{GC.GetTotalMemory(false) / (1024 * 1024)} MB");

                                    ctx.AtlasResolution               = r;
                                    ctx.ShellPaddingPx                = s;
                                    ctx.BorderPaddingPx               = b;
                                    ctx.ReparameterizeStretchedShells = arapIters > 0;
                                    if (arapIters > 0) ctx.ArapIterations = arapIters;
                                    ctx.StretchThreshold              = stretchThr;

                                    if (sm.resetBetweenRuns) ResetWorkingCopies();

                                    // Encode stretch threshold as e.g. "1p50" — Sanitize() collapses '.' to '_'
                                    // and that would break the recovery regex's _stretch(\d+p\d+)_ token.
                                    int stretchHundredths = Mathf.RoundToInt(stretchThr * 100f);
                                    string stretchTag = $"{stretchHundredths / 100}p{(stretchHundredths % 100):D2}";
                                    string label = $"sweep_res{r}_pad{s}_bdr{b}_arap{arapIters}_stretch{stretchTag}";
                                    string csvBefore = BenchmarkRecorder.LastWrittenCsvPath;
                                    try
                                    {
                                        ExecFullPipeline(label);
                                    }
                                    catch (Exception ex)
                                    {
                                        UvtLog.Error(UvtLog.Category.Benchmark,
                                            $"[Sweep] Cell {done + 1}/{total} threw: {ex.Message}");
                                    }

                                    // Capture the CSV the recorder just wrote (null if
                                    // the pipeline aborted before WriteArtefacts ran).
                                    string csvAfter = BenchmarkRecorder.LastWrittenCsvPath;
                                    string csvPath = (csvAfter != null && csvAfter != csvBefore)
                                        ? csvAfter : null;

                                    writtenCsvPaths.Add(csvPath);
                                    cellConfigs.Add(new BenchmarkSweep.CellConfig
                                    {
                                        atlasRes         = r,
                                        shellPad         = s,
                                        borderPad        = b,
                                        arapEnabled      = arapIters > 0,
                                        arapIterations   = arapIters,
                                        stretchThreshold = stretchThr,
                                    });
                                    done++;

                                    // Incremental aggregate: rewrite summary/winner/html after
                                    // every successful cell so a mid-sweep Unity crash leaves
                                    // usable reports. WriteAggregateReport accepts the same
                                    // pre-created sweepDir each call and overwrites in place.
                                    if (!string.IsNullOrEmpty(sweepDir))
                                    {
                                        try
                                        {
                                            BenchmarkSweep.WriteAggregateReport(
                                                writtenCsvPaths, cellConfigs, sweepDir);
                                        }
                                        catch (Exception ex)
                                        {
                                            UvtLog.Warn(UvtLog.Category.Benchmark,
                                                $"[Sweep] Incremental aggregate failed: {ex.Message}");
                                        }
                                    }

                                    // Release temporary meshes accumulated by the pipeline so
                                    // the native atlas allocator and the managed GC heap don't
                                    // balloon across 48+ cells (the original 30-cell crash was
                                    // most likely OOM or fragmentation in this code path).
                                    try
                                    {
                                        System.GC.Collect();
                                        UnityEngine.Resources.UnloadUnusedAssets();
                                    }
                                    catch (Exception ex)
                                    {
                                        UvtLog.Verbose(UvtLog.Category.Benchmark,
                                            $"[Sweep] Between-cell cleanup hiccup: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                if (cancelled) UvProgress.Cancel(); else UvProgress.End();
                ctx.AtlasResolution               = origRes;
                ctx.ShellPaddingPx                = origPad;
                ctx.BorderPaddingPx               = origBdr;
                ctx.ReparameterizeStretchedShells = origArapOn;
                ctx.ArapIterations                = origArapIters;
                ctx.StretchThreshold              = origStretchThr;
                ctx.RepackResolutionMode          = origResMode;
                UvtLog.Info(UvtLog.Category.Benchmark,
                    $"Sweep complete: {done}/{total} cells{(cancelled ? " (cancelled)" : "")}");

                // Aggregate per-cell CSVs into a sweep_<timestamp>/summary.csv +
                // winner.json. The incremental writer in the foreach above
                // already keeps these in sync after every successful cell —
                // this final call refreshes the same sweepDir to cover the
                // edge case where the last cell threw before the incremental
                // write executed. Safe to call even if individual cells
                // produced no CSV (they are recorded as failed entries).
                if (writtenCsvPaths.Count >= 1)
                {
                    try
                    {
                        BenchmarkSweep.WriteAggregateReport(writtenCsvPaths, cellConfigs, sweepDir);
                    }
                    catch (Exception ex)
                    {
                        UvtLog.Error(UvtLog.Category.Benchmark,
                            $"[Sweep] Aggregate report failed: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Prompts the user for a BenchmarkReports/ folder and asks
        /// <see cref="BenchmarkSweep.RebuildFromExistingCsvs"/> to reconstruct
        /// summary.csv / winner.json / index.html from whatever per-cell CSVs
        /// are still on disk after a mid-sweep Unity crash. Surfaces the result
        /// (or a "no CSVs found" message) via <see cref="EditorUtility.DisplayDialog"/>.
        /// </summary>
        void ExecRebuildSweepReport()
        {
            string projectRoot = System.IO.Directory.GetParent(Application.dataPath)?.FullName
                                 ?? Application.dataPath;
            string defaultDir = System.IO.Path.Combine(projectRoot, "BenchmarkReports");
            if (!System.IO.Directory.Exists(defaultDir)) defaultDir = projectRoot;

            string picked = EditorUtility.OpenFolderPanel(
                "Pick BenchmarkReports/ folder to rebuild", defaultDir, "");
            if (string.IsNullOrEmpty(picked)) return;

            string outDir;
            try
            {
                outDir = BenchmarkSweep.RebuildFromExistingCsvs(picked);
            }
            catch (Exception ex)
            {
                UvtLog.Error(UvtLog.Category.Benchmark,
                    $"[Sweep] Rebuild threw: {ex.Message}");
                EditorUtility.DisplayDialog("Rebuild Sweep Report",
                    $"Rebuild failed: {ex.Message}", "OK");
                return;
            }

            if (string.IsNullOrEmpty(outDir))
            {
                EditorUtility.DisplayDialog("Rebuild Sweep Report",
                    "No matching CSVs were found in:\n" + picked +
                    "\n\nLook for files named *_sweep_resR_padS_bdrB_arapA_stretchT_*.csv.",
                    "OK");
                return;
            }

            EditorUtility.DisplayDialog("Rebuild Sweep Report",
                "Recovery report written to:\n" + outDir +
                "\n\nOpen index.html in a browser for the per-run gallery.",
                "OK");
        }

        /// <summary>
        /// Run the auto-tune full pipeline. Returns <c>true</c> when the
        /// pipeline ran end-to-end and the in-memory per-mesh state reflects
        /// the just-completed run; returns <c>false</c> when the user
        /// cancelled mid-flight so the caller can skip artefact recording
        /// (stale state from a prior run would otherwise be written).
        /// </summary>
        bool ExecFullPipelineCore() => ExecFullPipelineCoreImpl(useAsync: false).GetAwaiter().GetResult();

        async Task<bool> ExecFullPipelineCoreImpl(bool useAsync)
        {
            string version = UnityEditor.PackageManager.PackageInfo
                .FindForAssembly(typeof(LightmapTransferTool).Assembly)?.version ?? "0.0.0";
            UvtLog.Info($"[Pipeline] Starting full pipeline... (v{version})");

            // Reset per-stage outcome state — fresh run, fresh icons.
            for (int i = 0; i < stageOutcome.Length; i++) stageOutcome[i] = StageStatus.Idle;

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
                try { ExecWeldUv0(); stageOutcome[2] = StageStatus.Success; }
                catch { stageOutcome[2] = StageStatus.Failed; throw; }
            }
            else
            {
                stageOutcome[2] = StageStatus.Skipped;
                UvtLog.Info("[Pipeline] Weld UV0 stage SKIPPED by user toggle");
            }

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

            int bestRejected = int.MaxValue;
            float bestCoverage = 0f;
            int bestConfigIdx = 0;
            var bestMeshes = new Dictionary<MeshEntry, Mesh>();
            var bestTransfers = new Dictionary<MeshEntry, (Mesh transferred, GroupedShellTransfer.TransferResult tr)>();

            bool cancelled = false;
            UvProgress.Begin("Auto-tune Pipeline", cancelable: true);
            try
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
                        stageOutcome[5] = stageRunTransfer ? StageStatus.Skipped : StageStatus.Skipped;
                        if (!stageRunTransfer)
                            UvtLog.Info("[Pipeline] Transfer stage SKIPPED by user toggle");
                    }
                    else
                    {
                        stageOutcome[5] = StageStatus.Skipped;
                    }

                    if (!hasTransferTargets)
                        break;

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
                if (cancelled) UvProgress.Cancel(); else UvProgress.End();
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
                return false;
            }
            if (separationConfigs.Length > 1 && bestConfigIdx > 0)
                UvtLog.Info($"[Pipeline] Auto-tune: selected config #{bestConfigIdx} " +
                    $"(sep={separationConfigs[bestConfigIdx]:P0})");

            UvtLog.Info("[Pipeline] Complete.");
            requestRepaint?.Invoke();
            return true;
        }

        // Sync entry — used by sweep / auto-tune internal loops which are
        // already on the main thread and have their own outer progress scope.
        // Editor freezes for the pack duration (acceptable for dev tools).
        void ExecRepack(List<MeshEntry> entries) => ExecRepackImpl(entries, useAsync: false).GetAwaiter().GetResult();

        // Async entry — button-click path. Editor main thread is free during
        // xatlas pack so the inline progress strip keeps repainting and Unity
        // never shows the "Hold on / Waiting for Unity's code…" busy dialog.
        Task ExecRepackAsync(List<MeshEntry> entries) => ExecRepackImpl(entries, useAsync: true);

        async Task ExecRepackImpl(List<MeshEntry> entries, bool useAsync)
        {
            if (entries.Count == 0) return;
            using var _bench = BenchmarkRecorder.NewRun(ctx, "Repack",
                splitTargetsInSymmetryStep, symSplitThresholdMode);
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

        void ExecRepackCore(List<MeshEntry> entries) => ExecRepackCoreImpl(entries, useAsync: false).GetAwaiter().GetResult();

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

            var results = useAsync
                ? await XatlasRepack.RepackMultiAsync(meshCopies.ToArray(), opts)
                : XatlasRepack.RepackMulti(meshCopies.ToArray(), opts);
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
            requestRepaint?.Invoke();
        }

        void ExecRepackPerMesh(List<MeshEntry> entries) => ExecRepackPerMeshImpl(entries, useAsync: false).GetAwaiter().GetResult();
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

        void ExecTransferAll() => ExecTransferAllImpl(useAsync: false).GetAwaiter().GetResult();
        Task ExecTransferAllAsync() => ExecTransferAllImpl(useAsync: true);

        async Task ExecTransferAllImpl(bool useAsync)
        {
            using var _bench = BenchmarkRecorder.NewRun(ctx, "TransferAll",
                splitTargetsInSymmetryStep, symSplitThresholdMode);
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
                    requestRepaint?.Invoke();
                    return;
                }

                crossLodHints.Clear();
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
                ctx.HasTransfer = !UvProgress.CancelRequested;
                completedSuccessfully = !UvProgress.CancelRequested;
                requestRepaint?.Invoke();
            }
            finally
            {
                BenchmarkRecorder.Current?.StageEnd("transfer");
                // Skip RecordMesh entirely on user-cancel / early abort so
                // we don't taint sweep aggregates with prior-run state.
                if (completedSuccessfully && ownsSession && BenchmarkRecorder.Current != null)
                    foreach (var e in ctx.MeshEntries)
                    {
                        if (!e.include) continue;
                        BenchmarkRecorder.Current.RecordMesh(e);
                    }
                if (ownsProgress)
                {
                    if (UvProgress.CancelRequested) UvProgress.Cancel();
                    else UvProgress.End();
                }
            }
        }

        void ExecTransferLod(int tLod) => ExecTransferLodImpl(tLod, useAsync: false).GetAwaiter().GetResult();
        Task ExecTransferLodAsync(int tLod) => ExecTransferLodImpl(tLod, useAsync: true);

        async Task ExecTransferLodImpl(int tLod, bool useAsync)
        {
            var targets = ctx.ForLod(tLod);
            if (targets.Count == 0) return;
            var sources = ctx.ForLod(ctx.SourceLodIndex);
            if (sources.Count == 0) return;

            foreach (var tgt in targets)
            {
                if (UvProgress.CancelRequested) break;
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

                string meshGroupKey = tgt.meshGroupKey ?? tgt.renderer.name;
                var hintKey = (source: srcEntry, meshGroupKey: meshGroupKey);
                if (!crossLodHints.TryGetValue(hintKey, out var hintState))
                {
                    hintState = new CrossLodHintState();
                    crossLodHints.Add(hintKey, hintState);
                }

                int srcId = srcMesh.GetInstanceID();
                if (!shellTransformCache.TryGetValue(srcId, out var srcInfos))
                {
                    srcInfos = GroupedShellTransfer.AnalyzeSource(srcMesh);
                    if (srcInfos != null) shellTransformCache[srcId] = srcInfos;
                }
                if (srcInfos == null) continue;

                UvProgress.Report(-1f, $"Transfer LOD{tLod} ← '{tgt.renderer.name}'");
                var tr = useAsync
                    ? await GroupedShellTransfer.TransferAsync(tgtMesh, srcMesh,
                        hintState.overlapHints.Count > 0 ? hintState.overlapHints : null,
                        hintState.matchHints.Count > 0 ? hintState.matchHints : null,
                        srcEntry.repackedAtlasWidth > 0 ? (int)srcEntry.repackedAtlasWidth : 0,
                        srcEntry.repackedAtlasHeight > 0 ? (int)srcEntry.repackedAtlasHeight : 0)
                    : GroupedShellTransfer.Transfer(tgtMesh, srcMesh,
                        hintState.overlapHints.Count > 0 ? hintState.overlapHints : null,
                        hintState.matchHints.Count > 0 ? hintState.matchHints : null,
                        srcEntry.repackedAtlasWidth > 0 ? (int)srcEntry.repackedAtlasWidth : 0,
                        srcEntry.repackedAtlasHeight > 0 ? (int)srcEntry.repackedAtlasHeight : 0);
                if (tr.uv2 == null) { UvtLog.Warn($"[Transfer] Failed for '{tgt.renderer.name}'"); continue; }

                // Accumulate overlap hints for subsequent LODs
                if (tr.overlapHints != null && tr.overlapHints.Count > 0)
                    hintState.overlapHints.AddRange(tr.overlapHints);
                // Replace match hints with this LOD's matches (latest LOD drives
                // next LOD's hint-guided matching; stale hints from older LODs
                // could conflict with changing geometry)
                hintState.matchHints.Clear();
                if (tr.matchHints != null && tr.matchHints.Count > 0)
                    hintState.matchHints.AddRange(tr.matchHints);

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

            var ch = VertexColorBakingTool.LastAppliedTargetChannel;
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
            // AO is written into selected UV component by VertexColorBakingTool.ApplyToMesh,
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

                // TBN: keep tangent presence in sync with the source FBX. If the FBX
                // import did not produce tangents, do not let derived/welded meshes
                // smuggle a synthesized tangent stream into the sidecar payload.
                // When tangents are present, validate the W (handedness) component.
                TangentValidator.EnforceTangentsMatchOriginal(sidecarMesh, entry.fbxMesh, "Sidecar");

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
                var colors = sidecarMesh.colors32;
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
                    optimizedColors = colors.Length == sidecarMesh.vertexCount ? colors : null,
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

        public void ExportFbxPublic(bool overwriteSource) => ExportFbx(overwriteSource, FbxExportIntent.All);
        public void ExportFbxPublic(bool overwriteSource, FbxExportIntent intent) => ExportFbx(overwriteSource, intent);
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

            string sourceFbxPath = ResolveFbxPath();
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

            ExportVertexColorsToFbxCore(sourceFbxPath, ctx.MeshEntries);
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
#endif
        }

        // Hierarchy-mode entry point: export a specific FBX using a filtered
        // entry list. Caller (VertexColorBakingTool) owns user confirmation. The FBX
        // structure is preserved as-is (no LOD-style hierarchy normalization)
        // so unrelated submeshes / instanced refs are not mutated.
        public void ExportVertexColorsToFbx(string sourceFbxPath, IEnumerable<MeshEntry> entries, int uvChannelOverride = -1)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (string.IsNullOrEmpty(sourceFbxPath))
            {
                UvtLog.Error("[FBX Export] Missing source FBX path.");
                return;
            }
            var list = entries?.ToList();
            if (list == null || list.Count == 0)
            {
                UvtLog.Warn($"[FBX Export] No entries for '{sourceFbxPath}'.");
                return;
            }

            RestoreAllPreviews();
            ExportVertexColorsToFbxCore(sourceFbxPath, list, uvChannelOverride);
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
#endif
        }

        // Variant export: write painted meshes into a NEW FBX next to the
        // source (or any caller-chosen path) without mutating the source FBX
        // importer settings, scene mesh bindings, or working copies. Caller
        // (VariantExportPipeline) owns suffix validation and conflict policy.
        public bool ExportVertexColorsToFbxAs(
            string sourceFbxPath,
            string outputFbxPath,
            IEnumerable<MeshEntry> entries,
            int uvChannelOverride = -1)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (string.IsNullOrEmpty(sourceFbxPath) || string.IsNullOrEmpty(outputFbxPath))
            {
                UvtLog.Error("[FBX Export] Variant export needs both source and output paths.");
                return false;
            }
            var list = entries?.ToList();
            if (list == null || list.Count == 0)
            {
                UvtLog.Warn($"[FBX Export] No entries for variant export to '{outputFbxPath}'.");
                return false;
            }

            RestoreAllPreviews();
            // Vcolor shim never sets the Hierarchy bit: VariantExportPipeline
            // matches new-FBX sub-meshes to source-prefab MeshFilters by
            // sub-asset name, and hierarchy normalization (rename to
            // baseName_LOD{N}) would break that matching. The variant FBX
            // must mirror the source FBX's sub-mesh naming so prefab clones
            // can swap mesh refs cleanly.
            return ExportVertexColorsToFbxCore(
                sourceFbxPath, list,
                uvChannelOverride,
                outputFbxPathOverride: outputFbxPath);
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
            return false;
#endif
        }

        // Resolve the legacy vcolor flow's "AO target UV channel".
        // Used by the vcolor wrappers to fold their args into a
        // FbxExportIntent for the unified isolated-export core.
        static int ResolveLegacyAoUvChannel(int uvChannelOverride)
        {
            if (uvChannelOverride >= 0) return uvChannelOverride;
            var aoChannel = VertexColorBakingTool.LastAppliedTargetChannel;
            if (!aoChannel.HasValue) return -1;
            int ch = (int)aoChannel.Value;
            if (ch <= (int)AOTargetChannel.VertexColorA) return -1;
            return (ch - (int)AOTargetChannel.UV0_X) / 2;
        }

        // Legacy shim. The implementation has been folded into
        // ExportFbxIsolatedCore — this method only computes the
        // FbxExportIntent for vcolor + optional AO-UV and delegates.
        // Public wrappers (ExportVertexColorsToFbx*) keep their
        // signatures so external callers (VariantExportPipeline,
        // VertexColorBakingTool, UvPackHierarchyTool) are unaffected.
        bool ExportVertexColorsToFbxCore(
            string sourceFbxPath,
            IEnumerable<MeshEntry> entries,
            int uvChannelOverride = -1,
            string outputFbxPathOverride = null)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            var intent = FbxExportIntent.VertexColors;
            int aoUvIdx = ResolveLegacyAoUvChannel(uvChannelOverride);
            if (aoUvIdx >= 0 && aoUvIdx <= 7)
                intent |= (FbxExportIntent)(1 << aoUvIdx);
            return ExportFbxIsolatedCore(sourceFbxPath, entries, intent, outputFbxPathOverride);
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
            return false;
#endif
        }

        // ─────────────────────────────────────────────────────────────────
        // Isolated-channel export (per FbxExportIntent)
        //
        // Re-saves the source FBX overwriting only the per-vertex channels
        // listed in the intent. All other data — node names, hierarchy,
        // transforms, material assignments, untouched UV channels, vertex
        // colors, normals, tangents — is inherited from the source FBX
        // asset on disk via clone-and-snapshot. Use this entry point when
        // a tool changed exactly one aspect of the mesh (e.g. only UV2
        // from atlas pack) and must not collateral-mutate the rest.
        // ─────────────────────────────────────────────────────────────────

        sealed class IsolatedExportSnapshot
        {
            public int vertexCount;
            public Color32[] colors32;
            public Color[]   colors;
            public Vector3[] normals;
            public Vector4[] tangents;
            public readonly Vector2[][] uvs = new Vector2[8][];
        }

        static IsolatedExportSnapshot BuildIsolatedSnapshot(Mesh source, FbxExportIntent intent)
        {
            var snap = new IsolatedExportSnapshot { vertexCount = source.vertexCount };
            if ((intent & FbxExportIntent.VertexColors) != 0)
            {
                var c32 = source.colors32;
                if (c32 != null && c32.Length == source.vertexCount)
                    snap.colors32 = c32;
                else
                {
                    var c = source.colors;
                    if (c != null && c.Length == source.vertexCount)
                        snap.colors = c;
                }
            }
            if ((intent & FbxExportIntent.Normals) != 0)
            {
                var n = source.normals;
                if (n != null && n.Length == source.vertexCount)
                    snap.normals = n;
            }
            if ((intent & FbxExportIntent.Tangents) != 0)
            {
                var t = source.tangents;
                if (t != null && t.Length == source.vertexCount)
                    snap.tangents = t;
            }
            for (int ch = 0; ch < 8; ch++)
            {
                if (!intent.IncludesUv(ch)) continue;
                var list = new List<Vector2>();
                source.GetUVs(ch, list);
                if (list.Count == source.vertexCount)
                    snap.uvs[ch] = list.ToArray();
            }
            return snap;
        }

        static int CopyIsolatedSnapshotsToClone(
            GameObject tempRoot,
            Dictionary<string, IsolatedExportSnapshot> snapshots,
            List<Mesh> tempSink = null)
        {
            if (snapshots == null) return 0;
            int updated = 0;
            int visited = 0;
            int matched = 0;
            foreach (var cloneMf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (cloneMf == null || cloneMf.sharedMesh == null) continue;
                visited++;
                if (!snapshots.TryGetValue(cloneMf.sharedMesh.name, out var snap)) continue;
                matched++;

                if (snap.vertexCount != cloneMf.sharedMesh.vertexCount)
                {
                    UvtLog.Warn($"[FBX Export] Skip '{cloneMf.sharedMesh.name}': vertex-count mismatch " +
                        $"(authored={snap.vertexCount}, FBX clone={cloneMf.sharedMesh.vertexCount}). " +
                        "The source FBX re-imports at a different vertex count than the tool worked on — " +
                        "usually 'Generate Lightmap UVs' splitting vertices. Disable it on the model " +
                        "importer and re-run the tool.");
                    continue;
                }

                // Clone before mutating — never write into the live FBX
                // sub-asset shared by other scene MeshFilters.
                var cloneMesh = UnityEngine.Object.Instantiate(cloneMf.sharedMesh);
                cloneMesh.name = cloneMf.sharedMesh.name;
                // Temporary copy — destroyed with the rest of the export scratch
                // meshes once the FBX is written.
                tempSink?.Add(cloneMesh);

                if (snap.colors32 != null) { cloneMesh.colors32 = snap.colors32; updated++; }
                else if (snap.colors != null) { cloneMesh.colors = snap.colors; updated++; }
                if (snap.normals != null)  { cloneMesh.normals  = snap.normals;  updated++; }
                if (snap.tangents != null) { cloneMesh.tangents = snap.tangents; updated++; }
                for (int ch = 0; ch < 8; ch++)
                {
                    if (snap.uvs[ch] == null) continue;
                    if (snap.uvs[ch].Length != cloneMesh.vertexCount) continue;
                    cloneMesh.SetUVs(ch, snap.uvs[ch]);
                    updated++;
                }

                cloneMf.sharedMesh = cloneMesh;
            }
            UvtLog.Verbose($"[FBX Export] CopyIsolatedSnapshotsToClone: visited={visited}, matched={matched}, updates={updated}.");
            return updated;
        }

        // ─────────────────────────────────────────────────────────────────
        // Pre-export preflight — flags FBX-pipeline-checklist violations
        // on the cloned hierarchy before export. Soft by design: every
        // finding is logged via UvtLog.Warn, none block the export.
        // The export is still atomic (write-to-tmp + File.Replace), so a
        // logged violation that doesn't block here can be diagnosed and
        // re-fixed without ever leaving a corrupt FBX on disk.
        // ─────────────────────────────────────────────────────────────────

        static bool IsGenericMeshName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            switch (name)
            {
                case "Scene":
                case "Geometry":
                case "Default":
                case "Mesh":
                case "Combined Mesh":
                    return true;
                default:
                    return name.StartsWith("Combined Mesh", StringComparison.Ordinal);
            }
        }

        static bool IsPlaceholderMaterialName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            switch (name)
            {
                case "Lit":
                case "Default":
                case "Material":
                case "DefaultMaterial":
                case "Default-Material":
                case "No Name":
                    return true;
                default:
                    return false;
            }
        }

        static void RunPreflight(
            GameObject tempRoot,
            FbxExportIntent intent,
            Dictionary<string, IsolatedExportSnapshot> snapshots)
        {
            if (tempRoot == null) return;

            // §5.5 + §8: node + mesh names. Generic names (`Scene`,
            // `Geometry`) are flagged because Max FBX importer auto-resets
            // mesh attributes to `Scene` on round-trip — a name like that
            // is a strong signal the source went through a metadata-
            // stripping tool. Invalid characters break Addressables /
            // asset bundles / filesystem rules.
            int badNodeNames = 0;
            int badMeshNames = 0;
            foreach (var t in tempRoot.GetComponentsInChildren<Transform>(true))
            {
                if (string.IsNullOrEmpty(t.name) || MeshHygieneUtility.HasInvalidChars(t.name))
                    badNodeNames++;
            }
            foreach (var mf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = mf.sharedMesh;
                if (m != null && IsGenericMeshName(m.name)) badMeshNames++;
            }
            foreach (var smr in tempRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var m = smr.sharedMesh;
                if (m != null && IsGenericMeshName(m.name)) badMeshNames++;
            }
            if (badNodeNames > 0)
                UvtLog.Warn($"[FBX Preflight] {badNodeNames} node name(s) are empty or contain invalid characters (see §5.5/§8).");
            if (badMeshNames > 0)
                UvtLog.Warn($"[FBX Preflight] {badMeshNames} mesh(es) have a generic name (Scene/Geometry/Default/empty); see §5.5.");

            // §1.5: placeholder material names. Soft because they may be
            // intentional during an early authoring pass; warning surfaces
            // them so they don't ship.
            int placeholderMats = 0;
            foreach (var mr in tempRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = mr.sharedMaterials;
                if (mats == null) continue;
                foreach (var mat in mats)
                {
                    if (mat == null || IsPlaceholderMaterialName(mat.name))
                        placeholderMats++;
                }
            }
            if (placeholderMats > 0)
                UvtLog.Warn($"[FBX Preflight] {placeholderMats} placeholder material slot(s) (Lit/Default/null); see §1.5.");

            // §4.2: vertex colors RGBA outside [0,1]. Only checked when the
            // intent overwrites VertexColors — otherwise the channel comes
            // straight from the source FBX and is the source's problem.
            if ((intent & FbxExportIntent.VertexColors) != 0 && snapshots != null)
            {
                int outOfRangeMeshes = 0;
                foreach (var snap in snapshots.Values)
                {
                    bool hit = false;
                    var c = snap.colors;
                    if (c != null)
                    {
                        for (int i = 0; i < c.Length && !hit; i++)
                        {
                            var v = c[i];
                            if (v.r < 0f || v.r > 1f || v.g < 0f || v.g > 1f ||
                                v.b < 0f || v.b > 1f || v.a < 0f || v.a > 1f) hit = true;
                        }
                    }
                    // colors32 is byte-clamped by definition; nothing to check.
                    if (hit) outOfRangeMeshes++;
                }
                if (outOfRangeMeshes > 0)
                    UvtLog.Warn($"[FBX Preflight] {outOfRangeMeshes} mesh(es) have vertex colors outside [0,1]; see §4.2.");
            }

            // §7.8: negative-determinant accumulated scale. Unity reads
            // inverted normals as backface-culled — mesh appears
            // transparent from the front.
            int negScaleNodes = 0;
            foreach (var t in tempRoot.GetComponentsInChildren<Transform>(true))
            {
                var s = t.lossyScale;
                if (s.x * s.y * s.z < 0f) negScaleNodes++;
            }
            if (negScaleNodes > 0)
                UvtLog.Warn($"[FBX Preflight] {negScaleNodes} node(s) have negative-determinant accumulated scale (mesh will render transparent from front); see §7.8.");
        }

        /// <summary>
        /// Re-save the FBX at <paramref name="sourceFbxPath"/> overwriting
        /// only the per-vertex channels listed in <paramref name="intent"/>.
        /// Mesh names, hierarchy, transforms, material assignments, and all
        /// untouched per-vertex channels are preserved from the source FBX.
        /// </summary>
        /// <param name="sourceFbxPath">Project path to the FBX to overwrite.</param>
        // Narrow-intent group dispatcher for ExportFbx. One core call
        // per source FBX, reusing the standard "overwrite vs save-as"
        // dialog flow but routing the actual write through the safe
        // atomic core. Save-as without a project-relative path
        // gracefully degrades to the absolute path the user picked
        // (Unity's FBX exporter accepts both).
        void ExportNarrowIntentGroups(
            Dictionary<string, List<(MeshEntry entry, Mesh resultMesh)>> fbxGroups,
            FbxExportIntent intent,
            bool overwriteSource)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            int okCount = 0;
            int totalCount = 0;
            foreach (var kv in fbxGroups)
            {
                totalCount++;
                string sourceFbxPath = kv.Key;
                var entries = kv.Value.Select(p => p.entry).ToList();
                string outputFbxPath = null;

                if (overwriteSource)
                {
                    if (!EditorUtility.DisplayDialog(
                            "Overwrite Source FBX",
                            $"Re-save '{System.IO.Path.GetFileName(sourceFbxPath)}' with intent {intent}?\n\n" +
                            "Channels not in the intent are preserved from the source FBX. " +
                            "Atomic write — original is untouched if export fails.",
                            "Overwrite", "Cancel"))
                        continue;
                }
                else
                {
                    string dir = System.IO.Path.GetDirectoryName(sourceFbxPath);
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(sourceFbxPath);
                    string suffix = (intent & FbxExportIntent.AnyUv) != 0 ? "_uv" :
                                    (intent & FbxExportIntent.VertexColors) != 0 ? "_vcolor" :
                                    "_isolated";
                    string picked = EditorUtility.SaveFilePanel(
                        "Export FBX (isolated)", dir, baseName + suffix + ".fbx", "fbx");
                    if (string.IsNullOrEmpty(picked)) continue;
                    string dataPath = Application.dataPath;
                    if (picked.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                        outputFbxPath = "Assets" + picked.Substring(dataPath.Length);
                    else
                        outputFbxPath = picked;
                }

                RestoreAllPreviews();
                if (ExportFbxIsolatedCore(sourceFbxPath, entries, intent, outputFbxPath))
                    okCount++;
            }
            UvtLog.Info($"[FBX Export] Narrow-intent export: {okCount}/{totalCount} group(s) succeeded.");
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
#endif
        }

        /// <param name="entries">Mesh entries supplying source data. Matched
        /// against the FBX clone by sub-asset name.</param>
        /// <param name="intent">Channels the caller is allowed to write.
        /// <see cref="FbxExportIntent.None"/> is a no-op (logged + returns false).</param>
        public bool ExportIsolatedChannelsToFbx(
            string sourceFbxPath,
            IEnumerable<MeshEntry> entries,
            FbxExportIntent intent)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (intent == FbxExportIntent.None)
            {
                UvtLog.Warn("[FBX Export] ExportIsolatedChannelsToFbx called with FbxExportIntent.None — nothing to write.");
                return false;
            }
            if (string.IsNullOrEmpty(sourceFbxPath))
            {
                UvtLog.Error("[FBX Export] ExportIsolatedChannelsToFbx: missing source FBX path.");
                return false;
            }
            var list = entries?.ToList();
            if (list == null || list.Count == 0)
            {
                UvtLog.Warn($"[FBX Export] ExportIsolatedChannelsToFbx: no entries for '{sourceFbxPath}'.");
                return false;
            }
            RestoreAllPreviews();
            return ExportFbxIsolatedCore(sourceFbxPath, list, intent, outputFbxPathOverride: null);
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
            return false;
#endif
        }

        // Unified isolated-channel export core. EVERY in-tool FBX-write
        // path goes through here — there is no parallel "destructive"
        // pipeline. Hierarchy / Materials / Collision mutations are
        // expressed as wider <see cref="FbxExportIntent"/> bits, gated
        // inside this method. Adding a new caller-side ModelExporter.
        // ExportObjects invocation is a checklist violation (§12).
        bool ExportFbxIsolatedCore(
            string sourceFbxPath,
            IEnumerable<MeshEntry> entries,
            FbxExportIntent intent,
            string outputFbxPathOverride)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (string.IsNullOrEmpty(sourceFbxPath) || entries == null) return false;

            string targetFbxPath = string.IsNullOrEmpty(outputFbxPathOverride) ? sourceFbxPath : outputFbxPathOverride;
            bool isVariantExport = !string.IsNullOrEmpty(outputFbxPathOverride)
                && !string.Equals(outputFbxPathOverride, sourceFbxPath, StringComparison.OrdinalIgnoreCase);

            // Snapshot pre-export. Captures only fields covered by intent.
            // Phase 1 (importer prep) can trigger a reimport that resets the
            // shared FBX sub-asset buffers in place — keying snapshots by
            // sub-asset name lets us look up the original data after the
            // reimport, when the in-memory mesh is back to its on-disk state.
            var snapshots = new Dictionary<string, IsolatedExportSnapshot>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                if (e == null || !e.include) continue;
                Mesh sm = e.originalMesh ?? e.fbxMesh;
                if (sm == null || string.IsNullOrEmpty(sm.name)) continue;
                snapshots[sm.name] = BuildIsolatedSnapshot(sm, intent);
            }
            if (snapshots.Count == 0)
            {
                UvtLog.Warn($"[FBX Export] ExportIsolatedChannelsToFbx: no source meshes had data for intent {intent}.");
                return false;
            }

            // ── Phase 1: Prepare importer (single reimport, scoped to intent) ──
            ModelImporter srcImporter = null;
            bool madeReadable = false;
            if (!isVariantExport)
            {
                srcImporter = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
                bool needsReimport = false;
                if (srcImporter != null)
                {
                    // generateSecondaryUV writes Unity UV channel 1.
                    // Lock only when the intent overwrites that channel.
                    // Intentionally NOT restored in Phase 5: re-enabling it
                    // makes Unity regenerate channel 1 on the restore reimport
                    // and clobber the UV1 we just authored into the FBX. When
                    // the tool writes UV1, authored data must win, so the
                    // setting stays off.
                    if (intent.IncludesUv(1) && srcImporter.generateSecondaryUV)
                        { srcImporter.generateSecondaryUV = false; needsReimport = true; }

                    // NOTE: we deliberately do NOT disable weldVertices /
                    // meshCompression / meshOptimizationFlags here. The snapshot
                    // is captured from the tool's working mesh, which was built
                    // from the CURRENT import; the clone below is also loaded
                    // from the current import, so the two share a vertex layout.
                    // Disabling weld/optimization and reimporting would renumber
                    // the clone's vertices, desyncing it from the snapshot —
                    // CopyIsolatedSnapshotsToClone would then skip every mesh on
                    // a vertex-count mismatch and write nothing. The wide
                    // LOD-rebuild path never does this reimport either. And
                    // because those settings were previously restored right
                    // after export, the final re-imported FBX kept the user's
                    // original weld/optimization state regardless — so removing
                    // the reimport changes nothing about the exported result
                    // except that the authored data now actually lands.
                    if (!srcImporter.isReadable)
                        { srcImporter.isReadable = true; needsReimport = true; madeReadable = true; }
                    if (needsReimport)
                    {
                        Uv2AssetPostprocessor.bypassPaths.Add(sourceFbxPath);
                        srcImporter.SaveAndReimport();
                    }
                }
            }

            // ── Phase 2: Build export hierarchy ──
            var fbxAsset = AssetDatabase.LoadMainAssetAtPath(sourceFbxPath) as GameObject;
            if (fbxAsset == null)
            {
                UvtLog.Error($"[FBX Export] Cannot load FBX asset at '{sourceFbxPath}'.");
                return false;
            }

            // Clone the source FBX prefab as-is — preserves names, hierarchy,
            // transforms, materials, and every channel the intent does not
            // cover. CopyIsolatedSnapshotsToClone overwrites only the
            // intended channels on freshly cloned per-node meshes.
            var tempRoot = UnityEngine.Object.Instantiate(fbxAsset);
            tempRoot.name = fbxAsset.name;

            int updated = 0;
            bool exported = false;
            Dictionary<string, string> renameMap = null;
            // Mesh copies created while baking node transforms — destroyed after export.
            var bakedMeshes = new List<Mesh>();
            try
            {
                updated = CopyIsolatedSnapshotsToClone(tempRoot, snapshots, bakedMeshes);
                if (updated == 0 && (intent & (FbxExportIntent.Hierarchy | FbxExportIntent.Materials)) == 0)
                {
                    // Per-vertex-only intent with no matching meshes — nothing to write.
                    // Hierarchy / Materials intents are still meaningful with zero
                    // mesh updates (they restructure the FBX without per-vertex changes).
                    UvtLog.Warn($"[FBX Export] No matching meshes in clone for intent {intent}.");
                    return false;
                }

                // Hierarchy mutations — gated on Hierarchy bit. Renames children
                // to baseName_LOD{N}, resets root to identity, bakes collision
                // transforms into vertices. Returns oldName→newName map for
                // post-reimport scene relink.
                // bakedMeshes sink: NormalizeExportHierarchy creates a mesh copy per
                // node while baking collision transforms. Without the sink those
                // copies leak — DestroyTempMeshes(bakedMeshes) in the finally below
                // would clean up nothing.
                if ((intent & FbxExportIntent.Hierarchy) != 0)
                    renameMap = NormalizeExportHierarchy(tempRoot, bakedMeshes);

                // Material mutations — gated on Materials bit. PrepareCollisionMaterials
                // copies a real material onto _COL renderers (avoids stale "Lit"
                // default in the FBX). TrimMaterialArrays prunes sharedMaterials
                // to subMeshCount.
                if ((intent & FbxExportIntent.Materials) != 0)
                {
                    PrepareCollisionMaterials(tempRoot);
                    TrimMaterialArrays(tempRoot);
                }

                // Pre-export preflight — surfaces FBX-pipeline-checklist
                // violations on tempRoot before we commit to disk. Soft
                // by design (logged, never blocks): collateral mutations
                // we can't catch from snapshots show up here as warnings.
                RunPreflight(tempRoot, intent, snapshots);

                // ── Phase 3: Export FBX (atomic) ──
                // Write to <target>.tmp first, verify, then File.Replace
                // for atomic rename. If ModelExporter throws or writes a
                // zero-byte file, the source FBX on disk is untouched —
                // unlike direct overwrite, which leaves a corrupt FBX
                // and a stale .meta when the exporter mid-faults.
                string fullPath = System.IO.Path.GetFullPath(targetFbxPath);
                // Hash the full path so two FBX files with the same filename
                // (e.g. Assets/A/Chair.fbx and Assets/B/Chair.fbx) get distinct
                // backup names and never overwrite each other. unchecked cast
                // rather than Math.Abs — Math.Abs(int.MinValue) throws, and the
                // throw would land after the .meta backup has already begun.
                string pathHash = unchecked((uint)fullPath.GetHashCode()).ToString("X8");
                string metaBak = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    System.IO.Path.GetFileName(fullPath) + "." + pathHash + ".meta.bak");
                bool metaBackedUp = System.IO.File.Exists(fullPath + ".meta");
                if (metaBackedUp)
                    System.IO.File.Copy(fullPath + ".meta", metaBak, true);

                string tmpRelPath = targetFbxPath + ".tmp";
                string tmpAbsPath = System.IO.Path.GetFullPath(tmpRelPath);
                // Strip any leftover tmp from a prior crashed run.
                if (System.IO.File.Exists(tmpAbsPath))
                    System.IO.File.Delete(tmpAbsPath);

                var exportOptions = new UnityEditor.Formats.Fbx.Exporter.ExportModelOptions
                    { ExportFormat = UnityEditor.Formats.Fbx.Exporter.ExportFormat.Binary };

                // Signal the UV2 postprocessor to skip sidecar UV2 injection
                // on the reimport triggered by the rename below — otherwise
                // an isolated UV2 export would be immediately overwritten by
                // stale sidecar data.
                Uv2AssetPostprocessor.fbxOverwritePaths.Add(targetFbxPath);

                UnityEditor.Formats.Fbx.Exporter.ModelExporter.ExportObjects(
                    tmpRelPath, new UnityEngine.Object[] { tempRoot }, exportOptions);

                // Verify tmp file is sane before we commit.
                var tmpInfo = new System.IO.FileInfo(tmpAbsPath);
                if (!tmpInfo.Exists || tmpInfo.Length == 0)
                {
                    if (System.IO.File.Exists(tmpAbsPath))
                        System.IO.File.Delete(tmpAbsPath);
                    throw new System.IO.IOException(
                        $"FBX Exporter produced an empty/missing file at '{tmpRelPath}'.");
                }

                // Atomic commit. File.Replace requires the target to exist
                // (overwrite + backup). For a fresh write (variant export
                // to a new path), File.Move is used.
                if (System.IO.File.Exists(fullPath))
                {
                    string fbxBak = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        System.IO.Path.GetFileName(fullPath) + "." + pathHash + ".fbx.bak");
                    System.IO.File.Replace(tmpAbsPath, fullPath, fbxBak);
                    // Backup served its purpose (rollback window during
                    // the rename itself). The .meta backup is still our
                    // primary safety net for the import settings.
                    if (System.IO.File.Exists(fbxBak))
                        System.IO.File.Delete(fbxBak);
                }
                else
                {
                    System.IO.File.Move(tmpAbsPath, fullPath);
                }

                // ModelExporter may have generated a .meta for the .tmp
                // sidecar entry — strip it so AssetDatabase doesn't pick
                // up a ghost asset on the next refresh.
                string tmpMetaPath = tmpAbsPath + ".meta";
                if (System.IO.File.Exists(tmpMetaPath))
                    System.IO.File.Delete(tmpMetaPath);

                UvtLog.Info($"[FBX Export] Isolated channels {intent} ({updated} updates) -> {targetFbxPath}");
                exported = true;

                if (metaBackedUp && System.IO.File.Exists(metaBak))
                {
                    System.IO.File.Copy(metaBak, fullPath + ".meta", true);
                    System.IO.File.Delete(metaBak);
                }
            }
            catch (Exception ex)
            {
                Uv2AssetPostprocessor.fbxOverwritePaths.Remove(targetFbxPath);
                // Best-effort: drop a leftover .tmp so a retry isn't blocked
                // by the "Strip any leftover tmp" sweep above logging into
                // a misleading state.
                try
                {
                    string tmpAbsPath = System.IO.Path.GetFullPath(targetFbxPath + ".tmp");
                    if (System.IO.File.Exists(tmpAbsPath))
                        System.IO.File.Delete(tmpAbsPath);
                }
                catch { /* swallow — original error matters */ }
                UvtLog.Error("[FBX Export] Isolated channel export failed: " + ex);
                return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tempRoot);
                DestroyTempMeshes(bakedMeshes);
            }

            // ── Phase 4: Reimport + relink ──
            // Variant export skips scene relink — live scene must keep
            // showing source meshes; only the new FBX needs to be picked up.
            AssetDatabase.Refresh();
            if (!isVariantExport && ctx?.LodGroup != null)
            {
                // renameMap is non-null only when the intent included
                // Hierarchy and NormalizeExportHierarchy renamed nodes —
                // for narrow per-vertex intents we re-bind purely by
                // sub-asset name.
                RelinkSceneMeshReferences(sourceFbxPath,
                    renameMap != null && renameMap.Count > 0 ? renameMap : null,
                    ctx.LodGroup);
                ctx.Refresh(ctx.LodGroup);
            }

            // ── Phase 5: Restore importer settings + working copies ──
            // Only isReadable is restored: Phase 1 no longer touches weld /
            // compression / optimization, and generateSecondaryUV is left
            // disabled on purpose so the just-authored UV1 is not regenerated.
            if (!isVariantExport)
            {
                if (srcImporter != null && madeReadable)
                {
                    srcImporter.isReadable = false;
                    Uv2AssetPostprocessor.bypassPaths.Add(sourceFbxPath);
                    srcImporter.SaveAndReimport();
                }
                RestoreWorkingCopiesToScene();
            }
            return exported;
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
            return false;
#endif
        }

        string ResolveFbxPath()
        {
            string path = ctx.SourceFbxPath;
            if (!string.IsNullOrEmpty(path)) return path;
            foreach (var e in ctx.MeshEntries)
            {
                if (e.fbxMesh == null) continue;
                string p = AssetDatabase.GetAssetPath(e.fbxMesh);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        void PrepareCollisionMaterials(GameObject tempRoot)
        {
            Material colFallbackMat = null;
            foreach (var e in ctx.MeshEntries)
            {
                if (e.renderer != null && e.renderer.sharedMaterial != null &&
                    !CheckerTexturePreview.IsPreviewShader(e.renderer.sharedMaterial.shader.name))
                { colFallbackMat = e.renderer.sharedMaterial; break; }
            }
            foreach (var colMf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (colMf == null || colMf.sharedMesh == null) continue;
                if (!MeshHygieneUtility.IsCollisionNodeName(colMf.gameObject.name)) continue;
                var colMr = colMf.GetComponent<MeshRenderer>();
                if (colFallbackMat != null)
                {
                    if (colMr == null) colMr = colMf.gameObject.AddComponent<MeshRenderer>();
                    colMr.sharedMaterials = new[] { colFallbackMat };
                }
                else if (colMr != null)
                    UnityEngine.Object.DestroyImmediate(colMr);
            }
        }

        void TrimMaterialArrays(GameObject tempRoot)
        {
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
        }

        void RestoreWorkingCopiesToScene()
        {
            if (ctx?.MeshEntries == null) return;
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.meshFilter == null) continue;
                if (e.originalMesh != null && e.meshFilter.sharedMesh != e.originalMesh)
                    e.meshFilter.sharedMesh = e.originalMesh;
            }
        }

        void ExportFbx(bool overwriteSource) => ExportFbx(overwriteSource, FbxExportIntent.All);

        // ExportFbx with intent. Narrow intent (no Hierarchy and no
        // LodGroup bits) delegates per-group to ExportFbxIsolatedCore —
        // the safe atomic-write + preflight path. Wide intent (Hierarchy
        // or LodGroup set) keeps the LOD-rebuild pipeline below: mesh
        // replacement by name, stale-child pruning, NormalizeExport-
        // Hierarchy, collision injection from sidecar. Migrating the
        // wide path to atomic write is a follow-up — for now the LOD-
        // rebuild scenario keeps direct overwrite for backwards
        // compatibility with existing tooling that depends on its
        // sequencing.
        void ExportFbx(bool overwriteSource, FbxExportIntent intent)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (intent == FbxExportIntent.None)
            {
                UvtLog.Warn("[FBX Export] ExportFbx called with FbxExportIntent.None — nothing to write.");
                return;
            }
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

            // Narrow-intent fast path. When the caller is not asking for
            // hierarchy / LOD-chain mutations, every group is exported
            // through the safe core (atomic write, preflight, no
            // NormalizeExportHierarchy, no material trim, no collision
            // injection from sidecar). This is the path UV2 transfer /
            // UV pack / vertex color baking should take — it preserves
            // node names, transforms, materials and untouched per-vertex
            // channels byte-for-byte (modulo what Unity's FBX Exporter
            // itself rewrites at the FBX-document level).
            if ((intent & (FbxExportIntent.Hierarchy | FbxExportIntent.LodGroup)) == 0)
            {
                ExportNarrowIntentGroups(fbxGroups, intent, overwriteSource);
                return;
            }

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
                // Hash the full path so two FBX files with the same filename
                // (e.g. Assets/A/Chair.fbx and Assets/B/Chair.fbx) get distinct
                // backup names and never overwrite each other.
                string fullSourcePath = System.IO.Path.GetFullPath(sourceFbxPath);
                string fbxBakName = System.IO.Path.GetFileName(fullSourcePath) + "." +
                    unchecked((uint)fullSourcePath.GetHashCode()).ToString("X8");
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
                // lockForFbxOverwrite=true normalizes topology and scale (keepQuads,
                // useFileScale, globalScale=1.0) so the Setup → Repack → Transfer →
                // Export round-trip round-trips 1:1 metres and preserves quads.
                if (overwriteSource)
                    Uv2AssetPostprocessor.PrepareImportSettings(sourceFbxPath, force: true, lockForFbxOverwrite: true);

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

                // Temporary mesh copies built for this export group — export clones,
                // transform-baked copies and stripped collision meshes. They only ever
                // live on tempRoot, so they are destroyed once the export finished.
                var tempMeshes = new List<Mesh>();
                try
                {
                    var lastLodRendererTemplate = FindLastLodRenderer(entries);

                    // Build lookup: original mesh name -> export mesh
                    var meshReplacements = new Dictionary<string, Mesh>();
                    var meshRendererTemplates = new Dictionary<string, Renderer>();
                    foreach (var (entry, resultMesh) in entries)
                    {
                        string meshName = ResolveExportMeshName(entry, resultMesh);
                        var exportMesh = UnityEngine.Object.Instantiate(resultMesh);
                        // Temporary copy — destroyed after the FBX export.
                        tempMeshes.Add(exportMesh);
                        // Without an explicit name, Object.Instantiate produces "X(Clone)"
                        // and Unity's FBX Exporter falls back to the FBX scene name
                        // ("Scene") when writing the FbxMesh node — every reimported mesh
                        // ends up named "Scene". Pin the canonical name now so the FBX
                        // node and post-reimport mesh asset stay aligned with the source.
                        exportMesh.name = meshName;
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
                        // Keep exported tangents consistent with the source mesh
                        // (from main's TangentValidator). meshName is already
                        // resolved at the top of this loop body.
                        TangentValidator.EnforceTangentsMatchOriginal(exportMesh, entry.fbxMesh, "FBX Export");
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
                        // Temporary copy — destroyed after the FBX export.
                        tempMeshes.Add(exportMesh);
                        // See the matching note in the replace-existing branch above:
                        // empty/Clone names cause Unity FBX Exporter to write "Scene".
                        exportMesh.name = meshName;
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
                        TangentValidator.EnforceTangentsMatchOriginal(exportMesh, entry.fbxMesh, "FBX Export");
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
                    var nodeRenameMap = NormalizeExportHierarchy(tempRoot, tempMeshes);
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
                        // GetCollisionMeshesFromSidecar builds every one of these from the
                        // sidecar's serialized arrays — they are never FBX sub-assets, and
                        // the caller owns them. Destroyed after the export.
                        tempMeshes.AddRange(colMeshes);
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
                            // Owns copies of srcCol's data (SetVertices/SetTriangles copy),
                            // so it can be destroyed after the export without touching srcCol.
                            var stripped = new Mesh { name = srcCol.name };
                            tempMeshes.Add(stripped);
                            stripped.SetVertices(srcCol.vertices);
                            for (int s = 0; s < srcCol.subMeshCount; s++)
                                stripped.SetTriangles(srcCol.GetTriangles(s), s);
                            stripped.RecalculateNormals();
                            // Only synthesize tangents when the source actually had them.
                            // Otherwise downstream tooling sees added TBN data that did
                            // not exist in the original FBX import.
                            if (TangentValidator.HasTangents(srcCol))
                            {
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
                                TangentValidator.ValidateTangentsW(tangents, stripped.name, "FBX Export (collision)");
                            }
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
                finally
                {
                    UnityEngine.Object.DestroyImmediate(tempRoot);
                    DestroyTempMeshes(tempMeshes);
                }

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

                // Re-apply UV2-friendly importer flags after the .meta backup is
                // restored above. The backup was captured BEFORE the pre-export
                // lock at line 1749, so it carries the user's original settings —
                // potentially keepQuads=false, globalScale=0.01, generateSecondaryUV=true,
                // etc. Restoring it as-is would undo every flag we set and break
                // triangulation/scale on the next import. peek mode lets us skip
                // the second SaveAndReimport when nothing actually drifted.
                if (overwriteSource && groupSucceeded &&
                    Uv2AssetPostprocessor.PrepareImportSettings(sourceFbxPath, force: true, peek: true, lockForFbxOverwrite: true))
                {
                    if (!persistentSidecarMode)
                        ArmTransientReplayForOverwrite(sourceFbxPath, transientReplayEntriesByPath);
                    Uv2AssetPostprocessor.PrepareImportSettings(sourceFbxPath, force: true, lockForFbxOverwrite: true);
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

        internal static void CopyRendererSettings(Renderer src, Renderer dst)
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
        /// <paramref name="bakedMeshSink"/> is optional — when provided, every mesh copy
        /// created here is appended to it so the caller can destroy the copies once the
        /// FBX export has finished (see <see cref="DestroyTempMeshes"/>).
        /// </summary>
        static Dictionary<string, string> NormalizeExportHierarchy(
            GameObject root,
            List<Mesh> bakedMeshSink = null)
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

            // Normalize direct child LOD names to contiguous _LOD0.._LODN suffixes
            // PER GROUP. Group key = the child's own prefix before "_LOD<N>" — so
            // hierarchies with multiple LOD chains under one root (e.g.
            // <Root>/<A>_LOD0..2 + <Root>/<B>_LOD0..2) keep their distinct
            // prefixes instead of being collapsed into one <Root>_LOD0..N chain.
            // Prevents importer warnings ("_LOD1 found but no _LOD0") when source
            // names contained invalid characters (e.g. dots) and were sanitized
            // inconsistently across tools.
            var groupedLodChildren = new Dictionary<string, List<(Transform transform, int index, int siblingIndex)>>();
            var groupOrder = new List<string>();
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
                    @"^(.+)_LOD(\d+)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success)
                    continue;

                int parsedIndex;
                if (!int.TryParse(match.Groups[2].Value, out parsedIndex))
                    continue;

                string groupPrefix = match.Groups[1].Value;
                if (!groupedLodChildren.TryGetValue(groupPrefix, out var list))
                {
                    list = new List<(Transform, int, int)>();
                    groupedLodChildren[groupPrefix] = list;
                    groupOrder.Add(groupPrefix);
                }
                list.Add((child, parsedIndex, child.GetSiblingIndex()));
            }

            foreach (var groupPrefix in groupOrder)
            {
                var list = groupedLodChildren[groupPrefix];
                list.Sort((a, b) =>
                {
                    int cmp = a.index.CompareTo(b.index);
                    return cmp != 0 ? cmp : a.siblingIndex.CompareTo(b.siblingIndex);
                });

                for (int i = 0; i < list.Count; i++)
                {
                    string normalizedName = groupPrefix + "_LOD" + i;
                    string oldName = list[i].transform.name;
                    if (oldName != normalizedName)
                    {
                        list[i].transform.name = normalizedName;
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

                // A cloned FBX hierarchy can contain several nodes that instance
                // the same Mesh. Baking into sharedMesh directly would apply each
                // node's transform cumulatively to that one object. Give every
                // transformed node its own copy before mutating the vertex data.
                var bakedMesh = UnityEngine.Object.Instantiate(mesh);
                bakedMesh.name = mesh.name;
                childMf.sharedMesh = bakedMesh;
                // Temporary copy — the caller destroys it after the FBX export.
                if (bakedMeshSink != null)
                    bakedMeshSink.Add(bakedMesh);

                BakeTransformIntoMesh(bakedMesh, t);

                // Reset transform to identity
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }

            return renameMap;
        }

        /// <summary>
        /// Destroys temporary mesh copies collected during export hierarchy setup.
        /// DestroyImmediate on the temp root only frees GameObjects, so the mesh
        /// copies have to be released explicitly. Call only AFTER the FBX export
        /// finished — the exporter reads vertex data straight from these meshes.
        /// </summary>
        static void DestroyTempMeshes(List<Mesh> meshes)
        {
            if (meshes == null) return;
            foreach (var mesh in meshes)
            {
                if (mesh != null)
                    UnityEngine.Object.DestroyImmediate(mesh);
            }
            meshes.Clear();
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

            UvProgress.Begin("Generate LODs", cancelable: true);
            try
            {
                for (int lodIdx = 0; lodIdx < generateLodCount; lodIdx++)
                {
                    if (UvProgress.CancelRequested) break;
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
                    UvProgress.Report(progress,
                        $"LOD {lodIdx + 1}/{generateLodCount} (ratio {ratio:P0})");

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
            finally { UvProgress.End(); }

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
                TangentValidator.EnforceTangentsMatchOriginal(m, e.fbxMesh, "SaveAll");
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
                e.repackedAtlasWidth = 0;
                e.repackedAtlasHeight = 0;
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
            VertexColorBakingTool.ActiveInstance?.RestorePreview();
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
            ctx.AtlasResolution = SanitizeAtlasResolution(s.atlasResolution);
            ctx.ShellPaddingPx = SanitizePadding(s.shellPaddingPx);
            ctx.BorderPaddingPx = SanitizePadding(s.borderPaddingPx);
            ctx.RepackPerMesh = s.repackPerMesh;
            symSplitThresholdMode = Enum.IsDefined(typeof(SymmetrySplitShells.ThresholdMode), s.symmetrySplitThresholdMode)
                ? (SymmetrySplitShells.ThresholdMode)s.symmetrySplitThresholdMode
                : SymmetrySplitShells.ThresholdMode.LegacyFixed;
            SymmetrySplitShells.CurrentThresholdMode = symSplitThresholdMode;
            ctx.SourceLodIndex = Mathf.Clamp(s.sourceLodIndex, 0, Mathf.Max(0, ctx.LodCount - 1));
            ctx.PipeSettings.saveNewMeshAssets = s.saveNewMeshAssets;
            if (IsSafeAssetFolderPath(s.savePath)) ctx.PipeSettings.savePath = s.savePath;
        }

        // The UI exposes atlas resolution as a free IntField, so the ceiling is
        // only here to keep a forged sidecar (or a typo) from turning into an
        // absurd xatlas allocation. It is deliberately far above the 4096 the
        // presets offer so manually typed values are never silently reduced.
        static int SanitizeAtlasResolution(int resolution) => Mathf.Clamp(resolution, 64, 16384);

        static int SanitizePadding(int padding) => Mathf.Clamp(padding, 0, 16);

        static bool IsSafeAssetFolderPath(string path)
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
                if (meshIndexCount == ulong.MaxValue)
                {
                    WarnHoverBudgetSkip(mesh);
                    continue;
                }

                var v = mesh.vertices;
                var tri = canvas.GetTrianglesCached(mesh);
                var uv = canvas.RdUvCached(mesh, ctx.PreviewUvChannel);
                if (v == null || tri == null || uv == null) continue;
                if (tri.Length / 3 > remainingTriangleBudget)
                {
                    WarnHoverBudgetSkip(mesh);
                    continue;
                }
                remainingTriangleBudget -= tri.Length / 3;
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

        static bool ToggleIssueBit(ref TransferValidator.TriIssue mask, TransferValidator.TriIssue bit, string label)
        {
            bool on = (mask & bit) != 0;
            bool newOn = EditorGUILayout.ToggleLeft(label, on);
            if (newOn == on) return false;
            if (newOn) mask |= bit;
            else       mask &= ~bit;
            return true;
        }

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
