// UvPackHierarchyTool.cs — Per-mesh UV1 pack/repack across a GameObject
// subtree, without requiring a LODGroup. For every included MeshRenderer
// under the selected root, clones the mesh and runs XatlasRepack.RepackSingle
// to write a fresh unique UV1 atlas. Apply swaps the clone into the scene;
// "Overwrite Selected FBX" forwards to LightmapTransferTool's hierarchy
// export overload with channel=1.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    public class UvPackHierarchyTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        Action requestRepaint;

        public string ToolName  => "UV1 Hierarchy";
        public string ToolId    => "uv1_hierarchy";
        public int    ToolOrder => 40;

        public Action RequestRepaint { set => requestRepaint = value; }

        // ── State ──
        GameObject hierarchyRoot;
        List<MeshEntry> hierarchyEntries = new List<MeshEntry>();
        GameObject lastSelection;
        bool entriesBuilt;
        bool meshesFoldout = true;
        Vector2 meshesScroll;
        bool fbxOverwriteFoldout = true;
        Vector2 fbxOverwriteScroll;
        Dictionary<string, bool> fbxOverwriteMap = new Dictionary<string, bool>();
        int listVisibleRows = 8;

        // Pack options
        int resolution = 1024;
        int shellPadding = 2;
        int borderPadding = 0;

        // Per-renderer packed working copies.
        // Key = entry.fbxMesh (original FBX sub-asset ref), Value = cloned mesh
        // with fresh UV1 ready to apply.
        Dictionary<Mesh, Mesh> packedMeshes = new Dictionary<Mesh, Mesh>();
        Dictionary<Mesh, RepackResult> packedResults = new Dictionary<Mesh, RepackResult>();

        // Independent backup of scene MeshFilters that had their sharedMesh
        // swapped to a packed clone. Survives hierarchyEntries rebuilds so
        // Restore still finds the applied set after the user changes
        // Selection (which would otherwise repopulate hierarchyEntries and
        // orphan the old refs).
        struct AppliedBackup
        {
            public MeshFilter mf;
            public Mesh originalFbxMesh;
            public Mesh appliedClone;
        }
        List<AppliedBackup> appliedBackups = new List<AppliedBackup>();

        bool packedAppliedToScene => appliedBackups.Count > 0;

        // ── Lifecycle ──

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            this.ctx = ctx;
            this.canvas = canvas;
            var ps = MeshLabProjectSettings.Instance;
            resolution = ps.atlasResolution > 0 ? ps.atlasResolution : 1024;
            shellPadding = ps.shellPaddingPx;
            borderPadding = ps.borderPaddingPx;
            EditorApplication.hierarchyChanged += OnEditorHierarchyChanged;
        }

        public void OnDeactivate()
        {
            EditorApplication.hierarchyChanged -= OnEditorHierarchyChanged;
            RestoreScene();
            DestroyPackedMeshes();
            hierarchyEntries.Clear();
            hierarchyRoot = null;
            lastSelection = null;
            entriesBuilt = false;
            fbxOverwriteMap.Clear();
        }

        public void OnRefresh()
        {
            RestoreScene();
            DestroyPackedMeshes();
        }

        void OnEditorHierarchyChanged() => entriesBuilt = false;

        // ── Entries ──

        void RefreshEntriesIfNeeded()
        {
            var sel = Selection.activeGameObject;
            if (entriesBuilt && sel == lastSelection) return;
            RefreshEntries();
            lastSelection = sel;
            entriesBuilt = true;
        }

        void RefreshEntries()
        {
            // Snapshot user include toggles so selection changes don't reset
            // checkboxes.
            var prevInclude = new Dictionary<int, bool>();
            foreach (var e in hierarchyEntries)
                if (e?.renderer != null)
                    prevInclude[e.renderer.GetInstanceID()] = e.include;

            hierarchyEntries.Clear();
            hierarchyRoot = Selection.activeGameObject;
            if (hierarchyRoot == null) return;

            foreach (var r in hierarchyRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (MeshHygieneUtility.IsCollisionNodeName(r.name)) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                if (mf.sharedMesh.vertexCount == 0) continue;

                var entry = new MeshEntry
                {
                    lodIndex = 0,
                    renderer = r,
                    meshFilter = mf,
                    originalMesh = mf.sharedMesh,
                    fbxMesh = mf.sharedMesh,
                    meshGroupKey = UvToolContext.ExtractGroupKey(r.name)
                };
                if (prevInclude.TryGetValue(r.GetInstanceID(), out var prev))
                    entry.include = prev;
                hierarchyEntries.Add(entry);
            }
        }

        void SyncFbxOverwriteMap()
        {
            var current = new HashSet<string>();
            foreach (var e in hierarchyEntries)
            {
                if (e == null || !e.include || e.fbxMesh == null) continue;
                string p = AssetDatabase.GetAssetPath(e.fbxMesh);
                if (string.IsNullOrEmpty(p)) continue;
                if (!p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                current.Add(p);
                if (!fbxOverwriteMap.ContainsKey(p))
                    fbxOverwriteMap[p] = true;
            }
            var stale = fbxOverwriteMap.Keys.Where(k => !current.Contains(k)).ToList();
            foreach (var k in stale) fbxOverwriteMap.Remove(k);
        }

        // ── UI ──

        public void OnDrawSidebar()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("UV1 Pack (Hierarchy)", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            RefreshEntriesIfNeeded();
            if (hierarchyRoot == null || hierarchyEntries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Select a root GameObject that has active MeshRenderer descendants.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(
                $"Root: {hierarchyRoot.name}  ({hierarchyEntries.Count} meshes)",
                EditorStyles.miniLabel);

            DrawMeshList();
            DrawPackSettings();

            EditorGUILayout.Space(8);
            DrawPackActions();

            if (packedMeshes.Count == 0) return;

            DrawResults();
            DrawApplyRow();
            DrawFbxOverwritePicker();
        }

        void DrawMeshList()
        {
            int included = 0;
            foreach (var e in hierarchyEntries) if (e.include) included++;

            meshesFoldout = EditorGUILayout.Foldout(meshesFoldout,
                $"Meshes  ({included} / {hierarchyEntries.Count} included)", true);
            if (!meshesFoldout) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("All", EditorStyles.miniButtonLeft))
                foreach (var e in hierarchyEntries) e.include = true;
            if (GUILayout.Button("None", EditorStyles.miniButtonMid))
                foreach (var e in hierarchyEntries) e.include = false;
            if (GUILayout.Button("Invert", EditorStyles.miniButtonRight))
                foreach (var e in hierarchyEntries) e.include = !e.include;
            EditorGUILayout.EndHorizontal();

            listVisibleRows = EditorGUILayout.IntSlider(
                new GUIContent("Rows", "Number of rows visible before scrolling."),
                listVisibleRows, 3, 30);

            float rowHeight = EditorGUIUtility.singleLineHeight + 4f;
            float listHeight = Mathf.Min(hierarchyEntries.Count, listVisibleRows) * rowHeight + 6f;
            meshesScroll = EditorGUILayout.BeginScrollView(meshesScroll,
                alwaysShowHorizontal: false, alwaysShowVertical: false,
                GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                GUILayout.Height(listHeight));

            foreach (var e in hierarchyEntries)
            {
                if (e == null || e.renderer == null) continue;
                bool hasUv1 = MeshHasUv1(e.originalMesh);
                string fbxName = e.fbxMesh != null
                    ? System.IO.Path.GetFileName(AssetDatabase.GetAssetPath(e.fbxMesh))
                    : null;
                string tooltip = (string.IsNullOrEmpty(fbxName) ? "" : fbxName + "\n")
                    + (hasUv1 ? "Has UV1 → will be repacked." : "No UV1 → unwrap from UV0.")
                    + "\nClick to ping in Hierarchy";

                EditorGUILayout.BeginHorizontal();
                bool next = EditorGUILayout.Toggle(e.include, GUILayout.Width(22));
                if (next != e.include) e.include = next;
                GUILayout.Space(4);
                string rowLabel = hasUv1 ? e.renderer.name : e.renderer.name + "  *";
                if (GUILayout.Button(new GUIContent(rowLabel, tooltip), EditorStyles.label))
                    EditorGUIUtility.PingObject(e.renderer.gameObject);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
            EditorGUI.indentLevel--;
        }

        void DrawPackSettings()
        {
            EditorGUILayout.Space(4);
            resolution = EditorGUILayout.IntField(
                new GUIContent("Atlas Resolution", "Target pixel resolution for the UV1 atlas."),
                resolution);
            if (resolution < 64) resolution = 64;
            shellPadding = EditorGUILayout.IntSlider(
                new GUIContent("Shell Padding", "Pixels between shells."),
                shellPadding, 0, 16);
            borderPadding = EditorGUILayout.IntSlider(
                new GUIContent("Border Padding", "Pixels of atlas-edge inset."),
                borderPadding, 0, 16);
        }

        void DrawPackActions()
        {
            var bgc = GUI.backgroundColor;
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = new Color(.4f, .8f, .4f);
            if (GUILayout.Button("Pack UV1", GUILayout.Height(28)))
                ExecutePack();
            GUI.backgroundColor = new Color(.9f, .3f, .3f);
            using (new EditorGUI.DisabledScope(packedMeshes.Count == 0))
            {
                if (GUILayout.Button("Clear", GUILayout.Height(28)))
                {
                    RestoreScene();
                    DestroyPackedMeshes();
                    requestRepaint?.Invoke();
                }
            }
            GUI.backgroundColor = bgc;
            EditorGUILayout.EndHorizontal();
        }

        void DrawResults()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Results", EditorStyles.boldLabel);
            int okCount = 0, failCount = 0, totalShells = 0;
            foreach (var r in packedResults.Values)
            {
                if (r.ok) okCount++; else failCount++;
                totalShells += r.shellCount;
            }
            EditorGUILayout.LabelField(
                $"  Packed: {okCount},  Failed: {failCount},  Shells: {totalShells}",
                EditorStyles.miniLabel);
            if (failCount > 0)
            {
                foreach (var kvp in packedResults)
                    if (!kvp.Value.ok)
                        EditorGUILayout.LabelField($"  • {kvp.Key.name}: {kvp.Value.error}", EditorStyles.miniLabel);
            }
        }

        void DrawApplyRow()
        {
            var bgc = GUI.backgroundColor;
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = packedAppliedToScene ? new Color(.3f, .7f, 1f) : new Color(.3f, .85f, .4f);
            if (GUILayout.Button(packedAppliedToScene ? "Applied ✓" : "Apply to Mesh", GUILayout.Height(24)))
                ApplyToScene();
            GUI.backgroundColor = new Color(.6f, .6f, .6f);
            using (new EditorGUI.DisabledScope(!packedAppliedToScene))
            {
                if (GUILayout.Button("Restore", GUILayout.Height(24)))
                    RestoreScene();
            }
            GUI.backgroundColor = bgc;
            EditorGUILayout.EndHorizontal();
        }

        void DrawFbxOverwritePicker()
        {
            SyncFbxOverwriteMap();

            if (fbxOverwriteMap.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No included FBX-backed meshes.",
                    MessageType.Info);
                return;
            }

            int checkedCount = 0;
            foreach (var v in fbxOverwriteMap.Values) if (v) checkedCount++;

            fbxOverwriteFoldout = EditorGUILayout.Foldout(fbxOverwriteFoldout,
                $"Overwrite FBX  ({checkedCount} / {fbxOverwriteMap.Count} selected)", true);
            if (!fbxOverwriteFoldout) return;

            EditorGUI.indentLevel++;
            var paths = fbxOverwriteMap.Keys.OrderBy(p => p).ToList();

            float rowHeight = EditorGUIUtility.singleLineHeight + 4f;
            float listHeight = Mathf.Min(paths.Count, listVisibleRows) * rowHeight + 6f;
            fbxOverwriteScroll = EditorGUILayout.BeginScrollView(fbxOverwriteScroll,
                alwaysShowHorizontal: false, alwaysShowVertical: false,
                GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                GUILayout.Height(listHeight));

            foreach (var path in paths)
            {
                string label = System.IO.Path.GetFileName(path);
                bool cur = fbxOverwriteMap[path];
                EditorGUILayout.BeginHorizontal();
                bool next = EditorGUILayout.Toggle(cur, GUILayout.Width(22));
                if (next != cur) fbxOverwriteMap[path] = next;
                GUILayout.Space(4);
                if (GUILayout.Button(new GUIContent(label, path + "\nClick to ping in Project"),
                        EditorStyles.label))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (asset != null) EditorGUIUtility.PingObject(asset);
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = checkedCount > 0 && packedAppliedToScene ? new Color(.4f, .7f, .95f) : Color.white;
            using (new EditorGUI.DisabledScope(checkedCount == 0 || !packedAppliedToScene))
            {
                if (GUILayout.Button($"Overwrite Selected FBX  ({checkedCount})", GUILayout.Height(22)))
                    OverwriteSelectedFbx();
            }
            GUI.backgroundColor = bgc;
            if (!packedAppliedToScene)
                EditorGUILayout.LabelField("  Apply to Mesh first.", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        // ── Pack ──

        void ExecutePack()
        {
            RestoreScene();
            DestroyPackedMeshes();

            var entries = hierarchyEntries.Where(e => e.include && e.renderer != null).ToList();
            if (entries.Count == 0)
            {
                UvtLog.Warn("[UV1] No meshes selected.");
                return;
            }

            var opts = new RepackOptions
            {
                padding       = (uint)Mathf.Max(0, shellPadding),
                borderPadding = (uint)Mathf.Max(0, borderPadding),
                resolution    = (uint)Mathf.Max(64, resolution),
                texelsPerUnit = 0f,
                bilinear      = true,
                blockAlign    = false,
                bruteForce    = false,
            };

            int ok = 0, fail = 0;
            foreach (var e in entries)
            {
                var src = e.fbxMesh ?? e.originalMesh;
                if (src == null) continue;
                if (packedMeshes.ContainsKey(src)) continue; // dedup shared sub-asset

                var clone = UnityEngine.Object.Instantiate(src);
                clone.name = src.name;
                clone.hideFlags = HideFlags.HideAndDontSave;

                var res = XatlasRepack.RepackSingle(clone, opts);
                packedMeshes[src] = clone;
                packedResults[src] = res;
                if (res.ok) ok++; else
                {
                    fail++;
                    UvtLog.Warn($"[UV1] Pack failed for '{src.name}': {res.error}");
                }
            }

            UvtLog.Info($"[UV1] Packed {ok} mesh(es), failed {fail}.");
            requestRepaint?.Invoke();
        }

        void ApplyToScene()
        {
            if (packedMeshes.Count == 0) return;
            // Clear previous apply state (restore into old scene refs first)
            // so re-apply on a different hierarchy doesn't stack backups.
            RestoreScene();

            int applied = 0;
            foreach (var e in hierarchyEntries)
            {
                if (!e.include || e.renderer == null || e.meshFilter == null) continue;
                var src = e.fbxMesh ?? e.originalMesh;
                if (src == null) continue;
                if (!packedMeshes.TryGetValue(src, out var clone) || clone == null) continue;
                if (!packedResults.TryGetValue(src, out var res) || !res.ok) continue;

                Undo.RecordObject(e.meshFilter, "Apply UV1 Pack");
                var original = e.meshFilter.sharedMesh;
                e.meshFilter.sharedMesh = clone;
                e.originalMesh = clone; // snapshot lookup uses originalMesh
                appliedBackups.Add(new AppliedBackup
                {
                    mf = e.meshFilter,
                    originalFbxMesh = original,
                    appliedClone = clone
                });
                applied++;
            }
            UvtLog.Info($"[UV1] Applied to {applied} mesh(es).");
            SceneView.RepaintAll();
            requestRepaint?.Invoke();
        }

        void RestoreScene()
        {
            if (appliedBackups.Count == 0) return;
            int restored = 0;
            foreach (var b in appliedBackups)
            {
                if (b.mf == null) continue;
                // Only roll back if the MeshFilter still holds our applied
                // clone; external tooling may have swapped it already.
                if (b.mf.sharedMesh != b.appliedClone) continue;
                if (b.originalFbxMesh == null) continue;
                Undo.RecordObject(b.mf, "Restore Mesh");
                b.mf.sharedMesh = b.originalFbxMesh;
                restored++;
            }
            appliedBackups.Clear();
            // Also rewire any still-live hierarchyEntries so their
            // originalMesh reflects the restored state (the entries may
            // have been rebuilt against a different selection).
            foreach (var e in hierarchyEntries)
                if (e != null && e.meshFilter != null && e.meshFilter.sharedMesh != null)
                    e.originalMesh = e.meshFilter.sharedMesh;
            if (restored > 0) UvtLog.Info($"[UV1] Restored {restored} mesh(es) to original.");
            SceneView.RepaintAll();
            requestRepaint?.Invoke();
        }

        void DestroyPackedMeshes()
        {
            // If a clone is still assigned to a live MeshFilter (because the
            // user walked away without Restore), swap it back before destroy
            // to avoid leaving missing-mesh refs in the scene.
            RestoreScene();
            foreach (var m in packedMeshes.Values)
                if (m != null) UnityEngine.Object.DestroyImmediate(m);
            packedMeshes.Clear();
            packedResults.Clear();
        }

        void OverwriteSelectedFbx()
        {
            var selected = fbxOverwriteMap.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            if (selected.Count == 0) return;

            string list = string.Join("\n", selected.Select(p => "  • " + System.IO.Path.GetFileName(p)));
            if (!EditorUtility.DisplayDialog(
                "Overwrite Selected FBX",
                $"Overwrite {selected.Count} FBX file(s)?\n\n{list}\n\nOnly UV channel 1 is updated. Topology stays unchanged.",
                "Overwrite", "Cancel"))
                return;

            var hub = Resources.FindObjectsOfTypeAll<UvToolHub>();
            if (hub.Length == 0) return;
            var transferTool = hub[0].FindTool<LightmapTransferTool>();
            if (transferTool == null)
            {
                UvtLog.Error("[UV1] LightmapTransferTool not found.");
                return;
            }

            foreach (var path in selected)
            {
                var entriesForPath = hierarchyEntries
                    .Where(e => e.include && e.fbxMesh != null &&
                                string.Equals(AssetDatabase.GetAssetPath(e.fbxMesh), path, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (entriesForPath.Count == 0)
                {
                    UvtLog.Warn($"[UV1] No hierarchy entries map to '{path}', skipping.");
                    continue;
                }
                transferTool.ExportVertexColorsToFbx(path, entriesForPath, uvChannelOverride: 1);
            }
        }

        // ── Helpers ──

        static bool MeshHasUv1(Mesh mesh)
        {
            if (mesh == null) return false;
            var list = new List<Vector2>();
            mesh.GetUVs(1, list);
            return list.Count == mesh.vertexCount && list.Count > 0;
        }

        // ── Unused IUvTool members ──

        public void OnDrawToolbarExtra() { }
        public void OnDrawStatusBar() { }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { }
        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes() { yield break; }
        public void OnSceneGUI(SceneView sv) { }
    }
}
