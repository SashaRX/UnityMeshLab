// VertexAOTool.cs — Vertex AO baking tool (IUvTool tab).
// GPU: non-blocking async BVH ray tracing via compute shader.
// CPU: synchronous BVH ray tracing with Parallel.For.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

namespace LightmapUvTool
{
    public class VertexAOTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        Action requestRepaint;

        public string ToolName  => "Vertex AO";
        public string ToolId    => "vertex_ao";
        public int    ToolOrder => 50;

        public Action RequestRepaint { set => requestRepaint = value; }

        // ── Settings ──
        static readonly int[] sampleCounts    = { 64, 128, 256, 512 };
        static readonly string[] sampleLabels = { "64", "128", "256", "512" };
        static readonly int[] resolutions     = { 256, 512, 1024 };
        static readonly string[] resLabels    = { "256", "512", "1024" };
        static readonly string[] channelTypeNames = { "Vertex Color", "UV0", "UV1", "UV2", "UV3", "UV4" };
        static readonly string[] colorCompNames  = { "R", "G", "B", "A" };
        static readonly string[] uvCompNames     = { "X", "Y" };
        static readonly string[] occluderModeLabels = { "Self Only", "Same Root + Nearby" };

        int channelType = 0;  // 0=VertexColor, 1-5=UV0-UV4
        int channelComp = 0;  // 0=R/X, 1=G/Y, 2=B, 3=A

        int sampleCountIndex = 2;   // 256
        int resolutionIndex  = 1;   // 512
        float maxRadius      = 10f;
        float intensity      = 1.0f;
        bool  groundPlane    = true;
        float groundOffset   = 0.01f;
        AOTargetChannel TargetChannel =>
            channelType == 0
                ? (AOTargetChannel)channelComp
                : (AOTargetChannel)(4 + (channelType - 1) * 2 + channelComp);

        string TargetChannelName => channelTypeNames[channelType] + " " + (channelType == 0 ? colorCompNames : uvCompNames)[channelComp];

        // ── Post-processing ──
        // Topology blur
        int   topoBlurIter = 0;
        float topoBlurStr  = 0.5f;
        bool  topoCrossHardEdges = true;
        bool  topoCrossUvSeams   = true;
        // 3D Spatial blur
        int   spatialBlurIter = 0;
        float spatialBlurStr  = 0.5f;
        float spatialBlurRadius = 0.1f;
        // Face-area correction + levels
        float faceAreaStrength = 0f;
        float ppBrightness = 0f;
        float ppContrast   = 1f;
        // Bake settings
        bool  backfaceCulling = true;
        bool  cosineWeighted  = true;
        int   bakeMode = 0; // 0=GPU, 1=CPU
        int   bakeTypeIndex = 0; // 0=AO, 1=Thickness
        int   occluderModeIndex = (int)VertexAOOccluderMode.SameRootNearby;
        float occluderRadiusMultiplier = 2.0f;
        bool  includeCollisionOccluders;
        static readonly string[] bakeModeLabels = { "GPU", "CPU" };
        static readonly string[] bakeTypeLabels = { "Ambient Occlusion", "Thickness" };
        bool applySelectedRendererOnly;
        bool applySelectedSubmeshOnly;
        int selectedSubmeshIndex;

        // ── Results ──
        Dictionary<Mesh, float[]> bakedRawAO;       // raw vertex AO (no face-area fix)
        Dictionary<Mesh, float[]> bakedFaceAreaAO;  // face-area corrected AO
        Dictionary<Mesh, float[]> bakedFinalAO;     // after blend + blur + brightness/contrast
        float bakeTimeSeconds;
        int   bakedVertexCount;
        int   lastTargetMeshCount;
        int   lastRenderableOccluderCount;
        int   lastColliderOccluderCount;
        bool  lastUsedSidecarCollisionFallback;

        // ── Async GPU bake ──
        VertexAOBaker.GpuAOBakeJob activeGpuJob;
        List<LodBakeBatch> pendingLodBatches;
        int pendingLodBatchIndex;
        Dictionary<Mesh, float[]> pendingRawAO;
        Dictionary<Mesh, float[]> pendingFaceAreaAO;
        List<MeshEntry> pendingBakeEntries;
        VertexAOSettings pendingBakeSettings;
        Stopwatch bakeStopwatch;

        // ── Preview ──
        bool previewActive;
        List<(MeshFilter mf, Mesh originalMesh, Material[] originalMats)> previewBackups
            = new List<(MeshFilter, Mesh, Material[])>();
        Material previewMaterial;

        // ── Lifecycle ──

        internal static VertexAOTool ActiveInstance { get; private set; }
        internal static AOTargetChannel? LastAppliedTargetChannel { get; private set; }

