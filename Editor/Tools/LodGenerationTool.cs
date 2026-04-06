// LodGenerationTool.cs — LOD generation via meshoptimizer simplification.
// Preserves UV2 lightmap coordinates with configurable weights.

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
        // Generated meshes per source FBX path for FBX export
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

            // Source info
            var sourceMeshes = ctx.MeshEntries
                .Where(e => e.include && e.lodIndex == ctx.SourceLodIndex)
                .ToList();

            // ── LOD Polycount Table ──
            EditorGUILayout.LabelField("Current LODs", EditorStyles.boldLabel);
            int totalSrcTris = 0;
            for (int li = 0; li < ctx.LodCount; li++)
            {
                var ee = ctx.ForLod(li);
                if (ee.Count == 0) continue;
                int lodTris = 0, lodVerts = 0;
                foreach (var e in ee)
                {
                    Mesh m = e.repackedMesh ?? e.originalMesh ?? e.fbxMesh;
                    if (m == null) continue;
                    lodTris += m.triangles.Length / 3;
                    lodVerts += m.vertexCount;
                }
                if (li == ctx.SourceLodIndex) totalSrcTris = lodTris;
                bool isSrc = li == ctx.SourceLodIndex;
                string prefix = isSrc ? "► " : "  ";
                float pct = totalSrcTris > 0 ? (float)lodTris / totalSrcTris * 100f : 100f;
                EditorGUILayout.LabelField($"{prefix}LOD{li}: {lodTris:N0} tris, {lodVerts:N0} verts ({pct:F0}%)",
                    isSrc ? EditorStyles.boldLabel : EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Generate New LODs", EditorStyles.boldLabel);

            // ── Settings ──
            generateLodCount = EditorGUILayout.IntSlider("LOD Count", generateLodCount, 1, 4);
            for (int i = 0; i < generateLodCount && i < generateLodRatios.Length; i++)
            {
                int targetLod = ctx.SourceLodIndex + i + 1;
                int estTris = Mathf.RoundToInt(totalSrcTris * generateLodRatios[i]);
                string existing = targetLod < ctx.LodCount ? " (exists!)" : "";
                generateLodRatios[i] = EditorGUILayout.Slider(
                    $"  LOD{targetLod} ratio", generateLodRatios[i], 0.01f, 0.99f);
                EditorGUILayout.LabelField($"      ≈ {estTris:N0} tris{existing}", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            generateTargetError = EditorGUILayout.Slider("Target Error", generateTargetError, 0.001f, 0.5f);
            generateUv2Weight = EditorGUILayout.Slider("UV2 Weight", generateUv2Weight, 0f, 500f);
            generateNormalWeight = EditorGUILayout.Slider("Normal Weight", generateNormalWeight, 0f, 10f);
            generateLockBorder = EditorGUILayout.Toggle("Lock Border", generateLockBorder);
            generateAddToLodGroup = EditorGUILayout.Toggle("Add to LODGroup", generateAddToLodGroup);

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            exportToFbx = EditorGUILayout.Toggle("Export to FBX", exportToFbx);
#endif

            EditorGUILayout.Space(6);

            // ── Generate button ──
            var c = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.7f, .4f, .95f);
            if (GUILayout.Button("Generate LODs", GUILayout.Height(30)))
                ExecGenerateLods();
            GUI.backgroundColor = c;

            // ── FBX Export buttons (after generation) ──
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (generatedPerFbx.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("FBX Export", EditorStyles.boldLabel);
                GUI.backgroundColor = new Color(.4f, .7f, .95f);
                if (GUILayout.Button("Export LODs as New FBX", GUILayout.Height(24)))
                    ExportGeneratedLods(false);
                EditorGUILayout.Space(2);
                GUI.backgroundColor = new Color(.95f, .6f, .2f);
                if (GUILayout.Button("Add LODs to Source FBX", GUILayout.Height(24)))
                    ExportGeneratedLods(true);
                GUI.backgroundColor = c;
            }
#endif

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

                        string baseName = entry.fbxMesh != null ? entry.fbxMesh.name : srcMesh.name;
                        baseName = System.Text.RegularExpressions.Regex.Replace(baseName, @"(_wc|_repack|_uvTransfer|_optimized|_LOD\d+)+$", "");
                        string meshName = baseName + "_LOD" + lodLevel;
                        r.simplifiedMesh.name = meshName;

                        // Preserve all UV channels from source
                        PreserveUvChannels(r.simplifiedMesh, entry.fbxMesh ?? entry.originalMesh);

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

                        // Track for FBX export
                        Mesh pathMesh = entry.fbxMesh ?? entry.originalMesh;
                        string fbxPath = pathMesh != null ? AssetDatabase.GetAssetPath(pathMesh) : null;
                        if (!string.IsNullOrEmpty(fbxPath))
                        {
                            if (!generatedPerFbx.ContainsKey(fbxPath))
                                generatedPerFbx[fbxPath] = new List<(MeshEntry, Mesh, string)>();
                            generatedPerFbx[fbxPath].Add((entry, r.simplifiedMesh, meshName));
                        }

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
                        if (lodLevel < newLods.Count)
                        {
                            // Replace existing LOD — destroy old renderers' GameObjects
                            var oldRenderers = newLods[lodLevel].renderers;
                            if (oldRenderers != null)
                            {
                                foreach (var oldR in oldRenderers)
                                {
                                    if (oldR != null && oldR.gameObject != null)
                                    {
                                        Undo.DestroyObjectImmediate(oldR.gameObject);
                                    }
                                }
                            }
                            float existingHeight = newLods[lodLevel].screenRelativeTransitionHeight;
                            newLods[lodLevel] = new LOD(existingHeight, lodRenderers.ToArray());
                        }
                        else
                        {
                            float baseHeight = newLods.Count > 0 ? newLods[newLods.Count - 1].screenRelativeTransitionHeight : 0.5f;
                            float height = baseHeight * 0.5f;
                            newLods.Add(new LOD(height, lodRenderers.ToArray()));
                        }
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

            // Auto-export to FBX if enabled
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (exportToFbx && generatedPerFbx.Count > 0)
                ExportGeneratedLods(true);
#endif

            ctx.Refresh(ctx.LodGroup);
            OnRefresh();
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
                    // Backup
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

                // Clone original FBX hierarchy
                var tempRoot = UnityEngine.Object.Instantiate(fbxPrefab);
                tempRoot.name = fbxPrefab.name;
                try
                {
                    // Add generated LOD meshes as new children
                    foreach (var (entry, genMesh, meshName) in generated)
                    {
                        var child = new GameObject(meshName);
                        child.transform.SetParent(tempRoot.transform, false);
                        // Copy transform from source renderer
                        if (entry.renderer != null)
                        {
                            // Use local transform relative to LODGroup root
                            var srcT = entry.renderer.transform;
                            var lodRoot = ctx.LodGroup.transform;
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
                    UvtLog.Info("[FBX LOD Export] Exported " + generated.Count + " LOD mesh(es) -> " + exportPath);

                    // Restore .meta for overwrite
                    if (addToSource)
                    {
                        string metaBak = System.IO.Path.GetFullPath(sourceFbxPath) + ".meta.bak";
                        string metaOrig = System.IO.Path.GetFullPath(sourceFbxPath) + ".meta";
                        if (System.IO.File.Exists(metaBak))
                        {
                            System.IO.File.Copy(metaBak, metaOrig, true);
                            System.IO.File.Delete(metaBak);
                        }
                    }
                }
                catch (Exception ex) { UvtLog.Error("[FBX LOD Export] Failed: " + ex); }
                finally { UnityEngine.Object.DestroyImmediate(tempRoot); }
            }
            AssetDatabase.Refresh();
        }
#endif

        /// <summary>
        /// Copy non-trivial UV channels from source to target mesh (if missing).
        /// </summary>
        static void PreserveUvChannels(Mesh exportMesh, Mesh sourceMesh)
        {
            if (sourceMesh == null || exportMesh == null) return;
            // For simplified meshes vertex count differs — can't copy per-vertex data directly.
            // But we ensure the simplifier preserves attributes via its own pipeline.
            // This method handles the case where the source has UV channels the simplifier didn't copy.
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
