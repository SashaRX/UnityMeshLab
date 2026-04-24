// CollisionMeshTool.cs — Collision mesh generation tool (IUvTool tab).
// Two modes: Simplified (non-convex MeshCollider) and Convex Decomposition (compound convex MeshColliders).

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    public class CollisionMeshTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        Action requestRepaint;

        public string ToolName  => "Collision";
        public string ToolId    => "collision_mesh";
        public int    ToolOrder => 40;

        public Action RequestRepaint { set => requestRepaint = value; }

        // ── Mode ──
        enum CollisionMode { Simplified = 0, ConvexDecomposition = 1 }
        CollisionMode mode = CollisionMode.Simplified;

        // ── Simplified settings ──
        float simplifyTargetRatio = 0.05f;
        float simplifyTargetError = 0.5f;

        // ── Convex decomposition settings ──
        int   convexMaxHulls        = 16;
        int   convexResolution      = 100000;
        int   convexMaxVertsPerHull = 64;
        int   convexMaxRecursionDepth = 10;
        bool  convexShrinkWrap      = true;
        int   convexFillMode        = 0; // 0=FloodFill, 1=SurfaceOnly, 2=RaycastFill
        int   convexMinEdgeLength   = 2;
        bool  convexFindBestPlane   = false;

        // ── Results ──
        CollisionMode generatedMode; // mode used during last Generate (for Apply)
        List<GeneratedCollisionInfo> lastResults = new List<GeneratedCollisionInfo>();
        List<Mesh> generatedMeshes = new List<Mesh>(); // kept alive for Apply and preview
        List<Transform> sourceTransforms = new List<Transform>(); // per-result source transforms

        struct GeneratedCollisionInfo
        {
            public string meshName;
            public int sourceTriCount;
            public int resultTriCount;
            public int hullCount;
            public float resultError;
            public Transform sourceTransform; // renderer transform for correct placement
        }

        // ── Lifecycle ──

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            this.ctx = ctx;
            this.canvas = canvas;
            SceneView.duringSceneGui += OnSceneGUIInternal;
        }

        public void OnDeactivate()
        {
            SceneView.duringSceneGui -= OnSceneGUIInternal;
            DestroyGeneratedMeshes();
        }

        public void OnRefresh()
        {
            DestroyGeneratedMeshes();
        }

        void DestroyGeneratedMeshes()
        {
            foreach (var m in generatedMeshes)
                if (m != null) UnityEngine.Object.DestroyImmediate(m);
            generatedMeshes.Clear();
            lastResults.Clear();
            sourceTransforms.Clear();
        }

        // ── UI: Sidebar ──

        public void OnDrawSidebar()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Collision Mesh Generation", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (ctx.LodGroup == null)
            {
                EditorGUILayout.HelpBox(
                    "Select a LODGroup in the UV2 Transfer tab to generate collision meshes.",
                    MessageType.Info);
                return;
            }

            // Mode selection
            EditorGUILayout.LabelField("Mode", EditorStyles.boldLabel);
            mode = (CollisionMode)GUILayout.Toolbar((int)mode, new[] { "Simplified", "Convex Decomp" });
            EditorGUILayout.Space(4);

            if (mode == CollisionMode.Simplified)
                DrawSimplifiedSettings();
            else
                DrawConvexSettings();

            EditorGUILayout.Space(8);

            // Generate button
            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.4f, .8f, .4f);
            if (GUILayout.Button("Generate Collision Mesh", GUILayout.Height(28)))
                ExecuteGenerate();
            GUI.backgroundColor = bgc;

            // Results
            if (lastResults.Count > 0)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Results", EditorStyles.boldLabel);

                foreach (var r in lastResults)
                {
                    if (mode == CollisionMode.Simplified)
                    {
                        float pct = r.sourceTriCount > 0 ? (r.resultTriCount * 100f / r.sourceTriCount) : 0;
                        EditorGUILayout.LabelField(
                            $"  {r.meshName}: {r.sourceTriCount:N0} \u2192 {r.resultTriCount:N0} tris ({pct:F1}%)",
                            EditorStyles.miniLabel);
                    }
                    else
                    {
                        EditorGUILayout.LabelField(
                            $"  {r.meshName}: {r.hullCount} hulls, {r.resultTriCount:N0} total tris",
                            EditorStyles.miniLabel);
                    }
                }

                EditorGUILayout.Space(4);
                var bgcClear = GUI.backgroundColor;
                GUI.backgroundColor = new Color(.9f, .3f, .3f);
                if (GUILayout.Button("Clear Results", GUILayout.Height(20)))
                {
                    DestroyGeneratedMeshes();
                    SceneView.RepaintAll();
                    requestRepaint?.Invoke();
                }
                GUI.backgroundColor = bgcClear;

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Scene", EditorStyles.boldLabel);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Apply to Scene", GUILayout.Height(24)))
                    ApplyToScene();
                if (GUILayout.Button("Remove from Scene", GUILayout.Height(24)))
                    RemoveFromScene();
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Persist", EditorStyles.boldLabel);

                var bgc2 = GUI.backgroundColor;
                GUI.backgroundColor = new Color(.3f, .85f, .4f);
                if (GUILayout.Button("Save to Sidecar (for FBX reimport)", GUILayout.Height(24)))
                    SaveToSidecar();
                GUI.backgroundColor = bgc2;
            }

            // Existing collision objects info
            if (ctx.LodGroup != null)
            {
                var existing = FindExistingCollisionObjects(ctx.LodGroup.transform);
                if (existing.Count > 0)
                {
                    EditorGUILayout.Space(8);
                    EditorGUILayout.LabelField("Existing Collision Objects", EditorStyles.boldLabel);
                    foreach (var go in existing)
                    {
                        var mc = go.GetComponent<MeshCollider>();
                        string info = mc != null && mc.sharedMesh != null
                            ? $"{mc.sharedMesh.triangles.Length / 3} tris, convex={mc.convex}"
                            : "no mesh";
                        EditorGUILayout.LabelField($"  {go.name} ({info})", EditorStyles.miniLabel);
                    }
                }
            }
        }

        static readonly string[] fillModeNames = { "Flood Fill", "Surface Only", "Raycast Fill" };

        void DrawSimplifiedSettings()
        {
            EditorGUILayout.HelpBox("For static objects, terrain, walls. Creates a single non-convex MeshCollider.", MessageType.None);
            simplifyTargetRatio = EditorGUILayout.Slider(
                new GUIContent("Target Ratio", "Fraction of triangles to keep (0.05 = 5%). Lower = fewer triangles, rougher shape."),
                simplifyTargetRatio, 0.01f, 0.5f);
            simplifyTargetError = EditorGUILayout.Slider(
                new GUIContent("Target Error", "Maximum allowed geometric error. Higher = more aggressive simplification, less accurate shape."),
                simplifyTargetError, 0.01f, 1.0f);
        }

        void DrawConvexSettings()
        {
            EditorGUILayout.HelpBox("For dynamic/kinematic objects. Creates compound convex MeshColliders.", MessageType.None);
            convexMaxHulls = EditorGUILayout.IntSlider(
                new GUIContent("Max Hulls", "Maximum number of convex hulls to produce. More hulls = better shape approximation, higher cost."),
                convexMaxHulls, 1, 64);
            convexResolution = EditorGUILayout.IntField(
                new GUIContent("Resolution", "Voxel grid resolution. Higher = more precise decomposition, slower computation. (10K\u20131M)"),
                convexResolution);
            convexResolution = Mathf.Clamp(convexResolution, 10000, 1000000);
            convexMaxVertsPerHull = EditorGUILayout.IntSlider(
                new GUIContent("Max Verts/Hull", "Maximum vertices per convex hull. PhysX hard limit is 255. Lower = simpler hulls."),
                convexMaxVertsPerHull, 8, 255);

            if (convexMaxVertsPerHull > 128)
                EditorGUILayout.HelpBox("Values above 128 may cause issues with some physics engines.", MessageType.Warning);

            EditorGUILayout.Space(4);
            convexFillMode = EditorGUILayout.Popup(
                new GUIContent("Fill Mode", "How to determine inside vs outside.\n\nFlood Fill: default, works for closed meshes.\nSurface Only: hollow result, for thin shells.\nRaycast Fill: better for meshes with holes."),
                convexFillMode, fillModeNames);
            convexMaxRecursionDepth = EditorGUILayout.IntSlider(
                new GUIContent("Max Recursion", "Maximum depth of recursive splitting. Higher = finer decomposition, slower. Default: 10."),
                convexMaxRecursionDepth, 1, 25);
            convexMinEdgeLength = EditorGUILayout.IntSlider(
                new GUIContent("Min Edge Length", "Stop recursing when voxel patch edge is below this length. Lower = more detail. Default: 2."),
                convexMinEdgeLength, 1, 8);
            convexShrinkWrap = EditorGUILayout.Toggle(
                new GUIContent("Shrink Wrap", "Snap voxel hull vertices back to the original mesh surface for tighter fit."),
                convexShrinkWrap);
            convexFindBestPlane = EditorGUILayout.Toggle(
                new GUIContent("Find Best Plane", "Experimental: search for optimal split plane instead of axis-aligned. Slower but can produce better results."),
                convexFindBestPlane);
        }

        // ── Generate ──

        void ExecuteGenerate()
        {
            DestroyGeneratedMeshes();
            generatedMode = mode;

            var sourceMeshEntries = ctx.MeshEntries
                .Where(e => e.lodIndex == ctx.SourceLodIndex && e.include)
                .ToList();

            if (sourceMeshEntries.Count == 0)
            {
                UvtLog.Warn("No source meshes found for collision generation.");
                return;
            }

            foreach (var entry in sourceMeshEntries)
            {
                Mesh sourceMesh = entry.originalMesh ?? entry.fbxMesh;
                if (sourceMesh == null) continue;

                if (mode == CollisionMode.Simplified)
                    GenerateSimplified(entry, sourceMesh);
                else
                    GenerateConvex(entry, sourceMesh);
            }

            if (lastResults.Count > 0)
                UvtLog.Info($"Collision generation complete: {lastResults.Count} mesh(es) processed.");

            requestRepaint?.Invoke();
            SceneView.RepaintAll();
        }

        void GenerateSimplified(MeshEntry entry, Mesh sourceMesh)
        {
            var result = CollisionMeshBuilder.BuildSimplified(
                sourceMesh, simplifyTargetRatio, simplifyTargetError);

            if (!result.ok)
            {
                UvtLog.Warn($"Simplified collision failed for {sourceMesh.name}: {result.error}");
                return;
            }

            generatedMeshes.Add(result.mesh);
            lastResults.Add(new GeneratedCollisionInfo
            {
                meshName        = sourceMesh.name,
                sourceTriCount  = result.sourceTriCount,
                resultTriCount  = result.resultTriCount,
                hullCount       = 1,
                resultError     = result.resultError,
                sourceTransform = entry.renderer != null ? entry.renderer.transform : null
            });
        }

        void GenerateConvex(MeshEntry entry, Mesh sourceMesh)
        {
            var settings = new CollisionMeshBuilder.ConvexDecompSettings
            {
                maxHulls          = convexMaxHulls,
                resolution        = convexResolution,
                maxVertsPerHull   = convexMaxVertsPerHull,
                minVolumePerHull  = 1f,
                maxRecursionDepth = convexMaxRecursionDepth,
                shrinkWrap        = convexShrinkWrap,
                fillMode          = convexFillMode,
                minEdgeLength     = convexMinEdgeLength,
                findBestPlane     = convexFindBestPlane
            };

            var result = CollisionMeshBuilder.BuildConvexDecomposition(sourceMesh, settings);

            if (!result.ok)
            {
                UvtLog.Warn($"Convex decomposition failed for {sourceMesh.name}: {result.error}");
                return;
            }

            int totalTris = 0;
            foreach (var hull in result.hulls)
            {
                generatedMeshes.Add(hull);
                totalTris += hull.triangles.Length / 3;
            }

            lastResults.Add(new GeneratedCollisionInfo
            {
                meshName        = sourceMesh.name,
                sourceTriCount  = result.sourceTriCount,
                resultTriCount  = totalTris,
                hullCount       = result.hulls.Count,
                sourceTransform = entry.renderer != null ? entry.renderer.transform : null
            });
        }

        // ── Apply / Remove ──

        void ApplyToScene()
        {
            if (ctx.LodGroup == null || generatedMeshes.Count == 0) return;

            Transform root = ctx.LodGroup.transform;
            int undoGroup = Undo.GetCurrentGroup();

            if (generatedMode == CollisionMode.Simplified)
            {
                // One _COL child per generated mesh
                int meshIdx = 0;
                foreach (var r in lastResults)
                {
                    if (meshIdx >= generatedMeshes.Count) break;

                    string name = generatedMeshes[meshIdx].name.Replace("_collision", "_COL");
                    var go = new GameObject(name);
                    Undo.RegisterCreatedObjectUndo(go, "Create Collision Mesh");
                    go.transform.SetParent(root, false);

                    // Preserve source renderer transform offset
                    if (r.sourceTransform != null && r.sourceTransform != root)
                    {
                        go.transform.localPosition = root.InverseTransformPoint(r.sourceTransform.position);
                        go.transform.localRotation = Quaternion.Inverse(root.rotation) * r.sourceTransform.rotation;
                        go.transform.localScale = r.sourceTransform.localScale;
                    }

                    var mc = Undo.AddComponent<MeshCollider>(go);
                    mc.sharedMesh = CreateAppliedMeshCopy(generatedMeshes[meshIdx], go.name + "_Mesh");
                    mc.convex = false;
                    meshIdx++;
                }
            }
            else
            {
                // Convex: group hulls under a _COL parent
                int meshIdx = 0;
                foreach (var r in lastResults)
                {
                    string containerName = r.meshName + "_COL";
                    var container = new GameObject(containerName);
                    Undo.RegisterCreatedObjectUndo(container, "Create Collision Container");
                    container.transform.SetParent(root, false);

                    // Preserve source renderer transform offset
                    if (r.sourceTransform != null && r.sourceTransform != root)
                    {
                        container.transform.localPosition = root.InverseTransformPoint(r.sourceTransform.position);
                        container.transform.localRotation = Quaternion.Inverse(root.rotation) * r.sourceTransform.rotation;
                        container.transform.localScale = r.sourceTransform.localScale;
                    }

                    for (int h = 0; h < r.hullCount && meshIdx < generatedMeshes.Count; h++, meshIdx++)
                    {
                        var hullGo = new GameObject($"{r.meshName}_COL_Hull{h}");
                        Undo.RegisterCreatedObjectUndo(hullGo, "Create Convex Hull");
                        hullGo.transform.SetParent(container.transform, false);

                        var mc = Undo.AddComponent<MeshCollider>(hullGo);
                        mc.sharedMesh = CreateAppliedMeshCopy(generatedMeshes[meshIdx], hullGo.name + "_Mesh");
                        mc.convex = true;
                    }
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            AssetDatabase.SaveAssets();
            UvtLog.Info("Collision meshes applied to scene.");
        }

        const string AppliedMeshAssetFolder = "Assets/LightmapUvTool/GeneratedCollisionMeshes";

        static Mesh CreateAppliedMeshCopy(Mesh source, string fallbackName)
        {
            if (source == null) return null;

            string meshName = string.IsNullOrEmpty(source.name) ? fallbackName : source.name + "_Applied";
            var instance = UnityEngine.Object.Instantiate(source);
            instance.name = meshName;

            EnsureAppliedMeshFolderExists();
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(AppliedMeshAssetFolder, meshName + ".asset"));
            AssetDatabase.CreateAsset(instance, assetPath.Replace('\\', '/'));
            return AssetDatabase.LoadAssetAtPath<Mesh>(assetPath.Replace('\\', '/'));
        }

        static void EnsureAppliedMeshFolderExists()
        {
            if (AssetDatabase.IsValidFolder("Assets/LightmapUvTool"))
            {
                if (!AssetDatabase.IsValidFolder(AppliedMeshAssetFolder))
                    AssetDatabase.CreateFolder("Assets/LightmapUvTool", "GeneratedCollisionMeshes");
                return;
            }

            AssetDatabase.CreateFolder("Assets", "LightmapUvTool");
            AssetDatabase.CreateFolder("Assets/LightmapUvTool", "GeneratedCollisionMeshes");
        }

        // ── Sidecar persistence ──

        void SaveToSidecar()
        {
            if (ctx.LodGroup == null || generatedMeshes.Count == 0) return;

            // Find source FBX path from source LOD entries
            string fbxPath = null;
            foreach (var e in ctx.MeshEntries)
            {
                if (e.lodIndex != ctx.SourceLodIndex || e.fbxMesh == null) continue;
                string p = AssetDatabase.GetAssetPath(e.fbxMesh);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                { fbxPath = p; break; }
            }
            if (string.IsNullOrEmpty(fbxPath))
            {
                UvtLog.Warn("[Collision] Cannot find source FBX path for sidecar.");
                return;
            }

            // Save mesh assets to savePath (like LOD Gen does)
            string savePath = !string.IsNullOrEmpty(ctx.PipeSettings.savePath)
                ? ctx.PipeSettings.savePath
                : "Assets/LightmapUvTool_Output";
            if (!AssetDatabase.IsValidFolder(savePath))
            {
                var par = Path.GetDirectoryName(savePath);
                var fld = Path.GetFileName(savePath);
                if (!string.IsNullOrEmpty(par)) AssetDatabase.CreateFolder(par, fld);
            }

            var savedMeshAssets = new List<Mesh>();
            foreach (var mesh in generatedMeshes)
            {
                string assetPath = AssetDatabase.GenerateUniqueAssetPath(savePath + "/" + mesh.name + ".asset");
                var copy = UnityEngine.Object.Instantiate(mesh);
                copy.name = mesh.name;
                AssetDatabase.CreateAsset(copy, assetPath);
                savedMeshAssets.Add(AssetDatabase.LoadAssetAtPath<Mesh>(assetPath));
            }

            // Build CollisionMeshEntry and save to sidecar
            string sidecarPath = Uv2DataAsset.GetSidecarPath(fbxPath);
            var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<Uv2DataAsset>();
                AssetDatabase.CreateAsset(data, sidecarPath);
            }

            // Build flattened collision entry per source mesh
            int meshIdx = 0;
            foreach (var r in lastResults)
            {
                // Compute fingerprint from source FBX mesh
                MeshFingerprint fp = null;
                var srcEntry = ctx.MeshEntries.FirstOrDefault(
                    e => e.lodIndex == ctx.SourceLodIndex && e.include &&
                    (e.fbxMesh != null ? e.fbxMesh.name : e.originalMesh?.name) == r.meshName);
                if (srcEntry != null && srcEntry.fbxMesh != null)
                    fp = MeshFingerprint.Compute(srcEntry.fbxMesh);

                var allPos = new List<Vector3>();
                var posOffsets = new List<int>();
                var allTri = new List<int>();
                var triOffsets = new List<int>();

                for (int h = 0; h < r.hullCount && meshIdx < savedMeshAssets.Count; h++, meshIdx++)
                {
                    var m = savedMeshAssets[meshIdx];
                    int posOffset = allPos.Count;
                    posOffsets.Add(posOffset);
                    triOffsets.Add(allTri.Count);
                    allPos.AddRange(m.vertices);
                    // Rebase triangle indices to global vertex offset so
                    // GetCollisionMeshesFromSidecar can correctly extract per-hull data.
                    var localTris = m.triangles;
                    for (int ti = 0; ti < localTris.Length; ti++)
                        localTris[ti] += posOffset;
                    allTri.AddRange(localTris);
                }

                var entry = new CollisionMeshEntry
                {
                    meshGroupKey      = r.meshName,
                    mode              = (int)generatedMode,
                    sourceFingerprint = fp,
                    allPositions      = allPos.ToArray(),
                    positionOffsets   = posOffsets.ToArray(),
                    allTriangles      = allTri.ToArray(),
                    triangleOffsets   = triOffsets.ToArray(),
                    targetRatio       = simplifyTargetRatio,
                    targetError       = simplifyTargetError,
                    maxHulls          = convexMaxHulls,
                    resolution        = convexResolution,
                    maxVertsPerHull   = convexMaxVertsPerHull,
                };

                // Replace existing entry for same meshGroupKey
                data.collisionEntries.RemoveAll(c => c.meshGroupKey == entry.meshGroupKey);
                data.collisionEntries.Add(entry);
            }

            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            UvtLog.Info($"[Collision] Saved to sidecar: {sidecarPath} ({data.collisionEntries.Count} collision entries)");
        }

        /// <summary>
        /// Get the saved collision meshes for FBX export.
        /// Returns list of (meshName, collisionMeshes, isConvex) for each entry.
        /// Called by LightmapTransferTool.ExportFbx.
        /// </summary>
        public static List<(string meshName, List<Mesh> meshes, bool isConvex)> GetCollisionMeshesFromSidecar(string fbxPath)
        {
            var result = new List<(string, List<Mesh>, bool)>();
            string sidecarPath = Uv2DataAsset.GetSidecarPath(fbxPath);
            var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
            if (data == null || data.collisionEntries == null) return result;

            foreach (var entry in data.collisionEntries)
            {
                bool isConvex = entry.mode == 1;
                var meshes = new List<Mesh>();
                int hullCount = entry.positionOffsets.Length;

                for (int h = 0; h < hullCount; h++)
                {
                    int posStart = entry.positionOffsets[h];
                    int posEnd   = (h + 1 < hullCount) ? entry.positionOffsets[h + 1] : entry.allPositions.Length;
                    int triStart = entry.triangleOffsets[h];
                    int triEnd   = (h + 1 < hullCount) ? entry.triangleOffsets[h + 1] : entry.allTriangles.Length;

                    int vertCount = posEnd - posStart;
                    var verts = new Vector3[vertCount];
                    Array.Copy(entry.allPositions, posStart, verts, 0, vertCount);

                    int idxCount = triEnd - triStart;
                    var tris = new int[idxCount];
                    Array.Copy(entry.allTriangles, triStart, tris, 0, idxCount);
                    // Rebase indices to local vertex offset
                    for (int i = 0; i < tris.Length; i++)
                        tris[i] -= posStart;

                    var mesh = new Mesh();
                    mesh.name = isConvex
                        ? $"{entry.meshGroupKey}_COL_Hull{h}"
                        : $"{entry.meshGroupKey}_COL";
                    mesh.SetVertices(verts);
                    mesh.SetTriangles(tris, 0);
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    meshes.Add(mesh);
                }

                result.Add((entry.meshGroupKey, meshes, isConvex));
            }
            return result;
        }

        void RemoveFromScene()
        {
            if (ctx.LodGroup == null) return;

            var existing = FindExistingCollisionObjects(ctx.LodGroup.transform);
            if (existing.Count == 0)
            {
                UvtLog.Info("No collision objects found to remove.");
                return;
            }

            int undoGroup = Undo.GetCurrentGroup();
            foreach (var go in existing)
                Undo.DestroyObjectImmediate(go);
            Undo.CollapseUndoOperations(undoGroup);

            UvtLog.Info($"Removed {existing.Count} collision object(s).");
        }

        static List<GameObject> FindExistingCollisionObjects(Transform root)
        {
            var result = new List<GameObject>();
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.name.Contains("_COL"))
                {
                    result.Add(child.gameObject);
                    // Also add grandchildren (hull objects)
                    for (int j = 0; j < child.childCount; j++)
                    {
                        if (child.GetChild(j).name.Contains("_COL"))
                            result.Add(child.GetChild(j).gameObject);
                    }
                }
            }
            return result;
        }

        // ── Scene view: wireframe preview ──

        void OnSceneGUIInternal(SceneView sv)
        {
            if (ctx?.LodGroup == null || generatedMeshes.Count == 0) return;

            Transform t = ctx.LodGroup.transform;
            Matrix4x4 matrix = t.localToWorldMatrix;

            for (int i = 0; i < generatedMeshes.Count; i++)
            {
                var mesh = generatedMeshes[i];
                if (mesh == null) continue;

                Color c = UvCanvasView.pal[i % UvCanvasView.pal.Length];
                c.a = 0.6f;
                Handles.color = c;

                var verts = mesh.vertices;
                var tris  = mesh.triangles;

                for (int ti = 0; ti < tris.Length; ti += 3)
                {
                    Vector3 a = matrix.MultiplyPoint3x4(verts[tris[ti]]);
                    Vector3 b = matrix.MultiplyPoint3x4(verts[tris[ti + 1]]);
                    Vector3 c2 = matrix.MultiplyPoint3x4(verts[tris[ti + 2]]);

                    Handles.DrawLine(a, b);
                    Handles.DrawLine(b, c2);
                    Handles.DrawLine(c2, a);
                }
            }
        }

        // ── Unused interface methods ──

        public void OnDrawToolbarExtra() { }
        public void OnDrawStatusBar() { }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { }

        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes()
        {
            yield break;
        }

        public void OnSceneGUI(SceneView sv) { }
    }
}
