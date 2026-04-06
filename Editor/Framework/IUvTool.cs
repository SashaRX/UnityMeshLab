// IUvTool.cs — Interface for pluggable tools in Mesh Lab.
// Each tool implements this interface and is auto-discovered via reflection.

using System.Collections.Generic;
using UnityEditor;

namespace LightmapUvTool
{
    public interface IUvTool
    {
        // ── Identity ──
        string ToolName  { get; }   // "UV2 Transfer", "Atlas Pack", etc.
        string ToolId    { get; }   // unique key for serialization
        int    ToolOrder { get; }   // position in toolbar (0, 10, 20...)

        // ── Lifecycle ──
        void OnActivate(UvToolContext ctx, UvCanvasView canvas);
        void OnDeactivate();
        void OnRefresh();   // meshEntries reloaded

        // ── UI ──
        void OnDrawSidebar();
        void OnDrawToolbarExtra();   // extra toolbar buttons (fill modes, etc.)
        void OnDrawStatusBar();      // tool-specific status info

        // ── Canvas integration ──
        void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz);
        IEnumerable<UvCanvasView.FillModeEntry> GetFillModes();

        // ── Scene integration ──
        void OnSceneGUI(SceneView sv);

        // ── Repaint request ──
        /// <summary>Set by the hub so tools can request Repaint().</summary>
        System.Action RequestRepaint { set; }
    }
}
