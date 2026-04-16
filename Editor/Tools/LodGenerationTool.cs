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
        float generateTargetError = 0.2f;
        float generateUv2Weight = 20f;
        float generateNormalWeight = 1f;
        bool generateLockBorder = false;

        // ── Results ──
        List<GeneratedLodInfo> lastResults = new List<GeneratedLodInfo>();
        List<GameObject> generatedObjects = new List<GameObject>();
        int cachedLodSelectionId = -1;
        int cachedRendererSelectionId = -1;
        List<(GameObject go, int lodIndex, int rendererCount, int triangleCount)> cachedDetectedLods =
            new List<(GameObject, int, int, int)>();
        bool cachedSelectionHasRenderers;

        // Static accessor for cross-tool cleanup (e.g., after FBX export)
        internal static LodGenerationTool ActiveInstance { get; private set; }

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
            ActiveInstance = this;
        }

        public void OnDeactivate() { }

        void ValidateGeneratedObjects()
        {
            for (int i = generatedObjects.Count - 1; i >= 0; i--)
                if (generatedObjects[i] == null)
                    generatedObjects.RemoveAt(i);
            if (generatedObjects.Count == 0)
                lastResults.Clear();
        }

        public void OnRefresh()
        {
            ValidateGeneratedObjects();
            lastResults.Clear();
            cachedLodSelectionId = -1;
            cachedRendererSelectionId = -1;
            cachedDetectedLods.Clear();
            cachedSelectionHasRenderers = false;
        }

        public void OnDrawSidebar()
        {
            ValidateGeneratedObjects();
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("LOD Generation", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (ctx.LodGroup == null)
            {
                // Try to detect LOD siblings from the current selection
                var selected = Selection.activeGameObject;
                var siblings = FindLodSiblings(selected);

                if (siblings != null && siblings.Count > 0)
                {
                    RefreshDetectedLodCache(selected, siblings);
                    EditorGUILayout.HelpBox("LOD objects detected — create a LODGroup to continue.", MessageType.Info);
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Detected LODs", EditorStyles.boldLabel);
                    foreach (var (go, lodIndex, rendererCount, triangleCount) in cachedDetectedLods)
                    {
                        EditorGUILayout.LabelField(
                            $"  LOD{lodIndex}: {go.name}  ({rendererCount} renderer{(rendererCount != 1 ? "s" : "")}, {triangleCount:N0} tris)",
                            EditorStyles.miniLabel);
                    }

                    EditorGUILayout.Space(6);
                    var bgc = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(.4f, .8f, .4f);
                    if (GUILayout.Button("Create LODGroup", GUILayout.Height(28)))
                        CreateLodGroup(siblings);
                    GUI.backgroundColor = bgc;
                }
                else if (selected != null && SelectionHasRenderers(selected))
                {
                    EditorGUILayout.HelpBox(
                        "No LOD naming detected, but child renderers found.\n" +
                        "Create a LODGroup with all renderers as LOD0.",
                        MessageType.Info);
                    EditorGUILayout.Space(6);
                    var bgc = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(.4f, .8f, .4f);
                    if (GUILayout.Button("Add LOD Group", GUILayout.Height(28)))
                    {
                        var lodGroup = CreateLodGroupFromRenderers(selected);
                        if (lodGroup != null)
                        {
                            ctx.Refresh(lodGroup);
                            requestRepaint?.Invoke();
                            UvtLog.Info($"[LOD Gen] Created LODGroup on '{selected.name}' with all renderers as LOD0.");
                        }
                    }
                    GUI.backgroundColor = bgc;
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Assign a LODGroup in the UV2 Transfer tab first.\n" +
                        "Or select a GameObject with a LOD suffix (e.g. MyObject_LOD0) to auto-detect LOD siblings.",
                        MessageType.Info);
                }
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
                    lodTris += GetTriangleCount(m);
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
                    if (m != null) lastLodTris += GetTriangleCount(m);
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
            if (lastResults.Count > 0 || generatedObjects.Count > 0)
            {
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
                }

                EditorGUILayout.Space(4);
                var bgClear = GUI.backgroundColor;
                GUI.backgroundColor = new Color(.9f, .3f, .3f);
                string clearLabel = lastResults.Count > 0 ? "Clear Results" : "Clear Generated LODs";
                if (GUILayout.Button(clearLabel, GUILayout.Height(20)))
                    ClearGeneratedLods();
                GUI.backgroundColor = bgClear;
            }
        }


        internal void ClearGeneratedLods()
        {
            if (generatedObjects.Count == 0 && lastResults.Count == 0) return;

            int undoGroup = Undo.GetCurrentGroup();

            // Remove LOD slots that reference generated objects
            if (ctx.LodGroup != null)
            {
                var lods = ctx.LodGroup.GetLODs();
                var generatedSet = new HashSet<GameObject>();
                foreach (var go in generatedObjects)
                    if (go != null) generatedSet.Add(go);

                var cleanedLods = new List<LOD>();
                foreach (var lod in lods)
                {
                    if (lod.renderers == null || lod.renderers.Length == 0) continue;
                    bool anyGenerated = false;
                    foreach (var r in lod.renderers)
                        if (r != null && generatedSet.Contains(r.gameObject))
                        { anyGenerated = true; break; }
                    if (!anyGenerated) cleanedLods.Add(lod);
                }

                if (cleanedLods.Count != lods.Length)
                {
                    Undo.RecordObject(ctx.LodGroup, "Clear Generated LODs");
                    ctx.LodGroup.SetLODs(cleanedLods.ToArray());
                }
            }

            // Destroy generated GameObjects (top-level ones destroy children too)
            foreach (var go in generatedObjects)
                if (go != null) Undo.DestroyObjectImmediate(go);

            Undo.CollapseUndoOperations(undoGroup);

            generatedObjects.Clear();
            lastResults.Clear();

            if (ctx.LodGroup != null)
                ctx.Refresh(ctx.LodGroup);
            requestRepaint?.Invoke();
        }

        void ExecGenerateLods(int startLod)
        {
            if (ctx.LodGroup == null) return;
            if (generatedObjects.Count > 0) ClearGeneratedLods();
            lastResults.Clear();

            var sourceMeshes = new List<(MeshEntry entry, Mesh mesh)>();
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.lodIndex != ctx.SourceLodIndex) continue;
                Mesh src = e.repackedMesh ?? e.originalMesh;
                if (src != null) sourceMeshes.Add((e, src));
            }
            if (sourceMeshes.Count == 0) { UvtLog.Error("[GenerateLOD] No source meshes found."); return; }

            // No .asset files — meshes live in memory, exported via FBX

            UvToolContext.CompactLodArray(ctx.LodGroup, removeEmptySlots: true);
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
                        uvChannel    = 1
                    };

                    float progress = (float)lodIdx / generateLodCount;
                    EditorUtility.DisplayProgressBar("Generate LODs",
                        $"LOD{startLod + lodIdx} (ratio {ratio:P0})", progress);

                    var lodRenderers = new List<Renderer>();
                    int lodLevel = startLod + lodIdx;

                    // Build source parent → LOD container mapping for hierarchy preservation
                    // If source renderers are nested (e.g. LOD0/Door_LOD0), create matching containers
                    var parentToContainer = new Dictionary<Transform, Transform>();

                    foreach (var (entry, srcMesh) in sourceMeshes)
                    {
                        var r = MeshSimplifier.Simplify(srcMesh, settings);
                        if (!r.ok) { UvtLog.Error($"[GenerateLOD] Failed on {srcMesh.name}: {r.error}"); continue; }

                        int sourceTriCount = GetTriangleCount(srcMesh);
                        float actualRatio = sourceTriCount > 0
                            ? (float)r.simplifiedTriCount / sourceTriCount : 1f;
                        bool hitLimit = actualRatio > ratio * 1.2f;
                        if (hitLimit)
                            UvtLog.Warn($"[GenerateLOD] LOD{lodLevel}: target {ratio:P0} but got {actualRatio:P0} — increase Target Error");

                        string baseName = entry.fbxMesh != null ? entry.fbxMesh.name : srcMesh.name;
                        baseName = Regex.Replace(baseName, @"(_wc|_repack|_uvTransfer|_optimized|_LOD\d+)+$", "");
                        string meshName = baseName + "_LOD" + lodLevel;
                        r.simplifiedMesh.name = meshName;

                        // Mesh stays in memory — exported to FBX via sidebar footer button
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

                        // Create scene GameObject preserving source hierarchy
                        if (entry.renderer != null)
                        {
                            var go = new GameObject(meshName);

                            // Determine correct parent: mirror source hierarchy
                            Transform srcParent = entry.renderer.transform.parent;
                            Transform lodGroupTransform = ctx.LodGroup.transform;

                            if (srcParent != lodGroupTransform && srcParent != null)
                            {
                                // Nested renderer — find or create matching container
                                if (parentToContainer.TryGetValue(srcParent, out var container))
                                    go.transform.SetParent(container, false);
                                else
                                    go.transform.SetParent(lodGroupTransform, false);
                            }
                            else
                            {
                                go.transform.SetParent(lodGroupTransform, false);
                            }

                            // Register this GO as container for its source transform
                            // so nested renderers can be parented under it
                            parentToContainer[entry.renderer.transform] = go.transform;
                            go.transform.localPosition = entry.renderer.transform.localPosition;
                            go.transform.localRotation = entry.renderer.transform.localRotation;
                            go.transform.localScale    = entry.renderer.transform.localScale;
                            var mf = go.AddComponent<MeshFilter>();
                            mf.sharedMesh = r.simplifiedMesh;
                            var mr = go.AddComponent<MeshRenderer>();
                            LightmapTransferTool.CopyRendererSettings(entry.renderer, mr);
                            GameObjectUtility.SetStaticEditorFlags(go,
                                GameObjectUtility.GetStaticEditorFlags(entry.renderer.gameObject));
                            Undo.RegisterCreatedObjectUndo(go, "Generate LOD");
                            generatedObjects.Add(go);
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

            // Rename source LOD0 renderers to add _LOD0 suffix for cross-LOD matching
            foreach (var (entry, srcMesh) in sourceMeshes)
            {
                if (entry.renderer == null) continue;
                bool hasLodSuffix = Regex.IsMatch(
                    entry.renderer.name, @"[_\-\s]+LOD\d+$",
                    RegexOptions.IgnoreCase);
                if (!hasLodSuffix)
                {
                    Undo.RecordObject(entry.renderer.gameObject, "Rename LOD0");
                    string newName = entry.renderer.gameObject.name + "_LOD0";
                    UvtLog.Info($"[GenerateLOD] Renamed source: {entry.renderer.gameObject.name} → {newName}");
                    entry.renderer.gameObject.name = newName;
                }
            }

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

        // ── Auto-detect LOD siblings ──

        /// <summary>
        /// Given a GameObject whose name ends with a LOD suffix (e.g. Gazebo_LOD0),
        /// find all sibling GameObjects under the same parent that share the same
        /// base name but with different LOD indices. Returns null if the name doesn't
        /// match the LOD pattern.
        /// </summary>
        internal static List<(GameObject go, int lodIndex)> FindLodSiblings(GameObject go)
        {
            if (go == null) return null;

            // Match trailing LOD suffix: _LOD0, -LOD1, LOD2, etc.
            var m = Regex.Match(go.name, @"^(.+?)([_\-\s]*)LOD(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                // Selected object has LOD suffix — search siblings
                string baseName = m.Groups[1].Value;
                var parent = go.transform.parent;
                if (parent == null) return null;

                var results = new List<(GameObject, int)>();
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i).gameObject;
                    var cm = Regex.Match(child.name, @"^(.+?)[_\-\s]*LOD(\d+)$", RegexOptions.IgnoreCase);
                    if (cm.Success && string.Equals(cm.Groups[1].Value, baseName, System.StringComparison.OrdinalIgnoreCase))
                        results.Add((child, int.Parse(cm.Groups[2].Value)));
                }

                results.Sort((a, b) => a.Item2.CompareTo(b.Item2));
                return results.Count > 0 ? results : null;
            }

            // Selected object does NOT have LOD suffix — search its children
            // Handles prefab pattern: Parent (Pallet_13) → Children (Pallet_LOD0, Pallet_LOD1, ...)
            var childResults = new List<(GameObject, int)>();
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                var cm = Regex.Match(child.name, @"^(.+?)[_\-\s]*LOD(\d+)$", RegexOptions.IgnoreCase);
                if (cm.Success)
                    childResults.Add((child, int.Parse(cm.Groups[2].Value)));
            }

            if (childResults.Count > 0)
            {
                childResults.Sort((a, b) => a.Item2.CompareTo(b.Item2));
                return childResults;
            }

            return null;
        }

        internal static LODGroup CreateLodGroupStatic(List<(GameObject go, int lodIndex)> siblings)
        {
            var lodRoot = siblings[0].go.transform.parent.gameObject;

            var lodGroup = Undo.AddComponent<LODGroup>(lodRoot);

            // Remap to contiguous indices (handles gaps like LOD0, LOD2 → slot 0, 1)
            var lods = new LOD[siblings.Count];
            for (int i = 0; i < siblings.Count; i++)
            {
                var renderers = siblings[i].go.GetComponentsInChildren<Renderer>();
                lods[i] = new LOD(Mathf.Pow(0.5f, i + 1), renderers);
            }

            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();

            return lodGroup;
        }

        internal static LODGroup CreateLodGroupFromRenderers(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return null;

            var lodGroup = Undo.AddComponent<LODGroup>(root);
            lodGroup.SetLODs(new[] { new LOD(0.01f, renderers) });
            lodGroup.RecalculateBounds();

            return lodGroup;
        }

        void CreateLodGroup(List<(GameObject go, int lodIndex)> siblings)
        {
            var lodGroup = CreateLodGroupStatic(siblings);
            ctx.Refresh(lodGroup);
            requestRepaint?.Invoke();

            UvtLog.Info($"[LOD Gen] Created LODGroup on '{lodGroup.gameObject.name}' with {siblings.Count} LODs.");
        }

        void RefreshDetectedLodCache(GameObject selected, List<(GameObject go, int lodIndex)> siblings)
        {
            int selectionId = selected != null ? selected.GetInstanceID() : -1;
            if (selectionId == cachedLodSelectionId && cachedDetectedLods.Count == siblings.Count)
                return;

            cachedLodSelectionId = selectionId;
            cachedDetectedLods.Clear();
            foreach (var (go, lodIndex) in siblings)
            {
                var renderers = go.GetComponentsInChildren<Renderer>();
                int tris = 0;
                foreach (var r in renderers)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    tris += GetTriangleCount(mf != null ? mf.sharedMesh : null);
                }
                cachedDetectedLods.Add((go, lodIndex, renderers.Length, tris));
            }
        }

        bool SelectionHasRenderers(GameObject selected)
        {
            int selectionId = selected != null ? selected.GetInstanceID() : -1;
            if (selectionId != cachedRendererSelectionId)
            {
                cachedRendererSelectionId = selectionId;
                cachedSelectionHasRenderers = selected != null && selected.GetComponentInChildren<Renderer>() != null;
            }
            return cachedSelectionHasRenderers;
        }

        static int GetTriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            long indexCount = 0;
            int subMeshCount = mesh.subMeshCount;
            for (int i = 0; i < subMeshCount; i++)
                indexCount += mesh.GetIndexCount(i);
            return (int)(indexCount / 3L);
        }
    }
}
