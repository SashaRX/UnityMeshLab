// IUvTool.cs — Interface for pluggable tools in Mesh Lab.
// Each tool implements this interface and is auto-discovered via reflection.

using System.Collections.Generic;
using UnityEditor;

namespace LightmapUvTool
{
    /// <summary>
    /// Interface for pluggable tools in Mesh Lab.
    /// Implementations are auto-discovered via reflection in <see cref="UvToolHub.OnEnable"/>.
    /// <para><b>Lifecycle contract:</b></para>
    /// <list type="number">
    ///   <item><see cref="OnActivate"/> — tool becomes the active tab; ctx and canvas are ready.</item>
    ///   <item><see cref="OnRefresh"/> — LODGroup changed; MeshEntries rebuilt. Clear per-mesh state.</item>
    ///   <item><see cref="OnDeactivate"/> — tool is being switched away or window is closing.
    ///     Must restore any scene/preview state. May not fire on crash or domain reload —
    ///     the hub has fallback cleanup in <c>OnEnable</c>.</item>
    /// </list>
    /// </summary>
    public interface IUvTool
    {
        // ── Identity ──
        string ToolName  { get; }   // "UV2 Transfer", "Atlas Pack", etc.
        string ToolId    { get; }   // unique key for serialization
        int    ToolOrder { get; }   // position in toolbar (0, 10, 20...)

        // ── Lifecycle ──

        /// <summary>
        /// Called when this tool becomes the active tab.
        /// ctx and canvas are fully initialized; store references and set up canvas callbacks here.
        /// </summary>
        void OnActivate(UvToolContext ctx, UvCanvasView canvas);

        /// <summary>
        /// Called when switching to another tool or when the window closes.
        /// Must release canvas callbacks and restore any scene/preview state modified by the tool.
        /// </summary>
        void OnDeactivate();

        /// <summary>
        /// Called after the LODGroup selection changes and MeshEntries have been rebuilt.
        /// Clear any per-mesh cached state (reports, shell caches, analysis results).
        /// </summary>
        void OnRefresh();

        // ── UI ──
        void OnDrawSidebar();
        void OnDrawToolbarExtra();
        void OnDrawStatusBar();

        // ── Canvas integration ──
        void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz);

        /// <summary>
        /// Return fill mode entries for the canvas. Called on tool activation
        /// and when the hub refreshes fill modes.
        /// </summary>
        IEnumerable<UvCanvasView.FillModeEntry> GetFillModes();

        // ── Scene integration ──
        void OnSceneGUI(SceneView sv);

        /// <summary>Set by the hub. Invoke to trigger an editor window repaint.</summary>
        System.Action RequestRepaint { set; }
    }
}
