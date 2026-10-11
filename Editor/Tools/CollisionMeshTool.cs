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
        int   convexMaxHulls        = 8;
        int   convexResolution      = 100000;
        int   convexMaxVertsPerHull = 16;
        int   convexMaxRecursionDepth = 10;
        bool  convexShrinkWrap      = true;
        int   convexFillMode        = 0; // 0=FloodFill, 1=SurfaceOnly, 2=RaycastFill
        int   convexMinEdgeLength   = 2;
        bool  convexFindBestPlane   = false;
        float convexVolumeError     = 85f;
        bool  convexGroupParts = true;
        float convexPartGap         = .02f;
        float convexPartAngle       = 50f;
        bool convexPrepareElements = true;
        float convexBoxFill = .9f;
        float convexElementFit = .02f;
        int convexElementResolution = 32;
        int convexElementTriangles = 64;
        bool convexSimpleShapes = true;
        float convexShapeFit = .075f, convexProjectionThickness = .25f;
        bool showElementSettings, showElementDetails;

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
            public int partGroupCount;
            public List<CollisionMeshBuilder.ElementReport> elementReports;
            public List<CollisionMeshBuilder.ShapeReport> shapeReports;
            public string hierarchyPath;
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
                DestroyCachedMesh(m);
            previewMeshes.Clear();
            previewVertices.Clear();
            previewEdges.Clear();
            foreach (var m in generatedMeshes)
                DestroyCachedMesh(m);
            generatedMeshes.Clear();
            lastResults.Clear();
        }

        static void DestroyCachedMesh(Mesh mesh)
        {
            if (!mesh) return;
            // Notify every live view while the instance ID is still accessible,
            // releasing encoded copies, wires, ribbons and pending uploads.
            VertexChannels.RaiseChanged(mesh);
            UnityEngine.Object.DestroyImmediate(mesh);
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
                            $"  {r.meshName}: {r.partGroupCount} groups, {r.hullCount} hulls, {r.resultTriCount:N0} tris",
                            EditorStyles.miniLabel);
                        if (r.elementReports != null && r.elementReports.Count > 0)
                            EditorGUILayout.LabelField("  " + ElementSummary(r.elementReports), EditorStyles.miniLabel);
                        if (r.shapeReports != null)
                        {
                            EditorGUILayout.LabelField(new GUIContent("  " + ShapeSummary(r.shapeReports), r.hierarchyPath), EditorStyles.miniLabel);
                        }
                    }
                }
                if (lastResults.Any(r => r.elementReports != null && r.elementReports.Count > 0 || r.shapeReports != null))
                {
                    showElementDetails = EditorGUILayout.Foldout(showElementDetails, "Hierarchy / Shape Analysis", true);
                    if (showElementDetails)
                        foreach (var r in lastResults)
                        {
                            EditorGUILayout.LabelField(r.hierarchyPath ?? r.meshName, EditorStyles.miniLabel);
                            if (r.shapeReports != null) foreach (var shape in r.shapeReports) {
                                string fit = shape.sampledError >= 0 ? $"{shape.sampledError:P1}" : "n/a";
                                EditorGUILayout.LabelField(new GUIContent($"  G{shape.groupIndex}: {shape.kind}, fit {fit}", shape.detail), EditorStyles.miniLabel);
                            }
                            if (r.elementReports != null)
                                foreach (var element in r.elementReports)
                                {
                                    string fill = element.boxFill >= 0 ? $"{element.boxFill:P0}" : "open/invalid";
                                    EditorGUILayout.LabelField(new GUIContent(
                                        $"  G{element.groupIndex} E{element.elementIndex}: fill {fill}, {element.preparation}, {element.sourceTriangles} -> {element.preparedTriangles} tris",
                                        $"Principal size: {element.principalSize}. Sampled fit error: {element.sampledError:P2}. {element.detail}"), EditorStyles.miniLabel);
                                }
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
                            : go.GetComponent<BoxCollider>() != null ? "BoxCollider" : "container";
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
            EditorGUILayout.HelpBox("For dynamic/kinematic objects. Preserves source hierarchy boundaries; fits boxes, projected profiles or 3D convex meshes.", MessageType.None);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Detailed", "Restore the original whole-mesh decomposition settings.")))
                ApplyConvexPreset(CollisionMeshBuilder.ConvexDecompSettings.Default);
            if (GUILayout.Button(new GUIContent("Coarse Parts", "Group nearby similarly oriented pieces, close small gaps, and preserve independent structures. Start with 8 hulls and 16 vertices per hull.")))
                ApplyConvexPreset(CollisionMeshBuilder.ConvexDecompSettings.CoarseParts);
            EditorGUILayout.EndHorizontal();
            if (GUILayout.Button(new GUIContent("Structural: Boxes + Profiles", "Analyze hierarchy nodes and geometry groups. Keep fitted boxes as final colliders; extrude thin convex profiles and split only where projected gaps are too large. Use 3D decomposition for other shapes.")))
                ApplyConvexPreset(CollisionMeshBuilder.ConvexDecompSettings.Structural);
            convexSimpleShapes = EditorGUILayout.Toggle(new GUIContent("Prefer Simple Shapes", "Fit final shapes before remeshing. Boxes become BoxColliders when the transform supports them; the preview and FBX use matching mesh proxies."), convexSimpleShapes);
            if (convexSimpleShapes)
            {
                convexShapeFit = EditorGUILayout.Slider(new GUIContent("Shape Fit (%)", "Sampled surface and projected-gap tolerance, relative to each group's oriented bounds diagonal."), convexShapeFit * 100, .01f, 10) / 100;
                convexProjectionThickness = EditorGUILayout.Slider(new GUIContent("Projection Thickness (%)", "Maximum thickness divided by the smaller in-plane extent for profile extrusion."), convexProjectionThickness * 100, 1, 100) / 100;
            }
            convexGroupParts = EditorGUILayout.Toggle(
                new GUIContent("Group Nearby Parts", "Decompose groups independently. Principal planes keep differently oriented structures separate; the hull budget is shared between all groups."), convexGroupParts);
            if (convexGroupParts)
            {
                convexPartGap = EditorGUILayout.Slider(new GUIContent("Part Gap (%)", "Join nearby disconnected pieces within this percentage of the source bounds diagonal. 0 keeps each disconnected piece separate."), convexPartGap * 100f, 0f, 10f) / 100f;
                convexPartAngle = EditorGUILayout.Slider(new GUIContent("Part Angle", "Maximum angle between the principal planes of neighboring pieces. Higher values can join structures across corners."), convexPartAngle, 0f, 90f);
            }
            bool preparationAvailable = ElementPreparationAvailable();
            if (!preparationAvailable) convexPrepareElements = false;
            using (new EditorGUI.DisabledScope(!preparationAvailable))
                convexPrepareElements = EditorGUILayout.Toggle(new GUIContent("Prepare Elements", "Analyze oriented bounds and closed-volume fill. Fit boxes to dense parts; remesh and decimate other elements. Open surfaces may be closed; reject invalid topology or excessive sampled surface distance."), convexPrepareElements);
            if (!preparationAvailable)
                EditorGUILayout.HelpBox("Enable Remesh and Simplification modules in Project Settings to prepare elements.", MessageType.Info);
            if (convexPrepareElements)
            {
                showElementSettings = EditorGUILayout.Foldout(showElementSettings, "Preparation Settings", true);
                if (showElementSettings)
                {
                    convexBoxFill = EditorGUILayout.Slider(new GUIContent("Box Fill (%)", "Minimum occupied volume / oriented bbox volume for a box candidate. Open or invalid source volumes cannot use a box."), convexBoxFill * 100, 50, 100) / 100;
                    convexElementFit = EditorGUILayout.Slider(new GUIContent("Element Fit (%)", "Maximum sampled distance in both directions, relative to each element's bounds diagonal. Independent of the final V-HACD volume error."), convexElementFit * 100, .01f, 10) / 100;
                    convexElementResolution = EditorGUILayout.IntSlider("Element Voxels", convexElementResolution, 16, 256);
                    convexElementTriangles = EditorGUILayout.IntSlider(new GUIContent("Element Tris", "Triangle target before convex decomposition, per element. Geometry-only decimation preserves topology; this is not the final convex budget."), convexElementTriangles, 12, 4096);
                }
            }
            convexVolumeError = EditorGUILayout.Slider(
                new GUIContent("Volume Error (%)", "Allowed difference between hull and occupied voxel volume, relative to occupied volume. Higher values stop splitting earlier and close more detail; this is not a minimum hull size."), convexVolumeError, .1f, 100f);
            convexMaxHulls = EditorGUILayout.IntSlider(
                new GUIContent("Max Hulls", "Maximum shapes per source mesh, shared between its geometry groups. More shapes can retain more detail at higher cost."),
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

        void ApplyConvexPreset(CollisionMeshBuilder.ConvexDecompSettings settings)
        {
            convexMaxHulls = settings.maxHulls;
            convexResolution = settings.resolution;
            convexMaxVertsPerHull = settings.maxVertsPerHull;
            convexVolumeError = settings.minVolumePerHull;
            convexMaxRecursionDepth = settings.maxRecursionDepth;
            convexShrinkWrap = settings.shrinkWrap;
            convexFillMode = settings.fillMode;
            convexMinEdgeLength = settings.minEdgeLength;
            convexFindBestPlane = settings.findBestPlane;
            convexGroupParts = settings.groupNearbyParts;
            convexPartGap = settings.partMergeDistance;
            convexPartAngle = settings.partMergeAngle;
            convexPrepareElements = settings.prepareElements && ElementPreparationAvailable();
            convexBoxFill = settings.boxFillThreshold;
            convexElementFit = settings.elementFitError;
            convexElementResolution = settings.elementRemeshResolution;
            convexElementTriangles = settings.elementTargetTriangles;
            convexSimpleShapes = settings.preferSimpleShapes;
            convexShapeFit = settings.shapeFitError;
            convexProjectionThickness = settings.projectionThicknessRatio;
        }

        void ExecuteGenerate()
        {
            DestroyGeneratedMeshes();
            generatedMode = mode;

            var sources = CollisionHierarchyAnalyzer.Analyze(ctx.LodGroup != null ? ctx.LodGroup.transform : null,
                ctx.MeshEntries, ctx.SourceLodIndex);

            if (sources.Count == 0)
            {
                UvtLog.Warn("No source meshes found for collision generation.");
                return;
            }

            foreach (var source in sources)
            {
                var entry = source.entry;
                Mesh sourceMesh = entry.originalMesh ?? entry.fbxMesh;
                if (sourceMesh == null) continue;

                if (mode == CollisionMode.Simplified)
                    GenerateSimplified(entry, sourceMesh);
                else
                    GenerateConvex(entry, sourceMesh, source.path);
            }

            if (lastResults.Count > 0)
                UvtLog.Info($"Collision generation complete: {lastResults.Count} source mesh(es), {generatedMeshes.Count} collider mesh(es), {lastResults.Sum(r => r.resultTriCount):N0} triangles.");

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

        void GenerateConvex(MeshEntry entry, Mesh sourceMesh, string hierarchyPath)
        {
            var settings = new CollisionMeshBuilder.ConvexDecompSettings
            {
                maxHulls          = convexMaxHulls,
                resolution        = convexResolution,
                maxVertsPerHull   = convexMaxVertsPerHull,
                minVolumePerHull  = convexVolumeError,
                maxRecursionDepth = convexMaxRecursionDepth,
                shrinkWrap        = convexShrinkWrap,
                fillMode          = convexFillMode,
                minEdgeLength     = convexMinEdgeLength,
                findBestPlane     = convexFindBestPlane,
                groupNearbyParts  = convexGroupParts,
                partMergeDistance = convexPartGap,
                partMergeAngle    = convexPartAngle,
                prepareElements   = convexPrepareElements && ElementPreparationAvailable(),
                boxFillThreshold  = convexBoxFill,
                elementFitError   = convexElementFit,
                elementRemeshResolution = convexElementResolution,
                elementTargetTriangles = convexElementTriangles,
                preferSimpleShapes = convexSimpleShapes,
                shapeFitError = convexShapeFit,
                projectionThicknessRatio = convexProjectionThickness
            };

            var result = CollisionMeshBuilder.BuildConvexDecomposition(sourceMesh, settings);

            if (!result.ok)
            {
                UvtLog.Warn($"Convex decomposition failed for {sourceMesh.name}: {result.error}");
                return;
            }

            if (result.elementReports != null && result.elementReports.Count > 0)
                UvtLog.Info($"Collision elements for {sourceMesh.name}: {ElementSummary(result.elementReports)}");
            if (result.shapeReports != null) UvtLog.Info($"Collision shapes for {hierarchyPath ?? sourceMesh.name}: {ShapeSummary(result.shapeReports)}");
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
                partGroupCount  = result.partGroupCount,
                elementReports  = result.elementReports,
                shapeReports    = result.shapeReports,
                hierarchyPath   = hierarchyPath,
                sourceTransform = entry.renderer != null ? entry.renderer.transform : null
            });
        }

        static bool ElementPreparationAvailable()
        {
            // Preparation is an optional remesh capability; disabling it must not
            // remove the basic Collision tab or bypass the project's module choices.
            var disabled = MeshLabProjectSettings.Instance.disabledLibraryIds;
            return !disabled.Contains(MeshLabLibraries.Remesh) && !disabled.Contains(MeshLabLibraries.Simplification);
        }

        static string ElementSummary(List<CollisionMeshBuilder.ElementReport> reports) =>
            $"{reports.Count} elements: {reports.Count(e => e.preparation == CollisionMeshBuilder.ElementPreparation.Box)} box, " +
            $"{reports.Count(e => e.preparation == CollisionMeshBuilder.ElementPreparation.RemeshDecimate)} remesh, " +
            $"{reports.Count(e => e.preparation == CollisionMeshBuilder.ElementPreparation.Decimate)} decimate, " +
            $"{reports.Count(e => e.preparation == CollisionMeshBuilder.ElementPreparation.Source)} original; " +
            $"{reports.Sum(e => e.sourceTriangles):N0} -> {reports.Sum(e => e.preparedTriangles):N0} input tris";

        static string ShapeSummary(List<CollisionMeshBuilder.ShapeReport> reports) =>
            $"{reports.Count(s => s.kind == CollisionMeshBuilder.ShapeKind.Box)} boxes, " +
            $"{reports.Count(s => s.kind == CollisionMeshBuilder.ShapeKind.Projected)} projected profiles, " +
            $"{reports.Count(s => s.kind == CollisionMeshBuilder.ShapeKind.ConvexMesh)} 3D hulls";

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
                            if (r.shapeReports != null && h < r.shapeReports.Count &&
                                r.shapeReports[h].kind == CollisionMeshBuilder.ShapeKind.Box &&
                                TryApplyBox(go, r.shapeReports[h], toRoot, root.localToWorldMatrix)) continue;
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

        internal static bool TryApplyBox(GameObject go, CollisionMeshBuilder.ShapeReport shape, Matrix4x4 toRoot, Matrix4x4 rootToWorld)
        {
            var local = toRoot * Matrix4x4.TRS(shape.center, shape.rotation, shape.size);
            // A BoxCollider cannot represent shear, including shear introduced by
            // its scaled parent. Keep the exact transformed mesh proxy in that case.
            if (!BoxFrame(local, out var rotation, out var size) || !BoxFrame(rootToWorld * local, out _, out _)) return false;
            Undo.RecordObject(go.transform, "Place Box Collider");
            go.transform.localPosition = local.GetColumn(3);
            go.transform.localRotation = rotation;
            go.transform.localScale = Vector3.one;
            PrefabUtility.RecordPrefabInstancePropertyModifications(go.transform);
            var box = Undo.AddComponent<BoxCollider>(go);
            Undo.RecordObject(box, "Configure Box Collider");
            box.center = Vector3.zero;
            box.size = size;
            PrefabUtility.RecordPrefabInstancePropertyModifications(box);
            return true;
        }

        static bool BoxFrame(Matrix4x4 matrix, out Quaternion rotation, out Vector3 size)
        {
            rotation = Quaternion.identity;
            Vector3 x = matrix.GetColumn(0), y = matrix.GetColumn(1), z = matrix.GetColumn(2);
            size = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (size.x <= 0 || size.y <= 0 || size.z <= 0 || !float.IsFinite(size.sqrMagnitude)) return false;
            x /= size.x; y /= size.y; z /= size.z;
            if (Mathf.Abs(Vector3.Dot(x,y)) > .0001f || Mathf.Abs(Vector3.Dot(x,z)) > .0001f || Mathf.Abs(Vector3.Dot(y,z)) > .0001f) return false;
            rotation = Quaternion.LookRotation(z, y);
            return true;
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
            // CreateAsset adopts the unique filename; keep the mesh's canonical
            // collision identity even when an earlier applied asset still exists.
            instance.name = meshName;
            EditorUtility.SetDirty(instance);
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
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var paths = new Dictionary<Transform, string>();
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            using var document = model != null ? FbxSourceDocument.Load(fbxPath) : null;
            var documentPaths = new Dictionary<Transform, string>();
#endif
            foreach (var result in lastResults)
            {
                if (!TryResolveSidecarPath(model, fbxPath, result.sourceTransform, result.meshName, out var path))
                {
                    UvtLog.Warn($"[Collision] Cannot identify source instance '{result.meshName}' in the imported FBX; sidecar was not changed.");
                    return;
                }
                if (result.sourceTransform != null)
                {
                    paths[result.sourceTransform] = path;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                    try
                    {
                        documentPaths[result.sourceTransform] = FbxStructureWrite.ImportedCollisionDocumentPath(document, path);
                    }
                    catch (FbxStructureRefusalException e)
                    {
                        UvtLog.Warn($"[Collision] {e.Message}; sidecar was not changed.");
                        return;
                    }
#endif
                }
            }
            foreach (var result in lastResults)
                if (result.sourceTransform != null && string.IsNullOrEmpty(paths[result.sourceTransform]) &&
                    lastResults.Count(r => r.meshName == result.meshName) > 1)
                {
                    UvtLog.Warn($"[Collision] Multiple scene instances of '{result.meshName}' have no unique FBX hierarchy identity; sidecar was not changed.");
                    return;
                }
            var data = SidecarStore.LoadOrCreate(fbxPath);
            Undo.RecordObject(data, "Save Collision Sidecar");
            var obsoleteKeys = PruneMissingCollisionSources(data, model, fbxPath);

            // Build flattened collision entry per source mesh
            int meshIdx = 0;
            foreach (var r in lastResults)
            {
                // Compute fingerprint from source FBX mesh
                MeshFingerprint fp = null;
                var srcEntry = ctx.MeshEntries.FirstOrDefault(
                    e => e.lodIndex == ctx.SourceLodIndex && e.include &&
                    (r.sourceTransform == null || e.renderer?.transform == r.sourceTransform) &&
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

                string sourcePath = r.sourceTransform != null ? paths[r.sourceTransform] : null;
                var entry = new CollisionMeshEntry
                {
                    meshGroupKey      = CollisionHierarchyAnalyzer.InstanceKey(r.meshName, sourcePath),
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
                    shapes            = r.shapeReports?.ToArray(),
                    sourceHierarchyPath = sourcePath,
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                    sourceDocumentHierarchyPath = r.sourceTransform != null ? documentPaths[r.sourceTransform] : null,
#endif
                    sourceMeshName    = r.meshName,
                    preferSimpleShapes = convexSimpleShapes,
                    shapeFitError     = convexShapeFit,
                    projectionThicknessRatio = convexProjectionThickness,
                };

                // Replace this instance and the old unscoped mesh entry, retaining others.
                var replaced = data.collisionEntries.Where(c => c != null && (c.meshGroupKey == entry.meshGroupKey ||
                    (!string.IsNullOrEmpty(sourcePath) && c.sourceHierarchyPath == sourcePath) ||
                    (string.IsNullOrEmpty(sourcePath) && c.sourceMeshName == r.meshName) ||
                    (string.IsNullOrEmpty(c.sourceHierarchyPath) && c.meshGroupKey == r.meshName))).ToList();
                foreach (var old in replaced)
                {
                    CollectReplacedCollisionKeys(old, obsoleteKeys);
                    data.collisionEntries.Remove(old);
                }
                obsoleteKeys.Remove(entry.meshGroupKey);
                entry.replacedMeshGroupKeys = obsoleteKeys.ToArray();
                obsoleteKeys.Clear();
                data.collisionEntries.Add(entry);
            }

            SidecarStore.Save(data);
            UvtLog.Info($"[Collision] Saved to sidecar: {sidecarPath} ({data.collisionEntries.Count} collision entries)");
        }

        bool TryResolveSidecarPath(GameObject model, string fbxPath, Transform source, string meshName, out string path)
        {
            path = null;
            if (model == null) return false;
            var original = source != null ? PrefabUtility.GetCorrespondingObjectFromOriginalSource(source) : null;
            if (original != null && AssetDatabase.GetAssetPath(original) == fbxPath)
                path = CollisionHierarchyAnalyzer.RelativePath(original.root, original);
            else if (source != null)
            {
                var candidate = CollisionHierarchyAnalyzer.RelativePath(ctx.LodGroup.transform, source);
                foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
                    if (SourceMeshName(renderer) == meshName && CollisionHierarchyAnalyzer.RelativePath(model.transform, renderer.transform) == candidate)
                    { path = candidate; break; }
            }
            if (!string.IsNullOrEmpty(path)) return true;
            // A manually assembled scene can use a unique mesh without sharing
            // the imported model's hierarchy. It retains legacy mesh-name lookup.
            int matches = 0;
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
                if (SourceMeshName(renderer) == meshName) matches++;
            return matches == 1;
        }

        string SourceMeshName(Renderer renderer)
        {
            var entry = ctx.MeshEntries.FirstOrDefault(e => e.renderer == renderer);
            if (entry?.fbxMesh != null) return entry.fbxMesh.name;
            var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            return mesh != null ? mesh.name : null;
        }

        bool MatchesWholeModel(GameObject model)
        {
            if (model == null) return false;
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
                if (!MeshNaming.IsCollision(renderer.name))
                    sources[CollisionHierarchyAnalyzer.RelativePath(model.transform, renderer.transform)] = SourceMeshName(renderer);
            int count = 0;
            foreach (var renderer in ctx.LodGroup.GetComponentsInChildren<Renderer>(true))
            {
                if (MeshNaming.IsCollision(renderer.name)) continue;
                string path = CollisionHierarchyAnalyzer.RelativePath(ctx.LodGroup.transform, renderer.transform);
                if (!sources.TryGetValue(path, out var name) || name != SourceMeshName(renderer)) return false;
                count++;
            }
            return count == sources.Count;
        }

        HashSet<string> PruneMissingCollisionSources(Uv2DataAsset data, GameObject model, string fbxPath)
        {
            var root = ctx.LodGroup.transform;
            var importedRoot = PrefabUtility.GetCorrespondingObjectFromOriginalSource(root);
            var obsolete = new HashSet<string>(StringComparer.Ordinal);
            bool knownRoot = importedRoot != null && AssetDatabase.GetAssetPath(importedRoot) == fbxPath;
            // A partial manual hierarchy cannot own all the asset's sidecar entries.
            if (!knownRoot && !MatchesWholeModel(model)) return obsolete;
            string scope = knownRoot ? CollisionHierarchyAnalyzer.RelativePath(importedRoot.root, importedRoot) : ".";
            var livePaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (MeshNaming.IsCollision(renderer.name)) continue;
                var node = renderer.transform;
                var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(node);
                string path = original != null ? CollisionHierarchyAnalyzer.RelativePath(original.root, original)
                    : CollisionHierarchyAnalyzer.RelativePath(root, node);
                if (!string.IsNullOrEmpty(path)) livePaths.Add(path);
            }
            for (int i = data.collisionEntries.Count - 1; i >= 0; i--)
            {
                var entry = data.collisionEntries[i];
                if (entry == null || string.IsNullOrEmpty(entry.sourceMeshName) || string.IsNullOrEmpty(entry.sourceHierarchyPath)) continue;
                bool inScope = scope == "." || entry.sourceHierarchyPath == scope || entry.sourceHierarchyPath.StartsWith(scope + "/", StringComparison.Ordinal);
                if (!inScope || livePaths.Contains(entry.sourceHierarchyPath)) continue;
                CollectReplacedCollisionKeys(entry, obsolete);
                data.collisionEntries.RemoveAt(i);
            }
            return obsolete;
        }

        static void CollectReplacedCollisionKeys(CollisionMeshEntry entry, HashSet<string> keys)
        {
            if (!string.IsNullOrEmpty(entry.meshGroupKey)) keys.Add(entry.meshGroupKey);
            if (entry.replacedMeshGroupKeys == null) return;
            foreach (string key in entry.replacedMeshGroupKeys)
                if (!string.IsNullOrEmpty(key)) keys.Add(key);
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
            var pending = new Stack<Transform>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var parent = pending.Pop();
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    if (MeshNaming.IsCollision(child.name))
                        result.Add(child.gameObject);
                    else pending.Push(child);
                }
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
