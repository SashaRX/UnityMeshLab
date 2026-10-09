// IUvTool.cs — Interface for pluggable tools in Mesh Lab.
// Each tool implements this interface and is auto-discovered via reflection.

using System.Collections.Generic;
using UnityEditor;

namespace SashaRX.UnityMeshLab
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

    internal interface IUvToolWindowPreferences
    {
        void SaveWindowPreferences();
    }

    /// <summary>
    /// Opt-in for tools that take part in the shared 3D canvas (the hub's UV | 3D
    /// switch). Without it a tool's 3D view shows the context's meshes for the
    /// preview LOD with their scene materials.
    /// </summary>
    public interface IUvTool3D
    {
        /// <summary>
        /// Fill items with what the 3D canvas should show while this tool is active and
        /// return true; return false to show the context's LOD meshes instead.
        /// </summary>
        bool Get3DContent(List<MeshViewport3D.Item> items);

        /// <summary>Draw on top of the content (wire, lines, points) through the viewport's overlay API.</summary>
        void OnDraw3D(MeshViewport3D view);
    }

    /// <summary>Optional input before the shared viewport handles orbit and spot selection.</summary>
    internal interface IUvTool3DInput
    {
        void On3DInput(MeshViewport3D view, UnityEngine.Event input);
    }

    /// <summary>
    /// Opt-in for tools whose own output should fill the shared UV canvas (the hub's
    /// UV mode) instead of the context's preview-LOD meshes — the Remesh &amp; Bake
    /// result with its new atlas. The canvas treats the entries like any other: fill
    /// modes, wire, border, spot picking, checker or the entry's own preview texture.
    /// </summary>
    public interface IUvToolUvContent
    {
        /// <summary>
        /// Fill entries with what the UV canvas should show while this tool is active and
        /// return true; return false to show the context's LOD meshes instead. Keep the
        /// MeshEntry instances stable across frames so hover and selection persist.
        /// </summary>
        bool GetUvContent(List<MeshEntry> entries);
    }

    /// <summary>
    /// Opt-in marker for tools that want a right-side sidebar in addition to
    /// the standard left sidebar. The hub renders this sidebar to the right
    /// of the canvas with its own resize handle. Tools that don't implement
    /// this interface render with the canvas spanning the remaining width.
    /// </summary>
    public interface IUvToolRightSidebar
    {
        void OnDrawRightSidebar();
    }

    /// <summary>Optional hooks around shared asset writes. Only the active tool is called.</summary>
    public interface IUvToolAssetLifecycle
    {
        void BeforeAssetWrite();
        void AfterAssetWrite();
    }
}
