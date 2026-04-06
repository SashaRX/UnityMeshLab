// Uv0OptimizeTool.cs — Stub: UV0 repack / optimization.
// Placeholder for future implementation.

using System;
using System.Collections.Generic;
using UnityEditor;

namespace LightmapUvTool
{
    public class Uv0OptimizeTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        Action requestRepaint;

        public string ToolName  => "UV0 Optimize";
        public string ToolId    => "uv0_optimize";
        public int    ToolOrder => 20;

        public Action RequestRepaint { set => requestRepaint = value; }

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            this.ctx = ctx;
            this.canvas = canvas;
        }

        public void OnDeactivate() { }
        public void OnRefresh() { }

        public void OnDrawSidebar()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("UV0 Optimize", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "UV0 repack and optimization — analyze UV0 quality, fix overlaps, " +
                "optimize texel density.\n\n" +
                "Not yet implemented.",
                MessageType.Info);
        }

        public void OnDrawToolbarExtra() { }
        public void OnDrawStatusBar() { }

        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { }

        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes()
        {
            yield return new UvCanvasView.FillModeEntry { name = "Shells" };
            yield return new UvCanvasView.FillModeEntry { name = "Overlap" };
            yield return new UvCanvasView.FillModeEntry { name = "Stretch" };
        }

        public void OnSceneGUI(SceneView sv) { }
    }
}
