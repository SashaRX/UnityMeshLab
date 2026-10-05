# Window preference persistence — 1.1.19

Window UI preferences are stored in EditorPrefs, keyed by Unity project path.
Closing the window, losing focus and the normal OnDisable lifecycle save state
before tool deactivation or preview cleanup. Remesh/Bake still uses its existing
pipeline-settings preference; its UI foldouts and preview controls are separate.

The hub restores the active tool by ID, panel widths and scroll positions,
UV/3D mode, inspection/projection mode, UV channel and display controls,
checker channel controls, and viewport shading, lighting, wire, grid and axes.
Each tool's UV fill preference is stored by name rather than a menu index.
Remesh/Bake restores its section foldouts, chart-options foldout, Maps/3D view,
map channel and base-color, normal-map, cage and trim display toggles.
Unavailable generated stages still fall back to Source through the existing
preview logic; generated meshes and materials are not persisted by this change.

## Validation, 2026-10-05

- Both compile-check configurations pass, with and without the FBX exporter define.
- Unity 6000.2.6f2, DX11, URP test project: WindowPreferencesTests **3/3 pass**.
  The integration test destroys and recreates a real UvToolHub, checking selected
  tool, widths, scrolling, UV fill, viewport flags and Remesh preview/foldouts.
  Further tests check defaults for absent JSON fields and obsolete IDs/invalid values.
- Combined WindowPreferencesTests + MeshViewport3DTests: **41/43 pass**.
  The two existing failures also reproduce on the unmodified bfcdaf93 baseline:
  AttributeColorsRenderIntoUvLayoutUsingTheSameEncodingAs3D observes 0.737 rather
  than 0.5 in this graphics/color-space configuration; the inspection UV7 test
  expects decimal dots while the editor prints decimal commas.
- Raw logs/results: `_results~/window-preferences/tests.{log,xml}` and
  `_results~/window-preferences/baseline-tests.{log,xml}` (local, not published).
- git diff --check passes. New source and test files have .meta files.

This verifies window close/recreation using persistent EditorPrefs; it does not
claim a manual full Unity restart or rendered layout review of the running E project.
