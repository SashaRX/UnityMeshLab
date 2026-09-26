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

        // Stage outputs. Each stage consumes the previous one; re-running a stage clears
        // everything after it. keys[] remember the settings each output was built with.
        RemeshSource snapshot;
        RemeshNative.IndexedMesh voxel, simplified;
        float simplifyError;
        RemeshNative.Geometry geometry;
        Vector4[] tangents;
        RemeshBaker.Maps maps;
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
            AssemblyReloadEvents.beforeAssemblyReload -= Clear;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }
        public void OnDeactivate()
        {
            Selection.selectionChanged -= FollowSelection;
            AssemblyReloadEvents.beforeAssemblyReload -= Clear; cancellation?.Cancel(); Clear();
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
                        "Angle: crease angle. UV islands: hard along island borders, smooth inside."), settings.hardEdges);
                    bool angle = settings.hardEdges == RemeshHardEdges.Angle || settings.hardEdges == RemeshHardEdges.UvIslandsAndAngle;
                    using (new EditorGUI.DisabledScope(!angle))
                        settings.normalCrease = EditorGUILayout.Slider("Crease angle", settings.normalCrease, 0, 180);
                    using (new EditorGUI.DisabledScope(settings.hardEdges == RemeshHardEdges.UvIslands || settings.hardEdges == RemeshHardEdges.UvIslandsAndAngle))
                        settings.normalSmoothing = EditorGUILayout.Slider("Normal smoothing", settings.normalSmoothing, 0, 10);
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
                using (new EditorGUI.DisabledScope(cancellation != null))
                    if (GUILayout.Button("Save mesh, maps & prefab…")) {
                        Save();
                        // The folder panel is modal and the export imports assets; both
                        // leave this event's layout state stale.
                        GUIUtility.ExitGUI();
                    }
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
                case Stage.Remesh: return $"{(source ? source.GetInstanceID() : 0)}|{s.voxelResolution}|{s.solve}|{s.shell}";
                case Stage.Simplify: return $"{s.simplify}|{s.targetTriangles}|{s.maximumError}|{s.regularize}|{s.preserveFolds}|{s.pruneSmallParts}";
                case Stage.Unwrap: return $"{s.hardEdges}|{s.normalCrease}|{s.normalSmoothing}|{s.textureResolution}|{s.padding}|{s.chartMaxCost}|" +
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
            var captured = RemeshSource.Capture(root);
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

        async Task RunSimplify(RemeshSettings options, CancellationToken token)
        {
            if (voxel == null) throw new InvalidOperationException("Run the remesh stage first.");
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
            if (simplified == null) throw new InvalidOperationException("Run the simplify stage first.");
            var input = simplified;
            Report("Generating normals and unwrapping UVs…");
            var unwrapped = await Task.Run(() => RemeshNative.Unwrap(input, options, token));
            token.ThrowIfCancellationRequested();
            geometry = unwrapped;
            resultMesh = new Mesh { name = resultName + "_LOD0", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            resultMesh.vertices = unwrapped.positions; resultMesh.normals = unwrapped.normals;
            resultMesh.uv = unwrapped.uv; resultMesh.triangles = unwrapped.indices;
            resultMesh.RecalculateBounds(); resultMesh.RecalculateTangents();
            tangents = resultMesh.tangents;
            status = $"Unwrap: {unwrapped.chartCount:N0} islands, {unwrapped.positions.Length:N0} vertices.";
        }

        async Task RunBake(RemeshSettings options, CancellationToken token)
        {
            if (geometry == null || snapshot == null) throw new InvalidOperationException("Run the UV stage first.");
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
            }
        }

        void Clear()
        {
            ClearFrom(Stage.Remesh);
            previews.Dispose();
        }

        void Save()
        {
            string parent = EditorUtility.OpenFolderPanel("Save Remesh Result", Application.dataPath, "");
            if (string.IsNullOrEmpty(parent)) return;
            parent = parent.Replace('\\', '/');
            string assets = Application.dataPath.Replace('\\', '/');
            if (parent != assets && !parent.StartsWith(assets + "/", StringComparison.Ordinal)) {
                status = "Choose a folder inside Assets."; return;
            }
            string relative = "Assets" + parent.Substring(assets.Length);
            // The folder panel can create a directory the AssetDatabase has not imported
            // yet; GenerateUniqueAssetPath/CreateFolder then fail on the unknown parent.
            if (!AssetDatabase.IsValidFolder(relative)) AssetDatabase.Refresh();
            if (!AssetDatabase.IsValidFolder(relative)) {
                status = relative + " is not an imported asset folder. Refresh the Project window and save again."; return;
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
                using (new AssetDatabase.AssetEditingScope()) {
                    AssetDatabase.CreateAsset(mesh, folder + "/" + clean + "_LOD0.asset");
                    AssetDatabase.CreateAsset(material, folder + "/" + clean + ".mat");
                }
                temporary = new GameObject(clean + "_LOD0") { hideFlags = HideFlags.HideAndDontSave };
                temporary.AddComponent<MeshFilter>().sharedMesh = mesh;
                temporary.AddComponent<MeshRenderer>().sharedMaterial = material;
                temporary.hideFlags = HideFlags.None;
                PrefabUtility.SaveAsPrefabAsset(temporary, folder + "/" + clean + ".prefab", out bool success);
                if (!success) throw new IOException("Prefab save failed.");
                AssetDatabase.SaveAssets(); status = "Saved: " + folder;
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/" + clean + ".prefab"));
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

        byte[] Encode(int index)
        {
            var texture = new Texture2D(maps.size, maps.size,
                index == 4 ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false, true);
            try {
                if (index == 4) { texture.SetPixels(maps.emission); texture.Apply(); return texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat); }
                texture.SetPixels32(index == 0 ? maps.color : index == 1 ? maps.normal : index == 2 ? maps.metal : maps.ao);
                texture.Apply(); return texture.EncodeToPNG();
            }
            finally { Object.DestroyImmediate(texture); }
        }
    }
}
