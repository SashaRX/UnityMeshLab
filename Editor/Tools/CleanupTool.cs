// CleanupTool.cs — Mesh/material/collider/scene hygiene tool (IUvTool tab).
// Provides scan + fix workflow for common issues after UV, LOD, collision, and FBX export operations.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    public class CleanupTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        Action requestRepaint;

        // ── Identity ──
        public string ToolName  => "Cleanup";
        public string ToolId    => "cleanup";
        public int    ToolOrder => 45;
        public Action RequestRepaint { set => requestRepaint = value; }

        // ── Foldout state ──
        bool foldMaterials = true, foldColliders, foldScene, foldMesh;

        // ── Scan results (null = not scanned yet, empty = scanned, no issues) ──
        List<MaterialIssue> materialIssues;
        List<ColliderIssue> colliderIssues;
        List<SceneIssue> sceneIssues;
        MeshReport meshReport;

        // ── Inner types ──

        struct MaterialIssue
        {
            public enum Kind { HiddenShader, MismatchedMaterial, ExtraSlot }
            public Kind kind;
            public Renderer renderer;
            public int submeshIndex;
            public Material current;
            public Material suggested;
            public string description;
        }

        struct ColliderIssue
        {
            public enum Kind { ExtraAttributes, Duplicate }
            public Kind kind;
            public GameObject gameObject;
            public Mesh mesh;
            public string description;
        }

        struct SceneIssue
        {
            public enum Kind { OrphanedLod, LodGroupMismatch }
            public Kind kind;
            public GameObject gameObject;
            public string description;
        }

        class MeshReport
        {
            public List<LodInfo> lods = new List<LodInfo>();
            public List<MeshEntry> weldCandidates = new List<MeshEntry>();
            public List<(MeshEntry entry, List<int> channels)> emptyUvEntries = new List<(MeshEntry, List<int>)>();
        }

        struct LodInfo
        {
            public int lodIndex;
            public int totalVerts, totalTris;
            public List<(string name, int verts, int tris)> meshes;
        }

        // ── Lifecycle ──

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            this.ctx = ctx;
            this.canvas = canvas;
        }

        public void OnDeactivate() { }

        public void OnRefresh()
        {
            materialIssues = null;
            colliderIssues = null;
            sceneIssues = null;
            meshReport = null;
        }

        // ── UI: Sidebar ──

        public void OnDrawSidebar()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Cleanup", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (ctx.LodGroup == null)
            {
                EditorGUILayout.HelpBox(
                    "Select a LODGroup to scan for cleanup issues.",
                    MessageType.Info);
                return;
            }

            DrawMaterialsSection();
            DrawCollidersSection();
            DrawSceneSection();
            DrawMeshSection();
        }

        // ═══════════════════════════════════════════════════════════════
        // Section 1: Fix Materials
        // ═══════════════════════════════════════════════════════════════

        void DrawMaterialsSection()
        {
            EditorGUILayout.Space(8);
            string label = materialIssues != null
                ? $"Fix Materials ({materialIssues.Count} issue{(materialIssues.Count == 1 ? "" : "s")})"
                : "Fix Materials";
            foldMaterials = EditorGUILayout.Foldout(foldMaterials, label, true);
            if (!foldMaterials) return;

            EditorGUI.indentLevel++;

            DrawScanFixButtons(ScanMaterials, FixMaterials, materialIssues);

            if (materialIssues != null)
            {
                if (materialIssues.Count == 0)
                    EditorGUILayout.HelpBox("No material issues found.", MessageType.Info);
                else
                    foreach (var issue in materialIssues)
                        EditorGUILayout.HelpBox(issue.description, MessageType.Warning);
            }

            EditorGUI.indentLevel--;
        }

        void ScanMaterials()
        {
            materialIssues = new List<MaterialIssue>();

            // Build LOD0 material map: groupKey → Material[]
            var lod0Mats = new Dictionary<string, Material[]>();
            foreach (var e in ctx.MeshEntries)
            {
                if (e.lodIndex != 0 || e.renderer == null) continue;
                string key = e.meshGroupKey ?? e.renderer.name;
                if (!lod0Mats.ContainsKey(key))
                    lod0Mats[key] = e.renderer.sharedMaterials;
            }

            foreach (var e in ctx.MeshEntries)
            {
                if (e.renderer == null || e.meshFilter == null) continue;
                var mats = e.renderer.sharedMaterials;
                var mesh = e.meshFilter.sharedMesh;
                if (mesh == null) continue;

                // Check hidden shaders
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    if (mats[i].shader.name.StartsWith("Hidden/LightmapUvTool/"))
                    {
                        string key = e.meshGroupKey ?? e.renderer.name;
                        Material suggested = null;
                        if (lod0Mats.TryGetValue(key, out var l0) && i < l0.Length)
                            suggested = l0[i];

                        materialIssues.Add(new MaterialIssue
                        {
                            kind = MaterialIssue.Kind.HiddenShader,
                            renderer = e.renderer,
                            submeshIndex = i,
                            current = mats[i],
                            suggested = suggested,
                            description = $"{e.renderer.name}: slot [{i}] has hidden shader \"{mats[i].shader.name}\""
                        });
                    }
                }

                // Check LOD1+ material mismatch with LOD0
                if (e.lodIndex > 0)
                {
                    string key = e.meshGroupKey ?? e.renderer.name;
                    if (lod0Mats.TryGetValue(key, out var l0))
                    {
                        int count = Mathf.Min(mats.Length, l0.Length);
                        for (int i = 0; i < count; i++)
                        {
                            if (mats[i] == l0[i]) continue;
                            // Skip if already flagged as hidden shader
                            if (mats[i] != null && mats[i].shader.name.StartsWith("Hidden/LightmapUvTool/"))
                                continue;
                            materialIssues.Add(new MaterialIssue
                            {
                                kind = MaterialIssue.Kind.MismatchedMaterial,
                                renderer = e.renderer,
                                submeshIndex = i,
                                current = mats[i],
                                suggested = l0[i],
                                description = $"{e.renderer.name} (LOD{e.lodIndex}): material [{i}] differs from LOD0"
                            });
                        }
                    }
                }

                // Check extra material slots
                if (mats.Length > mesh.subMeshCount)
                {
                    materialIssues.Add(new MaterialIssue
                    {
                        kind = MaterialIssue.Kind.ExtraSlot,
                        renderer = e.renderer,
                        submeshIndex = mesh.subMeshCount,
                        current = null,
                        suggested = null,
                        description = $"{e.renderer.name}: {mats.Length} materials but only {mesh.subMeshCount} submesh(es)"
                    });
                }
            }

            UvtLog.Info($"Material scan: {materialIssues.Count} issue(s) found.");
        }

        void FixMaterials()
        {
            if (materialIssues == null || materialIssues.Count == 0) return;

            int undoGroup = Undo.GetCurrentGroup();
            var processed = new HashSet<Renderer>();

            // Build LOD0 material map
            var lod0Mats = new Dictionary<string, Material[]>();
            foreach (var e in ctx.MeshEntries)
            {
                if (e.lodIndex != 0 || e.renderer == null) continue;
                string key = e.meshGroupKey ?? e.renderer.name;
                if (!lod0Mats.ContainsKey(key))
                    lod0Mats[key] = e.renderer.sharedMaterials;
            }

            foreach (var issue in materialIssues)
            {
                var r = issue.renderer;
                if (r == null) continue;

                Undo.RecordObject(r, "Cleanup Materials");
                var mats = r.sharedMaterials;

                switch (issue.kind)
                {
                    case MaterialIssue.Kind.HiddenShader:
                    case MaterialIssue.Kind.MismatchedMaterial:
                        if (issue.suggested != null && issue.submeshIndex < mats.Length)
                        {
                            mats[issue.submeshIndex] = issue.suggested;
                            r.sharedMaterials = mats;
                            UvtLog.Info($"Fixed material [{issue.submeshIndex}] on {r.name}");
                        }
                        else
                        {
                            UvtLog.Warn($"No LOD0 material available for {r.name} slot [{issue.submeshIndex}]");
                        }
                        break;

                    case MaterialIssue.Kind.ExtraSlot:
                        if (!processed.Contains(r))
                        {
                            var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                            if (mesh != null)
                            {
                                var trimmed = new Material[mesh.subMeshCount];
                                Array.Copy(mats, trimmed, Mathf.Min(mats.Length, trimmed.Length));
                                r.sharedMaterials = trimmed;
                                UvtLog.Info($"Trimmed materials on {r.name}: {mats.Length} → {trimmed.Length}");
                            }
                            processed.Add(r);
                        }
                        break;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            materialIssues = null; // force re-scan
            requestRepaint?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════════
        // Section 2: Clean Colliders
        // ═══════════════════════════════════════════════════════════════

        void DrawCollidersSection()
        {
            EditorGUILayout.Space(8);
            string label = colliderIssues != null
                ? $"Clean Colliders ({colliderIssues.Count} issue{(colliderIssues.Count == 1 ? "" : "s")})"
                : "Clean Colliders";
            foldColliders = EditorGUILayout.Foldout(foldColliders, label, true);
            if (!foldColliders) return;

            EditorGUI.indentLevel++;

            DrawScanFixButtons(ScanColliders, FixColliders, colliderIssues);

            if (colliderIssues != null)
            {
                if (colliderIssues.Count == 0)
                    EditorGUILayout.HelpBox("No collider issues found.", MessageType.Info);
                else
                    foreach (var issue in colliderIssues)
                        EditorGUILayout.HelpBox(issue.description, MessageType.Warning);
            }

            EditorGUI.indentLevel--;
        }

        void ScanColliders()
        {
            colliderIssues = new List<ColliderIssue>();
            if (ctx.LodGroup == null) return;

            var colObjects = FindCollisionObjects(ctx.LodGroup.transform);

            // Check extra attributes
            foreach (var go in colObjects)
            {
                var mf = go.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;

                // Also check MeshCollider
                if (mesh == null)
                {
                    var mc = go.GetComponent<MeshCollider>();
                    mesh = mc != null ? mc.sharedMesh : null;
                }

                if (mesh == null) continue;

                bool hasNormals  = mesh.normals != null && mesh.normals.Length > 0;
                bool hasTangents = mesh.tangents != null && mesh.tangents.Length > 0;
                bool hasColors   = mesh.colors != null && mesh.colors.Length > 0;
                bool hasUvs      = mesh.uv != null && mesh.uv.Length > 0;

                if (hasNormals || hasTangents || hasColors || hasUvs)
                {
                    var extras = new List<string>();
                    if (hasNormals) extras.Add("normals");
                    if (hasTangents) extras.Add("tangents");
                    if (hasColors) extras.Add("colors");
                    if (hasUvs) extras.Add("UVs");

                    colliderIssues.Add(new ColliderIssue
                    {
                        kind = ColliderIssue.Kind.ExtraAttributes,
                        gameObject = go,
                        mesh = mesh,
                        description = $"{go.name}: has {string.Join(", ", extras)} (can strip)"
                    });
                }
            }

            // Check duplicates
            for (int i = 0; i < colObjects.Count; i++)
            {
                var mfA = colObjects[i].GetComponent<MeshFilter>();
                var meshA = mfA != null ? mfA.sharedMesh : null;
                if (meshA == null) continue;

                for (int j = i + 1; j < colObjects.Count; j++)
                {
                    var mfB = colObjects[j].GetComponent<MeshFilter>();
                    var meshB = mfB != null ? mfB.sharedMesh : null;
                    if (meshB == null) continue;

                    if (AreMeshesDuplicate(meshA, meshB))
                    {
                        colliderIssues.Add(new ColliderIssue
                        {
                            kind = ColliderIssue.Kind.Duplicate,
                            gameObject = colObjects[j],
                            mesh = meshB,
                            description = $"{colObjects[j].name}: duplicate of {colObjects[i].name}"
                        });
                    }
                }
            }

            UvtLog.Info($"Collider scan: {colliderIssues.Count} issue(s) found.");
        }

        void FixColliders()
        {
            if (colliderIssues == null || colliderIssues.Count == 0) return;

            int undoGroup = Undo.GetCurrentGroup();

            foreach (var issue in colliderIssues)
            {
                switch (issue.kind)
                {
                    case ColliderIssue.Kind.ExtraAttributes:
                        if (issue.mesh != null)
                        {
                            Undo.RecordObject(issue.mesh, "Strip Collider Attributes");
                            var pos = issue.mesh.vertices;
                            var tris = issue.mesh.triangles;
                            issue.mesh.Clear();
                            issue.mesh.SetVertices(pos);
                            issue.mesh.SetTriangles(tris, 0);
                            issue.mesh.RecalculateBounds();
                            UvtLog.Info($"Stripped extra attributes from {issue.gameObject.name}");
                        }
                        break;

                    case ColliderIssue.Kind.Duplicate:
                        if (issue.gameObject != null)
                        {
                            UvtLog.Info($"Removed duplicate collider: {issue.gameObject.name}");
                            Undo.DestroyObjectImmediate(issue.gameObject);
                        }
                        break;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            colliderIssues = null;
            requestRepaint?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════════
        // Section 3: Scene Cleanup
        // ═══════════════════════════════════════════════════════════════

        void DrawSceneSection()
        {
            EditorGUILayout.Space(8);
            string label = sceneIssues != null
                ? $"Scene Cleanup ({sceneIssues.Count} issue{(sceneIssues.Count == 1 ? "" : "s")})"
                : "Scene Cleanup";
            foldScene = EditorGUILayout.Foldout(foldScene, label, true);
            if (!foldScene) return;

            EditorGUI.indentLevel++;

            DrawScanFixButtons(ScanScene, FixScene, sceneIssues);

            if (sceneIssues != null)
            {
                if (sceneIssues.Count == 0)
                    EditorGUILayout.HelpBox("No scene issues found.", MessageType.Info);
                else
                    foreach (var issue in sceneIssues)
                    {
                        var msgType = issue.kind == SceneIssue.Kind.OrphanedLod
                            ? MessageType.Warning : MessageType.Info;
                        EditorGUILayout.HelpBox(issue.description, msgType);
                    }
            }

            EditorGUI.indentLevel--;
        }

        void ScanScene()
        {
            sceneIssues = new List<SceneIssue>();
            if (ctx.LodGroup == null) return;

            var root = ctx.LodGroup.transform;

            // Build set of GameObjects referenced by LODGroup
            var referenced = new HashSet<GameObject>();
            var lods = ctx.LodGroup.GetLODs();
            foreach (var lod in lods)
            {
                if (lod.renderers == null) continue;
                foreach (var r in lod.renderers)
                    if (r != null) referenced.Add(r.gameObject);
            }

            // Gather collision object names to exclude from orphan check
            var colObjects = FindCollisionObjects(root);
            var colSet = new HashSet<GameObject>(colObjects);

            // Try to get FBX prefab children for comparison
            HashSet<string> prefabChildNames = null;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(r.gameObject);
                if (prefabSource != null && prefabSource.transform.parent != null)
                {
                    var prefabRoot = prefabSource.transform.root;
                    prefabChildNames = new HashSet<string>();
                    for (int i = 0; i < prefabRoot.childCount; i++)
                        prefabChildNames.Add(prefabRoot.GetChild(i).name);
                    break;
                }
            }

            // Check for orphaned LOD objects
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (colSet.Contains(child.gameObject)) continue;
                if (referenced.Contains(child.gameObject)) continue;

                // Must have a Renderer to be considered orphaned LOD
                if (child.GetComponent<Renderer>() == null) continue;

                // Check if name has LOD suffix
                bool hasLodSuffix = System.Text.RegularExpressions.Regex.IsMatch(
                    child.name, @"_LOD\d+$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // If prefab available, check if this child doesn't exist in prefab
                bool orphanedFromPrefab = prefabChildNames != null && !prefabChildNames.Contains(child.name);

                if (hasLodSuffix || orphanedFromPrefab)
                {
                    sceneIssues.Add(new SceneIssue
                    {
                        kind = SceneIssue.Kind.OrphanedLod,
                        gameObject = child.gameObject,
                        description = $"{child.name}: not referenced by LODGroup" +
                            (orphanedFromPrefab ? " (not in FBX prefab)" : "")
                    });
                }
            }

            // Check if LODGroup can be rebuilt from hierarchy naming
            var lodChildren = new Dictionary<int, List<Renderer>>();
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
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

            if (lodChildren.Count > 0)
            {
                // Compare with current LODGroup
                bool mismatch = false;
                if (lodChildren.Count != lods.Length)
                    mismatch = true;
                else
                {
                    foreach (var kvp in lodChildren)
                    {
                        if (kvp.Key >= lods.Length) { mismatch = true; break; }
                        var lodRenderers = lods[kvp.Key].renderers;
                        if (lodRenderers == null) { mismatch = true; break; }
                        var currentSet = new HashSet<Renderer>(lodRenderers.Where(r => r != null));
                        foreach (var r in kvp.Value)
                        {
                            if (!currentSet.Contains(r)) { mismatch = true; break; }
                        }
                        if (mismatch) break;
                    }
                }

                if (mismatch)
                {
                    sceneIssues.Add(new SceneIssue
                    {
                        kind = SceneIssue.Kind.LodGroupMismatch,
                        gameObject = ctx.LodGroup.gameObject,
                        description = $"LODGroup can be rebuilt from hierarchy ({lodChildren.Count} LOD level(s) detected)"
                    });
                }
            }

            UvtLog.Info($"Scene scan: {sceneIssues.Count} issue(s) found.");
        }

        void FixScene()
        {
            if (sceneIssues == null || sceneIssues.Count == 0) return;

            int undoGroup = Undo.GetCurrentGroup();
            bool needsRefresh = false;

            // First pass: remove orphans
            foreach (var issue in sceneIssues)
            {
                if (issue.kind != SceneIssue.Kind.OrphanedLod) continue;
                if (issue.gameObject == null) continue;

                UvtLog.Info($"Removed orphaned object: {issue.gameObject.name}");
                Undo.DestroyObjectImmediate(issue.gameObject);
                needsRefresh = true;
            }

            // Second pass: rebuild LODGroup
            foreach (var issue in sceneIssues)
            {
                if (issue.kind != SceneIssue.Kind.LodGroupMismatch) continue;
                if (ctx.LodGroup == null) continue;

                RebuildLodGroupFromHierarchy();
                needsRefresh = true;
                break; // only one rebuild needed
            }

            Undo.CollapseUndoOperations(undoGroup);
            sceneIssues = null;

            if (needsRefresh)
            {
                ctx.Refresh(ctx.LodGroup);
                requestRepaint?.Invoke();
            }
        }

        void RebuildLodGroupFromHierarchy()
        {
            var root = ctx.LodGroup.transform;
            var colSet = new HashSet<GameObject>(FindCollisionObjects(root));
            var lodChildren = new SortedDictionary<int, List<Renderer>>();

            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
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

            int maxLod = lodChildren.Keys.Max() + 1;
            var newLods = new LOD[maxLod];
            for (int i = 0; i < maxLod; i++)
            {
                Renderer[] renderers;
                if (lodChildren.TryGetValue(i, out var list))
                    renderers = list.ToArray();
                else
                    renderers = new Renderer[0];

                newLods[i] = new LOD(Mathf.Pow(0.5f, i + 1), renderers);
            }

            ctx.LodGroup.SetLODs(newLods);
            ctx.LodGroup.RecalculateBounds();
            UvtLog.Info($"Rebuilt LODGroup with {maxLod} LOD level(s).");
        }

        // ═══════════════════════════════════════════════════════════════
        // Section 4: Mesh
        // ═══════════════════════════════════════════════════════════════

        void DrawMeshSection()
        {
            EditorGUILayout.Space(8);
            foldMesh = EditorGUILayout.Foldout(foldMesh, "Mesh", true);
            if (!foldMesh) return;

            EditorGUI.indentLevel++;

            // Scan button
            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.6f, .75f, .9f);
            if (GUILayout.Button("Scan", GUILayout.Height(28)))
                ScanMesh();
            GUI.backgroundColor = bgc;

            if (meshReport != null)
            {
                // Per-LOD summary
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Vertex / Triangle Summary", EditorStyles.boldLabel);
                foreach (var lod in meshReport.lods)
                {
                    EditorGUILayout.LabelField(
                        $"LOD{lod.lodIndex}: {lod.totalVerts:N0} verts, {lod.totalTris:N0} tris");
                    EditorGUI.indentLevel++;
                    foreach (var m in lod.meshes)
                        EditorGUILayout.LabelField($"{m.name}: {m.verts:N0} v, {m.tris:N0} t");
                    EditorGUI.indentLevel--;
                }

                // Weld candidates
                if (meshReport.weldCandidates.Count > 0)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.HelpBox(
                        $"{meshReport.weldCandidates.Count} mesh(es) with false seams (weld candidates).",
                        MessageType.Info);

                    GUI.backgroundColor = new Color(.4f, .8f, .4f);
                    if (GUILayout.Button("Batch Weld", GUILayout.Height(28)))
                        FixMeshWeld();
                    GUI.backgroundColor = bgc;
                }

                // Empty UV channels
                if (meshReport.emptyUvEntries.Count > 0)
                {
                    int totalEmpty = meshReport.emptyUvEntries.Sum(e => e.channels.Count);
                    EditorGUILayout.Space(4);
                    EditorGUILayout.HelpBox(
                        $"{totalEmpty} empty UV channel(s) across {meshReport.emptyUvEntries.Count} mesh(es).",
                        MessageType.Info);

                    GUI.backgroundColor = new Color(.4f, .8f, .4f);
                    if (GUILayout.Button("Strip Empty UV Channels", GUILayout.Height(28)))
                        FixMeshStripUvs();
                    GUI.backgroundColor = bgc;
                }

                if (meshReport.weldCandidates.Count == 0 && meshReport.emptyUvEntries.Count == 0)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.HelpBox("No mesh issues found.", MessageType.Info);
                }
            }

            EditorGUI.indentLevel--;
        }

        void ScanMesh()
        {
            meshReport = new MeshReport();
            var tmpUv = new List<Vector2>();

            for (int li = 0; li < ctx.LodCount; li++)
            {
                var entries = ctx.ForLod(li);
                var lodInfo = new LodInfo
                {
                    lodIndex = li,
                    meshes = new List<(string, int, int)>()
                };

                foreach (var e in entries)
                {
                    var mesh = e.originalMesh ?? e.fbxMesh;
                    if (mesh == null) continue;

                    int verts = mesh.vertexCount;
                    int tris = GetTriangleCount(mesh);
                    lodInfo.totalVerts += verts;
                    lodInfo.totalTris += tris;
                    lodInfo.meshes.Add((mesh.name, verts, tris));

                    // Weld check
                    var report = Uv0Analyzer.Analyze(mesh);
                    if (report.HasIssues)
                        meshReport.weldCandidates.Add(e);

                    // Empty UV channels
                    var emptyChannels = new List<int>();
                    for (int ch = 0; ch < 8; ch++)
                    {
                        tmpUv.Clear();
                        mesh.GetUVs(ch, tmpUv);
                        if (tmpUv.Count == 0) continue; // channel not set at all, skip

                        bool allZero = true;
                        for (int vi = 0; vi < tmpUv.Count; vi++)
                        {
                            if (tmpUv[vi].sqrMagnitude > 0f) { allZero = false; break; }
                        }
                        if (allZero)
                            emptyChannels.Add(ch);
                    }
                    if (emptyChannels.Count > 0)
                        meshReport.emptyUvEntries.Add((e, emptyChannels));
                }

                meshReport.lods.Add(lodInfo);
            }

            UvtLog.Info($"Mesh scan: {meshReport.weldCandidates.Count} weld candidate(s), " +
                        $"{meshReport.emptyUvEntries.Count} mesh(es) with empty UV channels.");
        }

        void FixMeshWeld()
        {
            if (meshReport == null || meshReport.weldCandidates.Count == 0) return;

            int undoGroup = Undo.GetCurrentGroup();
            int welded = 0;

            foreach (var e in meshReport.weldCandidates)
            {
                var mesh = e.originalMesh ?? e.fbxMesh;
                if (mesh == null) continue;

                // Clone if asset-backed
                if (AssetDatabase.Contains(mesh))
                {
                    var clone = UnityEngine.Object.Instantiate(mesh);
                    clone.name = mesh.name;
                    Undo.RecordObject(e.meshFilter, "Batch Weld");
                    e.meshFilter.sharedMesh = clone;
                    e.originalMesh = clone;
                    mesh = clone;
                }

                Undo.RecordObject(mesh, "Batch Weld");
                if (Uv0Analyzer.WeldInPlace(mesh))
                {
                    MeshOptimizer.Optimize(mesh);
                    welded++;
                    UvtLog.Info($"Welded: {mesh.name}");
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            UvtLog.Info($"Batch weld complete: {welded} mesh(es) welded.");
            meshReport = null;
            requestRepaint?.Invoke();
        }

        void FixMeshStripUvs()
        {
            if (meshReport == null || meshReport.emptyUvEntries.Count == 0) return;

            int undoGroup = Undo.GetCurrentGroup();
            int stripped = 0;

            foreach (var (entry, channels) in meshReport.emptyUvEntries)
            {
                var mesh = entry.originalMesh ?? entry.fbxMesh;
                if (mesh == null) continue;

                // Clone if asset-backed
                if (AssetDatabase.Contains(mesh))
                {
                    var clone = UnityEngine.Object.Instantiate(mesh);
                    clone.name = mesh.name;
                    Undo.RecordObject(entry.meshFilter, "Strip Empty UVs");
                    entry.meshFilter.sharedMesh = clone;
                    entry.originalMesh = clone;
                    mesh = clone;
                }

                Undo.RecordObject(mesh, "Strip Empty UVs");
                foreach (int ch in channels)
                    mesh.SetUVs(ch, (List<Vector2>)null);

                stripped += channels.Count;
                UvtLog.Info($"Stripped {channels.Count} empty UV channel(s) from {mesh.name}");
            }

            Undo.CollapseUndoOperations(undoGroup);
            UvtLog.Info($"Stripped {stripped} empty UV channel(s) total.");
            meshReport = null;
            requestRepaint?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════════════

        void DrawScanFixButtons(Action scan, Action fix, System.Collections.IList issues)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            var bgc = GUI.backgroundColor;

            GUI.backgroundColor = new Color(.6f, .75f, .9f);
            if (GUILayout.Button("Scan", GUILayout.Height(24)))
                scan();

            GUI.backgroundColor = new Color(.4f, .8f, .4f);
            GUI.enabled = issues != null && issues.Count > 0;
            if (GUILayout.Button("Fix", GUILayout.Height(24)))
                fix();
            GUI.enabled = true;

            GUI.backgroundColor = bgc;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        static List<GameObject> FindCollisionObjects(Transform root)
        {
            var result = new List<GameObject>();
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.name.Contains("_COL"))
                {
                    result.Add(child.gameObject);
                    for (int j = 0; j < child.childCount; j++)
                    {
                        if (child.GetChild(j).name.Contains("_COL"))
                            result.Add(child.GetChild(j).gameObject);
                    }
                }
            }
            return result;
        }

        static int GetTriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            long count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                count += mesh.GetIndexCount(i);
            return (int)(count / 3L);
        }

        static bool AreMeshesDuplicate(Mesh a, Mesh b)
        {
            if (a == b) return true;
            if (a.vertexCount != b.vertexCount) return false;
            if (a.triangles.Length != b.triangles.Length) return false;

            var va = a.vertices;
            var vb = b.vertices;

            // Quick check: first and last vertices
            if (va.Length == 0) return true;
            float eps = 1e-5f;
            if ((va[0] - vb[0]).sqrMagnitude > eps) return false;
            if ((va[va.Length - 1] - vb[vb.Length - 1]).sqrMagnitude > eps) return false;

            // Full vertex comparison
            for (int i = 0; i < va.Length; i++)
            {
                if ((va[i] - vb[i]).sqrMagnitude > eps)
                    return false;
            }
            return true;
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
