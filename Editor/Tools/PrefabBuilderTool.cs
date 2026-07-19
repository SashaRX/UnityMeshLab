// PrefabBuilderTool.cs — Prefab Builder tool (IUvTool tab).
// Sidebar layout (PR-1):
//   • Scene preview toolbar (Off / Vert Colors / Normals / Tangents / UV0-3 / Edges / Problems)
//   • Hierarchy: Root + Apply Changes + per-Dummy blocks (LOD rows + COL rows + channel badges)
//   • Legacy Collision / Split / Merge / Mesh Info / Edge / Problem sections
//
// Pending-changes model: clicking "+ Add LOD" or ✕ on an existing LOD row queues
// a pending operation; the prefab itself is untouched until the user clicks
// Apply Changes. Apply Changes commits, in order, Root/Dummy renames, pending
// deletes, pending inserts (creates GameObject + simplified mesh), then renumbers
// trailing _LOD{N} / _COL[_Hull{N}] suffixes. Regenerate (↻) currently still
// runs immediately — it will move into the right-side settings panel with a 3D
// preview in PR-2; for now regenerated rows expose a Discard (↶) button that
// restores the import-time fbxMesh.
//
// PR-2 will pull tool settings (LOD generation, transfer, collider, VC bake) into a
// right-side stack and migrate the legacy sections out of the sidebar. PR-3 adds the
// Build & Save bottom bar with pre-flight validation. The Build Pipeline / LOD Levels
// foldouts that lived here previously have been folded into the new Hierarchy view.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    public class PrefabBuilderTool : IUvTool, IUvToolRightSidebar
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        System.Action requestRepaint;

        // ── Identity ──
        public string ToolName  => "Prefab Builder";
        public string ToolId    => "prefab_builder";
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
        PrefabBuilderPreview preview;

        // ── Hierarchy view state ──
        // pendingNames: instanceID → edited name (uncommitted; committed by Apply Names).
        // lodQualitySliders: renderer instanceID → simplification target ratio.
        // freshRendererIds: renderer instanceID set freshly created or regenerated
        // since the last Apply Names. Drives the bright-orange "this is new"
        // highlight so the user can spot just-inserted LODs in a busy hierarchy.
        // hierarchyDummies: cached view model rebuilt from ctx; null = needs rebuild.
        Dictionary<int, string> pendingNames;
        Dictionary<int, float> lodQualitySliders;
        HashSet<int> freshRendererIds;
        // Tracks the LODGroup the fresh-set is scoped to. When the user
        // selects a different prefab the hub fires OnRefresh; we then know
        // to discard the highlight from the previous prefab. Structural
        // mutations on the SAME LODGroup (Insert/Delete) keep the set.
        LODGroup freshTrackedLodGroup;
        bool hierarchyFoldout = true;
        List<HierarchyDummy> hierarchyDummies;

        // ── Right sidebar state ──
        // PrefabBuilderTool implements IUvToolRightSidebar so the hub renders
        // a separate sidebar on the right edge of the window for our
        // Settings stack. The sidebar hosts deferred regenerate parameters
        // (LOD), collider config, UV2 transfer, vertex-color bake — each
        // section is sourced from the existing tool's instance via
        // UvToolHub.FindTool<T>(). Tools are not duplicated — we just call
        // into their existing settings UI.
        bool rightPanelLodFoldout = true;
        bool rightPanelColliderFoldout;
        bool rightPanelTransferFoldout;
        bool rightPanelVcBakeFoldout;

        // ── Per-chain foldout state ──
        // Set of "<dummyTransformInstanceId>|<chainBaseName>" keys that the
        // user has collapsed. Default open (key absent).
        HashSet<string> collapsedChains;

        // ── Pending changes (deferred until "Apply Changes") ──
        // Pending Insert: the user clicked "+ Add LOD after LODN" but the
        // GameObject + simplified mesh aren't created until Apply Changes.
        // The pending row renders inline in DrawDummyBlock with a PENDING
        // badge and a quality slider the user can tweak before commit.
        // Pending Delete: the user clicked ✕ on an existing LOD row. The
        // renderer is marked for destruction but not actually removed
        // until Apply Changes; clicking ✕ again reverts the mark.
        // Pending inserts are slot-scoped (apply to the LODGroup as a whole, not
        // to a single Dummy). Clicking "+ Add LOD after LOD{N}" in any Dummy
        // block queues one entry; on Apply Changes we create a renderer in
        // EVERY Dummy that has a LOD0 source mesh and splice them all into the
        // new slot together. Otherwise inserting in Stove_Base alone would
        // leave Stove_Cap with no coverage at the new slot, and Stove_Cap
        // would silently disappear at that camera distance.
        sealed class PendingInsert
        {
            public int afterLodIndex;          // slot to insert after (-1 = at the very start)
            // Captured at click time so the slot index can be re-resolved
            // against the live LODGroup at Apply Changes time. Without this,
            // applying a pending delete BEFORE the insert (in the same Apply
            // batch) would shift slot indices and the insert would land in
            // the wrong place.
            public Renderer afterRenderer;
            public float quality;              // simplification target ratio chosen by the user
        }
        // Pending Wrap-chain-in-Dummy: captures the chain base name + the
        // renderers that should be reparented into the new Dummy when the
        // user clicks Apply Changes. Kept as a queue so the user can stack
        // multiple wraps before committing, and cancel any queued wrap by
        // clicking the chain action a second time.
        sealed class PendingWrapChain
        {
            public string baseName;
            public List<Renderer> renderers;
        }
        // Pending Add-empty-Dummy: just a name. Apply Changes creates the
        // GameObject under Root.
        sealed class PendingAddDummy
        {
            public string name;
        }
        List<PendingInsert> pendingInserts;
        HashSet<int> pendingDeleteRendererIds;
        List<PendingWrapChain> pendingWrapChains;
        List<PendingAddDummy> pendingAddDummies;
        // Per-renderer backup of the mesh before the FIRST regenerate this
        // session. Used by RowWasRegenerated / DiscardRegenerate so the
        // affordance survives ctx.Refresh (which would otherwise rewrite
        // MeshEntry.fbxMesh to point at the simplified mesh).
        Dictionary<int, Mesh> regenBackupMeshes;

        sealed class HierarchyDummy
        {
            public Transform dummy;          // container transform; equals root when flat
            public bool isRoot;              // true when the prefab has no separate dummy container
            public List<HierarchyLodRow> lods = new List<HierarchyLodRow>();
            public List<HierarchyColRow> cols = new List<HierarchyColRow>();
            public bool foldout = true;
        }

        sealed class HierarchyLodRow
        {
            public int lodIndex;
            public Renderer renderer;
            public Mesh mesh;
        }

        sealed class HierarchyColRow
        {
            public Transform colTransform;     // GameObject the collider is on (dummy itself or a _COL child).
            public Collider collider;          // Component reference (Mesh / Box / Capsule / Sphere…).
            public Mesh mesh;                  // sharedMesh for MeshCollider, or sharedMesh of a _COL GO MeshFilter.
            public string typeLabel;           // "Mesh" / "Box" / "Capsule" / "Sphere" / fallback type name.
            // True when this row represents a Collider component attached to the
            // Dummy/Root GameObject itself (not a separate _COL child). Component
            // rows can't be renamed by Rebuild Names and ✕ removes only the
            // component instead of destroying the whole GameObject.
            public bool isComponentOnly;
        }

        // Accumulates which channels mutating operations touched since the last
        // refresh. Drives the export pipeline (PR-3) so isolated re-save can be
        // used when only data channels changed.
        FbxExportIntent buildIntent = FbxExportIntent.None;

        // ── Split/Merge state (legacy, migrates to right panel in PR-2) ──
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

        // ── Collision state (legacy) ──
        bool collisionFoldout;

        // ── Edge / problem report cache (legacy) ──
        struct MeshEdgeReport
        {
            public string meshName;
            public EdgeAnalyzer.EdgeReport report;
        }
        List<MeshEdgeReport> edgeReports;

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
            if (preview == null) preview = new PrefabBuilderPreview();
            pendingNames = new Dictionary<int, string>();
            lodQualitySliders = new Dictionary<int, float>();
            freshRendererIds = new HashSet<int>();
            pendingInserts = new List<PendingInsert>();
            pendingDeleteRendererIds = new HashSet<int>();
            pendingWrapChains = new List<PendingWrapChain>();
            pendingAddDummies = new List<PendingAddDummy>();
            regenBackupMeshes = new Dictionary<int, Mesh>();
            collapsedChains = new HashSet<string>();
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
            lodQualitySliders?.Clear();

            // Clear session-scoped state ONLY when the selected prefab's
            // LODGroup itself has changed. Hub.OnGUI also fires OnRefresh on
            // structural mutations within the same prefab (LodCount delta);
            // those should keep the ★ NEW highlight and pending-changes
            // queue so the user can spot the row they just added even after
            // a Rebuild Names.
            if (ctx == null || ctx.LodGroup != freshTrackedLodGroup)
            {
                freshRendererIds?.Clear();
                pendingInserts?.Clear();
                pendingDeleteRendererIds?.Clear();
                pendingWrapChains?.Clear();
                pendingAddDummies?.Clear();
                regenBackupMeshes?.Clear();
                freshTrackedLodGroup = ctx?.LodGroup;
            }

            hierarchyDummies = null;
            splitCandidates = null;
            mergeCandidates = null;
            buildIntent = FbxExportIntent.None;
        }

        // ── UI: Sidebar ──

        public void OnDrawSidebar()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Prefab Builder", EditorStyles.boldLabel);
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
            DrawCollisionSection();
            DrawSplitMergeSection();
            DrawMeshInfo();
            if (edgeReports != null) DrawEdgeReportSection();
            if (problemSummaries != null) DrawProblemSummarySection();
            DrawEdgeLegend();
        }

        // ═══════════════════════════════════════════════════════════
        // Right sidebar — Settings stack hosted by the hub on the right
        // edge of the window. Each foldout pulls its UI from the matching
        // tool instance via UvToolHub.FindTool<T>() so we don't duplicate
        // the per-tool state. PR-2 lights up LOD Settings; the remaining
        // sections are placeholders pending migration.
        // ═══════════════════════════════════════════════════════════

        public void OnDrawRightSidebar()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Tool Settings", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            DrawRightPanelSection(ref rightPanelLodFoldout, "LOD Settings", DrawLodSettingsContent);
            DrawRightPanelSection(ref rightPanelColliderFoldout, "Collider Settings",
                () => EditorGUILayout.HelpBox("To be migrated from Collision tool in a follow-up commit.", MessageType.None));
            DrawRightPanelSection(ref rightPanelTransferFoldout, "Transfer Settings",
                () => EditorGUILayout.HelpBox("To be migrated from UV2 Transfer tool in a follow-up commit.", MessageType.None));
            DrawRightPanelSection(ref rightPanelVcBakeFoldout, "Vertex Color Bake",
                () => EditorGUILayout.HelpBox("To be migrated from Vertex Color Baking tool in a follow-up commit.", MessageType.None));
        }

        void DrawRightPanelSection(ref bool foldout, string title, System.Action drawContent)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            foldout = EditorGUILayout.Foldout(foldout, title, true, EditorStyles.foldoutHeader);
            if (foldout)
            {
                EditorGUILayout.Space(2);
                drawContent?.Invoke();
            }
            EditorGUILayout.EndVertical();
        }

        void DrawLodSettingsContent()
        {
            var lodGen = FindLodGenerationTool();
            if (lodGen == null)
            {
                EditorGUILayout.HelpBox(
                    "LodGenerationTool instance not found in the hub.",
                    MessageType.Info);
                return;
            }
            // Only the fine-tuning weights live here. LOD count + per-target
            // ratios are managed via the Hierarchy "+ Add LOD" pending model
            // (left sidebar). Showing the full LOD Gen settings panel here
            // would duplicate that workflow.
            lodGen.DrawSimplifierSettingsPanel();
        }

        // Resolve the singleton LodGenerationTool instance the Hub created
        // when it discovered IUvTool implementations on activation. Iterating
        // open windows is cheap; the hub is a single dockable EditorWindow.
        static LodGenerationTool FindLodGenerationTool()
        {
            var hubs = Resources.FindObjectsOfTypeAll<UvToolHub>();
            if (hubs == null) return null;
            foreach (var hub in hubs)
            {
                var t = hub != null ? hub.FindTool<LodGenerationTool>() : null;
                if (t != null) return t;
            }
            return null;
        }

        // Pull simplifier weights from the right-sidebar Settings panel
        // (LodGenerationTool's tunables). When the LOD Gen tool isn't
        // discoverable yet (e.g. during very early initialization), fall
        // back to a known-safe preset so regenerate / insert still proceeds.
        MeshSimplifier.SimplifySettings ResolveSimplifierSettings(float targetRatio)
        {
            var lodGen = FindLodGenerationTool();
            if (lodGen != null)
                return lodGen.GetSimplifierSettings(targetRatio);
            return new MeshSimplifier.SimplifySettings
            {
                targetRatio  = targetRatio,
                targetError  = 0.1f,
                uv2Weight    = 0.5f,
                normalWeight = 0.5f,
                lockBorder   = true,
                uvChannel    = 1,
            };
        }

        // ═══════════════════════════════════════════════════════════
        // Preview mode toolbar (PR-4 will move this to viewport top bar)
        // ═══════════════════════════════════════════════════════════

        void DrawPreviewModeToolbar()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Scene Preview", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            DrawModeButton("Off", PreviewMode.None);
            DrawModeButton("Vert Colors", PreviewMode.VertexColors);
            DrawModeButton("Normals", PreviewMode.Normals);
            DrawModeButton("Tangents", PreviewMode.Tangents);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            DrawModeButton("UV0", PreviewMode.UV0);
            DrawModeButton("UV1", PreviewMode.UV1);
            DrawModeButton("UV2", PreviewMode.UV2);
            DrawModeButton("UV3", PreviewMode.UV3);
            EditorGUILayout.EndHorizontal();

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
        // Hierarchy section (NEW, PR-1)
        //
        // Shape:
        //   Root row: [name field] "Root"  [Apply Names (green)]
        //   For each Dummy block:
        //     Foldout header  [name field if non-root]
        //     Per LOD row:
        //       Row A: [name display]   [+ Add LOD]  [↻]  [✕]
        //       Row B: LOD{N}  Vv / Tt   [channel badges]
        //       Row C: LOD{N}  [— quality slider —]  value
        //     "+ Add LOD" insert-row between LODs and at the bottom
        //     COL rows (different colour) with [name] [✕]
        //
        // Per user spec: only Root + Dummy names are user-editable; child names
        // (LOD / COL) are auto-derived on Apply Names. ↑↓ reorder removed.
        // ═══════════════════════════════════════════════════════════

        void DrawHierarchySection()
        {
            EditorGUILayout.Space(8);
            hierarchyFoldout = EditorGUILayout.Foldout(hierarchyFoldout, "Hierarchy", true);
            if (!hierarchyFoldout) return;

            if (ctx.LodGroup == null && !ctx.StandaloneMesh)
            {
                EditorGUILayout.HelpBox("No LODGroup selected.", MessageType.Info);
                return;
            }

            if (ctx.LodGroup == null)
            {
                EditorGUILayout.HelpBox("Standalone mesh: hierarchy editing requires LODGroup.", MessageType.Info);
                return;
            }

            if (hierarchyDummies == null) RebuildHierarchyView();
            if (hierarchyDummies == null) return;

            DrawRootRow();

            // Re-check: DrawRootRow's Rebuild Names button can mutate state
            // and null hierarchyDummies mid-frame.
            if (hierarchyDummies == null) return;

            // Indent each Dummy block so the hierarchy reads as a tree
            // (Root at the left edge → Dummy children visually nested under
            // it). The 20-px gutter on the left of each block holds an
            // L-shaped connector that explicitly anchors the dummy back to
            // Root, so the parent/child relationship is unmistakable even
            // when the helpBox borders blend in with the inspector.
            foreach (var dummy in hierarchyDummies)
            {
                if (dummy == null || dummy.dummy == null) continue;

                EditorGUILayout.BeginHorizontal();
                var gutter = GUILayoutUtility.GetRect(HierarchyDummyIndent, 22,
                    GUILayout.Width(HierarchyDummyIndent), GUILayout.Height(22));
                if (Event.current.type == EventType.Repaint)
                {
                    var line = new Color(0.55f, 0.55f, 0.55f);
                    float xMid = gutter.x + HierarchyDummyIndent * 0.5f;
                    // Vertical drop from top of gutter to the elbow.
                    EditorGUI.DrawRect(new Rect(xMid, gutter.y, 1, 14), line);
                    // Horizontal stub from the elbow into the helpBox.
                    EditorGUI.DrawRect(new Rect(xMid, gutter.y + 14,
                        HierarchyDummyIndent * 0.5f + 2, 1), line);
                }

                EditorGUILayout.BeginVertical();
                DrawDummyBlock(dummy);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();

                if (hierarchyDummies == null) break;
            }

            DrawPendingAddDummyPlaceholders();
            DrawAddDummyButton();
        }

        // Render a placeholder block for each queued Add-Dummy entry so the
        // user can SEE what they've stacked before clicking Apply Changes.
        // Click ✕ on a placeholder to remove it from the queue.
        void DrawPendingAddDummyPlaceholders()
        {
            if (pendingAddDummies == null || pendingAddDummies.Count == 0) return;
            for (int i = 0; i < pendingAddDummies.Count; i++)
            {
                var pending = pendingAddDummies[i];
                if (pending == null) continue;

                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(HierarchyDummyIndent);

                var prevBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.80f, 0.55f, 1.0f);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUI.backgroundColor = prevBg;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"⋯ PENDING DUMMY  {pending.name}",
                    EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                GUI.backgroundColor = new Color(0.90f, 0.30f, 0.30f);
                if (GUILayout.Button(new GUIContent("✕",
                        "Discard this pending Dummy creation."),
                        EditorStyles.miniButton,
                        GUILayout.Width(22), GUILayout.Height(16)))
                {
                    pendingAddDummies.RemoveAt(i);
                    GUI.backgroundColor = prevBg;
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    EditorGUILayout.EndHorizontal();
                    requestRepaint?.Invoke();
                    return;
                }
                GUI.backgroundColor = prevBg;
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.LabelField(
                    "Empty container will be created on Apply Changes.",
                    EditorStyles.miniLabel);

                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
            }
        }

        // Queue an empty Dummy GameObject creation. The actual GameObject
        // lands on Apply Changes — until then nothing in the scene
        // changes; the queued count reads on the button label so the user
        // can see how many they've stacked.
        void DrawAddDummyButton()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(HierarchyDummyIndent);
            var bgc = GUI.backgroundColor;
            int pendingCount = pendingAddDummies?.Count ?? 0;
            GUI.backgroundColor = pendingCount > 0
                ? new Color(0.95f, 0.65f, 0.20f)  // amber — has queued
                : new Color(0.55f, 0.85f, 0.55f); // green — idle
            string label = pendingCount > 0
                ? $"+ Add Dummy group ({pendingCount} queued)"
                : "+ Add Dummy group";
            if (GUILayout.Button(
                    new GUIContent(label,
                        "Queue a new empty container under Root. Click multiple times to queue more. "
                        + "Created on Apply Changes; nothing in the scene changes until you click that. "
                        + "After commit, drop meshes into the dummy via Unity's Inspector or click "
                        + "+ Add LOD inside the new block."),
                    GUILayout.Height(20)))
                EnqueueAddEmptyDummy();
            GUI.backgroundColor = bgc;
            EditorGUILayout.EndHorizontal();
        }

        // ── Pending wrap / add Dummy queue helpers ──
        // Both operations are deferred until Apply Changes — clicking the
        // chain "→ Dummy" or the "+ Add Dummy group" button only enqueues
        // the action; the scene stays untouched until commit.

        bool IsChainWrapPending(HierarchyChain chain)
        {
            if (pendingWrapChains == null || chain == null) return false;
            foreach (var p in pendingWrapChains)
                if (p != null && string.Equals(p.baseName, chain.baseName, System.StringComparison.Ordinal))
                    return true;
            return false;
        }

        void ToggleChainWrapPending(HierarchyChain chain)
        {
            if (pendingWrapChains == null) pendingWrapChains = new List<PendingWrapChain>();
            for (int i = 0; i < pendingWrapChains.Count; i++)
            {
                var p = pendingWrapChains[i];
                if (p != null && string.Equals(p.baseName, chain.baseName, System.StringComparison.Ordinal))
                {
                    pendingWrapChains.RemoveAt(i);
                    requestRepaint?.Invoke();
                    return;
                }
            }
            // Capture renderer references at queue time so the commit step
            // can reparent them even after the dummy view is rebuilt.
            var renderers = new List<Renderer>();
            foreach (var lod in chain.rows)
                if (lod?.renderer != null) renderers.Add(lod.renderer);
            pendingWrapChains.Add(new PendingWrapChain
            {
                baseName  = chain.baseName,
                renderers = renderers,
            });
            requestRepaint?.Invoke();
        }

        void EnqueueAddEmptyDummy()
        {
            if (pendingAddDummies == null) pendingAddDummies = new List<PendingAddDummy>();
            // Pick a fresh placeholder name that won't collide with existing
            // children OR with already-queued pending adds. The user can
            // rename it via Apply Changes after the GameObject lands.
            var root = ctx?.LodGroup != null ? ctx.LodGroup.transform : null;
            int idx = 1;
            string name;
            while (true)
            {
                name = $"Dummy_{idx}";
                bool collides = false;
                if (root != null && root.Find(name) != null) collides = true;
                else
                {
                    foreach (var p in pendingAddDummies)
                        if (p != null && string.Equals(p.name, name, System.StringComparison.Ordinal))
                        { collides = true; break; }
                }
                if (!collides) break;
                idx++;
            }
            pendingAddDummies.Add(new PendingAddDummy { name = name });
            requestRepaint?.Invoke();
        }

        void DrawRootRow()
        {
            var rootGo = ctx.LodGroup.gameObject;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Root", EditorStyles.miniLabel, GUILayout.Width(34));
            DrawEditableNameField(rootGo, GUILayout.MinWidth(120));
            GUILayout.FlexibleSpace();

            // Apply Changes is the single commit point: pending name edits,
            // pending LOD inserts, pending LOD deletes, and leaf-name renumber
            // are all applied together. Disabled when nothing is queued so it's
            // visually obvious the prefab is already in sync.
            int pendingTotal =
                (pendingNames?.Count ?? 0)
                + (pendingInserts?.Count ?? 0)
                + (pendingDeleteRendererIds?.Count ?? 0)
                + (pendingWrapChains?.Count ?? 0)
                + (pendingAddDummies?.Count ?? 0);
            bool hasAny = pendingTotal > 0 || HasAnyStaleLeafName();
            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = hasAny
                ? new Color(0.40f, 0.80f, 0.40f)
                : new Color(0.55f, 0.55f, 0.55f);
            string label = hasAny ? $"Apply Changes ({pendingTotal})" : "Apply Changes";
            var tooltip = new GUIContent(label,
                "Commit every pending change at once:\n"
                + "  • Root / Dummy name edits\n"
                + "  • Pending LOD inserts (creates GameObject + simplifies mesh)\n"
                + "  • Pending LOD deletes\n"
                + "  • Renumber trailing _LOD{N} / _COL suffixes to match slots\n\n"
                + "Until you click this, the prefab itself is untouched.");
            using (new EditorGUI.DisabledScope(!hasAny))
            {
                if (GUILayout.Button(tooltip, GUILayout.Height(20), GUILayout.Width(160)))
                    ApplyChanges();
            }
            GUI.backgroundColor = bgc;
            EditorGUILayout.EndHorizontal();

            // Sanity warnings on root
            if (MeshHygieneUtility.HasLodOrColSuffix(rootGo.name))
                EditorGUILayout.HelpBox("Root name has LOD/COL suffix.", MessageType.Warning);
            var rootMf = rootGo.GetComponent<MeshFilter>();
            if (rootMf != null && rootMf.sharedMesh != null)
                EditorGUILayout.HelpBox("Root has mesh — should be empty pivot.", MessageType.Warning);
        }

        void DrawDummyBlock(HierarchyDummy dummy)
        {
            if (dummy == null || dummy.dummy == null) return;

            EditorGUILayout.Space(4);

            // Compute chains up front so the dummy header summary can include
            // chain-wide badges in the single-chain case (where no chain
            // foldout header is rendered to host them).
            var chains = GroupLodsByChain(dummy);
            bool useChainFoldouts = chains.Count > 1;

            // Tint the helpBox per Dummy via GUI.backgroundColor — green for the
            // implicit root group, blue for explicit Dummy containers. Visually
            // separates multiple Dummy groups in a busy hierarchy.
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = dummy.isRoot
                ? new Color(0.75f, 1.05f, 0.75f)
                : new Color(0.78f, 0.92f, 1.10f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            // Header: small foldout arrow + dummy name (editable when not root) + summary.
            EditorGUILayout.BeginHorizontal();
            var foldoutRect = GUILayoutUtility.GetRect(14, 16, GUILayout.Width(14), GUILayout.Height(16));
            dummy.foldout = EditorGUI.Foldout(foldoutRect, dummy.foldout, GUIContent.none, true);
            if (dummy.isRoot)
                EditorGUILayout.LabelField("Root group", EditorStyles.boldLabel, GUILayout.MinWidth(100));
            else
                DrawEditableNameField(dummy.dummy.gameObject, GUILayout.MinWidth(100));
            GUILayout.FlexibleSpace();
            string dummySummary = $"{dummy.lods.Count} LOD · {dummy.cols.Count} COL";
            // Single-chain dummies don't render a chain foldout header, so
            // the channel badges have to live on the dummy header instead.
            // Multi-chain dummies put their badges on each chain header
            // (different chains can have different channel sets).
            if (!useChainFoldouts && chains.Count == 1 && chains[0].rows.Count > 0)
            {
                string b = ChannelBadges(chains[0].rows[0].mesh);
                if (!string.IsNullOrEmpty(b))
                    dummySummary += "  ·  " + b;
            }
            EditorGUILayout.LabelField(dummySummary,
                EditorStyles.miniLabel, GUILayout.Width(170));
            EditorGUILayout.EndHorizontal();

            if (!dummy.foldout)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.Space(2);

            // Render any pending inserts that should appear BEFORE the first
            // existing LOD (afterRenderer == null).
            if (DrawPendingInsertsForAfter(dummy, null))
            {
                EditorGUILayout.EndVertical();
                return;
            }

            // LOD rows grouped by chain base name with a Unity Hierarchy-
            // style foldout per chain when there's more than one chain.
            // Foldout chevron sits at the dummy's content edge; chain rows
            // are indented one level deeper (14 px) to mirror Unity's
            // child-of-parent visual nesting. Single-chain dummies skip
            // the chain foldout entirely so the rows sit at the dummy
            // indent without an extra step.
            //
            // Insert buttons stay slot-scoped: clicking "+ Add LOD after
            // LOD{N}" in any chain queues a single PendingInsert that
            // materialises a new slot across all chains on Apply Changes.
            for (int chainIdx = 0; chainIdx < chains.Count; chainIdx++)
            {
                var chain = chains[chainIdx];
                bool chainOpen = true;
                Color prevChainBg = GUI.backgroundColor;
                if (useChainFoldouts)
                {
                    if (chainIdx > 0) EditorGUILayout.Space(2);
                    // Tint the chain container with the per-group palette
                    // entry — this is the visual identification the user
                    // asked for. helpBox texture multiplies with
                    // GUI.backgroundColor so a saturated chain colour reads
                    // as a soft pastel background covering the entire chain
                    // block (header + rows + insert buttons).
                    GUI.backgroundColor = MeshGroupColors.GetColor(chain.baseName);
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    GUI.backgroundColor = prevChainBg;
                    chainOpen = DrawChainFoldoutHeader(dummy, chain);
                }

                if (!chainOpen)
                {
                    if (useChainFoldouts) EditorGUILayout.EndVertical();
                    continue;
                }

                bool earlyReturn = false;
                if (useChainFoldouts)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(14f);
                    EditorGUILayout.BeginVertical();
                }
                for (int i = 0; i < chain.rows.Count; i++)
                {
                    if (i > 0) DrawRowDivider();
                    if (DrawLodRow(dummy, chain.rows[i])) { earlyReturn = true; break; }
                    if (DrawPendingInsertsForAfter(dummy, chain.rows[i].renderer))
                    { earlyReturn = true; break; }
                }
                // Single insert button at the bottom of the chain. Replaces
                // the inter-row "+" affordance which broke the visual
                // rhythm — three rows per LOD plus a "+" gap-row between
                // every pair turned the hierarchy into a noisy zig-zag.
                // Inserts after the last existing LOD; mid-chain insert is
                // out-of-scope for this view (rare operation).
                if (!earlyReturn && chain.rows.Count > 0)
                {
                    int lastLodIndex = chain.rows[chain.rows.Count - 1].lodIndex;
                    if (DrawAddLodButton(dummy, lastLodIndex))
                        earlyReturn = true;
                }
                if (useChainFoldouts)
                {
                    EditorGUILayout.EndVertical();
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                }
                if (earlyReturn)
                {
                    EditorGUILayout.EndVertical();
                    return;
                }
            }

            // No LODs yet — single Add at top
            if (dummy.lods.Count == 0)
            {
                if (DrawAddLodButton(dummy, -1))
                {
                    EditorGUILayout.EndVertical();
                    return;
                }
            }

            // COL rows live in their own tinted helpBox so they sit at the
            // same indent / right-pad as the chain blocks above. Wrapping
            // the section gives a visible border (separator from the last
            // LOD's "+" insert button) plus consistent left/right padding
            // — without the box, COL rows spread to the dummy helpBox
            // edges and the ✕ button ends up ~20 px right of the LOD ✕
            // cluster.
            if (dummy.cols.Count > 0)
            {
                EditorGUILayout.Space(4);
                var prevColBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.65f, 0.95f, 0.75f);  // soft mint
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUI.backgroundColor = prevColBg;

                bool colEarly = false;
                for (int i = 0; i < dummy.cols.Count; i++)
                {
                    if (i > 0) DrawRowDivider();
                    if (DrawColRow(dummy, dummy.cols[i], i, dummy.cols.Count))
                    {
                        colEarly = true;
                        break;
                    }
                }

                EditorGUILayout.EndVertical();
                if (colEarly)
                {
                    EditorGUILayout.EndVertical();  // dummy helpBox
                    return;
                }
            }

            // Hint when any child's _LOD/_COL suffix doesn't match its slot.
            if (DummyHasStale(dummy))
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.HelpBox(
                    "Trailing _LOD/_COL suffix doesn't match the slot. Apply Changes will renumber.",
                    MessageType.None);
            }

            EditorGUILayout.EndVertical();
        }

        const float HierarchyRowIndent = 10f;
        const float HierarchyDummyIndent = 18f;
        // Trailing right-margin for rows nested inside a chain helpBox.
        // The helpBox border + the left sidebar's vertical scrollbar both
        // eat width on the right; without an explicit pad sliders / "+"
        // buttons / etc. spill past the visible rect.
        const float ChainContentRightPad = 18f;

        // Returns true when the operation invalidates the UI for this frame.
        bool DrawLodRow(HierarchyDummy dummy, HierarchyLodRow lod)
        {
            if (lod == null || lod.renderer == null) return false;

            int rid = lod.renderer.GetInstanceID();
            bool markedDelete = pendingDeleteRendererIds != null && pendingDeleteRendererIds.Contains(rid);
            bool fresh = !markedDelete && freshRendererIds != null && freshRendererIds.Contains(rid);
            bool stale = !markedDelete && !fresh && IsLodRowStale(dummy, lod);
            bool regenerated = fresh && RowWasRegenerated(lod);

            int verts = lod.mesh != null ? lod.mesh.vertexCount : 0;
            int tris = lod.mesh != null ? MeshHygieneUtility.GetTriangleCount(lod.mesh) : 0;
            string stat = $"{verts:N0}v / {tris:N0}t";

            // Row A: marker + name + stats + actions. Stats moved inline so
            // we drop the dedicated mini-stats row that previously sat
            // between name and slider — the per-LOD entry collapses from 3
            // rows to 2. Channel badges (UV0·UV1·N·T …) are now shown once
            // on the chain header instead of repeated on every LOD row,
            // since channel layout is consistent across a chain.
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(HierarchyRowIndent);
            DrawStatusMarker(fresh, stale, markedDelete);

            string shortName = ShortLeafName(lod.renderer.gameObject.name);
            string nameLabel;
            if (markedDelete)
                nameLabel = "✗ DELETE  " + shortName;
            else if (fresh)
                nameLabel = "★ NEW  " + shortName;
            else if (stale)
                nameLabel = "⚠  " + shortName;
            else
                nameLabel = shortName;

            var prevBg = GUI.backgroundColor;
            if (markedDelete)
                GUI.backgroundColor = new Color(1.0f, 0.35f, 0.35f);    // red — pending delete
            else if (fresh)
                GUI.backgroundColor = new Color(1.0f, 0.55f, 0.10f);    // bright orange — just inserted/regen
            else if (stale)
                GUI.backgroundColor = new Color(0.95f, 0.78f, 0.30f);   // amber — name out of sync
            // Click on the name pings the renderer's GameObject in the
            // Hierarchy window so the user can locate it quickly. Name field
            // expands to absorb whatever width is left after stats + buttons.
            if (GUILayout.Button(nameLabel,
                    EditorStyles.textField,
                    GUILayout.MinWidth(70), GUILayout.ExpandWidth(true)))
            {
                EditorGUIUtility.PingObject(lod.renderer.gameObject);
            }
            GUI.backgroundColor = prevBg;

            EditorGUILayout.LabelField(stat, EditorStyles.miniLabel,
                GUILayout.Width(78));

            // Discard reverts a regenerate-in-place back to the import-time
            // fbxMesh. Only shown for rows whose mesh actually differs from
            // the FBX source (i.e. ones we know we modified this session).
            if (regenerated)
            {
                GUI.backgroundColor = new Color(0.85f, 0.65f, 0.30f);
                if (GUILayout.Button(new GUIContent("↶",
                        "Discard regenerate — restore the import-time FBX mesh on this LOD."),
                        GUILayout.Width(22), GUILayout.Height(18)))
                {
                    DiscardRegenerate(lod);
                    GUI.backgroundColor = prevBg;
                    EditorGUILayout.EndHorizontal();
                    return true;
                }
            }

            GUI.backgroundColor = new Color(0.60f, 0.75f, 0.90f);
            using (new EditorGUI.DisabledScope(markedDelete))
            {
                if (GUILayout.Button(new GUIContent("↻",
                        "Regenerate this LOD from LOD0 source with the current quality."),
                        GUILayout.Width(22), GUILayout.Height(18)))
                {
                    RegenerateLodWithQuality(dummy, lod);
                    GUI.backgroundColor = prevBg;
                    EditorGUILayout.EndHorizontal();
                    return true;
                }
            }

            // ✕ toggles a pending-delete mark instead of destroying the row
            // immediately. Apply Changes commits all marked rows together.
            GUI.backgroundColor = markedDelete
                ? new Color(0.55f, 0.85f, 0.55f)
                : new Color(0.90f, 0.30f, 0.30f);
            string xTooltip = markedDelete
                ? "Cancel pending delete (revert mark)."
                : "Mark this LOD for deletion. Applied on Apply Changes.";
            if (GUILayout.Button(new GUIContent(markedDelete ? "↶" : "✕", xTooltip),
                    GUILayout.Width(22), GUILayout.Height(18)))
            {
                if (markedDelete) pendingDeleteRendererIds.Remove(rid);
                else pendingDeleteRendererIds.Add(rid);
                requestRepaint?.Invoke();
                GUI.backgroundColor = prevBg;
                EditorGUILayout.EndHorizontal();
                return true;
            }
            GUI.backgroundColor = prevBg;
            GUILayout.Space(ChainContentRightPad);
            EditorGUILayout.EndHorizontal();

            // Row B: slider + inline value field. EditorGUILayout.Slider
            // draws both in a single coherent widget — switching to a
            // manual HorizontalSlider + DelayedFloatField pair caused the
            // value field to wrap to a new line in narrow sidebars (the
            // slider's ExpandWidth was greedy and didn't leave room for
            // the field). fieldWidth pins the value field to a
            // predictable 56 px so the right edge still aligns with the
            // [↻][✕] button cluster on row A.
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(HierarchyRowIndent);
            if (!lodQualitySliders.TryGetValue(rid, out var quality))
                quality = ComputeLodRatioFromTriangles(dummy, lod);
            float prevFieldW = EditorGUIUtility.fieldWidth;
            EditorGUIUtility.fieldWidth = 56f;
            float newQuality = EditorGUILayout.Slider(quality, 0.001f, 1f);
            EditorGUIUtility.fieldWidth = prevFieldW;
            if (Mathf.Abs(newQuality - quality) > 0.0001f)
                lodQualitySliders[rid] = newQuality;
            GUILayout.Space(ChainContentRightPad);
            EditorGUILayout.EndHorizontal();

            return false;
        }

        // Compact insert affordance between LOD rows. A single small "+"
        // mini-button centred via FlexibleSpace so it can never overflow
        // past the chain / dummy helpBox border (the previous design used
        // ExpandWidth rules on either side, which IMGUI happily extended
        // past the available rect — clipping the button on narrow
        // sidebars). Click enqueues a slot-scoped pending insert.
        // Returns true on click (UI invalidated by insertion).
        bool DrawAddLodButton(HierarchyDummy dummy, int afterLodIndex)
        {
            const float btnW = 36f;
            const float btnH = 14f;

            string tip = afterLodIndex < 0
                ? "Click to queue an insert at the start of this group. Commits on Apply Changes."
                : $"Click to queue an insert after LOD{afterLodIndex}. Commits on Apply Changes.";

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(HierarchyRowIndent);
            GUILayout.FlexibleSpace();

            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.55f, 0.75f, 0.95f);
            bool clicked = GUILayout.Button(new GUIContent("+", tip),
                EditorStyles.miniButton,
                GUILayout.Width(btnW), GUILayout.Height(btnH));
            GUI.backgroundColor = bgc;

            GUILayout.FlexibleSpace();
            GUILayout.Space(ChainContentRightPad);
            EditorGUILayout.EndHorizontal();

            if (clicked)
            {
                EnqueuePendingInsert(afterLodIndex);
                return true;
            }
            return false;
        }

        // ── Pending insert row ──
        // Render every pending insert whose afterLodIndex matches the slot
        // currently occupied by the given anchor renderer. Pending entries
        // are slot-scoped, so the row is shown in EVERY Dummy block at the
        // same slot — that's the visible counterpart of the synced commit
        // (every dummy gets a renderer at the new slot on Apply Changes).
        // Returns true when a row mutated state (UI invalidated).
        bool DrawPendingInsertsForAfter(HierarchyDummy dummy, Renderer afterRenderer)
        {
            if (pendingInserts == null || pendingInserts.Count == 0) return false;
            int anchorSlot = -1;
            if (afterRenderer != null)
            {
                // Anchor is one of THIS dummy's renderers — its lodIndex was
                // already cached on the matching HierarchyLodRow, but find it
                // explicitly here so the helper is self-contained.
                foreach (var lr in dummy.lods)
                {
                    if (lr == null || lr.renderer != afterRenderer) continue;
                    anchorSlot = lr.lodIndex;
                    break;
                }
                if (anchorSlot < 0) return false;
            }
            for (int i = 0; i < pendingInserts.Count; i++)
            {
                var p = pendingInserts[i];
                if (p == null) continue;
                if (p.afterLodIndex != anchorSlot) continue;
                DrawRowDivider();
                if (DrawPendingInsertRow(p))
                {
                    pendingInserts.RemoveAt(i);
                    requestRepaint?.Invoke();
                    return true;
                }
            }
            return false;
        }

        // Returns true on cancel (UI invalidated).
        bool DrawPendingInsertRow(PendingInsert pending)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(HierarchyRowIndent);

            // Marker — bright violet so pending rows are unmistakable.
            const float markerW = 6f;
            const float markerH = 18f;
            var rect = GUILayoutUtility.GetRect(markerW, markerH,
                GUILayout.Width(markerW), GUILayout.Height(markerH));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(rect.x, rect.y + 2, markerW - 1, markerH - 4),
                    new Color(0.65f, 0.30f, 0.95f));

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.80f, 0.55f, 1.0f);
            EditorGUILayout.LabelField($"⋯ PENDING LOD  (insert on Apply Changes)",
                EditorStyles.textField, GUILayout.MinWidth(120));
            GUI.backgroundColor = prevBg;

            GUILayout.FlexibleSpace();
            GUI.backgroundColor = new Color(0.90f, 0.30f, 0.30f);
            bool cancelled = GUILayout.Button(
                new GUIContent("✕", "Discard this pending LOD insert."),
                GUILayout.Width(22), GUILayout.Height(18));
            GUI.backgroundColor = prevBg;
            GUILayout.Space(ChainContentRightPad);
            EditorGUILayout.EndHorizontal();

            if (cancelled) return true;

            // Quality slider — live editable so the user can preview the
            // chosen ratio in the row before committing. Single-widget
            // layout (matches the existing-LOD slider row) so the value
            // field stays inline with the slider track instead of wrapping
            // to a second line on narrow sidebars.
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(HierarchyRowIndent);
            float prevFieldW = EditorGUIUtility.fieldWidth;
            EditorGUIUtility.fieldWidth = 56f;
            pending.quality = Mathf.Clamp(EditorGUILayout.Slider(
                Mathf.Clamp(pending.quality, 0.001f, 1f), 0.001f, 1f),
                0.001f, 1f);
            EditorGUIUtility.fieldWidth = prevFieldW;
            GUILayout.Space(ChainContentRightPad);
            EditorGUILayout.EndHorizontal();

            return false;
        }

        // Enqueue a pending insert for this dummy. Captures the renderer that
        // it should follow so the slot index can be recomputed accurately at
        // Apply time even after structural shifts. Default quality = half of
        // the average previous LOD slider across all dummies that share this
        // slot (so multi-dummy prefabs get a sensible starting ratio without
        // privileging the dummy where the click happened).
        void EnqueuePendingInsert(int afterLodIndex)
        {
            if (pendingInserts == null) pendingInserts = new List<PendingInsert>();
            float defaultQ = 0.5f;
            // Pick any renderer at the anchor slot as the live anchor. The
            // commit step resolves the actual slot index from this renderer's
            // CURRENT position in the LODGroup, so deletes earlier in the
            // same Apply batch shift the insert into the right slot.
            Renderer afterRenderer = null;
            if (afterLodIndex >= 0 && hierarchyDummies != null && lodQualitySliders != null)
            {
                int samples = 0;
                float sum = 0f;
                foreach (var dummy in hierarchyDummies)
                {
                    if (dummy?.lods == null) continue;
                    foreach (var lr in dummy.lods)
                    {
                        if (lr == null || lr.renderer == null) continue;
                        if (lr.lodIndex != afterLodIndex) continue;
                        if (afterRenderer == null) afterRenderer = lr.renderer;
                        if (lodQualitySliders.TryGetValue(lr.renderer.GetInstanceID(), out var q))
                        { sum += q; samples++; }
                    }
                }
                if (samples > 0) defaultQ = Mathf.Max(0.01f, (sum / samples) * 0.5f);
            }
            pendingInserts.Add(new PendingInsert
            {
                afterLodIndex = afterLodIndex,
                afterRenderer = afterRenderer,
                quality       = defaultQ
            });
            requestRepaint?.Invoke();
        }

        bool DrawColRow(HierarchyDummy dummy, HierarchyColRow col, int index, int totalCols)
        {
            if (col == null || col.colTransform == null) return false;

            // Component-only rows (collider on the dummy itself) are never
            // renamed — the host GameObject is the dummy, not a leaf.
            bool stale = !col.isComponentOnly && IsColRowStale(dummy, col, index, totalCols);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(HierarchyRowIndent);
            DrawStatusMarker(false, stale);

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = stale
                ? new Color(0.95f, 0.78f, 0.30f)
                : new Color(0.55f, 0.95f, 0.70f);
            string colShort = ShortLeafName(col.colTransform.gameObject.name);
            string display = col.isComponentOnly
                ? $"{col.colTransform.gameObject.name}  [{col.typeLabel}]"
                : colShort;
            // Click pings the host GameObject so the user can jump to it.
            if (GUILayout.Button(display, EditorStyles.textField, GUILayout.MinWidth(120)))
            {
                Object pingTarget = col.isComponentOnly && col.collider != null
                    ? (Object)col.collider
                    : col.colTransform.gameObject;
                EditorGUIUtility.PingObject(pingTarget);
            }
            GUI.backgroundColor = prevBg;

            EditorGUILayout.LabelField(BuildColStatLabel(col),
                EditorStyles.miniLabel, GUILayout.Width(180));

            GUILayout.FlexibleSpace();
            string removeTooltip = col.isComponentOnly
                ? $"Remove the {col.typeLabel}Collider component from '{col.colTransform.name}'."
                : "Remove this collision GameObject.";
            GUI.backgroundColor = new Color(0.90f, 0.30f, 0.30f);
            if (GUILayout.Button(new GUIContent("✕", removeTooltip),
                    GUILayout.Width(22), GUILayout.Height(18)))
            {
                DeleteCol(dummy, col);
                GUI.backgroundColor = prevBg;
                GUILayout.Space(ChainContentRightPad);
                EditorGUILayout.EndHorizontal();
                return true;
            }
            GUI.backgroundColor = prevBg;
            GUILayout.Space(ChainContentRightPad);
            EditorGUILayout.EndHorizontal();

            return false;
        }

        static string BuildColStatLabel(HierarchyColRow col)
        {
            if (col.collider is MeshCollider)
            {
                int verts = col.mesh != null ? col.mesh.vertexCount : 0;
                int tris = col.mesh != null ? MeshHygieneUtility.GetTriangleCount(col.mesh) : 0;
                string meshName = col.mesh != null ? col.mesh.name : "(none)";
                return $"Mesh  {verts:N0}v / {tris:N0}t  · {meshName}";
            }
            if (col.collider is BoxCollider bc)
                return $"Box  {bc.size.x:F2}×{bc.size.y:F2}×{bc.size.z:F2}";
            if (col.collider is CapsuleCollider cc)
                return $"Capsule  r={cc.radius:F2}  h={cc.height:F2}";
            if (col.collider is SphereCollider sc)
                return $"Sphere  r={sc.radius:F2}";
            if (col.collider != null)
                return col.typeLabel ?? col.collider.GetType().Name;
            int v = col.mesh != null ? col.mesh.vertexCount : 0;
            int t = col.mesh != null ? MeshHygieneUtility.GetTriangleCount(col.mesh) : 0;
            return $"COL  {v:N0}v / {t:N0}t";
        }

        // Editable text field bound to pendingNames keyed by GameObject instanceID.
        // Callers receive a draggable change indicator on the right of the input.
        void DrawEditableNameField(GameObject go, params GUILayoutOption[] options)
        {
            if (go == null) return;
            int id = go.GetInstanceID();
            if (!pendingNames.TryGetValue(id, out string editName))
                editName = go.name;

            string newName = EditorGUILayout.TextField(editName, options);
            if (newName != editName)
            {
                if (newName != go.name) pendingNames[id] = newName;
                else pendingNames.Remove(id);
            }

            if (pendingNames.ContainsKey(id))
            {
                var dot = EditorGUILayout.GetControlRect(false, 14, GUILayout.Width(14));
                EditorGUI.DrawRect(new Rect(dot.x + 2, dot.y + 2, 10, 10),
                    new Color(1f, 0.7f, 0.2f));
            }
        }

        // ── Hierarchy view rebuild ──

        void RebuildHierarchyView()
        {
            hierarchyDummies = new List<HierarchyDummy>();
            if (ctx == null || ctx.LodGroup == null) return;

            var root = ctx.LodGroup.transform;
            var lookup = new Dictionary<Transform, HierarchyDummy>();

            // Group LOD renderers by their parent transform. A parent that equals
            // root means the prefab is flat (no separate Dummy container) — in
            // that case the root acts as the implicit single Dummy.
            var lods = ctx.LodGroup.GetLODs();
            for (int li = 0; li < lods.Length; li++)
            {
                if (lods[li].renderers == null) continue;
                foreach (var r in lods[li].renderers)
                {
                    if (r == null) continue;
                    var parent = r.transform.parent;
                    if (parent == null) continue;
                    Transform key = parent == root ? root : parent;
                    var dummy = GetOrCreateDummy(lookup, key, root);
                    var mf = r.GetComponent<MeshFilter>();
                    dummy.lods.Add(new HierarchyLodRow
                    {
                        lodIndex = li,
                        renderer = r,
                        mesh = mf != null ? mf.sharedMesh : null
                    });
                }
            }

            // Two collision sources flow into each dummy block:
            //  1) Collider components attached to the dummy/root GameObject itself
            //     (e.g. a MeshCollider on the prefab root pointing at a *_COL mesh
            //     asset). These are "component-only" rows.
            //  2) Standalone _COL named child GameObjects (legacy convention).
            // The first is the case the user just flagged — meshes referenced by a
            // root-level MeshCollider were invisible in the tree.
            foreach (var dummy in lookup.Values)
            {
                if (dummy.dummy == null) continue;
                foreach (var c in dummy.dummy.GetComponents<Collider>())
                {
                    if (c == null) continue;
                    Mesh m = null;
                    if (c is MeshCollider mc) m = mc.sharedMesh;
                    dummy.cols.Add(new HierarchyColRow
                    {
                        colTransform   = dummy.dummy,
                        collider       = c,
                        mesh           = m,
                        typeLabel      = ColliderTypeLabel(c),
                        isComponentOnly = true,
                    });
                }
            }

            foreach (var colGo in MeshHygieneUtility.FindCollisionObjects(root))
            {
                if (colGo == null) continue;
                var colT = colGo.transform;
                var parent = colT.parent;
                if (parent == null) continue;
                Transform key = parent == root ? root : parent;
                var dummy = GetOrCreateDummy(lookup, key, root);
                var mf = colGo.GetComponent<MeshFilter>();
                var c = colGo.GetComponent<Collider>();
                dummy.cols.Add(new HierarchyColRow
                {
                    colTransform   = colT,
                    collider       = c,
                    mesh           = (c is MeshCollider mc2) ? mc2.sharedMesh
                                       : (mf != null ? mf.sharedMesh : null),
                    typeLabel      = c != null ? ColliderTypeLabel(c) : "Mesh",
                    isComponentOnly = false,
                });
            }

            // Pick up empty container transforms under root that aren't
            // already represented (no LOD renderers, no _COL leaf). These
            // are Dummies the user just added via "+ Add Dummy" — keep
            // them in the view so the user can rename them, populate
            // them, or remove them.
            var colSetForEmpty = new HashSet<GameObject>(MeshHygieneUtility.FindCollisionObjects(root));
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child == null) continue;
                if (lookup.ContainsKey(child)) continue;
                if (colSetForEmpty.Contains(child.gameObject)) continue;
                if (child.GetComponent<MeshFilter>() != null) continue;
                lookup[child] = new HierarchyDummy
                {
                    dummy = child,
                    isRoot = false
                };
            }

            hierarchyDummies = lookup.Values.ToList();
            hierarchyDummies.Sort(CompareDummies);
            foreach (var d in hierarchyDummies)
                d.lods.Sort((a, b) => a.lodIndex.CompareTo(b.lodIndex));
        }

        static HierarchyDummy GetOrCreateDummy(Dictionary<Transform, HierarchyDummy> lookup,
            Transform key, Transform root)
        {
            if (!lookup.TryGetValue(key, out var dummy))
            {
                dummy = new HierarchyDummy
                {
                    dummy = key,
                    isRoot = key == root
                };
                lookup[key] = dummy;
            }
            return dummy;
        }

        static string ColliderTypeLabel(Collider c)
        {
            switch (c)
            {
                case MeshCollider _:    return "Mesh";
                case BoxCollider _:     return "Box";
                case CapsuleCollider _: return "Capsule";
                case SphereCollider _:  return "Sphere";
                case WheelCollider _:   return "Wheel";
                case TerrainCollider _: return "Terrain";
                default:                return c.GetType().Name.Replace("Collider", "");
            }
        }

        static int CompareDummies(HierarchyDummy a, HierarchyDummy b)
        {
            if (a.isRoot && !b.isRoot) return -1;
            if (!a.isRoot && b.isRoot) return 1;
            return string.Compare(a.dummy.name, b.dummy.name,
                System.StringComparison.OrdinalIgnoreCase);
        }

        // ── Apply Changes: single commit point for every pending edit. ──
        // Order matters:
        //   1) Root/Dummy renames (so subsequent leaf-name rebuilds use the
        //      new prefix).
        //   2) Pending deletes (frees up slots before inserts re-index).
        //   3) Pending inserts (creates GameObjects + simplifies meshes;
        //      slot index recomputed from the captured afterRenderer's
        //      current LOD position).
        //   4) Leaf rename rebuild ("<base>_LOD{slot}").
        // All four happen inside a single Undo group so Ctrl+Z reverts the
        // whole batch.
        //
        // The freshRendererIds set is intentionally NOT cleared here — the
        // ★ NEW highlight should persist until the user selects a different
        // prefab so they can still spot rows they added this session.

        void ApplyChanges()
        {
            if (ctx == null || ctx.LodGroup == null) return;

            Undo.SetCurrentGroupName("Prefab Builder: Apply Changes");
            int undoGroup = Undo.GetCurrentGroup();

            // 1) Root / Dummy renames.
            if (pendingNames != null)
            {
                foreach (var kvp in pendingNames)
                {
                    var go = EditorUtility.InstanceIDToObject(kvp.Key) as GameObject;
                    if (go == null || go.name == kvp.Value) continue;
                    Undo.RecordObject(go, "Rename");
                    UvtLog.Info($"[LightmapUV] Renamed: {go.name} → {kvp.Value}");
                    go.name = kvp.Value;
                }
                pendingNames.Clear();
            }

            // 2) Wrap chains and add empty Dummies BEFORE inserts/deletes
            //    so insert anchors that reference the wrapped renderers
            //    still resolve to the right slot afterwards.
            if (pendingWrapChains != null && pendingWrapChains.Count > 0)
                CommitPendingWrapChains();
            if (pendingAddDummies != null && pendingAddDummies.Count > 0)
                CommitPendingAddDummies();

            // 3) Pending deletes — process highest LOD index first to keep
            //    earlier indices stable during the loop.
            if (pendingDeleteRendererIds != null && pendingDeleteRendererIds.Count > 0)
                CommitPendingDeletes();

            // 4) Pending inserts — recompute target slot index from each
            //    captured afterRenderer's CURRENT slot, so inserts after
            //    deletions land at the right place.
            if (pendingInserts != null && pendingInserts.Count > 0)
                CommitPendingInserts();

            // 5) Leaf rename rebuild.
            hierarchyDummies = null;
            RebuildLeafNamesNoUndoGroup();

            Undo.CollapseUndoOperations(undoGroup);
            buildIntent |= FbxExportIntent.Hierarchy;

            ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        // If the LODGroup root is part of a prefab instance, fully unpack it
        // — otherwise SetTransformParent on prefab-instance children is a
        // no-op and structural changes silently fail (which is exactly the
        // bug that surfaced when "→ Dummy" wrapped without reparenting).
        void EnsurePrefabUnpackedForStructuralEdit()
        {
            if (ctx?.LodGroup == null) return;
            var lgGo = ctx.LodGroup.gameObject;
            if (PrefabUtility.IsPartOfPrefabInstance(lgGo))
            {
                var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(lgGo);
                if (outer != null)
                {
                    UvtLog.Info($"[LightmapUV] Unpacking prefab instance '{outer.name}' before structural edit.");
                    PrefabUtility.UnpackPrefabInstance(outer,
                        PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                }
            }
        }

        void CommitPendingWrapChains()
        {
            if (ctx?.LodGroup == null || pendingWrapChains == null) return;
            EnsurePrefabUnpackedForStructuralEdit();
            var root = ctx.LodGroup.transform;
            foreach (var pending in pendingWrapChains)
            {
                if (pending == null || pending.renderers == null || pending.renderers.Count == 0) continue;
                string dummyName = pending.baseName;
                if (root.Find(dummyName) != null)
                {
                    int n = 1;
                    while (root.Find($"{dummyName}_{n}") != null) n++;
                    dummyName = $"{dummyName}_{n}";
                }
                var newDummy = new GameObject(dummyName);
                Undo.RegisterCreatedObjectUndo(newDummy, "Wrap chain in Dummy");
                newDummy.transform.SetParent(root, false);
                int reparented = 0;
                foreach (var r in pending.renderers)
                {
                    if (r == null) continue;
                    Undo.SetTransformParent(r.transform, newDummy.transform, "Wrap LOD into Dummy");
                    reparented++;
                }
                UvtLog.Info($"[LightmapUV] Wrapped chain '{pending.baseName}' in Dummy '{dummyName}' "
                    + $"({reparented} renderer(s) reparented).");
            }
            pendingWrapChains.Clear();
            buildIntent |= FbxExportIntent.Hierarchy;
        }

        void CommitPendingAddDummies()
        {
            if (ctx?.LodGroup == null || pendingAddDummies == null) return;
            EnsurePrefabUnpackedForStructuralEdit();
            var root = ctx.LodGroup.transform;
            foreach (var pending in pendingAddDummies)
            {
                if (pending == null) continue;
                string name = pending.name;
                if (string.IsNullOrEmpty(name)) name = "Dummy";
                if (root.Find(name) != null)
                {
                    int n = 1;
                    while (root.Find($"{name}_{n}") != null) n++;
                    name = $"{name}_{n}";
                }
                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Add Dummy");
                go.transform.SetParent(root, false);
                UvtLog.Info($"[LightmapUV] Added empty Dummy '{name}' under '{root.name}'.");
            }
            pendingAddDummies.Clear();
            buildIntent |= FbxExportIntent.Hierarchy;
        }

        void CommitPendingDeletes()
        {
            if (ctx?.LodGroup == null) return;
            if (pendingDeleteRendererIds == null) return;

            // Collect the renderers and their slot indices. Process from the
            // highest LOD slot down so removing entries doesn't invalidate
            // the indices of the remaining ones.
            var lodsBefore = ctx.LodGroup.GetLODs();
            var queue = new List<(int slot, Renderer renderer)>();
            for (int li = 0; li < lodsBefore.Length; li++)
            {
                if (lodsBefore[li].renderers == null) continue;
                foreach (var r in lodsBefore[li].renderers)
                {
                    if (r == null) continue;
                    if (pendingDeleteRendererIds.Contains(r.GetInstanceID()))
                        queue.Add((li, r));
                }
            }
            queue.Sort((a, b) => b.slot.CompareTo(a.slot));

            foreach (var (slot, renderer) in queue)
            {
                var lods = ctx.LodGroup.GetLODs();
                if (slot < 0 || slot >= lods.Length) continue;
                var slotData = lods[slot];
                var remaining = slotData.renderers != null
                    ? new List<Renderer>(slotData.renderers) : new List<Renderer>();
                remaining.Remove(renderer);

                if (remaining.Count > 0)
                {
                    lods[slot] = new LOD(slotData.screenRelativeTransitionHeight, remaining.ToArray());
                    LodGroupUtility.ApplyLods(ctx.LodGroup, lods);
                }
                else
                {
                    if (lods.Length <= 1)
                    {
                        UvtLog.Warn("[LightmapUV] Skipping delete that would empty the LODGroup.");
                        continue;
                    }
                    var newLods = new LOD[lods.Length - 1];
                    for (int i = 0, j = 0; i < lods.Length; i++)
                    {
                        if (i == slot) continue;
                        newLods[j++] = lods[i];
                    }
                    LodGroupUtility.ApplyLods(ctx.LodGroup, newLods);
                }

                if (renderer != null && renderer.gameObject != null)
                {
                    UvtLog.Info($"[LightmapUV] Deleted '{renderer.name}' from slot {slot}.");
                    Undo.DestroyObjectImmediate(renderer.gameObject);
                }
            }

            pendingDeleteRendererIds.Clear();
            buildIntent |= FbxExportIntent.Hierarchy | FbxExportIntent.LodGroup;
            ctx.LodGroup.RecalculateBounds();
            ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;
        }

        void CommitPendingInserts()
        {
            if (ctx?.LodGroup == null) return;
            if (pendingInserts == null) return;

            // Apply in REVERSE click order so earlier pendings still land in
            // their intended position even after later pendings shift slot
            // numbers. Slot index is re-resolved from each pending's live
            // afterRenderer anchor; if the anchor was deleted earlier in the
            // same Apply batch, fall back to the captured afterLodIndex
            // clamped to the current LOD count.
            for (int i = pendingInserts.Count - 1; i >= 0; i--)
            {
                var pending = pendingInserts[i];
                if (pending == null) continue;
                int targetIdx = ResolvePendingInsertSlot(pending);
                InsertSlotAtIndex(targetIdx, pending.quality);
            }
            pendingInserts.Clear();
        }

        // Re-resolve the slot a pending insert should land in, against the
        // live LODGroup. Prefers the captured afterRenderer's current slot
        // (handles deletes-before-inserts in the same Apply batch); falls
        // back to the originally captured afterLodIndex when the anchor was
        // destroyed mid-batch.
        int ResolvePendingInsertSlot(PendingInsert pending)
        {
            if (pending == null || ctx?.LodGroup == null) return 0;
            if (pending.afterRenderer != null)
            {
                var lods = ctx.LodGroup.GetLODs();
                for (int slot = 0; slot < lods.Length; slot++)
                {
                    var rs = lods[slot].renderers;
                    if (rs == null) continue;
                    foreach (var r in rs)
                        if (r == pending.afterRenderer)
                            return slot + 1;
                }
            }
            int currentLodCount = ctx.LodGroup.GetLODs().Length;
            return Mathf.Clamp(pending.afterLodIndex + 1, 0, currentLodCount);
        }

        bool HasAnyStaleLeafName()
        {
            if (hierarchyDummies == null) RebuildHierarchyView();
            if (hierarchyDummies == null) return false;
            foreach (var d in hierarchyDummies)
                if (DummyHasStale(d)) return true;
            return false;
        }

        // ── LOD row operations ──

        void RegenerateLodWithQuality(HierarchyDummy dummy, HierarchyLodRow lod)
        {
            if (ctx.LodGroup == null || dummy == null || lod == null || lod.renderer == null)
                return;

            int rid = lod.renderer.GetInstanceID();
            float quality = lodQualitySliders.TryGetValue(rid, out var q) ? q
                : Mathf.Pow(0.5f, lod.lodIndex);

            // LOD0 source = matching renderer in this dummy's LOD0 row, or fall back
            // to the first LOD0 renderer in the LODGroup. We match by stripped group
            // key so renames mid-pipeline don't break the link.
            var sourceMesh = ResolveLodSourceMesh(dummy, lod);
            if (sourceMesh == null)
            {
                UvtLog.Warn($"[LightmapUV] Regenerate LOD{lod.lodIndex}: no source mesh.");
                return;
            }

            // Pull simplifier weights from the right-sidebar Settings panel
            // (LodGenerationTool's tunables). Falls back to a sensible default
            // when the Hub / tool isn't available.
            var settings = ResolveSimplifierSettings(quality);

            var res = MeshSimplifier.Simplify(sourceMesh, settings);
            if (!res.ok)
            {
                UvtLog.Warn($"[LightmapUV] Simplify failed for '{sourceMesh.name}': {res.error}");
                return;
            }
            res.simplifiedMesh.name = sourceMesh.name + "_LOD" + lod.lodIndex;

            var mf = lod.renderer.GetComponent<MeshFilter>();
            if (mf == null) return;
            // Snapshot the original mesh BEFORE swapping. ctx.Refresh below
            // would otherwise rewrite MeshEntry.fbxMesh to point at the
            // simplified mesh, defeating the Discard affordance the next
            // time the user wants to revert.
            if (regenBackupMeshes != null
                && !regenBackupMeshes.ContainsKey(rid)
                && mf.sharedMesh != null)
                regenBackupMeshes[rid] = mf.sharedMesh;

            Undo.RecordObject(mf, "Regenerate LOD");
            mf.sharedMesh = res.simplifiedMesh;

            // Drop the stored slider value so the row recomputes its ratio
            // from the new polygon counts on the next paint; otherwise the
            // slider keeps showing the requested quality even when the
            // simplifier missed the target.
            lodQualitySliders?.Remove(rid);
            freshRendererIds?.Add(rid);

            UvtLog.Info($"[LightmapUV] Regenerated LOD{lod.lodIndex} on '{lod.renderer.name}': "
                + $"{res.originalTriCount} → {res.simplifiedTriCount} tris (target {quality:P0})");

            buildIntent |= FbxExportIntent.AnyUv | FbxExportIntent.Normals
                | FbxExportIntent.Tangents | FbxExportIntent.VertexColors;

            ctx.LodGroup.RecalculateBounds();
            ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        Mesh ResolveLodSourceMesh(HierarchyDummy dummy, HierarchyLodRow lod)
        {
            // Always prefer the import-time fbxMesh from MeshEntries over the
            // currently-bound sharedMesh. Otherwise re-running simplify on
            // LOD0 would replace its mesh with a degraded copy and every
            // downstream regenerate (LOD1 → LODN) would start from that
            // already-simplified geometry instead of the original FBX.
            string key = lod != null && lod.renderer != null
                ? UvToolContext.ExtractGroupKey(lod.renderer.name)
                : null;
            if (key != null)
            {
                foreach (var candidate in dummy.lods)
                {
                    if (candidate == lod) continue;
                    if (candidate.lodIndex != 0) continue;
                    if (candidate.mesh == null) continue;
                    string ck = UvToolContext.ExtractGroupKey(candidate.renderer != null ? candidate.renderer.name : "");
                    if (string.Equals(ck, key, System.StringComparison.OrdinalIgnoreCase))
                        return PreferOriginalMesh(candidate.renderer, candidate.mesh);
                }
            }
            // For LOD0 → reuse the renderer's own mesh, but always go through
            // the original-mesh lookup so we don't simplify a simplified mesh.
            if (lod != null && lod.lodIndex == 0)
                return PreferOriginalMesh(lod.renderer, lod.mesh);
            // Fallback 1: first LOD0 mesh in the dummy.
            foreach (var candidate in dummy.lods)
                if (candidate.lodIndex == 0 && candidate.mesh != null)
                    return PreferOriginalMesh(candidate.renderer, candidate.mesh);
            // Fallback 2: empty dummies (just added via "+ Add Dummy" with
            // no children yet) borrow LOD0 from the first sibling dummy
            // that has one — gives the user a working seed they can swap
            // out later via the inspector.
            if (hierarchyDummies != null)
            {
                foreach (var sibling in hierarchyDummies)
                {
                    if (sibling == null || sibling == dummy) continue;
                    foreach (var candidate in sibling.lods)
                        if (candidate.lodIndex == 0 && candidate.mesh != null)
                            return PreferOriginalMesh(candidate.renderer, candidate.mesh);
                }
            }
            return null;
        }

        // Look up the import-time mesh (fbxMesh) for a renderer via the
        // shared MeshEntries cache. Falls back to the supplied current
        // sharedMesh when the entry doesn't carry an FBX-source reference.
        Mesh PreferOriginalMesh(Renderer r, Mesh fallback)
        {
            if (r == null || ctx?.MeshEntries == null) return fallback;
            foreach (var e in ctx.MeshEntries)
            {
                if (e.renderer != r) continue;
                return e.fbxMesh ?? e.originalMesh ?? fallback;
            }
            return fallback;
        }

        // Insert a new LOD slot at index targetIdx and create a simplified
        // renderer in EVERY Dummy that has a LOD0 source mesh. Synced inserts
        // keep multi-Dummy prefabs intact — Stove_Cap doesn't silently drop
        // out at the new camera distance just because the user clicked
        // "+ Add LOD" on the Stove_Base block.
        void InsertSlotAtIndex(int insertIndex, float requestedQuality)
        {
            if (ctx?.LodGroup == null) return;

            Undo.SetCurrentGroupName("Prefab Builder: Insert LOD slot");
            int undoGroup = Undo.GetCurrentGroup();

            // Compact pre-existing empty slots before splicing so the new slot
            // index doesn't get pushed past a phantom gap.
            UvToolContext.CompactLodArray(ctx.LodGroup, removeEmptySlots: true);
            if (hierarchyDummies == null) RebuildHierarchyView();

            var lods = ctx.LodGroup.GetLODs();
            int targetIdx = Mathf.Clamp(insertIndex, 0, lods.Length);

            float prevTrans = targetIdx > 0
                ? lods[targetIdx - 1].screenRelativeTransitionHeight : 1f;
            float nextTrans = targetIdx < lods.Length
                ? lods[targetIdx].screenRelativeTransitionHeight : 0.01f;
            float newTrans = Mathf.Max(0.01f, (prevTrans + nextTrans) * 0.5f);

            float quality = requestedQuality > 0f ? requestedQuality : 0.5f;

            var newRenderers = new List<Renderer>();
            int totalOrigTris = 0;
            int totalSimplTris = 0;

            foreach (var dummy in hierarchyDummies)
            {
                if (dummy == null || dummy.dummy == null) continue;

                var sourceMesh = ResolveLodSourceMesh(dummy,
                    dummy.lods.Count > 0 ? dummy.lods[0] : new HierarchyLodRow { lodIndex = 0 });
                if (sourceMesh == null) continue;

                var settings = ResolveSimplifierSettings(quality);
                var res = MeshSimplifier.Simplify(sourceMesh, settings);
                if (!res.ok)
                {
                    UvtLog.Warn($"[LightmapUV] Insert slot simplify failed for '{sourceMesh.name}': {res.error}");
                    continue;
                }

                // Pick a base name for the new mesh + GameObject:
                //   1) prefer this dummy's existing LOD0 chain base — keeps
                //      naming stable when inserting into a populated chain;
                //   2) fall back to the dummy GameObject's name when the
                //      dummy is empty (just added via "+ Add Dummy") and
                //      it isn't the implicit Root group;
                //   3) last resort, use the source mesh's stripped name.
                string baseName = null;
                foreach (var existing in dummy.lods)
                {
                    if (existing?.renderer == null) continue;
                    if (existing.lodIndex != 0) continue;
                    string b = UvToolContext.ExtractGroupKey(existing.renderer.name);
                    if (!string.IsNullOrEmpty(b)) { baseName = b; break; }
                }
                if (string.IsNullOrEmpty(baseName) && dummy.dummy != null && !dummy.isRoot)
                    baseName = dummy.dummy.name;
                if (string.IsNullOrEmpty(baseName))
                    baseName = UvToolContext.ExtractGroupKey(sourceMesh.name);
                if (string.IsNullOrEmpty(baseName)) baseName = sourceMesh.name;
                res.simplifiedMesh.name = baseName + "_LOD" + targetIdx;

                Transform parent = dummy.dummy != null ? dummy.dummy : ctx.LodGroup.transform;
                var go = new GameObject(baseName + "_LOD" + targetIdx);
                Undo.RegisterCreatedObjectUndo(go, "Insert LOD slot");
                go.transform.SetParent(parent, false);

                // Sibling-position the new GO right after this dummy's
                // previous-LOD sibling (or before the next-LOD sibling) so the
                // scene hierarchy mirrors the LODGroup slot order.
                int desiredSibling = -1;
                if (targetIdx > 0)
                {
                    foreach (var candidate in dummy.lods)
                    {
                        if (candidate?.renderer == null) continue;
                        if (candidate.lodIndex != targetIdx - 1) continue;
                        if (candidate.renderer.transform.parent != parent) continue;
                        desiredSibling = candidate.renderer.transform.GetSiblingIndex() + 1;
                        break;
                    }
                }
                if (desiredSibling < 0)
                {
                    foreach (var candidate in dummy.lods)
                    {
                        if (candidate?.renderer == null) continue;
                        if (candidate.lodIndex < targetIdx) continue;
                        if (candidate.renderer.transform.parent != parent) continue;
                        desiredSibling = candidate.renderer.transform.GetSiblingIndex();
                        break;
                    }
                }
                if (desiredSibling >= 0)
                    go.transform.SetSiblingIndex(desiredSibling);

                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = res.simplifiedMesh;
                var mr = go.AddComponent<MeshRenderer>();

                Renderer sourceRenderer = null;
                foreach (var candidate in dummy.lods)
                    if (candidate.lodIndex == 0 && candidate.renderer != null)
                    { sourceRenderer = candidate.renderer; break; }
                if (sourceRenderer != null)
                    LightmapTransferTool.CopyRendererSettings(sourceRenderer, mr);

                int newRid = mr.GetInstanceID();
                // Don't seed lodQualitySliders with `quality` — leave the
                // slot empty so the slider recomputes from the actual
                // polygon ratio next paint (which may differ from the
                // requested ratio when the simplifier hits Target Error
                // before reaching the target tri count).
                freshRendererIds?.Add(newRid);

                newRenderers.Add(mr);
                totalOrigTris += res.originalTriCount;
                totalSimplTris += res.simplifiedTriCount;
            }

            if (newRenderers.Count == 0)
            {
                UvtLog.Warn("[LightmapUV] Insert slot: no dummy had a LOD0 source mesh; nothing inserted.");
                Undo.CollapseUndoOperations(undoGroup);
                return;
            }

            // Splice into LODs[]: shift renderers from targetIdx onward down.
            var newLods = new LOD[lods.Length + 1];
            for (int i = 0; i < targetIdx; i++) newLods[i] = lods[i];
            newLods[targetIdx] = new LOD(newTrans, newRenderers.ToArray());
            for (int i = targetIdx; i < lods.Length; i++) newLods[i + 1] = lods[i];
            LodGroupUtility.ApplyLods(ctx.LodGroup, newLods);

            buildIntent |= FbxExportIntent.Hierarchy | FbxExportIntent.LodGroup
                | FbxExportIntent.AnyUv | FbxExportIntent.Normals
                | FbxExportIntent.Tangents | FbxExportIntent.VertexColors;

            ctx.LodGroup.RecalculateBounds();
            ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;

            Undo.CollapseUndoOperations(undoGroup);

            UvtLog.Info($"[LightmapUV] Inserted LOD slot {targetIdx} across {newRenderers.Count} dummies "
                + $"(quality {quality:P0}, {totalOrigTris} → {totalSimplTris} tris total)");
        }

        void DeleteCol(HierarchyDummy dummy, HierarchyColRow col)
        {
            if (col == null) return;

            if (col.isComponentOnly)
            {
                if (col.collider == null) return;
                UvtLog.Info($"[LightmapUV] Removed {col.typeLabel}Collider from '{col.colTransform.name}'.");
                Undo.DestroyObjectImmediate(col.collider);
            }
            else
            {
                if (col.colTransform == null) return;
                UvtLog.Info($"[LightmapUV] Removed COL '{col.colTransform.name}' from '{dummy.dummy.name}'.");
                Undo.DestroyObjectImmediate(col.colTransform.gameObject);
            }

            buildIntent |= FbxExportIntent.Hierarchy | FbxExportIntent.Collision;

            if (ctx.LodGroup != null) ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        // ── Leaf rename: only fix the trailing _LOD{N} / _COL[_Hull{N}] suffix.
        // Each leaf keeps its existing base name (e.g. "Stove_Base_LOD0" stays
        // "Stove_Base_LOD0", not "Stove_LOD0") — the user controls the base by
        // editing GameObject names directly; we just renumber slots so that
        // Insert / Delete don't leave duplicates like two "*_LOD2"s.
        // Caller owns the surrounding Undo group; rename ops are recorded with
        // Undo.RecordObject so they collapse with the triggering mutation.
        void RebuildLeafNamesNoUndoGroup()
        {
            if (ctx?.LodGroup == null) return;
            if (hierarchyDummies == null) RebuildHierarchyView();
            if (hierarchyDummies == null) return;

            string rootName = ctx.LodGroup.gameObject.name;
            foreach (var dummy in hierarchyDummies)
            {
                if (dummy.dummy == null) continue;
                string fallbackBase = dummy.isRoot ? rootName : dummy.dummy.name;
                if (string.IsNullOrEmpty(fallbackBase)) fallbackBase = "Group";

                for (int i = 0; i < dummy.lods.Count; i++)
                {
                    var lod = dummy.lods[i];
                    if (lod.renderer == null) continue;
                    string current = lod.renderer.gameObject.name;
                    string baseName = UvToolContext.ExtractGroupKey(current);
                    if (string.IsNullOrEmpty(baseName)) baseName = fallbackBase;
                    string desired = $"{baseName}_LOD{lod.lodIndex}";
                    if (current == desired) continue;
                    Undo.RecordObject(lod.renderer.gameObject, "Rename LOD");
                    lod.renderer.gameObject.name = desired;
                }

                // Count standalone _COL GameObjects to pick single vs Hull{N}.
                int standaloneCount = 0;
                foreach (var c in dummy.cols)
                    if (!c.isComponentOnly) standaloneCount++;
                int hullIndex = 0;
                for (int i = 0; i < dummy.cols.Count; i++)
                {
                    var col = dummy.cols[i];
                    if (col.colTransform == null) continue;
                    if (col.isComponentOnly) continue;
                    string current = col.colTransform.gameObject.name;
                    string baseName = UvToolContext.ExtractGroupKey(current);
                    if (string.IsNullOrEmpty(baseName)) baseName = fallbackBase;
                    string desired = standaloneCount <= 1
                        ? $"{baseName}_COL"
                        : $"{baseName}_COL_Hull{hullIndex}";
                    hullIndex++;
                    if (current == desired) continue;
                    Undo.RecordObject(col.colTransform.gameObject, "Rename COL");
                    col.colTransform.gameObject.name = desired;
                }
            }
        }

        // ── Chain foldout header (Unity Hierarchy-style chevron). ──
        // Returns true when the chain is expanded. Persists per-chain
        // state in collapsedChains keyed by dummy + base name.
        bool DrawChainFoldoutHeader(HierarchyDummy dummy, HierarchyChain chain)
        {
            string key = (dummy.dummy != null ? dummy.dummy.GetInstanceID() : 0)
                + "|" + chain.baseName;
            bool open = collapsedChains == null || !collapsedChains.Contains(key);

            EditorGUILayout.BeginHorizontal();
            // Chevron at the dummy content edge.
            var chevRect = GUILayoutUtility.GetRect(14, 16,
                GUILayout.Width(14), GUILayout.Height(16));
            bool nextOpen = EditorGUI.Foldout(chevRect, open, GUIContent.none, true);
            EditorGUILayout.LabelField(chain.baseName, EditorStyles.boldLabel,
                GUILayout.MinWidth(80));
            GUILayout.FlexibleSpace();
            // Channel badges sourced from LOD0's mesh — chain-wide channels
            // are uniform in practice (regenerate preserves them; transfer
            // copies them), so showing UV0·UV1·N·T once per chain replaces
            // the per-row repetition we used to render below the name.
            string chainBadges = chain.rows.Count > 0
                ? ChannelBadges(chain.rows[0].mesh)
                : "";
            string summary = string.IsNullOrEmpty(chainBadges)
                ? $"{chain.rows.Count} LOD"
                : $"{chain.rows.Count} LOD  ·  {chainBadges}";
            EditorGUILayout.LabelField(summary,
                EditorStyles.miniLabel, GUILayout.Width(140));
            // "→ Dummy" wraps a flat-under-Root chain in a fresh GameObject
            // container named after the chain base. The chain renderers get
            // re-parented under it; LODGroup references stay intact since
            // they track Renderer components, not parents. Only offered
            // when the chain currently lives directly under Root (already-
            // nested chains skip the affordance).
            if (dummy.isRoot)
            {
                bool wrapPending = IsChainWrapPending(chain);
                var prevBg2 = GUI.backgroundColor;
                GUI.backgroundColor = wrapPending
                    ? new Color(0.95f, 0.65f, 0.20f)   // amber — queued
                    : new Color(0.55f, 0.85f, 0.55f);  // green — idle
                string label = wrapPending ? "✓ Queued" : "→ Dummy";
                string tooltip = wrapPending
                    ? $"Wrap '{chain.baseName}' chain queued. Click to cancel before Apply Changes."
                    : $"Queue: wrap '{chain.baseName}' chain in a new Dummy GameObject under Root. "
                        + "Applied on Apply Changes.";
                if (GUILayout.Button(new GUIContent(label, tooltip),
                        EditorStyles.miniButton, GUILayout.Width(74)))
                {
                    ToggleChainWrapPending(chain);
                }
                GUI.backgroundColor = prevBg2;
            }
            GUILayout.Space(ChainContentRightPad);
            EditorGUILayout.EndHorizontal();

            if (nextOpen != open)
            {
                if (collapsedChains == null) collapsedChains = new HashSet<string>();
                if (nextOpen) collapsedChains.Remove(key);
                else collapsedChains.Add(key);
            }
            return nextOpen;
        }

        // ── Chain grouping ──
        // Split a Dummy's LOD rows into chains keyed by stripped base name
        // (Metal_LOD0 and Metal_LOD1 share base "Metal", so they form one
        // chain). Chains are returned in first-seen order so the visual
        // layout follows the LODGroup's slot-0 ordering. Within each chain,
        // rows are sorted by lodIndex so LODs read top-to-bottom.
        sealed class HierarchyChain
        {
            public string baseName;
            public List<HierarchyLodRow> rows = new List<HierarchyLodRow>();
        }
        static List<HierarchyChain> GroupLodsByChain(HierarchyDummy dummy)
        {
            var byKey = new Dictionary<string, HierarchyChain>();
            var ordered = new List<HierarchyChain>();
            foreach (var lod in dummy.lods)
            {
                if (lod == null) continue;
                string key = "(unnamed)";
                if (lod.renderer != null && !string.IsNullOrEmpty(lod.renderer.name))
                {
                    string stripped = UvToolContext.ExtractGroupKey(lod.renderer.name);
                    key = string.IsNullOrEmpty(stripped) ? lod.renderer.name : stripped;
                }
                if (!byKey.TryGetValue(key, out var chain))
                {
                    chain = new HierarchyChain { baseName = key };
                    byKey[key] = chain;
                    ordered.Add(chain);
                }
                chain.rows.Add(lod);
            }
            foreach (var chain in ordered)
                chain.rows.Sort((a, b) => a.lodIndex.CompareTo(b.lodIndex));
            return ordered;
        }

        // ── Visual divider between LOD rows so the table reads as discrete entries. ──
        // The tinted chain helpBox washes out faint dividers, so the rule
        // here is darker (full alpha) and 1 px tall.
        static void DrawRowDivider()
        {
            EditorGUILayout.Space(2);
            var rect = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(rect, new Color(0.18f, 0.18f, 0.18f, 0.85f));
            EditorGUILayout.Space(2);
        }

        // ── Status marker: small coloured square painted at the start of a row. ──
        // Drawn via GetRect+DrawRect (a guaranteed-rendered rect from the layout)
        // so the indicator survives nested helpBox layouts that swallow post-paint
        // strokes elsewhere.
        static void DrawStatusMarker(bool fresh, bool stale, bool markedDelete = false)
        {
            const float w = 6f;
            const float h = 18f;
            var rect = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
            if (Event.current.type != EventType.Repaint) return;
            Color color;
            if (markedDelete) color = new Color(1f, 0.20f, 0.20f);   // red — pending delete
            else if (fresh) color = new Color(1f, 0.50f, 0.05f);     // bright orange — just inserted / regenerated
            else if (stale) color = new Color(0.95f, 0.75f, 0.20f);  // amber — name out of sync
            else color = new Color(0.30f, 0.30f, 0.30f, 0.35f);      // subtle gutter
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + 2, w - 1, h - 4), color);
        }

        // True when the row's renderer was regenerated this session and its
        // current mesh differs from the captured original — i.e. Discard
        // would actually change something. The backup is captured BEFORE
        // the first regenerate (see RegenerateLodWithQuality), so this stays
        // valid across the ctx.Refresh that runs immediately after.
        bool RowWasRegenerated(HierarchyLodRow lod)
        {
            if (lod == null || lod.renderer == null) return false;
            if (regenBackupMeshes == null) return false;
            int rid = lod.renderer.GetInstanceID();
            if (!regenBackupMeshes.TryGetValue(rid, out var backup) || backup == null)
                return false;
            var mf = lod.renderer.GetComponent<MeshFilter>();
            if (mf == null) return false;
            return mf.sharedMesh != backup;
        }

        // Restore the renderer's MeshFilter back to the captured original
        // mesh and drop the row from both the freshRendererIds highlight and
        // the backup map.
        void DiscardRegenerate(HierarchyLodRow lod)
        {
            if (lod == null || lod.renderer == null || regenBackupMeshes == null) return;
            int rid = lod.renderer.GetInstanceID();
            if (!regenBackupMeshes.TryGetValue(rid, out var backup) || backup == null) return;
            var mf = lod.renderer.GetComponent<MeshFilter>();
            if (mf == null) return;
            Undo.RecordObject(mf, "Discard Regenerate");
            mf.sharedMesh = backup;
            regenBackupMeshes.Remove(rid);
            freshRendererIds?.Remove(rid);
            buildIntent |= FbxExportIntent.AnyUv | FbxExportIntent.Normals
                | FbxExportIntent.Tangents | FbxExportIntent.VertexColors;
            UvtLog.Info($"[LightmapUV] Discarded regenerate on '{lod.renderer.name}' — restored {backup.name}.");
            ctx.LodGroup.RecalculateBounds();
            ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        // ── Pre-Apply staleness detection ──
        // After Insert / Delete / Regenerate the LOD slot indices and the
        // renderer GameObject names drift apart (e.g. inserting a slot at
        // index 1 leaves the old `_LOD1` GameObject sitting in slot 2). Apply
        // Names rebuilds the canonical names; until then the row is "stale".

        bool IsLodRowStale(HierarchyDummy dummy, HierarchyLodRow lod)
        {
            if (dummy == null || lod == null || lod.renderer == null || ctx.LodGroup == null)
                return false;
            string current = lod.renderer.gameObject.name;
            string baseName = UvToolContext.ExtractGroupKey(current);
            if (string.IsNullOrEmpty(baseName))
                baseName = dummy.isRoot ? ctx.LodGroup.gameObject.name : dummy.dummy.name;
            return current != $"{baseName}_LOD{lod.lodIndex}";
        }

        bool IsColRowStale(HierarchyDummy dummy, HierarchyColRow col, int index, int totalCols)
        {
            if (dummy == null || col == null || col.colTransform == null || ctx.LodGroup == null)
                return false;
            // Component-only rows live ON the dummy/root and can't be renamed
            // independently — never report them as stale.
            if (col.isComponentOnly) return false;
            // Compute base from the existing name so custom prefixes
            // (e.g. "Stove_Base_COL") are preserved through Rebuild.
            string current = col.colTransform.gameObject.name;
            string baseName = UvToolContext.ExtractGroupKey(current);
            if (string.IsNullOrEmpty(baseName))
                baseName = dummy.isRoot ? ctx.LodGroup.gameObject.name : dummy.dummy.name;
            // Count standalone _COL siblings to choose single vs Hull{N} convention.
            int standaloneCount = 0;
            int hullIdx = -1;
            int hullPos = 0;
            foreach (var c in dummy.cols)
            {
                if (c.isComponentOnly) continue;
                if (ReferenceEquals(c, col)) hullIdx = hullPos;
                hullPos++;
                standaloneCount++;
            }
            string expected = standaloneCount <= 1
                ? $"{baseName}_COL"
                : $"{baseName}_COL_Hull{hullIdx}";
            return current != expected;
        }

        bool DummyHasStale(HierarchyDummy dummy)
        {
            if (dummy == null) return false;
            foreach (var lod in dummy.lods)
                if (IsLodRowStale(dummy, lod)) return true;
            for (int i = 0; i < dummy.cols.Count; i++)
                if (IsColRowStale(dummy, dummy.cols[i], i, dummy.cols.Count)) return true;
            return false;
        }

        // ── Default slider ratio from polygon counts ──
        // Compute the row's quality slider default as currentTris / LOD0Tris
        // within the same chain. LOD0 itself reads as 1.0. Falls back to a
        // safe 1.0 when the source LOD0 isn't readable (e.g. non-readable
        // FBX import — Mesh.GetIndexCount works regardless of isReadable).
        static float ComputeLodRatioFromTriangles(HierarchyDummy dummy, HierarchyLodRow lod)
        {
            if (lod == null || lod.mesh == null) return 1f;
            if (lod.lodIndex == 0) return 1f;

            string key = lod.renderer != null
                ? UvToolContext.ExtractGroupKey(lod.renderer.name) : null;
            int lod0Tris = 0;
            foreach (var candidate in dummy.lods)
            {
                if (candidate?.mesh == null) continue;
                if (candidate.lodIndex != 0) continue;
                if (key != null && candidate.renderer != null)
                {
                    string ck = UvToolContext.ExtractGroupKey(candidate.renderer.name);
                    if (!string.Equals(ck, key, System.StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                lod0Tris = MeshHygieneUtility.GetTriangleCount(candidate.mesh);
                break;
            }
            if (lod0Tris <= 0) return 1f;
            int currentTris = MeshHygieneUtility.GetTriangleCount(lod.mesh);
            return Mathf.Clamp(currentTris / (float)lod0Tris, 0.001f, 1f);
        }

        // ── Short leaf name ──
        // Strip the chain base from a leaf GameObject name so the row only
        // shows the trailing suffix (e.g. "Foo_Bar_LOD0" → "_LOD0",
        // "Foo_Bar_COL_Hull1" → "_COL_Hull1"). The base is already shown in
        // the dummy / chain header above, so repeating it on every row was
        // pure noise. Falls back to the full name when the leaf doesn't
        // start with the canonical base or has no recognisable suffix.
        static string ShortLeafName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return fullName;
            string baseKey = UvToolContext.ExtractGroupKey(fullName);
            if (string.IsNullOrEmpty(baseKey)) return fullName;
            if (fullName.Length <= baseKey.Length) return fullName;
            if (fullName.StartsWith(baseKey, System.StringComparison.OrdinalIgnoreCase))
                return fullName.Substring(baseKey.Length);
            return fullName;
        }

        // ── Channel badges: compact summary of which mesh streams have data. ──
        // Uses Mesh.HasVertexAttribute (which works on non-readable FBX-imported
        // meshes) instead of GetUVs/colors32/normals — the data accessors silently
        // return empty arrays when isReadable is false, so the badges used to
        // appear only after a regenerate (which produces a readable mesh) and
        // never on the original import meshes.

        static string ChannelBadges(Mesh mesh)
        {
            if (mesh == null) return "";
            var parts = new List<string>();
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0)) parts.Add("UV0");
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1)) parts.Add("UV1");
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord2)) parts.Add("UV2");
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord3)) parts.Add("UV3");
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color))     parts.Add("VC");
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal))    parts.Add("N");
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent))   parts.Add("T");
            return parts.Count == 0 ? "—" : string.Join("·", parts);
        }

        // Helper kept for use by FixSplitByMaterial, which still needs to rebuild
        // the LODGroup after restructuring renderer GameObjects.
        void RebuildLodGroupFromNames()
        {
            if (ctx.LodGroup == null) return;

            var root = ctx.LodGroup.transform;
            var colSet = new HashSet<GameObject>(MeshHygieneUtility.FindCollisionObjects(root));
            var lodChildren = new SortedDictionary<int, List<Renderer>>();

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.transform == root) continue;
                if (colSet.Contains(r.gameObject)) continue;

                var match = System.Text.RegularExpressions.Regex.Match(
                    r.gameObject.name, @"_LOD(\d+)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success) continue;

                int lodIdx = int.Parse(match.Groups[1].Value);
                if (!lodChildren.ContainsKey(lodIdx))
                    lodChildren[lodIdx] = new List<Renderer>();
                lodChildren[lodIdx].Add(r);
            }

            if (lodChildren.Count == 0) return;

            Undo.RecordObject(ctx.LodGroup, "Rebuild LODGroup");

            int lodCount = lodChildren.Count;
            var newLods = new LOD[lodCount];
            int idx = 0;
            foreach (var kvp in lodChildren)
            {
                float screenHeight = lodCount == 1
                    ? 0.01f
                    : 1f - ((float)idx / (lodCount - 1)) * 0.99f;
                newLods[idx] = new LOD(screenHeight, kvp.Value.ToArray());
                idx++;
            }
            ctx.LodGroup.SetLODs(newLods);
            ctx.LodGroup.RecalculateBounds();
        }

        // ═══════════════════════════════════════════════════════════
        // Collision section (legacy — migrates to right-panel Collider Settings in PR-2)
        // ═══════════════════════════════════════════════════════════

        void DrawCollisionSection()
        {
            EditorGUILayout.Space(8);
            collisionFoldout = EditorGUILayout.Foldout(collisionFoldout, "Collision", true);
            if (!collisionFoldout) return;

            if (ctx.LodGroup == null) return;

            var root = ctx.LodGroup.transform;
            var colObjects = MeshHygieneUtility.FindCollisionObjects(root);
            var rootCollider = root.GetComponent<MeshCollider>();

            if (rootCollider != null)
            {
                Mesh colMesh = rootCollider.sharedMesh;
                string meshInfo = colMesh != null
                    ? $"{colMesh.name} ({colMesh.vertexCount:N0}v, {MeshHygieneUtility.GetTriangleCount(colMesh):N0}t)"
                    : "(none)";
                EditorGUILayout.LabelField($"Root MeshCollider: {meshInfo}", EditorStyles.miniLabel);

                EditorGUILayout.BeginHorizontal();
                bool convex = rootCollider.convex;
                bool newConvex = EditorGUILayout.Toggle("Convex", convex);
                if (newConvex != convex)
                {
                    Undo.RecordObject(rootCollider, "Toggle Convex");
                    rootCollider.convex = newConvex;
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.LabelField("Root: no MeshCollider", EditorStyles.miniLabel);
            }

            if (colObjects.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Collision Objects", EditorStyles.boldLabel);

                foreach (var colGo in colObjects)
                {
                    if (colGo == null) continue;
                    var mf = colGo.GetComponent<MeshFilter>();
                    var mr = colGo.GetComponent<MeshRenderer>();
                    Mesh mesh = mf != null ? mf.sharedMesh : null;

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"  {colGo.name}", EditorStyles.miniLabel, GUILayout.MinWidth(100));

                    if (mesh != null)
                        EditorGUILayout.LabelField($"{mesh.vertexCount:N0}v",
                            EditorStyles.miniLabel, GUILayout.Width(60));

                    if (mr != null)
                    {
                        bool vis = mr.enabled;
                        bool newVis = EditorGUILayout.Toggle(vis, GUILayout.Width(16));
                        if (newVis != vis)
                        {
                            Undo.RecordObject(mr, "Toggle COL Visibility");
                            mr.enabled = newVis;
                        }
                    }

                    if (mesh != null && (rootCollider == null || rootCollider.sharedMesh != mesh))
                    {
                        if (GUILayout.Button("Assign", GUILayout.Width(50), GUILayout.Height(16)))
                            AssignCollisionToRoot(mesh);
                    }

                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.Space(4);
            var bgc = GUI.backgroundColor;
            EditorGUILayout.BeginHorizontal();

            if (rootCollider == null && colObjects.Count > 0)
            {
                GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
                if (GUILayout.Button("Add MeshCollider to Root", GUILayout.Height(22)))
                {
                    var firstCol = colObjects[0];
                    var colMf = firstCol.GetComponent<MeshFilter>();
                    if (colMf != null && colMf.sharedMesh != null)
                        AssignCollisionToRoot(colMf.sharedMesh);
                }
            }

            if (rootCollider == null && colObjects.Count == 0)
            {
                GUI.backgroundColor = new Color(0.6f, 0.75f, 0.9f);
                if (GUILayout.Button("Use LOD0 as Collider", GUILayout.Height(22)))
                {
                    var lod0Entries = ctx.ForLod(0);
                    if (lod0Entries.Count > 0)
                    {
                        Mesh lod0Mesh = lod0Entries[0].originalMesh ?? lod0Entries[0].fbxMesh;
                        if (lod0Mesh != null)
                            AssignCollisionToRoot(lod0Mesh);
                    }
                }
            }

            if (colObjects.Count > 0)
            {
                GUI.backgroundColor = new Color(0.7f, 0.4f, 0.95f);
                bool anyEnabled = false;
                foreach (var go in colObjects)
                {
                    var mr = go != null ? go.GetComponent<MeshRenderer>() : null;
                    if (mr != null && mr.enabled) { anyEnabled = true; break; }
                }
                if (anyEnabled)
                {
                    if (GUILayout.Button("Hide COL Renderers", GUILayout.Height(22)))
                    {
                        foreach (var go in colObjects)
                        {
                            var mr = go != null ? go.GetComponent<MeshRenderer>() : null;
                            if (mr != null && mr.enabled)
                            {
                                Undo.RecordObject(mr, "Hide COL");
                                mr.enabled = false;
                            }
                        }
                    }
                }
            }

            GUI.backgroundColor = bgc;
            EditorGUILayout.EndHorizontal();
        }

        void AssignCollisionToRoot(Mesh mesh)
        {
            if (ctx.LodGroup == null || mesh == null) return;

            var root = ctx.LodGroup.gameObject;
            var mc = root.GetComponent<MeshCollider>();
            if (mc == null)
            {
                mc = Undo.AddComponent<MeshCollider>(root);
                UvtLog.Info($"[LightmapUV] Added MeshCollider to {root.name}");
            }
            else
            {
                Undo.RecordObject(mc, "Assign Collision Mesh");
            }

            mc.sharedMesh = mesh;
            buildIntent |= FbxExportIntent.Collision;
            UvtLog.Info($"[LightmapUV] Assigned collision mesh: {mesh.name}");
            requestRepaint?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════
        // Split / Merge section (legacy — migrates in PR-2)
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

            UvtLog.Info($"[LightmapUV] Split/Merge scan: {splitCandidates.Count} split, {mergeCandidates.Count} merge.");
        }

        void FixSplitByMaterial()
        {
            if (splitCandidates == null) return;

            Undo.SetCurrentGroupName("Prefab Builder: Split by Material");
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

                UvtLog.Info($"[LightmapUV] Split: {sc.entry.renderer.name} → {mesh.subMeshCount} children");
                Undo.DestroyObjectImmediate(sc.entry.renderer.gameObject);
                split++;
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (split > 0)
            {
                ctx.Refresh(ctx.LodGroup);
                RebuildLodGroupFromNames();
                ctx.LodGroup.RecalculateBounds();
                buildIntent |= FbxExportIntent.Hierarchy | FbxExportIntent.LodGroup
                    | FbxExportIntent.Materials;
            }

            splitCandidates = null;
            mergeCandidates = null;
            preview?.Restore();
            previewMode = PreviewMode.None;
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        void FixMerge()
        {
            if (mergeCandidates == null) return;

            Undo.SetCurrentGroupName("Prefab Builder: Merge");
            int undoGroup = Undo.GetCurrentGroup();
            int merged = 0;

            foreach (var g in mergeCandidates)
            {
                if (!g.include || g.entries.Count < 2) continue;

                var firstEntry = g.entries[0];
                if (firstEntry.renderer == null) continue;

                var baseMatrix = firstEntry.renderer.transform.worldToLocalMatrix;

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

                // Record the LODGroup before rewriting its renderer arrays.
                // Without this, undoing the merge restores the destroyed
                // GameObjects (Undo.DestroyObjectImmediate is undoable) but
                // leaves the LODGroup pointing at the merged renderer list,
                // so the restored objects fall out of LOD switching.
                Undo.RecordObject(ctx.LodGroup, "Merge");

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
                // Persist the renderer-array change on prefab instances;
                // otherwise the merge survives only in the live scene and is
                // lost on scene reload / prefab reapply.
                if (PrefabUtility.IsPartOfPrefabInstance(ctx.LodGroup))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(ctx.LodGroup);

                foreach (var go in destroyList)
                {
                    if (go == null) continue;
                    Undo.DestroyObjectImmediate(go);
                }

                merged++;
                UvtLog.Info($"[LightmapUV] Merged: {g.entries.Count} objects → {firstEntry.renderer.name}");
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (merged > 0)
            {
                ctx.Refresh(ctx.LodGroup);
                ctx.LodGroup.RecalculateBounds();
                buildIntent |= FbxExportIntent.Hierarchy | FbxExportIntent.LodGroup;
            }

            splitCandidates = null;
            mergeCandidates = null;
            preview?.Restore();
            previewMode = PreviewMode.None;
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════
        // Mesh info / edge / problem report sections
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
