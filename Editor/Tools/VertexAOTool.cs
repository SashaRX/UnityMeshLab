// VertexAOTool.cs — Vertex AO baking tool (IUvTool tab).
// GPU depth-map hemisphere sampling with ground plane, blur, and channel-select output.

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
        int   blurIterations = 0;
        float blurStrength   = 0.5f;
        bool  blurCrossHardEdges = true;
        bool  blurCrossUvSeams   = true;
        bool  faceAreaCorrection = false;
        bool  backfaceCulling = true;

        // ── Results ──
        Dictionary<Mesh, float[]> bakedRawAO;    // raw bake result (before blur)
        Dictionary<Mesh, float[]> bakedFinalAO;   // after blur
        float bakeTimeSeconds;
        int   bakedVertexCount;

        // ── Preview ──
        bool previewActive;
        List<(MeshFilter mf, Mesh originalMesh, Material[] originalMats)> previewBackups
            = new List<(MeshFilter, Mesh, Material[])>();
        Material previewMaterial;

        // ── Lifecycle ──

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            this.ctx = ctx;
            this.canvas = canvas;
        }

        public void OnDeactivate()
        {
            RestorePreview();
            ClearResults();
        }

        public void OnRefresh()
        {
            RestorePreview();
            ClearResults();
        }

        void ClearResults()
        {
            bakedRawAO = null;
            bakedFinalAO = null;
            bakedVertexCount = 0;
        }

        // ── UI ──

        public void OnDrawSidebar()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Vertex AO Baker", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (ctx.LodGroup == null)
            {
                EditorGUILayout.HelpBox(
                    "Select a LODGroup to bake vertex ambient occlusion.",
                    MessageType.Info);
                return;
            }

            // Bake mode indicator
            bool gpuAvailable = SystemInfo.supportsComputeShaders;
            if (gpuAvailable)
            {
                EditorGUILayout.HelpBox(
                    "GPU mode (" + SystemInfo.graphicsDeviceType + ") — depth-map hemisphere sampling via compute shader.",
                    MessageType.None);
            }
            else
            {
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

            EditorGUILayout.Space(4);

            // Bake settings
            sampleCountIndex = EditorGUILayout.Popup(
                new GUIContent("Sample Count", "Number of hemisphere directions to sample. Higher = smoother AO, slower bake."),
                sampleCountIndex, sampleLabels);
            if (gpuAvailable)
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

            // Ground plane
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
                new GUIContent("Backface Culling", "Ignore hits on back side of triangles. Reduces false occlusion on thin walls and single-sided geometry."),
                backfaceCulling);
            faceAreaCorrection = EditorGUILayout.Toggle(
                new GUIContent("Face-Area Fix", "Fix large flat polygons where all vertices are in occlusion but the surface is open. Enable only for low-poly meshes with large quads."),
                faceAreaCorrection);

            EditorGUILayout.Space(8);

            // Bake button
            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.4f, .8f, .4f);
            if (GUILayout.Button("Bake Vertex AO", GUILayout.Height(28)))
                ExecuteBake();
            GUI.backgroundColor = bgc;

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

                // Post-processing — blur updates preview in real-time
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Post-Processing", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                blurIterations = EditorGUILayout.IntSlider(
                    new GUIContent("Blur", "Smooth AO across neighboring vertices. More iterations = softer result."),
                    blurIterations, 0, 10);
                blurStrength = EditorGUILayout.Slider(
                    new GUIContent("Strength", "Blend factor per iteration. 1 = full neighbor average."),
                    blurStrength, 0f, 1f);
                blurCrossHardEdges = EditorGUILayout.Toggle(
                    new GUIContent("Cross Hard Edges", "Blur across hard edges (vertices with different normals at same position)."),
                    blurCrossHardEdges);
                blurCrossUvSeams = EditorGUILayout.Toggle(
                    new GUIContent("Cross UV Seams", "Blur across UV shell boundaries (vertices with different UV0 at same position)."),
                    blurCrossUvSeams);
                if (EditorGUI.EndChangeCheck())
                    ApplyBlur();

                EditorGUILayout.Space(8);

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
            RestorePreview();

            var entries = ctx.MeshEntries
                .Where(e => e.include && e.renderer != null)
                .ToList();

            if (entries.Count == 0)
            {
                UvtLog.Warn("[Vertex AO] No meshes found.");
                return;
            }

            var meshList = new List<(Mesh mesh, Matrix4x4 transform)>();
            foreach (var e in entries)
            {
                Mesh mesh = e.originalMesh ?? e.fbxMesh;
                if (mesh == null) continue;
                meshList.Add((mesh, e.renderer.transform.localToWorldMatrix));
            }

            var settings = new VertexAOSettings
            {
                sampleCount     = sampleCounts[sampleCountIndex],
                depthResolution = resolutions[resolutionIndex],
                maxRadius       = maxRadius,
                intensity       = intensity,
                groundPlane     = groundPlane,
                groundOffset    = groundOffset,
                faceAreaCorrection = faceAreaCorrection,
                backfaceCulling = backfaceCulling
            };

            var sw = Stopwatch.StartNew();
            bakedRawAO = VertexAOBaker.BakeMultiMesh(meshList, settings);
            sw.Stop();
            bakeTimeSeconds = (float)sw.Elapsed.TotalSeconds;

            // Apply initial blur
            bakedFinalAO = new Dictionary<Mesh, float[]>();
            bakedVertexCount = 0;
            foreach (var kvp in bakedRawAO)
            {
                bakedFinalAO[kvp.Key] = (float[])kvp.Value.Clone();
                bakedVertexCount += kvp.Value.Length;
            }
            if (blurIterations > 0)
                ApplyBlurInternal();

            int lodCount = entries.Select(e => e.lodIndex).Distinct().Count();
            UvtLog.Info($"[Vertex AO] Baked {bakedVertexCount} vertices across {lodCount} LOD(s) in {bakeTimeSeconds:F1}s");

            // Auto-enable preview after bake
            ActivatePreview();
            requestRepaint?.Invoke();
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
            if (blurIterations <= 0 || bakedFinalAO == null) return;
            foreach (var mesh in bakedFinalAO.Keys.ToList())
            {
                var uv0List = new List<Vector2>();
                mesh.GetUVs(0, uv0List);
                var uv0Arr = uv0List.Count == mesh.vertexCount ? uv0List.ToArray() : null;

                bakedFinalAO[mesh] = VertexAOBaker.BlurAO(
                    bakedFinalAO[mesh], mesh.triangles, mesh.vertexCount,
                    blurIterations, blurStrength,
                    mesh.vertices, mesh.normals, uv0Arr,
                    blurCrossHardEdges, blurCrossUvSeams);
            }
        }

        // ── Apply ──

        void ApplyToMesh()
        {
            if (bakedFinalAO == null) return;
            RestorePreview();

            var channel = TargetChannel;
            foreach (var kvp in bakedFinalAO)
            {
                VertexAOBaker.WriteToChannel(kvp.Key, kvp.Value, channel);
                EditorUtility.SetDirty(kvp.Key);
            }

            UvtLog.Info($"[Vertex AO] Applied to {bakedFinalAO.Count} mesh(es) → {TargetChannelName}");
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

        void RestorePreview()
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
