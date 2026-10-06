# Graphics API validation

MeshLab must remain usable in the Unity Editor on Direct3D 11, Direct3D 12,
OpenGL Core and Vulkan, including projects whose active build target is Android.
GPU availability is a device/kernel capability, not an API-name whitelist.
Unavailable projection and AO kernels use the existing CPU paths.

MeshLab's C# tools are Editor-only. Android target compilation and Windows Editor
execution are separate checks; neither proves execution on an Android device.

## Local validation, 2026-10-05

Environment: Unity 6000.2.6f2, Windows 11, NVIDIA GTX 980 Ti, URP 17.2, linear
color space, Android active build target. OpenGL Core used OpenGL 4.5.

Each Editor API was tested with XYZ and DXT5nm normal encoding. The suite includes
the six BVH/AO kernels, GPU/CPU projection and AO comparisons, cancellation and
batch boundaries, ordinary/tiny geometry, nearest points in all seven triangle
regions, absent optional filter buffers, and actual Lit preview lighting across
rotated/mirrored tangent frames. XYZ skips the one fixture specifically requiring
Android DXT5nm; it still runs the general physical-normal lighting tests.

The full 228-test matrix recorded these results (pass / fail / skip):

| Editor API | DXT5nm | XYZ |
| --- | --- | --- |
| Direct3D 11 | 228 / 0 / 0 | 227 / 0 / 1 |
| Direct3D 12 | 228 / 0 / 0 | 227 / 0 / 1 |
| OpenGL Core 4.5 | 228 / 0 / 0 | 225 / 2 / 1 |
| Vulkan | 213 / 15 / 0 | 214 / 13 / 1 |

The 182-test BVH/bake/padding/AO subset passed in every matrix case. Remaining
failures are in the actual-render preview fixtures. OpenGL lighting comparisons
also failed in an earlier DXT5nm run and passed on repeat, so its preview result
is unstable. Vulkan preview failures remain unresolved: blank/partially covered
controls, corrupted HDR values and differing normal-map lighting were observed.
Successful compute checks do not establish preview correctness.

The Android shader check builds all eleven package shader assets into explicit
AssetBundles for GLES3 and Vulkan, separately with XYZ and DXT5nm. Build callbacks
record the target compiler platforms and verify that all six compute kernels were
submitted. Strict builds must succeed; a successful host import alone is insufficient.

The local XML results, shader-build reports and preview PNGs are saved under
`_results~/graphics-api-matrix/`. These generated artifacts are not shipped in UPM.
An earlier OpenGL run had two lighting comparison failures; the focused repeat and
full API matrix are retained separately so failed runs are not overwritten.

Vulkan diagnostic repeats retained their own XML/log/capture files. Changing the
readback format, using synchronous GPU readback, setting a Repaint event/viewport,
using MeshRenderers, disabling SRP batching or RenderGraph, advancing a frame
between fixtures, disabling HDR, disabling asynchronous shader compilation and
forcing direct rendering did not produce a passing preview series. These experimental
changes were not included in the package. A separate constant-texture blit/readback
probe passed 100 repetitions for each of three HDR format combinations; sampled
production normal-preview textures also contained the expected ASTC channel packing.
The preview failure's root cause has not been established.

No Android device was connected during this validation. GLES/Vulkan mobile
execution, vendor-driver behavior and device buffer limits still require a device
test. OpenGL ES 3.1 only guarantees four compute buffers, so compute support alone
does not establish that these kernels can run on a particular mobile GPU.
See [Unity's compute portability rules](https://docs.unity3d.com/6000.0/Documentation/Manual/class-ComputeShader-crossplatform.html).

## Repeating the checks

Use an isolated Unity project with the package registered as testable and an
Android build target. For each API, start a fresh Editor with `-force-d3d11`,
`-force-d3d12`, `-force-glcore` or `-force-vulkan`. Run EditMode tests with a real
graphics device; do not pass `-nographics`. Set the normal encoding before running
each suite and retain the actual `SystemInfo.graphicsDeviceType` in the log.

Run these fixtures: `ComputeShaderImportTests`, `RemeshProjectionSurfaceTests`,
`RemeshBakeTests`, `RemeshSurfacePaddingTests`, `VertexAOBakerBlurTests`,
`RemeshPreviewNormalPackingTests`, `RemeshNormalFrameTests`,
`RemeshSourceNormalScaleTests`, `RemeshPreviewLightingTests`, and
`GpuWatchdogReportsErrorsButKeepsCancellationTerminal`.

The Android compilation harness source lives in
`Tools~/graphics-api-validation/AndroidShaderBuild.cs`. Copy it only into the
isolated project's `Assets/Editor/`, then run `-executeMethod AndroidShaderBuild.Run`
without `-quit`. It is restricted to the scratch project named
`preview-lighting-urp` and writes target/compiler reports alongside it. It changes
that scratch project's graphics API and normal encoding settings; do not put it
in a consumer project.

For device validation, test exported assets in an Android player using GLES3 and
Vulkan separately, checking physical-normal lighting, UV seams and texture padding.
Do not treat the Editor-only pipeline as a runtime Android component.
