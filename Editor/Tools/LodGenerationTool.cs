// LodGenerationTool.cs — LOD generation via meshoptimizer simplification.
// Preserves UV2 lightmap coordinates with configurable weights.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
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
        float generateTargetError = 0.01f;
        float generateUv2Weight = 100f;
        float generateNormalWeight = 1f;
        bool generateLockBorder = true;
        bool generateAddToLodGroup = true;

        // ── Results ──
        List<GeneratedLodInfo> lastResults = new List<GeneratedLodInfo>();

        struct GeneratedLodInfo
        {
            public string meshName;
            public int originalTris;
            public int simplifiedTris;
            public float error;
            public int lodLevel;
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

            // Source info
            var sourceMeshes = ctx.MeshEntries
                .Where(e => e.include && e.lodIndex == ctx.SourceLodIndex)
                .ToList();
            int srcCount = sourceMeshes.Count;
            int srcWithRepack = sourceMeshes.Count(e => e.repackedMesh != null);
            EditorGUILayout.LabelField($"Source: LOD{ctx.SourceLodIndex} — {srcCount} mesh(es), {srcWithRepack} repacked", EditorStyles.miniLabel);

            EditorGUILayout.Space(6);

            // ── Settings ──
            generateLodCount = EditorGUILayout.IntSlider("LOD Count", generateLodCount, 1, 4);
            for (int i = 0; i < generateLodCount && i < generateLodRatios.Length; i++)
                generateLodRatios[i] = EditorGUILayout.Slider("  LOD" + (ctx.SourceLodIndex + i + 1) + " ratio", generateLodRatios[i], 0.01f, 0.99f);

            EditorGUILayout.Space(4);
            generateTargetError = EditorGUILayout.Slider("Target Error", generateTargetError, 0.001f, 0.5f);
            generateUv2Weight = EditorGUILayout.Slider("UV2 Weight", generateUv2Weight, 0f, 500f);
            generateNormalWeight = EditorGUILayout.Slider("Normal Weight", generateNormalWeight, 0f, 10f);
            generateLockBorder = EditorGUILayout.Toggle("Lock Border", generateLockBorder);
            generateAddToLodGroup = EditorGUILayout.Toggle("Add to LODGroup", generateAddToLodGroup);

            EditorGUILayout.Space(6);

            // ── Generate button ──
            var c = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.7f, .4f, .95f);
            if (GUILayout.Button("Generate LODs", GUILayout.Height(30)))
                ExecGenerateLods();
            GUI.backgroundColor = c;

            // ── Results ──
            if (lastResults.Count > 0)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Results", EditorStyles.boldLabel);
                foreach (var r in lastResults)
                {
                    float pct = r.originalTris > 0 ? (float)r.simplifiedTris / r.originalTris * 100f : 0;
                    EditorGUILayout.LabelField(
                        $"  LOD{r.lodLevel}: {r.meshName} — {r.originalTris} → {r.simplifiedTris} tris ({pct:F0}%), err={r.error:F4}",
                        EditorStyles.miniLabel);
                }
            }
        }

        void ExecGenerateLods()
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
                        $"LOD {lodIdx + 1}/{generateLodCount} (ratio {ratio:P0})", progress);

                    var lodRenderers = new List<Renderer>();
                    int lodLevel = ctx.SourceLodIndex + lodIdx + 1;

                    foreach (var (entry, srcMesh) in sourceMeshes)
                    {
                        var r = MeshSimplifier.Simplify(srcMesh, settings);
                        if (!r.ok) { UvtLog.Error($"[GenerateLOD] Failed on {srcMesh.name}: {r.error}"); continue; }

                        string meshName = srcMesh.name + "_LOD" + lodLevel;
                        r.simplifiedMesh.name = meshName;
                        string assetPath = AssetDatabase.GenerateUniqueAssetPath(savePath + "/" + meshName + ".asset");
                        AssetDatabase.CreateAsset(r.simplifiedMesh, assetPath);
                        UvtLog.Info($"[GenerateLOD] {meshName}: {r.originalTriCount} → {r.simplifiedTriCount} tris, saved → {assetPath}");

                        lastResults.Add(new GeneratedLodInfo
                        {
                            meshName = meshName,
                            originalTris = r.originalTriCount,
                            simplifiedTris = r.simplifiedTriCount,
                            error = r.resultError,
                            lodLevel = lodLevel
                        });

                        if (generateAddToLodGroup && entry.renderer != null)
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

                    if (generateAddToLodGroup && lodRenderers.Count > 0)
                    {
                        float baseHeight = newLods.Count > 0 ? newLods[newLods.Count - 1].screenRelativeTransitionHeight : 0.5f;
                        float height = baseHeight * 0.5f;
                        var newLod = new LOD(height, lodRenderers.ToArray());
                        if (lodLevel < newLods.Count) newLods.Insert(lodLevel, newLod);
                        else newLods.Add(newLod);
                    }
                }

                if (generateAddToLodGroup)
                {
                    Undo.RecordObject(ctx.LodGroup, "Generate LODs");
                    ctx.LodGroup.SetLODs(newLods.ToArray());
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            finally { EditorUtility.ClearProgressBar(); }

            ctx.Refresh(ctx.LodGroup);
            OnRefresh();
            requestRepaint?.Invoke();
            // Re-populate results after refresh (Refresh clears them)
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
