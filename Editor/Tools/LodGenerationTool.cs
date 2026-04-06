// LodGenerationTool.cs — LOD generation via meshoptimizer simplification.
// Preserves UV2 lightmap coordinates with configurable weights.
//
// Workflow: UV2 Transfer (Analyze → Weld → Repack → Transfer) → LOD Gen → Export FBX
// Generation continues after the last existing LOD level automatically.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using UnityEditor.Formats.Fbx.Exporter;
#endif

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
        bool exportToFbx = true;

        // ── Results ──
        List<GeneratedLodInfo> lastResults = new List<GeneratedLodInfo>();
        Dictionary<string, List<(MeshEntry entry, Mesh generatedMesh, string meshName)>> generatedPerFbx
            = new Dictionary<string, List<(MeshEntry, Mesh, string)>>();

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
        public void OnRefresh() { lastResults.Clear(); generatedPerFbx.Clear(); }

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
            bool hasTransfer = ctx.MeshEntries.Any(e => e.transferredMesh != null);
            if (!hasRepack && !hasTransfer)
            {
                EditorGUILayout.HelpBox(
                    "Recommended workflow:\n" +
                    "1. UV2 Transfer: Analyze → Weld → Repack → Transfer\n" +
                    "2. LOD Gen: Generate new LOD levels (uses repacked UV2)\n" +
                    "3. Export FBX (Overwrite Source)\n\n" +
                    "Important: Generate LODs BEFORE exporting FBX.\n" +
                    "LODs are simplified from the repacked source mesh\n" +
                    "which has the new UV2 lightmap coordinates.",
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

            // First new LOD level = after last existing
            int startLod = lastExistingLod + 1;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField($"Generate LOD{startLod}+", EditorStyles.boldLabel);

            // ── Settings ──
            generateLodCount = EditorGUILayout.IntSlider("Count", generateLodCount, 1, 4);

            // Find last LOD's ratio relative to source
            float lastRatio = 1f;
            if (sourceTris > 0)
            {
                if (lastExistingLod > ctx.SourceLodIndex)
                {
                    int lastLodTris = 0;
                    foreach (var e in ctx.ForLod(lastExistingLod))
                    {
                        Mesh m = e.repackedMesh ?? e.originalMesh ?? e.fbxMesh;
                        if (m != null) lastLodTris += m.triangles.Length / 3;
                    }
                    lastRatio = sourceTris > 0 ? (float)lastLodTris / sourceTris : 0.25f;
                }

                // Auto-init ratios as halving from last existing
                for (int i = 0; i < generateLodCount && i < generateLodRatios.Length; i++)
                {
                    if (generateLodRatios[i] >= lastRatio && lastRatio > 0.01f)
                        generateLodRatios[i] = lastRatio * Mathf.Pow(0.5f, i + 1);
                }
            }

            for (int i = 0; i < generateLodCount && i < generateLodRatios.Length; i++)
            {
                // Clamp: each LOD must have fewer tris than the previous
                float maxRatio = i == 0 ? lastRatio * 0.99f : generateLodRatios[i - 1] * 0.99f;
                if (maxRatio < 0.001f) maxRatio = 0.001f;
                if (generateLodRatios[i] > maxRatio) generateLodRatios[i] = maxRatio * 0.5f;

                int targetLod = startLod + i;
                generateLodRatios[i] = EditorGUILayout.Slider(
                    $"  LOD{targetLod}", generateLodRatios[i], 0.001f, maxRatio);
                int estTris = Mathf.RoundToInt(sourceTris * generateLodRatios[i]);
                float estPct = sourceTris > 0 ? generateLodRatios[i] * 100f : 0;
                EditorGUILayout.LabelField($"      ≈ {estTris:N0} tris ({estPct:F0}% of source)", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            generateTargetError = EditorGUILayout.Slider("Target Error", generateTargetError, 0.001f, 0.5f);
            generateUv2Weight = EditorGUILayout.Slider("UV2 Weight", generateUv2Weight, 0f, 500f);
            generateNormalWeight = EditorGUILayout.Slider("Normal Weight", generateNormalWeight, 0f, 10f);
            generateLockBorder = EditorGUILayout.Toggle("Lock Border", generateLockBorder);
            generateAddToLodGroup = EditorGUILayout.Toggle("Add to LODGroup", generateAddToLodGroup);

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            exportToFbx = EditorGUILayout.Toggle("Auto-export to FBX", exportToFbx);
#endif

            EditorGUILayout.Space(6);

            // ── Generate button ──
            var bg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.7f, .4f, .95f);
            if (GUILayout.Button($"Generate LOD{startLod}–LOD{startLod + generateLodCount - 1}", GUILayout.Height(30)))
                ExecGenerateLods(startLod);
            GUI.backgroundColor = bg;

            // ── FBX Export buttons (after generation) ──
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (generatedPerFbx.Count > 0)
            {
                EditorGUILayout.Space(4);
                GUI.backgroundColor = new Color(.4f, .7f, .95f);
                if (GUILayout.Button("Export LODs as New FBX", GUILayout.Height(24)))
                    ExportGeneratedLods(false);
                EditorGUILayout.Space(2);
                GUI.backgroundColor = new Color(.95f, .6f, .2f);
                if (GUILayout.Button("Add LODs to Source FBX", GUILayout.Height(24)))
                    ExportGeneratedLods(true);
                GUI.backgroundColor = bg;
            }
#endif

            // ── Results ──
            if (lastResults.Count > 0)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Generated", EditorStyles.boldLabel);
                foreach (var r in lastResults)
                {
                    float pct = sourceTris > 0 ? (float)r.simplifiedTris / sourceTris * 100f : 0;
                    EditorGUILayout.LabelField(
                        $"  LOD{r.lodLevel}: {r.meshName} — {r.simplifiedTris:N0} tris ({pct:F0}%)",
                        EditorStyles.miniLabel);
                }
            }
        }

        void ExecGenerateLods(int startLod)
        {
            if (ctx.LodGroup == null) return;
            lastResults.Clear();
            generatedPerFbx.Clear();

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
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            bool saveAsAsset = !exportToFbx;
#else
            bool saveAsAsset = true;
#endif
            if (saveAsAsset && !AssetDatabase.IsValidFolder(savePath))
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

                        // Warn if error limit prevented reaching target ratio
                        float actualRatio = srcMesh.triangles.Length > 0 ? (float)r.simplifiedTriCount / (srcMesh.triangles.Length / 3) : 1f;
                        if (actualRatio > ratio * 1.5f)
                            UvtLog.Warn($"[GenerateLOD] LOD{lodLevel}: target {ratio:P0} but got {actualRatio:P0} — increase Target Error to simplify further");

                        string baseName = entry.fbxMesh != null ? entry.fbxMesh.name : srcMesh.name;
                        baseName = System.Text.RegularExpressions.Regex.Replace(baseName, @"(_wc|_repack|_uvTransfer|_optimized|_LOD\d+)+$", "");
                        string meshName = baseName + "_LOD" + lodLevel;
                        r.simplifiedMesh.name = meshName;

                        PreserveUvChannels(r.simplifiedMesh, entry.fbxMesh ?? entry.originalMesh);

                        if (saveAsAsset)
                        {
                            string assetPath = AssetDatabase.GenerateUniqueAssetPath(savePath + "/" + meshName + ".asset");
                            AssetDatabase.CreateAsset(r.simplifiedMesh, assetPath);
                        }
                        UvtLog.Info($"[GenerateLOD] {meshName}: {r.originalTriCount} → {r.simplifiedTriCount} tris");

                        lastResults.Add(new GeneratedLodInfo
                        {
                            meshName = meshName,
                            originalTris = r.originalTriCount,
                            simplifiedTris = r.simplifiedTriCount,
                            error = r.resultError,
                            lodLevel = lodLevel
                        });

                        Mesh pathMesh = entry.fbxMesh ?? entry.originalMesh;
                        string fbxPath = pathMesh != null ? AssetDatabase.GetAssetPath(pathMesh) : null;
                        if (!string.IsNullOrEmpty(fbxPath))
                        {
                            if (!generatedPerFbx.ContainsKey(fbxPath))
                                generatedPerFbx[fbxPath] = new List<(MeshEntry, Mesh, string)>();
                            generatedPerFbx[fbxPath].Add((entry, r.simplifiedMesh, meshName));
                        }

                        // Only create scene GameObjects if NOT auto-exporting to FBX
                        // (FBX reimport will create them from the exported file)
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                        bool createSceneObjects = generateAddToLodGroup && !exportToFbx;
#else
                        bool createSceneObjects = generateAddToLodGroup;
#endif
                        if (createSceneObjects && entry.renderer != null)
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

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                bool updateLodGroup = generateAddToLodGroup && !exportToFbx;
#else
                bool updateLodGroup = generateAddToLodGroup;
#endif
                if (updateLodGroup)
                {
                    Undo.RecordObject(ctx.LodGroup, "Generate LODs");
                    ctx.LodGroup.SetLODs(newLods.ToArray());
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            finally { EditorUtility.ClearProgressBar(); }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (exportToFbx && generatedPerFbx.Count > 0)
                ExportGeneratedLods(true);
#endif

            // Add new LOD entries WITHOUT destroying existing pipeline state
            // (ctx.Refresh would clear repack/transfer/weld state on LOD0-2)
            if (generateAddToLodGroup)
            {
                var lods = ctx.LodGroup.GetLODs();
                for (int li = 0; li < lods.Length; li++)
                {
                    // Only add entries for LOD levels we don't already have
                    if (ctx.MeshEntries.Any(e => e.lodIndex == li)) continue;
                    if (lods[li].renderers == null) continue;
                    foreach (var r in lods[li].renderers)
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
            }
            ctx.ClearAllCaches();
            requestRepaint?.Invoke();
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        void ExportGeneratedLods(bool addToSource)
        {
            foreach (var kv in generatedPerFbx)
            {
                string sourceFbxPath = kv.Key;
                var generated = kv.Value;

                var fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourceFbxPath);
                if (fbxPrefab == null) { UvtLog.Error("[FBX LOD Export] Cannot load: " + sourceFbxPath); continue; }

                string exportPath;
                if (addToSource)
                {
                    exportPath = sourceFbxPath;
                    string fullSource = System.IO.Path.GetFullPath(sourceFbxPath);
                    string fullMeta = fullSource + ".meta";
                    try
                    {
                        System.IO.File.Copy(fullSource, fullSource + ".bak", true);
                        if (System.IO.File.Exists(fullMeta))
                            System.IO.File.Copy(fullMeta, fullSource + ".meta.bak", true);
                    }
                    catch (Exception ex) { UvtLog.Error("[FBX LOD Export] Backup failed: " + ex.Message); continue; }
                }
                else
                {
                    string dir = System.IO.Path.GetDirectoryName(sourceFbxPath);
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(sourceFbxPath);
                    exportPath = EditorUtility.SaveFilePanel("Export LODs FBX", dir, baseName + "_lods.fbx", "fbx");
                    if (string.IsNullOrEmpty(exportPath)) continue;
                    string dataPath = Application.dataPath;
                    if (exportPath.StartsWith(dataPath))
                        exportPath = "Assets" + exportPath.Substring(dataPath.Length);
                }

                var tempRoot = UnityEngine.Object.Instantiate(fbxPrefab);
                tempRoot.name = fbxPrefab.name;
                try
                {
                    // Remove existing children with same names to avoid duplicates
                    var namesToAdd = new HashSet<string>(generated.Select(g => g.meshName));
                    for (int i = tempRoot.transform.childCount - 1; i >= 0; i--)
                    {
                        var ch = tempRoot.transform.GetChild(i);
                        if (namesToAdd.Contains(ch.name))
                            UnityEngine.Object.DestroyImmediate(ch.gameObject);
                    }

                    foreach (var (entry, genMesh, meshName) in generated)
                    {
                        var child = new GameObject(meshName);
                        child.transform.SetParent(tempRoot.transform, false);
                        if (entry.renderer != null)
                        {
                            var srcT = entry.renderer.transform;
                            var lodRoot = ctx.LodGroup != null ? ctx.LodGroup.transform : srcT.parent;
                            child.transform.localPosition = lodRoot.InverseTransformPoint(srcT.position);
                            child.transform.localRotation = Quaternion.Inverse(lodRoot.rotation) * srcT.rotation;
                            child.transform.localScale = srcT.localScale;
                        }
                        var mf = child.AddComponent<MeshFilter>();
                        mf.sharedMesh = genMesh;
                        var mr = child.AddComponent<MeshRenderer>();
                        if (entry.renderer != null)
                            mr.sharedMaterials = entry.renderer.sharedMaterials;
                    }

                    var exportOptions = new ExportModelOptions { ExportFormat = ExportFormat.Binary };
                    ModelExporter.ExportObjects(exportPath, new UnityEngine.Object[] { tempRoot }, exportOptions);
                    UvtLog.Info("[FBX LOD Export] " + generated.Count + " mesh(es) -> " + exportPath);

                    if (addToSource)
                    {
                        string fullPath = System.IO.Path.GetFullPath(sourceFbxPath);
                        // Restore .meta
                        string metaBak = fullPath + ".meta.bak";
                        if (System.IO.File.Exists(metaBak))
                        {
                            System.IO.File.Copy(metaBak, fullPath + ".meta", true);
                            System.IO.File.Delete(metaBak);
                        }
                        // Delete .fbx.bak to avoid Unity warning about orphan .meta
                        string fbxBak = fullPath + ".bak";
                        if (System.IO.File.Exists(fbxBak))
                            System.IO.File.Delete(fbxBak);
                    }
                }
                catch (Exception ex) { UvtLog.Error("[FBX LOD Export] Failed: " + ex); }
                finally { UnityEngine.Object.DestroyImmediate(tempRoot); }
            }
            AssetDatabase.Refresh();
        }
#endif

        static void PreserveUvChannels(Mesh exportMesh, Mesh sourceMesh)
        {
            if (sourceMesh == null || exportMesh == null) return;
            if (sourceMesh.vertexCount != exportMesh.vertexCount) return;
            for (int ch = 0; ch < 8; ch++)
            {
                var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
                if (exportMesh.HasVertexAttribute(attr)) continue;
                if (!sourceMesh.HasVertexAttribute(attr)) continue;
                var uv = new List<Vector2>();
                sourceMesh.GetUVs(ch, uv);
                if (uv.Count == 0) continue;
                bool allZero = true;
                for (int i = 0; i < uv.Count; i++)
                    if (uv[i].x != 0f || uv[i].y != 0f) { allZero = false; break; }
                if (allZero) continue;
                exportMesh.SetUVs(ch, uv);
            }
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
