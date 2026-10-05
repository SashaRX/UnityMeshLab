using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// The Remesh &amp; Bake tab: source selection, per-stage settings and buttons, the
    /// right-sidebar previews and the save action. The stage work itself lives in
    /// <see cref="RemeshPipeline"/>, the export in <see cref="RemeshExporter"/>.
    /// </summary>
    [MeshLabTool("remesh_bake", MeshLabLibraries.Remesh, MeshLabLibraries.Baking, MeshLabLibraries.Assets)]
    public sealed class RemeshBakeTool : IUvTool, IUvToolRightSidebar, IUvTool3D, IUvToolUvContent, IUvTool3DFrameContext
    {
        public string ToolName => "Remesh & Bake";
        public string ToolId => "remesh_bake";
        public int ToolOrder => 35;
        object IUvTool3DFrameContext.FrameContext => source ? source : null;
        public Action RequestRepaint { private get; set; }

        // Settings survive domain reloads and tab switches so a tuned pipeline
        // does not silently reset to defaults between runs.
        const string SettingsKey = "MeshLab.RemeshBake.Settings";

        static readonly string[] StageTitles = { "1 · Voxel remesh", "2 · Simplify", "3 · Normals & UV", "4 · Bake" };
        static readonly string[] SampleNames = { "1 (off)", "4 (2×2)", "9 (3×3)", "16 (4×4)" };
        static readonly int[] SampleCounts = { 1, 4, 9, 16 };

        GameObject source, lastSelection;
        readonly RemeshSettings settings = new RemeshSettings();
        readonly RemeshPipeline pipeline = new RemeshPipeline();
        readonly RemeshCaptureHighlight highlight = new RemeshCaptureHighlight();
        bool highlightCapture;
        readonly bool[] folds = { true, true, true, true };
        bool chartFold;
        readonly RemeshPreview previews = new RemeshPreview();
        readonly RemeshPreview.Data previewData = new RemeshPreview.Data();
        string saveStatus;
        UvToolContext ctx;
        // The result mesh as a canvas entry: one stable instance so the canvas's hover
        // and selection survive repaints; its mesh follows the pipeline.
        readonly MeshEntry resultEntry = new MeshEntry { include = true };
        readonly List<MeshEntry> sourceEntries = new List<MeshEntry>();
        readonly List<Renderer> sourceRenderers = new List<Renderer>();
        readonly List<Mesh> ownedSourceMeshes = new List<Mesh>();
        GameObject previewSourceRoot;
        bool sourcePreviewDirty = true, previewLod0Only;
        internal GameObject Source => source;

        public RemeshBakeTool()
        {
            string json = EditorPrefs.GetString(SettingsKey, "");
            if (!string.IsNullOrEmpty(json)) {
                var restored = RemeshSettings.FromSavedJson(json);
                if (restored != null) settings = restored;
            }
            pipeline.Changed = () => { saveStatus = null; RequestRepaint?.Invoke(); };
        }

        internal void SaveSettings() => EditorPrefs.SetString(SettingsKey, JsonUtility.ToJson(settings));

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            this.ctx = ctx;
            FollowSelection();
            FollowSelectionChanges(FollowSelection, true);
            EditorApplication.hierarchyChanged += InvalidateSourcePreview;
            previews.RequestRepaint = () => RequestRepaint?.Invoke();
            // Result/preview objects carry HideAndDontSave, so they survive scene loads but
            // would leak across a domain reload once this instance is discarded.
            AssemblyReloadEvents.beforeAssemblyReload -= SaveSettingsAndClear;
            AssemblyReloadEvents.beforeAssemblyReload += SaveSettingsAndClear;
        }
        public void OnDeactivate()
        {
            FollowSelectionChanges(FollowSelection, false);
            AssemblyReloadEvents.beforeAssemblyReload -= SaveSettingsAndClear;
            SaveSettingsAndClear();
        }

        // Selection.selectionChanged is a static delegate field, so the one place that
        // writes it is static too; remove-then-add keeps the subscription single.
        static void FollowSelectionChanges(Action handler, bool follow)
        {
            Selection.selectionChanged -= handler;
            if (follow) Selection.selectionChanged += handler;
        }

        void SaveSettingsAndClear()
        {
            SaveSettings();
            previews.Dispose();
            pipeline.Dispose();
            highlight.Dispose();
            EditorApplication.hierarchyChanged -= InvalidateHighlight;
            EditorApplication.hierarchyChanged -= InvalidateSourcePreview;
            ClearSourcePreview();
        }

        // Source root follows the hierarchy selection, as the status text asks. A pick
        // made in the field holds until the selection changes, also across tab
        // switches. A LOD child resolves to its LODGroup so the bake keeps the
        // LOD0-only contract. Selections without a mesh or skinned renderer (lights,
        // cameras) and selections made during a bake leave the source as is.
        internal void FollowSelection()
        {
            var selected = Selection.activeGameObject;
            if (selected == lastSelection) return;
            lastSelection = selected;
            if (!selected || pipeline.IsRunning) return;
            var group = selected.GetComponentInParent<LODGroup>();
            var root = group ? group.gameObject : selected;
            if (!root.GetComponentInChildren<MeshRenderer>() && !root.GetComponentInChildren<SkinnedMeshRenderer>()) return;
            SetSource(root);
            RequestRepaint?.Invoke();
        }
        internal void SetSource(GameObject root)
        {
            if (source == root) return;
            source = root;
            InvalidateSourcePreview();
        }

        void InvalidateSourcePreview() { sourcePreviewDirty = true; RequestRepaint?.Invoke(); }

        internal void ClearSourcePreview()
        {
            foreach (var mesh in ownedSourceMeshes) if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
            ownedSourceMeshes.Clear(); sourceEntries.Clear(); sourceRenderers.Clear();
            previewData.sourceVertices = previewData.sourceTriangles = 0;
            previewSourceRoot = null; sourcePreviewDirty = true;
        }

        void EnsureSourcePreview()
        {
            var root = source ? source : pipeline.CapturedSource;
            if (!sourcePreviewDirty && root == previewSourceRoot && settings.lod0Only == previewLod0Only) return;
            ClearSourcePreview();
            previewSourceRoot = root; previewLod0Only = settings.lod0Only; sourcePreviewDirty = false;
            if (!root) return;
            foreach (var renderer in RemeshSource.CollectRenderers(root, settings.lod0Only)) {
                Mesh mesh = ReadSourcePreviewMesh(renderer);
                // Preview copies never substitute meshes in the scene's renderers.
                sourceEntries.Add(new MeshEntry { originalMesh = mesh, fbxMesh = mesh, previewTexture = SourcePreviewTexture(renderer) });
                sourceRenderers.Add(renderer);
                previewData.sourceVertices += mesh.vertexCount;
                previewData.sourceTriangles += TriangleCount(mesh);
            }
        }

        Mesh ReadSourcePreviewMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skin) {
                var posed = new Mesh { name = skin.sharedMesh.name, hideFlags = HideFlags.HideAndDontSave };
                ownedSourceMeshes.Add(posed);
                // Compensate renderer scale; Get3DContent applies its matrix once.
                skin.BakeMesh(posed, true);
                CopySkinAttributes(skin.sharedMesh, posed);
                return posed;
            }
            var mesh = MeshAccess.Readable(renderer.GetComponent<MeshFilter>().sharedMesh, out bool isCopy);
            if (isCopy) ownedSourceMeshes.Add(mesh);
            return mesh;
        }

        static void CopySkinAttributes(Mesh sourceMesh, Mesh posed)
        {
            // BakeMesh supplies deformed channels, but omits authored skin attributes.
            var authored = MeshAccess.Readable(sourceMesh, out bool authoredCopy);
            try {
                posed.bindposes = authored.bindposes;
                using (var counts = authored.GetBonesPerVertex())
                using (var weights = authored.GetAllBoneWeights())
                    if (weights.Length > 0) posed.SetBoneWeights(counts, weights);
            }
            finally { if (authoredCopy) UnityEngine.Object.DestroyImmediate(authored); }
        }

        static Texture SourcePreviewTexture(Renderer renderer)
        {
            foreach (var material in renderer.sharedMaterials)
                if (material && material.HasProperty("_MainTex") && material.mainTexture) return material.mainTexture;
            return null;
        }

        static int TriangleCount(Mesh mesh)
        {
            int count = 0;
            for (int sub = 0; sub < mesh.subMeshCount; ++sub)
                if (mesh.GetTopology(sub) == MeshTopology.Triangles) count += (int)mesh.GetIndexCount(sub) / 3;
            return count;
        }
        // Hub context (LODGroup selection, Undo) does not feed this tool: the source
        // snapshot is captured when the remesh stage runs, so a running bake must not be cancelled here.
        public void OnRefresh() { InvalidateSourcePreview(); }
        public void OnDrawToolbarExtra()
        {
            if (!previews.IsSource && MeshUvState.IsDraft(previews.DisplayMesh(previewData)))
                GUILayout.Label(new GUIContent("Draft UV0", "Temporary normalized XY; unwrap before UV-dependent processing or baking."), EditorStyles.miniLabel);
        }
        public void OnDrawStatusBar() { GUILayout.Label(Status, EditorStyles.miniLabel); }
        // The canvas's UV mode shows the selected stage, including the source before
        // any stages run, and the result's atlas once Normals & UV ran: the same
        // shells, wire, border and spot picking as any mesh, over the baked base color
        // (checker when the canvas asks for it). "Islands" tints every UV shell.
        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes()
        {
            yield return new UvCanvasView.FillModeEntry { name = "Islands", drawCallback = DrawFillIslands };
            yield return new UvCanvasView.FillModeEntry { name = "None", drawCallback = null };
        }

        public bool GetUvContent(List<MeshEntry> entries)
        {
            SyncPreviewData();
            if (previews.IsSource) { entries.AddRange(sourceEntries); return true; }
            var mesh = previews.DisplayMesh(previewData);
            // A captured stage without UVs is still this tool's content. Do not
            // silently fall back to the source LOD behind the user's chosen stage.
            if (!mesh) return pipeline.Nodes.Count > 0;
            resultEntry.originalMesh = resultEntry.fbxMesh = mesh;
            resultEntry.previewTexture = mesh == pipeline.ResultMesh ? pipeline.BaseColorPreview : null;
            entries.Add(resultEntry);
            return true;
        }

        void DrawFillIslands(UvCanvasView cv, float cx, float cy, float sz, Mesh mesh, MeshEntry entry)
        {
            if (ctx == null) return;
            var uvs = cv.RdUvCached(mesh, ctx.PreviewUvChannel);
            var tri = cv.GetTrianglesCached(mesh);
            if (uvs == null || tri == null) return;
            int hover = cv.HasHoveredShell && cv.HoveredShell.meshEntry == entry ? cv.HoveredShell.shellId : -1;
            int selected = cv.HasSelectedShell && cv.SelectedShell.meshEntry == entry ? cv.SelectedShell.shellId : -1;
            cv.GlFillSh(ctx, cx, cy, sz, mesh, tri.Length / 3, uvs.Length, entry, hover, selected, null);
        }
        public void OnSceneGUI(SceneView sv)
        {
            if (!highlightCapture || !source) return;
            if (highlight.Key != RemeshCaptureHighlight.KeyFor(source, settings)) { highlight.Build(source, settings); RequestRepaint?.Invoke(); }
            highlight.Draw();
        }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { }

        string Status => saveStatus ?? pipeline.Status;

        // Scene paint of the capture: what the filters keep and drop, before any stage runs.
        void DrawHighlightToggle()
        {
            bool on = EditorGUILayout.Toggle(new GUIContent("Highlight capture in Scene",
                "Paint the source in the Scene view as the remesh stage will see it: green = captured, orange = dropped as a small part, " +
                "red = dropped as a rod, grey = renderer excluded (LOD1+, collision, disabled). Follows the filter sliders live."), highlightCapture);
            if (on != highlightCapture) {
                highlightCapture = on;
                if (on) { EditorApplication.hierarchyChanged -= InvalidateHighlight; EditorApplication.hierarchyChanged += InvalidateHighlight; }
                else { EditorApplication.hierarchyChanged -= InvalidateHighlight; highlight.Clear(); }
                SceneView.RepaintAll();
            }
            if (!highlightCapture) return;
            // The paint follows the filter sliders live: a changed key repaints the Scene,
            // which rebuilds the highlight lazily in OnSceneGUI.
            if (highlight.Key != RemeshCaptureHighlight.KeyFor(source, settings)) SceneView.RepaintAll();
            using (new EditorGUI.IndentLevelScope()) {
                if (highlight.Error != null) EditorGUILayout.HelpBox(highlight.Error, MessageType.Warning);
                else if (!string.IsNullOrEmpty(highlight.Summary)) EditorGUILayout.LabelField(highlight.Summary, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField("green captured · orange small part · red rod · grey excluded", EditorStyles.centeredGreyMiniLabel);
                if (GUILayout.Button("Refresh highlight", EditorStyles.miniButton)) { InvalidateHighlight(); }
            }
        }

        void InvalidateHighlight() { highlight.Clear(); SceneView.RepaintAll(); }

        public void OnDrawRightSidebar()
        {
            SyncPreviewData();
            previews.Draw(previewData);
        }

        void SyncPreviewData()
        {
            if (pipeline.Nodes.Count == 0) previews.Show(RemeshPreview.Stage.Source);
            if (previews.IsSource) EnsureSourcePreview();
            previewData.meshes[(int)RemeshPreview.Stage.Source] = pipeline.SourceMesh;
            previewData.meshes[(int)RemeshPreview.Stage.Remesh] = pipeline.VoxelMesh;
            previewData.meshes[(int)RemeshPreview.Stage.Simplified] = pipeline.SimplifiedMesh;
            previewData.meshes[(int)RemeshPreview.Stage.Result] = pipeline.ResultMesh;
            previewData.geometry = pipeline.Geometry; previewData.maps = pipeline.Maps; previewData.baseColor = pipeline.BaseColorPreview;
            previewData.trimMask = pipeline.TrimMaskMesh;
            previewData.spaceToWorld = pipeline.Primary?.spaceToWorld ?? Matrix4x4.identity;
            // Live from the current settings so the cage preview reflects projection
            // distance changes before a re-bake; zero until a source snapshot exists.
            previewData.cageDistance = pipeline.SourceDiagonal * settings.projectionDistance;
            previewData.cageSmoothing = settings.cageSmoothing;
            previewData.cageFit = settings.cageFit && settings.sourceShape == RemeshShape.LOD0;
            previewData.sourceBackfaces = settings.sourceBackfaces;
            previewData.source = pipeline.Source;
            previewData.twoSided = pipeline.Primary?.twoSided ?? false;
        }

        // The shared 3D canvas shows the selected pipeline stage (in capture space) with
        // the right sidebar's surface and overlay toggles. Source shows the original
        // hierarchy with all UV channels, also before any stage runs.
        public bool Get3DContent(List<MeshViewport3D.Item> items)
        {
            SyncPreviewData();
            if (previews.IsSource) {
                for (int i = 0; i < sourceEntries.Count; ++i) {
                    var renderer = sourceRenderers[i];
                    if (renderer) items.Add(new MeshViewport3D.Item(sourceEntries[i].originalMesh,
                        renderer.localToWorldMatrix, renderer.sharedMaterials));
                }
                return true;
            }
            return previews.Fill3D(previewData, items);
        }

        public void OnDraw3D(MeshViewport3D view) => previews.Overlay3D(previewData, view);

        public void OnDrawSidebar()
        {
            EditorGUILayout.LabelField("High-poly → Low-poly", EditorStyles.boldLabel);
            bool busy = pipeline.IsRunning;
            using (new EditorGUI.DisabledScope(busy)) {
                SetSource((GameObject)EditorGUILayout.ObjectField("Source root", source, typeof(GameObject), true));
                using (new EditorGUI.DisabledScope(!source || UvProgress.IsActive))
                    if (GUILayout.Button("Run all stages", GUILayout.Height(26))) Start(RemeshPipeline.Stage.Bake, true);

                if (StageHeader(RemeshPipeline.Stage.Remesh)) {
                    settings.sourceShape = (RemeshShape)EditorGUILayout.EnumPopup(new GUIContent("Shape",
                        "LOD0: voxelize the captured geometry. Bounding box: one box per renderer along the renderer's own axes — a far-LOD proxy; " +
                        "materials and lighting bake from the original geometry, projected onto the box faces. Hull: a coarse blocky voxel hull " +
                        "simplified to a small triangle budget, keeping L/T footprints, courtyards and roof steps."), settings.sourceShape);
                    if (settings.sourceShape == RemeshShape.Hull) {
                        settings.hullResolution = EditorGUILayout.IntSlider(new GUIContent("Hull resolution", "Voxels along the longest axis of the coarse pass."), settings.hullResolution, 8, 64);
                        settings.hullTriangles = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Hull triangles", "Triangle budget the hull is simplified to."), settings.hullTriangles), 12, 100000);
                    }
                    settings.minPartSize = EditorGUILayout.Slider(new GUIContent("Exclude parts smaller than",
                        "Connected pieces whose bounds diagonal is below this fraction of the model's diagonal (bolts, railings, debris) are left out before any shape is built. 0 = keep everything."),
                        settings.minPartSize, 0, 0.2f);
                    settings.minRodVoxels = EditorGUILayout.Slider(new GUIContent("Exclude rods thinner than (voxels)",
                        "Connected pieces whose cross-section — the two smaller of their extents along their own axes — is under this many voxel cells " +
                        "(pipes, cables, railings, bolts) are left out; the grid cannot carry their section anyway. Sheets of any thickness (gate leaves, " +
                        "glass panes, decals) stay. A cell is the model's longest side divided by the voxel (or hull) resolution. 0 = keep everything."),
                        settings.minRodVoxels, 0, 4f);
                    settings.lod0Only = EditorGUILayout.Toggle(new GUIContent("LOD0 only",
                        "Capture ignores meshes named Name_LOD1 and higher; LODGroups already contribute LOD0 only."), settings.lod0Only);
                    settings.keepHierarchy = EditorGUILayout.Toggle(new GUIContent("Keep hierarchy",
                        "Remesh every renderer SEPARATELY and save the result as a hierarchy of meshes with per-node baked materials under one root, instead of welding everything into one mesh with one material."), settings.keepHierarchy);
                    DrawHighlightToggle();
                    using (new EditorGUI.DisabledScope(settings.sourceShape != RemeshShape.LOD0)) {
                        settings.voxelResolution = EditorGUILayout.IntSlider(new GUIContent("Voxel resolution",
                            "Voxels along the model's longest axis. Higher values preserve finer geometry and increase memory use and processing time."),
                            settings.voxelResolution, 4, RemeshSettings.MaxVoxelResolution);
                        settings.solve = EditorGUILayout.Toggle("Fit source surface", settings.solve);
                        settings.shell = EditorGUILayout.Toggle("Two-sided shell", settings.shell);
                        settings.trimToSource = EditorGUILayout.Toggle(new GUIContent("Trim to source surface",
                            "After the voxel remesh, drop the faces the source has no surface for. The voxelizer closes an open sheet (a wall, a roof " +
                            "plane, a curtain) into a thin slab; its back side and rims have no source face nearby with an aligned normal and go. " +
                            "Closed sources are left whole. Turn off to keep the slab, e.g. with Two-sided shell."), settings.trimToSource);
                    }
                    settings.sourceBackfaces = (RemeshBackfaces)EditorGUILayout.EnumPopup(new GUIContent("Source backfaces",
                        "Whether the source's back faces count as surface. From materials: two-sided when a material's cull mode is Off or its double-sided " +
                        "switch is on (a Cull Off written into the shader itself is not detectable — use Always). The trim still keeps one sheet; the result " +
                        "material renders both sides instead, and the bake samples two-sided faces from either side. Never: only fronts count."), settings.sourceBackfaces);
                    StageButton(RemeshPipeline.Stage.Remesh, "Remesh");
                }
                if (StageHeader(RemeshPipeline.Stage.Simplify)) {
                    settings.simplify = EditorGUILayout.Toggle("Simplify", settings.simplify);
                    using (new EditorGUI.DisabledScope(!settings.simplify)) {
                        settings.maximumError = EditorGUILayout.Slider(new GUIContent("Maximum error",
                            "Relative to the mesh size. Flat areas collapse first; raise it for fewer triangles."), settings.maximumError, 0, 0.2f);
                        settings.targetTriangles = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Stop at triangles",
                            "Simplification stops at this count or at Maximum error, whichever comes first. 0 = go as far as the error allows."),
                            settings.targetTriangles), 0, 5000000);
                        settings.regularize = (RemeshRegularize)EditorGUILayout.EnumPopup(new GUIContent("Regularize",
                            "None keeps density where the shape needs it; Light/Strong even out triangle sizes."), settings.regularize);
                        settings.preserveFolds = EditorGUILayout.Toggle("Preserve folds", settings.preserveFolds);
                        settings.pruneSmallParts = EditorGUILayout.Toggle("Remove small parts", settings.pruneSmallParts);
                    }
                    if (pipeline.Has(RemeshPipeline.Stage.Simplify) && settings.simplify)
                        EditorGUILayout.LabelField($"Achieved error {pipeline.SimplifyError:0.#####}", EditorStyles.miniLabel);
                    StageButton(RemeshPipeline.Stage.Simplify, "Simplify");
                }
                if (StageHeader(RemeshPipeline.Stage.Unwrap)) {
                    settings.hardEdges = (RemeshHardEdges)EditorGUILayout.EnumPopup(new GUIContent("Hard edges",
                        "Final normals after unwrap. Angle: crease angle. UV islands: hard along island borders, smooth inside. These modes do not change UV charting."), settings.hardEdges);
                    bool angle = settings.hardEdges == RemeshHardEdges.Angle || settings.hardEdges == RemeshHardEdges.UvIslandsAndAngle;
                    using (new EditorGUI.DisabledScope(!angle))
                        settings.normalCrease = EditorGUILayout.Slider("Crease angle", settings.normalCrease, 0, 180);
                    settings.normalWeighting = (RemeshNormalWeighting)EditorGUILayout.EnumPopup(new GUIContent("Normal weighting",
                        "How face normals are weighted when the UV-split vertex normals are regenerated — the Blender Weighted Normal " +
                        "analog. Face area is meshopt's own accumulation; corner angle pulls sharp corners harder; both multiply them."),
                        settings.normalWeighting);
                    settings.normalSmoothing = EditorGUILayout.Slider(new GUIContent("Normal smoothing",
                        "Applied after UV generation, so it works with every hard-edge mode: smoothing flows along the surface " +
                        "and stops at crease edges and island borders alike."),
                        settings.normalSmoothing, 0, 10);
                    settings.textureResolution = EditorGUILayout.IntPopup("Texture size", settings.textureResolution,
                        new[] { "512", "1024", "2048", "4096" }, new[] { 512, 1024, 2048, 4096 });
                    settings.padding = EditorGUILayout.IntSlider("Atlas padding", settings.padding, 1, 32);
                    settings.reduceUvFragmentation = EditorGUILayout.Toggle(new GUIContent("Reduce UV fragmentation",
                        "Compare the current chart settings with two alternatives; keep fewer islands and small fragments only within bounded UV stretch. " +
                        "Crease edges, island size limits and packing stay as configured. Adds up to two unwrap passes on the worker."),
                        settings.reduceUvFragmentation);
                    settings.mergeCharts = EditorGUILayout.Toggle(new GUIContent("Merge charts",
                        "Deterministically merges adjacent island pairs whose seam UVs align within bounded stretch and texel density, " +
                        "respecting max island area/border. Relaxes the resulting UV islands to reduce distortion before packing. Adds one xatlas pack pass on the worker."),
                        settings.mergeCharts);
                    chartFold = EditorGUILayout.Foldout(chartFold, "Islands & packing", true);
                    if (chartFold) {
                        using (new EditorGUI.IndentLevelScope()) {
                            if (GUILayout.Button(new GUIContent("Recommended xatlas settings", "Apply tested island-growth and packing defaults."))) {
                                settings.ApplyDefaultXatlasSettings();
                                SaveSettings();
                            }
                            settings.chartMaxCost = EditorGUILayout.Slider(new GUIContent("Max cost", "Lower = more, smaller islands."), settings.chartMaxCost, 0.1f, 10);
                            settings.chartNormalDeviation = EditorGUILayout.Slider(new GUIContent("Normal deviation", "Penalty for bending inside one island."), settings.chartNormalDeviation, 0, 10);
                            settings.chartNormalSeam = EditorGUILayout.Slider(new GUIContent("Hard edge seam", "Prefer island borders on hard edges (>1000 always)."), settings.chartNormalSeam, 0, 1000);
                            settings.chartStraightness = EditorGUILayout.Slider(new GUIContent("Straightness", "Prefer straight island borders."), settings.chartStraightness, 0, 20);
                            settings.chartRoundness = EditorGUILayout.Slider(new GUIContent("Roundness", "Prefer compact islands."), settings.chartRoundness, 0, 1);
                            settings.chartIterations = EditorGUILayout.IntSlider("Iterations", settings.chartIterations, 1, 16);
                            settings.maxChartArea = Mathf.Max(0, EditorGUILayout.FloatField(new GUIContent("Max island area", "Source units², 0 = no limit."), settings.maxChartArea));
                            settings.maxChartBoundary = Mathf.Max(0, EditorGUILayout.FloatField(new GUIContent("Max island border", "Source units, 0 = no limit."), settings.maxChartBoundary));
                            settings.packRotate = EditorGUILayout.Toggle("Rotate islands", settings.packRotate);
                            settings.packBlockAlign = EditorGUILayout.Toggle("Align to 4×4 blocks", settings.packBlockAlign);
                            settings.packBruteForce = EditorGUILayout.Toggle(new GUIContent("Best packing (slow)"), settings.packBruteForce);
                        }
                    }
                    StageButton(RemeshPipeline.Stage.Unwrap, "Unwrap");
                }
                if (StageHeader(RemeshPipeline.Stage.Bake)) {
                    settings.bakeMode = (RemeshBakeMode)EditorGUILayout.EnumPopup(new GUIContent("Bake mode",
                        "Materials transfers the source maps. Beauty bakes the object as the player sees it — realtime/mixed light with ray shadows, " +
                        "lightmaps, ambient and reflection probes folded into one lit BaseColor texture; the saved material becomes Unlit. " +
                        "Specular uses the scene view camera's position at bake time."), settings.bakeMode);
                    if (settings.sourceShape == RemeshShape.LOD0) {
                        settings.projectionDistance = EditorGUILayout.Slider(new GUIContent("Projection / bounds",
                            "Ray travel each way from the result surface, as a fraction of the source diagonal. The cage preview in the 3D view shows the shells the rays start and end on."),
                            settings.projectionDistance, 0.001f, 0.2f);
                        settings.cageSmoothing = EditorGUILayout.Slider(new GUIContent("Cage smoothing",
                            "Smoothing passes over the cage directions (welded across UV and crease splits, kept apart on double-sided sheets). 0 casts along the raw averaged normals; more flattens the decimation's sliver noise out of the ray directions."),
                            settings.cageSmoothing, 0f, 10f);
                        settings.cageFit = EditorGUILayout.Toggle(new GUIContent("Fit cage to source",
                            "Measure where the source sits along each cage ray and reach that far (1× to 8× the projection distance), instead of one global distance: fewer missed texels where the decimation drifted, no deep rays where it stayed close."),
                            settings.cageFit);
                    }
                    else
                        settings.proxyDepth = EditorGUILayout.Slider(new GUIContent("Proxy search depth",
                            "How deep behind a proxy face a texel looks for the source, as a fraction of the model's diagonal: the ray along the face " +
                            "normal, then the nearest surface within the same reach. Short keeps each face to what sits behind it and bakes fast; " +
                            "long lets a face see across courtyards and costs a full traversal per empty texel."), settings.proxyDepth, 0.01f, 1f);
                    settings.bakeSamples = EditorGUILayout.IntPopup(new GUIContent("Samples per texel", "Integrate texel coverage over the surface, including neighbouring faces across UV seams. More samples capture finer texture detail."),
                        settings.bakeSamples, Array.ConvertAll(SampleNames, n => new GUIContent(n)), SampleCounts);
                    settings.dilationRadius = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Dilation radius (px)",
                        "After atlas padding, extend each shell by this additional radius, sampling the connected 3D surface across UV seams. Open edges clamp to their surface boundary. 0 keeps padding alone."),
                        settings.dilationRadius), 0, RemeshSettings.MaxDilationRadius);
                    using (new EditorGUI.DisabledScope(!GpuBvh.Supported))
                        settings.gpuProjection = EditorGUILayout.Toggle(new GUIContent("GPU projection",
                            GpuBvh.Supported ? "Run the projection rays and nearest-point fallbacks on the GPU (BvhQueries.compute). Same results as the CPU BVH, usually several times faster on large atlases."
                                             : "This platform has no compute shaders; the bake runs on the CPU."), settings.gpuProjection);
                    settings.bakeSourceAO = EditorGUILayout.Toggle(new GUIContent("Bake source AO",
                        "Calculate ambient occlusion at projected source samples instead of only transferring the material's AO map. AO rays run on CPU workers."), settings.bakeSourceAO);
                    if (settings.bakeSourceAO) {
                        settings.sourceAO ??= new SourceAoSettings();
                        TextureAoBakePanel.DrawSourceSettings(settings.sourceAO);
                        settings.multiplySourceAO = EditorGUILayout.Toggle("Multiply source AO map", settings.multiplySourceAO);
                    }
                    settings.transferVertexColor = EditorGUILayout.Toggle("Vertex color (RGB)", settings.transferVertexColor);
                    settings.transferVertexAlpha = EditorGUILayout.Toggle("Vertex alpha", settings.transferVertexAlpha);
                    settings.vertexColorTint = EditorGUILayout.Toggle(new GUIContent("Vertex color tints albedo",
                        "Multiply the projected albedo by the source vertex color (RGB), the way vertex-tinting shaders read it. Meshes without vertex colors are unaffected; turn off for shaders that ignore the vertex color."), settings.vertexColorTint);
                    if (pipeline.Has(RemeshPipeline.Stage.Remesh) && !pipeline.SourceHasColors && (settings.transferVertexColor || settings.transferVertexAlpha))
                        EditorGUILayout.HelpBox("The source has no vertex colors; the transfer writes white.", MessageType.None);
                    StageButton(RemeshPipeline.Stage.Bake, "Bake");
                }
                EditorGUILayout.HelpBox("Experimental voxel remesh. Thin details and small gaps may disappear.", MessageType.Info);
            }
            if (busy)
                using (new EditorGUI.DisabledScope(!pipeline.CanCancel))
                    if (GUILayout.Button("Cancel (after current native phase)")) pipeline.Cancel();
            var maps = pipeline.Maps;
            EditorGUILayout.HelpBox(Status, maps != null && maps.misses > 0 ? MessageType.Warning : MessageType.None);
            if (pipeline.Has(RemeshPipeline.Stage.Bake)) DrawSave();
        }

        void DrawSave()
        {
            var result = pipeline.ResultMesh;
            EditorGUILayout.LabelField(pipeline.IsHierarchy
                ? $"{pipeline.Nodes.Count} node(s) · largest {result.vertexCount:N0} vertices · {result.GetIndexCount(0) / 3:N0} triangles"
                : $"{result.vertexCount:N0} vertices · {result.GetIndexCount(0) / 3:N0} triangles");
            settings.normalizeSize = EditorGUILayout.Toggle(new GUIContent("Normalize size (saved at scale 1)",
                "Bakes the source's world scale into the saved geometry, so the model keeps its real size with a " +
                "scale-1 transform regardless of how the source is scaled. Off: the saved transform carries the " +
                "source's scale instead. Keep-hierarchy saves always carry the scale on the root."),
                settings.normalizeSize);
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            settings.embedFbxTextures = EditorGUILayout.Toggle(new GUIContent("Embed textures in FBX",
                "Self-contained FBX that carries its maps — portable to other machines, but the file grows by the map sizes " +
                "(the float EXR emission map alone is 16 bytes per texel). Off: the FBX links the exported maps by absolute path."),
                settings.embedFbxTextures);
            string label = pipeline.IsHierarchy ? "Save meshes, maps & prefab…" : "Save FBX, maps & prefab…";
#else
            string label = pipeline.IsHierarchy ? "Save meshes, maps & prefab…" : "Save mesh, maps & prefab…";
#endif
            using (new EditorGUI.DisabledScope(pipeline.IsRunning))
                if (GUILayout.Button(label)) {
                    Save();
                    // The folder panel is modal and the export imports assets; both
                    // leave this event's layout state stale.
                    GUIUtility.ExitGUI();
                }
#if !LIGHTMAP_UV_TOOL_FBX_EXPORTER
            EditorGUILayout.HelpBox("Install com.unity.formats.fbx to save the model as an FBX instead of a mesh asset.", MessageType.None);
#endif
        }

        bool StageHeader(RemeshPipeline.Stage stage)
        {
            int i = (int)stage;
            string summary = pipeline.Summary(stage);
            bool stale = pipeline.IsStale(stage, settings, source);
            string label = StageTitles[i] + (summary == null ? "" : "   —   " + summary + (stale ? " (settings changed)" : ""));
            EditorGUILayout.Space(3);
            folds[i] = EditorGUILayout.Foldout(folds[i], label, true, EditorStyles.foldoutHeader);
            return folds[i];
        }

        void StageButton(RemeshPipeline.Stage stage, string label)
        {
            using (new EditorGUI.DisabledScope(!source && !pipeline.Has(RemeshPipeline.Stage.Remesh) || UvProgress.IsActive))
                if (GUILayout.Button(label)) Start(stage, false);
        }

        // Runs the clicked stage, first bringing every earlier stage up to date (missing
        // output or changed settings). "Run all" re-runs everything.
        void Start(RemeshPipeline.Stage target, bool all)
        {
            var from = all ? RemeshPipeline.Stage.Remesh : pipeline.FirstStale(target, settings, source);
            // Preview caches key on mesh instances the run is about to destroy.
            previews.Invalidate();
            saveStatus = null;
            _ = ShowWhenDone(pipeline.Run(source, settings, from, target), target);
            // Run() has already changed the sidebar (Cancel button, cleared results);
            // end this event before IMGUI asks for controls the layout pass never
            // registered. Kept synchronous: ExitGUI must unwind this GUI event's stack.
            GUIUtility.ExitGUI();
        }

        // The caller discards the task, so a failure past the pipeline's own catch would
        // otherwise vanish as an unobserved task exception; it is logged here instead,
        // as the former async void did through Unity's synchronization context.
        async Task ShowWhenDone(Task<bool> run, RemeshPipeline.Stage target)
        {
            bool ok;
            try { ok = await run; }
            catch (Exception e) { Debug.LogException(e); saveStatus = e.Message; RequestRepaint?.Invoke(); return; }
            if (ok)
                previews.Show(target == RemeshPipeline.Stage.Remesh ? RemeshPreview.Stage.Remesh :
                    target == RemeshPipeline.Stage.Simplify ? RemeshPreview.Stage.Simplified : RemeshPreview.Stage.Result);
        }

        void Save()
        {
            // Default output: a "remesh" subfolder next to the CAPTURED model's asset (the
            // selection may have moved on since the bake). Falls back to the folder
            // picker for scene-only objects with no asset, or a captured root that is gone.
            string parent = RemeshExporter.ResolveDefaultFolder(pipeline.CapturedSource) ?? RemeshExporter.PickFolder(out saveStatus);
            if (parent == null) return;
            try { saveStatus = RemeshExporter.Export(pipeline, settings, parent); }
            catch (Exception e) { saveStatus = e.Message; }
        }
    }
}
