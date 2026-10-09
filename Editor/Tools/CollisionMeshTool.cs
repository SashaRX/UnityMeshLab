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
    [MeshLabTool("collision_mesh", MeshLabLibraries.Collision, MeshLabLibraries.Assets)]
    public class CollisionMeshTool : IUvTool, IUvTool3D
    {
        UvToolContext ctx;
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
        readonly List<Mesh> previewMeshes = new List<Mesh>(); // normals for surface preview only
        readonly List<Vector3[]> previewVertices = new List<Vector3[]>();
        readonly List<int[]> previewEdges = new List<int[]>();
        bool previewSource;

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
        }

        public void OnDeactivate()
        {
            DestroyGeneratedMeshes();
        }

        public void OnRefresh()
        {
            DestroyGeneratedMeshes();
        }

        void DestroyGeneratedMeshes()
        {
            foreach (var m in previewMeshes)
                if (m != null) UnityEngine.Object.DestroyImmediate(m);
            previewMeshes.Clear();
            previewVertices.Clear();
            previewEdges.Clear();
            foreach (var m in generatedMeshes)
                if (m != null) UnityEngine.Object.DestroyImmediate(m);
            generatedMeshes.Clear();
            lastResults.Clear();
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
                    if (generatedMode == CollisionMode.Simplified)
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
                using (new EditorGUI.DisabledScope(FindExistingCollisionObjects(ctx.LodGroup.transform).Count > 0))
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
                    EditorGUILayout.HelpBox("Remove existing collision objects before applying a new result.", MessageType.Info);
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
                new GUIContent("Max Verts/Hull", "Unity limits convex colliders to 255 triangles. At most 128 vertices keeps a closed triangulated hull within that budget (2V - 4)."),
                convexMaxVertsPerHull, 8, CollisionMeshBuilder.MaxConvexVertices);

            EditorGUILayout.Space(4);
            convexFillMode = EditorGUILayout.Popup(
                new GUIContent("Fill Mode", "How to determine inside vs outside.\n\nFlood Fill: default, works for closed meshes.\nSurface Only: hollow result, for thin shells.\nRaycast Fill: better for meshes with holes."),
                convexFillMode, fillModeNames);
            convexMaxRecursionDepth = EditorGUILayout.IntSlider(
                new GUIContent("Max Recursion", "Maximum depth of recursive splitting. Higher = finer decomposition, slower. Capped at 10 to bound memory and CPU use."),
                convexMaxRecursionDepth, 1, CollisionMeshBuilder.MaxConvexRecursionDepth);
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
                // Working geometry may carry _wc; persistence must retain the
                // imported source identity, including the selected LOD suffix.
                meshName        = entry.fbxMesh != null ? entry.fbxMesh.name : sourceMesh.name,
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
                meshName        = entry.fbxMesh != null ? entry.fbxMesh.name : sourceMesh.name,
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
            if (EditorUtility.IsPersistent(root.gameObject))
            {
                UvtLog.Warn("Open the prefab in Prefab Mode before applying collision meshes.");
                return;
            }
            if (FindExistingCollisionObjects(root).Count > 0)
            {
                UvtLog.Warn("Remove existing collision objects before applying a new result.");
                return;
            }
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply Collision Meshes");
            EnsureAppliedMeshFolderExists();
            try
            {
                int meshIdx = 0;
                using (new AssetDatabase.AssetEditingScope())
                {
                    foreach (var r in lastResults)
                    {
                        string baseName = UvToolContext.ExtractGroupKey(r.meshName);
                        var container = CreateCollisionObject(baseName + "_COL", root);
                        // A single TRS cannot represent nested non-uniform scale + rotation.
                        // Bake the full relative matrix into the applied copy; sidecar and
                        // generated meshes stay in source-local space.
                        var toRoot = root.worldToLocalMatrix * SourceMatrix(r);
                        for (int h = 0; h < r.hullCount && meshIdx < generatedMeshes.Count; h++, meshIdx++)
                        {
                            var go = generatedMode == CollisionMode.Simplified ? container :
                                CreateCollisionObject($"{baseName}_COL_Hull{h}", container.transform);
                            var mc = Undo.AddComponent<MeshCollider>(go);
                            Undo.RecordObject(mc, "Configure Collision Mesh");
                            mc.convex = generatedMode == CollisionMode.ConvexDecomposition;
                            mc.sharedMesh = CreateAppliedMeshCopy(generatedMeshes[meshIdx], go.name, toRoot);
                            PrefabUtility.RecordPrefabInstancePropertyModifications(mc);
                        }
                    }
                }
                AssetDatabase.SaveAssets();
            }
            finally { Undo.CollapseUndoOperations(undoGroup); }
            UvtLog.Info("Collision meshes applied to scene.");
        }

        static GameObject CreateCollisionObject(string name, Transform parent)
        {
            var go = new GameObject(name) { layer = parent.gameObject.layer };
            Undo.RegisterCreatedObjectUndo(go, "Create Collision Object");
            Undo.SetTransformParent(go.transform, parent, "Parent Collision Object");
            Undo.RecordObject(go.transform, "Reset Collision Transform");
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            PrefabUtility.RecordPrefabInstancePropertyModifications(go.transform);
            return go;
        }

        const string AppliedMeshAssetFolder = "Assets/UnityMeshLab/GeneratedCollisionMeshes";

        static Mesh CreateAppliedMeshCopy(Mesh source, string meshName, Matrix4x4 toRoot)
        {
            if (source == null) return null;

            var instance = UnityEngine.Object.Instantiate(source);
            instance.name = meshName;
            MeshGeometry.TransformCollisionMesh(instance, toRoot);

            string assetPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(AppliedMeshAssetFolder, meshName + ".asset"));
            AssetDatabase.CreateAsset(instance, assetPath.Replace('\\', '/'));
            return instance;
        }

        Matrix4x4 SourceMatrix(GeneratedCollisionInfo result) => result.sourceTransform != null
            ? result.sourceTransform.localToWorldMatrix : ctx.LodGroup.transform.localToWorldMatrix;

        static void EnsureAppliedMeshFolderExists()
        {
            if (AssetDatabase.IsValidFolder("Assets/UnityMeshLab"))
            {
                if (!AssetDatabase.IsValidFolder(AppliedMeshAssetFolder))
                    AssetDatabase.CreateFolder("Assets/UnityMeshLab", "GeneratedCollisionMeshes");
                return;
            }

            AssetDatabase.CreateFolder("Assets", "UnityMeshLab");
            AssetDatabase.CreateFolder("Assets/UnityMeshLab", "GeneratedCollisionMeshes");
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

            // Build CollisionMeshEntry and save to sidecar
            string sidecarPath = SidecarStore.PathFor(fbxPath);
            var data = SidecarStore.LoadOrCreate(fbxPath);
            Undo.RecordObject(data, "Save Collision Sidecar");

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
                // Keep the sidecar in source-local space: the document save places
                // collision next to that source. The rebuild converts to root space
                // using source frames captured before hierarchy normalization.

                for (int h = 0; h < r.hullCount && meshIdx < generatedMeshes.Count; h++, meshIdx++)
                {
                    var m = generatedMeshes[meshIdx];
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

            SidecarStore.Save(data);
            UvtLog.Info($"[Collision] Saved to sidecar: {sidecarPath} ({data.collisionEntries.Count} collision entries)");
        }

        /// <summary>
        /// The saved collision meshes for FBX export, one list per entry (meshName,
        /// meshes, isConvex). Forwards to <see cref="SidecarStore.CollisionMeshes"/>; the
        /// caller owns and destroys the meshes.
        /// </summary>
        public static List<(string meshName, List<Mesh> meshes, bool isConvex)> GetCollisionMeshesFromSidecar(string fbxPath)
            => SidecarStore.CollisionMeshes(fbxPath);

        void RemoveFromScene()
        {
            if (ctx.LodGroup == null) return;
            if (EditorUtility.IsPersistent(ctx.LodGroup.gameObject)) return;

            var existing = FindExistingCollisionObjects(ctx.LodGroup.transform);
            if (existing.Count == 0)
            {
                UvtLog.Info("No collision objects found to remove.");
                return;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Remove Collision Meshes");
            try
            {
                foreach (var go in existing)
                    Undo.DestroyObjectImmediate(go);
            }
            finally { Undo.CollapseUndoOperations(undoGroup); }

            UvtLog.Info($"Removed {existing.Count} collision object(s).");
        }

        static List<GameObject> FindExistingCollisionObjects(Transform root)
        {
            var result = new List<GameObject>();
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (MeshNaming.IsCollision(child.name))
                    result.Add(child.gameObject);
            }
            return result;
        }

        // ── Scene view: wireframe preview ──

        void EnsurePreviewMeshes()
        {
            if (previewMeshes.Count == generatedMeshes.Count) return;
            foreach (var mesh in generatedMeshes)
            {
                var copy = UnityEngine.Object.Instantiate(mesh);
                copy.hideFlags = HideFlags.HideAndDontSave;
                copy.RecalculateNormals();
                previewMeshes.Add(copy);
                previewVertices.Add(mesh.vertices);
                previewEdges.Add(MeshViewport3D.EdgeIndices(mesh).ToArray());
            }
        }

        public bool Get3DContent(List<MeshViewport3D.Item> items)
        {
            if (ctx?.LodGroup == null || generatedMeshes.Count == 0) return false;
            EnsurePreviewMeshes();
            if (previewSource)
            {
                foreach (var entry in ctx.MeshEntries)
                    if (entry.include && entry.lodIndex == ctx.SourceLodIndex)
                        items.Add(new MeshViewport3D.Item(entry.originalMesh ?? entry.fbxMesh,
                            entry.renderer != null ? entry.renderer.localToWorldMatrix : ctx.LodGroup.transform.localToWorldMatrix,
                            entry.renderer != null ? entry.renderer.sharedMaterials : null));
            }
            else
            {
                int i = 0;
                foreach (var result in lastResults)
                    for (int h = 0; h < result.hullCount; ++h, ++i)
                        items.Add(new MeshViewport3D.Item(previewMeshes[i], SourceMatrix(result)));
            }
            return true;
        }

        public void OnDraw3D(MeshViewport3D view)
        {
            if (ctx?.LodGroup == null) return;
            int i = 0;
            foreach (var result in lastResults)
                for (int h = 0; h < result.hullCount; ++h, ++i)
                    view.DrawWire(generatedMeshes[i], SourceMatrix(result), UvCanvasView.pal[i % UvCanvasView.pal.Length]);
        }

        public void OnSceneGUI(SceneView sv)
        {
            if (ctx?.LodGroup == null || generatedMeshes.Count == 0 || Event.current.type != EventType.Repaint) return;
            EnsurePreviewMeshes();
            int i = 0;
            foreach (var result in lastResults)
                for (int h = 0; h < result.hullCount; ++h, ++i)
                    using (new Handles.DrawingScope(UvCanvasView.pal[i % UvCanvasView.pal.Length], SourceMatrix(result)))
                        Handles.DrawLines(previewVertices[i], previewEdges[i]);
        }

        // ── Unused interface methods ──

        public void OnDrawToolbarExtra()
        {
            previewSource = GUILayout.Toggle(previewSource, new GUIContent("Source + collision wire",
                "Show the source surface with collision edges, or the generated collision surface and edges."), EditorStyles.toolbarButton);
        }
        public void OnDrawStatusBar() { }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { }

        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes()
        {
            yield break;
        }

    }
}
