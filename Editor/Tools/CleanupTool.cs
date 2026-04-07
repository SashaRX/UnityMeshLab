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
        bool foldMaterials = true, foldColliders, foldScene, foldMesh, foldAttributes, foldSplitMerge;

        // ── Ensure-attribute toggles ──
        bool ensureNormals, ensureTangents, ensureColors;
        bool[] ensureUv = new bool[8];

        // ── Split/Merge state ──
        List<SplitCandidate> splitCandidates;
        List<MergeGroup> mergeCandidates;

        // ── Scan results (null = not scanned yet, empty = scanned, no issues) ──
        List<MaterialIssue> materialIssues;
        List<ColliderIssue> colliderIssues;
        List<SceneIssue> sceneIssues;
        MeshReport meshReport;

        // ── Inner types ──

        struct MaterialIssue
        {
            public enum Kind { HiddenShader, MismatchedMaterial, ExtraSlot, ImporterRemap }
            public Kind kind;
            public Renderer renderer;
            public int submeshIndex;
            public Material current;
            public Material suggested;
            public string description;
            // For ImporterRemap kind
            public string fbxPath;
            public string remapSourceName;
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

        class MeshReport
        {
            public List<LodInfo> lods = new List<LodInfo>();
            public List<MeshEntry> weldCandidates = new List<MeshEntry>();
            public List<EmptyUvEntry> emptyUvEntries = new List<EmptyUvEntry>();
            public List<MeshAttributeInfo> attributes = new List<MeshAttributeInfo>();
        }

        struct EmptyUvEntry
        {
            public MeshEntry entry;
            public List<int> channels;
            public bool hasZeroColors;
        }

        struct MeshAttributeInfo
        {
            public MeshEntry entry;
            public string meshName;
            public int vertexCount;
            public bool hasNormals, hasTangents, hasColors;
            public bool[] hasUv; // [8]
        }

        struct MeshStats
        {
            public string name;
            public int verts;
            public int tris;
        }

        struct LodInfo
        {
            public int lodIndex;
            public int totalVerts, totalTris;
            public int deltaVerts, deltaTris;
            public List<MeshStats> meshes;
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
            splitCandidates = null;
            mergeCandidates = null;
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
            DrawSplitMergeSection();
            DrawAttributesSection();
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

            // Scan FBX importer material remaps
            var fbxPaths = new HashSet<string>();
            foreach (var e in ctx.MeshEntries)
            {
                Mesh m = e.fbxMesh ?? e.originalMesh;
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    fbxPaths.Add(p);
            }

            // Collect correct materials from LOD0 scene renderers
            var correctMats = new Dictionary<string, Material>();
            foreach (var e in ctx.MeshEntries)
            {
                if (e.lodIndex != 0 || e.renderer == null) continue;
                foreach (var mat in e.renderer.sharedMaterials)
                {
                    if (mat != null && !mat.shader.name.StartsWith("Hidden/LightmapUvTool/"))
                        correctMats[mat.name] = mat;
                }
            }

            foreach (string fbxPath in fbxPaths)
            {
                var imp = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                if (imp == null) continue;
                string fbxName = System.IO.Path.GetFileName(fbxPath);

                // Check external object map (user remaps)
                var map = imp.GetExternalObjectMap();
                foreach (var kvp in map)
                {
                    if (kvp.Key.type != typeof(Material)) continue;
                    string sourceName = kvp.Key.name;
                    if (string.IsNullOrEmpty(sourceName)) continue;

                    // Check SOURCE name (material name baked into FBX)
                    bool sourceIsHidden = sourceName.StartsWith("Hidden_LightmapUvTool")
                                       || sourceName.StartsWith("Hidden/LightmapUvTool");
                    bool sourceIsDefault = sourceName == "Lit" || sourceName == "Standard"
                                        || sourceName == "No Name";

                    if (sourceIsHidden || sourceIsDefault)
                    {
                        var mat = kvp.Value as Material;
                        materialIssues.Add(new MaterialIssue
                        {
                            kind = MaterialIssue.Kind.ImporterRemap,
                            fbxPath = fbxPath,
                            remapSourceName = sourceName,
                            current = mat,
                            suggested = mat, // remap is already correct, FBX needs re-export
                            description = $"{fbxName}: FBX contains bad material \"{sourceName}\"" +
                                (mat != null ? $" (remapped to \"{mat.name}\")" : "") +
                                " — re-export FBX to fix"
                        });
                    }
                }

                // Also check FBX embedded materials (not yet remapped)
                var subAssets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
                foreach (var asset in subAssets)
                {
                    var mat = asset as Material;
                    if (mat == null) continue;

                    bool isHidden = mat.name.StartsWith("Hidden_LightmapUvTool")
                                 || mat.shader.name.StartsWith("Hidden/LightmapUvTool/");
                    bool isDefault = mat.name == "Lit" || mat.name == "No Name"
                                  || (mat.name == "Standard" && mat.shader.name == "Standard");

                    // Skip if already covered by remap scan
                    bool alreadyCovered = false;
                    foreach (var kvp in map)
                    {
                        if (kvp.Key.type == typeof(Material) && kvp.Key.name == mat.name)
                        { alreadyCovered = true; break; }
                    }
                    if (alreadyCovered) continue;

                    if (isHidden || isDefault)
                    {
                        Material suggested = null;
                        if (correctMats.TryGetValue(mat.name, out var found))
                            suggested = found;

                        materialIssues.Add(new MaterialIssue
                        {
                            kind = MaterialIssue.Kind.ImporterRemap,
                            fbxPath = fbxPath,
                            remapSourceName = mat.name,
                            current = mat,
                            suggested = suggested,
                            description = $"{fbxName}: embedded material \"{mat.name}\" has bad shader" +
                                (suggested != null ? $" → suggest \"{suggested.name}\"" : " — re-export FBX to fix")
                        });
                    }
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

            // Fix scene renderer issues
            foreach (var issue in materialIssues)
            {
                if (issue.kind == MaterialIssue.Kind.ImporterRemap) continue;

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

            // Fix FBX importer remap issues
            var reimportPaths = new HashSet<string>();
            foreach (var issue in materialIssues)
            {
                if (issue.kind != MaterialIssue.Kind.ImporterRemap) continue;
                if (string.IsNullOrEmpty(issue.fbxPath) || issue.suggested == null) continue;

                var imp = AssetImporter.GetAtPath(issue.fbxPath) as ModelImporter;
                if (imp == null) continue;

                var sourceId = new AssetImporter.SourceAssetIdentifier(
                    typeof(Material), issue.remapSourceName);
                imp.AddRemap(sourceId, issue.suggested);
                UvtLog.Info($"Fixed FBX remap \"{issue.remapSourceName}\" → \"{issue.suggested.name}\" in {System.IO.Path.GetFileName(issue.fbxPath)}");
                reimportPaths.Add(issue.fbxPath);
            }

            Undo.CollapseUndoOperations(undoGroup);

            // Reimport FBX files with updated remaps
            foreach (string path in reimportPaths)
            {
                UvtLog.Info($"Reimporting {System.IO.Path.GetFileName(path)}...");
                AssetDatabase.WriteImportSettingsIfDirty(path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }

            materialIssues = null;
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

            // Track meshes already checked (avoid duplicates)
            var checkedMeshes = new HashSet<int>();

            // 1. Scan scene children with _COL in name
            var colObjects = FindCollisionObjects(ctx.LodGroup.transform);
            foreach (var go in colObjects)
            {
                var mf = go.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null)
                {
                    var mc = go.GetComponent<MeshCollider>();
                    mesh = mc != null ? mc.sharedMesh : null;
                }
                if (mesh == null) continue;
                checkedMeshes.Add(mesh.GetInstanceID());
                CheckColliderMesh(mesh, go.name, go);
            }

            // 2. Scan FBX sub-assets for _COL meshes not in scene
            var fbxPaths = new HashSet<string>();
            foreach (var e in ctx.MeshEntries)
            {
                Mesh m = e.fbxMesh ?? e.originalMesh;
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    fbxPaths.Add(p);
            }

            foreach (string fbxPath in fbxPaths)
            {
                var subAssets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
                foreach (var asset in subAssets)
                {
                    var mesh = asset as Mesh;
                    if (mesh == null) continue;
                    if (checkedMeshes.Contains(mesh.GetInstanceID())) continue;
                    if (!mesh.name.Contains("_COL") && !mesh.name.Contains("_col")) continue;

                    checkedMeshes.Add(mesh.GetInstanceID());
                    CheckColliderMesh(mesh, $"{mesh.name} (FBX: {System.IO.Path.GetFileName(fbxPath)})", null);
                }
            }

            // Check duplicates (scene objects only)
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

        void CheckColliderMesh(Mesh mesh, string displayName, GameObject go)
        {
            bool hasNormals  = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal);
            bool hasTangents = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent);
            bool hasColors   = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color);
            bool hasUvs      = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0);

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
                    description = $"{displayName}: has {string.Join(", ", extras)} (can strip)"
                });
            }
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
                        if (issue.mesh == null) break;

                        if (issue.mesh.isReadable)
                        {
                            // Mesh is readable — strip in-place
                            Undo.RecordObject(issue.mesh, "Strip Collider Attributes");
                            var pos = issue.mesh.vertices;
                            var tris = issue.mesh.triangles;
                            issue.mesh.Clear();
                            issue.mesh.SetVertices(pos);
                            issue.mesh.SetTriangles(tris, 0);
                            issue.mesh.RecalculateBounds();
                            UvtLog.Info($"Stripped extra attributes from {issue.gameObject.name} (in-place)");
                        }
                        else
                        {
                            // Mesh is not readable — need to clone with read enabled,
                            // strip, then assign to MeshFilter/MeshCollider
                            var mf = issue.gameObject.GetComponent<MeshFilter>();
                            var mc = issue.gameObject.GetComponent<MeshCollider>();

                            // Try to enable Read/Write on the FBX source and reimport
                            string assetPath = AssetDatabase.GetAssetPath(issue.mesh);
                            if (!string.IsNullOrEmpty(assetPath))
                            {
                                var imp = AssetImporter.GetAtPath(assetPath) as ModelImporter;
                                if (imp != null)
                                {
                                    bool wasReadable = imp.isReadable;
                                    if (!wasReadable)
                                    {
                                        imp.isReadable = true;
                                        imp.SaveAndReimport();
                                    }

                                    // Now mesh should be readable — reload and strip
                                    Mesh freshMesh = mf != null ? mf.sharedMesh : (mc != null ? mc.sharedMesh : null);
                                    if (freshMesh != null && freshMesh.isReadable)
                                    {
                                        var clone = UnityEngine.Object.Instantiate(freshMesh);
                                        clone.name = freshMesh.name;
                                        var cPos = clone.vertices;
                                        var cTris = clone.triangles;
                                        clone.Clear();
                                        clone.SetVertices(cPos);
                                        clone.SetTriangles(cTris, 0);
                                        clone.RecalculateBounds();

                                        if (mf != null)
                                        {
                                            Undo.RecordObject(mf, "Strip Collider Attributes");
                                            mf.sharedMesh = clone;
                                        }
                                        if (mc != null)
                                        {
                                            Undo.RecordObject(mc, "Strip Collider Attributes");
                                            mc.sharedMesh = clone;
                                        }
                                        UvtLog.Info($"Stripped extra attributes from {issue.gameObject.name} (cloned)");
                                    }

                                    // Restore Read/Write setting
                                    if (!wasReadable)
                                    {
                                        imp.isReadable = false;
                                        imp.SaveAndReimport();
                                    }
                                }
                            }
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
                    string delta = lod.lodIndex > 0 && meshReport.lods.Count > 1
                        ? $" (Δ {lod.deltaVerts:+#,##0;-#,##0;0} v, {lod.deltaTris:+#,##0;-#,##0;0} t)"
                        : "";
                    EditorGUILayout.LabelField(
                        $"LOD{lod.lodIndex}: {lod.totalVerts:N0} verts, {lod.totalTris:N0} tris{delta}");
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
                    {
                        FixMeshWeld();
                        EditorGUI.indentLevel--;
                        return; // meshReport nulled, stop drawing
                    }
                    GUI.backgroundColor = bgc;
                }

                if (meshReport == null) { EditorGUI.indentLevel--; return; }

                // Empty UV channels
                if (meshReport.emptyUvEntries.Count > 0)
                {
                    int totalEmptyUvs = meshReport.emptyUvEntries.Sum(e => e.channels.Count);
                    int totalZeroColors = meshReport.emptyUvEntries.Count(e => e.hasZeroColors);
                    EditorGUILayout.Space(4);

                    var parts = new List<string>();
                    if (totalEmptyUvs > 0)
                        parts.Add($"{totalEmptyUvs} empty UV channel(s)");
                    if (totalZeroColors > 0)
                        parts.Add($"{totalZeroColors} mesh(es) with zero vertex colors");
                    EditorGUILayout.HelpBox(
                        string.Join(", ", parts) + $" across {meshReport.emptyUvEntries.Count} mesh(es).",
                        MessageType.Info);

                    GUI.backgroundColor = new Color(.4f, .8f, .4f);
                    if (GUILayout.Button("Strip Empty Channels", GUILayout.Height(28)))
                    {
                        FixMeshStripUvs();
                        EditorGUI.indentLevel--;
                        return;
                    }
                    GUI.backgroundColor = bgc;
                }

                if (meshReport == null) { EditorGUI.indentLevel--; return; }

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
                    meshes = new List<MeshStats>()
                };

                foreach (var e in entries)
                {
                    var mesh = e.originalMesh ?? e.fbxMesh;
                    if (mesh == null) continue;

                    int verts = mesh.vertexCount;
                    int tris = GetTriangleCount(mesh);
                    lodInfo.totalVerts += verts;
                    lodInfo.totalTris += tris;
                    lodInfo.meshes.Add(new MeshStats
                    {
                        name = mesh.name,
                        verts = verts,
                        tris = tris
                    });

                    // Attribute info (HasVertexAttribute works even without isReadable)
                    var attrInfo = new MeshAttributeInfo
                    {
                        entry = e,
                        meshName = mesh.name,
                        vertexCount = verts,
                        hasNormals = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal),
                        hasTangents = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent),
                        hasColors = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color),
                        hasUv = new bool[8]
                    };
                    for (int ch = 0; ch < 8; ch++)
                        attrInfo.hasUv[ch] = mesh.HasVertexAttribute(
                            UnityEngine.Rendering.VertexAttribute.TexCoord0 + ch);
                    meshReport.attributes.Add(attrInfo);

                    // Skip vertex-data operations for non-readable meshes
                    if (!mesh.isReadable) continue;

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
                    // Check for zero vertex colors
                    bool hasZeroColors = false;
                    var colors = mesh.colors;
                    if (colors != null && colors.Length > 0)
                    {
                        bool allZero = true;
                        for (int vi = 0; vi < colors.Length; vi++)
                        {
                            var c = colors[vi];
                            if (c.r > 0f || c.g > 0f || c.b > 0f || c.a > 0f)
                            { allZero = false; break; }
                        }
                        hasZeroColors = allZero;
                    }

                    if (emptyChannels.Count > 0 || hasZeroColors)
                    {
                        meshReport.emptyUvEntries.Add(new EmptyUvEntry
                        {
                            entry = e,
                            channels = emptyChannels,
                            hasZeroColors = hasZeroColors
                        });
                    }
                }

                meshReport.lods.Add(lodInfo);
            }

            // Compute delta from LOD0
            if (meshReport.lods.Count > 1)
            {
                var lod0 = meshReport.lods[0];
                for (int i = 1; i < meshReport.lods.Count; i++)
                {
                    var l = meshReport.lods[i];
                    l.deltaVerts = l.totalVerts - lod0.totalVerts;
                    l.deltaTris = l.totalTris - lod0.totalTris;
                    meshReport.lods[i] = l;
                }
            }

            int colorCount = meshReport.emptyUvEntries.Count(e => e.hasZeroColors);
            UvtLog.Info($"Mesh scan: {meshReport.weldCandidates.Count} weld candidate(s), " +
                        $"{meshReport.emptyUvEntries.Count} mesh(es) with empty channels" +
                        (colorCount > 0 ? $" ({colorCount} with zero vertex colors)" : "") + ".");

            // Initialize desired-state toggles from current mesh attributes (union),
            // but only on first scan (all toggles false = never initialized)
            bool anyToggle = ensureNormals || ensureTangents || ensureColors;
            if (!anyToggle) for (int ch = 0; ch < 8; ch++) if (ensureUv[ch]) { anyToggle = true; break; }
            if (!anyToggle && meshReport.attributes.Count > 0)
            {
                foreach (var attr in meshReport.attributes)
                {
                    if (attr.hasNormals) ensureNormals = true;
                    if (attr.hasTangents) ensureTangents = true;
                    if (attr.hasColors) ensureColors = true;
                    for (int ch = 0; ch < 8; ch++)
                        if (attr.hasUv[ch]) ensureUv[ch] = true;
                }
            }
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
                if (!mesh.isReadable)
                {
                    UvtLog.Warn($"Skipped {mesh.name}: mesh is not readable (enable Read/Write in import settings)");
                    continue;
                }

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

            foreach (var uvEntry in meshReport.emptyUvEntries)
            {
                var entry = uvEntry.entry;
                var channels = uvEntry.channels;
                var mesh = entry.originalMesh ?? entry.fbxMesh;
                if (mesh == null || !mesh.isReadable) continue;

                // Clone if asset-backed
                if (AssetDatabase.Contains(mesh))
                {
                    var clone = UnityEngine.Object.Instantiate(mesh);
                    clone.name = mesh.name;
                    Undo.RecordObject(entry.meshFilter, "Strip Empty Channels");
                    entry.meshFilter.sharedMesh = clone;
                    entry.originalMesh = clone;
                    mesh = clone;
                }

                Undo.RecordObject(mesh, "Strip Empty Channels");
                foreach (int ch in channels)
                    mesh.SetUVs(ch, (List<Vector2>)null);
                if (uvEntry.hasZeroColors)
                    mesh.colors = null;

                stripped += channels.Count + (uvEntry.hasZeroColors ? 1 : 0);
                UvtLog.Info($"Stripped {channels.Count} empty UV channel(s)" +
                            (uvEntry.hasZeroColors ? " + zero vertex colors" : "") +
                            $" from {mesh.name}");
            }

            Undo.CollapseUndoOperations(undoGroup);
            UvtLog.Info($"Stripped {stripped} empty channel(s) total.");
            meshReport = null;
            requestRepaint?.Invoke();
        }

        void FixMeshSplitByMaterial()
        {
            if (splitCandidates == null || splitCandidates.Count == 0) return;

            int undoGroup = Undo.GetCurrentGroup();
            int split = 0;

            foreach (var sc in splitCandidates)
            {
                if (!sc.include) continue;
                var e = sc.entry;
                var srcMesh = e.originalMesh ?? e.fbxMesh;
                if (srcMesh == null || e.renderer == null || e.meshFilter == null) continue;

                var mats = e.renderer.sharedMaterials;
                int subCount = srcMesh.subMeshCount;
                if (subCount <= 1) continue;

                var parent = e.renderer.transform.parent;
                var srcTransform = e.renderer.transform;
                var newRenderers = new List<Renderer>();

                for (int s = 0; s < subCount; s++)
                {
                    // Extract submesh
                    var subTris = srcMesh.GetTriangles(s);
                    if (subTris.Length == 0) continue;

                    // Build vertex remap: old index → new compact index
                    var usedVerts = new HashSet<int>(subTris);
                    var oldToNew = new Dictionary<int, int>();
                    int newIdx = 0;
                    foreach (int vi in usedVerts.OrderBy(v => v))
                        oldToNew[vi] = newIdx++;
                    int newVertCount = newIdx;

                    // Extract vertex data
                    var srcPos = srcMesh.vertices;
                    var srcNorm = srcMesh.normals;
                    var srcTan = srcMesh.tangents;
                    var srcColors = srcMesh.colors;
                    var srcBw = srcMesh.boneWeights;

                    var newPos = new Vector3[newVertCount];
                    Vector3[] newNorm = srcNorm != null && srcNorm.Length > 0 ? new Vector3[newVertCount] : null;
                    Vector4[] newTan = srcTan != null && srcTan.Length > 0 ? new Vector4[newVertCount] : null;
                    Color[] newColors = srcColors != null && srcColors.Length > 0 ? new Color[newVertCount] : null;
                    BoneWeight[] newBw = srcBw != null && srcBw.Length > 0 ? new BoneWeight[newVertCount] : null;

                    foreach (var kvp in oldToNew)
                    {
                        int oi = kvp.Key, ni = kvp.Value;
                        newPos[ni] = srcPos[oi];
                        if (newNorm != null) newNorm[ni] = srcNorm[oi];
                        if (newTan != null) newTan[ni] = srcTan[oi];
                        if (newColors != null) newColors[ni] = srcColors[oi];
                        if (newBw != null) newBw[ni] = srcBw[oi];
                    }

                    // Remap triangle indices
                    var newTris = new int[subTris.Length];
                    for (int t = 0; t < subTris.Length; t++)
                        newTris[t] = oldToNew[subTris[t]];

                    // Build new mesh
                    var newMesh = new Mesh();
                    newMesh.name = $"{srcMesh.name}_sub{s}";
                    newMesh.SetVertices(newPos);
                    if (newNorm != null) newMesh.normals = newNorm;
                    if (newTan != null) newMesh.tangents = newTan;
                    if (newColors != null) newMesh.colors = newColors;
                    if (newBw != null) newMesh.boneWeights = newBw;

                    // Copy UV channels
                    var tmpUv2 = new List<Vector2>();
                    var tmpUv3 = new List<Vector3>();
                    var tmpUv4 = new List<Vector4>();
                    for (int ch = 0; ch < 8; ch++)
                    {
                        // Try Vector4 first (preserves dimension)
                        tmpUv4.Clear();
                        srcMesh.GetUVs(ch, tmpUv4);
                        if (tmpUv4.Count > 0)
                        {
                            var chData = new List<Vector4>(newVertCount);
                            for (int vi = 0; vi < newVertCount; vi++) chData.Add(default);
                            foreach (var kvp in oldToNew)
                                chData[kvp.Value] = tmpUv4[kvp.Key];
                            newMesh.SetUVs(ch, chData);
                            continue;
                        }
                    }

                    if (newVertCount > 65535)
                        newMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    newMesh.SetTriangles(newTris, 0);
                    newMesh.RecalculateBounds();

                    // Create new GameObject
                    string matName = s < mats.Length && mats[s] != null ? mats[s].name : $"mat{s}";
                    // Preserve trailing LOD suffix for ExtractGroupKey compatibility
                    string srcName = e.renderer.name;
                    string lodSuffix = "";
                    var lodMatch = System.Text.RegularExpressions.Regex.Match(
                        srcName, @"([_\-\s]+LOD\d+)$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (lodMatch.Success)
                    {
                        lodSuffix = lodMatch.Value;
                        srcName = srcName.Substring(0, srcName.Length - lodSuffix.Length);
                    }
                    var go = new GameObject($"{srcName}_{matName}{lodSuffix}");
                    Undo.RegisterCreatedObjectUndo(go, "Split by Material");
                    go.transform.SetParent(parent, false);
                    go.transform.localPosition = srcTransform.localPosition;
                    go.transform.localRotation = srcTransform.localRotation;
                    go.transform.localScale = srcTransform.localScale;

                    var mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = newMesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = s < mats.Length ? mats[s] : null;
                    // Copy renderer settings from source
                    var srcR = e.renderer;
                    mr.shadowCastingMode = srcR.shadowCastingMode;
                    mr.receiveShadows = srcR.receiveShadows;
                    mr.lightProbeUsage = srcR.lightProbeUsage;
                    mr.reflectionProbeUsage = srcR.reflectionProbeUsage;
                    mr.motionVectorGenerationMode = srcR.motionVectorGenerationMode;
                    mr.probeAnchor = srcR.probeAnchor;
                    mr.lightmapIndex = srcR.lightmapIndex;
                    mr.realtimeLightmapIndex = srcR.realtimeLightmapIndex;

                    newRenderers.Add(mr);
                }

                // Update LODGroup to replace original renderer with new renderers
                if (ctx.LodGroup != null)
                {
                    Undo.RecordObject(ctx.LodGroup, "Split by Material");
                    var lods = ctx.LodGroup.GetLODs();
                    for (int li = 0; li < lods.Length; li++)
                    {
                        if (lods[li].renderers == null) continue;
                        var renderers = new List<Renderer>(lods[li].renderers);
                        int idx = renderers.IndexOf(e.renderer);
                        if (idx >= 0)
                        {
                            renderers.RemoveAt(idx);
                            renderers.InsertRange(idx, newRenderers);
                            lods[li].renderers = renderers.ToArray();
                        }
                    }
                    ctx.LodGroup.SetLODs(lods);
                }

                // Destroy original
                UvtLog.Info($"Split {e.renderer.name}: {subCount} submeshes → {newRenderers.Count} objects");
                Undo.DestroyObjectImmediate(e.renderer.gameObject);
                split++;
            }

            Undo.CollapseUndoOperations(undoGroup);
            UvtLog.Info($"Split {split} multi-material mesh(es).");

            if (split > 0 && ctx.LodGroup != null)
                ctx.Refresh(ctx.LodGroup);

            splitCandidates = null;
            mergeCandidates = null;
            meshReport = null;
            requestRepaint?.Invoke();
        }

        void FixMeshMerge()
        {
            if (mergeCandidates == null || mergeCandidates.Count == 0) return;

            int undoGroup = Undo.GetCurrentGroup();
            int merged = 0;

            foreach (var group in mergeCandidates)
            {
                if (!group.include || group.entries.Count < 2) continue;

                // Determine which vertex attributes exist across all meshes
                bool hasNormals = false, hasTangents = false, hasColors = false;
                bool[] hasUv = new bool[8];
                int totalVerts = 0, totalTris = 0;

                foreach (var e in group.entries)
                {
                    var mesh = e.originalMesh ?? e.fbxMesh;
                    if (mesh == null) continue;
                    totalVerts += mesh.vertexCount;
                    totalTris += (int)(mesh.triangles.Length);
                    if (mesh.normals != null && mesh.normals.Length > 0) hasNormals = true;
                    if (mesh.tangents != null && mesh.tangents.Length > 0) hasTangents = true;
                    if (mesh.colors != null && mesh.colors.Length > 0) hasColors = true;
                    var tmpCheck = new List<Vector2>();
                    for (int ch = 0; ch < 8; ch++)
                    {
                        tmpCheck.Clear();
                        mesh.GetUVs(ch, tmpCheck);
                        if (tmpCheck.Count > 0) hasUv[ch] = true;
                    }
                }

                // Merge vertex data
                var allPos = new List<Vector3>(totalVerts);
                var allNorm = hasNormals ? new List<Vector3>(totalVerts) : null;
                var allTan = hasTangents ? new List<Vector4>(totalVerts) : null;
                var allColors = hasColors ? new List<Color>(totalVerts) : null;
                var allUvs = new List<Vector4>[8];
                for (int ch = 0; ch < 8; ch++)
                    allUvs[ch] = hasUv[ch] ? new List<Vector4>(totalVerts) : null;
                var allTrisArr = new List<int>(totalTris);

                var parent = group.entries[0].renderer.transform.parent;
                var firstEntry = group.entries[0];
                var destroyList = new List<GameObject>();

                foreach (var e in group.entries)
                {
                    var mesh = e.originalMesh ?? e.fbxMesh;
                    if (mesh == null || e.renderer == null) continue;

                    int vertOffset = allPos.Count;

                    // Transform vertices to local space of first entry
                    var srcTransform = e.renderer.transform;
                    var dstTransform = firstEntry.renderer.transform;
                    var pos = mesh.vertices;
                    for (int vi = 0; vi < pos.Length; vi++)
                    {
                        // world → dst local
                        var worldPos = srcTransform.TransformPoint(pos[vi]);
                        allPos.Add(dstTransform.InverseTransformPoint(worldPos));
                    }

                    if (allNorm != null)
                    {
                        var norms = mesh.normals;
                        if (norms != null && norms.Length > 0)
                        {
                            for (int vi = 0; vi < norms.Length; vi++)
                            {
                                var worldNorm = srcTransform.TransformDirection(norms[vi]);
                                allNorm.Add(dstTransform.InverseTransformDirection(worldNorm));
                            }
                        }
                        else
                        {
                            for (int vi = 0; vi < pos.Length; vi++)
                                allNorm.Add(Vector3.up);
                        }
                    }

                    if (allTan != null)
                    {
                        var tans = mesh.tangents;
                        if (tans != null && tans.Length > 0)
                        {
                            for (int vi = 0; vi < tans.Length; vi++)
                            {
                                var t = tans[vi];
                                var worldTan = srcTransform.TransformDirection(new Vector3(t.x, t.y, t.z));
                                var localTan = dstTransform.InverseTransformDirection(worldTan);
                                allTan.Add(new Vector4(localTan.x, localTan.y, localTan.z, t.w));
                            }
                        }
                        else
                        {
                            for (int vi = 0; vi < pos.Length; vi++)
                                allTan.Add(new Vector4(1, 0, 0, 1));
                        }
                    }

                    if (allColors != null)
                    {
                        var cols = mesh.colors;
                        if (cols != null && cols.Length > 0)
                        {
                            allColors.AddRange(cols);
                        }
                        else
                        {
                            for (int vi = 0; vi < pos.Length; vi++)
                                allColors.Add(Color.white);
                        }
                    }

                    var tmpUv4 = new List<Vector4>();
                    for (int ch = 0; ch < 8; ch++)
                    {
                        if (allUvs[ch] == null) continue;
                        tmpUv4.Clear();
                        mesh.GetUVs(ch, tmpUv4);
                        if (tmpUv4.Count > 0)
                        {
                            allUvs[ch].AddRange(tmpUv4);
                        }
                        else
                        {
                            for (int vi = 0; vi < pos.Length; vi++)
                                allUvs[ch].Add(Vector4.zero);
                        }
                    }

                    // Offset triangle indices
                    var tris = mesh.triangles;
                    for (int t = 0; t < tris.Length; t++)
                        allTrisArr.Add(tris[t] + vertOffset);

                    destroyList.Add(e.renderer.gameObject);
                }

                // Build merged mesh
                var mergedMesh = new Mesh();
                if (allPos.Count > 65535) mergedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mergedMesh.name = $"{firstEntry.renderer.name}_merged";
                mergedMesh.SetVertices(allPos);
                if (allNorm != null) mergedMesh.SetNormals(allNorm);
                if (allTan != null) mergedMesh.SetTangents(allTan);
                if (allColors != null) mergedMesh.SetColors(allColors);
                for (int ch = 0; ch < 8; ch++)
                {
                    if (allUvs[ch] != null)
                        mergedMesh.SetUVs(ch, allUvs[ch]);
                }
                mergedMesh.SetTriangles(allTrisArr, 0);
                mergedMesh.RecalculateBounds();

                // Create new GameObject at first entry's position
                var srcT = firstEntry.renderer.transform;
                var mergedGo = new GameObject(mergedMesh.name);
                Undo.RegisterCreatedObjectUndo(mergedGo, "Merge Same-Material");
                mergedGo.transform.SetParent(parent, false);
                mergedGo.transform.localPosition = srcT.localPosition;
                mergedGo.transform.localRotation = srcT.localRotation;
                mergedGo.transform.localScale = srcT.localScale;

                var mergedMf = mergedGo.AddComponent<MeshFilter>();
                mergedMf.sharedMesh = mergedMesh;
                var mergedMr = mergedGo.AddComponent<MeshRenderer>();
                mergedMr.sharedMaterial = group.material;

                // Update LODGroup
                if (ctx.LodGroup != null)
                {
                    Undo.RecordObject(ctx.LodGroup, "Merge Same-Material");
                    var lods = ctx.LodGroup.GetLODs();
                    for (int li = 0; li < lods.Length; li++)
                    {
                        if (lods[li].renderers == null) continue;
                        var renderers = new List<Renderer>(lods[li].renderers);
                        bool replaced = false;
                        for (int ri = renderers.Count - 1; ri >= 0; ri--)
                        {
                            if (renderers[ri] == null) continue;
                            if (destroyList.Contains(renderers[ri].gameObject))
                            {
                                if (!replaced)
                                {
                                    renderers[ri] = mergedMr;
                                    replaced = true;
                                }
                                else
                                {
                                    renderers.RemoveAt(ri);
                                }
                            }
                        }
                        lods[li].renderers = renderers.ToArray();
                    }
                    ctx.LodGroup.SetLODs(lods);
                }

                // Destroy originals
                foreach (var go in destroyList)
                {
                    if (go == null) continue;
                    UvtLog.Info($"Merged: {go.name}");
                    Undo.DestroyObjectImmediate(go);
                }

                merged++;
                UvtLog.Info($"Created merged object: {mergedMesh.name} ({allPos.Count} verts)");
            }

            Undo.CollapseUndoOperations(undoGroup);
            UvtLog.Info($"Merged {merged} group(s).");

            if (merged > 0 && ctx.LodGroup != null)
                ctx.Refresh(ctx.LodGroup);

            splitCandidates = null;
            mergeCandidates = null;
            meshReport = null;
            requestRepaint?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════════
        // Section 5: Split / Merge
        // ═══════════════════════════════════════════════════════════════

        void DrawSplitMergeSection()
        {
            EditorGUILayout.Space(8);
            foldSplitMerge = EditorGUILayout.Foldout(foldSplitMerge, "Split / Merge", true);
            if (!foldSplitMerge) return;

            EditorGUI.indentLevel++;

            if (ctx.LodGroup == null)
            {
                EditorGUILayout.HelpBox("Select a LODGroup first.", MessageType.Info);
                EditorGUI.indentLevel--;
                return;
            }

            // Scan button
            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.6f, .75f, .9f);
            if (GUILayout.Button("Scan", GUILayout.Height(24)))
                ScanSplitMerge();
            GUI.backgroundColor = bgc;

            // ── Split by Material ──
            if (splitCandidates != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Split Multi-Material", EditorStyles.boldLabel);

                if (splitCandidates.Count == 0)
                {
                    EditorGUILayout.LabelField("  No multi-material meshes found.", EditorStyles.miniLabel);
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

                        // Show result preview
                        if (sc.include)
                        {
                            // Compute output names (same logic as FixMeshSplitByMaterial)
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

                            EditorGUILayout.LabelField("      Remove:", EditorStyles.miniLabel);
                            EditorGUILayout.LabelField($"        {sc.entry.renderer.name}", EditorStyles.miniLabel);
                            EditorGUILayout.LabelField("      Create:", EditorStyles.miniLabel);
                            for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
                            {
                                string matName = mats[s] != null ? mats[s].name : $"mat{s}";
                                string newName = $"{srcName}_{matName}{lodSuffix}";
                                EditorGUILayout.LabelField(
                                    $"        {newName}", EditorStyles.miniLabel);
                            }
                        }
                    }

                    int selected = splitCandidates.Count(s => s.include);
                    GUI.backgroundColor = new Color(.7f, .4f, .95f);
                    GUI.enabled = selected > 0;
                    if (GUILayout.Button($"Split Selected ({selected})", GUILayout.Height(28)))
                    {
                        FixMeshSplitByMaterial();
                        EditorGUI.indentLevel--;
                        return;
                    }
                    GUI.enabled = true;
                    GUI.backgroundColor = bgc;
                }
            }

            // ── Merge Same-Material ──
            if (mergeCandidates != null)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Merge Same-Material", EditorStyles.boldLabel);

                if (mergeCandidates.Count == 0)
                {
                    EditorGUILayout.LabelField("  No merge candidates found.", EditorStyles.miniLabel);
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

                        // Show result preview
                        if (g.include)
                        {
                            int totalVerts = 0;
                            EditorGUILayout.LabelField("      Remove:", EditorStyles.miniLabel);
                            foreach (var e in g.entries)
                            {
                                if (e.renderer == null) continue;
                                var mesh = e.originalMesh ?? e.fbxMesh;
                                int verts = mesh != null ? mesh.vertexCount : 0;
                                totalVerts += verts;
                                EditorGUILayout.LabelField(
                                    $"        {e.renderer.name} ({verts:N0} v)",
                                    EditorStyles.miniLabel);
                            }
                            string mergedName = g.entries[0].renderer != null
                                ? $"{g.entries[0].renderer.name}_merged" : "merged";
                            EditorGUILayout.LabelField("      Create:", EditorStyles.miniLabel);
                            EditorGUILayout.LabelField(
                                $"        {mergedName} ({totalVerts:N0} v)",
                                EditorStyles.miniLabel);
                        }
                    }

                    int selected = mergeCandidates.Count(g => g.include);
                    GUI.backgroundColor = new Color(.7f, .4f, .95f);
                    GUI.enabled = selected > 0;
                    if (GUILayout.Button($"Merge Selected ({selected})", GUILayout.Height(28)))
                    {
                        FixMeshMerge();
                        EditorGUI.indentLevel--;
                        return;
                    }
                    GUI.enabled = true;
                    GUI.backgroundColor = bgc;
                }
            }

            EditorGUI.indentLevel--;
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

                    // Multi-material detection
                    if (mesh.subMeshCount > 1)
                    {
                        splitCandidates.Add(new SplitCandidate
                        {
                            entry = e,
                            include = true
                        });
                    }

                    // Merge candidates: single-submesh, single-material
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
            {
                if (kvp.Value.entries.Count > 1)
                    mergeCandidates.Add(kvp.Value);
            }

            UvtLog.Info($"Split/Merge scan: {splitCandidates.Count} split candidate(s), " +
                        $"{mergeCandidates.Count} merge group(s).");
        }

        // ═══════════════════════════════════════════════════════════════
        // Section 6: Mesh Attributes
        // ═══════════════════════════════════════════════════════════════

        void DrawAttributesSection()
        {
            EditorGUILayout.Space(8);
            foldAttributes = EditorGUILayout.Foldout(foldAttributes, "Mesh Attributes", true);
            if (!foldAttributes) return;

            EditorGUI.indentLevel++;

            if (meshReport == null || meshReport.attributes.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Click Scan in the Mesh section above to inspect attributes.",
                    MessageType.Info);
                EditorGUI.indentLevel--;
                return;
            }

            // Current state per mesh
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Current Channels", EditorStyles.boldLabel);

            foreach (var attr in meshReport.attributes)
            {
                string meshName = attr.meshName;
                if (meshName.Length > 28) meshName = meshName.Substring(0, 25) + "...";

                // Build compact presence list
                var present = new List<string>();
                if (attr.hasNormals) present.Add("N");
                if (attr.hasTangents) present.Add("T");
                if (attr.hasColors) present.Add("C");
                for (int ch = 0; ch < 8; ch++)
                    if (attr.hasUv[ch]) present.Add($"UV{ch}");

                string channels = present.Count > 0 ? string.Join(" ", present) : "(none)";
                EditorGUILayout.LabelField($"  {meshName}", channels, EditorStyles.miniLabel);
            }

            // Desired state toggles
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Desired Channels", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Check = add if missing.  Uncheck = remove if present.",
                EditorStyles.miniLabel);

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            ensureNormals  = EditorGUILayout.ToggleLeft("Normals", ensureNormals, GUILayout.Width(80));
            ensureTangents = EditorGUILayout.ToggleLeft("Tangents", ensureTangents, GUILayout.Width(80));
            ensureColors   = EditorGUILayout.ToggleLeft("Colors", ensureColors, GUILayout.Width(72));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            for (int ch = 0; ch < 4; ch++)
                ensureUv[ch] = EditorGUILayout.ToggleLeft($"UV{ch}", ensureUv[ch], GUILayout.Width(55));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            for (int ch = 4; ch < 8; ch++)
                ensureUv[ch] = EditorGUILayout.ToggleLeft($"UV{ch}", ensureUv[ch], GUILayout.Width(55));
            EditorGUILayout.EndHorizontal();

            // Per-mesh change preview
            int toAdd = 0, toRemove = 0;
            EditorGUILayout.Space(4);
            foreach (var attr in meshReport.attributes)
            {
                var adds = new List<string>();
                var removes = new List<string>();

                if (ensureNormals && !attr.hasNormals) adds.Add("N");
                if (!ensureNormals && attr.hasNormals) removes.Add("N");
                if (ensureTangents && !attr.hasTangents) adds.Add("T");
                if (!ensureTangents && attr.hasTangents) removes.Add("T");
                if (ensureColors && !attr.hasColors) adds.Add("C");
                if (!ensureColors && attr.hasColors) removes.Add("C");
                for (int ch = 0; ch < 8; ch++)
                {
                    if (ensureUv[ch] && !attr.hasUv[ch]) adds.Add($"UV{ch}");
                    if (!ensureUv[ch] && attr.hasUv[ch]) removes.Add($"UV{ch}");
                }

                toAdd += adds.Count;
                toRemove += removes.Count;

                if (adds.Count == 0 && removes.Count == 0) continue;

                string meshName = attr.meshName;
                if (meshName.Length > 22) meshName = meshName.Substring(0, 19) + "...";

                var parts = new List<string>();
                if (adds.Count > 0) parts.Add("+" + string.Join(" +", adds));
                if (removes.Count > 0) parts.Add("-" + string.Join(" -", removes));

                EditorGUILayout.LabelField(
                    $"  {meshName}", string.Join("  ", parts), EditorStyles.miniLabel);
            }

            if (toAdd == 0 && toRemove == 0)
            {
                EditorGUILayout.HelpBox("All meshes already match desired state.", MessageType.Info);
            }

            EditorGUILayout.Space(4);
            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.4f, .8f, .4f);
            GUI.enabled = toAdd > 0 || toRemove > 0;
            string btnLabel = toAdd > 0 && toRemove > 0
                ? $"Apply (+{toAdd} add, -{toRemove} remove)"
                : toAdd > 0 ? $"Apply (+{toAdd} add)"
                : toRemove > 0 ? $"Apply (-{toRemove} remove)"
                : "Apply (no changes)";
            if (GUILayout.Button(btnLabel, GUILayout.Height(28)))
                FixApplyAttributes();
            GUI.enabled = true;
            GUI.backgroundColor = bgc;

            EditorGUI.indentLevel--;
        }


        void FixApplyAttributes()
        {
            if (meshReport == null) return;

            int undoGroup = Undo.GetCurrentGroup();
            int changed = 0;

            foreach (var attr in meshReport.attributes)
            {
                var entry = attr.entry;
                var mesh = entry.originalMesh ?? entry.fbxMesh;
                if (mesh == null || entry.meshFilter == null || !mesh.isReadable) continue;

                // Check if any change needed
                bool needsChange = false;
                if (ensureNormals != attr.hasNormals) needsChange = true;
                if (ensureTangents != attr.hasTangents) needsChange = true;
                if (ensureColors != attr.hasColors) needsChange = true;
                for (int ch = 0; ch < 8; ch++)
                    if (ensureUv[ch] != attr.hasUv[ch]) needsChange = true;

                if (!needsChange) continue;

                // Clone if asset-backed
                if (AssetDatabase.Contains(mesh))
                {
                    var clone = UnityEngine.Object.Instantiate(mesh);
                    clone.name = mesh.name;
                    Undo.RecordObject(entry.meshFilter, "Apply Attributes");
                    entry.meshFilter.sharedMesh = clone;
                    entry.originalMesh = clone;
                    mesh = clone;
                }

                Undo.RecordObject(mesh, "Apply Attributes");
                int vertCount = mesh.vertexCount;

                // Normals
                if (ensureNormals && !attr.hasNormals)
                {
                    mesh.RecalculateNormals();
                    UvtLog.Info($"[Cleanup] {mesh.name}: added normals");
                }
                else if (!ensureNormals && attr.hasNormals)
                {
                    mesh.normals = null;
                    UvtLog.Info($"[Cleanup] {mesh.name}: removed normals");
                }

                // Tangents
                if (ensureTangents && !attr.hasTangents)
                {
                    if ((attr.hasNormals || ensureNormals) && attr.hasUv[0])
                        mesh.RecalculateTangents();
                    else
                    {
                        var tangents = new Vector4[vertCount];
                        for (int vi = 0; vi < vertCount; vi++)
                            tangents[vi] = new Vector4(1f, 0f, 0f, 1f);
                        mesh.tangents = tangents;
                    }
                    UvtLog.Info($"[Cleanup] {mesh.name}: added tangents");
                }
                else if (!ensureTangents && attr.hasTangents)
                {
                    mesh.tangents = null;
                    UvtLog.Info($"[Cleanup] {mesh.name}: removed tangents");
                }

                // Colors
                if (ensureColors && !attr.hasColors)
                {
                    var colors = new Color[vertCount];
                    for (int vi = 0; vi < vertCount; vi++)
                        colors[vi] = Color.white;
                    mesh.colors = colors;
                    UvtLog.Info($"[Cleanup] {mesh.name}: added vertex colors");
                }
                else if (!ensureColors && attr.hasColors)
                {
                    mesh.colors = null;
                    UvtLog.Info($"[Cleanup] {mesh.name}: removed vertex colors");
                }

                // UV channels
                for (int ch = 0; ch < 8; ch++)
                {
                    if (ensureUv[ch] && !attr.hasUv[ch])
                    {
                        mesh.SetUVs(ch, new Vector2[vertCount]);
                        UvtLog.Info($"[Cleanup] {mesh.name}: added UV{ch}");
                    }
                    else if (!ensureUv[ch] && attr.hasUv[ch])
                    {
                        mesh.SetUVs(ch, (List<Vector2>)null);
                        UvtLog.Info($"[Cleanup] {mesh.name}: removed UV{ch}");
                    }
                }

                changed++;
            }

            Undo.CollapseUndoOperations(undoGroup);
            UvtLog.Info($"Applied attribute changes to {changed} mesh(es).");
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

            // Compare index counts per submesh
            if (a.subMeshCount != b.subMeshCount) return false;
            for (int s = 0; s < a.subMeshCount; s++)
            {
                if (a.GetIndexCount(s) != b.GetIndexCount(s)) return false;
            }

            // Compare bounds (works without isReadable)
            float eps = 0.01f;
            if ((a.bounds.center - b.bounds.center).sqrMagnitude > eps) return false;
            if ((a.bounds.size - b.bounds.size).sqrMagnitude > eps) return false;

            // If both readable, do vertex comparison
            if (a.isReadable && b.isReadable)
            {
                var va = a.vertices;
                var vb = b.vertices;
                if (va.Length == 0) return true;
                float vEps = 1e-5f;
                if ((va[0] - vb[0]).sqrMagnitude > vEps) return false;
                if ((va[va.Length - 1] - vb[vb.Length - 1]).sqrMagnitude > vEps) return false;
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
