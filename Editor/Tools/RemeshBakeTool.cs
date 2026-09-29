using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    public sealed class RemeshBakeTool : IUvTool, IUvToolRightSidebar
    {
        public string ToolName => "Remesh & Bake";
        public string ToolId => "remesh_bake";
        public int ToolOrder => 35;
        public Action RequestRepaint { private get; set; }

        // Settings survive domain reloads and tab switches so a tuned pipeline
        // does not silently reset to defaults between runs.
        const string SettingsKey = "MeshLab.RemeshBake.Settings";

        enum Stage { Remesh, Simplify, Unwrap, Bake }
        static readonly string[] StageTitles = { "1 · Voxel remesh", "2 · Simplify", "3 · Normals & UV", "4 · Bake" };
        static readonly string[] SampleNames = { "1 (off)", "4 (2×2)", "9 (3×3)", "16 (4×4)" };
        static readonly int[] SampleCounts = { 1, 4, 9, 16 };

        GameObject source, lastSelection;
        RemeshSettings settings = new RemeshSettings();
        CancellationTokenSource cancellation;
        static int running;
        string status = "Select a static model root. LODGroups contribute only LOD0.";
        string resultName;
        internal GameObject Source => source;

        public RemeshBakeTool()
        {
            string json = EditorPrefs.GetString(SettingsKey, "");
            if (string.IsNullOrEmpty(json)) return;
            var restored = JsonUtility.FromJson<RemeshSettings>(json);
            if (restored != null) settings = restored;
        }

        internal void SaveSettings() => EditorPrefs.SetString(SettingsKey, JsonUtility.ToJson(settings));

        // Stage outputs. Each stage consumes the previous one; re-running a stage clears
        // everything after it. keys[] remember the settings each output was built with.
        RemeshSource snapshot;
        RemeshNative.IndexedMesh voxel, simplified;
        float simplifyError;
        RemeshNative.Geometry geometry;
        Vector4[] tangents;
        RemeshBaker.Maps maps;
        // Keep-hierarchy mode (settings.keepHierarchy): per-node stage outputs. Each entry
        // carries its own capture → voxel → simplified → unwrapped → baked chain; the node's
        // transform is kept relative to the source root so Save can rebuild the hierarchy.
        internal sealed class RemeshNode
        {
            public string name;
            public Vector3 localPosition = Vector3.zero;
            public Quaternion localRotation = Quaternion.identity;
            public Vector3 localScale = Vector3.one;
            public RemeshSource source;
            public RemeshNative.IndexedMesh voxel;
            public RemeshNative.Geometry simplified;
            public RemeshNative.Geometry geometry;
            public Vector4[] tangents;
            public Mesh mesh;
            public RemeshBaker.Maps maps;
        }
        List<RemeshNode> nodes;
        Mesh sourceMesh, voxelMesh, simplifiedMesh, resultMesh;
        Texture2D preview;
        readonly string[] keys = new string[4];
        readonly bool[] folds = { true, true, true, true };
        bool chartFold;
        readonly RemeshPreview previews = new RemeshPreview();
        readonly RemeshPreview.Data previewData = new RemeshPreview.Data();

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
            SaveSettings();
            cancellation?.Cancel(); Clear();
        }

        void SaveSettingsAndClear()
        {
            SaveSettings();
            Clear();
        }

        // Source root follows the hierarchy selection, as the status text asks. A pick
        // made in the field holds until the selection changes, also across tab
        // switches. A LOD child resolves to its LODGroup so the bake keeps the
        // LOD0-only contract. Selections without MeshRenderers (lights, cameras) and
        // selections made during a bake leave the source as is.
        internal void FollowSelection()
        {
            var selected = Selection.activeGameObject;
            if (selected == lastSelection) return;
            lastSelection = selected;
            if (!selected || Volatile.Read(ref running) != 0) return;
            var group = selected.GetComponentInParent<LODGroup>();
            var root = group ? group.gameObject : selected;
            if (!root.GetComponentInChildren<MeshRenderer>()) return;
            source = root;
            RequestRepaint?.Invoke();
        }
        // Hub context (LODGroup selection, Undo) does not feed this tool: the source
        // snapshot is captured when the remesh stage runs, so a running bake must not be cancelled here.
        public void OnRefresh() { }
        public void OnDrawToolbarExtra() { }
        public void OnDrawStatusBar() { GUILayout.Label(status, EditorStyles.miniLabel); }
        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes() => null;
        public void OnSceneGUI(SceneView sceneView) { }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float size) { }

        public void OnDrawRightSidebar()
        {
            previewData.meshes[(int)RemeshPreview.Stage.Source] = sourceMesh;
            previewData.meshes[(int)RemeshPreview.Stage.Remesh] = voxelMesh;
            previewData.meshes[(int)RemeshPreview.Stage.Simplified] = simplifiedMesh;
            previewData.meshes[(int)RemeshPreview.Stage.Result] = resultMesh;
            previewData.geometry = geometry; previewData.maps = maps; previewData.baseColor = preview;
            // Live from the current settings so the cage preview reflects projection
            // distance changes before a re-bake; zero until a source snapshot exists.
            previewData.cageDistance = snapshot != null ? snapshot.diagonal * settings.projectionDistance : 0;
            previews.Draw(previewData);
        }

        public void OnDrawSidebar()
        {
            EditorGUILayout.LabelField("High-poly → Low-poly", EditorStyles.boldLabel);
            bool busy = Volatile.Read(ref running) != 0;
            using (new EditorGUI.DisabledScope(busy)) {
                source = (GameObject)EditorGUILayout.ObjectField("Source root", source, typeof(GameObject), true);
                using (new EditorGUI.DisabledScope(!source || UvProgress.IsActive))
                    if (GUILayout.Button("Run all stages", GUILayout.Height(26))) Start(Stage.Bake, true);

                if (StageHeader(Stage.Remesh, voxel != null ? $"{voxel.TriangleCount:N0} tris" : null)) {
                    settings.lod0Only = EditorGUILayout.Toggle(new GUIContent("LOD0 only",
                        "Capture ignores meshes named Name_LOD1 and higher; LODGroups already contribute LOD0 only."), settings.lod0Only);
                    settings.keepHierarchy = EditorGUILayout.Toggle(new GUIContent("Keep hierarchy",
                        "Remesh every node SEPARATELY and save the result as a hierarchy of meshes with per-node baked materials under one root, instead of welding everything into one mesh with one material."), settings.keepHierarchy);
                    settings.voxelResolution = EditorGUILayout.IntSlider("Voxel resolution", settings.voxelResolution, 4, 256);
                    settings.solve = EditorGUILayout.Toggle("Fit source surface", settings.solve);
                    settings.shell = EditorGUILayout.Toggle("Two-sided shell", settings.shell);
                    StageButton(Stage.Remesh, "Remesh");
                }
                if (StageHeader(Stage.Simplify, simplified != null ? $"{simplified.TriangleCount:N0} tris" : null)) {
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
                    if (simplified != null && settings.simplify) EditorGUILayout.LabelField($"Achieved error {simplifyError:0.#####}", EditorStyles.miniLabel);
                    StageButton(Stage.Simplify, "Simplify");
                }
                if (StageHeader(Stage.Unwrap, geometry != null ? $"{geometry.chartCount:N0} islands" : null)) {
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
                    StageButton(Stage.Unwrap, "Unwrap");
                }
                if (StageHeader(Stage.Bake, maps != null ? (maps.misses > 0 ? $"{maps.misses:N0} missed" : "done") : null)) {
                    settings.projectionDistance = EditorGUILayout.Slider("Projection / bounds", settings.projectionDistance, 0.001f, 0.2f);
                    settings.bakeSamples = EditorGUILayout.IntPopup(new GUIContent("Samples per texel", "Supersampling for smoother edges and detail."),
                        settings.bakeSamples, Array.ConvertAll(SampleNames, n => new GUIContent(n)), SampleCounts);
                    settings.transferVertexColor = EditorGUILayout.Toggle("Vertex color (RGB)", settings.transferVertexColor);
                    settings.transferVertexAlpha = EditorGUILayout.Toggle("Vertex alpha", settings.transferVertexAlpha);
                    if (snapshot != null && !snapshot.hasColors && (settings.transferVertexColor || settings.transferVertexAlpha))
                        EditorGUILayout.HelpBox("The source has no vertex colors; the transfer writes white.", MessageType.None);
                    StageButton(Stage.Bake, "Bake");
                }
                EditorGUILayout.HelpBox("Experimental voxel remesh. Thin details and small gaps may disappear.", MessageType.Info);
            }
            if (cancellation != null)
                using (new EditorGUI.DisabledScope(cancellation.IsCancellationRequested))
                    if (GUILayout.Button("Cancel (after current native phase)")) {
                        cancellation.Cancel(); status = "Cancelling after the current phase…";
                    }
            EditorGUILayout.HelpBox(status, maps != null && maps.misses > 0 ? MessageType.Warning : MessageType.None);
            if (resultMesh && maps != null) {
                EditorGUILayout.LabelField($"{resultMesh.vertexCount:N0} vertices · {resultMesh.GetIndexCount(0) / 3:N0} triangles");
                settings.normalizeSize = EditorGUILayout.Toggle(new GUIContent("Normalize size (saved at scale 1)",
                    "Bakes the source's world scale into the saved geometry, so the model keeps its real size with a " +
                    "scale-1 transform regardless of how the source is scaled. Off: the saved transform carries the " +
                    "source's scale instead."),
                    settings.normalizeSize);
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                settings.embedFbxTextures = EditorGUILayout.Toggle(new GUIContent("Embed textures in FBX",
                    "Self-contained FBX that carries its maps — portable to other machines, but the file grows by the map sizes " +
                    "(the float EXR emission map alone is 16 bytes per texel). Off: the FBX links the exported maps by absolute path."),
                    settings.embedFbxTextures);
#endif
                using (new EditorGUI.DisabledScope(cancellation != null))
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                    if (GUILayout.Button("Save FBX, maps & prefab…")) {
#else
                    if (GUILayout.Button("Save mesh, maps & prefab…")) {
#endif
                        Save();
                        // The folder panel is modal and the export imports assets; both
                        // leave this event's layout state stale.
                        GUIUtility.ExitGUI();
                    }
#if !LIGHTMAP_UV_TOOL_FBX_EXPORTER
                EditorGUILayout.HelpBox("Install com.unity.formats.fbx to save the model as an FBX instead of a mesh asset.", MessageType.None);
#endif
            }
        }

        bool StageHeader(Stage stage, string summary)
        {
            int i = (int)stage;
            bool stale = keys[i] != null && keys[i] != Key(stage);
            string label = StageTitles[i] + (summary == null ? "" : "   —   " + summary + (stale ? " (settings changed)" : ""));
            EditorGUILayout.Space(3);
            folds[i] = EditorGUILayout.Foldout(folds[i], label, true, EditorStyles.foldoutHeader);
            return folds[i];
        }

        void StageButton(Stage stage, string label)
        {
            using (new EditorGUI.DisabledScope(!source && snapshot == null || UvProgress.IsActive))
                if (GUILayout.Button(label)) Start(stage, false);
        }

        // Runs the clicked stage, first bringing every earlier stage up to date (missing
        // output or changed settings). "Run all" re-runs everything.
        void Start(Stage target, bool all)
        {
            Stage from = Stage.Remesh;
            if (!all) {
                from = target;
                // An emptied Source field does not invalidate a remesh already captured.
                for (var s = Stage.Remesh; s < target; ++s)
                    if (keys[(int)s] == null || keys[(int)s] != Key(s) && (s != Stage.Remesh || source)) { from = s; break; }
            }
            Run(from, target);
            // Run() has already changed the sidebar (Cancel button, cleared results);
            // end this event before IMGUI asks for controls the layout pass never registered.
            GUIUtility.ExitGUI();
        }

        string Key(Stage stage)
        {
            var s = settings;
            switch (stage) {
                case Stage.Remesh: return $"{(source ? source.GetInstanceID() : 0)}|{s.voxelResolution}|{s.solve}|{s.shell}|{s.lod0Only}|{s.keepHierarchy}";
                case Stage.Simplify: return $"{s.simplify}|{s.targetTriangles}|{s.maximumError}|{s.regularize}|{s.preserveFolds}|{s.pruneSmallParts}";
                case Stage.Unwrap: return $"{s.hardEdges}|{s.normalCrease}|{s.normalSmoothing}|{s.normalWeighting}|{s.textureResolution}|{s.padding}|{s.chartMaxCost}|" +
                    $"{s.chartNormalDeviation}|{s.chartNormalSeam}|{s.chartStraightness}|{s.chartRoundness}|{s.chartIterations}|" +
                    $"{s.maxChartArea}|{s.maxChartBoundary}|{s.packRotate}|{s.packBlockAlign}|{s.packBruteForce}";
                default: return $"{s.projectionDistance}|{s.bakeSamples}|{s.transferVertexColor}|{s.transferVertexAlpha}";
            }
        }

        async void Run(Stage from, Stage to)
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            bool locked = false;
            try {
                settings.Validate(); RemeshNative.CheckAvailable();
                var options = JsonUtility.FromJson<RemeshSettings>(JsonUtility.ToJson(settings));
                ClearFrom(from);
                EditorApplication.LockReloadAssemblies(); locked = true;
                for (var stage = from; stage <= to; ++stage) {
                    string key = Key(stage);
                    switch (stage) {
                        case Stage.Remesh: await RunRemesh(options, token); break;
                        case Stage.Simplify: await RunSimplify(options, token); break;
                        case Stage.Unwrap: await RunUnwrap(options, token); break;
                        default: await RunBake(options, token); break;
                    }
                    keys[(int)stage] = key;
                }
                previews.Show(to == Stage.Remesh ? RemeshPreview.Stage.Remesh :
                    to == Stage.Simplify ? RemeshPreview.Stage.Simplified : RemeshPreview.Stage.Result);
            }
            catch (OperationCanceledException) { status = "Cancelled. Source assets were preserved."; }
            catch (Exception e) { status = e.Message; UvtLog.Error("[Remesh] " + e); }
            finally {
                cancellation.Dispose(); cancellation = null;
                if (locked) EditorApplication.UnlockReloadAssemblies();
                Interlocked.Exchange(ref running, 0); RequestRepaint?.Invoke();
            }
        }

        async Task RunRemesh(RemeshSettings options, CancellationToken token)
        {
            var root = source;
            if (!root) throw new InvalidOperationException("Select a source root.");
            Report("Reading source geometry and textures…");
            await Task.Yield(); token.ThrowIfCancellationRequested();
            if (options.keepHierarchy) {
                await RunRemeshHierarchy(root, options, token);
                return;
            }
            var captured = RemeshSource.Capture(root, options.lod0Only);
            foreach (var warning in captured.warnings) UvtLog.Warn("[Remesh] " + warning);
            Report("Voxel remeshing…");
            var mesh = await Task.Run(() => RemeshNative.Voxelize(captured.positions, captured.indices, options, token));
            token.ThrowIfCancellationRequested();
            snapshot = captured; resultName = root.name; voxel = mesh;
            sourceMesh = BuildMesh(root.name + "_Source", captured.positions, captured.indices, captured.normals, captured.hasColors ? captured.colors : null);
            voxelMesh = BuildMesh(root.name + "_Voxel", mesh.positions, mesh.indices);
            status = $"Remesh: {captured.indices.Length / 3:N0} → {mesh.TriangleCount:N0} triangles." +
                (captured.warnings.Length > 0 ? $" {captured.warnings.Length} material warning(s), see Console." : "");
        }

        // Keep-hierarchy capture: one node per RENDERER that Capture would have folded into the
        // weld (same filters: active, non-collision, LOD0), each captured in its own local space
        // with its own subtree. The node's transform relative to the source root is kept so
        // Save can rebuild the hierarchy.
        async Task RunRemeshHierarchy(GameObject root, RemeshSettings options, CancellationToken token)
        {
            var renderers = CollectHierarchyRenderers(root, options.lod0Only);
            if (renderers.Count == 0) throw new InvalidOperationException("No remeshable renderers under the root.");
            nodes = new List<RemeshNode>(renderers.Count);
            long sourceTriangles = 0, resultTriangles = 0; int warnings = 0;
            for (int i = 0; i < renderers.Count; ++i) {
                var renderer = renderers[i];
                token.ThrowIfCancellationRequested();
                Report($"Reading {renderer.name} ({i + 1}/{renderers.Count})…");
                var captured = RemeshSource.Capture(renderer.gameObject, options.lod0Only);
                foreach (var warning in captured.warnings) { UvtLog.Warn("[Remesh] " + warning); ++warnings; }
                if (captured.indices.Length == 0) {
                    UvtLog.Warn("[Remesh] " + renderer.name + ": nothing to remesh, skipped.");
                    continue;
                }
                Report($"Voxel remeshing {renderer.name} ({i + 1}/{renderers.Count})…");
                var nodeVoxel = await Task.Run(() => RemeshNative.Voxelize(captured.positions, captured.indices, options, token));
                var toRoot = root.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                nodes.Add(new RemeshNode {
                    name = renderer.name,
                    localPosition = toRoot.GetColumn(3),
                    localRotation = toRoot.rotation,
                    localScale = toRoot.lossyScale,
                    source = captured,
                    voxel = nodeVoxel,
                });
                sourceTriangles += captured.indices.Length / 3;
                resultTriangles += nodeVoxel.TriangleCount;
            }
            if (nodes.Count == 0) throw new InvalidOperationException("Every captured node was empty; nothing to remesh.");
            // Previews come from the largest node so the existing preview panel keeps working;
            // the single-path snapshot/voxel fields stay null (Save branches on `nodes`).
            var largest = LargestNode();
            resultName = root.name;
            snapshot = null; voxel = null;
            sourceMesh = BuildMesh(root.name + "_Source", largest.source.positions, largest.source.indices,
                largest.source.normals, largest.source.hasColors ? largest.source.colors : null);
            voxelMesh = BuildMesh(root.name + "_Voxel", largest.voxel.positions, largest.voxel.indices);
            status = $"Remesh: {nodes.Count} node(s), {sourceTriangles:N0} → {resultTriangles:N0} triangles." +
                (warnings > 0 ? $" {warnings} material warning(s), see Console." : "");
        }

        internal static List<Renderer> CollectHierarchyRenderers(GameObject root, bool lod0Only)
        {
            var excluded = new HashSet<Renderer>();
            foreach (var group in root.GetComponentsInChildren<LODGroup>()) {
                var lods = group.GetLODs();
                var first = new HashSet<Renderer>(lods.Length > 0 ? lods[0].renderers : Array.Empty<Renderer>());
                for (int l = 1; l < lods.Length; ++l)
                    foreach (var r in lods[l].renderers) if (!first.Contains(r)) excluded.Add(r);
            }
            var result = new List<Renderer>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>()) {
                if (!renderer.enabled || excluded.Contains(renderer) || MeshHygieneUtility.IsCollisionNodeName(renderer.name)) continue;
                if (lod0Only && RemeshSource.IsHigherLodName(renderer.name)) continue;
                if (renderer is SkinnedMeshRenderer || renderer.GetComponent<MeshFilter>()?.sharedMesh != null) result.Add(renderer);
            }
            return result;
        }

        RemeshNode LargestNode()
        {
            RemeshNode best = null;
            foreach (var node in nodes)
                if (best == null || node.voxel.TriangleCount > best.voxel.TriangleCount) best = node;
            return best;
        }

        async Task RunSimplify(RemeshSettings options, CancellationToken token)
        {
            if (voxel == null && nodes == null) throw new InvalidOperationException("Run the remesh stage first.");
            if (nodes != null) {
                long before = 0, after = 0;
                for (int i = 0; i < nodes.Count; ++i) {
                    var node = nodes[i];
                    token.ThrowIfCancellationRequested();
                    Report($"Simplifying {node.name} ({i + 1}/{nodes.Count})…");
                    if (options.simplify) {
                        float error = 0;
                        node.simplified = await Task.Run(() => RemeshNative.Simplify(node.voxel, options, token, out error), token);
                        before += node.voxel.TriangleCount; after += node.simplified.TriangleCount;
                    }
                    else { node.simplified = node.voxel; before += node.voxel.TriangleCount; after += node.simplified.TriangleCount; }
                }
                simplifyError = options.simplify ? options.maximumError : 0;
                var largest = LargestNode();
                simplifiedMesh = BuildMesh(resultName + "_Simplified", largest.simplified.positions, largest.simplified.indices);
                status = $"Simplify: {nodes.Count} node(s), {before:N0} → {after:N0} triangles.";
                return;
            }
            var input = voxel;
            if (options.simplify) {
                Report("Simplifying…");
                float error = 0;
                simplified = await Task.Run(() => RemeshNative.Simplify(input, options, token, out error));
                simplifyError = error;
            }
            else { simplified = input; simplifyError = 0; }
            token.ThrowIfCancellationRequested();
            simplifiedMesh = BuildMesh(resultName + "_Simplified", simplified.positions, simplified.indices);
            status = $"Simplify: {input.TriangleCount:N0} → {simplified.TriangleCount:N0} triangles.";
        }

        async Task RunUnwrap(RemeshSettings options, CancellationToken token)
        {
            if (simplified == null && nodes == null) throw new InvalidOperationException("Run the simplify stage first.");
            if (nodes != null) {
                int charts = 0, vertices = 0;
                for (int i = 0; i < nodes.Count; ++i) {
                    var node = nodes[i];
                    token.ThrowIfCancellationRequested();
                    Report($"Generating normals and unwrapping UVs on {node.name} ({i + 1}/{nodes.Count})…");
                    node.geometry = await Task.Run(() => RemeshNative.Unwrap(node.simplified, options, token), token);
                    node.mesh = BuildResultMesh(node.name + "_LOD0", node.geometry, out var nativeTangents, out _);
                    node.tangents = nativeTangents;
                    charts += node.geometry.chartCount; vertices += node.geometry.positions.Length;
                }
                // The result preview shows the largest node; resultMesh is a COPY so clearing
                // the single-path outputs never destroys a node's own mesh.
                var largest = LargestNode();
                resultMesh = Object.Instantiate(largest.mesh);
                resultMesh.name = resultName + "_LOD0";
                tangents = null;
                status = $"Unwrap: {nodes.Count} node(s), {charts:N0} islands, {vertices:N0} vertices.";
                return;
            }
            var input = simplified;
            Report("Generating normals and unwrapping UVs…");
            var unwrapped = await Task.Run(() => RemeshNative.Unwrap(input, options, token));
            token.ThrowIfCancellationRequested();
            geometry = unwrapped;
            resultMesh = BuildResultMesh(resultName + "_LOD0", unwrapped, out tangents, out _);
            status = $"Unwrap: {unwrapped.chartCount:N0} islands, {unwrapped.positions.Length:N0} vertices.";
        }

        // Builds a display/result Mesh from unwrapped geometry. The tangent frame comes from
        // meshoptimizer over the final UV layout (MikkT-compatible, the same basis the bake
        // encodes against); Unity's recalculation is only a fallback if the native tangents
        // are missing.
        static Mesh BuildResultMesh(string name, RemeshNative.Geometry unwrapped, out Vector4[] tangents, out bool anyTangent)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = unwrapped.positions; mesh.normals = unwrapped.normals;
            mesh.uv = unwrapped.uv; mesh.triangles = unwrapped.indices;
            mesh.RecalculateBounds();
            anyTangent = false;
            if (unwrapped.tangents != null)
                for (int i = 0; i < unwrapped.tangents.Length && !anyTangent; ++i)
                    anyTangent = unwrapped.tangents[i].sqrMagnitude > 1e-12f;
            if (anyTangent) mesh.tangents = unwrapped.tangents;
            else {
                mesh.RecalculateTangents();
                UvtLog.Warn("[Remesh] Native tangents missing; fell back to Unity's recalculation. Rebuild the native plugins for this commit.");
            }
            tangents = mesh.tangents;
            return mesh;
        }

        async Task RunBake(RemeshSettings options, CancellationToken token)
        {
            if ((geometry == null || snapshot == null) && nodes == null) throw new InvalidOperationException("Run the UV stage first.");
            if (nodes != null) {
                long sourceTriangles = 0, targetTriangles = 0, misses = 0, covered = 0;
                for (int i = 0; i < nodes.Count; ++i) {
                    var node = nodes[i];
                    token.ThrowIfCancellationRequested();
                    Report($"Projecting {node.name} into its UV atlas ({i + 1}/{nodes.Count})…");
                    var baked = await Task.Run(() => RemeshBaker.Bake(node.source, node.geometry, node.tangents, options, token), token);
                    node.maps = baked;
                    if (baked.vertexColors != null) node.mesh.colors = baked.vertexColors;
                    sourceTriangles += node.source.indices.Length / 3;
                    targetTriangles += node.geometry.indices.Length / 3;
                    misses += baked.misses; covered += baked.covered;
                }
                var largest = LargestNode();
                preview = new Texture2D(largest.maps.size, largest.maps.size, TextureFormat.RGBA32, false, false) { hideFlags = HideFlags.HideAndDontSave };
                preview.SetPixels32(largest.maps.color); preview.Apply();
                maps = largest.maps;
                status = $"{nodes.Count} node(s): {sourceTriangles:N0} → {targetTriangles:N0} triangles. " +
                    (misses == 0 ? "All covered texels projected." : $"{misses:N0} / {covered:N0} texels missed (magenta). Increase projection distance and rebake.");
                UvtLog.Info("[Remesh] " + status);
                return;
            }
            var captured = snapshot; var target = geometry; var frame = tangents;
            Report("Projecting source materials into the new UV atlas…");
            var baked = await Task.Run(() => RemeshBaker.Bake(captured, target, frame, options, token));
            token.ThrowIfCancellationRequested();
            preview = new Texture2D(baked.size, baked.size, TextureFormat.RGBA32, false, false) { hideFlags = HideFlags.HideAndDontSave };
            preview.SetPixels32(baked.color); preview.Apply();
            maps = baked;
            if (baked.vertexColors != null) resultMesh.colors = baked.vertexColors;
            status = $"{captured.indices.Length / 3:N0} → {target.indices.Length / 3:N0} triangles. " +
                (baked.misses == 0 ? "All covered texels projected." : $"{baked.misses:N0} / {baked.covered:N0} texels missed (magenta). Increase projection distance and rebake.") +
                (captured.warnings.Length > 0 ? $" {captured.warnings.Length} material warning(s), see Console." : "");
            UvtLog.Info("[Remesh] " + status);
            // Source and target live in the same root-local space by construction; the
            // diagonal ratio below proves it at a glance and catches scale regressions.
            Vector3 mn = target.positions[0], mx = target.positions[0];
            foreach (var pv in target.positions) { mn = Vector3.Min(mn, pv); mx = Vector3.Max(mx, pv); }
            float targetDiagonal = (mx - mn).magnitude;
            float scaleRatio = targetDiagonal / Mathf.Max(1e-8f, captured.diagonal);
            // Bake health counters (RemeshDiag log category): cage welding, one-sided
            // border normals, nearest-query fallbacks and normal-map tilt. A loud,
            // strongly-tilted map on a smooth-ish source is the signature of
            // displaced projection samples; loud fraction > 5% also warns on its own.
            if (UvtLog.IsCategoryEnabled(UvtLog.Category.RemeshDiag))
                UvtLog.Info(UvtLog.Category.RemeshDiag,
                    $"cage: {baked.weldedPositions:N0} welded positions ({baked.splitCopies:N0} split copies), " +
                    $"{baked.oneSidedNormals:N0} one-sided border normals, max cage deviation {baked.maxOneSidedDeg:F0}°, {baked.zeroNormals:N0} zero normals; " +
                    $"projection: {baked.rayFallbacks:N0} nearest-fallback samples, {baked.misses:N0} missed texels, front-face filter {(baked.facingFilter ? "on" : "off")}; " +
                    $"normal map tilt: mean {baked.meanTiltDeg:F1}° / max {baked.maxTiltDeg:F0}°, {baked.loudTexels:N0} texels >45°; " +
                    $"bounds diagonal: source {captured.diagonal:F3} / target {targetDiagonal:F3} (ratio {scaleRatio:F2})");
            if (baked.zeroNormals > 0)
                UvtLog.Warn(UvtLog.Category.RemeshDiag,
                    baked.zeroNormals + " result vertices have zero normals — their texels bake through zeroed ray directions " +
                    "and tangent frames. Re-run the UV stage; if it repeats, the remesh produced degenerate faces (lower simplification error or raise voxel resolution).");
            else if (baked.rayFallbacks > baked.covered * 4 / 5 && baked.covered > 0)
                UvtLog.Warn(UvtLog.Category.RemeshDiag,
                    baked.rayFallbacks.ToString("N0") + " of the projection samples fell back to nearest-point search — the rays " +
                    "are not hitting the source. Check the projection distance and the hard-edge mode, and rebake.");
            if (Mathf.Abs(scaleRatio - 1f) > 0.1f)
                UvtLog.Warn(UvtLog.Category.RemeshDiag,
                    $"target/source bounds diagonal ratio is {scaleRatio:F2} — the remeshed mesh no longer matches the source size. " +
                    "Check voxel resolution, small-part pruning and simplification settings, and rebake.");
            if (baked.loudTexels > baked.covered / 20 && baked.meanTiltDeg > 30f)
                UvtLog.Warn(UvtLog.Category.RemeshDiag,
                    $"{100.0 * baked.loudTexels / Mathf.Max(1, baked.covered):F1}% of texels lean >45° with a {baked.meanTiltDeg:F0}° mean tilt — " +
                    "the map is dominated by extreme normals. Check the hard-edge mode, projection distance and cage fit, " +
                    "and compare against the source: fine detail should tilt a map, not saturate it.");
        }

        void Report(string message) { status = message; RequestRepaint?.Invoke(); }

        static Mesh BuildMesh(string name, Vector3[] positions, int[] indices, Vector3[] normals = null, Color[] colors = null)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = positions; mesh.triangles = indices;
            if (normals != null) mesh.normals = normals; else mesh.RecalculateNormals();
            if (colors != null) mesh.colors = colors;
            mesh.RecalculateBounds();
            return mesh;
        }

        // Drops the output of `stage` and every stage after it.
        void ClearFrom(Stage stage)
        {
            previews.Invalidate();
            if (stage <= Stage.Bake) {
                if (preview) Object.DestroyImmediate(preview);
                preview = null; maps = null; keys[(int)Stage.Bake] = null;
                if (resultMesh && stage == Stage.Bake) resultMesh.colors = null;
            }
            if (stage <= Stage.Unwrap) {
                if (resultMesh) Object.DestroyImmediate(resultMesh);
                resultMesh = null; geometry = null; tangents = null; keys[(int)Stage.Unwrap] = null;
            }
            if (stage <= Stage.Simplify) {
                if (simplifiedMesh) Object.DestroyImmediate(simplifiedMesh);
                simplifiedMesh = null; simplified = null; keys[(int)Stage.Simplify] = null;
            }
            if (stage <= Stage.Remesh) {
                if (voxelMesh) Object.DestroyImmediate(voxelMesh);
                if (sourceMesh) Object.DestroyImmediate(sourceMesh);
                voxelMesh = null; sourceMesh = null; voxel = null; snapshot = null; keys[(int)Stage.Remesh] = null;
                if (nodes != null) {
                    foreach (var node in nodes) if (node.mesh) Object.DestroyImmediate(node.mesh);
                    nodes = null;
                }
            }
        }

        void Clear()
        {
            ClearFrom(Stage.Remesh);
            previews.Dispose();
        }

        void Save()
        {
            // Default output: a "remesh" subfolder next to the source model's asset.
            // Falls back to the folder picker for scene-only objects with no asset.
            string relative = ResolveSourceRemeshFolder();
            if (relative == null) {
                string parent = EditorUtility.OpenFolderPanel("Save Remesh Result", Application.dataPath, "");
                if (string.IsNullOrEmpty(parent)) return;
                parent = parent.Replace('\\', '/');
                string assets = Application.dataPath.Replace('\\', '/');
                if (parent != assets && !parent.StartsWith(assets + "/", StringComparison.Ordinal)) {
                    status = "Choose a folder inside Assets."; return;
                }
                relative = "Assets" + parent.Substring(assets.Length);
                // The folder panel can create a directory the AssetDatabase has not imported
                // yet; GenerateUniqueAssetPath/CreateFolder then fail on the unknown parent.
                if (!AssetDatabase.IsValidFolder(relative)) AssetDatabase.Refresh();
                if (!AssetDatabase.IsValidFolder(relative)) {
                    status = relative + " is not an imported asset folder. Refresh the Project window and save again."; return;
                }
            }
            string clean = resultName;
            foreach (char c in Path.GetInvalidFileNameChars()) clean = clean.Replace(c, '_');
            clean = clean.Replace('/', '_').Replace('\\', '_');
            if (string.IsNullOrWhiteSpace(clean)) clean = "Remesh";
            string folder = AssetDatabase.GenerateUniqueAssetPath(relative + "/" + clean + "_Remesh");
            GameObject temporary = null;
            Mesh mesh = null; Material material = null;
            bool createdFolder = false;
            try {
                string shaderName = GraphicsSettings.currentRenderPipeline == null ? "Standard" : "Universal Render Pipeline/Lit";
                if (GraphicsSettings.currentRenderPipeline != null &&
                    !GraphicsSettings.currentRenderPipeline.GetType().Name.Contains("Universal"))
                    throw new InvalidOperationException("Result materials support Built-in and URP; HDRP export is not implemented.");
                var shader = Shader.Find(shaderName);
                if (!shader) throw new InvalidOperationException("Result shader is unavailable: " + shaderName);
                string guid = AssetDatabase.CreateFolder(relative, Path.GetFileName(folder));
                if (string.IsNullOrEmpty(guid)) throw new IOException("Could not create " + folder + ".");
                createdFolder = true;
                folder = AssetDatabase.GUIDToAssetPath(guid);
                // Keep-hierarchy results export through their own per-node path: one baked
                // material + mesh asset per node under a rebuilt root prefab. The FBX lane
                // stays single-mesh (the weld) — a multi-node FBX round-trip would re-import
                // per-node normals/mappings for no gain over the native prefab.
                if (settings.keepHierarchy && nodes != null) {
                    SaveHierarchy(folder, clean, shader);
                    return;
                }
                string[] names = { "BaseColor", "Normal", "MetallicSmoothness", "Occlusion", "Emission" };
                var paths = new string[names.Length];
                using (new AssetDatabase.AssetEditingScope()) {
                    for (int i = 0; i < paths.Length; ++i) {
                        paths[i] = folder + "/" + names[i] + (i == 4 ? ".exr" : ".png");
                        // These are new exports in a uniquely created folder, never source-asset writes.
                        File.WriteAllBytes(paths[i], Encode(i));
                        AssetDatabase.ImportAsset(paths[i]);
                    }
                }
                using (new AssetDatabase.AssetEditingScope()) {
                    for (int i = 0; i < paths.Length; ++i) {
                        var importer = (TextureImporter)AssetImporter.GetAtPath(paths[i]);
                        if (importer == null) throw new IOException("Texture import failed: " + paths[i]);
                        importer.textureType = i == 1 ? TextureImporterType.NormalMap : TextureImporterType.Default;
                        importer.sRGBTexture = i == 0; importer.wrapMode = TextureWrapMode.Clamp;
                        importer.maxTextureSize = maps.size; importer.textureCompression = TextureImporterCompression.Uncompressed;
                        importer.mipmapEnabled = true; importer.alphaSource = TextureImporterAlphaSource.FromInput;
                        importer.SaveAndReimport();
                    }
                }
                mesh = Object.Instantiate(resultMesh); mesh.name = clean + "_LOD0"; mesh.hideFlags = HideFlags.None;
                // The result mesh is expressed in the source root's local space. Normalized
                // saves bake the root's world scale into the vertices so the exported root
                // sits at scale 1 and the model keeps its real size; otherwise the node
                // carries the scale (a scaled source would otherwise save at root-local size).
                Vector3 worldScale = source ? source.transform.lossyScale : Vector3.one;
                if (settings.normalizeSize) BakeScaleIntoMesh(mesh, worldScale);
                material = new Material(shader) { name = clean + "_Remesh" };
                bool urp = shaderName != "Standard";
                material.SetTexture(urp ? "_BaseMap" : "_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(paths[0]));
                material.SetColor(urp ? "_BaseColor" : "_Color", Color.white);
                material.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(paths[1]));
                material.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(paths[2]));
                material.SetTexture("_OcclusionMap", AssetDatabase.LoadAssetAtPath<Texture2D>(paths[3]));
                material.SetTexture("_EmissionMap", AssetDatabase.LoadAssetAtPath<Texture2D>(paths[4]));
                material.SetColor("_EmissionColor", Color.white);
                material.SetFloat("_Metallic", 1); material.SetFloat(urp ? "_Smoothness" : "_GlossMapScale", 1);
                material.SetFloat("_BumpScale", 1); material.SetFloat("_OcclusionStrength", 1);
                material.EnableKeyword("_NORMALMAP"); material.EnableKeyword("_EMISSION");
                material.EnableKeyword(urp ? "_METALLICSPECGLOSSMAP" : "_METALLICGLOSSMAP");
                if (urp) material.EnableKeyword("_OCCLUSIONMAP");
                temporary = new GameObject(clean + "_LOD0") { hideFlags = HideFlags.HideAndDontSave };
                temporary.AddComponent<MeshFilter>().sharedMesh = mesh;
                temporary.AddComponent<MeshRenderer>().sharedMaterial = material;
                temporary.hideFlags = HideFlags.None;
                temporary.transform.localScale = settings.normalizeSize ? Vector3.one : worldScale;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                // The model ships as an FBX (geometry, split normals, UV0, tangents,
                // vertex colors); the material stays a curated asset next to it that
                // the prefab references. This is brand-new geometry in a uniquely
                // created folder, not a channel re-save of a source FBX, so the
                // isolated-export core's same-vertex-count snapshot contract does not
                // apply; a failed export still rolls the whole folder back.
                using (new AssetDatabase.AssetEditingScope())
                    AssetDatabase.CreateAsset(material, folder + "/" + clean + ".mat");
                string fbxPath = folder + "/" + clean + ".fbx";
                UnityEditor.Formats.Fbx.Exporter.ModelExporter.ExportObjects(fbxPath, new Object[] { temporary },
                    new UnityEditor.Formats.Fbx.Exporter.ExportModelOptions {
                        ExportFormat = UnityEditor.Formats.Fbx.Exporter.ExportFormat.Binary,
                        // Embedded maps make the FBX self-contained; a linked FBX
                        // would reference this machine's absolute paths instead.
                        EmbedTextures = settings.embedFbxTextures });
                var fbxInfo = new FileInfo(Path.GetFullPath(fbxPath));
                if (!fbxInfo.Exists || fbxInfo.Length == 0) throw new IOException("FBX export produced an empty file.");
                var modelImporter = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
                if (modelImporter != null) {
                    // The curated material + prefab ship next to the FBX; keep the
                    // importer from generating a duplicate MaterialDescription copy.
                    modelImporter.materialImportMode = ModelImporterMaterialImportMode.None;
                    // The normal map was baked against the exported tangent frame;
                    // a recalculation (MikkT default) can flip its handedness per
                    // vertex, which green-flips chunks of the baked map.
                    modelImporter.importNormals = ModelImporterNormals.Import;
                    modelImporter.importTangents = ModelImporterTangents.Import;
                    modelImporter.SaveAndReimport();
                }
                var fbxRoot = AssetDatabase.LoadMainAssetAtPath(fbxPath) as GameObject;
                if (!fbxRoot) throw new IOException("FBX reimport failed.");
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
                instance.GetComponentInChildren<MeshRenderer>().sharedMaterial = material;
                PrefabUtility.SaveAsPrefabAsset(instance, folder + "/" + clean + ".prefab", out bool success);
                Object.DestroyImmediate(instance);
                if (!success) throw new IOException("Prefab save failed.");
                AssetDatabase.SaveAssets(); status = "Saved: " + folder;
                EditorGUIUtility.PingObject(fbxRoot);
#else
                using (new AssetDatabase.AssetEditingScope()) {
                    AssetDatabase.CreateAsset(mesh, folder + "/" + clean + "_LOD0.asset");
                    AssetDatabase.CreateAsset(material, folder + "/" + clean + ".mat");
                }
                PrefabUtility.SaveAsPrefabAsset(temporary, folder + "/" + clean + ".prefab", out bool success);
                if (!success) throw new IOException("Prefab save failed.");
                AssetDatabase.SaveAssets(); status = "Saved: " + folder;
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/" + clean + ".prefab"));
#endif
            }
            catch (Exception e) {
                if (createdFolder && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                status = e.Message; UvtLog.Error("[Remesh export] " + e);
            }
            finally {
                if (temporary) Object.DestroyImmediate(temporary);
                if (mesh && !AssetDatabase.Contains(mesh)) Object.DestroyImmediate(mesh);
                if (material && !AssetDatabase.Contains(material)) Object.DestroyImmediate(material);
            }
        }

        // Keep-hierarchy export (settings.keepHierarchy): per-node baked map sets, materials
        // and mesh assets under one root prefab that rebuilds the captured local transforms.
        // The source root's own scale stays ON the prefab root (a hierarchy has no single
        // vertex space to bake it into), so "Normalized size" does not apply here.
        void SaveHierarchy(string folder, string clean, Shader shader)
        {
            var temporary = new GameObject(clean + "_LOD0") { hideFlags = HideFlags.None };
            bool urp = shader.name != "Standard";
            try {
                if (settings.normalizeSize)
                    UvtLog.Warn("[Remesh export] Keep-hierarchy saves keep the source root's scale on the prefab root; " +
                                "'Normalized size' applies to the single-mesh weld only.");
                temporary.transform.localScale = source ? source.transform.lossyScale : Vector3.one;
                string[] mapNames = { "BaseColor", "Normal", "MetallicSmoothness", "Occlusion", "Emission" };
                var nodeNames = new string[nodes.Count];
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < nodes.Count; ++i) {
                    string nodeClean = nodes[i].name;
                    foreach (char c in Path.GetInvalidFileNameChars()) nodeClean = nodeClean.Replace(c, '_');
                    nodeClean = nodeClean.Replace('/', '_').Replace('\\', '_');
                    if (string.IsNullOrWhiteSpace(nodeClean)) nodeClean = "Node";
                    var unique = nodeClean;
                    int suffix = 2;
                    while (!used.Add(unique)) unique = nodeClean + "_" + suffix++;
                    nodeNames[i] = unique;
                }
                using (new AssetDatabase.AssetEditingScope()) {
                    for (int i = 0; i < nodes.Count; ++i) {
                        var node = nodes[i];
                        var paths = new string[mapNames.Length];
                        for (int m = 0; m < mapNames.Length; ++m) {
                            paths[m] = folder + "/" + nodeNames[i] + "_" + mapNames[m] + (m == 4 ? ".exr" : ".png");
                            File.WriteAllBytes(paths[m], EncodeMaps(node.maps, m));
                            AssetDatabase.ImportAsset(paths[m]);
                        }
                        for (int m = 0; m < mapNames.Length; ++m) {
                            var importer = (TextureImporter)AssetImporter.GetAtPath(paths[m]);
                            if (importer == null) throw new IOException("Texture import failed: " + paths[m]);
                            importer.textureType = m == 1 ? TextureImporterType.NormalMap : TextureImporterType.Default;
                            importer.sRGBTexture = m == 0; importer.wrapMode = TextureWrapMode.Clamp;
                            importer.maxTextureSize = node.maps.size; importer.textureCompression = TextureImporterCompression.Uncompressed;
                            importer.mipmapEnabled = true; importer.alphaSource = TextureImporterAlphaSource.FromInput;
                            importer.SaveAndReimport();
                        }
                        var material = new Material(shader) { name = nodeNames[i] + "_Remesh" };
                        material.SetTexture(urp ? "_BaseMap" : "_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(paths[0]));
                        material.SetColor(urp ? "_BaseColor" : "_Color", Color.white);
                        material.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(paths[1]));
                        material.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(paths[2]));
                        material.SetTexture("_OcclusionMap", AssetDatabase.LoadAssetAtPath<Texture2D>(paths[3]));
                        material.SetTexture("_EmissionMap", AssetDatabase.LoadAssetAtPath<Texture2D>(paths[4]));
                        material.SetColor("_EmissionColor", Color.white);
                        material.SetFloat("_Metallic", 1); material.SetFloat(urp ? "_Smoothness" : "_GlossMapScale", 1);
                        material.SetFloat("_BumpScale", 1); material.SetFloat("_OcclusionStrength", 1);
                        material.EnableKeyword("_NORMALMAP"); material.EnableKeyword("_EMISSION");
                        material.EnableKeyword(urp ? "_METALLICSPECGLOSSMAP" : "_METALLICGLOSSMAP");
                        if (urp) material.EnableKeyword("_OCCLUSIONMAP");
                        AssetDatabase.CreateAsset(material, folder + "/" + nodeNames[i] + ".mat");
                        // The mesh asset is a COPY: the runtime node.mesh stays a preview
                        // object owned by the stage outputs (destroyed by ClearFrom).
                        var mesh = Object.Instantiate(node.mesh);
                        mesh.name = nodeNames[i] + "_LOD0"; mesh.hideFlags = HideFlags.None;
                        AssetDatabase.CreateAsset(mesh, folder + "/" + nodeNames[i] + "_LOD0.asset");
                        var go = new GameObject(nodeNames[i]);
                        go.AddComponent<MeshFilter>().sharedMesh = mesh;
                        go.AddComponent<MeshRenderer>().sharedMaterial = material;
                        go.transform.localPosition = node.localPosition;
                        go.transform.localRotation = node.localRotation;
                        go.transform.localScale = node.localScale;
                        go.transform.SetParent(temporary.transform, false);
                    }
                }
                PrefabUtility.SaveAsPrefabAsset(temporary, folder + "/" + clean + ".prefab", out bool success);
                if (!success) throw new IOException("Prefab save failed.");
                AssetDatabase.SaveAssets();
                status = $"Saved: {folder} ({nodes.Count} node(s))";
                UvtLog.Info("[Remesh export] " + status);
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/" + clean + ".prefab"));
            }
            finally {
                Object.DestroyImmediate(temporary);
            }
        }

        // "<source asset folder>/remesh" when the source root resolves to an imported
        // asset (a prefab/model instance, or any mesh asset under it); null when the
        // source is scene-only and the folder picker is needed instead.
        string ResolveSourceRemeshFolder()
        {
            string assetPath = null;
            if (source) {
                assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source);
                if (string.IsNullOrEmpty(assetPath)) {
                    var filter = source.GetComponentInChildren<MeshFilter>();
                    if (filter && filter.sharedMesh) assetPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
                }
            }
            if (string.IsNullOrEmpty(assetPath)) return null;
            string dir = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(dir)) return null;
            dir = dir.Replace('\\', '/');
            if (!dir.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !AssetDatabase.IsValidFolder(dir))
                return null;
            string remesh = dir + "/remesh";
            if (!AssetDatabase.IsValidFolder(remesh)) AssetDatabase.CreateFolder(dir, "remesh");
            return AssetDatabase.IsValidFolder(remesh) ? remesh : null;
        }

        // Bakes a world scale into the vertex data so the saved root sits at scale 1:
        // positions scale component-wise, normals and tangents take the inverse
        // (inverse-transpose of a diagonal matrix) and renormalize, and a negative
        // determinant mirrors the mesh, so the triangle winding flips with it.
        static void BakeScaleIntoMesh(Mesh mesh, Vector3 scale)
        {
            if (Mathf.Approximately(scale.x, 1) && Mathf.Approximately(scale.y, 1) && Mathf.Approximately(scale.z, 1)) return;
            if (Mathf.Abs(scale.x) < 1e-6f || Mathf.Abs(scale.y) < 1e-6f || Mathf.Abs(scale.z) < 1e-6f) return;
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; ++i) vertices[i] = Vector3.Scale(vertices[i], scale);
            mesh.vertices = vertices;
            var inv = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
            var normals = mesh.normals;
            for (int i = 0; i < normals.Length; ++i) normals[i] = Vector3.Scale(normals[i], inv).normalized;
            mesh.normals = normals;
            var tangents = mesh.tangents;
            for (int i = 0; i < tangents.Length; ++i) {
                var t = Vector3.Scale(new Vector3(tangents[i].x, tangents[i].y, tangents[i].z), inv).normalized;
                tangents[i] = new Vector4(t.x, t.y, t.z, tangents[i].w);
            }
            mesh.tangents = tangents;
            if (scale.x * scale.y * scale.z < 0)
                for (int sub = 0; sub < mesh.subMeshCount; ++sub) {
                    var triangles = mesh.GetTriangles(sub);
                    for (int i = 0; i < triangles.Length; i += 3) {
                        int tmp = triangles[i + 1]; triangles[i + 1] = triangles[i + 2]; triangles[i + 2] = tmp;
                    }
                    mesh.SetTriangles(triangles, sub);
                }
        }

        byte[] Encode(int index) => EncodeMaps(maps, index);

        byte[] EncodeMaps(RemeshBaker.Maps source, int index)
        {
            var texture = new Texture2D(source.size, source.size,
                index == 4 ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false, true);
            try {
                if (index == 4) { texture.SetPixels(source.emission); texture.Apply(); return texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat); }
                texture.SetPixels32(index == 0 ? source.color : index == 1 ? source.normal : index == 2 ? source.metal : source.ao);
                texture.Apply(); return texture.EncodeToPNG();
            }
            finally { Object.DestroyImmediate(texture); }
        }
    }
}
