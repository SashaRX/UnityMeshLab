# 3D preview line antialiasing

The wire, UV borders, cage and floor now draw triangle ribbons with analytic
coverage instead of hardware one-pixel lines. This affects preview rendering only.
Mesh geometry, UVs, baking and exported assets are unchanged.

## Rendering and ownership

- Each unique segment has four vertices and six indices. The vertex shader expands
  it in physical render-target pixels; camera movement does not rebuild the mesh.
- The fragment shader evaluates distance to the finite segment, including rounded
  ends. Widths below one pixel reduce opacity while retaining raster coverage.
- Endpoints are clipped against the near plane before perspective division.
  Depth testing hides occluded edges; a small polygon offset avoids surface fighting.
- Exact coincident/reversed segments with matching endpoint colours are deduplicated,
  including edges represented by different UV/normal split vertices.
- Persistent line meshes use the existing cancellable preview-work queue. Unity
  mesh snapshots and upload happen on the editor thread; ribbon construction happens
  in the background. Cache invalidation, source destruction and viewport disposal
  release the uploaded meshes. Small transient floor/axis lists live for one frame.
- The shader uses vertex/fragment stages only, without geometry shaders.

## MSAA policy and observed engine failures

Built-in retains 4x MSAA for surface silhouettes. Line coverage also works with
MSAA off. Multisampled targets are sampled through `Graphics.Blit` for automatic
resolve, preserving `GL.sRGBWrite`. Explicit `ResolveAntiAliasedSurface(target)`
asserted on OpenGLCore after `Camera.Render` restored its framebuffer; the rendered
2x/4x test frames were empty. Automatic resolve passed those checks.

SRP previews use a single-sampled target with 24-bit depth and do not modify the
shared pipeline asset. In Unity 6000.2.6f2 / URP 17.2, the previous implementation
still produced `Missing resolve surface` / `Not inside a Renderpass`. Setting the
asset and preview to matching 2x/4x counts before rendering did not resolve it.
The single-sampled path was validated with project MSAA set to Off, 2x and 4x.
**SRP silhouettes are not multisampled by this change.** Line smoothing remains
active. HDRP and Android device rendering were not exercised.

## Verification

Tests render actual pixels, checking continuous sloped lines, subpixel energy,
near-plane clipping and occlusion. Additional tests check split-edge deduplication,
cache cleanup and the actual URP preview target without changing project MSAA.
An independent constant-colour clear/resolve/readback test checks the render-target
path without running the line shader.
The render fixture uses temporary renderers: queued `Graphics.DrawMesh` calls can
survive multiple `Camera.Render` calls in one editor frame and contaminate a test.

Environment: Windows, Unity 6000.2.6f2, URP 17.2, NVIDIA GTX 980 Ti. The editor project
uses the Android build target; this is not an Android player/device test.

| API | Final checks |
| --- | --- |
| D3D11 | 87/87 passed: line pixels, viewport, settings, LOD switching, lighting and normal packing |
| D3D12 | 10/10 line/resolve/URP integration tests passed |
| OpenGLCore | 10/10 line/resolve/URP integration tests passed |
| Vulkan | 5/10 passed; window capture replay completed, but automated pixel validation remains failing (details below) |

Static compilation passes with and without `LIGHTMAP_UV_TOOL_FBX_EXPORTER`.
Identifier and tool-dependency guards report no findings.

Local evidence is in `_results~/preview-line-aa/` (ignored, not shipped):

- `index.html`: paired main/new screenshots and 24-frame slow orbit sequences,
  matching camera, meshes and Off/2x/4x settings.
- `legacy-builtin/`, `analytic-builtin/`, `analytic-urp/`: PNGs and measurements.
- NUnit XML and editor logs for D3D11, D3D12, OpenGLCore and Vulkan.
- Static compilation with and without the FBX exporter define.

The comparison baseline is main `4d5766660edb11148357b23a0e8cf31d1bac010a`.
The scratch baseline only exposes its existing MSAA count to the replay harness.
Production defaults and shaders are otherwise unchanged.

### Open Vulkan validation issue

The actual EditorWindow replay rendered all three meshes at Off/2x/4x on Vulkan;
the saved window captures were visually intact. However, the isolated pixel tests
are **not passing on Vulkan**. Even constant-colour MSAA clear/resolve/readback
(without any mesh or line shader) returned incorrect values at 2x/4x, while the
single-sampled calibration passed. Explicit resolve, automatic resolve, camera
rendering, command-buffer rendering and synchronous/asynchronous readback did not
establish a reliable path on this environment. This does not prove the line shader
is the cause or certify Vulkan support. Keep this PR in draft until that validation
gap is resolved; no failing assertion is disabled or weakened.
The final failures are two MSAA clear/resolve cases, two MSAA subpixel-energy cases
and the single-sampled occlusion pixel case (`tests-vulkan-final-current.xml`).

## Cost and remaining limits

The captured meshes have 1,598 / 30,288 / 210,626 triangles. Uploaded ribbon meshes
take about **0.77 / 14.56 / 101.24 MiB**, respectively, according to
`Profiler.GetRuntimeMemorySizeLong`. These are additional buffers; temporary build
arrays can increase peak memory further. Dense raw meshes cost more than hardware
lines; there is no quality-reducing edge decimation.

The replay records CPU repaint time and `FrameTimingManager` GPU frame time.
The latter includes the editor frame, not just the viewport. Runs share the desktop
with other editor activity, so the CSVs are observations, not an isolated GPU
benchmark or proof of a speedup. On the dense mesh the new wire was more expensive.

Exact duplicate removal does not eliminate blending darkening at distinct segment
junctions. Very dense wire remains visually dense. This implementation does not add
temporal AA or a separate line-coverage buffer to resolve those intersections.
