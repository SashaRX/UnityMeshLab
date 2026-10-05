using System;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public partial class UvToolHub
    {
        [Serializable]
        sealed class WindowSettings
        {
            public string toolId;
            public float leftWidth = 300, rightWidth = 360;
            public Vector2 leftScroll, rightScroll, tabsScroll, inspectScroll;
            public bool canvas3D = EditorPrefs.GetBool(Canvas3DPref, false);
            public bool inspect;
            public int projection, uvChannel = 1, checkerUvChannel = 1;
            public bool checkerColor, checkerR = true, checkerG = true;
            public float zoom = 1, fillAlpha = .25f, exposure = 1;
            public Vector2 pan;
            public bool uvWire = true, uvBorder = true, fillHidden, spot;
            public MeshViewport3D.Shading shading;
            public MeshViewport3D.Projection viewProjection;
            public bool wire, lit = true, grid = true, axes = true;
            public Color background = new Color(.16f, .19f, .24f, 1);
        }

        void RestoreWindowSettings()
        {
            var state = MeshLabWindowPreferences.Load<WindowSettings>("Hub");
            SelectToolById(string.IsNullOrEmpty(pendingToolId) ? state.toolId : pendingToolId);
            sideW = MeshLabWindowPreferences.Clamp(state.leftWidth, 220, 900, 300);
            rightSideW = MeshLabWindowPreferences.Clamp(state.rightWidth, 220, 700, 360);
            sideScroll = state.leftScroll; rightSideScroll = state.rightScroll;
            toolTabsScroll = state.tabsScroll; inspectionScroll = state.inspectScroll;
            canvas3D = state.canvas3D; inspectMesh = state.inspect;
            planarProjection = Mathf.Clamp(state.projection, 0, 3);
            ctx.PreviewUvChannel = Mathf.Clamp(state.uvChannel, 0, 3);
            _checkerUvChannel = Mathf.Clamp(state.checkerUvChannel, 0, 3);
            _checkerColorMode = state.checkerColor; _checkerShowR = state.checkerR; _checkerShowG = state.checkerG;
            canvas.Zoom = MeshLabWindowPreferences.Clamp(state.zoom, .01f, 20, 1);
            canvas.Pan = state.pan;
            canvas.FillAlpha = MeshLabWindowPreferences.Clamp(state.fillAlpha, 0, 1, .25f);
            canvas.LmExposure = MeshLabWindowPreferences.Clamp(state.exposure, 0, 2, 1);
            canvas.ShowWireframe = state.uvWire; canvas.ShowBorder = state.uvBorder;
            canvas.FillHidden = state.fillHidden; canvas.SpotMode = state.spot;
            viewport.Mode = MeshLabWindowPreferences.ValidEnum(state.shading, MeshViewport3D.Shading.Shaded);
            viewport.ViewProjection = MeshLabWindowPreferences.ValidEnum(state.viewProjection, MeshViewport3D.Projection.Perspective);
            viewport.Wireframe = state.wire; viewport.Lit = state.lit;
            viewport.ShowGrid = state.grid; viewport.ShowAxes = state.axes; viewport.Background = state.background;
        }

        void SaveWindowSettings()
        {
            if (ctx == null || canvas == null || viewport == null) return;
            MeshLabWindowPreferences.Save("Hub", new WindowSettings {
                toolId = ActiveTool?.ToolId,
                leftWidth = sideW, rightWidth = rightSideW,
                leftScroll = sideScroll, rightScroll = rightSideScroll,
                tabsScroll = toolTabsScroll, inspectScroll = inspectionScroll,
                canvas3D = canvas3D, inspect = inspectMesh, projection = planarProjection,
                uvChannel = ctx.PreviewUvChannel, checkerUvChannel = _checkerUvChannel,
                checkerColor = _checkerColorMode, checkerR = _checkerShowR, checkerG = _checkerShowG,
                zoom = canvas.Zoom, pan = canvas.Pan, fillAlpha = canvas.FillAlpha, exposure = canvas.LmExposure,
                uvWire = canvas.ShowWireframe, uvBorder = canvas.ShowBorder,
                fillHidden = canvas.FillHidden, spot = canvas.SpotMode,
                shading = viewport.Mode, viewProjection = viewport.ViewProjection,
                wire = viewport.Wireframe, lit = viewport.Lit, grid = viewport.ShowGrid,
                axes = viewport.ShowAxes, background = viewport.Background
            });
            SaveFillPreference();
            FindTool<RemeshBakeTool>()?.SaveSettings();
        }

        // Fill menus differ between tools. Store the name, not a transient menu index.
        void SaveFillPreference()
        {
            if (ActiveTool == null || canvas == null) return;
            int index = canvas.ActiveFillModeIndex;
            if (index >= 0 && index < canvas.FillModes.Count)
                EditorPrefs.SetString(MeshLabWindowPreferences.Key("Fill." + ActiveTool.ToolId), canvas.FillModes[index].name);
        }

        void RestoreFillPreference()
        {
            string name = EditorPrefs.GetString(MeshLabWindowPreferences.Key("Fill." + ActiveTool.ToolId), "");
            int index = canvas.FillModes.FindIndex(mode => mode.name == name);
            if (index >= 0) canvas.ActiveFillModeIndex = index;
        }
    }
}
