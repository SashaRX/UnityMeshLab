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
    public sealed class RemeshBakeTool : IUvTool
    {
        public string ToolName => "Remesh & Bake";
        public string ToolId => "remesh_bake";
        public int ToolOrder => 35;
        public Action RequestRepaint { private get; set; }
        GameObject source, lastSelection;
        RemeshSettings settings = new RemeshSettings();
        CancellationTokenSource cancellation;
        static int running;
        Mesh result;
        RemeshBaker.Maps maps;
        Texture2D preview;
        string status = "Select a static model root. LODGroups contribute only LOD0.";
        string resultName;
        internal GameObject Source => source;

        public void OnActivate(UvToolContext context, UvCanvasView canvas)
        {
            FollowSelection();
            Selection.selectionChanged -= FollowSelection;
            Selection.selectionChanged += FollowSelection;
            // Result/preview carry HideAndDontSave, so they survive scene loads but
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
        // snapshot is captured at Run() start, so a running bake must not be cancelled here.
        public void OnRefresh() { }
        public void OnDrawToolbarExtra() { }
        public void OnDrawStatusBar() { GUILayout.Label(status, EditorStyles.miniLabel); }
        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes() => null;
        public void OnSceneGUI(SceneView sceneView) { }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float size) { }

        public void OnDrawSidebar()
        {
            EditorGUILayout.LabelField("High-poly → Low-poly", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(Volatile.Read(ref running) != 0)) {
                source = (GameObject)EditorGUILayout.ObjectField("Source root", source, typeof(GameObject), true);
                settings.voxelResolution = EditorGUILayout.IntSlider("Voxel resolution", settings.voxelResolution, 4, 256);
                settings.targetTriangles = Mathf.Clamp(EditorGUILayout.IntField("Target triangles", settings.targetTriangles), 1, 5000000);
                settings.maximumError = EditorGUILayout.Slider("Maximum error", settings.maximumError, 0, 1);
                settings.solve = EditorGUILayout.Toggle("Fit source surface", settings.solve);
                settings.shell = EditorGUILayout.Toggle("Two-sided shell", settings.shell);
                settings.normalCrease = EditorGUILayout.Slider("Normal crease", settings.normalCrease, 0, 180);
                settings.normalSmoothing = EditorGUILayout.Slider("Normal smoothing", settings.normalSmoothing, 0, 10);
                settings.textureResolution = EditorGUILayout.IntPopup("Texture size", settings.textureResolution,
                    new[] { "512", "1024", "2048", "4096" }, new[] { 512, 1024, 2048, 4096 });
                settings.padding = EditorGUILayout.IntSlider("Atlas padding", settings.padding, 1, 32);
                settings.projectionDistance = EditorGUILayout.Slider("Projection / bounds", settings.projectionDistance, 0.001f, 0.2f);
                EditorGUILayout.HelpBox("Experimental voxel remesh. Thin details and small gaps may disappear. Supports opaque Standard and URP/Lit metallic materials.", MessageType.Info);
                using (new EditorGUI.DisabledScope(!source || UvProgress.IsActive))
                    if (GUILayout.Button("Generate & Bake", GUILayout.Height(28))) {
                        Run();
                        // Run() has already swapped the result section for the Cancel
                        // button; end this event before IMGUI asks for controls the
                        // layout pass never registered.
                        GUIUtility.ExitGUI();
                    }
            }
            if (cancellation != null)
                using (new EditorGUI.DisabledScope(cancellation.IsCancellationRequested))
                    if (GUILayout.Button("Cancel (after current native phase)")) {
                        cancellation.Cancel(); status = "Cancelling after the current phase…";
                    }
            EditorGUILayout.HelpBox(status, maps != null && maps.misses > 0 ? MessageType.Warning : MessageType.None);
            if (result && maps != null) {
                EditorGUILayout.LabelField($"{result.vertexCount:N0} vertices · {result.GetIndexCount(0) / 3:N0} triangles");
                if (preview) {
                    Rect rect = GUILayoutUtility.GetAspectRect(1);
                    EditorGUI.DrawPreviewTexture(rect, preview);
                }
                using (new EditorGUI.DisabledScope(cancellation != null))
                    if (GUILayout.Button("Save mesh, maps & prefab…")) {
                        Save();
                        // The folder panel is modal and the export imports assets; both
                        // leave this event's layout state stale.
                        GUIUtility.ExitGUI();
                    }
            }
        }

        async void Run()
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            Mesh temporary = null;
            bool locked = false;
            try {
                settings.Validate(); RemeshNative.CheckAvailable();
                var options = JsonUtility.FromJson<RemeshSettings>(JsonUtility.ToJson(settings));
                var root = source;
                string name = root.name;
                Clear();
                EditorApplication.LockReloadAssemblies(); locked = true;
                status = "Reading source geometry and textures…"; RequestRepaint?.Invoke();
                await Task.Yield(); token.ThrowIfCancellationRequested();
                var snapshot = RemeshSource.Capture(root);
                foreach (var warning in snapshot.warnings) UvtLog.Warn("[Remesh] " + warning);
                status = "Remeshing, simplifying and unwrapping…"; RequestRepaint?.Invoke();
                var geometry = await Task.Run(() => RemeshNative.Build(snapshot.positions, snapshot.indices, options, token));
                token.ThrowIfCancellationRequested();
                temporary = new Mesh { name = name + "_LOD0", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
                temporary.vertices = geometry.positions; temporary.normals = geometry.normals;
                temporary.uv = geometry.uv; temporary.triangles = geometry.indices;
                temporary.RecalculateBounds(); temporary.RecalculateTangents();
                Vector4[] tangents = temporary.tangents;
                status = "Projecting source materials into the new UV atlas…"; RequestRepaint?.Invoke();
                var baked = await Task.Run(() => RemeshBaker.Bake(snapshot, geometry, tangents, options, token));
                token.ThrowIfCancellationRequested();
                preview = new Texture2D(baked.size, baked.size, TextureFormat.RGBA32, false, false) { hideFlags = HideFlags.HideAndDontSave };
                preview.SetPixels32(baked.color); preview.Apply();
                result = temporary; temporary = null; maps = baked; resultName = name;
                status = $"{snapshot.indices.Length / 3:N0} → {geometry.indices.Length / 3:N0} triangles. " +
                    (baked.misses == 0 ? "All covered texels projected." : $"{baked.misses:N0} / {baked.covered:N0} texels missed (magenta). Increase projection distance and rebake.") +
                    (snapshot.warnings.Length > 0 ? $" {snapshot.warnings.Length} material warning(s), see Console." : "");
                UvtLog.Info("[Remesh] " + status);
            }
            catch (OperationCanceledException) { status = "Cancelled. Source assets were preserved."; }
            catch (Exception e) { status = e.Message; UvtLog.Error("[Remesh] " + e); }
            finally {
                if (temporary) Object.DestroyImmediate(temporary);
                cancellation.Dispose(); cancellation = null;
                if (locked) EditorApplication.UnlockReloadAssemblies();
                Interlocked.Exchange(ref running, 0); RequestRepaint?.Invoke();
            }
        }

        void Clear()
        {
            if (result) Object.DestroyImmediate(result);
            if (preview) Object.DestroyImmediate(preview);
            result = null; preview = null; maps = null;
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
                mesh = Object.Instantiate(result); mesh.name = clean + "_LOD0"; mesh.hideFlags = HideFlags.None;
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
