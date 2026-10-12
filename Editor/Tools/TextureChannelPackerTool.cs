using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    [MeshLabTool("texture_channel_packer", MeshLabLibraries.Core)]
    public sealed class TextureChannelPackerTool : IUvTool, IUvToolFullWindow, IUvToolWindowPreferences
    {
        private TextureChannelPackerPanel panel;
        private Action repaint;

        public string ToolName => "Texture Packer";
        public string ToolId => "texture_channel_packer";
        public int ToolOrder => 80;
        public Action RequestRepaint
        {
            get => repaint;
            set { repaint = value; if (panel) panel.RequestRepaint = value; }
        }

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            if (panel) return;
            panel = ScriptableObject.CreateInstance<TextureChannelPackerPanel>();
            panel.hideFlags = HideFlags.HideAndDontSave;
            panel.RequestRepaint = repaint;
            panel.RestoreWindowPreferences();
        }

        public void OnDeactivate()
        {
            if (!panel) return;
            try { SaveWindowPreferences(); }
            finally
            {
                panel.RequestRepaint = null;
                UnityEngine.Object.DestroyImmediate(panel);
                panel = null;
            }
        }

        public void SaveWindowPreferences() { if (panel) panel.SaveWindowPreferences(); }
        public void RestoreWindowPreferences() { if (panel) panel.RestoreWindowPreferences(); }
        public void OnDrawWindowContent() { if (panel) panel.DrawContent(); }
        public void OnRefresh() { /* FBX selection is tracked by the panel independently of mesh context. */ }
        public void OnDrawSidebar() { /* Controls occupy the full window body. */ }
        public void OnDrawToolbarExtra() { /* The packer has no mesh canvas toolbar. */ }
        public void OnDrawStatusBar() { /* Export progress is shown within the panel. */ }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { /* No mesh canvas. */ }
        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes() => Array.Empty<UvCanvasView.FillModeEntry>();
        public void OnSceneGUI(SceneView sv) { /* Texture packing does not edit scene objects. */ }
    }
}
