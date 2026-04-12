// ModelBuilderTool.cs — Model Builder tool (IUvTool tab).
// Provides 3D scene preview of mesh channels, edge topology, and problem areas.
// PR #1: preview modes only. PR #2: cleanup scan/fix migration. PR #3: LOD + collision management.

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    public class ModelBuilderTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        System.Action requestRepaint;

        // ── Identity ──
        public string ToolName  => "Model Builder";
        public string ToolId    => "model_builder";
        public int    ToolOrder => 44;
        public System.Action RequestRepaint { set => requestRepaint = value; }

        // ── Preview ──
        enum PreviewMode
        {
            None,
            VertexColors,
            Normals,
            Tangents,
            UV0, UV1, UV2, UV3, UV4, UV5, UV6, UV7,
            EdgeWireframe,
            ProblemAreas
        }

        PreviewMode previewMode = PreviewMode.None;
        ModelBuilderPreview preview;

        // ── Hierarchy editing state ──
        Dictionary<int, string> pendingNames; // instanceID → edited name
        bool hierarchyFoldout = true;

        // ── Split/Merge state ──
        struct SplitCandidate
        {
            public MeshEntry entry;
            public bool include;
        }
        struct MergeGroup
        {
            public int lodIndex;
            public Material material;
            public List<MeshEntry> entries;
            public bool include;
        }
        List<SplitCandidate> splitCandidates;
        List<MergeGroup> mergeCandidates;
        bool splitMergeFoldout;

        // ── Edge report cache ──
        struct MeshEdgeReport
        {
            public string meshName;
            public EdgeAnalyzer.EdgeReport report;
        }
        List<MeshEdgeReport> edgeReports;

        // ── Problem scan cache ──
        struct ProblemSummary
        {
            public string meshName;
            public int degenerateTris;
            public int unusedVerts;
            public int falseSeamVerts;
        }
        List<ProblemSummary> problemSummaries;

        // ── Lifecycle ──

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            this.ctx = ctx;
            this.canvas = canvas;
            if (preview == null) preview = new ModelBuilderPreview();
            pendingNames = new Dictionary<int, string>();
        }

        public void OnDeactivate()
        {
            preview?.Restore();
            previewMode = PreviewMode.None;
        }

        public void OnRefresh()
        {
            preview?.Restore();
            previewMode = PreviewMode.None;
            edgeReports = null;
            problemSummaries = null;
            pendingNames?.Clear();
            splitCandidates = null;
            mergeCandidates = null;
        }

        // ── UI: Sidebar ──

        public void OnDrawSidebar()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Model Builder", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (ctx == null || (ctx.LodGroup == null && !ctx.StandaloneMesh))
            {
                EditorGUILayout.HelpBox(
                    "Select a GameObject with LODGroup or MeshRenderer.",
                    MessageType.Info);
                return;
            }

            DrawPreviewModeToolbar();
            DrawHierarchySection();
            DrawSplitMergeSection();
            DrawMeshInfo();

            if (edgeReports != null)
                DrawEdgeReportSection();

            if (problemSummaries != null)
                DrawProblemSummarySection();

            DrawEdgeLegend();
        }

        // ═══════════════════════════════════════════════════════════
        // Preview mode toolbar
        // ═══════════════════════════════════════════════════════════

        void DrawPreviewModeToolbar()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Scene Preview", EditorStyles.miniLabel);

            // Row 1: Off, Vert Colors, Normals, Tangents
            EditorGUILayout.BeginHorizontal();
            DrawModeButton("Off", PreviewMode.None);
            DrawModeButton("Vert Colors", PreviewMode.VertexColors);
            DrawModeButton("Normals", PreviewMode.Normals);
            DrawModeButton("Tangents", PreviewMode.Tangents);
            EditorGUILayout.EndHorizontal();

            // Row 2: UV channels
            EditorGUILayout.BeginHorizontal();
            DrawModeButton("UV0", PreviewMode.UV0);
            DrawModeButton("UV1", PreviewMode.UV1);
            DrawModeButton("UV2", PreviewMode.UV2);
            DrawModeButton("UV3", PreviewMode.UV3);
            EditorGUILayout.EndHorizontal();

            // Row 3: Edges, Problems
            EditorGUILayout.BeginHorizontal();
            DrawModeButton("Edges", PreviewMode.EdgeWireframe);
            DrawModeButton("Problems", PreviewMode.ProblemAreas);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
        }

        void DrawModeButton(string label, PreviewMode mode)
        {
            var bgc = GUI.backgroundColor;
            if (previewMode == mode)
                GUI.backgroundColor = new Color(0.3f, 0.7f, 1f);

            if (GUILayout.Button(label, GUILayout.Height(20)))
            {
                if (previewMode == mode)
                {
                    // Toggle off
                    preview.Restore();
                    previewMode = PreviewMode.None;
                    edgeReports = null;
                    problemSummaries = null;
                }
                else
                {
                    preview.Restore();
                    previewMode = mode;
                    edgeReports = null;
                    problemSummaries = null;
                    ActivateCurrentPreview();
                }
                SceneView.RepaintAll();
                requestRepaint?.Invoke();
            }

            GUI.backgroundColor = bgc;
        }

        void ActivateCurrentPreview()
        {
            switch (previewMode)
            {
                case PreviewMode.VertexColors:
                    preview.ActivateVertexColorPreview(ctx);
                    break;
                case PreviewMode.Normals:
                    preview.ActivateNormalsPreview(ctx);
                    break;
                case PreviewMode.Tangents:
                    preview.ActivateTangentsPreview(ctx);
                    break;
                case PreviewMode.UV0: case PreviewMode.UV1:
                case PreviewMode.UV2: case PreviewMode.UV3:
                case PreviewMode.UV4: case PreviewMode.UV5:
                case PreviewMode.UV6: case PreviewMode.UV7:
                    int channel = previewMode - PreviewMode.UV0;
                    preview.ActivateUvPreview(ctx, channel);
                    break;
                case PreviewMode.EdgeWireframe:
                    preview.BuildEdgeOverlays(ctx);
                    BuildEdgeReports();
                    break;
                case PreviewMode.ProblemAreas:
                    preview.ActivateProblemPreview(ctx);
                    BuildProblemSummaries();
                    break;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Hierarchy section with inline name editing
        // ═══════════════════════════════════════════════════════════

        void DrawHierarchySection()
        {
            EditorGUILayout.Space(8);
            hierarchyFoldout = EditorGUILayout.Foldout(hierarchyFoldout, "Hierarchy", true);
            if (!hierarchyFoldout) return;

            if (ctx.LodGroup == null && !ctx.StandaloneMesh) return;

            Transform root = ctx.LodGroup != null ? ctx.LodGroup.transform : null;
            if (root == null) return;

            var colSet = new HashSet<GameObject>(MeshHygieneUtility.FindCollisionObjects(root));

            // Root name
            EditorGUI.indentLevel++;
            DrawEditableName(root.gameObject, "Root");

            // Children grouped by LOD
            int lodCount = ctx.LodCount;
            for (int li = 0; li < lodCount; li++)
            {
                var entries = ctx.ForLod(li);
                if (entries.Count == 0) continue;

                foreach (var e in entries)
                {
                    if (e.renderer == null) continue;
                    Mesh mesh = e.originalMesh ?? e.fbxMesh;
                    int verts = mesh != null ? mesh.vertexCount : 0;
                    int tris = mesh != null ? MeshHygieneUtility.GetTriangleCount(mesh) : 0;
                    string suffix = $"LOD{li}  {verts:N0}v / {tris:N0}t";
                    DrawEditableName(e.renderer.gameObject, suffix);
                }
            }

            // Collision objects
            foreach (var colGo in colSet)
            {
                if (colGo == null) continue;
                var mf = colGo.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                int verts = mesh != null ? mesh.vertexCount : 0;
                string suffix = $"COL  {verts:N0}v";
                DrawEditableName(colGo, suffix);
            }

            EditorGUI.indentLevel--;

            // Apply / Normalize buttons
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            bool hasPending = pendingNames != null && pendingNames.Count > 0;
            var bgc = GUI.backgroundColor;

            GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
            GUI.enabled = hasPending;
            if (GUILayout.Button($"Apply Names ({(hasPending ? pendingNames.Count : 0)})", GUILayout.Height(22)))
                ApplyPendingNames();
            GUI.enabled = true;

            GUI.backgroundColor = new Color(0.7f, 0.4f, 0.95f);
            if (GUILayout.Button("Normalize", GUILayout.Height(22)))
                NormalizeHierarchy();

            GUI.backgroundColor = bgc;
            EditorGUILayout.EndHorizontal();

            // Warnings
            if (MeshHygieneUtility.HasLodOrColSuffix(root.name))
                EditorGUILayout.HelpBox("Root name has LOD/COL suffix.", MessageType.Warning);

            var rootMf = root.GetComponent<MeshFilter>();
            if (rootMf != null && rootMf.sharedMesh != null)
                EditorGUILayout.HelpBox("Root has mesh — should be empty pivot.", MessageType.Warning);
        }

        void DrawEditableName(GameObject go, string suffix)
        {
            if (go == null) return;
            int id = go.GetInstanceID();

            // Get current editing value or actual name
            if (!pendingNames.TryGetValue(id, out string editName))
                editName = go.name;

            EditorGUILayout.BeginHorizontal();

            // Editable name field
            string newName = EditorGUILayout.TextField(editName, GUILayout.MinWidth(100));
            if (newName != editName)
            {
                if (newName != go.name)
                    pendingNames[id] = newName;
                else
                    pendingNames.Remove(id);
            }

            // Suffix label (LOD0, COL, etc.)
            EditorGUILayout.LabelField(suffix, EditorStyles.miniLabel, GUILayout.Width(150));

            // Changed indicator
            if (pendingNames.ContainsKey(id))
            {
                var r = EditorGUILayout.GetControlRect(false, 14, GUILayout.Width(14));
                EditorGUI.DrawRect(new Rect(r.x + 2, r.y + 2, 10, 10), new Color(1f, 0.7f, 0.2f));
            }

            EditorGUILayout.EndHorizontal();
        }

        void ApplyPendingNames()
        {
            if (pendingNames == null || pendingNames.Count == 0) return;

            Undo.SetCurrentGroupName("Model Builder: Rename");
            int group = Undo.GetCurrentGroup();

            foreach (var kvp in pendingNames)
            {
                var go = EditorUtility.InstanceIDToObject(kvp.Key) as GameObject;
                if (go == null || go.name == kvp.Value) continue;

                Undo.RecordObject(go, "Rename");
                UvtLog.Info($"Renamed: {go.name} -> {kvp.Value}");
                go.name = kvp.Value;
            }

            Undo.CollapseUndoOperations(group);
            pendingNames.Clear();

            // Refresh context since names changed
            if (ctx.LodGroup != null) ctx.Refresh(ctx.LodGroup);
            requestRepaint?.Invoke();
        }

        void NormalizeHierarchy()
        {
            if (ctx.LodGroup == null) return;

            Undo.SetCurrentGroupName("Model Builder: Normalize");
            int group = Undo.GetCurrentGroup();

            var root = ctx.LodGroup.transform;
            string baseName = UvToolContext.ExtractGroupKey(root.name);
            if (string.IsNullOrEmpty(baseName)) baseName = root.name;

            // Sanitize root name
            if (MeshHygieneUtility.HasInvalidChars(root.name))
            {
                string sanitized = MeshHygieneUtility.SanitizeName(root.name);
                Undo.RecordObject(root.gameObject, "Sanitize Root");
                root.name = sanitized;
                baseName = UvToolContext.ExtractGroupKey(sanitized);
            }

            // Strip LOD/COL suffix from root
            if (MeshHygieneUtility.HasLodOrColSuffix(root.name))
            {
                Undo.RecordObject(root.gameObject, "Strip Root Suffix");
                root.name = baseName;
            }

            // Move root mesh to child if root has mesh
            var rootMf = root.GetComponent<MeshFilter>();
            if (rootMf != null && rootMf.sharedMesh != null)
            {
                var rootMr = root.GetComponent<MeshRenderer>();
                var lod0Child = new GameObject(baseName + "_LOD0");
                Undo.RegisterCreatedObjectUndo(lod0Child, "Move Root Mesh");
                lod0Child.transform.SetParent(root, false);

                var newMf = lod0Child.AddComponent<MeshFilter>();
                newMf.sharedMesh = rootMf.sharedMesh;
                if (rootMr != null)
                {
                    var newMr = lod0Child.AddComponent<MeshRenderer>();
                    newMr.sharedMaterials = rootMr.sharedMaterials;
                    newMr.shadowCastingMode = rootMr.shadowCastingMode;
                    newMr.receiveShadows = rootMr.receiveShadows;
                    GameObjectUtility.SetStaticEditorFlags(lod0Child,
                        GameObjectUtility.GetStaticEditorFlags(root.gameObject));
                    Undo.DestroyObjectImmediate(rootMr);
                }
                Undo.DestroyObjectImmediate(rootMf);
            }

            // Sort LOD children by polycount, rename _LOD0, _LOD1, etc.
            var colSet = new HashSet<GameObject>(MeshHygieneUtility.FindCollisionObjects(root));
            var lodCandidates = new List<(Transform t, int polyCount)>();
            foreach (Transform child in root)
            {
                if (colSet.Contains(child.gameObject)) continue;
                var mf = child.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                lodCandidates.Add((child, MeshHygieneUtility.GetTriangleCount(mf.sharedMesh)));
            }
            lodCandidates.Sort((a, b) => b.polyCount.CompareTo(a.polyCount));

            for (int i = 0; i < lodCandidates.Count; i++)
            {
                string newName = baseName + "_LOD" + i;
                if (lodCandidates[i].t.name != newName)
                {
                    Undo.RecordObject(lodCandidates[i].t.gameObject, "Rename LOD");
                    lodCandidates[i].t.name = newName;
                }
            }

            // Sanitize child names
            foreach (Transform child in root)
            {
                if (MeshHygieneUtility.HasInvalidChars(child.name))
                {
                    string sanitized = MeshHygieneUtility.SanitizeName(child.name);
                    Undo.RecordObject(child.gameObject, "Sanitize Name");
                    child.name = sanitized;
                }
            }

            // Rebuild LODGroup from hierarchy naming
            RebuildLodGroupFromNames();

            Undo.CollapseUndoOperations(group);
            pendingNames?.Clear();

            ctx.Refresh(ctx.LodGroup);
            requestRepaint?.Invoke();
            UvtLog.Info("Normalized hierarchy.");
        }

        void RebuildLodGroupFromNames()
        {
            if (ctx.LodGroup == null) return;

            var root = ctx.LodGroup.transform;
            var colSet = new HashSet<GameObject>(MeshHygieneUtility.FindCollisionObjects(root));
            var lodChildren = new SortedDictionary<int, List<Renderer>>();

            foreach (Transform child in root)
            {
                if (colSet.Contains(child.gameObject)) continue;

                var match = System.Text.RegularExpressions.Regex.Match(
                    child.name, @"_LOD(\d+)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success) continue;

                int lodIdx = int.Parse(match.Groups[1].Value);
                var r = child.GetComponent<Renderer>();
                if (r == null) continue;

                if (!lodChildren.ContainsKey(lodIdx))
                    lodChildren[lodIdx] = new List<Renderer>();
                lodChildren[lodIdx].Add(r);
            }

            if (lodChildren.Count == 0) return;

            Undo.RecordObject(ctx.LodGroup, "Rebuild LODGroup");
            int maxLod = 0;
            foreach (var k in lodChildren.Keys)
                if (k > maxLod) maxLod = k;
            maxLod++;

            var newLods = new LOD[maxLod];
            for (int i = 0; i < maxLod; i++)
            {
                Renderer[] renderers;
                if (lodChildren.TryGetValue(i, out var list))
                    renderers = list.ToArray();
                else
                    renderers = new Renderer[0];

                float screenHeight = i == 0 ? 0.5f : (1f / (i + 1));
                newLods[i] = new LOD(screenHeight, renderers);
            }
            ctx.LodGroup.SetLODs(newLods);
            ctx.LodGroup.RecalculateBounds();
        }

        // ═══════════════════════════════════════════════════════════
        // Split / Merge section
        // ═══════════════════════════════════════════════════════════

        void DrawSplitMergeSection()
        {
            EditorGUILayout.Space(8);
            splitMergeFoldout = EditorGUILayout.Foldout(splitMergeFoldout, "Split / Merge", true);
            if (!splitMergeFoldout) return;

            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.6f, 0.75f, 0.9f);
            if (GUILayout.Button("Scan Split/Merge", GUILayout.Height(22)))
                ScanSplitMerge();
            GUI.backgroundColor = bgc;

            // ── Split by Material ──
            if (splitCandidates != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Split Multi-Material", EditorStyles.boldLabel);

                if (splitCandidates.Count == 0)
                {
                    EditorGUILayout.LabelField("  No multi-material meshes.", EditorStyles.miniLabel);
                }
                else
                {
                    for (int i = 0; i < splitCandidates.Count; i++)
                    {
                        var sc = splitCandidates[i];
                        if (sc.entry.renderer == null) continue;
                        var mesh = sc.entry.originalMesh ?? sc.entry.fbxMesh;
                        if (mesh == null) continue;
                        var mats = sc.entry.renderer.sharedMaterials;

                        EditorGUILayout.BeginHorizontal();
                        sc.include = EditorGUILayout.Toggle(sc.include, GUILayout.Width(16));
                        splitCandidates[i] = sc;

                        string info = $"LOD{sc.entry.lodIndex} {sc.entry.renderer.name}: {mesh.subMeshCount} submeshes";
                        EditorGUILayout.LabelField(info, EditorStyles.miniLabel);
                        EditorGUILayout.EndHorizontal();

                        // Preview names
                        if (sc.include)
                        {
                            string srcName = sc.entry.renderer.name;
                            string lodSuffix = "";
                            var lodMatch = System.Text.RegularExpressions.Regex.Match(
                                srcName, @"([_\-\s]+LOD\d+)$",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (lodMatch.Success)
                            {
                                lodSuffix = lodMatch.Value;
                                srcName = srcName.Substring(0, srcName.Length - lodSuffix.Length);
                            }

                            EditorGUILayout.LabelField("      Create:", EditorStyles.miniLabel);
                            for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
                            {
                                string matName = mats[s] != null ? mats[s].name : $"mat{s}";
                                string newName = $"{srcName}_{matName}{lodSuffix}";
                                EditorGUILayout.LabelField($"        {newName}", EditorStyles.miniLabel);
                            }
                        }
                    }

                    // Split preview + apply buttons
                    EditorGUILayout.BeginHorizontal();
                    int splitSel = 0;
                    foreach (var s in splitCandidates) if (s.include) splitSel++;

                    GUI.backgroundColor = new Color(0.3f, 0.7f, 1f);
                    GUI.enabled = splitSel > 0;
                    if (GUILayout.Button("Preview Split", GUILayout.Height(24)))
                    {
                        preview.Restore();
                        previewMode = PreviewMode.None;
                        preview.ActivateSplitPreview(ctx, splitCandidates.FindAll(s => s.include)
                            .ConvertAll(s => (s.entry.renderer, s.entry.originalMesh ?? s.entry.fbxMesh)));
                    }

                    GUI.backgroundColor = new Color(0.7f, 0.4f, 0.95f);
                    if (GUILayout.Button($"Split ({splitSel})", GUILayout.Height(24)))
                        FixSplitByMaterial();
                    GUI.enabled = true;
                    GUI.backgroundColor = bgc;
                    EditorGUILayout.EndHorizontal();
                }
            }

            // ── Merge Same-Material ──
            if (mergeCandidates != null)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Merge Same-Material", EditorStyles.boldLabel);

                if (mergeCandidates.Count == 0)
                {
                    EditorGUILayout.LabelField("  No merge candidates.", EditorStyles.miniLabel);
                }
                else
                {
                    for (int i = 0; i < mergeCandidates.Count; i++)
                    {
                        var g = mergeCandidates[i];
                        EditorGUILayout.BeginHorizontal();
                        g.include = EditorGUILayout.Toggle(g.include, GUILayout.Width(16));
                        mergeCandidates[i] = g;

                        string info = $"LOD{g.lodIndex} \"{g.material.name}\": {g.entries.Count} objects";
                        EditorGUILayout.LabelField(info, EditorStyles.miniLabel);
                        EditorGUILayout.EndHorizontal();

                        if (g.include)
                        {
                            int totalVerts = 0;
                            foreach (var e in g.entries)
                            {
                                var mesh = e.originalMesh ?? e.fbxMesh;
                                int verts = mesh != null ? mesh.vertexCount : 0;
                                totalVerts += verts;
                                EditorGUILayout.LabelField(
                                    $"        {e.renderer.name} ({verts:N0} v)", EditorStyles.miniLabel);
                            }
                            EditorGUILayout.LabelField(
                                $"      -> merged ({totalVerts:N0} v)", EditorStyles.miniLabel);
                        }
                    }

                    int mergeSel = 0;
                    foreach (var g in mergeCandidates) if (g.include) mergeSel++;

                    GUI.backgroundColor = new Color(0.7f, 0.4f, 0.95f);
                    GUI.enabled = mergeSel > 0;
                    if (GUILayout.Button($"Merge ({mergeSel})", GUILayout.Height(24)))
                        FixMerge();
                    GUI.enabled = true;
                    GUI.backgroundColor = bgc;
                }
            }
        }

        void ScanSplitMerge()
        {
            splitCandidates = new List<SplitCandidate>();
            mergeCandidates = new List<MergeGroup>();

            var mergeMap = new Dictionary<string, MergeGroup>();

            for (int li = 0; li < ctx.LodCount; li++)
            {
                var entries = ctx.ForLod(li);
                foreach (var e in entries)
                {
                    var mesh = e.originalMesh ?? e.fbxMesh;
                    if (mesh == null || e.renderer == null) continue;

                    if (mesh.subMeshCount > 1)
                    {
                        splitCandidates.Add(new SplitCandidate
                        {
                            entry = e,
                            include = true
                        });
                    }

                    var mats = e.renderer.sharedMaterials;
                    if (mesh.subMeshCount == 1 && mats.Length == 1 && mats[0] != null)
                    {
                        string key = $"{li}_{mats[0].GetInstanceID()}";
                        if (!mergeMap.ContainsKey(key))
                            mergeMap[key] = new MergeGroup
                            {
                                lodIndex = li,
                                material = mats[0],
                                entries = new List<MeshEntry>(),
                                include = true
                            };
                        mergeMap[key].entries.Add(e);
                    }
                }
            }

            foreach (var kvp in mergeMap)
                if (kvp.Value.entries.Count > 1)
                    mergeCandidates.Add(kvp.Value);

            UvtLog.Info($"Split/Merge scan: {splitCandidates.Count} split, {mergeCandidates.Count} merge.");
        }

        void FixSplitByMaterial()
        {
            if (splitCandidates == null) return;

            Undo.SetCurrentGroupName("Model Builder: Split by Material");
            int undoGroup = Undo.GetCurrentGroup();
            int split = 0;

            foreach (var sc in splitCandidates)
            {
                if (!sc.include) continue;
                if (sc.entry.renderer == null || sc.entry.meshFilter == null) continue;
                var mesh = sc.entry.originalMesh ?? sc.entry.fbxMesh;
                if (mesh == null || !mesh.isReadable) continue;

                var mats = sc.entry.renderer.sharedMaterials;
                string srcName = sc.entry.renderer.name;
                string lodSuffix = "";
                var lodMatch = System.Text.RegularExpressions.Regex.Match(
                    srcName, @"([_\-\s]+LOD\d+)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (lodMatch.Success)
                {
                    lodSuffix = lodMatch.Value;
                    srcName = srcName.Substring(0, srcName.Length - lodSuffix.Length);
                }

                var parent = sc.entry.renderer.transform.parent;

                for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
                {
                    string matName = mats[s] != null ? mats[s].name : $"mat{s}";
                    string childName = $"{srcName}_{matName}{lodSuffix}";

                    // Extract submesh
                    var subTris = mesh.GetTriangles(s);
                    var subMesh = MeshHygieneUtility.ExtractSubmesh(mesh, subTris);
                    if (subMesh == null) continue;
                    subMesh.name = childName;

                    var childGo = new GameObject(childName);
                    Undo.RegisterCreatedObjectUndo(childGo, "Split");
                    childGo.transform.SetParent(parent, false);
                    childGo.transform.localPosition = sc.entry.renderer.transform.localPosition;
                    childGo.transform.localRotation = sc.entry.renderer.transform.localRotation;
                    childGo.transform.localScale = sc.entry.renderer.transform.localScale;

                    var newMf = childGo.AddComponent<MeshFilter>();
                    newMf.sharedMesh = subMesh;
                    var newMr = childGo.AddComponent<MeshRenderer>();
                    newMr.sharedMaterials = new[] { mats[s] };

                    GameObjectUtility.SetStaticEditorFlags(childGo,
                        GameObjectUtility.GetStaticEditorFlags(sc.entry.renderer.gameObject));
                }

                UvtLog.Info($"Split: {sc.entry.renderer.name} -> {mesh.subMeshCount} children");
                Undo.DestroyObjectImmediate(sc.entry.renderer.gameObject);
                split++;
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (split > 0)
            {
                ctx.Refresh(ctx.LodGroup);
                RebuildLodGroupFromNames();
                ctx.LodGroup.RecalculateBounds();
            }

            splitCandidates = null;
            mergeCandidates = null;
            preview?.Restore();
            previewMode = PreviewMode.None;
            requestRepaint?.Invoke();
        }

        void FixMerge()
        {
            if (mergeCandidates == null) return;

            Undo.SetCurrentGroupName("Model Builder: Merge");
            int undoGroup = Undo.GetCurrentGroup();
            int merged = 0;

            foreach (var g in mergeCandidates)
            {
                if (!g.include || g.entries.Count < 2) continue;

                var firstEntry = g.entries[0];
                if (firstEntry.renderer == null) continue;

                var parent = firstEntry.renderer.transform.parent;
                var baseMatrix = firstEntry.renderer.transform.worldToLocalMatrix;

                // Combine meshes
                var allPos = new List<Vector3>();
                var allNormals = new List<Vector3>();
                var allUvs = new List<Vector2>();
                var allTris = new List<int>();
                var destroyList = new List<GameObject>();

                foreach (var e in g.entries)
                {
                    var mesh = e.originalMesh ?? e.fbxMesh;
                    if (mesh == null || e.renderer == null) continue;

                    var verts = mesh.vertices;
                    var normals = mesh.normals;
                    var uvs = mesh.uv;
                    var tris = mesh.triangles;

                    Matrix4x4 toFirst = baseMatrix * e.renderer.transform.localToWorldMatrix;
                    int vertBase = allPos.Count;

                    for (int v = 0; v < verts.Length; v++)
                    {
                        allPos.Add(toFirst.MultiplyPoint3x4(verts[v]));
                        if (normals != null && v < normals.Length)
                            allNormals.Add(toFirst.MultiplyVector(normals[v]).normalized);
                        if (uvs != null && v < uvs.Length)
                            allUvs.Add(uvs[v]);
                    }

                    for (int t = 0; t < tris.Length; t++)
                        allTris.Add(tris[t] + vertBase);

                    if (e != firstEntry)
                        destroyList.Add(e.renderer.gameObject);
                }

                var mergedMesh = new Mesh();
                mergedMesh.name = firstEntry.renderer.name;
                mergedMesh.SetVertices(allPos);
                if (allNormals.Count == allPos.Count) mergedMesh.SetNormals(allNormals);
                if (allUvs.Count == allPos.Count) mergedMesh.SetUVs(0, allUvs);
                mergedMesh.SetTriangles(allTris, 0);
                mergedMesh.RecalculateBounds();

                Undo.RecordObject(firstEntry.meshFilter, "Merge");
                firstEntry.meshFilter.sharedMesh = mergedMesh;

                // Update LODGroup renderers
                var lods = ctx.LodGroup.GetLODs();
                for (int li = 0; li < lods.Length; li++)
                {
                    var renderers = new List<Renderer>(lods[li].renderers);
                    bool replaced = false;
                    for (int ri = renderers.Count - 1; ri >= 0; ri--)
                    {
                        if (renderers[ri] == null) continue;
                        if (destroyList.Contains(renderers[ri].gameObject))
                        {
                            if (!replaced)
                            {
                                renderers[ri] = firstEntry.renderer;
                                replaced = true;
                            }
                            else
                                renderers.RemoveAt(ri);
                        }
                    }
                    lods[li].renderers = renderers.ToArray();
                }
                ctx.LodGroup.SetLODs(lods);

                foreach (var go in destroyList)
                {
                    if (go == null) continue;
                    Undo.DestroyObjectImmediate(go);
                }

                merged++;
                UvtLog.Info($"Merged: {g.entries.Count} objects -> {firstEntry.renderer.name}");
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (merged > 0)
            {
                ctx.Refresh(ctx.LodGroup);
                ctx.LodGroup.RecalculateBounds();
            }

            splitCandidates = null;
            mergeCandidates = null;
            preview?.Restore();
            previewMode = PreviewMode.None;
            requestRepaint?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════
        // Mesh info section
        // ═══════════════════════════════════════════════════════════

        void DrawMeshInfo()
        {
            if (ctx.MeshEntries == null || ctx.MeshEntries.Count == 0) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Mesh Entries", EditorStyles.boldLabel);

            int lodCount = ctx.LodCount;
            for (int li = 0; li < lodCount; li++)
            {
                var entries = ctx.ForLod(li);
                if (entries.Count == 0) continue;

                int totalVerts = 0, totalTris = 0;
                foreach (var e in entries)
                {
                    Mesh m = e.originalMesh ?? e.fbxMesh;
                    if (m == null) continue;
                    totalVerts += m.vertexCount;
                    totalTris += MeshHygieneUtility.GetTriangleCount(m);
                }

                EditorGUILayout.LabelField(
                    $"  LOD{li}: {entries.Count} mesh(es), {totalVerts:N0}v / {totalTris:N0}t",
                    EditorStyles.miniLabel);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Edge report
        // ═══════════════════════════════════════════════════════════

        void BuildEdgeReports()
        {
            edgeReports = new List<MeshEdgeReport>();
            if (ctx?.MeshEntries == null) return;

            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.renderer == null) continue;
                Mesh mesh = e.originalMesh ?? e.fbxMesh;
                if (mesh == null || !mesh.isReadable) continue;

                var report = EdgeAnalyzer.Analyze(mesh, out _);
                edgeReports.Add(new MeshEdgeReport
                {
                    meshName = mesh.name,
                    report = report
                });
            }
        }

        void DrawEdgeReportSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Edge Analysis", EditorStyles.boldLabel);

            foreach (var er in edgeReports)
            {
                string name = er.meshName;
                if (name.Length > 25) name = name.Substring(0, 22) + "...";

                var r = er.report;
                var parts = new List<string>();
                if (r.borderEdges > 0) parts.Add($"border:{r.borderEdges}");
                if (r.uvSeamEdges > 0) parts.Add($"seam:{r.uvSeamEdges}");
                if (r.hardEdges > 0) parts.Add($"hard:{r.hardEdges}");
                if (r.nonManifoldEdges > 0) parts.Add($"non-manifold:{r.nonManifoldEdges}");
                if (r.uvFoldoverEdges > 0) parts.Add($"foldover:{r.uvFoldoverEdges}");

                string info = parts.Count > 0 ? string.Join(", ", parts) : "clean";
                EditorGUILayout.LabelField($"  {name}", $"{r.totalEdges} edges: {info}",
                    EditorStyles.miniLabel);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Problem summary
        // ═══════════════════════════════════════════════════════════

        void BuildProblemSummaries()
        {
            problemSummaries = new List<ProblemSummary>();
            if (ctx?.MeshEntries == null) return;

            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.renderer == null) continue;
                Mesh mesh = e.originalMesh ?? e.fbxMesh;
                if (mesh == null || !mesh.isReadable) continue;

                int degCount = MeshHygieneUtility.CountDegenerateTriangles(mesh);
                int unusedCount = MeshHygieneUtility.CountUnusedVertices(mesh);
                var seamVerts = Uv0Analyzer.GetFalseSeamVertices(mesh);
                int seamCount = seamVerts?.Count ?? 0;

                if (degCount > 0 || unusedCount > 0 || seamCount > 0)
                {
                    problemSummaries.Add(new ProblemSummary
                    {
                        meshName = mesh.name,
                        degenerateTris = degCount,
                        unusedVerts = unusedCount,
                        falseSeamVerts = seamCount
                    });
                }
            }
        }

        void DrawProblemSummarySection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Problem Areas", EditorStyles.boldLabel);

            if (problemSummaries.Count == 0)
            {
                EditorGUILayout.LabelField("  No problems detected.", EditorStyles.miniLabel);
                return;
            }

            foreach (var ps in problemSummaries)
            {
                string name = ps.meshName;
                if (name.Length > 25) name = name.Substring(0, 22) + "...";

                var parts = new List<string>();
                if (ps.degenerateTris > 0) parts.Add($"degen:{ps.degenerateTris}");
                if (ps.unusedVerts > 0) parts.Add($"unused:{ps.unusedVerts}");
                if (ps.falseSeamVerts > 0) parts.Add($"weld:{ps.falseSeamVerts}");

                EditorGUILayout.LabelField($"  {name}", string.Join(", ", parts),
                    EditorStyles.miniLabel);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Edge legend (shown when Edge mode is active)
        // ═══════════════════════════════════════════════════════════

        void DrawEdgeLegend()
        {
            if (previewMode != PreviewMode.EdgeWireframe && previewMode != PreviewMode.ProblemAreas)
                return;

            EditorGUILayout.Space(8);

            if (previewMode == PreviewMode.EdgeWireframe)
            {
                EditorGUILayout.LabelField("Legend", EditorStyles.boldLabel);
                DrawLegendItem(new Color(1f, 1f, 1f), "Border");
                DrawLegendItem(new Color(1f, 0.9f, 0.2f), "UV Seam");
                DrawLegendItem(new Color(0.3f, 0.5f, 1f), "Hard Edge");
                DrawLegendItem(new Color(1f, 0.2f, 0.2f), "Non-Manifold");
                DrawLegendItem(new Color(1f, 0.3f, 0.9f), "UV Foldover");
                DrawLegendItem(new Color(0.4f, 0.4f, 0.4f), "Interior");
            }
            else if (previewMode == PreviewMode.ProblemAreas)
            {
                EditorGUILayout.LabelField("Legend", EditorStyles.boldLabel);
                DrawLegendItem(new Color(1f, 0.16f, 0.16f), "Degenerate Tri");
                DrawLegendItem(new Color(0.16f, 0.86f, 0.86f), "Weld Candidate");
                DrawLegendItem(new Color(1f, 0.86f, 0.16f), "Unused Vertex (dot)");
                DrawLegendItem(new Color(0.24f, 0.71f, 0.31f), "Healthy");
            }
        }

        void DrawLegendItem(Color color, string label)
        {
            var rect = EditorGUILayout.GetControlRect(false, 16);
            rect.x += 8;
            var colorRect = new Rect(rect.x, rect.y + 3, 10, 10);
            EditorGUI.DrawRect(colorRect, color);
            var labelRect = new Rect(rect.x + 16, rect.y, rect.width - 24, rect.height);
            EditorGUI.LabelField(labelRect, label, EditorStyles.miniLabel);
        }

        // ═══════════════════════════════════════════════════════════
        // Scene integration
        // ═══════════════════════════════════════════════════════════

        public void OnSceneGUI(SceneView sv)
        {
            if (previewMode == PreviewMode.EdgeWireframe && preview != null && preview.HasEdgeOverlays)
            {
                preview.DrawEdgeWireframe(sv);
            }

            if (previewMode == PreviewMode.ProblemAreas && preview != null && preview.HasUnusedVertOverlays)
            {
                preview.DrawUnusedVertexDots();
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
    }
}
