# Viewport geometry inspection

The shared 3D and planar geometry canvas has a **Pick** selector for Polygon,
Edge, Border and Vertex. Hover is cyan; selection is yellow. A plain left click
selects an element, **Clear** releases it, and Alt+drag still controls the camera.
Shift-click remains available to Remesh's synthetic region selection.

**Borders** in the existing status bar means UV island seams. **Border** in the
Pick selector and **Holes** use geometric boundary edges, after welding identical
positions for inspection only. This prevents UV/normal seam duplicates from
creating false holes and never changes the mesh. Each mesh is inspected separately.
Closed boundary loops can be intentional openings; detection alone does not
recommend closing them. Open/branched boundaries and non-manifold edges remain
visible instead of cancelling inspection of the other loops.

**Holes** shows cyan boundary edges and red non-manifold edges. Type a mesh name,
contour number, `loop` or `branched` in the search field, choose a boundary, or use
the previous/next buttons. A selected contour includes its vertices and reports
edge count, perimeter and whether it is a simple loop. **Frame hole** explicitly
fits the selected contour; changing mesh, LOD, preview stage or selection preserves
the camera. Navigation does not edit closure settings.

**X-ray** disables depth testing for highlights, rims, wire and vertex markers.
**Opacity** draws the underlying surface transparently, without changing its
materials, FBX, sidecar or scene. **Solid** restores full surface opacity. These
display preferences survive model changes and are saved with the window.

In Remesh's **Cap / Bridge** stage, the **Surface** dropdown shows all geometry,
only the original support, or only the added closure faces. Original contours
are retained even after closing: **Find rim**, **Refused contours only** and the
previous/next buttons navigate the preparation diagnostics, including refusal
reasons. Selected rims are yellow over the complete diagnostic rim overlay.
X-ray exposes accepted closure faces behind the original support.

Hole detection, BVH construction and vertex sprite preparation run through the
shared cancellable preview worker. Source changes invalidate the data; switching
models and closing the hub release owned meshes and cancel pending uploads.

## Verification

Unity Editor regression controls cover closed/open geometry with split corner
vertices, multiple independent holes, zero-area input faces, branched/non-manifold
boundaries, asynchronous invalidation and source preservation. GPU readbacks
check an occluded closure triangle with opaque, X-ray and transparent rendering,
and the return to solid depth testing. Separate line tests verify X-ray visibility.
