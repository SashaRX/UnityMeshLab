# Preview up axis — 2026-10-06

The shared 3D toolbar offers X Up, Y Up and Z Up. Choose Z Up for the supplied
bust: a direct render of its captured, root-local source geometry reproduces
the head pointing down under Y Up and shows it upright under Z Up.

The selected axis sets the perspective camera's orbit basis and the floor
plane. Picking rays and pan use that same camera basis. The floor lies below
the content along the selected axis. Planar XY/XZ/YZ views retain their named
coordinate planes, independent of this perspective setting. The camera pivot,
orbit angles and zoom remain intact when changing the up axis.

Floor toggles the reference grid only; the surface wireframe and coordinate
axes have separate states. The up axis and floor visibility are saved in
project-specific window preferences. Existing preference JSON without the new
axis field keeps Y Up; invalid axis values also restore Y Up. The shared
viewport retains this choice across Source/Remesh/Simplify/Result switches.
Mesh vertices, item matrices, baking inputs and export transforms are untouched.

## Verification

Unity 6000.2.6f2 / D3D11: 115 tests passed, none skipped. This includes camera
rays picking unmodified geometry for all three up axes, the floor plane below
the model for all three axes, all nine planar-projection/up-axis combinations,
window-close/reopen restoring Z Up and disabled floor, and the viewport,
preview-lighting, normal-packing, hierarchy and resource regression selections.

The actual MeshViewport3D Draw path was rendered through an isolated EditorWindow
on the user's captured bust. Six PNGs cover X/Y/Z with floor enabled/disabled.
Visual inspection confirms the bust is upright under Z Up and that disabling
Floor removes the grid. Local ignored evidence:
`_results~/preview-up-axis/` (tests XML/log, render log and six PNGs).
