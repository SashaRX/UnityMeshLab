// CleanupTool.cs — Mesh/material/collider/scene hygiene tool (IUvTool tab).
// Provides scan + fix workflow for common issues after UV, LOD, collision, and FBX export operations.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
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

        // ── Foldout state (grouped into 4 logical sections) ──
        bool foldHierarchy;
        bool foldMaterials = true;
        bool foldMeshData;
        bool foldImportSettings;

        // ── Ensure-attribute toggles ──
        bool ensureNormals, ensureTangents, ensureColors;
        bool[] ensureUv = new bool[8];

        // ── Split/Merge state ──
        List<MeshSplitMerge.SplitCandidate> splitCandidates;
        List<MeshSplitMerge.MergeGroup> mergeCandidates;

        // ── Scan results (null = not scanned yet, empty = scanned, no issues) ──
        List<MaterialIssue> materialIssues;
        List<ColliderIssue> colliderIssues;
        List<SceneIssue> sceneIssues;
        MeshReport meshReport;
        List<MeshHygieneUtility.ImportSettingsIssue> importIssues;

        // ── Inner types ──

        struct MaterialIssue
        {
            public enum Kind { HiddenShader, MismatchedMaterial, ExtraSlot, ImporterRemap, DuplicateSlot, NullSlot }
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
            public enum Kind { ExtraAttributes, Duplicate, RendererActive }
            public Kind kind;
            public GameObject gameObject;
            public Mesh mesh;
            public string description;
        }

        struct SceneIssue
        {
            public enum Kind { OrphanedLod, LodGroupMismatch, MissingLod0Suffix, RootHasMesh, MissingCollider, InvalidRootName, InvalidChars }
            public Kind kind;
            public GameObject gameObject;
            public string description;
        }

        class MeshReport
        {
            public List<LodInfo> lods = new List<LodInfo>();
            public List<MeshEntry> weldCandidates = new List<MeshEntry>();
            public List<EmptyUvEntry> emptyUvEntries = new List<EmptyUvEntry>();
            public List<MeshAttributeInfo> attributes = new List<MeshAttributeInfo>();
            public List<DegenerateEntry> degenerateEntries = new List<DegenerateEntry>();
            public List<UnusedVertEntry> unusedVertEntries = new List<UnusedVertEntry>();
        }

        struct DegenerateEntry
        {
            public MeshEntry entry;
            public int count;
        }

        struct UnusedVertEntry
        {
            public MeshEntry entry;
            public int count;
            public bool hasBlendShapes;
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
                    "Select a GameObject with LODGroup, or add one below.",
                    MessageType.Info);

                // Offer to add LODGroup to selected GameObject
                var selected = Selection.activeGameObject;
                if (selected != null && selected.GetComponent<LODGroup>() == null)
                {
                    var bgc = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(.6f, .75f, .9f);
                    if (GUILayout.Button($"Add LODGroup to \"{selected.name}\"", GUILayout.Height(24)))
                    {
                        // Every renderer below the root becomes LOD0.
                        var lodGroup = LodHierarchy.CreateFromRenderers(selected, 0.5f, requireRenderers: false, out int rendererCount);
                        ctx.Refresh(lodGroup);
                        requestRepaint?.Invoke();
                        UvtLog.Info($"Added LODGroup to {selected.name} with {rendererCount} renderer(s)");
                    }
                    GUI.backgroundColor = bgc;
                }
                return;
            }

            DrawHierarchyGroup();
            DrawMaterialsGroup();
            DrawMeshDataGroup();
            DrawImportSettingsGroup();
        }

        // ═══════════════════════════════════════════════════════════════
        // Group layout (4 top-level foldouts)
        // ═══════════════════════════════════════════════════════════════

        void DrawHierarchyGroup()
        {
            EditorGUILayout.Space(8);
            int sceneCount = sceneIssues?.Count ?? 0;
            int colCount   = colliderIssues?.Count ?? 0;
            bool scanned = sceneIssues != null || colliderIssues != null;
            int total = sceneCount + colCount;
            string header = scanned
                ? $"Hierarchy ({total} issue{(total == 1 ? "" : "s")})"
                : "Hierarchy";
            foldHierarchy = EditorGUILayout.Foldout(foldHierarchy, header, true);
            if (!foldHierarchy) return;

            EditorGUI.indentLevel++;
            DrawGroupScanFix(
                () => { ScanScene(); ScanColliders(); },
                () => { FixScene(); FixColliders(); },
                total > 0);
            DrawSceneSection();
            DrawCollidersSection();
            EditorGUI.indentLevel--;
        }

        void DrawMaterialsGroup()
        {
            EditorGUILayout.Space(8);
            int matCount = materialIssues?.Count ?? 0;
            string header = materialIssues != null
                ? $"Materials ({matCount} issue{(matCount == 1 ? "" : "s")})"
                : "Materials";
            foldMaterials = EditorGUILayout.Foldout(foldMaterials, header, true);
            if (!foldMaterials) return;

            EditorGUI.indentLevel++;
            DrawMaterialsSection();
            EditorGUI.indentLevel--;
        }

        void DrawMeshDataGroup()
        {
            EditorGUILayout.Space(8);
            bool anyScanned = meshReport != null || splitCandidates != null || mergeCandidates != null;
            int meshIssues = 0;
            if (meshReport != null)
            {
                meshIssues += meshReport.weldCandidates.Count;
                meshIssues += meshReport.emptyUvEntries.Count;
                meshIssues += meshReport.degenerateEntries.Count;
                meshIssues += meshReport.unusedVertEntries.Count;
            }
            int splitIssues = splitCandidates?.Count(s => s.include) ?? 0;
            int mergeIssues = mergeCandidates?.Count(g => g.include) ?? 0;
            int total = meshIssues + splitIssues + mergeIssues;
            string header = anyScanned
                ? $"Mesh Data ({total} issue{(total == 1 ? "" : "s")})"
                : "Mesh Data";
            foldMeshData = EditorGUILayout.Foldout(foldMeshData, header, true);
            if (!foldMeshData) return;

            EditorGUI.indentLevel++;
            DrawGroupScanFix(
                () => { ScanMesh(); ScanSplitMerge(); },
                null,
                false); // no group-level Fix — mesh fixes are per-issue
            DrawMeshSection();
            DrawSplitMergeSection();
            DrawAttributesSection();
            DrawMeshRecalcSection();
            EditorGUI.indentLevel--;
        }

        void DrawImportSettingsGroup()
        {
            EditorGUILayout.Space(8);
            int issueCount = importIssues?.Count ?? 0;
            string header = importIssues != null
                ? $"Import Settings ({issueCount} issue{(issueCount == 1 ? "" : "s")})"
                : "Import Settings";
            foldImportSettings = EditorGUILayout.Foldout(foldImportSettings, header, true);
            if (!foldImportSettings) return;

            EditorGUI.indentLevel++;
            DrawImportSettingsSection();
            EditorGUI.indentLevel--;
        }

        void DrawGroupScanFix(Action scan, Action fix, bool fixEnabled)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            var bgc = GUI.backgroundColor;
            if (scan != null)
            {
                GUI.backgroundColor = new Color(.6f, .75f, .9f);
                if (GUILayout.Button("Scan All", GUILayout.Height(22)))
                    scan();
            }
            if (fix != null)
            {
                GUI.backgroundColor = new Color(.4f, .8f, .4f);
                GUI.enabled = fixEnabled;
                if (GUILayout.Button("Fix All", GUILayout.Height(22)))
                    fix();
                GUI.enabled = true;
            }
            GUI.backgroundColor = bgc;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        // ═══════════════════════════════════════════════════════════════
        // Section 1: Fix Materials
        // ═══════════════════════════════════════════════════════════════

        void DrawMaterialsSection()
        {
            EditorGUILayout.Space(4);

            // Scan button
            var bgc2 = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.6f, .75f, .9f);
            if (GUILayout.Button("Scan", GUILayout.Height(24)))
                ScanMaterials();
            GUI.backgroundColor = bgc2;

            if (materialIssues == null) return;

            if (materialIssues.Count == 0)
            {
                EditorGUILayout.HelpBox("No material issues found.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(4);

            // Group issues by type for clarity
            bool hasSceneIssues = materialIssues.Any(i => i.kind != MaterialIssue.Kind.ImporterRemap);
            bool hasFbxIssues = materialIssues.Any(i => i.kind == MaterialIssue.Kind.ImporterRemap);

            // ── Scene issues (fixable in-place) ──
            if (hasSceneIssues)
            {
                EditorGUILayout.LabelField("Scene (fix in-place):", EditorStyles.boldLabel);
                foreach (var issue in materialIssues)
                {
                    if (issue.kind == MaterialIssue.Kind.ImporterRemap) continue;
                    EditorGUILayout.LabelField($"  {issue.description}", EditorStyles.miniLabel);
                }

                GUI.backgroundColor = new Color(.4f, .8f, .4f);
                if (GUILayout.Button("Fix Scene Materials", GUILayout.Height(24)))
                {
                    FixMaterials();
                    return;
                }
                GUI.backgroundColor = bgc2;
            }

            // ── FBX-embedded issues (need re-export) ──
            if (hasFbxIssues)
            {
                if (hasSceneIssues) EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("FBX-embedded (need re-export):", EditorStyles.boldLabel);
                foreach (var issue in materialIssues)
                {
                    if (issue.kind != MaterialIssue.Kind.ImporterRemap) continue;
                    EditorGUILayout.LabelField($"  {issue.description}", EditorStyles.miniLabel);
                }
                EditorGUILayout.HelpBox(
                    "These material names are baked into the FBX file. " +
                    "Click \"Overwrite Source FBX\" below to re-export and fix them.",
                    MessageType.Info);
            }
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

                // Check null material slots (empty slots within valid submesh range)
                for (int i = 0; i < mats.Length && i < mesh.subMeshCount; i++)
                {
                    if (mats[i] == null)
                    {
                        string key = e.meshGroupKey ?? e.renderer.name;
                        Material suggested = null;
                        if (lod0Mats.TryGetValue(key, out var l0Null) && i < l0Null.Length)
                            suggested = l0Null[i];

                        materialIssues.Add(new MaterialIssue
                        {
                            kind = MaterialIssue.Kind.NullSlot,
                            renderer = e.renderer,
                            submeshIndex = i,
                            current = null,
                            suggested = suggested,
                            description = $"{e.renderer.name}: material slot [{i}] is empty (null)"
                        });
                    }
                }

                // Check hidden shaders
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    if (mats[i].shader.name.StartsWith(CheckerTexturePreview.ToolShaderPrefix))
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
                            if (mats[i] != null && mats[i].shader.name.StartsWith(CheckerTexturePreview.ToolShaderPrefix))
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

                // Check duplicate material slots (same material in multiple submeshes)
                if (mats.Length > 1 && mesh.subMeshCount > 1)
                {
                    var seen = new Dictionary<int, int>(); // materialInstanceID → first slot index
                    for (int i = 0; i < mats.Length && i < mesh.subMeshCount; i++)
                    {
                        if (mats[i] == null) continue;
                        int matId = mats[i].GetInstanceID();
                        if (seen.TryGetValue(matId, out int firstSlot))
                        {
                            materialIssues.Add(new MaterialIssue
                            {
                                kind = MaterialIssue.Kind.DuplicateSlot,
                                renderer = e.renderer,
                                submeshIndex = i,
                                current = mats[i],
                                suggested = null,
                                description = $"{e.renderer.name}: material \"{mats[i].name}\" in slots [{firstSlot}] and [{i}] — submeshes can be merged"
                            });
                        }
                        else
                        {
                            seen[matId] = i;
                        }
                    }
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
                    if (mat != null && !mat.shader.name.StartsWith(CheckerTexturePreview.ToolShaderPrefix))
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

                    // Check SOURCE name (material name baked into FBX).
                    // Both naming generations must be matched: these names are baked
                    // into FBX files already on disk, so assets exported before the
                    // Hidden/LightmapUvTool -> Hidden/UnityMeshLab rename still carry
                    // the legacy prefix and must stay cleanable.
                    bool sourceIsHidden = sourceName.StartsWith("Hidden_UnityMeshLab")
                                       || sourceName.StartsWith("Hidden/UnityMeshLab")
                                       || sourceName.StartsWith("Hidden_LightmapUvTool")
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

                    bool isHidden = mat.name.StartsWith("Hidden_UnityMeshLab")
                                 || mat.name.StartsWith("Hidden_LightmapUvTool")
                                 || mat.shader.name.StartsWith(CheckerTexturePreview.ToolShaderPrefix);
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

            using var _undo = MeshHygieneUtility.BeginUndoGroup("Cleanup: Fix Materials");
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
                    case MaterialIssue.Kind.NullSlot:
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

            // Fix duplicate material slots — merge submeshes sharing the same material
            var dupRenderers = new HashSet<Renderer>();
            foreach (var issue in materialIssues)
            {
                if (issue.kind != MaterialIssue.Kind.DuplicateSlot) continue;
                if (issue.renderer == null || dupRenderers.Contains(issue.renderer)) continue;
                dupRenderers.Add(issue.renderer);

                var mf = issue.renderer.GetComponent<MeshFilter>();
                if (mf == null) continue;
                var mesh = mf.sharedMesh;
                if (mesh == null || mesh.subMeshCount <= 1) continue;

                var mats = issue.renderer.sharedMaterials;

                // Group submeshes by material
                var matGroups = new Dictionary<int, List<int>>(); // matInstanceID → list of submesh indices
                for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
                {
                    int matId = mats[s] != null ? mats[s].GetInstanceID() : -1;
                    if (!matGroups.ContainsKey(matId))
                        matGroups[matId] = new List<int>();
                    matGroups[matId].Add(s);
                }

                if (matGroups.Count == mesh.subMeshCount) continue; // no duplicates

                // A readable copy through MeshData: no importer flip, no reimport.
                var newMesh = MeshAccess.ReadableCopy(mesh);
                newMesh.name = mesh.name;

                // Build merged submeshes
                var newMats = new List<Material>();
                var mergedSubs = new List<int[]>();
                foreach (var kvp in matGroups)
                {
                    var indices = kvp.Value;
                    Material mat = indices[0] < mats.Length ? mats[indices[0]] : null;
                    newMats.Add(mat);

                    // Combine triangle indices from all submeshes in this group
                    var combinedTris = new List<int>();
                    foreach (int s in indices)
                        combinedTris.AddRange(newMesh.GetTriangles(s));
                    mergedSubs.Add(combinedTris.ToArray());
                }

                // Apply to new mesh
                newMesh.subMeshCount = mergedSubs.Count;
                for (int s = 0; s < mergedSubs.Count; s++)
                    newMesh.SetTriangles(mergedSubs[s], s);

                Undo.RecordObject(mf, "Merge Duplicate Material Slots");
                mf.sharedMesh = newMesh;
                Undo.RecordObject(issue.renderer, "Merge Duplicate Material Slots");
                issue.renderer.sharedMaterials = newMats.ToArray();

                UvtLog.Info($"Merged submeshes on {issue.renderer.name}: {mesh.subMeshCount} → {mergedSubs.Count} submeshes");
            }

            // ImporterRemap issues are not fixable — they require "Overwrite Source FBX"

            materialIssues = null;
            requestRepaint?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════════
        // Section 2: Clean Colliders
        // ═══════════════════════════════════════════════════════════════

        void DrawCollidersSection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Colliders", EditorStyles.boldLabel);

            // Scan button
            var bgc2 = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.6f, .75f, .9f);
            if (GUILayout.Button("Scan Colliders", GUILayout.Height(22)))
                ScanColliders();
            GUI.backgroundColor = bgc2;

            if (colliderIssues == null) return;

            if (colliderIssues.Count == 0)
            {
                EditorGUILayout.HelpBox("No collider issues found.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(4);

            bool hasSceneIssues = colliderIssues.Any(i => i.gameObject != null);
            bool hasFbxIssues = colliderIssues.Any(i => i.gameObject == null);

            // ── Scene colliders (fixable in-place) ──
            if (hasSceneIssues)
            {
                EditorGUILayout.LabelField("Scene (fix in-place):", EditorStyles.boldLabel);
                foreach (var issue in colliderIssues)
                {
                    if (issue.gameObject == null) continue;
                    EditorGUILayout.LabelField($"  {issue.description}", EditorStyles.miniLabel);
                }

                GUI.backgroundColor = new Color(.4f, .8f, .4f);
                if (GUILayout.Button("Fix Scene Colliders", GUILayout.Height(24)))
                {
                    FixColliders();
                    return;
                }
                GUI.backgroundColor = bgc2;
            }

            // ── FBX sub-asset colliders (need re-export) ──
            if (hasFbxIssues)
            {
                if (hasSceneIssues) EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("FBX-embedded (need re-export):", EditorStyles.boldLabel);
                foreach (var issue in colliderIssues)
                {
                    if (issue.gameObject != null) continue;
                    EditorGUILayout.LabelField($"  {issue.description}", EditorStyles.miniLabel);
                }
                EditorGUILayout.HelpBox(
                    "Collider attributes are baked into the FBX mesh. " +
                    "Click \"Overwrite Source FBX\" below to re-export with stripped colliders.",
                    MessageType.Info);
            }
        }

        void ScanColliders()
        {
            colliderIssues = new List<ColliderIssue>();
            if (ctx.LodGroup == null) return;

            // Track meshes already checked (avoid duplicates)
            var checkedMeshes = new HashSet<int>();

            // 1. Scan scene children with _COL in name
            var colObjects = MeshHygieneUtility.FindCollisionObjects(ctx.LodGroup.transform);
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

                // Check if _COL has active Renderer (should be collision-only)
                var renderer = go.GetComponent<Renderer>();
                bool hasMeshCollider = go.GetComponent<MeshCollider>() != null;
                if (renderer != null && renderer.enabled)
                {
                    colliderIssues.Add(new ColliderIssue
                    {
                        kind = ColliderIssue.Kind.RendererActive,
                        gameObject = go,
                        mesh = mesh,
                        description = $"{go.name}: has active Renderer" +
                            (!hasMeshCollider ? " and no MeshCollider" : "") +
                            " — should be collision-only"
                    });
                }
                else if (!hasMeshCollider && mesh != null)
                {
                    colliderIssues.Add(new ColliderIssue
                    {
                        kind = ColliderIssue.Kind.RendererActive,
                        gameObject = go,
                        mesh = mesh,
                        description = $"{go.name}: no MeshCollider assigned — collision mesh unused"
                    });
                }
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
                    if (MeshHygieneUtility.AreMeshesDuplicate(meshA, meshB))
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

            using var _undo = MeshHygieneUtility.BeginUndoGroup("Cleanup: Fix Colliders");

            foreach (var issue in colliderIssues)
            {
                switch (issue.kind)
                {
                    case ColliderIssue.Kind.ExtraAttributes:
                        if (issue.mesh == null) break;

                        // For FBX sub-asset colliders (no scene object) — info only,
                        // needs "Overwrite Source FBX" to strip attributes
                        if (issue.gameObject == null)
                        {
                            UvtLog.Warn($"Cannot strip {issue.mesh.name} — FBX sub-asset. Use \"Overwrite Source FBX\".");
                            break;
                        }

                        // Safety: don't strip if mesh is shared with a Renderer (would destroy render data)
                        {
                            bool sharedWithRenderer = false;
                            var mfCheck = issue.gameObject.GetComponent<MeshFilter>();
                            if (mfCheck != null)
                            {
                                var rendCheck = issue.gameObject.GetComponent<MeshRenderer>();
                                if (rendCheck != null && rendCheck.enabled)
                                    sharedWithRenderer = true;
                            }
                            if (!sharedWithRenderer)
                            {
                                // Also check parent/children with same mesh
                                var parentFilters = issue.gameObject.GetComponentsInParent<MeshFilter>(true);
                                foreach (var pf in parentFilters)
                                {
                                    if (pf.gameObject == issue.gameObject) continue;
                                    if (pf.sharedMesh == issue.mesh)
                                    {
                                        var pr = pf.GetComponent<MeshRenderer>();
                                        if (pr != null && pr.enabled) { sharedWithRenderer = true; break; }
                                    }
                                }
                            }
                            if (sharedWithRenderer)
                            {
                                UvtLog.Warn($"Skipping {issue.mesh.name} — mesh is shared with an active Renderer.");
                                break;
                            }
                        }

                        if (issue.mesh.isReadable)
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
                        else
                        {
                            var mf = issue.gameObject.GetComponent<MeshFilter>();
                            var mc = issue.gameObject.GetComponent<MeshCollider>();

                            // Positions and triangles only, read through MeshData whatever the
                            // import's Read/Write flag says: no importer flip, no reimport.
                            Mesh fresh = mf != null ? mf.sharedMesh : (mc != null ? mc.sharedMesh : null);
                            if (fresh != null)
                            {
                                var readable = MeshAccess.ReadableCopy(fresh);
                                var clone = new Mesh { name = fresh.name, indexFormat = readable.indexFormat };
                                clone.SetVertices(readable.vertices);
                                clone.SetTriangles(readable.triangles, 0);
                                clone.RecalculateBounds();
                                UnityEngine.Object.DestroyImmediate(readable);
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
                                UvtLog.Info($"Stripped extra attributes from {issue.gameObject.name}");
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

                    case ColliderIssue.Kind.RendererActive:
                        if (issue.gameObject == null) break;

                        // Add MeshCollider if missing
                        var existingMc = issue.gameObject.GetComponent<MeshCollider>();
                        if (existingMc == null && issue.mesh != null)
                        {
                            var mc = Undo.AddComponent<MeshCollider>(issue.gameObject);
                            mc.sharedMesh = issue.mesh;
                            UvtLog.Info($"Added MeshCollider to {issue.gameObject.name}");
                        }

                        // Disable Renderer
                        var colRenderer = issue.gameObject.GetComponent<Renderer>();
                        if (colRenderer != null && colRenderer.enabled)
                        {
                            Undo.RecordObject(colRenderer, "Disable COL Renderer");
                            colRenderer.enabled = false;
                            UvtLog.Info($"Disabled Renderer on {issue.gameObject.name}");
                        }
                        break;
                }
            }

            colliderIssues = null;
            requestRepaint?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════════
        // Section 3: Scene Cleanup
        // ═══════════════════════════════════════════════════════════════

        void DrawSceneSection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Scene", EditorStyles.boldLabel);

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
            var colObjects = MeshHygieneUtility.FindCollisionObjects(root);
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
                bool hasLodSuffix = MeshNaming.HasLodSuffix(child.name);

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

                if (!MeshHygieneUtility.TryParseLodIndex(child.name, out int lodIdx)) continue;
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

            // Check for LOD0 renderers without _LOD0 suffix
            // (only if other LODs exist with _LOD1, _LOD2 etc.)
            if (lodChildren.Count > 0)
            {
                var currentLods = ctx.LodGroup.GetLODs();
                if (currentLods.Length > 1 && currentLods[0].renderers != null)
                {
                    foreach (var r in currentLods[0].renderers)
                    {
                        if (r == null) continue;
                        bool hasLodSuffix = MeshNaming.HasLodSuffix(r.name);
                        if (!hasLodSuffix)
                        {
                            sceneIssues.Add(new SceneIssue
                            {
                                kind = SceneIssue.Kind.MissingLod0Suffix,
                                gameObject = r.gameObject,
                                description = $"{r.name}: LOD0 renderer missing _LOD0 suffix"
                            });
                        }
                    }
                }
            }

            // Check if root has MeshFilter (should be empty pivot)
            var rootMf = root.GetComponent<MeshFilter>();
            var rootSmr = root.GetComponent<SkinnedMeshRenderer>();
            bool rootHasRenderableMesh =
                (rootMf != null && rootMf.sharedMesh != null) ||
                (rootSmr != null && rootSmr.sharedMesh != null);
            if (rootHasRenderableMesh && !LodHierarchy.RootRendererIsLod0(root.gameObject, lods))
            {
                sceneIssues.Add(new SceneIssue
                {
                    kind = SceneIssue.Kind.RootHasMesh,
                    gameObject = root.gameObject,
                    description = $"{root.name}: root has mesh — should be empty pivot with LODGroup only"
                });
            }

            // Check if collision mesh exists but root has no MeshCollider
            if (colObjects.Count > 0 && root.GetComponent<MeshCollider>() == null)
            {
                sceneIssues.Add(new SceneIssue
                {
                    kind = SceneIssue.Kind.MissingCollider,
                    gameObject = root.gameObject,
                    description = $"{root.name}: has collision mesh but no MeshCollider on root"
                });
            }

            // Check if root name has _LOD/_COL/_Collider suffix (should be clean base name)
            if (MeshHygieneUtility.HasLodOrColSuffix(root.name))
            {
                sceneIssues.Add(new SceneIssue
                {
                    kind = SceneIssue.Kind.InvalidRootName,
                    gameObject = root.gameObject,
                    description = $"{root.name}: root name has LOD/COL suffix — should be base name"
                });
            }

            // Check root + direct children for invalid characters (dots, cyrillic, etc.)
            if (MeshHygieneUtility.HasInvalidChars(root.name))
            {
                sceneIssues.Add(new SceneIssue
                {
                    kind = SceneIssue.Kind.InvalidChars,
                    gameObject = root.gameObject,
                    description = $"{root.name}: name contains invalid characters (dots, cyrillic, etc.)"
                });
            }
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (MeshHygieneUtility.HasInvalidChars(child.name))
                {
                    sceneIssues.Add(new SceneIssue
                    {
                        kind = SceneIssue.Kind.InvalidChars,
                        gameObject = child.gameObject,
                        description = $"{child.name}: name contains invalid characters"
                    });
                }
            }

            UvtLog.Info($"Scene scan: {sceneIssues.Count} issue(s) found.");
        }

        void FixScene()
        {
            if (sceneIssues == null || sceneIssues.Count == 0) return;

            bool needsRefresh;
            using (MeshHygieneUtility.BeginUndoGroup("Cleanup: Fix Scene"))
            {
                needsRefresh = FixSceneCore();
            }

            sceneIssues = null;

            if (needsRefresh)
            {
                ctx.Refresh(ctx.LodGroup);
                requestRepaint?.Invoke();
            }
        }

        bool FixSceneCore()
        {
            bool needsRefresh = false;

            // First pass: sanitize invalid characters in names
            foreach (var issue in sceneIssues)
            {
                if (issue.kind != SceneIssue.Kind.InvalidChars) continue;
                if (issue.gameObject == null) continue;

                string sanitized = MeshHygieneUtility.SanitizeName(issue.gameObject.name);
                if (sanitized != issue.gameObject.name)
                {
                    Undo.RecordObject(issue.gameObject, "Sanitize Name");
                    UvtLog.Info($"Sanitized: {issue.gameObject.name} → {sanitized}");
                    issue.gameObject.name = sanitized;
                    needsRefresh = true;
                }
            }

            // Second pass: strip LOD/COL suffix from root name
            foreach (var issue in sceneIssues)
            {
                if (issue.kind != SceneIssue.Kind.InvalidRootName) continue;
                if (issue.gameObject == null) continue;

                string cleaned = UvToolContext.ExtractGroupKey(issue.gameObject.name);
                if (!string.IsNullOrEmpty(cleaned) && cleaned != issue.gameObject.name)
                {
                    Undo.RecordObject(issue.gameObject, "Strip Root Suffix");
                    UvtLog.Info($"Cleaned root name: {issue.gameObject.name} → {cleaned}");
                    issue.gameObject.name = cleaned;
                    needsRefresh = true;
                }
            }

            // Third pass: remove orphans
            foreach (var issue in sceneIssues)
            {
                if (issue.kind != SceneIssue.Kind.OrphanedLod) continue;
                if (issue.gameObject == null) continue;

                UvtLog.Info($"Removed orphaned object: {issue.gameObject.name}");
                Undo.DestroyObjectImmediate(issue.gameObject);
                needsRefresh = true;
            }

            // Fourth pass: move root mesh to child LOD0 (restructures hierarchy)
            var movedRoots = new HashSet<GameObject>();
            foreach (var issue in sceneIssues)
            {
                if (issue.kind != SceneIssue.Kind.RootHasMesh) continue;
                if (issue.gameObject == null || ctx.LodGroup == null) continue;
                if (LodHierarchy.RootRendererIsLod0(issue.gameObject, ctx.LodGroup.GetLODs()))
                {
                    UvtLog.Info($"Skipped root mesh move for '{issue.gameObject.name}': root renderer is already used as LOD0.");
                    continue;
                }

                int lodCount = LodHierarchy.MoveRootMeshToChild(issue.gameObject);
                UvtLog.Info($"Moved root mesh to child, sorted {lodCount} LOD(s) by polycount.");
                movedRoots.Add(issue.gameObject);
                needsRefresh = true;
            }

            // Fifth pass: add _LOD0 suffix to LOD0 renderers
            // Skip roots that had their mesh moved — the new child is already named _LOD0.
            foreach (var issue in sceneIssues)
            {
                if (issue.kind != SceneIssue.Kind.MissingLod0Suffix) continue;
                if (issue.gameObject == null) continue;
                if (movedRoots.Contains(issue.gameObject)) continue;
                // Skip if the gameObject no longer has a mesh renderer (mesh was moved)
                if (issue.gameObject.GetComponent<MeshRenderer>() == null &&
                    issue.gameObject.GetComponent<SkinnedMeshRenderer>() == null) continue;

                string newName = issue.gameObject.name + "_LOD0";
                Undo.RecordObject(issue.gameObject, "Add _LOD0 Suffix");
                issue.gameObject.name = newName;
                UvtLog.Info($"Renamed: {issue.description.Split(':')[0]} → {newName}");
                needsRefresh = true;
            }

            // Sixth pass: rebuild LODGroup (after hierarchy is finalized)
            bool needsRebuild = sceneIssues.Any(i => i.kind == SceneIssue.Kind.LodGroupMismatch) ||
                                movedRoots.Count > 0;
            if (needsRebuild && ctx.LodGroup != null)
            {
                RebuildLodGroupFromHierarchy();
                needsRefresh = true;
            }

            // Seventh pass: add MeshCollider from collision mesh
            foreach (var issue in sceneIssues)
            {
                if (issue.kind != SceneIssue.Kind.MissingCollider) continue;
                if (issue.gameObject == null) continue;

                LodHierarchy.AddColliderFromCollisionMesh(issue.gameObject);
                needsRefresh = true;
            }

            return needsRefresh;
        }

        // Direct children named _LOD{n}: one slot per index up to the highest (gaps stay
        // empty), halving transitions.
        void RebuildLodGroupFromHierarchy()
        {
            int levels = LodHierarchy.RebuildFromNames(ctx.LodGroup, recursive: false, keepEmptySlots: true, LodHierarchy.Transitions.Halving);
            if (levels > 0) UvtLog.Info($"Rebuilt LODGroup with {levels} LOD level(s).");
        }

        // ═══════════════════════════════════════════════════════════════
        // Section 4: Mesh
        // ═══════════════════════════════════════════════════════════════

        void DrawMeshSection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Mesh", EditorStyles.boldLabel);

            // Scan button
            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.6f, .75f, .9f);
            if (GUILayout.Button("Scan Mesh", GUILayout.Height(22)))
                ScanMesh();
            GUI.backgroundColor = bgc;

            if (meshReport == null) return;

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
                    return;
                }
                GUI.backgroundColor = bgc;
            }

            if (meshReport == null) return;

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
                    return;
                }
                GUI.backgroundColor = bgc;
            }

            if (meshReport == null) return;

            // Degenerate triangles
            if (meshReport.degenerateEntries.Count > 0)
            {
                int totalDegen = meshReport.degenerateEntries.Sum(d => d.count);
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    $"{totalDegen} degenerate triangle(s) across {meshReport.degenerateEntries.Count} mesh(es).",
                    MessageType.Warning);

                GUI.backgroundColor = new Color(.4f, .8f, .4f);
                if (GUILayout.Button("Remove Degenerate Triangles", GUILayout.Height(26)))
                {
                    FixMeshRemoveDegenerate();
                    return;
                }
                GUI.backgroundColor = bgc;
            }

            if (meshReport == null) return;

            // Unused vertices
            if (meshReport.unusedVertEntries.Count > 0)
            {
                int totalUnused = meshReport.unusedVertEntries.Sum(u => u.count);
                int blocked = meshReport.unusedVertEntries.Count(u => u.hasBlendShapes);
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    $"{totalUnused} unused vertex(es) across {meshReport.unusedVertEntries.Count} mesh(es)." +
                    (blocked > 0 ? $" {blocked} skipped (blend shapes)." : ""),
                    MessageType.Warning);

                GUI.backgroundColor = new Color(.4f, .8f, .4f);
                if (GUILayout.Button("Remove Unused Vertices", GUILayout.Height(26)))
                {
                    FixMeshRemoveUnusedVertices();
                    return;
                }
                GUI.backgroundColor = bgc;
            }

            if (meshReport == null) return;

            if (meshReport.weldCandidates.Count == 0 &&
                meshReport.emptyUvEntries.Count == 0 &&
                meshReport.degenerateEntries.Count == 0 &&
                meshReport.unusedVertEntries.Count == 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox("No mesh issues found.", MessageType.Info);
            }
        }

        void DrawMeshRecalcSection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Recalculate / Optimize", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Standalone operations on current LODGroup meshes.",
                EditorStyles.miniLabel);

            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.55f, .75f, .95f);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Recalc Normals", GUILayout.Height(22)))
                FixMeshRecalculate(RecalcKind.Normals);
            if (GUILayout.Button("Recalc Tangents", GUILayout.Height(22)))
                FixMeshRecalculate(RecalcKind.Tangents);
            if (GUILayout.Button("Recalc Bounds", GUILayout.Height(22)))
                FixMeshRecalculate(RecalcKind.Bounds);
            EditorGUILayout.EndHorizontal();

            GUI.backgroundColor = new Color(.7f, .55f, .95f);
            if (GUILayout.Button("Optimize Mesh (meshopt)", GUILayout.Height(22)))
                FixMeshOptimize();

            GUI.backgroundColor = bgc;
        }

        void DrawImportSettingsSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Unity's built-in vertex weld and secondary UV generator corrupt the tool's " +
                "lightmap UV2 output. They must stay disabled. This is already enforced per-reimport " +
                "by Uv2AssetPostprocessor; this scan surfaces the source state across all tool-managed " +
                "FBX files.",
                MessageType.Info);

            var bgc = GUI.backgroundColor;
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = new Color(.6f, .75f, .9f);
            if (GUILayout.Button("Scan", GUILayout.Height(24)))
                ScanImportSettings();
            GUI.backgroundColor = new Color(.4f, .8f, .4f);
            GUI.enabled = importIssues != null && importIssues.Any(i => i.isHardIssue);
            if (GUILayout.Button("Fix", GUILayout.Height(24)))
                FixImportSettings();
            GUI.enabled = true;
            GUI.backgroundColor = bgc;
            EditorGUILayout.EndHorizontal();

            if (importIssues == null) return;

            if (importIssues.Count == 0)
            {
                EditorGUILayout.HelpBox("No import-setting issues found.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(4);
            foreach (var issue in importIssues)
            {
                var msgType = issue.isHardIssue ? MessageType.Warning : MessageType.Info;
                EditorGUILayout.HelpBox(issue.description, msgType);
            }
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
                    int tris = MeshHygieneUtility.GetTriangleCount(mesh);
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

                    // Degenerate triangles
                    int degenerate = MeshHygieneUtility.CountDegenerateTriangles(mesh);
                    if (degenerate > 0)
                    {
                        meshReport.degenerateEntries.Add(new DegenerateEntry
                        {
                            entry = e,
                            count = degenerate
                        });
                    }

                    // Unused vertices
                    int unused = MeshHygieneUtility.CountUnusedVertices(mesh);
                    if (unused > 0)
                    {
                        meshReport.unusedVertEntries.Add(new UnusedVertEntry
                        {
                            entry = e,
                            count = unused,
                            hasBlendShapes = mesh.blendShapeCount > 0
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
            int totalDegen = meshReport.degenerateEntries.Sum(d => d.count);
            int totalUnused = meshReport.unusedVertEntries.Sum(u => u.count);
            UvtLog.Info($"Mesh scan: {meshReport.weldCandidates.Count} weld candidate(s), " +
                        $"{meshReport.emptyUvEntries.Count} mesh(es) with empty channels" +
                        (colorCount > 0 ? $" ({colorCount} with zero vertex colors)" : "") +
                        (totalDegen > 0 ? $", {totalDegen} degenerate tri(s) across {meshReport.degenerateEntries.Count} mesh(es)" : "") +
                        (totalUnused > 0 ? $", {totalUnused} unused vert(s) across {meshReport.unusedVertEntries.Count} mesh(es)" : "") +
                        ".");

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

            using var _undo = MeshHygieneUtility.BeginUndoGroup("Cleanup: Batch Weld");
            int welded = 0;

            foreach (var e in meshReport.weldCandidates)
            {
                if (!MeshHygieneUtility.PrepareWritable(e, "Batch Weld", out var mesh))
                    continue;

                if (Uv0Analyzer.WeldInPlace(mesh))
                {
                    MeshOptimizer.Optimize(mesh);
                    welded++;
                    UvtLog.Info($"Welded: {mesh.name}");
                }
            }

            UvtLog.Info($"Batch weld complete: {welded} mesh(es) welded.");
            meshReport = null;
            requestRepaint?.Invoke();
        }

        void FixMeshRemoveDegenerate()
        {
            if (meshReport == null || meshReport.degenerateEntries.Count == 0) return;

            using var _undo = MeshHygieneUtility.BeginUndoGroup("Cleanup: Remove Degenerate Triangles");
            int totalRemoved = 0;
            int affected = 0;

            foreach (var entry in meshReport.degenerateEntries)
            {
                if (!MeshHygieneUtility.PrepareWritable(entry.entry, "Remove Degenerate Triangles", out var mesh))
                    continue;
                int removed = MeshHygieneUtility.RemoveDegenerateTriangles(mesh);
                if (removed > 0)
                {
                    totalRemoved += removed;
                    affected++;
                    UvtLog.Info($"[Cleanup] {mesh.name}: removed {removed} degenerate triangle(s)");
                }
            }

            UvtLog.Info($"Removed {totalRemoved} degenerate triangle(s) across {affected} mesh(es).");
            meshReport = null;
            requestRepaint?.Invoke();
        }

        void FixMeshRemoveUnusedVertices()
        {
            if (meshReport == null || meshReport.unusedVertEntries.Count == 0) return;

            using var _undo = MeshHygieneUtility.BeginUndoGroup("Cleanup: Remove Unused Vertices");
            int totalRemoved = 0;
            int affected = 0;

            foreach (var entry in meshReport.unusedVertEntries)
            {
                if (entry.hasBlendShapes)
                {
                    UvtLog.Warn($"[Cleanup] Skipped '{entry.entry.originalMesh?.name}' — blend shapes present.");
                    continue;
                }
                if (!MeshHygieneUtility.PrepareWritable(entry.entry, "Remove Unused Vertices", out var mesh))
                    continue;
                int removed = MeshHygieneUtility.CompactVertices(mesh);
                if (removed > 0)
                {
                    totalRemoved += removed;
                    affected++;
                    UvtLog.Info($"[Cleanup] {mesh.name}: removed {removed} unused vertex(es)");
                }
            }

            UvtLog.Info($"Removed {totalRemoved} unused vertex(es) across {affected} mesh(es).");
            meshReport = null;
            requestRepaint?.Invoke();
        }

        enum RecalcKind { Normals, Tangents, Bounds }

        void FixMeshRecalculate(RecalcKind kind)
        {
            if (ctx.LodGroup == null || ctx.MeshEntries == null) return;

            string label = kind switch
            {
                RecalcKind.Normals  => "Cleanup: Recalculate Normals",
                RecalcKind.Tangents => "Cleanup: Recalculate Tangents",
                RecalcKind.Bounds   => "Cleanup: Recalculate Bounds",
                _ => "Cleanup: Recalculate"
            };

            using var _undo = MeshHygieneUtility.BeginUndoGroup(label);
            int touched = 0;
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include) continue;
                if (!MeshHygieneUtility.PrepareWritable(e, label, out var mesh))
                    continue;
                switch (kind)
                {
                    case RecalcKind.Normals:  mesh.RecalculateNormals();  break;
                    case RecalcKind.Tangents: mesh.RecalculateTangents(); break;
                    case RecalcKind.Bounds:   mesh.RecalculateBounds();   break;
                }
                touched++;
            }
            UvtLog.Info($"{label}: {touched} mesh(es).");
            requestRepaint?.Invoke();
        }

        void FixMeshOptimize()
        {
            if (ctx.LodGroup == null || ctx.MeshEntries == null) return;

            using var _undo = MeshHygieneUtility.BeginUndoGroup("Cleanup: Optimize Mesh");
            int touched = 0;
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include) continue;
                if (!MeshHygieneUtility.PrepareWritable(e, "Optimize Mesh", out var mesh))
                    continue;
                var result = MeshOptimizer.Optimize(mesh);
                if (result.ok)
                {
                    touched++;
                    UvtLog.Info($"[Cleanup] Optimized {mesh.name}: {result.originalVertexCount} → {result.optimizedVertexCount} verts");
                }
                else if (!string.IsNullOrEmpty(result.error))
                {
                    UvtLog.Warn($"[Cleanup] Optimize failed on {mesh.name}: {result.error}");
                }
            }
            UvtLog.Info($"Optimize Mesh: {touched} mesh(es).");
            requestRepaint?.Invoke();
        }

        void FixMeshStripUvs()
        {
            if (meshReport == null || meshReport.emptyUvEntries.Count == 0) return;

            using var _undo = MeshHygieneUtility.BeginUndoGroup("Cleanup: Strip Empty Channels");
            int stripped = 0;

            foreach (var uvEntry in meshReport.emptyUvEntries)
            {
                var entry = uvEntry.entry;
                var channels = uvEntry.channels;
                if (!MeshHygieneUtility.PrepareWritable(entry, "Strip Empty Channels", out var mesh))
                    continue;

                foreach (int ch in channels)
                    mesh.SetUVs(ch, (List<Vector2>)null);
                // Note: vertex colors are NOT auto-stripped — they may contain
                // valid AO/data even when all zeros (full occlusion).

                stripped += channels.Count;
                UvtLog.Info($"Stripped {channels.Count} empty UV channel(s) from {mesh.name}");
            }

            UvtLog.Info($"Stripped {stripped} empty channel(s) total.");
            meshReport = null;
            requestRepaint?.Invoke();
        }

        void FixMeshSplitByMaterial()
        {
            if (splitCandidates == null || splitCandidates.Count == 0) return;
            MeshSplitMerge.SplitByMaterial(ctx, splitCandidates, "Cleanup: Split by Material");
            splitCandidates = null; mergeCandidates = null; meshReport = null;
            requestRepaint?.Invoke();
        }

        void FixMeshMerge()
        {
            if (mergeCandidates == null || mergeCandidates.Count == 0) return;
            MeshSplitMerge.MergeSameMaterial(ctx, mergeCandidates, "Cleanup: Merge Same-Material");
            splitCandidates = null; mergeCandidates = null; meshReport = null;
            requestRepaint?.Invoke();
        }

        void ScanSplitMerge()
        {
            var report = MeshSplitMerge.Scan(ctx);
            splitCandidates = report.split; mergeCandidates = report.merge;
        }

        // ═══════════════════════════════════════════════════════════════
        // Section 5: Split / Merge
        // ═══════════════════════════════════════════════════════════════

        void DrawSplitMergeSection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Split / Merge", EditorStyles.boldLabel);

            if (ctx.LodGroup == null)
            {
                EditorGUILayout.HelpBox("Select a LODGroup first.", MessageType.Info);
                return;
            }

            // Scan button
            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.6f, .75f, .9f);
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
                            srcName = MeshNaming.SplitLodSuffix(srcName, out string lodSuffix);

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
                            string previewMergeName = "merged";
                            if (g.entries[0].renderer != null)
                            {
                                string pn = g.entries[0].renderer.name;
                                var pMatch = System.Text.RegularExpressions.Regex.Match(
                                    pn, @"([_\-\s]+LOD\d+)$",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (pMatch.Success)
                                    pn = pn.Substring(0, pn.Length - pMatch.Value.Length);
                                previewMergeName = $"{pn}_LOD{g.lodIndex}";
                            }
                            EditorGUILayout.LabelField("      Create:", EditorStyles.miniLabel);
                            EditorGUILayout.LabelField(
                                $"        {previewMergeName} ({totalVerts:N0} v)",
                                EditorStyles.miniLabel);
                        }
                    }

                    int selected = mergeCandidates.Count(g => g.include);
                    GUI.backgroundColor = new Color(.7f, .4f, .95f);
                    GUI.enabled = selected > 0;
                    if (GUILayout.Button($"Merge Selected ({selected})", GUILayout.Height(28)))
                    {
                        FixMeshMerge();
                        return;
                    }
                    GUI.enabled = true;
                    GUI.backgroundColor = bgc;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Section 6: Mesh Attributes
        // ═══════════════════════════════════════════════════════════════

        void DrawAttributesSection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Mesh Attributes", EditorStyles.boldLabel);

            if (meshReport == null || meshReport.attributes.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Click Scan in the Mesh section above to inspect attributes.",
                    MessageType.Info);
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
        }


        // ═══════════════════════════════════════════════════════════════
        // Section: Import Settings
        // ═══════════════════════════════════════════════════════════════

        void ScanImportSettings()
        {
            importIssues = new List<MeshHygieneUtility.ImportSettingsIssue>();
            if (ctx.LodGroup == null || ctx.MeshEntries == null) return;

            var fbxPaths = new HashSet<string>();
            foreach (var e in ctx.MeshEntries)
            {
                Mesh m = e.fbxMesh ?? e.originalMesh;
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    fbxPaths.Add(p);
            }

            foreach (var path in fbxPaths)
                MeshHygieneUtility.ScanImportSettings(path, importIssues);

            UvtLog.Info($"Import Settings scan: {importIssues.Count} issue(s) across {fbxPaths.Count} FBX file(s).");
        }

        void FixImportSettings()
        {
            if (importIssues == null || importIssues.Count == 0) return;

            // Unique asset paths with hard issues.
            var paths = new HashSet<string>();
            foreach (var i in importIssues)
                if (i.isHardIssue) paths.Add(i.assetPath);

            if (paths.Count == 0)
            {
                UvtLog.Info("Import Settings: nothing to fix (only informational issues).");
                importIssues = null;
                requestRepaint?.Invoke();
                return;
            }

            foreach (var path in paths)
            {
                Uv2AssetPostprocessor.PrepareImportSettings(path, force: true);
                UvtLog.Info($"Import Settings: normalized '{System.IO.Path.GetFileName(path)}'.");
            }

            UvtLog.Info($"Import Settings: fixed {paths.Count} FBX file(s).");
            importIssues = null;
            if (ctx.LodGroup != null) ctx.Refresh(ctx.LodGroup);
            requestRepaint?.Invoke();
        }

        void FixApplyAttributes()
        {
            if (meshReport == null) return;

            using var _undo = MeshHygieneUtility.BeginUndoGroup("Cleanup: Apply Attributes");
            int changed = 0;

            foreach (var attr in meshReport.attributes)
            {
                var entry = attr.entry;
                if (entry.meshFilter == null) continue;

                // Check if any change needed
                bool needsChange = false;
                if (ensureNormals != attr.hasNormals) needsChange = true;
                if (ensureTangents != attr.hasTangents) needsChange = true;
                if (ensureColors != attr.hasColors) needsChange = true;
                for (int ch = 0; ch < 8; ch++)
                    if (ensureUv[ch] != attr.hasUv[ch]) needsChange = true;

                if (!needsChange) continue;

                if (!MeshHygieneUtility.PrepareWritable(entry, "Apply Attributes", out var mesh))
                    continue;

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
