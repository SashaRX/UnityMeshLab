using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    [MeshLabTool("foliage_normals", MeshLabLibraries.Geometry, MeshLabLibraries.Remesh, MeshLabLibraries.VertexChannels, MeshLabLibraries.Assets)]
    public sealed class FoliageNormalsTool : IUvTool, IUvTool3D, IUvToolFbxExport, IUvToolMeshExport
    {
        internal enum Preview
        {
            SourceCards, SelectionMask, PrimitiveSamples, SourceProxy, SmoothedProxy, TransferProxy, ProxyAndMesh, TransferredNormals, BeforeAfter
        }
        public string ToolName => "Foliage Normals";
        public string ToolId => "foliage_normals";
        public int ToolOrder => 36;
        public Action RequestRepaint
        {
            private get; set;
        }
        internal readonly FoliageNormalsPipeline pipeline = new FoliageNormalsPipeline();
        internal FoliageNormalsSettings settings = new FoliageNormalsSettings { sampleByCount = true, maximumPrimitiveCount = 2000 };
        internal GameObject root;
        internal Preview preview = Preview.SelectionMask;
        internal bool afterNormals = true, showNormalDirections;
        internal bool followSelection = true;
        internal readonly FoliageNormalsPreview sourcePreview = new FoliageNormalsPreview();
        GameObject lastSelection, pendingRoot;
        bool hasPendingRoot, pendingHierarchyRefresh;
        bool previewCaptured;
        GameObject previewCapturedRoot;
        float normalLength = .025f;
        internal int previewLod;
        bool advanced, showSphereEdges, active, assetWriteInProgress;
        internal bool showSamplePoints;
        public bool CanExportFbx => FoliageFbxExport.Available && CanApplyPreview;
        internal bool showProxyEdges = true, showProxySurface = true;
        internal float proxyOpacity = .25f;
        bool? previewCanApply;
        internal bool CanApplyPreview
        {
            get
            {
                if (pipeline.IsRunning || !pipeline.Has(FoliageNormalsPipeline.Stage.Transfer)) return false;
                if (!previewCanApply.HasValue) previewCanApply = pipeline.CanApply;
                return previewCanApply.Value;
            }
        }
        MeshRenderer[] availableRenderers = Array.Empty<MeshRenderer>();
        Material[] availableMaterials = Array.Empty<Material>();
        int submeshCount;
        const string Prefs = "MeshLab.FoliageNormals.Settings";

        public FoliageNormalsTool()
        {
            string json = EditorPrefs.GetString(Prefs, "");
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    settings = RestoreSettings(json);
                }
                catch (Exception e) { UvtLog.Warn("[Foliage] Saved settings invalid; using defaults. " + e.Message); }
            }
            // Scene references belong to the current selection, never to persisted instance IDs.
            settings.renderers = Array.Empty<MeshRenderer>();
            settings.materials = Array.Empty<Material>();
            settings.filterRenderers = false;
            pipeline.Changed = PipelineChanged;
        }

        void PipelineChanged()
        {
            previewCanApply = null;
            if (active) RefreshHierarchyWhenIdle();
            ValidateComparisonPreview();
            bool captured = pipeline.Has(FoliageNormalsPipeline.Stage.Capture);
            if (captured != previewCaptured || pipeline.CapturedRoot != previewCapturedRoot)
            {
                previewCaptured = captured;
                previewCapturedRoot = pipeline.CapturedRoot;
                if (active) RefreshSourcePreview();
            }
            if (active) RequestRepaint?.Invoke();
        }

        void ValidateComparisonPreview()
        {
            if (preview != Preview.BeforeAfter || pipeline.Has(FoliageNormalsPipeline.Stage.Transfer)) return;
            preview = Preview.TransferredNormals;
            afterNormals = false;
        }

        internal static FoliageNormalsSettings RestoreSettings(string json)
        {
            var restored = JsonUtility.FromJson<FoliageNormalsSettings>(json);
            if (json.IndexOf("\"remeshProxy\"", StringComparison.Ordinal) < 0)
            {
                // Migrate preferences saved before the Remesh stage. Explicitly saved
                // disabled settings still stay disabled, and invalid new values fail.
                var defaults = new FoliageNormalsSettings();
                restored.remeshProxy = defaults.remeshProxy;
                restored.remeshEdgeLength = defaults.remeshEdgeLength;
                restored.remeshIterations = defaults.remeshIterations;
                restored.remeshRelaxation = defaults.remeshRelaxation;
                restored.maximumProxyTriangles = defaults.maximumProxyTriangles;
            }
            if (json.IndexOf("\"automaticProxyTriangleBudget\"", StringComparison.Ordinal) < 0)
                restored.automaticProxyTriangleBudget = true;
            if (json.IndexOf("\"sampleRadiusVariation\"", StringComparison.Ordinal) < 0)
                restored.sampleRadiusVariation = 0; // retain uniform radii in existing preferences
            if (restored.automaticProxyTriangleBudget)
                restored.maximumProxyTriangles = Mathf.Clamp(restored.maximumProxyTriangles, 4, FoliageNormalsSettings.HardProxyTriangleCap);
            if (json.IndexOf("\"sampleByCount\"", StringComparison.Ordinal) < 0) restored.sampleByCount = true;
            restored.ClampSampleBudgets();
            restored.Validate();
            return restored;
        }

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            active = true;
            sourcePreview.Repaint = () => RequestRepaint?.Invoke();
            FollowSelection();
            RefreshSourcePreview();
            FollowSelectionChanges(FollowSelection, true);
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.hierarchyChanged += HierarchyChanged;
            EditorApplication.projectChanged += OnRefresh;
            SubscribeUndo(OnRefresh);
            SubscribeSourceChanges(SourceChannelsChanged);
            SubscribeTick(Tick);
        }
        public void OnDeactivate()
        {
            active = false;
            FollowSelectionChanges(FollowSelection, false);
            AssemblyReloadEvents.beforeAssemblyReload -= Clear;
            EditorApplication.hierarchyChanged -= HierarchyChanged;
            EditorApplication.projectChanged -= OnRefresh;
            UnsubscribeUndo(OnRefresh);
            UnsubscribeSourceChanges(SourceChannelsChanged);
            UnsubscribeTick(Tick);
            Clear();
        }
        static void SubscribeTick(EditorApplication.CallbackFunction tick)
        {
            EditorApplication.update += tick;
        }
        static void UnsubscribeTick(EditorApplication.CallbackFunction tick)
        {
            EditorApplication.update -= tick;
        }
        static void SubscribeSourceChanges(Action<Mesh> changed)
        {
            VertexChannels.Changed += changed;
        }
        static void UnsubscribeSourceChanges(Action<Mesh> changed)
        {
            VertexChannels.Changed -= changed;
        }
        static void SubscribeUndo(Undo.UndoRedoCallback refresh)
        {
            Undo.undoRedoPerformed += refresh;
        }
        static void UnsubscribeUndo(Undo.UndoRedoCallback refresh)
        {
            Undo.undoRedoPerformed -= refresh;
        }
        void Tick()
        {
            if (hasPendingRoot && !pipeline.IsRunning)
            {
                var next = pendingRoot;
                hasPendingRoot = false;
                pendingRoot = null;
                SetRoot(next);
            }
            if (pipeline.IsRunning)
            {
                RequestRepaint?.Invoke();
            }
        }
        void Clear()
        {
            previewCanApply = null;
            pendingHierarchyRefresh = false;
            hasPendingRoot = false;
            pendingRoot = null;
            sourcePreview.Dispose();
            EditorPrefs.SetString(Prefs, JsonUtility.ToJson(settings));
            pipeline.Dispose();
        }
        public void OnRefresh()
        {
            if (assetWriteInProgress) return;
            previewCanApply = null;
            if (!pipeline.IsRunning)
            {
                pipeline.InvalidateChanged(settings, root);
            }
            RefreshSourcePreview();
        }
        void HierarchyChanged()
        {
            previewCanApply = null;
            pendingHierarchyRefresh = true;
            RefreshHierarchyWhenIdle();
            RequestRepaint?.Invoke();
        }

        void RefreshHierarchyWhenIdle()
        {
            if (!pendingHierarchyRefresh || pipeline.IsRunning || assetWriteInProgress) return;
            pendingHierarchyRefresh = false;
            RefreshFilters();
            pipeline.InvalidateChanged(settings, root);
            RefreshSourcePreview();
        }

        void SourceChannelsChanged(Mesh mesh)
        {
            if (pipeline.Targets.Any(t => t.source == mesh || t.applied == mesh) ||
                availableRenderers.Any(r => r && FoliageLodSelection.SharedMesh(r) == mesh) ||
                sourcePreview.ContainsSource(mesh)) OnRefresh();
        }

        static void FollowSelectionChanges(Action handler, bool follow)
        {
            Selection.selectionChanged -= handler;
            if (follow) Selection.selectionChanged += handler;
        }

        internal void FollowSelection()
        {
            if (!followSelection) return;
            var selected = Selection.activeGameObject;
            if (selected == lastSelection) return;
            lastSelection = selected;
            hasPendingRoot = false;
            pendingRoot = null;
            if (!selected || EditorUtility.IsPersistent(selected)) return;
            var next = FoliageLodSelection.SelectionRoot(selected);
            if (!next) return;
            if (next == root) return;
            if (pipeline.IsRunning)
            {
                pendingRoot = next;
                hasPendingRoot = true;
                pipeline.Cancel();
                return;
            }
            SetRoot(next);
        }

        void RefreshSourcePreview() => sourcePreview.Refresh(root, settings, pipeline);

        internal void SetRoot(GameObject source)
        {
            if (pipeline.IsRunning)
            {
                return;
            }

            root = source;
            pendingHierarchyRefresh = false;
            settings.renderers = Array.Empty<MeshRenderer>();
            settings.materials = Array.Empty<Material>();
            settings.filterRenderers = false;
            previewLod = 0;
            preview = Preview.SelectionMask;
            pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture);
            RefreshFilters();
            RefreshSourcePreview();
            RequestRepaint?.Invoke();
        }

        void RefreshFilters()
        {
            availableRenderers = root ? root.GetComponentsInChildren<MeshRenderer>() : Array.Empty<MeshRenderer>();
            var materials = new List<Material>();
            submeshCount = 0;
            foreach (var r in availableRenderers)
            {
                foreach (var m in r.sharedMaterials.Where(m => m && !materials.Contains(m)))
                {
                    materials.Add(m);
                }

                var f = r.GetComponent<MeshFilter>();
                if (f && f.sharedMesh)
                {
                    submeshCount = Math.Max(submeshCount, f.sharedMesh.subMeshCount);
                }
            }
            availableMaterials = materials.ToArray();
        }

        public void OnDrawSidebar()
        {
            using (new EditorGUI.DisabledScope(pipeline.IsRunning))
            {
                EditorGUI.BeginChangeCheck();
                DrawSourceSettings();
                DrawProxySettings();
                DrawTransferSettings();
                if (EditorGUI.EndChangeCheck())
                {
                    pipeline.InvalidateChanged(settings, root);
                    RefreshSourcePreview();
                    EditorPrefs.SetString(Prefs, JsonUtility.ToJson(settings));
                }
                DrawOutput();
            }
            if (pipeline.IsRunning && GUILayout.Button("Cancel"))
            {
                pipeline.Cancel();
            }

            DrawPreviewSettings();
            if (!string.IsNullOrEmpty(sourcePreview.Error)) EditorGUILayout.HelpBox(sourcePreview.Error, MessageType.Warning);
            DrawDiagnostics();
            EditorGUILayout.LabelField(pipeline.Status, EditorStyles.wordWrappedMiniLabel);
        }

        void DrawPreviewSettings()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            preview = (Preview)EditorGUILayout.EnumPopup("Display", preview);
            DrawNormalComparison();
            DrawLodPreview();
            using (new EditorGUI.DisabledScope(pipeline.Samples == null))
                showSamplePoints = EditorGUILayout.Toggle("Show sample points", showSamplePoints);
            if (UsesScatterPreview) showSphereEdges = EditorGUILayout.Toggle("Show sphere edges", showSphereEdges);
            else showProxyEdges = EditorGUILayout.Toggle("Show proxy edges", showProxyEdges);
            if (CombinedPreview)
            {
                showProxySurface = EditorGUILayout.Toggle(UsesScatterPreview ? "Sphere surface" : "Proxy surface", showProxySurface);
                using (new EditorGUI.DisabledScope(!showProxySurface))
                    proxyOpacity = EditorGUILayout.Slider(UsesScatterPreview ? "Sphere opacity" : "Proxy opacity", proxyOpacity, 0, 1);
            }
            if (EditorGUI.EndChangeCheck()) RequestRepaint?.Invoke();
            EditorGUILayout.HelpBox("Selection Mask: green = proxy source, amber = transfer only in other LODs, grey = excluded. Primitive Samples overlays translucent spheres at the actual Scatter radius. Proxy And Mesh overlays the latest proxy, or Scatter spheres before Build Proxy, with optional edges. Before/After keeps the camera fixed; Side by side shows both. Normal directions controls arrows in every display, including Side by side. Normals shading is available in the 3D toolbar.", MessageType.Info);
        }

        void DrawLodPreview()
        {
            var levels = sourcePreview.Levels.Length > 0 ? sourcePreview.Levels : pipeline.LodLevels;
            if (levels.Length == 0) return;
            int index = Math.Max(0, Array.IndexOf(levels, previewLod));
            previewLod = levels[EditorGUILayout.Popup("Preview LOD", index, Array.ConvertAll(levels, level => "LOD" + level))];
            EditorGUILayout.LabelField($"{pipeline.Targets.Count} meshes in all LODs · {pipeline.CaptureSourceCount} proxy sources", EditorStyles.wordWrappedMiniLabel);
        }

        void DrawNormalComparison()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool inPlace = preview == Preview.TransferredNormals || preview == Preview.ProxyAndMesh;
                bool before = !afterNormals && inPlace;
                bool after = afterNormals && inPlace;
                if (GUILayout.Toggle(before, "Before", EditorStyles.miniButtonLeft) && !before)
                    SetNormalComparison(false);
                using (new EditorGUI.DisabledScope(!pipeline.Has(FoliageNormalsPipeline.Stage.Transfer)))
                {
                    if (GUILayout.Toggle(after, "After", EditorStyles.miniButtonMid) && !after)
                        SetNormalComparison(true);
                    if (GUILayout.Toggle(preview == Preview.BeforeAfter, "Side by side", EditorStyles.miniButtonRight) && preview != Preview.BeforeAfter)
                        preview = Preview.BeforeAfter;
                }
            }
            showNormalDirections = EditorGUILayout.Toggle("Normal directions", showNormalDirections);
            if (showNormalDirections) normalLength = EditorGUILayout.Slider("Normal length / diagonal", normalLength, .001f, .1f);
        }

        internal void SetNormalComparison(bool after)
        {
            afterNormals = after;
            if (preview != Preview.ProxyAndMesh) preview = Preview.TransferredNormals;
            RequestRepaint?.Invoke();
        }

        void DrawSourceSettings()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            bool follow = EditorGUILayout.Toggle("Follow selection", followSelection);
            if (follow != followSelection)
            {
                followSelection = follow;
                lastSelection = null;
                if (follow) FollowSelection();
            }
            var selectedRoot = (GameObject)EditorGUILayout.ObjectField("Root", root, typeof(GameObject), true);
            if (selectedRoot != root)
            {
                SetRoot(selectedRoot);
            }

            settings.lod0Only = EditorGUILayout.Toggle("LOD0 proxy source", settings.lod0Only);
            settings.selection = (FoliageFaceSelection)EditorGUILayout.EnumPopup("Foliage faces", settings.selection);
            DrawRendererSelection();
            DrawFaceSelection();
            EditorGUILayout.HelpBox("Normals are transferred to the selected mesh family in every LOD. The proxy uses the lowest available LOD when LOD0 proxy source is enabled. Selected submesh numbers follow their materials across LODs.", MessageType.Info);
            StageButton("Capture", FoliageNormalsPipeline.Stage.Capture);
            EditorGUILayout.Space();
        }

        void DrawRendererSelection()
        {
            bool useFilter = EditorGUILayout.Toggle("Choose renderers", settings.filterRenderers);
            if (useFilter && !settings.filterRenderers)
            {
                settings.renderers = (MeshRenderer[])availableRenderers.Clone();
            }

            settings.filterRenderers = useFilter;
            if (useFilter)
            {
                var selected = new List<MeshRenderer>(settings.renderers);
                foreach (var r in availableRenderers)
                {
                    ToggleList(selected, r, r.name);
                }

                settings.renderers = selected.ToArray();
                if (selected.Count == 0)
                {
                    EditorGUILayout.HelpBox("No renderers selected. Choose at least one before capture.", MessageType.Warning);
                }
            }
            else
            {
                settings.renderers = Array.Empty<MeshRenderer>();
            }
        }

        void DrawFaceSelection()
        {
            if (settings.selection == FoliageFaceSelection.SelectedMaterials)
            {
                var selected = new List<Material>(settings.materials);
                foreach (var m in availableMaterials)
                {
                    ToggleList(selected, m, m.name);
                }

                settings.materials = selected.ToArray();
            }
            if (settings.selection == FoliageFaceSelection.SelectedSubmeshIndices)
            {
                var selected = new List<int>(settings.submeshes);
                for (int i = 0; i < submeshCount; i++)
                {
                    ToggleList(selected, i, "Submesh " + i);
                }

                settings.submeshes = selected.ToArray();
            }
        }

        void DrawProxySettings()
        {
            EditorGUILayout.LabelField("Normal source", EditorStyles.boldLabel);
            settings.normalSource = (FoliageNormalSource)EditorGUILayout.EnumPopup("Proxy shape", settings.normalSource);
            if (settings.normalSource == FoliageNormalSource.VoxelEnvelope)
            {
                DrawVoxelSettings();
            }
            else
            {
                DrawShapeSettings();
            }
            EditorGUILayout.Space();
        }

        void DrawVoxelSettings()
        {
            DrawScatterSettings();
            DrawEnvelopeSettings();
            DrawSmoothSettings();
            DrawRemeshSettings();
        }

        void DrawScatterSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scatter", EditorStyles.boldLabel);
            settings.sampleByCount = EditorGUILayout.Toggle("By primitive count", settings.sampleByCount);
            settings.maximumPrimitiveCount = Mathf.Clamp(EditorGUILayout.DelayedIntField(
                settings.sampleByCount ? "Primitive count" : "Maximum primitives", settings.maximumPrimitiveCount), 1, FoliageNormalsSettings.HardPrimitiveCap);
            if (!settings.sampleByCount)
            {
                settings.relativeSpacing = EditorGUILayout.Toggle("Relative to bounds", settings.relativeSpacing);
                settings.sampleSpacing = EditorGUILayout.FloatField(settings.relativeSpacing ? "Spacing / diagonal" : "Spacing (capture units)", settings.sampleSpacing);
                settings.minimumSamplesPerIsland = EditorGUILayout.IntSlider("Minimum / island", settings.minimumSamplesPerIsland, 1, 100);
                settings.includeSourceVertices = EditorGUILayout.Toggle("Include vertices", settings.includeSourceVertices);
                settings.includeTriangleCentroids = EditorGUILayout.Toggle("Include centroids", settings.includeTriangleCentroids);
                settings.dedupSpacingFraction = EditorGUILayout.Slider("Dedup / spacing", settings.dedupSpacingFraction, .001f, 1);
            }
            else EditorGUILayout.LabelField("Exact count, distributed by surface area (1–50,000). Radius follows the resulting density.", EditorStyles.wordWrappedMiniLabel);
            settings.radiusMultiplier = EditorGUILayout.Slider("Sphere radius / spacing", settings.radiusMultiplier, .1f, 10);
            settings.sampleRadiusVariation = EditorGUILayout.Slider(new GUIContent("Radius variation", "Random radius spread around the base size. Zero keeps uniform spheres; Seed repeats the pattern."), settings.sampleRadiusVariation, 0, .95f);
            settings.deterministicSeed = EditorGUILayout.IntField("Seed", settings.deterministicSeed);
            StageButton("Scatter samples", FoliageNormalsPipeline.Stage.Scatter);
            if (pipeline.Samples != null)
            {
                int covered = pipeline.Samples.coveredIslands;
                EditorGUILayout.HelpBox($"Built {pipeline.Samples.samples.Length:N0} primitives · islands covered {covered:N0}/{pipeline.Samples.islands:N0}.",
                    covered < pipeline.Samples.islands ? MessageType.Warning : MessageType.Info);
            }
        }

        void DrawEnvelopeSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Envelope", EditorStyles.boldLabel);
            settings.voxelResolution = EditorGUILayout.IntSlider("Voxel resolution", settings.voxelResolution, 4, 256);
            settings.primitiveSubdivision = EditorGUILayout.IntSlider("Sphere subdivision", settings.primitiveSubdivision, 0, 1);
            settings.automaticProxyTriangleBudget = EditorGUILayout.Toggle("Automatic triangle budget", settings.automaticProxyTriangleBudget);
            if (settings.automaticProxyTriangleBudget)
                EditorGUILayout.LabelField($"Budget fits the proxy automatically, up to {FoliageNormalsSettings.HardProxyTriangleCap:N0} triangles. Voxel resolution is kept.", EditorStyles.wordWrappedMiniLabel);
            else
                settings.maximumProxyTriangles = Mathf.Clamp(EditorGUILayout.DelayedIntField("Proxy triangle budget", settings.maximumProxyTriangles), 4, FoliageNormalsSettings.HardProxyTriangleCap);
            StageButton("Build Proxy", FoliageNormalsPipeline.Stage.Envelope);
            DrawEnvelopeBudgetRecovery();
        }

        void DrawEnvelopeBudgetRecovery()
        {
            var failure = pipeline.EnvelopeBudgetExceeded;
            if (failure == null) return;
            EditorGUILayout.HelpBox(pipeline.Status, MessageType.Warning);
            if (failure.TriangleCount > FoliageNormalsSettings.HardProxyTriangleCap)
            {
                EditorGUILayout.HelpBox($"The proxy exceeds the {FoliageNormalsSettings.HardProxyTriangleCap:N0}-triangle hard cap. Lower Voxel resolution and rebuild.", MessageType.Warning);
                return;
            }
            if (GUILayout.Button($"Set budget to {failure.TriangleCount:N0} and rebuild") && pipeline.IncreaseEnvelopeBudget(settings, root))
            {
                GUI.changed = true;
                _ = Run(FoliageNormalsPipeline.Stage.Envelope);
            }
        }

        void DrawSmoothSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Smooth", EditorStyles.boldLabel);
            settings.smoothingIterations = EditorGUILayout.IntSlider("Iterations", settings.smoothingIterations, 0, 100);
            settings.smoothingStrength = EditorGUILayout.Slider("Strength", settings.smoothingStrength, 0, 1);
            advanced = EditorGUILayout.Foldout(advanced, "Taubin parameters", true);
            if (advanced)
            {
                settings.lambda = EditorGUILayout.FloatField("Lambda (+)", settings.lambda);
                settings.mu = EditorGUILayout.FloatField("Mu (−)", settings.mu);
            }
            StageButton("Smooth / rebuild", FoliageNormalsPipeline.Stage.Smooth);
        }

        void DrawRemeshSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Remesh Proxy", EditorStyles.boldLabel);
            settings.remeshProxy = EditorGUILayout.Toggle("Uniform triangles", settings.remeshProxy);
            using (new EditorGUI.DisabledScope(!settings.remeshProxy))
            {
                settings.remeshEdgeLength = EditorGUILayout.FloatField("Edge length (0 = auto)", settings.remeshEdgeLength);
                settings.remeshIterations = EditorGUILayout.IntSlider("Remesh iterations", settings.remeshIterations, 1, 20);
                settings.remeshRelaxation = EditorGUILayout.Slider("Tangential relaxation", settings.remeshRelaxation, 0, 1);
            }
            EditorGUILayout.HelpBox("Split, collapse and flip edges, then relax and project onto the smoothed envelope. Auto uses its mean edge length. Transfer uses this final proxy.", MessageType.Info);
            StageButton("Remesh / rebuild", FoliageNormalsPipeline.Stage.Remesh);
        }

        void DrawShapeSettings()
        {
            settings.autoFitProxy = EditorGUILayout.Toggle("Auto fit", settings.autoFitProxy);
            if (settings.autoFitProxy)
                settings.proxyFit = (FoliageProxyFit)EditorGUILayout.EnumPopup("Fit to", settings.proxyFit);
            settings.proxyCenter = EditorGUILayout.Vector3Field(settings.autoFitProxy ? "Center offset (root local)" : "Center (root local)", settings.proxyCenter);
            if (settings.autoFitProxy)
            {
                settings.proxyRadiusScale = EditorGUILayout.Slider("Size multiplier", settings.proxyRadiusScale, .01f, 4);
                if (settings.proxyFit == FoliageProxyFit.ObjectBoundingBox)
                    EditorGUILayout.HelpBox("Fits each axis to the full bounding box of the meshes used to build the proxy, including excluded submeshes. Other renderers and transfer-only LODs do not enlarge it. Transfer still changes only selected faces.", MessageType.Info);
            }
            else
            {
                settings.proxyRadius = EditorGUILayout.FloatField("Radius (capture units)", settings.proxyRadius);
            }

            if (settings.normalSource == FoliageNormalSource.Hemisphere)
            {
                settings.proxyRotation = EditorGUILayout.Vector3Field("Rotation (root local)", settings.proxyRotation);
                EditorGUILayout.HelpBox("Open hemisphere, no base cap. Default axis: root +Y. Auto fit places its base at the bottom of the chosen fit bounds along that axis.", MessageType.Info);
            }
            settings.proxySegments = (EditorGUILayout.IntSlider("Segments", settings.proxySegments, 16, 128) + 2) / 4 * 4;
            EditorGUILayout.HelpBox("Sphere and hemisphere use smooth surface normals and the same nearest-triangle transfer. Bounding-box fitting stretches the shape along each axis. Scatter, voxelization, smoothing and remeshing are skipped.", MessageType.Info);
            StageButton("Build Proxy", FoliageNormalsPipeline.Stage.Envelope);
        }

        void DrawTransferSettings()
        {
            EditorGUILayout.LabelField("Transfer", EditorStyles.boldLabel);
            settings.maxTransferDistance = EditorGUILayout.FloatField("Max distance (0 = auto)", settings.maxTransferDistance);
            settings.transferBlend = EditorGUILayout.Slider("Blend", settings.transferBlend, 0, 1);
            settings.alignToOriginalHemisphere = EditorGUILayout.Toggle("Align original hemisphere", settings.alignToOriginalHemisphere);
            settings.gpuProjection = EditorGUILayout.Toggle("GPU projection", settings.gpuProjection);
            settings.recalculateTangents = EditorGUILayout.Toggle("Recalculate tangents", settings.recalculateTangents);
            StageButton("Transfer normals", FoliageNormalsPipeline.Stage.Transfer);
        }

        void DrawOutput()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            if (GUILayout.Button("One Click: Build + Transfer"))
            {
                _ = Run(FoliageNormalsPipeline.Stage.Transfer);
            }

            using (new EditorGUI.DisabledScope(!CanApplyPreview))
            {
                if (pipeline.InPrefabMode)
                {
                    EditorGUILayout.HelpBox("Save a Mesh Asset or Separate FBX and assign the saved mesh in Prefab Mode.", MessageType.Info);
                }

                using (new EditorGUI.DisabledScope(pipeline.InPrefabMode))
                {
                    if (GUILayout.Button("Apply to Scene"))
                    {
                        ApplyToScene();
                    }
                }

                EditorGUILayout.LabelField("Save using the shared footer below.", EditorStyles.miniLabel);
            }
            if (!FoliageFbxExport.Available)
            {
                EditorGUILayout.HelpBox("Install Unity FBX Exporter (com.unity.formats.fbx) to save a separate FBX.", MessageType.Info);
            }

            if (GUILayout.Button("Clear Preview"))
            {
                pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture);
            }
        }

        static void ToggleList<T>(List<T> list, T value, string label)
        {
            bool had = list.Contains(value), use = EditorGUILayout.ToggleLeft(label, had);
            if (use && !had)
            {
                list.Add(value);
            }
            else if (!use && had)
            {
                list.Remove(value);
            }
        }
        void StageButton(string label, FoliageNormalsPipeline.Stage stage)
        {
            if (GUILayout.Button(label))
            {
                pipeline.ClearFrom(stage);
                _ = Run(stage);
            }
        }
        internal async Task Run(FoliageNormalsPipeline.Stage to)
        {
            var runRoot = root;
            if (await pipeline.Run(runRoot, settings, to) && runRoot == root && !hasPendingRoot)
            {
                switch (to)
                {
                    case FoliageNormalsPipeline.Stage.Capture:
                        preview = Preview.SelectionMask;
                        break;
                    case FoliageNormalsPipeline.Stage.Scatter:
                        preview = Preview.PrimitiveSamples;
                        break;
                    case FoliageNormalsPipeline.Stage.Envelope:
                        preview = Preview.SourceProxy;
                        break;
                    case FoliageNormalsPipeline.Stage.Smooth:
                        preview = Preview.SmoothedProxy;
                        break;
                    case FoliageNormalsPipeline.Stage.Remesh:
                        preview = Preview.TransferProxy;
                        break;
                    default:
                        preview = Preview.TransferredNormals;
                        afterNormals = true;
                        break;
                }
            }
            if (active)
            {
                RequestRepaint?.Invoke();
            }
        }
        internal void ApplyToScene()
        {
            try { pipeline.Apply(); }
            catch (Exception e)
            {
                previewCanApply = null;
                UvtLog.Error("[Foliage] " + e.Message);
            }
            RequestRepaint?.Invoke();
        }
        public bool CanSaveMeshAssets => CanApplyPreview;
        public void SaveMeshAssets() => Save();
        void Save()
        {
            string folder = EditorUtility.OpenFolderPanel("Save foliage meshes", Application.dataPath, "");
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            string path = FileUtil.GetProjectRelativePath(folder);
            try
            {
                var saved = pipeline.Save(path);
                UvtLog.Info("[Foliage] Saved " + string.Join(", ", saved));
            }
            catch (Exception e) { UvtLog.Error("[Foliage] " + e.Message); }
        }
        public void ExportFbx(bool overwriteSource)
        {
            if (!overwriteSource) { SaveFbx(); return; }
            assetWriteInProgress = true;
            try
            {
                var saved = pipeline.OverwriteFbx(sources => EditorUtility.DisplayDialog("Overwrite Source FBX",
                    "Write the current foliage normals and tangents into the selected submeshes in all captured LODs?\n\n" +
                    string.Join("\n", sources) + "\n\nOther FBX channels and hierarchy are preserved.", "Overwrite", "Cancel"));
                if (saved.Length > 0) UvtLog.Info("[Foliage] Overwritten FBX: " + string.Join(", ", saved));
            }
            catch (Exception e) { UvtLog.Error("[Foliage] " + e.Message); }
            finally
            {
                assetWriteInProgress = false;
                OnRefresh();
                RequestRepaint?.Invoke();
            }
        }

        void SaveFbx()
        {
            string folder = EditorUtility.OpenFolderPanel("Save separate foliage FBX", Application.dataPath, "");
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            try
            {
                var saved = pipeline.SaveFbx(FileUtil.GetProjectRelativePath(folder));
                UvtLog.Info("[Foliage] Saved separate FBX: " + string.Join(", ", saved));
            }
            catch (Exception e) { UvtLog.Error("[Foliage] " + e.Message); }
        }
        void DrawDiagnostics()
        {
            var s = pipeline.Samples;
            if (s != null)
            {
                EditorGUILayout.LabelField($"{s.faces:N0} selected faces · area {s.area:G5} · {s.islands:N0} islands\n" +
                $"{s.generated:N0} samples → {s.deduplicated:N0} dedup → {s.samples.Length:N0} primitives · {s.skippedDegenerate} degenerate\n" +
                $"Neighbour min/mean/max: {s.minNeighbour:G4}/{s.meanNeighbour:G4}/{s.maxNeighbour:G4}", EditorStyles.wordWrappedMiniLabel);
                if (pipeline.Primitives?.Length > 0)
                    EditorGUILayout.LabelField($"Base sphere radius: {s.spacing * settings.radiusMultiplier:G5} capture units · variation ±{settings.sampleRadiusVariation:P0} · Scatter preview: {pipeline.ScatterPreviewCount:N0} / {pipeline.Primitives.Length:N0} spheres", EditorStyles.wordWrappedMiniLabel);
                if (s.retainedForIslandMinimum > 0)
                    EditorGUILayout.LabelField($"{s.retainedForIslandMinimum:N0} close samples retained to preserve Minimum / island.", EditorStyles.wordWrappedMiniLabel);
            }

            if (pipeline.Envelope != null)
            {
                EditorGUILayout.LabelField($"{pipeline.Envelope.TriangleCount:N0} proxy triangles · {pipeline.ProxyComponents} components", EditorStyles.miniLabel);
            }

            if (pipeline.ShapeProxy != null)
            {
                EditorGUILayout.LabelField($"{pipeline.ProxyName}\nCenter {pipeline.ShapeProxy.center} · radii {pipeline.ShapeProxy.radii} · axis {pipeline.ShapeProxy.axis}", EditorStyles.wordWrappedMiniLabel);
            }

            if (pipeline.SmoothDiagnostics != null)
            {
                EditorGUILayout.LabelField($"Volume change: {pipeline.SmoothDiagnostics.VolumeChange:P1}", EditorStyles.miniLabel);
            }

            if (pipeline.RemeshDiagnostics != null)
            {
                EditorGUILayout.LabelField(pipeline.RemeshDiagnostics.ToString(), EditorStyles.wordWrappedMiniLabel);
            }

            if (pipeline.TransferDiagnostics != null)
            {
                EditorGUILayout.LabelField(pipeline.TransferDiagnostics.ToString(), EditorStyles.wordWrappedMiniLabel);
            }
        }

        bool CombinedPreview => preview == Preview.ProxyAndMesh || preview == Preview.PrimitiveSamples;
        bool UsesScatterPreview => preview == Preview.PrimitiveSamples ||
            preview == Preview.ProxyAndMesh && !pipeline.RemeshedMesh && !pipeline.SmoothedMesh && !pipeline.EnvelopeMesh;

        public bool Get3DContent(List<MeshViewport3D.Item> items)
        {
            ValidateComparisonPreview();
            var proxy = PreviewProxy();
            if (proxy && !CombinedPreview)
            {
                items.Add(new MeshViewport3D.Item(proxy, Matrix4x4.identity));
                return true;
            }
            foreach (var source in sourcePreview.Sources(previewLod))
            {
                if (preview == Preview.SelectionMask && source.mask)
                {
                    items.Add(new MeshViewport3D.Item(source.mask, source.matrix) { ShadingOverride = MeshViewport3D.Shading.VertexColors });
                    continue;
                }
                var target = pipeline.Targets.FirstOrDefault(t => t.renderer == source.renderer);
                AddTargetPreview(items, source, target);
            }
            if (CombinedPreview && proxy)
                items.Add(new MeshViewport3D.Item(proxy, Matrix4x4.identity) { WireOnly = true, SuppressWire = UsesScatterPreview ? !showSphereEdges : !showProxyEdges });
            return true;
        }

        Mesh PreviewProxy()
        {
            switch (preview)
            {
                case Preview.PrimitiveSamples: return pipeline.ScatterMesh;
                case Preview.SourceProxy: return pipeline.EnvelopeMesh;
                case Preview.SmoothedProxy: return pipeline.SmoothedMesh;
                case Preview.TransferProxy: return pipeline.RemeshedMesh;
                case Preview.ProxyAndMesh:
                    if (pipeline.RemeshedMesh) return pipeline.RemeshedMesh;
                    if (pipeline.SmoothedMesh) return pipeline.SmoothedMesh;
                    return pipeline.EnvelopeMesh ? pipeline.EnvelopeMesh : pipeline.ScatterMesh;
                default: return null;
            }
        }

        void AddTargetPreview(List<MeshViewport3D.Item> items, FoliageNormalsPreview.Source source, FoliageTargetSnapshot target)
        {
            var original = target?.original ? target.original : source.mesh;
            var result = target?.working ? target.working : original;
            float offset = ComparisonOffset();
            if (preview == Preview.BeforeAfter)
            {
                items.Add(new MeshViewport3D.Item(original, Matrix4x4.Translate(Vector3.left * offset) * source.matrix, source.materials));
                items.Add(new MeshViewport3D.Item(result, Matrix4x4.Translate(Vector3.right * offset) * source.matrix, source.materials));
            }
            else
            {
                bool after = (preview == Preview.TransferredNormals || preview == Preview.ProxyAndMesh) && afterNormals;
                items.Add(new MeshViewport3D.Item(after ? result : original, source.matrix, source.materials));
            }
        }

        public void OnDraw3D(MeshViewport3D view)
        {
            DrawCombinedProxySurface(view);
            if (showProxyEdges && !view.Wireframe && !CombinedPreview)
            {
                var proxy = PreviewProxy();
                if (proxy)
                {
                    view.DrawWire(proxy, Matrix4x4.identity, view.WireColor);
                }
            }
            if (showSamplePoints && pipeline.Samples != null)
            {
                DrawSamples(view);
            }
            if (!showNormalDirections)
            {
                return;
            }

            var before = new List<Vector3>();
            var after = new List<Vector3>();
            AppendPreviewNormalLines(before, after);
            view.DrawLines(before, Matrix4x4.identity, Color.cyan);
            view.DrawLines(after, Matrix4x4.identity, Color.yellow);
        }

        void DrawCombinedProxySurface(MeshViewport3D view)
        {
            if (!CombinedPreview || !showProxySurface || proxyOpacity <= 0) return;
            var proxy = PreviewProxy();
            if (proxy) view.DrawHighlightMesh(proxy, Matrix4x4.identity, new Color(.05f, .8f, 1f, Mathf.Clamp01(proxyOpacity)));
        }

        internal void AppendPreviewNormalLines(List<Vector3> before, List<Vector3> after)
        {
            float offset = preview == Preview.BeforeAfter ? ComparisonOffset() : 0;
            float length = (pipeline.Has(FoliageNormalsPipeline.Stage.Capture) ? pipeline.CaptureBounds : sourcePreview.Bounds(previewLod)).size.magnitude * normalLength;
            foreach (var source in sourcePreview.Sources(previewLod))
            {
                var t = pipeline.Targets.FirstOrDefault(target => target.renderer == source.renderer);
                if (t == null)
                {
                    if (!pipeline.Has(FoliageNormalsPipeline.Stage.Capture))
                        FoliageNormalsPreview.AppendSourceNormalLines(source, length, before);
                    continue;
                }
                AppendTargetNormalLines(t, offset, length, before, after);
            }
        }

        void AppendTargetNormalLines(FoliageTargetSnapshot target, float offset, float length, List<Vector3> before, List<Vector3> after)
        {
            bool resultNormals = afterNormals && (preview == Preview.TransferredNormals || preview == Preview.ProxyAndMesh);
            if (preview == Preview.BeforeAfter || !resultNormals)
                FoliageNormalsPreview.AppendNormalLines(target, false, Vector3.left * offset, length, before);
            if (preview == Preview.BeforeAfter || resultNormals)
                FoliageNormalsPreview.AppendNormalLines(target, true, Vector3.right * offset, length, after);
        }

        float ComparisonOffset()
        {
            var bounds = pipeline.CaptureBounds;
            if (sourcePreview.Sources(previewLod).Any()) bounds.Encapsulate(sourcePreview.Bounds(previewLod));
            var size = bounds.size;
            return (size.x > 0 ? size.x : size.magnitude) * .65f;
        }

        void DrawSamples(MeshViewport3D view)
        {
            var p = pipeline.Samples.samples;
            var subset = new List<Vector3>();
            int n = Math.Min(FoliagePrimitiveBuilder.MaximumPreviewPrimitives, p.Length);
            for (int i = 0; i < n; i++)
            {
                subset.Add(p[(int)((long)i * p.Length / n)]);
            }

            view.DrawPoints(subset, Matrix4x4.identity, Color.green, 4);
        }

        public void OnDrawToolbarExtra()
        {
        }
        public void OnDrawStatusBar() => GUILayout.Label($"{pipeline.RunningStage?.ToString() ?? "Foliage"} · {pipeline.NormalSource} · {pipeline.ElapsedSeconds:F1}s · {pipeline.Primitives?.Length ?? 0:N0} primitives · {(pipeline.Remeshed ?? pipeline.Smoothed ?? pipeline.Envelope)?.TriangleCount ?? 0:N0} proxy triangles / {pipeline.ProxyComponents} components · {pipeline.TransferDiagnostics?.transferred ?? 0:N0} transferred / {pipeline.TransferDiagnostics?.misses ?? 0:N0} missed");
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz)
        {
        }
        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes() => Array.Empty<UvCanvasView.FillModeEntry>();
        public void OnSceneGUI(SceneView sv)
        {
        }
    }
}
