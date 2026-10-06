# GPU bake pipeline validation — 2026-10-05

Version: 1.1.20. Baseline: 850b446f67e85c4709d73ec85f368a639e349983 (1.1.19).

## Change and quality contract

ProjectSurface calls the existing BvhRaycast/BvhRaycastClosestToTarget and, only
when that ray misses, the existing BvhNearest with the same point, radius, normal
filter and either-side mask. Ray and nearest results are requested together and
both readbacks finish before cancellation propagates or buffers are reused.

Two CPU query bands overlap GPU projection/readback with one ordered CPU worker:
evaluate band N-1, then prepare N+1 while the GPU resolves N. Their shared bounded
footprint cache has one producer, so prefetched footprints and their diagnostics
are not recomputed on a band swap. Pixel/sample order, per-pixel reductions and
band evaluation order stay unchanged. GPU AO completes before that band's material
evaluation; CPU fallback retains its original sequential pipeline.

No sample counts, footprint walks, dilation radii, cage settings, quality gates,
refinement passes, normal encoding, GPU batch limits or native binaries change.
The additional memory is one reusable CPU query band; GPU buffers remain bounded
by the existing 65536-query chunk. Fault/cancellation drains the worker and GPU
queries before backend disposal. Results are published only after Finish succeeds.

## Matched textured-bust benchmark

Unity 6000.2.6f2, isolated preview-lighting-urp project, Windows DX11 on NVIDIA
GTX 980 Ti; Android is the active build target. Captured URP Lit source materials,
including the source normal map, and retained simplified bust input. Both versions
use 1024px maps, 9 samples, atlas padding 3, dilation 64px, the same fitted/smoothed
cage and vertex color/alpha transfer, source AO disabled. Same 1997 triangles,
51 charts, geometry/tangent buffers and settings in every before/after comparison.
This measures BakeAsync, excluding source capture, unwrap and Editor startup.

| Repeat | Baseline | Pipelined |
| --- | ---: | ---: |
| 1 | 24.694 s | 9.230 s |
| 2 | 24.445 s | 9.024 s |
| 3 | 25.310 s | 9.023 s |
| Mean | 24.816 s | 9.093 s |

Local mean speedup: **2.73x**, approximately **63.4% less bake time**. This is a
controlled result on this model/hardware, not a guaranteed speedup on every scene.

All three raw map files match the baseline byte for byte: Color32 color, normal,
metal and AO; float emission and vertex colors. The geometry/UV/normal/tangent
file and serialized settings match exactly, as do all non-timing health counters.
Every bake submits 9933125 queries across 157 bands. Baseline dispatches: 221 ray
and 153 nearest. New dispatches: 221 combined projection. Nearest traversal still
runs for every original ray miss; fallback quality was not reduced.

- Target-buffer SHA256: `a7ca6a25942276066d0989403bfe3bad16646d7c5adb2b64381818010607a672`.
- Raw-map SHA256, all repeats and variants: `0c2b02791532828087d224e2713d050f9ded951e4a5da1c1b58d5326bc7b0265`.
- Local renders, raw maps, reports and logs: `_results~/gpu-pipeline/{before,after}`.
- Local replay driver: `_results~/preview-lighting-urp/Assets/Editor/GpuPipelineReplay.cs`.

Timing semantics: gpuBuildRequestsMs/gpuEvaluateMs now measure actual worker
execution in the GPU pipeline; resolve/AO retain asynchronous await spans.
These overlap, so their sum is not elapsed bake time. gpuPipelineMs measures wall
time for the streamed stage, excluding Prepare/setup/Finish. Do not interpret
changes in individual old/new stage spans as pure CPU or GPU kernel speedups.

## Regression and platform checks

- Both FBX compile-check variants pass.
- DX11: 210 EditMode tests pass, zero failures/skips, covering baking, 3D padding,
  projection surface, bake health and source-normal scaling.
- New/extended checks cover exact merged-vs-independent GPU query results across
  a chunk boundary, successful nearest fallbacks, skipped queries, readback
  cancellation and buffer reuse, multi-band affine map/color oracles, GPU source
  AO, pipeline cancellation/restart and cache swaps across mid-row cutoffs.
- DX12, OpenGL Core and Vulkan on Windows: the four focused GPU integration tests
  pass on each API, zero failures/skips. Each actually uses the shader backend.
- Android GLES3 and Vulkan shader bundles compile in both XYZ and DXT5nm normal
  encodings, including the new ProjectSurface kernel. This is target compilation,
  not runtime or performance validation on a physical Android device.
- Raw test/compile reports are local under `_results~/gpu-pipeline/tests`,
  `_results~/gpu-pipeline/android` and `_results~/gpu-pipeline-compile`.

The user's live E-project was not modified by these validation runs.