        class LodBakeBatch
        {
            public int lodIndex;
            public List<MeshEntry> targetEntries = new List<MeshEntry>();
            public List<(Mesh mesh, Matrix4x4 transform)> targetMeshes = new List<(Mesh mesh, Matrix4x4 transform)>();
            public List<(Mesh mesh, Matrix4x4 transform)> occluderMeshes = new List<(Mesh mesh, Matrix4x4 transform)>();
            public List<Mesh> temporaryMeshesToDestroy = new List<Mesh>();
            public int renderableOccluderCount;
            public int colliderOccluderCount;
            public bool usedSidecarCollisionFallback;
        }

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            this.ctx = ctx;
            this.canvas = canvas;
            ActiveInstance = this;
        }

        public void OnDeactivate()
        {
            CancelGpuJob();
            RestorePreview();
            ClearResults();
        }

        public void OnRefresh()
        {
            CancelGpuJob();
            RestorePreview();
            ClearResults();
        }

        void ClearResults()
        {
            bakedRawAO = null;
            bakedFaceAreaAO = null;
            bakedFinalAO = null;
            bakedVertexCount = 0;
            lastTargetMeshCount = 0;
            lastRenderableOccluderCount = 0;
            lastColliderOccluderCount = 0;
            lastUsedSidecarCollisionFallback = false;
        }

        void CancelGpuJob()
        {
            if (activeGpuJob != null && activeGpuJob.IsRunning)
                activeGpuJob.Cancel();
            EditorApplication.update -= RepaintDuringBake;
            activeGpuJob = null;
            DisposePendingBatchTempMeshes();
            pendingLodBatches = null;
            pendingLodBatchIndex = 0;
            pendingRawAO = null;
            pendingFaceAreaAO = null;
            pendingBakeEntries = null;
            pendingBakeSettings = null;
            bakeStopwatch = null;
        }

        // ── UI ──

        public void OnDrawSidebar()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Vertex AO Baker", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (ctx == null || ctx.MeshEntries == null || ctx.MeshEntries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Select a LODGroup or an individual MeshRenderer to bake vertex AO.",
                    MessageType.Info);
                return;
            }

            // Bake mode selector
            bool gpuAvailable = SystemInfo.supportsComputeShaders;
            if (gpuAvailable)
            {
                bakeMode = EditorGUILayout.Popup(
                    new GUIContent("Bake Mode", "GPU: fast depth-map hemisphere sampling via compute shader.\nCPU: BVH ray tracing, slower but works on all platforms."),
                    bakeMode, bakeModeLabels);
            }
            else
            {
                bakeMode = 1; // force CPU
                EditorGUILayout.HelpBox(
                    "CPU mode (" + SystemInfo.graphicsDeviceType + " — no compute support). " +
                    "Switch to DX11/DX12/Vulkan/Metal for GPU acceleration.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(4);

            // Target channel — two combo boxes
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Target", "Channel to store AO values."));
            channelType = EditorGUILayout.Popup(channelType, channelTypeNames);
            var compNames = channelType == 0 ? colorCompNames : uvCompNames;
            if (channelComp >= compNames.Length) channelComp = 0;
            channelComp = EditorGUILayout.Popup(channelComp, compNames);
            EditorGUILayout.EndHorizontal();

            bakeTypeIndex = EditorGUILayout.Popup(
                new GUIContent("Bake Type", "Ambient Occlusion: how exposed each vertex is.\nThickness: how thin the mesh is (inverted normals, for SSS/translucency)."),
                bakeTypeIndex, bakeTypeLabels);

            EditorGUILayout.Space(4);

            // Bake settings
            sampleCountIndex = EditorGUILayout.Popup(
                new GUIContent("Sample Count", "Number of hemisphere directions to sample. Higher = smoother AO, slower bake."),
                sampleCountIndex, sampleLabels);
            if (gpuAvailable && bakeMode == 0)
            {
                resolutionIndex = EditorGUILayout.Popup(
                    new GUIContent("Resolution", "Depth map resolution per sample. Higher = sharper shadow edges. GPU only."),
                    resolutionIndex, resLabels);
            }
            maxRadius = EditorGUILayout.Slider(
                new GUIContent("Radius", "Maximum occlusion distance. Objects beyond this radius don't contribute to AO."),
                maxRadius, 0.1f, 100f);
            intensity = EditorGUILayout.Slider(
                new GUIContent("Intensity", "AO contrast. >1 = darker shadows, <1 = softer."),
                intensity, 0.5f, 3.0f);

            // Ground plane & backface culling (not relevant for thickness)
            if (bakeTypeIndex == 0)
            {
                EditorGUILayout.Space(4);
                groundPlane = EditorGUILayout.Toggle(
                    new GUIContent("Ground Plane", "Add virtual ground plane below object for bottom occlusion."),
                    groundPlane);
                if (groundPlane)
                {
                    EditorGUI.indentLevel++;
                    groundOffset = EditorGUILayout.FloatField(
                        new GUIContent("Offset", "Distance below mesh bounds minimum."),
                        groundOffset);
                    groundOffset = Mathf.Max(0, groundOffset);
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space(4);
                backfaceCulling = EditorGUILayout.Toggle(
                    new GUIContent("Backface Culling", "Ignore hits on back side of triangles. Reduces false occlusion on thin walls."),
                    backfaceCulling);
            }
            cosineWeighted = EditorGUILayout.Toggle(
                new GUIContent("Cosine Weighted", "Cosine: rays near normal contribute more (physically correct).\nUniform: all hemisphere directions contribute equally (harder shadows)."),
                cosineWeighted);

            if (bakeTypeIndex == 0)
            {
                EditorGUILayout.Space(4);
                occluderModeIndex = EditorGUILayout.Popup(
                    new GUIContent("Occluders", "Self Only: bake only against the selected target meshes.\nSame Root + Nearby: include nearby sibling geometry from the same model root as extra occluders."),
                    occluderModeIndex,
                    occluderModeLabels);

                if ((VertexAOOccluderMode)occluderModeIndex != VertexAOOccluderMode.SelfOnly)
                {
                    occluderRadiusMultiplier = EditorGUILayout.Slider(
                        new GUIContent("Occluder Radius", "Nearby occluders are accepted when their bounds are within this multiple of the target batch bounds radius."),
                        occluderRadiusMultiplier,
                        0.25f,
                        8f);
                    includeCollisionOccluders = EditorGUILayout.Toggle(
                        new GUIContent("Use collision meshes as occluders", "Include same-root collision meshes. Falls back to sidecar collision meshes when no live collision objects are present."),
                        includeCollisionOccluders);
                }
            }

            EditorGUILayout.Space(8);

            // Bake button / progress
            bool isBaking = activeGpuJob != null && activeGpuJob.IsRunning;
            var bgc = GUI.backgroundColor;

            if (isBaking)
            {
                // Inline progress bar + cancel
                var rect = EditorGUILayout.GetControlRect(false, 22);
                EditorGUI.ProgressBar(rect, activeGpuJob.Progress, activeGpuJob.StatusText);
                GUI.backgroundColor = new Color(.9f, .5f, .3f);
                if (GUILayout.Button("Cancel", GUILayout.Height(24)))
                {
                    CancelGpuJob();
                    UvtLog.Info("[Vertex AO] GPU bake cancelled.");
                }
                GUI.backgroundColor = bgc;
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                GUI.backgroundColor = new Color(.4f, .8f, .4f);
                if (GUILayout.Button("Bake Vertex AO", GUILayout.Height(28)))
                    ExecuteBake();
                GUI.backgroundColor = new Color(.6f, .75f, .9f);
                if (GUILayout.Button("Load from Mesh", GUILayout.Height(28)))
                    LoadFromMesh();
                GUI.backgroundColor = bgc;
                EditorGUILayout.EndHorizontal();
            }

            // Results
            if (bakedFinalAO != null && bakedFinalAO.Count > 0)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Results", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    $"  {bakedVertexCount:N0} vertices, {sampleCounts[sampleCountIndex]} samples, {bakeTimeSeconds:F1}s",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    $"  Target: {TargetChannelName}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    $"  Bake meshes: {lastTargetMeshCount}, nearby occluders: {lastRenderableOccluderCount}, collision occluders: {lastColliderOccluderCount}",
                    EditorStyles.miniLabel);
                if (lastUsedSidecarCollisionFallback)
                    EditorGUILayout.LabelField("  Collision source: sidecar fallback", EditorStyles.miniLabel);

                // Post-processing — all controls update preview in real-time
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Post-Processing", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();

                // Face-area correction blend
                faceAreaStrength = EditorGUILayout.Slider(
                    new GUIContent("Face-Area Fix", "Blend raw AO with face-area corrected. Fixes black polygons with open surfaces."),
                    faceAreaStrength, 0f, 1f);

                // Topology blur
                EditorGUILayout.Space(2);
                topoBlurIter = EditorGUILayout.IntSlider(
                    new GUIContent("Topo Blur", "Topology blur along mesh edges."),
                    topoBlurIter, 0, 10);
                if (topoBlurIter > 0)
                {
                    topoBlurStr = EditorGUILayout.Slider(
                        new GUIContent("  Strength", "Blend factor per iteration."),
                        topoBlurStr, 0f, 1f);
                    topoCrossHardEdges = EditorGUILayout.Toggle(
                        new GUIContent("  Cross Hard Edges", "Blur across hard edges."),
                        topoCrossHardEdges);
                    topoCrossUvSeams = EditorGUILayout.Toggle(
                        new GUIContent("  Cross UV Seams", "Blur across UV shell boundaries."),
                        topoCrossUvSeams);
                }

                // 3D Spatial blur
                EditorGUILayout.Space(2);
                spatialBlurIter = EditorGUILayout.IntSlider(
                    new GUIContent("3D Blur", "3D spatial blur — ignores topology, crosses all seams."),
                    spatialBlurIter, 0, 10);
                if (spatialBlurIter > 0)
                {
                    spatialBlurStr = EditorGUILayout.Slider(
                        new GUIContent("  Strength", "Blend factor per iteration."),
                        spatialBlurStr, 0f, 1f);
                    spatialBlurRadius = EditorGUILayout.Slider(
                        new GUIContent("  Radius", "3D search radius in world units."),
                        spatialBlurRadius, 0.01f, 2f);
                }

                // Levels
                EditorGUILayout.Space(2);
                ppBrightness = EditorGUILayout.Slider(
                    new GUIContent("Brightness", "Shift AO values. + lighter, - darker."),
                    ppBrightness, -1f, 1f);
                ppContrast = EditorGUILayout.Slider(
                    new GUIContent("Contrast", "AO contrast around 0.5. >1 = sharper, <1 = flatter."),
                    ppContrast, 0f, 3f);
                if (EditorGUI.EndChangeCheck())
                    ApplyBlur();

                EditorGUILayout.Space(8);

                applySelectedRendererOnly = EditorGUILayout.ToggleLeft(
                    new GUIContent("Apply only selected renderer", "Apply AO only to currently selected mesh renderer/skinned renderer."),
                    applySelectedRendererOnly);
                if (applySelectedRendererOnly)
                {
                    var selectedRenderer = Selection.activeGameObject != null
                        ? Selection.activeGameObject.GetComponentInParent<Renderer>()
                        : null;
                    int subCount = GetRendererSubmeshCount(selectedRenderer);
                    if (selectedRenderer == null || subCount <= 0)
                    {
                        EditorGUILayout.HelpBox("Select an object with MeshRenderer/SkinnedMeshRenderer for selective apply.", MessageType.Warning);
                    }
                    else
                    {
                        applySelectedSubmeshOnly = EditorGUILayout.ToggleLeft(
                            new GUIContent("Only selected submesh", "Apply AO only to vertices used by the chosen submesh. Other parts stay untouched."),
                            applySelectedSubmeshOnly);
                        if (applySelectedSubmeshOnly)
                        {
                            selectedSubmeshIndex = EditorGUILayout.IntSlider(
                                new GUIContent("Submesh Index"),
                                Mathf.Clamp(selectedSubmeshIndex, 0, subCount - 1),
                                0,
                                subCount - 1);
                        }
                    }
                }

                // Preview / Apply / Clear — three buttons
                EditorGUILayout.BeginHorizontal();

                // Preview toggle button (highlighted when active)
                var prevBg = GUI.backgroundColor;
                GUI.backgroundColor = previewActive ? new Color(.3f, .7f, 1f) : Color.white;
                if (GUILayout.Button(previewActive ? "Preview ON" : "Preview", GUILayout.Height(24)))
                {
                    if (previewActive) RestorePreview();
                    else ActivatePreview();
                }
                GUI.backgroundColor = prevBg;

                GUI.backgroundColor = new Color(.3f, .85f, .4f);
                if (GUILayout.Button("Apply to Mesh", GUILayout.Height(24)))
                    ApplyToMesh();
                GUI.backgroundColor = new Color(.9f, .3f, .3f);
                if (GUILayout.Button("Clear", GUILayout.Height(24)))
                {
                    RestorePreview();
                    ClearResults();
                    requestRepaint?.Invoke();
                }
                GUI.backgroundColor = bgc;
                EditorGUILayout.EndHorizontal();
            }
        }

        // ── Bake ──

        void ExecuteBake()
        {
            CancelGpuJob();
            RestorePreview();

            var entries = ctx.MeshEntries
                .Where(e => e.include && e.renderer != null)
                .ToList();

            if (entries.Count == 0)
            {
                UvtLog.Warn("[Vertex AO] No meshes found.");
                return;
            }

            var settings = new VertexAOSettings
            {
                sampleCount     = sampleCounts[sampleCountIndex],
                depthResolution = resolutions[resolutionIndex],
                maxRadius       = maxRadius,
                intensity       = intensity,
                groundPlane     = groundPlane,
                groundOffset    = groundOffset,
                backfaceCulling = backfaceCulling,
                cosineWeighted  = cosineWeighted,
                useGPU          = bakeMode == 0,
                bakeType        = (AOBakeType)bakeTypeIndex,
                occluderMode    = bakeTypeIndex == 0
                    ? (VertexAOOccluderMode)occluderModeIndex
                    : VertexAOOccluderMode.SelfOnly,
                occluderRadiusMultiplier = Mathf.Max(occluderRadiusMultiplier, 0.01f),
                includeCollisionOccluders = bakeTypeIndex == 0 && includeCollisionOccluders
            };

            var batches = BuildLodBatches(entries, settings);
            if (batches.Count == 0)
            {
                UvtLog.Warn("[Vertex AO] No valid meshes to bake.");
                return;
            }
            StoreBatchStats(batches);

            bakeStopwatch = Stopwatch.StartNew();

            if (settings.useGPU && SystemInfo.supportsComputeShaders)
            {
                pendingLodBatches = batches;
                pendingLodBatchIndex = 0;
                pendingRawAO = new Dictionary<Mesh, float[]>();
                pendingFaceAreaAO = new Dictionary<Mesh, float[]>();
                pendingBakeEntries = entries;
                pendingBakeSettings = settings;

                // Subscribe repaint pump so progress bar updates.
                EditorApplication.update += RepaintDuringBake;
                StartNextGpuLodBatch();
            }
            else
            {
                if (settings.useGPU && !SystemInfo.supportsComputeShaders)
                    UvtLog.Warn("[Vertex AO] Compute shaders not supported. Falling back to CPU.");
                ExecuteBakeCPU(batches, settings, entries);
            }
        }

        List<LodBakeBatch> BuildLodBatches(List<MeshEntry> entries, VertexAOSettings settings)
        {
            var batches = new List<LodBakeBatch>();

            foreach (var group in entries.GroupBy(e => e.lodIndex).OrderBy(g => g.Key))
            {
                var batch = new LodBakeBatch { lodIndex = group.Key };
                foreach (var entry in group)
                {
                    if (entry?.renderer == null)
                        continue;

                    Mesh mesh = entry.originalMesh ?? entry.fbxMesh;
                    if (mesh == null)
                        continue;

                    batch.targetEntries.Add(entry);
                    batch.targetMeshes.Add((mesh, entry.renderer.transform.localToWorldMatrix));
                }

                if (batch.targetMeshes.Count == 0)
                {
                    DisposeBatchTemporaryMeshes(batch);
                    continue;
                }

                CollectAdditionalOccluders(batch, entries, settings);
                batches.Add(batch);
            }

            return batches;
        }

        void CollectAdditionalOccluders(
            LodBakeBatch batch,
            List<MeshEntry> allEntries,
            VertexAOSettings settings)
        {
            if (batch == null || batch.targetEntries.Count == 0)
                return;
            if (settings.bakeType != AOBakeType.AmbientOcclusion)
                return;
            if (settings.occluderMode == VertexAOOccluderMode.SelfOnly)
                return;

            var root = ResolveOccluderRoot(batch.targetEntries);
            if (root == null)
                return;

            var candidateRenderers = root.GetComponentsInChildren<Renderer>(true);
            if (candidateRenderers == null || candidateRenderers.Length == 0)
                return;

            var targetAnchors = ComputeTargetAnchors(batch.targetEntries, out Bounds targetBounds);
            float effectiveRadius = Mathf.Max(targetBounds.extents.magnitude * settings.occluderRadiusMultiplier, 0.01f);

            var targetRendererIds = new HashSet<int>(
                batch.targetEntries
                    .Where(e => e.renderer != null)
                    .Select(e => e.renderer.GetInstanceID()));

            var alternateLodRendererIds = new HashSet<int>();
            if (ctx?.LodGroup != null)
            {
                foreach (var entry in allEntries)
                {
                    if (entry?.renderer == null || entry.lodIndex == batch.lodIndex)
                        continue;
                    alternateLodRendererIds.Add(entry.renderer.GetInstanceID());
                }
            }

            var seenRenderers = new HashSet<int>();
            foreach (var renderer in candidateRenderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                int rendererId = renderer.GetInstanceID();
                if (!seenRenderers.Add(rendererId))
                    continue;
                if (targetRendererIds.Contains(rendererId))
                    continue;
                if (alternateLodRendererIds.Contains(rendererId))
                    continue;
                if (MeshHygieneUtility.IsCollisionNodeName(renderer.name))
                    continue;
                if (!TryGetRendererOccluderData(renderer, out var mesh, out var matrix, out var bounds, out var anchor))
                    continue;
                if (!IsWithinOccluderRange(targetBounds, targetAnchors, bounds, anchor, effectiveRadius))
                    continue;

                batch.occluderMeshes.Add((mesh, matrix));
                batch.renderableOccluderCount++;
            }

            if (!settings.includeCollisionOccluders)
                return;

            int liveColliderCount = CollectLiveCollisionOccluders(batch, root, targetBounds, targetAnchors, effectiveRadius);
            if (liveColliderCount > 0 || string.IsNullOrEmpty(ctx?.SourceFbxPath))
                return;

            CollectSidecarCollisionOccluders(batch, candidateRenderers, targetBounds, targetAnchors, effectiveRadius);
        }

        GameObject ResolveOccluderRoot(List<MeshEntry> targetEntries)
        {
            if (ctx?.LodGroup != null)
                return ctx.LodGroup.gameObject;

            var firstRenderer = targetEntries.FirstOrDefault(e => e?.renderer != null)?.renderer;
            if (firstRenderer == null)
                return null;

            var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(firstRenderer.gameObject);
            return prefabRoot != null ? prefabRoot : firstRenderer.transform.root.gameObject;
        }

        int CollectLiveCollisionOccluders(
            LodBakeBatch batch,
            GameObject root,
            Bounds targetBounds,
            List<Vector3> targetAnchors,
            float effectiveRadius)
        {
            int added = 0;
            var seen = new HashSet<int>();
            foreach (var go in MeshHygieneUtility.FindCollisionObjects(root.transform))
            {
                if (go == null || !go.activeInHierarchy)
                    continue;
                if (!seen.Add(go.GetInstanceID()))
                    continue;
                if (!TryGetCollisionMesh(go, out var mesh, out var matrix, out var bounds, out var anchor))
                    continue;
                if (!IsWithinOccluderRange(targetBounds, targetAnchors, bounds, anchor, effectiveRadius))
                    continue;

                batch.occluderMeshes.Add((mesh, matrix));
                batch.colliderOccluderCount++;
                added++;
            }
            return added;
        }

        void CollectSidecarCollisionOccluders(
            LodBakeBatch batch,
            Renderer[] candidateRenderers,
            Bounds targetBounds,
            List<Vector3> targetAnchors,
            float effectiveRadius)
        {
            var sidecarEntries = CollisionMeshTool.GetCollisionMeshesFromSidecar(ctx.SourceFbxPath);
            if (sidecarEntries == null || sidecarEntries.Count == 0)
                return;

            var seenHullKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool addedAny = false;

            foreach (var (meshName, meshes, _) in sidecarEntries)
            {
                if (meshes == null || meshes.Count == 0)
                    continue;

                foreach (var mesh in meshes)
                {
                    if (mesh != null && !batch.temporaryMeshesToDestroy.Contains(mesh))
                        batch.temporaryMeshesToDestroy.Add(mesh);
                }

                var matchedRenderer = FindBestSidecarRenderer(meshName, candidateRenderers, batch);
                if (matchedRenderer == null)
                    continue;

                var matrix = matchedRenderer.transform.localToWorldMatrix;
                for (int i = 0; i < meshes.Count; i++)
                {
                    var mesh = meshes[i];
                    if (mesh == null)
                        continue;

                    string hullKey = $"{meshName}:{i}";
                    if (!seenHullKeys.Add(hullKey))
                        continue;

                    var bounds = TransformBounds(mesh.bounds, matrix);
                    var anchor = GetMeshWorldAnchor(mesh, matrix);
                    if (!IsWithinOccluderRange(targetBounds, targetAnchors, bounds, anchor, effectiveRadius))
                        continue;

                    batch.occluderMeshes.Add((mesh, matrix));
                    batch.colliderOccluderCount++;
                    addedAny = true;
                }
            }

            batch.usedSidecarCollisionFallback = addedAny;
        }

        Renderer FindBestSidecarRenderer(string meshName, Renderer[] candidateRenderers, LodBakeBatch batch)
        {
            Renderer best = null;
            int bestScore = 0;

            foreach (var renderer in candidateRenderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                int score = GetSidecarRendererScore(meshName, renderer, batch);
                if (score <= 0 || score <= bestScore)
                    continue;

                bestScore = score;
                best = renderer;
            }

            return best;
        }

        int GetSidecarRendererScore(string meshName, Renderer renderer, LodBakeBatch batch)
        {
            int score = 0;
            string rendererGroupKey = UvToolContext.ExtractGroupKey(renderer.name);
            if (NamesEqual(meshName, rendererGroupKey))
                score = 40;
            else if (TryGetRendererMesh(renderer, out var mesh) && mesh != null && NamesEqual(meshName, mesh.name))
                score = 30;
            else if (NamesEqual(meshName, renderer.name))
                score = 20;

            if (score == 0)
                return 0;

            var ownerEntry = ctx?.MeshEntries?.FirstOrDefault(e => e?.renderer == renderer);
            if (ownerEntry != null && ownerEntry.lodIndex == batch.lodIndex)
                score += 5;
            if (batch.targetEntries.Any(e => e?.renderer == renderer))
                score += 10;

            return score;
        }

        static bool NamesEqual(string a, string b)
            => !string.IsNullOrEmpty(a) &&
               !string.IsNullOrEmpty(b) &&
               string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static bool TryGetRendererMesh(Renderer renderer, out Mesh mesh)
        {
            mesh = null;
            if (renderer is SkinnedMeshRenderer smr && smr.sharedMesh != null)
            {
                mesh = smr.sharedMesh;
                return true;
            }

            var mf = renderer.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                mesh = mf.sharedMesh;
                return true;
            }

            return false;
        }

        static bool TryGetRendererOccluderData(
            Renderer renderer,
            out Mesh mesh,
            out Matrix4x4 matrix,
            out Bounds bounds,
            out Vector3 anchor)
        {
            mesh = null;
            matrix = Matrix4x4.identity;
            bounds = default;
            anchor = Vector3.zero;
            if (!TryGetRendererMesh(renderer, out mesh) || mesh == null)
                return false;

            matrix = renderer.transform.localToWorldMatrix;
            bounds = GetRendererWorldBounds(renderer, mesh, matrix);
            anchor = GetRendererWorldAnchor(renderer, mesh, matrix);
            return true;
        }

        static bool TryGetCollisionMesh(GameObject go, out Mesh mesh, out Matrix4x4 matrix, out Bounds bounds, out Vector3 anchor)
        {
            mesh = null;
            matrix = Matrix4x4.identity;
            bounds = default;
            anchor = Vector3.zero;
            if (go == null)
                return false;

            var meshCollider = go.GetComponent<MeshCollider>();
            if (meshCollider != null && meshCollider.sharedMesh != null)
                mesh = meshCollider.sharedMesh;

            if (mesh == null)
            {
                var meshFilter = go.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                    mesh = meshFilter.sharedMesh;
            }

            if (mesh == null)
                return false;

            matrix = go.transform.localToWorldMatrix;
            bounds = TransformBounds(mesh.bounds, matrix);
            anchor = GetMeshWorldAnchor(mesh, matrix);
            return true;
        }

        static List<Vector3> ComputeTargetAnchors(List<MeshEntry> entries, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            var anchors = new List<Vector3>();
            foreach (var entry in entries)
            {
                if (entry?.renderer == null)
                    continue;

                var mesh = entry.originalMesh ?? entry.fbxMesh;
                if (mesh == null)
                    continue;

                var matrix = entry.renderer.transform.localToWorldMatrix;
                var worldBounds = GetRendererWorldBounds(entry.renderer, mesh, matrix);
                anchors.Add(GetRendererWorldAnchor(entry.renderer, mesh, matrix));

                if (!hasBounds)
                {
                    bounds = worldBounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(worldBounds);
                }
            }

            if (!hasBounds)
                bounds = new Bounds(Vector3.zero, Vector3.one * 0.01f);
            return anchors;
        }

        static Bounds GetRendererWorldBounds(Renderer renderer, Mesh mesh, Matrix4x4 matrix)
        {
            if (renderer is SkinnedMeshRenderer || mesh == null)
                return renderer.bounds;
            return TransformBounds(mesh.bounds, matrix);
        }

        static Vector3 GetRendererWorldAnchor(Renderer renderer, Mesh mesh, Matrix4x4 matrix)
        {
            if (renderer is SkinnedMeshRenderer || mesh == null)
                return renderer.bounds.center;
            return GetMeshWorldAnchor(mesh, matrix);
        }

        static Vector3 GetMeshWorldAnchor(Mesh mesh, Matrix4x4 matrix)
        {
            if (mesh == null)
                return matrix.MultiplyPoint3x4(Vector3.zero);
            return matrix.MultiplyPoint3x4(mesh.bounds.center);
        }

        static Bounds TransformBounds(Bounds localBounds, Matrix4x4 matrix)
        {
            var corners = new[]
            {
                new Vector3(localBounds.min.x, localBounds.min.y, localBounds.min.z),
                new Vector3(localBounds.max.x, localBounds.min.y, localBounds.min.z),
                new Vector3(localBounds.min.x, localBounds.max.y, localBounds.min.z),
                new Vector3(localBounds.max.x, localBounds.max.y, localBounds.min.z),
                new Vector3(localBounds.min.x, localBounds.min.y, localBounds.max.z),
                new Vector3(localBounds.max.x, localBounds.min.y, localBounds.max.z),
                new Vector3(localBounds.min.x, localBounds.max.y, localBounds.max.z),
                new Vector3(localBounds.max.x, localBounds.max.y, localBounds.max.z)
            };

            Bounds worldBounds = new Bounds(matrix.MultiplyPoint3x4(corners[0]), Vector3.zero);
            for (int i = 1; i < corners.Length; i++)
                worldBounds.Encapsulate(matrix.MultiplyPoint3x4(corners[i]));
            return worldBounds;
        }

        static bool IsWithinOccluderRange(
            Bounds targetBounds,
            List<Vector3> targetAnchors,
            Bounds candidateBounds,
            Vector3 candidateAnchor,
            float radius)
        {
            if (BoundsDistance(targetBounds, candidateBounds) <= radius)
                return true;

            if (targetAnchors == null || targetAnchors.Count == 0)
                return false;

            float sqrRadius = radius * radius;
            for (int i = 0; i < targetAnchors.Count; i++)
            {
                if ((targetAnchors[i] - candidateAnchor).sqrMagnitude <= sqrRadius)
                    return true;
            }

            return false;
        }

        static float BoundsDistance(Bounds a, Bounds b)
        {
            float dx = Mathf.Max(0f, Mathf.Max(a.min.x - b.max.x, b.min.x - a.max.x));
            float dy = Mathf.Max(0f, Mathf.Max(a.min.y - b.max.y, b.min.y - a.max.y));
            float dz = Mathf.Max(0f, Mathf.Max(a.min.z - b.max.z, b.min.z - a.max.z));
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        void StoreBatchStats(List<LodBakeBatch> batches)
        {
            lastTargetMeshCount = batches.Sum(b => b.targetMeshes.Count);
            lastRenderableOccluderCount = batches.Sum(b => b.renderableOccluderCount);
            lastColliderOccluderCount = batches.Sum(b => b.colliderOccluderCount);
            lastUsedSidecarCollisionFallback = batches.Any(b => b.usedSidecarCollisionFallback);
        }

        void DisposePendingBatchTempMeshes()
        {
            if (pendingLodBatches == null)
                return;

            foreach (var batch in pendingLodBatches)
                DisposeBatchTemporaryMeshes(batch);
        }

        static void DisposeBatchTemporaryMeshes(LodBakeBatch batch)
        {
            if (batch?.temporaryMeshesToDestroy == null)
                return;

            foreach (var mesh in batch.temporaryMeshesToDestroy)
            {
                if (mesh != null)
                    UnityEngine.Object.DestroyImmediate(mesh);
            }
            batch.temporaryMeshesToDestroy.Clear();
        }

        void StartNextGpuLodBatch()
        {
            if (pendingLodBatches == null || pendingLodBatchIndex >= pendingLodBatches.Count)
            {
                bakedRawAO = pendingRawAO ?? new Dictionary<Mesh, float[]>();
                bakedFaceAreaAO = pendingFaceAreaAO;
                pendingLodBatches = null;
                pendingLodBatchIndex = 0;
                pendingRawAO = null;
                pendingFaceAreaAO = null;

                var finalEntries = pendingBakeEntries ?? new List<MeshEntry>();
                pendingBakeEntries = null;
                pendingBakeSettings = null;
                FinalizeBake(finalEntries);
                return;
            }

            var batch = pendingLodBatches[pendingLodBatchIndex];

            activeGpuJob = VertexAOBaker.StartGPUBake(batch.targetMeshes, batch.occluderMeshes, pendingBakeSettings,
                result => OnGpuLodBakeComplete(result, batch),
                error => OnGpuLodBakeError(error, batch));

            if (activeGpuJob == null)
            {
                UvtLog.Warn($"[Vertex AO] GPU setup failed on LOD{batch.lodIndex}. Falling back to CPU for remaining LODs.");
                ExecuteRemainingCpuBatchesFrom(pendingLodBatchIndex);
                return;
            }

            UvtLog.Info(
                $"[Vertex AO] GPU bake started for LOD{batch.lodIndex} ({pendingLodBatchIndex + 1}/{pendingLodBatches.Count}) — " +
                $"targets={batch.targetMeshes.Count}, nearby={batch.renderableOccluderCount}, collision={batch.colliderOccluderCount}" +
                (batch.usedSidecarCollisionFallback ? ", sidecar collision fallback." : "."));
        }

        void OnGpuLodBakeComplete(Dictionary<Mesh, float[]> result, LodBakeBatch batch)
        {
            activeGpuJob = null;
            try
            {
                if (pendingRawAO == null) pendingRawAO = new Dictionary<Mesh, float[]>();
                foreach (var kvp in result)
                    pendingRawAO[kvp.Key] = kvp.Value;

                var faceArea = VertexAOBaker.ApplyFaceAreaCorrection(
                    result,
                    batch.targetMeshes,
                    batch.occluderMeshes,
                    pendingBakeSettings);
                if (pendingFaceAreaAO == null) pendingFaceAreaAO = new Dictionary<Mesh, float[]>();
                foreach (var kvp in faceArea)
                    pendingFaceAreaAO[kvp.Key] = kvp.Value;
            }
            finally
            {
                DisposeBatchTemporaryMeshes(batch);
            }

            pendingLodBatchIndex++;
            StartNextGpuLodBatch();
        }

        void OnGpuLodBakeError(string error, LodBakeBatch batch)
        {
            UvtLog.Error($"[Vertex AO] GPU bake failed on LOD{batch.lodIndex}: {error}. Falling back to CPU for remaining LODs.");
            activeGpuJob = null;
            ExecuteRemainingCpuBatchesFrom(pendingLodBatchIndex);
        }

        void ExecuteRemainingCpuBatchesFrom(int startIndex)
        {
            if (pendingLodBatches == null || pendingBakeSettings == null)
                return;

            for (int i = startIndex; i < pendingLodBatches.Count; i++)
            {
                var batch = pendingLodBatches[i];
                UvtLog.Info(
                    $"[Vertex AO] CPU fallback for LOD{batch.lodIndex} — targets={batch.targetMeshes.Count}, nearby={batch.renderableOccluderCount}, collision={batch.colliderOccluderCount}" +
                    (batch.usedSidecarCollisionFallback ? ", sidecar collision fallback." : "."));

                try
                {
                    var raw = VertexAOBaker.BakeMultiMesh(batch.targetMeshes, batch.occluderMeshes, pendingBakeSettings);
                    var face = VertexAOBaker.ApplyFaceAreaCorrection(
                        raw,
                        batch.targetMeshes,
                        batch.occluderMeshes,
                        pendingBakeSettings);

                    if (pendingRawAO == null) pendingRawAO = new Dictionary<Mesh, float[]>();
                    foreach (var kvp in raw)
                        pendingRawAO[kvp.Key] = kvp.Value;

                    if (pendingFaceAreaAO == null) pendingFaceAreaAO = new Dictionary<Mesh, float[]>();
                    foreach (var kvp in face)
                        pendingFaceAreaAO[kvp.Key] = kvp.Value;
                }
                finally
                {
                    DisposeBatchTemporaryMeshes(batch);
                }
            }

            pendingLodBatchIndex = pendingLodBatches.Count;
            StartNextGpuLodBatch();
        }

        void RepaintDuringBake()
        {
            if (activeGpuJob == null || !activeGpuJob.IsRunning)
            {
                EditorApplication.update -= RepaintDuringBake;
                return;
            }
            requestRepaint?.Invoke();
        }

        void ExecuteBakeCPU(
            List<LodBakeBatch> batches,
            VertexAOSettings settings,
            List<MeshEntry> entries)
        {
            bakedRawAO = new Dictionary<Mesh, float[]>();
            bakedFaceAreaAO = new Dictionary<Mesh, float[]>();

            foreach (var batch in batches)
            {
                UvtLog.Info(
                    $"[Vertex AO] CPU bake for LOD{batch.lodIndex} — targets={batch.targetMeshes.Count}, nearby={batch.renderableOccluderCount}, collision={batch.colliderOccluderCount}" +
                    (batch.usedSidecarCollisionFallback ? ", sidecar collision fallback." : "."));

                try
                {
                    var raw = VertexAOBaker.BakeMultiMesh(batch.targetMeshes, batch.occluderMeshes, settings);
                    var face = VertexAOBaker.ApplyFaceAreaCorrection(raw, batch.targetMeshes, batch.occluderMeshes, settings);
                    foreach (var kvp in raw)
                        bakedRawAO[kvp.Key] = kvp.Value;
                    foreach (var kvp in face)
                        bakedFaceAreaAO[kvp.Key] = kvp.Value;
                }
                finally
                {
                    DisposeBatchTemporaryMeshes(batch);
                }
            }

            FinalizeBake(entries);
        }

        void FinalizeBake(List<MeshEntry> entries)
        {
            bakeStopwatch?.Stop();
            bakeTimeSeconds = bakeStopwatch != null ? (float)bakeStopwatch.Elapsed.TotalSeconds : 0f;
            bakeStopwatch = null;

            bakedVertexCount = 0;
            foreach (var kvp in bakedRawAO)
                bakedVertexCount += kvp.Value.Length;

            ApplyBlurInternal();

            int lodCount = entries.Select(e => e.lodIndex).Distinct().Count();
            string bakeTypeName = bakeTypeIndex == 0 ? "AO" : "Thickness";
            UvtLog.Info(
                $"[Vertex AO] Baked {bakeTypeName} for {bakedVertexCount} vertices across {lodCount} LOD(s) in {bakeTimeSeconds:F1}s " +
                $"(targets={lastTargetMeshCount}, nearby={lastRenderableOccluderCount}, collision={lastColliderOccluderCount}" +
                (lastUsedSidecarCollisionFallback ? ", sidecar collision fallback)." : ")."));

            ActivatePreview();
            requestRepaint?.Invoke();
        }

        void LoadFromMesh()
        {
            RestorePreview();

            var entries = ctx.MeshEntries
                .Where(e => e.include && e.renderer != null)
                .ToList();

            if (entries.Count == 0)
            {
                UvtLog.Warn("[Vertex AO] No meshes found.");
                return;
            }

            var channel = TargetChannel;
            bakedRawAO = new Dictionary<Mesh, float[]>();
            bakedFaceAreaAO = null;
            bakedVertexCount = 0;

            foreach (var e in entries)
            {
                Mesh mesh = e.originalMesh ?? e.fbxMesh;
                if (ctx != null && ctx.StandaloneMesh &&
                    e.renderer != null &&
                    TryGetRendererMesh(e.renderer, out var currentMesh) &&
                    currentMesh != null)
                {
                    mesh = currentMesh;
                }
                if (mesh == null) continue;

                var ao = ReadFromChannel(mesh, channel);
                if (ao == null) continue;

                bakedRawAO[mesh] = ao;
                bakedVertexCount += ao.Length;
            }

            if (bakedRawAO.Count == 0)
            {
                UvtLog.Warn($"[Vertex AO] No AO data found in {TargetChannelName}.");
                bakedRawAO = null;
                return;
            }

            lastTargetMeshCount = bakedRawAO.Count;
            lastRenderableOccluderCount = 0;
            lastColliderOccluderCount = 0;
            lastUsedSidecarCollisionFallback = false;
            bakeTimeSeconds = 0;
            ApplyBlurInternal();
            ActivatePreview();
            requestRepaint?.Invoke();
            UvtLog.Info($"[Vertex AO] Loaded {bakedVertexCount} vertices from {TargetChannelName}");
        }

        static float[] ReadFromChannel(Mesh mesh, AOTargetChannel channel)
        {
            int ch = (int)channel;
            int vertCount = mesh.vertexCount;

            if (ch <= (int)AOTargetChannel.VertexColorA)
            {
                var colors = mesh.colors32;
                if (colors == null || colors.Length != vertCount) return null;
                int comp = ch - (int)AOTargetChannel.VertexColorR;
                var ao = new float[vertCount];
                for (int i = 0; i < vertCount; i++)
                {
                    var c = colors[i];
                    ao[i] = (comp == 0 ? c.r : comp == 1 ? c.g : comp == 2 ? c.b : c.a) / 255f;
                }
                return ao;
            }
            else
            {
                int uvIdx = (ch - (int)AOTargetChannel.UV0_X) / 2;
                int comp  = (ch - (int)AOTargetChannel.UV0_X) % 2;
                var uvs = new List<Vector2>();
                mesh.GetUVs(uvIdx, uvs);
                if (uvs.Count != vertCount) return null;
                var ao = new float[vertCount];
                for (int i = 0; i < vertCount; i++)
                    ao[i] = comp == 0 ? uvs[i].x : uvs[i].y;
                return ao;
            }
        }

        void ApplyBlur()
        {
            if (bakedRawAO == null) return;
            // Reset to raw, then apply blur
            bakedFinalAO = new Dictionary<Mesh, float[]>();
            foreach (var kvp in bakedRawAO)
                bakedFinalAO[kvp.Key] = (float[])kvp.Value.Clone();
            ApplyBlurInternal();
            if (previewActive)
            {
                // Re-create preview with updated blur results
                RestorePreview();
                ActivatePreview();
            }
            requestRepaint?.Invoke();
        }

        void ApplyBlurInternal()
        {
            if (bakedRawAO == null) return;

            // Start from raw or face-area-blended base
            bakedFinalAO = new Dictionary<Mesh, float[]>();
            foreach (var mesh in bakedRawAO.Keys)
            {
                var raw = bakedRawAO[mesh];
                if (faceAreaStrength > 0f && bakedFaceAreaAO != null && bakedFaceAreaAO.TryGetValue(mesh, out var faa))
                {
                    var blended = new float[raw.Length];
                    for (int i = 0; i < raw.Length; i++)
                        blended[i] = Mathf.Lerp(raw[i], faa[i], faceAreaStrength);
                    bakedFinalAO[mesh] = blended;
                }
                else
                {
                    bakedFinalAO[mesh] = (float[])raw.Clone();
                }
            }

            // 1. Topology blur
            if (topoBlurIter > 0)
            {
                foreach (var mesh in bakedFinalAO.Keys.ToList())
                {
                    var uv0List = new List<Vector2>();
                    mesh.GetUVs(0, uv0List);
                    var uv0Arr = uv0List.Count == mesh.vertexCount ? uv0List.ToArray() : null;

                    bakedFinalAO[mesh] = VertexAOBaker.BlurAO(
                        bakedFinalAO[mesh], mesh.triangles, mesh.vertexCount,
                        topoBlurIter, topoBlurStr,
                        mesh.vertices, mesh.normals, uv0Arr,
                        topoCrossHardEdges, topoCrossUvSeams);
                }
            }

            // 2. 3D Spatial blur (stacks on top of topology blur)
            if (spatialBlurIter > 0)
            {
                foreach (var mesh in bakedFinalAO.Keys.ToList())
                {
                    bakedFinalAO[mesh] = VertexAOBaker.BlurAO3D(
                        bakedFinalAO[mesh], mesh.vertices,
                        spatialBlurIter, spatialBlurStr, spatialBlurRadius);
                }
            }

            // 3. Brightness / Contrast
            if (ppBrightness != 0f || ppContrast != 1f)
            {
                foreach (var mesh in bakedFinalAO.Keys.ToList())
                {
                    var ao = bakedFinalAO[mesh];
                    for (int i = 0; i < ao.Length; i++)
                    {
                        float v = (ao[i] - 0.5f) * ppContrast + 0.5f + ppBrightness;
                        ao[i] = Mathf.Clamp01(v);
                    }
                }
            }
        }

        // ── Apply ──

        void ApplyToMesh()
        {
            if (bakedFinalAO == null) return;
            RestorePreview();

            var channel = TargetChannel;
            LastAppliedTargetChannel = channel;
            var appliedMeshes = new HashSet<Mesh>();
            var selectedRenderer = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponentInParent<Renderer>()
                : null;
            foreach (var kvp in bakedFinalAO)
            {
                Mesh seedMesh = kvp.Key;
                var ao = kvp.Value;
                if (seedMesh == null || ao == null) continue;

                // AO bake runs on original working meshes, but FBX export can use
                // repacked/transferred variants. Mirror AO into all mesh variants
                // of the same entry so overwrite export persists the selected channel.
                var targetMeshes = new List<Mesh> { seedMesh };
                MeshEntry owner = null;
                if (ctx?.MeshEntries != null)
                {
                    owner = ctx.MeshEntries.FirstOrDefault(e =>
                        e.originalMesh == seedMesh ||
                        e.fbxMesh == seedMesh ||
                        e.repackedMesh == seedMesh ||
                        e.transferredMesh == seedMesh);
                    if (owner != null)
                    {
                        if (owner.originalMesh != null) targetMeshes.Add(owner.originalMesh);
                        if (owner.repackedMesh != null) targetMeshes.Add(owner.repackedMesh);
                        if (owner.transferredMesh != null) targetMeshes.Add(owner.transferredMesh);
                        if (owner.fbxMesh != null) targetMeshes.Add(owner.fbxMesh);
                    }
                }

                if (applySelectedRendererOnly)
                {
                    if (selectedRenderer == null)
                        continue;
                    if (owner == null || owner.renderer != selectedRenderer)
                        continue;
                }
                foreach (var mesh in targetMeshes)
                {
                    if (mesh == null) continue;
                    if (!appliedMeshes.Add(mesh)) continue;
                    if (mesh.vertexCount != ao.Length) continue;

                    if (applySelectedRendererOnly && applySelectedSubmeshOnly)
                    {
                        var merged = ReadFromChannel(mesh, channel) ?? new float[mesh.vertexCount];
                        if (selectedSubmeshIndex < 0 || selectedSubmeshIndex >= mesh.subMeshCount)
                        {
                            UvtLog.Warn($"[Vertex AO] Submesh index {selectedSubmeshIndex} is out of range for mesh '{mesh.name}'.");
                            continue;
                        }

                        var triangles = mesh.GetTriangles(selectedSubmeshIndex);
                        for (int ti = 0; ti < triangles.Length; ti++)
                        {
                            int vi = triangles[ti];
                            if (vi >= 0 && vi < merged.Length)
                                merged[vi] = ao[vi];
                        }
                        VertexAOBaker.WriteToChannel(mesh, merged, channel);
                    }
                    else
                    {
                        VertexAOBaker.WriteToChannel(mesh, ao, channel);
                    }
                    EditorUtility.SetDirty(mesh);
                }
            }

            UvtLog.Info($"[Vertex AO] Applied to {appliedMeshes.Count} mesh(es) → {TargetChannelName}");
        }

        static int GetRendererSubmeshCount(Renderer renderer)
        {
            if (renderer == null) return 0;
            if (renderer is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                return smr.sharedMesh.subMeshCount;
            var mf = renderer.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                return mf.sharedMesh.subMeshCount;
            return 0;
        }

        // ── Preview ──

        void ActivatePreview()
        {
            if (bakedFinalAO == null || previewActive) return;

            if (previewMaterial == null)
            {
                var sh = Shader.Find("Hidden/Internal-Colored");
                previewMaterial = new Material(sh)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                previewMaterial.SetInt("_SrcBlend", (int)BlendMode.One);
                previewMaterial.SetInt("_DstBlend", (int)BlendMode.Zero);
                previewMaterial.SetInt("_Cull", (int)CullMode.Back);
                previewMaterial.SetInt("_ZWrite", 1);
            }

            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.renderer == null) continue;
                var mf = e.meshFilter;
                if (mf == null) continue;
                Mesh mesh = e.originalMesh ?? e.fbxMesh;
                if (mesh == null || !bakedFinalAO.ContainsKey(mesh)) continue;

                var mr = e.renderer as MeshRenderer;
                if (mr == null) continue;

                // Backup
                previewBackups.Add((mf, mf.sharedMesh, mr.sharedMaterials));

                // Clone mesh with AO in vertex colors
                var clone = UnityEngine.Object.Instantiate(mesh);
                clone.hideFlags = HideFlags.HideAndDontSave;
                var colors = new Color32[clone.vertexCount];
                var ao = bakedFinalAO[mesh];
                for (int i = 0; i < colors.Length; i++)
                {
                    byte v = (byte)(Mathf.Clamp01(ao[i]) * 255f);
                    colors[i] = new Color32(v, v, v, 255);
                }
                clone.colors32 = colors;
                mf.sharedMesh = clone;

                var mats = new Material[mr.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++)
                    mats[i] = previewMaterial;
                mr.sharedMaterials = mats;
            }

            previewActive = true;
            SceneView.RepaintAll();
        }

        internal void RestorePreview()
        {
            if (!previewActive) return;

            foreach (var (mf, originalMesh, originalMats) in previewBackups)
            {
                if (mf == null) continue;
                if (mf.sharedMesh != null && mf.sharedMesh != originalMesh)
                    UnityEngine.Object.DestroyImmediate(mf.sharedMesh);
                mf.sharedMesh = originalMesh;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr != null) mr.sharedMaterials = originalMats;
            }
            previewBackups.Clear();
            previewActive = false;
            SceneView.RepaintAll();
        }

        void UpdatePreviewColors()
        {
            if (!previewActive || bakedFinalAO == null) return;
            // Update preview clone colors without re-creating everything
            foreach (var (mf, originalMesh, _) in previewBackups)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (!bakedFinalAO.TryGetValue(originalMesh, out var ao)) continue;
                var clone = mf.sharedMesh;
                var colors = new Color32[clone.vertexCount];
                for (int i = 0; i < colors.Length && i < ao.Length; i++)
                {
                    byte v = (byte)(Mathf.Clamp01(ao[i]) * 255f);
                    colors[i] = new Color32(v, v, v, 255);
                }
                clone.colors32 = colors;
            }
            SceneView.RepaintAll();
        }

        // ── Unused interface ──

        public void OnDrawToolbarExtra() { }
        public void OnDrawStatusBar() { }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { }
        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes() { yield break; }
        public void OnSceneGUI(SceneView sv) { }
    }
}
