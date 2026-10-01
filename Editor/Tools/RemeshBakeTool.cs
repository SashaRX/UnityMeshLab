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
    public sealed class RemeshBakeTool : IUvTool, IUvToolRightSidebar
    {
        public string ToolName => "Remesh & Bake";
        public string ToolId => "remesh_bake";
        public int ToolOrder => 35;
        public Action RequestRepaint { private get; set; }

        // Settings survive domain reloads and tab switches so a tuned pipeline
        // does not silently reset to defaults between runs.
        const string SettingsKey = "MeshLab.RemeshBake.Settings";

        static readonly string[] StageTitles = { "1 · Voxel remesh", "2 · Simplify", "3 · Normals & UV", "4 · Bake" };
        static readonly string[] SampleNames = { "1 (off)", "4 (2×2)", "9 (3×3)", "16 (4×4)" };
        static readonly int[] SampleCounts = { 1, 4, 9, 16 };

        GameObject source, lastSelection;
        RemeshSettings settings = new RemeshSettings();
        readonly RemeshPipeline pipeline = new RemeshPipeline();
        readonly bool[] folds = { true, true, true, true };
        bool chartFold;
        readonly RemeshPreview previews = new RemeshPreview();
        readonly RemeshPreview.Data previewData = new RemeshPreview.Data();
        string saveStatus;
        internal GameObject Source => source;

        public RemeshBakeTool()
        {
            string json = EditorPrefs.GetString(SettingsKey, "");
            if (!string.IsNullOrEmpty(json)) {
                var restored = JsonUtility.FromJson<RemeshSettings>(json);
                if (restored != null) settings = restored;
            }
            pipeline.Changed = () => { saveStatus = null; RequestRepaint?.Invoke(); };
        }

        internal void SaveSettings() => EditorPrefs.SetString(SettingsKey, JsonUtility.ToJson(settings));

        public void OnActivate(UvToolContext context, UvCanvasView canvas)
        {
            FollowSelection();
            Selection.selectionChanged -= FollowSelection;
            Selection.selectionChanged += FollowSelection;
            previews.RequestRepaint = () => RequestRepaint?.Invoke();
            // Result/preview objects carry HideAndDontSave, so they survive scene loads but
            // would leak across a domain reload once this instance is discarded.
            AssemblyReloadEvents.beforeAssemblyReload -= SaveSettingsAndClear;
            AssemblyReloadEvents.beforeAssemblyReload += SaveSettingsAndClear;
        }
        public void OnDeactivate()
        {
            Selection.selectionChanged -= FollowSelection;
            AssemblyReloadEvents.beforeAssemblyReload -= SaveSettingsAndClear;
            SaveSettingsAndClear();
        }

        void SaveSettingsAndClear()
        {
            SaveSettings();
            previews.Dispose();
            pipeline.Dispose();
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
            source = root;
            RequestRepaint?.Invoke();
        }
        // Hub context (LODGroup selection, Undo) does not feed this tool: the source
        // snapshot is captured when the remesh stage runs, so a running bake must not be cancelled here.
        public void OnRefresh() { }
        public void OnDrawToolbarExtra() { }
        public void OnDrawStatusBar() { GUILayout.Label(Status, EditorStyles.miniLabel); }
        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes() => null;
        public void OnSceneGUI(SceneView sceneView) { }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float size) { }

        string Status => saveStatus ?? pipeline.Status;

        public void OnDrawRightSidebar()
        {
            previewData.meshes[(int)RemeshPreview.Stage.Source] = pipeline.SourceMesh;
            previewData.meshes[(int)RemeshPreview.Stage.Remesh] = pipeline.VoxelMesh;
            previewData.meshes[(int)RemeshPreview.Stage.Simplified] = pipeline.SimplifiedMesh;
            previewData.meshes[(int)RemeshPreview.Stage.Result] = pipeline.ResultMesh;
            previewData.geometry = pipeline.Geometry; previewData.maps = pipeline.Maps; previewData.baseColor = pipeline.BaseColorPreview;
            // Live from the current settings so the cage preview reflects projection
            // distance changes before a re-bake; zero until a source snapshot exists.
            previewData.cageDistance = pipeline.SourceDiagonal * settings.projectionDistance;
            previews.Draw(previewData);
        }

        public void OnDrawSidebar()
        {
            EditorGUILayout.LabelField("High-poly → Low-poly", EditorStyles.boldLabel);
            bool busy = pipeline.IsRunning;
            using (new EditorGUI.DisabledScope(busy)) {
                source = (GameObject)EditorGUILayout.ObjectField("Source root", source, typeof(GameObject), true);
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
                    settings.minPartThickness = EditorGUILayout.Slider(new GUIContent("Exclude parts thinner than",
                        "Connected pieces whose smallest bounds side is below this fraction of the model's diagonal (decals, glass sheets, fences) are left out. 0 = keep everything."),
                        settings.minPartThickness, 0, 0.1f);
                    settings.lod0Only = EditorGUILayout.Toggle(new GUIContent("LOD0 only",
                        "Capture ignores meshes named Name_LOD1 and higher; LODGroups already contribute LOD0 only."), settings.lod0Only);
                    settings.keepHierarchy = EditorGUILayout.Toggle(new GUIContent("Keep hierarchy",
                        "Remesh every renderer SEPARATELY and save the result as a hierarchy of meshes with per-node baked materials under one root, instead of welding everything into one mesh with one material."), settings.keepHierarchy);
                    using (new EditorGUI.DisabledScope(settings.sourceShape != RemeshShape.LOD0)) {
                        settings.voxelResolution = EditorGUILayout.IntSlider("Voxel resolution", settings.voxelResolution, 4, 256);
                        settings.solve = EditorGUILayout.Toggle("Fit source surface", settings.solve);
                        settings.shell = EditorGUILayout.Toggle("Two-sided shell", settings.shell);
                    }
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
                        "Angle: crease angle. UV islands: hard along island borders, smooth inside. The crease choice also feeds xatlas charting."), settings.hardEdges);
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
                    chartFold = EditorGUILayout.Foldout(chartFold, "Islands & packing", true);
                    if (chartFold) {
                        using (new EditorGUI.IndentLevelScope()) {
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
                    settings.projectionDistance = EditorGUILayout.Slider("Projection / bounds", settings.projectionDistance, 0.001f, 0.2f);
                    settings.bakeSamples = EditorGUILayout.IntPopup(new GUIContent("Samples per texel", "Supersampling for smoother edges and detail."),
                        settings.bakeSamples, Array.ConvertAll(SampleNames, n => new GUIContent(n)), SampleCounts);
                    settings.transferVertexColor = EditorGUILayout.Toggle("Vertex color (RGB)", settings.transferVertexColor);
                    settings.transferVertexAlpha = EditorGUILayout.Toggle("Vertex alpha", settings.transferVertexAlpha);
                    settings.vertexColorTint = EditorGUILayout.Toggle(new GUIContent("Vertex color tints albedo",
                        "Multiply the projected albedo by the source vertex color (RGB), for shaders that use the vertex color as a tint."), settings.vertexColorTint);
                    if (pipeline.Has(RemeshPipeline.Stage.Remesh) && !pipeline.SourceHasColors && (settings.transferVertexColor || settings.transferVertexAlpha || settings.vertexColorTint))
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
            ShowWhenDone(pipeline.Run(source, settings, from, target), target);
            // Run() has already changed the sidebar (Cancel button, cleared results);
            // end this event before IMGUI asks for controls the layout pass never
            // registered. Kept synchronous: ExitGUI must unwind this GUI event's stack.
            GUIUtility.ExitGUI();
        }

        async void ShowWhenDone(Task<bool> run, RemeshPipeline.Stage target)
        {
            if (await run)
                previews.Show(target == RemeshPipeline.Stage.Remesh ? RemeshPreview.Stage.Remesh :
                    target == RemeshPipeline.Stage.Simplify ? RemeshPreview.Stage.Simplified : RemeshPreview.Stage.Result);
        }

        void Save()
        {
            // Default output: a "remesh" subfolder next to the source model's asset.
            // Falls back to the folder picker for scene-only objects with no asset.
            string parent = RemeshExporter.ResolveDefaultFolder(source) ?? RemeshExporter.PickFolder(out saveStatus);
            if (parent == null) return;
            try { saveStatus = RemeshExporter.Export(pipeline, settings, parent); }
            catch (Exception e) { saveStatus = e.Message; }
        }
    }
}
