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
