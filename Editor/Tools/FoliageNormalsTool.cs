using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    [MeshLabTool("foliage_normals", MeshLabLibraries.Geometry, MeshLabLibraries.Remesh, MeshLabLibraries.VertexChannels, MeshLabLibraries.Assets)]
    public sealed class FoliageNormalsTool : IUvTool, IUvTool3D, IUvTool3DFrameContext
    {
        enum Preview
        {
            SourceCards, PrimitiveSamples, SourceProxy, SmoothedProxy, TransferProxy, TransferredNormals, BeforeAfter
        }
        public string ToolName => "Foliage Normals";
        public string ToolId => "foliage_normals";
        public int ToolOrder => 36;
        public Action RequestRepaint
        {
            private get; set;
        }
        object IUvTool3DFrameContext.FrameContext => root;
        internal readonly FoliageNormalsPipeline pipeline = new FoliageNormalsPipeline();
        internal FoliageNormalsSettings settings = new FoliageNormalsSettings();
        internal GameObject root;
        Preview preview = Preview.TransferredNormals;
        bool advanced, showPrimitives, showProxyEdges = true, active;
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
            pipeline.Changed = () => { if (active) { RequestRepaint?.Invoke(); } };
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
            restored.Validate();
            return restored;
        }

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            active = true;
            if (!root)
            {
                SetRoot(Selection.activeGameObject);
            }

            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.hierarchyChanged += HierarchyChanged;
            SubscribeTick(Tick);
        }
        public void OnDeactivate()
        {
            active = false;
            AssemblyReloadEvents.beforeAssemblyReload -= Clear;
            EditorApplication.hierarchyChanged -= HierarchyChanged;
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
        void Tick()
        {
            if (pipeline.IsRunning)
            {
                RequestRepaint?.Invoke();
            }
        }
        void Clear()
        {
            EditorPrefs.SetString(Prefs, JsonUtility.ToJson(settings));
            pipeline.Dispose();
        }
        public void OnRefresh()
        {
            if (!pipeline.IsRunning)
            {
                pipeline.InvalidateChanged(settings, root);
            }
        }
        void HierarchyChanged()
        {
            if (!pipeline.IsRunning)
            {
                RefreshFilters();
                pipeline.InvalidateChanged(settings, root);
            }
            RequestRepaint?.Invoke();
        }

        internal void SetRoot(GameObject source)
        {
            if (pipeline.IsRunning)
            {
                return;
            }

            root = source;
            settings.renderers = Array.Empty<MeshRenderer>();
            settings.materials = Array.Empty<Material>();
            settings.filterRenderers = false;
            pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture);
            RefreshFilters();
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
                    EditorPrefs.SetString(Prefs, JsonUtility.ToJson(settings));
                }
                DrawOutput();
            }
            if (pipeline.IsRunning && GUILayout.Button("Cancel"))
            {
                pipeline.Cancel();
            }

            preview = (Preview)EditorGUILayout.EnumPopup("Preview", preview);
            showProxyEdges = EditorGUILayout.Toggle("Show proxy edges", showProxyEdges);
            EditorGUILayout.HelpBox("Use the shared 3D canvas. Before/after: original on left, transferred on right; cyan/yellow normal vectors. Samples display at most 4,096 points.", MessageType.Info);
            DrawDiagnostics();
            EditorGUILayout.LabelField(pipeline.Status, EditorStyles.wordWrappedMiniLabel);
        }

        void DrawSourceSettings()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            var selectedRoot = (GameObject)EditorGUILayout.ObjectField("Root", root, typeof(GameObject), true);
            if (selectedRoot != root)
            {
                SetRoot(selectedRoot);
            }

            settings.lod0Only = EditorGUILayout.Toggle("LOD0 Only", settings.lod0Only);
            settings.selection = (FoliageFaceSelection)EditorGUILayout.EnumPopup("Foliage faces", settings.selection);
            DrawRendererSelection();
            DrawFaceSelection();
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
            settings.relativeSpacing = EditorGUILayout.Toggle("Relative to bounds", settings.relativeSpacing);
            settings.sampleSpacing = EditorGUILayout.FloatField(settings.relativeSpacing ? "Spacing / diagonal" : "Spacing (capture units)", settings.sampleSpacing);
            settings.radiusMultiplier = EditorGUILayout.FloatField("Sphere radius / spacing", settings.radiusMultiplier);
            settings.minimumSamplesPerIsland = EditorGUILayout.IntField("Minimum / island", settings.minimumSamplesPerIsland);
            settings.includeSourceVertices = EditorGUILayout.Toggle("Include vertices", settings.includeSourceVertices);
            settings.includeTriangleCentroids = EditorGUILayout.Toggle("Include centroids", settings.includeTriangleCentroids);
            settings.deterministicSeed = EditorGUILayout.IntField("Seed", settings.deterministicSeed);
            settings.maximumPrimitiveCount = EditorGUILayout.IntField("Primitive budget", settings.maximumPrimitiveCount);
            settings.dedupSpacingFraction = EditorGUILayout.Slider("Dedup / spacing", settings.dedupSpacingFraction, .001f, 1);
            StageButton("Scatter samples", FoliageNormalsPipeline.Stage.Scatter);
        }

        void DrawEnvelopeSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Envelope", EditorStyles.boldLabel);
            settings.voxelResolution = EditorGUILayout.IntSlider("Voxel resolution", settings.voxelResolution, 4, 256);
            settings.primitiveSubdivision = EditorGUILayout.IntSlider("Sphere subdivision", settings.primitiveSubdivision, 0, 1);
            showPrimitives = EditorGUILayout.Toggle("Show sample points", showPrimitives);
            StageButton("Build Proxy", FoliageNormalsPipeline.Stage.Envelope);
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
                settings.maximumProxyTriangles = EditorGUILayout.IntField("Proxy triangle budget", settings.maximumProxyTriangles);
            }
            EditorGUILayout.HelpBox("Split, collapse and flip edges, then relax and project onto the smoothed envelope. Auto uses its mean edge length. Transfer uses this final proxy.", MessageType.Info);
            StageButton("Remesh / rebuild", FoliageNormalsPipeline.Stage.Remesh);
        }

        void DrawShapeSettings()
        {
            settings.autoFitProxy = EditorGUILayout.Toggle("Auto fit selected foliage", settings.autoFitProxy);
            settings.proxyCenter = EditorGUILayout.Vector3Field(settings.autoFitProxy ? "Center offset (root local)" : "Center (root local)", settings.proxyCenter);
            if (settings.autoFitProxy)
            {
                settings.proxyRadiusScale = EditorGUILayout.Slider("Radius multiplier", settings.proxyRadiusScale, .01f, 4);
            }
            else
            {
                settings.proxyRadius = EditorGUILayout.FloatField("Radius (capture units)", settings.proxyRadius);
            }

            if (settings.normalSource == FoliageNormalSource.Hemisphere)
            {
                settings.proxyRotation = EditorGUILayout.Vector3Field("Rotation (root local)", settings.proxyRotation);
                EditorGUILayout.HelpBox("Open hemisphere, no base cap. Default axis: root +Y. Auto fit places its base at the lowest selected point along that axis.", MessageType.Info);
            }
            settings.proxySegments = (EditorGUILayout.IntSlider("Segments", settings.proxySegments, 16, 128) + 2) / 4 * 4;
            EditorGUILayout.HelpBox("Sphere and hemisphere use smooth radial normals and the same nearest-triangle transfer. Scatter, voxelization, smoothing and remeshing are skipped.", MessageType.Info);
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

            using (new EditorGUI.DisabledScope(!pipeline.CanApply))
            {
                if (pipeline.InPrefabMode)
                {
                    EditorGUILayout.HelpBox("Save a Mesh Asset or Separate FBX and assign the saved mesh in Prefab Mode.", MessageType.Info);
                }

                using (new EditorGUI.DisabledScope(!pipeline.CanApplyToScene))
                {
                    if (GUILayout.Button("Apply to Scene"))
                    {
                        pipeline.Apply();
                        RequestRepaint?.Invoke();
                    }
                }

                if (GUILayout.Button("Save Mesh Asset"))
                {
                    Save();
                }

                using (new EditorGUI.DisabledScope(!FoliageFbxExport.Available))
                {
                    if (GUILayout.Button("Save Separate FBX"))
                    {
                        SaveFbx();
                    }
                }
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
        async Task Run(FoliageNormalsPipeline.Stage to)
        {
            if (await pipeline.Run(root, settings, to))
            {
                switch (to)
                {
                    case FoliageNormalsPipeline.Stage.Capture:
                        preview = Preview.SourceCards;
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
                        break;
                }
            }
            if (active)
            {
                RequestRepaint?.Invoke();
            }
        }
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
            }

            if (pipeline.Envelope != null)
            {
                EditorGUILayout.LabelField($"{pipeline.Envelope.TriangleCount:N0} proxy triangles · {pipeline.ProxyComponents} components", EditorStyles.miniLabel);
            }

            if (pipeline.ShapeProxy != null)
            {
                EditorGUILayout.LabelField($"{pipeline.ProxyName}\nCenter {pipeline.ShapeProxy.center} · radius {pipeline.ShapeProxy.radius:G5} · axis {pipeline.ShapeProxy.axis}", EditorStyles.wordWrappedMiniLabel);
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

        public bool Get3DContent(List<MeshViewport3D.Item> items)
        {
            var proxy = PreviewProxy();
            if (proxy)
            {
                items.Add(new MeshViewport3D.Item(proxy, Matrix4x4.identity));
                return true;
            }
            float offset = pipeline.CaptureBounds.size.x * .65f;
            foreach (var t in pipeline.Targets)
            {
                AddTargetPreview(items, t, offset);
            }
            return true;
        }

        Mesh PreviewProxy()
        {
            switch (preview)
            {
                case Preview.SourceProxy: return pipeline.EnvelopeMesh;
                case Preview.SmoothedProxy: return pipeline.SmoothedMesh;
                case Preview.TransferProxy: return pipeline.RemeshedMesh;
                default: return null;
            }
        }

        void AddTargetPreview(List<MeshViewport3D.Item> items, FoliageTargetSnapshot t, float offset)
        {
            if (preview == Preview.BeforeAfter)
            {
                items.Add(new MeshViewport3D.Item(t.original, Matrix4x4.Translate(Vector3.left * offset) * t.localToCapture, t.materials));
                if (t.working)
                {
                    items.Add(new MeshViewport3D.Item(t.working, Matrix4x4.Translate(Vector3.right * offset) * t.localToCapture, t.materials));
                }
            }
            else
            {
                items.Add(new MeshViewport3D.Item(preview == Preview.TransferredNormals && t.working ? t.working : t.original, t.localToCapture, t.materials));
            }
        }

        public void OnDraw3D(MeshViewport3D view)
        {
            if (showProxyEdges && !view.Wireframe)
            {
                var proxy = PreviewProxy();
                if (proxy)
                {
                    view.DrawWire(proxy, Matrix4x4.identity, view.WireColor);
                }
            }
            if ((showPrimitives || preview == Preview.PrimitiveSamples) && pipeline.Samples != null)
            {
                DrawSamples(view);
            }
            if (preview != Preview.BeforeAfter)
            {
                return;
            }

            float offset = pipeline.CaptureBounds.size.x * .65f, length = pipeline.CaptureBounds.size.magnitude * .025f;
            var before = new List<Vector3>();
            var after = new List<Vector3>();
            foreach (var t in pipeline.Targets)
            {
                AppendNormalLines(t, offset, length, before, after);
            }
            view.DrawLines(before, Matrix4x4.identity, Color.cyan);
            view.DrawLines(after, Matrix4x4.identity, Color.yellow);
        }
        void DrawSamples(MeshViewport3D view)
        {
            var p = pipeline.Samples.samples;
            var subset = new List<Vector3>();
            int n = Math.Min(4096, p.Length);
            for (int i = 0; i < n; i++)
            {
                subset.Add(p[(int)((long)i * p.Length / n)]);
            }

            view.DrawPoints(subset, Matrix4x4.identity, Color.green, 4);
        }

        static void AppendNormalLines(FoliageTargetSnapshot t, float offset, float length, List<Vector3> before, List<Vector3> after)
        {
            var normals = t.working ? t.working.normals : t.localNormals;
            int stride = Math.Max(1, (t.positions.Length + 255) / 256), nextDuplicate = t.localPositions.Length;
            for (int i = 0; i < t.positions.Length; i++)
            {
                // One linear scan tracks appended corners; no repeated prefix scans per drawn normal.
                int v = t.selectedVertices[i] && t.excludedVertices[i] && t.working ? nextDuplicate++ : i;
                if (i % stride != 0 || !t.selectedVertices[i])
                {
                    continue;
                }

                var a = t.positions[i] + Vector3.left * offset;
                before.Add(a);
                before.Add(a + t.captureNormals[i] * length);
                var b = t.positions[i] + Vector3.right * offset;
                after.Add(b);
                after.Add(b + MeshGeometry.UnitDirection(t.normalLocalToCapture.MultiplyVector(normals[v])) * length);
            }
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
