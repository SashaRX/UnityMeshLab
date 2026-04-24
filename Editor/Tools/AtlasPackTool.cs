// AtlasPackTool.cs — Stub: Multi-model atlas packing into one UV space.
// Placeholder for future implementation.

using System;
using System.Collections.Generic;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    public class AtlasPackTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        Action requestRepaint;

        public string ToolName  => "Atlas Pack";
        public string ToolId    => "atlas_pack";
        public int    ToolOrder => 10;

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
            EditorGUILayout.LabelField("Atlas Pack", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Multi-model atlas packing — combine UVs from multiple models into a shared atlas.\n\n" +
                "Not yet implemented.",
                MessageType.Info);
        }

        public void OnDrawToolbarExtra() { }
        public void OnDrawStatusBar() { }

        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { }

        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes()
        {
            yield return new UvCanvasView.FillModeEntry { name = "Shells" };
            yield return new UvCanvasView.FillModeEntry { name = "Coverage" };
        }

        public void OnSceneGUI(SceneView sv) { }
    }
}
