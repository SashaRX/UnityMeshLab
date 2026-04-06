// LodGenerationTool.cs — LOD generation via meshoptimizer simplification.
// Preserves UV2 lightmap coordinates with configurable weights.
//
// Workflow: UV2 Transfer (Analyze → Weld → Repack → Transfer) → LOD Gen → Overwrite FBX (from UV2 Transfer tab)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

namespace LightmapUvTool
{
    public class LodGenerationTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        Action requestRepaint;

        public string ToolName  => "LOD Gen";
        public string ToolId    => "lod_generation";
        public int    ToolOrder => 30;

        public Action RequestRepaint { set => requestRepaint = value; }

        // ── Settings ──
        int generateLodCount = 2;
        float[] generateLodRatios = { 0.5f, 0.25f, 0.125f, 0.0625f };
        float generateTargetError = 0.05f;
        float generateUv2Weight = 100f;
        float generateNormalWeight = 1f;
        bool generateLockBorder = true;

        // ── Results ──
        List<GeneratedLodInfo> lastResults = new List<GeneratedLodInfo>();

        struct GeneratedLodInfo
        {
            public string meshName;
            public int simplifiedTris;
            public int lodLevel;
            public float targetRatio;
            public float actualRatio;
            public bool hitErrorLimit;
        }

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            this.ctx = ctx;
            this.canvas = canvas;
        }

        public void OnDeactivate() { }
        public void OnRefresh() { lastResults.Clear(); }

        public void OnDrawSidebar()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("LOD Generation", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (ctx.LodGroup == null)
            {
                EditorGUILayout.HelpBox("Assign a LODGroup in the UV2 Transfer tab first.", MessageType.Info);
                return;
            }

            // ── Workflow hint ──
            bool hasRepack = ctx.MeshEntries.Any(e => e.repackedMesh != null);
            if (!hasRepack)
            {
                EditorGUILayout.HelpBox(
                    "Workflow:\n" +
                    "1. UV2 Transfer: Analyze → Weld → Repack → Transfer\n" +
                    "2. LOD Gen: Generate new LODs\n" +
                    "3. UV2 Transfer: Overwrite Source FBX (saves everything)",
                    MessageType.Info);
            }

            // ── LOD Polycount Table ──
            EditorGUILayout.LabelField("Existing LODs", EditorStyles.boldLabel);
            int sourceTris = 0;
            int lastExistingLod = -1;
            for (int li = 0; li < ctx.LodCount; li++)
            {
                var ee = ctx.ForLod(li);
                if (ee.Count == 0) continue;
                lastExistingLod = li;
                int lodTris = 0, lodVerts = 0;
                foreach (var e in ee)
                {
                    Mesh m = e.repackedMesh ?? e.originalMesh ?? e.fbxMesh;
                    if (m == null) continue;
                    lodTris += m.triangles.Length / 3;
                    lodVerts += m.vertexCount;
                }
                if (li == ctx.SourceLodIndex) sourceTris = lodTris;
                bool isSrc = li == ctx.SourceLodIndex;
                string prefix = isSrc ? "► " : "  ";
                float pct = sourceTris > 0 ? (float)lodTris / sourceTris * 100f : 100f;
                EditorGUILayout.LabelField(
                    $"{prefix}LOD{li}: {lodTris:N0} tris  {lodVerts:N0} verts  ({pct:F0}%)",
                    isSrc ? EditorStyles.boldLabel : EditorStyles.miniLabel);
            }

            int startLod = lastExistingLod + 1;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField($"Generate LOD{startLod}+", EditorStyles.boldLabel);

            // ── Settings ──
            generateLodCount = EditorGUILayout.IntSlider("Count", generateLodCount, 1, 4);

            float lastRatio = 1f;
            if (sourceTris > 0 && lastExistingLod > ctx.SourceLodIndex)
            {
                int lastLodTris = 0;
                foreach (var e in ctx.ForLod(lastExistingLod))
                {
                    Mesh m = e.repackedMesh ?? e.originalMesh ?? e.fbxMesh;
                    if (m != null) lastLodTris += m.triangles.Length / 3;
                }
                lastRatio = (float)lastLodTris / sourceTris;

                for (int i = 0; i < generateLodCount && i < generateLodRatios.Length; i++)
                {
                    if (generateLodRatios[i] >= lastRatio && lastRatio > 0.01f)
                        generateLodRatios[i] = lastRatio * Mathf.Pow(0.5f, i + 1);
                }
            }

            for (int i = 0; i < generateLodCount && i < generateLodRatios.Length; i++)
            {
                float maxRatio = i == 0 ? lastRatio * 0.99f : generateLodRatios[i - 1] * 0.99f;
                if (maxRatio < 0.001f) maxRatio = 0.001f;
                if (generateLodRatios[i] > maxRatio) generateLodRatios[i] = maxRatio * 0.5f;

                int targetLod = startLod + i;
                generateLodRatios[i] = EditorGUILayout.Slider(
                    $"  LOD{targetLod}", generateLodRatios[i], 0.001f, maxRatio);
                int estTris = Mathf.RoundToInt(sourceTris * generateLodRatios[i]);
                EditorGUILayout.LabelField($"      target ≈ {estTris:N0} tris ({generateLodRatios[i] * 100f:F0}% of source)", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("      (actual depends on Target Error, UV2 Weight, Lock Border)", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            generateTargetError = EditorGUILayout.Slider("Target Error", generateTargetError, 0.001f, 0.5f);
            generateUv2Weight = EditorGUILayout.Slider("UV2 Weight", generateUv2Weight, 0f, 500f);
            generateNormalWeight = EditorGUILayout.Slider("Normal Weight", generateNormalWeight, 0f, 10f);
            generateLockBorder = EditorGUILayout.Toggle("Lock Border", generateLockBorder);

            if (generateTargetError < 0.1f && generateUv2Weight > 50f)
                EditorGUILayout.HelpBox(
                    "Low Target Error + High UV2 Weight may prevent reaching target polygon count. " +
                    "Try: Target Error 0.1–0.3, UV2 Weight 10–30, Lock Border OFF.",
                    MessageType.Warning);

            EditorGUILayout.Space(6);

            var bg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.7f, .4f, .95f);
            if (GUILayout.Button($"Generate LOD{startLod}–LOD{startLod + generateLodCount - 1}", GUILayout.Height(30)))
                ExecGenerateLods(startLod);
            GUI.backgroundColor = bg;

            // ── Results ──
            if (lastResults.Count > 0)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Generated", EditorStyles.boldLabel);
                foreach (var r in lastResults)
                {
                    float pct = sourceTris > 0 ? (float)r.simplifiedTris / sourceTris * 100f : 0;
                    string warn = r.hitErrorLimit ? " ⚠" : "";
                    EditorGUILayout.LabelField(
                        $"  LOD{r.lodLevel}: {r.meshName} — {r.simplifiedTris:N0} tris ({pct:F0}%){warn}",
                        EditorStyles.miniLabel);
                    if (r.hitErrorLimit)
                        EditorGUILayout.LabelField(
                            $"      target {r.targetRatio:P0}, got {r.actualRatio:P0} — increase Target Error",
                            EditorStyles.miniLabel);
                }

                // ── FBX Export button right here ──
                EditorGUILayout.Space(4);
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                GUI.backgroundColor = new Color(.95f, .6f, .2f);
                if (GUILayout.Button("Overwrite Source FBX (all LODs)", GUILayout.Height(26)))
                {
                    // Delegate to LightmapTransferTool's ExportFbx via reflection-free approach:
                    // find the tool and call its export method
                    foreach (var tool in FindToolsInHub())
                    {
                        if (tool is LightmapTransferTool ltt)
                        {
                            ltt.ExportFbxPublic(true);
                            break;
                        }
                    }
                }
                GUI.backgroundColor = bg;
#else
                EditorGUILayout.HelpBox("Install com.unity.formats.fbx for FBX export.", MessageType.Info);
#endif
            }
        }

        IEnumerable<IUvTool> FindToolsInHub()
        {
            var hubs = Resources.FindObjectsOfTypeAll<UvToolHub>();
            if (hubs.Length == 0) yield break;
            // Access tools via reflection since field is private
            var fi = typeof(UvToolHub).GetField("tools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (fi == null) yield break;
            var tools = fi.GetValue(hubs[0]) as List<IUvTool>;
            if (tools == null) yield break;
            foreach (var t in tools) yield return t;
        }

        void ExecGenerateLods(int startLod)
        {
            if (ctx.LodGroup == null) return;
            lastResults.Clear();

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

            try
            {
                for (int lodIdx = 0; lodIdx < generateLodCount; lodIdx++)
                {
                    float ratio = generateLodRatios[lodIdx];
                    var settings = new MeshSimplifier.SimplifySettings
                    {
                        targetRatio  = ratio,
                        targetError  = generateTargetError,
                        uv2Weight    = generateUv2Weight,
                        normalWeight = generateNormalWeight,
                        lockBorder   = generateLockBorder,
                        uvChannel    = ctx.PipeSettings.targetUvChannel
                    };

                    float progress = (float)lodIdx / generateLodCount;
                    EditorUtility.DisplayProgressBar("Generate LODs",
                        $"LOD{startLod + lodIdx} (ratio {ratio:P0})", progress);

                    var lodRenderers = new List<Renderer>();
                    int lodLevel = startLod + lodIdx;

                    foreach (var (entry, srcMesh) in sourceMeshes)
                    {
                        var r = MeshSimplifier.Simplify(srcMesh, settings);
                        if (!r.ok) { UvtLog.Error($"[GenerateLOD] Failed on {srcMesh.name}: {r.error}"); continue; }

                        float actualRatio = srcMesh.triangles.Length > 0
                            ? (float)r.simplifiedTriCount / (srcMesh.triangles.Length / 3) : 1f;
                        bool hitLimit = actualRatio > ratio * 1.2f;
                        if (hitLimit)
                            UvtLog.Warn($"[GenerateLOD] LOD{lodLevel}: target {ratio:P0} but got {actualRatio:P0} — increase Target Error");

                        string baseName = entry.fbxMesh != null ? entry.fbxMesh.name : srcMesh.name;
                        baseName = Regex.Replace(baseName, @"(_wc|_repack|_uvTransfer|_optimized|_LOD\d+)+$", "");
                        string meshName = baseName + "_LOD" + lodLevel;
                        r.simplifiedMesh.name = meshName;

                        // Save as asset
                        string assetPath = AssetDatabase.GenerateUniqueAssetPath(savePath + "/" + meshName + ".asset");
                        AssetDatabase.CreateAsset(r.simplifiedMesh, assetPath);
                        UvtLog.Info($"[GenerateLOD] {meshName}: {r.originalTriCount} → {r.simplifiedTriCount} tris ({actualRatio:P0})");

                        lastResults.Add(new GeneratedLodInfo
                        {
                            meshName = meshName,
                            simplifiedTris = r.simplifiedTriCount,
                            lodLevel = lodLevel,
                            targetRatio = ratio,
                            actualRatio = actualRatio,
                            hitErrorLimit = hitLimit
                        });

                        // Create scene GameObject + add to LODGroup
                        if (entry.renderer != null)
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

                    if (lodRenderers.Count > 0)
                    {
                        if (lodLevel < newLods.Count)
                        {
                            // Replace existing LOD
                            var oldRenderers = newLods[lodLevel].renderers;
                            if (oldRenderers != null)
                                foreach (var oldR in oldRenderers)
                                    if (oldR != null && oldR.gameObject != null)
                                        Undo.DestroyObjectImmediate(oldR.gameObject);
                            newLods[lodLevel] = new LOD(newLods[lodLevel].screenRelativeTransitionHeight, lodRenderers.ToArray());
                        }
                        else
                        {
                            float baseHeight = newLods.Count > 0 ? newLods[newLods.Count - 1].screenRelativeTransitionHeight : 0.5f;
                            newLods.Add(new LOD(baseHeight * 0.5f, lodRenderers.ToArray()));
                        }
                    }
                }

                Undo.RecordObject(ctx.LodGroup, "Generate LODs");
                ctx.LodGroup.SetLODs(newLods.ToArray());
                AssetDatabase.SaveAssets();
            }
            finally { EditorUtility.ClearProgressBar(); }

            // Add new MeshEntries for generated LODs without touching existing state
            var currentLods = ctx.LodGroup.GetLODs();
            for (int li = 0; li < currentLods.Length; li++)
            {
                if (ctx.MeshEntries.Any(e => e.lodIndex == li)) continue;
                if (currentLods[li].renderers == null) continue;
                foreach (var r in currentLods[li].renderers)
                {
                    if (r == null) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var fbm = mf.sharedMesh;
                    var uv2Check = new List<Vector2>();
                    fbm.GetUVs(1, uv2Check);
                    ctx.MeshEntries.Add(new MeshEntry
                    {
                        lodIndex = li,
                        renderer = r,
                        meshFilter = mf,
                        originalMesh = fbm,
                        fbxMesh = fbm,
                        hasExistingUv2 = uv2Check.Count > 0,
                        meshGroupKey = UvToolContext.ExtractGroupKey(r.name)
                    });
                }
            }
            ctx.ClearAllCaches();
            requestRepaint?.Invoke();
        }

        public void OnDrawToolbarExtra() { }
        public void OnDrawStatusBar() { }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { }

        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes()
        {
            yield return new UvCanvasView.FillModeEntry { name = "Shells" };
        }

        public void OnSceneGUI(SceneView sv) { }
    }
}
