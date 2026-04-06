// CollisionMeshTool.cs — Collision mesh generation tool (IUvTool tab).
// Two modes: Simplified (non-convex MeshCollider) and Convex Decomposition (compound convex MeshColliders).

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
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

        // ── Results ──
        List<GeneratedCollisionInfo> lastResults = new List<GeneratedCollisionInfo>();
        List<Mesh> generatedMeshes = new List<Mesh>(); // kept alive for Apply and preview

        struct GeneratedCollisionInfo
        {
            public string meshName;
            public int sourceTriCount;
            public int resultTriCount;
            public int hullCount;
            public float resultError;
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
        }

        public void OnRefresh()
        {
            lastResults.Clear();
            generatedMeshes.Clear();
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

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Scene", EditorStyles.boldLabel);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Apply to Scene", GUILayout.Height(24)))
                    ApplyToScene();
                if (GUILayout.Button("Remove from Scene", GUILayout.Height(24)))
                    RemoveFromScene();
                EditorGUILayout.EndHorizontal();
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

        void DrawSimplifiedSettings()
        {
            EditorGUILayout.HelpBox("For static objects, terrain, walls. Creates a single non-convex MeshCollider.", MessageType.None);
            simplifyTargetRatio = EditorGUILayout.Slider("Target Ratio", simplifyTargetRatio, 0.01f, 0.5f);
            simplifyTargetError = EditorGUILayout.Slider("Target Error", simplifyTargetError, 0.01f, 1.0f);
        }

        void DrawConvexSettings()
        {
            EditorGUILayout.HelpBox("For dynamic/kinematic objects. Creates compound convex MeshColliders.", MessageType.None);
            convexMaxHulls        = EditorGUILayout.IntSlider("Max Hulls", convexMaxHulls, 1, 64);
            convexResolution      = EditorGUILayout.IntField("Resolution", convexResolution);
            convexResolution      = Mathf.Clamp(convexResolution, 10000, 1000000);
            convexMaxVertsPerHull = EditorGUILayout.IntSlider("Max Verts/Hull", convexMaxVertsPerHull, 8, 255);

            if (convexMaxVertsPerHull > 128)
                EditorGUILayout.HelpBox("Values above 128 may cause issues with some physics engines.", MessageType.Warning);
        }

        // ── Generate ──

        void ExecuteGenerate()
        {
            lastResults.Clear();
            generatedMeshes.Clear();

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
                meshName       = sourceMesh.name,
                sourceTriCount = result.sourceTriCount,
                resultTriCount = result.resultTriCount,
                hullCount      = 1,
                resultError    = result.resultError
            });
        }

        void GenerateConvex(MeshEntry entry, Mesh sourceMesh)
        {
            var settings = new CollisionMeshBuilder.ConvexDecompSettings
            {
                maxHulls         = convexMaxHulls,
                resolution       = convexResolution,
                maxVertsPerHull  = convexMaxVertsPerHull,
                minVolumePerHull = 1f
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
                meshName       = sourceMesh.name,
                sourceTriCount = result.sourceTriCount,
                resultTriCount = totalTris,
                hullCount      = result.hulls.Count
            });
        }

        // ── Apply / Remove ──

        void ApplyToScene()
        {
            if (ctx.LodGroup == null || generatedMeshes.Count == 0) return;

            Transform root = ctx.LodGroup.transform;
            int undoGroup = Undo.GetCurrentGroup();

            if (mode == CollisionMode.Simplified)
            {
                // One _COL child per generated mesh
                for (int i = 0; i < generatedMeshes.Count; i++)
                {
                    string name = generatedMeshes[i].name.Replace("_collision", "_COL");
                    var go = new GameObject(name);
                    Undo.RegisterCreatedObjectUndo(go, "Create Collision Mesh");
                    go.transform.SetParent(root, false);

                    var mc = Undo.AddComponent<MeshCollider>(go);
                    mc.sharedMesh = generatedMeshes[i];
                    mc.convex = false;
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

                    for (int h = 0; h < r.hullCount && meshIdx < generatedMeshes.Count; h++, meshIdx++)
                    {
                        var hullGo = new GameObject($"{r.meshName}_COL_Hull{h}");
                        Undo.RegisterCreatedObjectUndo(hullGo, "Create Convex Hull");
                        hullGo.transform.SetParent(container.transform, false);

                        var mc = Undo.AddComponent<MeshCollider>(hullGo);
                        mc.sharedMesh = generatedMeshes[meshIdx];
                        mc.convex = true;
                    }
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            UvtLog.Info("Collision meshes applied to scene.");
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
