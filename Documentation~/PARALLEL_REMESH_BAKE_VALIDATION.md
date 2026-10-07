# Parallel refinement and bake validation — 2026-10-05

Version: 1.1.18. Baseline: 35efc46f91cb521b47c37d0b3ca385be2f43cfad (1.1.17).

## Quality contract

Keep the existing passes, seven coarse-fit candidates, three trials per candidate,
13 source probes per triangle, UV/projection settings, area weights, tangent-frame
continuation, source-distance and topology acceptance gates. No quality preset or
iteration count was reduced.

- Coarse fitting evaluates the independent candidates for one vertex in parallel.
  Candidate/trial results are reduced in the original order with the original 1%
  improvement threshold. Vertices and passes still update sequentially.
- Baking gathers independent physical texel footprints in parallel. Samples are
  concatenated in the original pixel/sample order and keep the original query
  cutoffs. A circular window of at most 256 pixels retains prefetched work across
  cutoffs, including mid-row cutoffs, without counting footprint diagnostics twice.
- GPU kernels, readback scheduling, CPU material evaluation, chart merge and the
  dense pre-Simplify refinement algorithm remain unchanged.

## Controlled coarse-fit replay

Unity 6000.2.6f2, isolated `preview-lighting-urp` project, current native plugin.
Input: retained `neck-contour/trim.bin` (210626 triangles), `source.bin` (30288
triangles), 256 voxel resolution and target 1600 triangles. Both variants use the
same ordinary Simplify -> post-refinement -> source-aligned triangulation output
as their coarse-fit input. This isolates the expensive candidate search, excluding
Editor startup/compilation. The rejected dense-pre-refinement alternative is not
repeated in this isolated phase benchmark.

| Repeat | Sequential fit | Parallel fit |
| --- | ---: | ---: |
| 1 | 29.663 s | 8.313 s |
| 2 | 30.393 s | 7.978 s |

Average phase speedup: approximately 3.69x. This is not a measurement of the whole
Simplify stage.

Both output mesh buffers and both complete diagnostic reports (apart from the
elapsed timer) match exactly across variants and repeats. Each applies 379 moves;
the same source-distance/topology gates pass. Positions/indices SHA256:
`8dfb6f467d2292806aa99451ba910bf1a91604518770f900b91581324a33bcf1`.

## Controlled textured bake replay

Same Unity version, actual URP Lit source material capture, Direct3D11 GPU
queries, retained textured bust input and identical settings in both variants:
512 texture resolution, 16 samples, padding 3, 64px dilation, fitted/smoothed
cage, source AO disabled, vertex color/alpha transfer enabled. Target: 1997
triangles, 31 charts. Three paired cases use one source/geometry per run; timing
numbers are single pairs and include Editor scheduling, not GPU-only timings.

| Case | Sequential footprints | Parallel footprints |
| --- | ---: | ---: |
| CPU, source normal map enabled | 12.995 s | 10.686 s |
| CPU, source normal map disabled | 9.816 s | 7.448 s |
| GPU queries, source normal map enabled | 12.382 s | 11.123 s |
| GPU request construction component | 4.861 s | 3.507 s |

For each case, the serialized raw Color32 color/normal/metal/AO maps, float
emission map and float vertex colors match **byte for byte** against the original
implementation. Surface/coverage/normal diagnostics match. The GPU workload is
unchanged: 70 bands and 4529452 requests in each variant. Existing CPU/GPU numerical
differences are not claimed to be eliminated; each backend matches its own baseline.

Renders and raw maps are retained locally under `_results~/parallel-bake/{before,after}`.
Coarse-fit meshes/reports are under `_results~/parallel-refine/{before,after}`.
The user's live E-project was not modified by the validation runs.

## Regression checks

- 224 Unity EditMode tests passed, zero failed/skipped: surface padding, baking,
  projection surface, bake health, source normal scale and surface refinement.
- New serial-footprint oracle checks preserve every sample, its order and
  transported frame across multiple grid sizes, mid-row query cutoffs, cache wraps
  and atlas holes; footprint diagnostic totals also match exactly.
- Cancellation leaves an unpublished band empty. Coarse-fit repeated runs retain
  exact vertex/index buffers, movement counts and quality metrics.
- Both FBX define variants compile successfully. Native binaries/shaders and all
  existing quality thresholds are unchanged.
