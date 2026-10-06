# Source-aligned triangulation after Simplify

## 1.1.13: coarse source fitting and bounded triangle regularization

The neck/collar report exposed a second limitation: fitting was capped at 0.35 voxel cells, while all vertex motion was accepted or backtracked together. The strict diagonal policy could also retain a zigzag/poor triangle pair for a small local distance advantage. The pipeline now first retains the existing source-aligned triangulation, fits coarse vertices individually against their full one-ring, and then regularizes the fitted mesh. Performing the fit before the existing triangulation produced a different, less useful set of local moves in the bust replay and was discarded.

The coarse fit skips dense voxel edges, open/non-manifold boundaries, split positions and existing source feature anchors. It projects candidate positions to normal-compatible source faces, compares 13 probes per adjacent triangle, and only accepts a local mean-squared error improvement without increasing local maximum error by more than 0.005 cells. Vertex motion is bounded by 25% of the shortest incident edge and four voxel cells from the input position. Orientation, area and minimum-quality checks apply to every incident face. The complete forward/reverse surface and topology guards still decide whether to retain the fitted mesh.

The final regularization can trade a small local distance increase for at least a 25% increase in minimum triangle quality on a poor pair (quality below 0.5). Its candidate maximum error must stay below 0.8 cells; local maximum can grow by at most 0.125 cells and local RMS by at most 0.075 cells. Existing sharp-fold, source-feature, orientation and sliver checks remain. The original `Retriangulate` entry point retains its previous strict policy; this extra shape budget belongs to `RegularizeFitted` after accepted coarse fitting. These are sampled geometric checks, not a self-intersection proof or artistic retopology.

Replay: `remesh_20261005_121621_951_8b9208.bin` and retained `unwrap_20261005_121700_966_552f8a.bin`, using the recorded settings and source geometry in the isolated Unity 6000.2.6f2 project. The full pipeline starts from the captured 210,626-face trimmed Remesh. Its native collapse error remains 0.004777913 and its final face count remains 1,598. Coarse fitting accepts 379 vertex updates; the final regularizer accepts 35 flips. The original strict connectivity pass still accepts 76 flips. The complete Simplify/refinement replay takes 65.84 seconds on the local machine, excluding unwrap and Editor startup.

| Area | Old final RMS, cells | New final RMS, cells | Maximum old/new, cells |
| --- | ---: | ---: | ---: |
| Whole mesh, 1,598 faces | 0.365806891 | 0.324644431 | 2.370909222 / 2.321382536 |
| Chin/front patch, 108 faces | 0.430350732 | 0.388078361 | 1.518407680 / 1.398098598 |

Sampled RMS improves by 11.25% overall and 9.82% in the chin/front region defined below. The reproduced neck pair replaces the old `295–310` diagonal with `309–271`; vertex IDs are diagnostic capture indices. Final topology has no boundaries, duplicate/degenerate faces, non-manifold/inconsistent edges or disconnected fans. Subsequent native unwrap produces 49 charts and passes a complete whole-atlas scan with zero overlap pairs, invalid UV faces and degenerate UV faces. Chart count is reported for reproduction, not used as proof of seam placement quality.

Local artifacts: `_results~/neck-contour/`, including `full/result.bin`, `full/metrics.txt`, `full/topology.txt`, `full/uv.txt`, and matching-camera geometry renders. No textures or lighting were baked in this replay; it validates geometry and UV integrity, not final material appearance. Rebuild **Simplify → Unwrap → Bake** in the live project to compare the new final material.

Verification for 1.1.13: 97/97 local Unity EditMode tests passed with no skipped tests (46.03 seconds). The suite covers surface refinement, topology, normals and chart merge; added coverage verifies coarse fitting at unit/millimetre scales, unchanged open boundaries, opposite-facing source rejection, sharp-fold retention, cancellation and invalid-topology rejection. Both FBX define configurations compile with zero errors. Tool dependencies and undeclared-identifier checks report no findings.

## 1.1.11: connectivity-only baseline

The previous surface-refinement pass only flipped a diagonal when the minimum triangle quality increased by 2%, the target faces were almost coplanar, and the nearest source face normals agreed within about 18 degrees. On an organic bend, two diagonals can have equal triangle quality while one cuts through the source's bulge. Keeping the shorter or more regular diagonal alone does not solve this.

`RemeshSurfaceRefine.Simplify` now runs a separate connectivity pass after the existing bounded vertex fitting. This pass leaves every vertex position and the face count unchanged. It runs before normals/UV construction and baking.

For each eligible two-face patch it compares both diagonals against the original source BVH. Each triangle has 13 distance probes along its edges and across its interior; squared errors are area-weighted. A curved patch can change its diagonal for at least a 10% local squared-error improvement even without a triangle-quality improvement. This requires different source normal directions (dot product below 0.999): face-centroid probes are supplemented by opposite interior probes when both centroids land on the same side of a ridge. Flat patches can still change for better triangle shape with essentially equal source fit. The search reach follows the size of the coarse patch rather than only the voxel cell size.

The pass retains sharp-fold, orientation, minimum-area, existing-edge and sliver checks. Minimum triangle quality cannot drop below 80% of the previous minimum. Local maximum source error has a 0.025-cell tolerance. After the pass, the existing forward and reverse surface probes, manifold connectivity, boundaries and component topology must pass; otherwise the original mesh is retained. These sampled checks are not a certified Hausdorff or self-intersection proof.

The original dense voxel fitting/flip policy remains in place. An initial experiment that widened that policy also changed vertex-motion backtracking and slightly increased the chin error. It was discarded. The final change separates connectivity from motion.

## Retained bust replay

Input: the retained `unwrap_20261005_094021_994_f6453a.bin` capture, 801 positions and 1,598 faces. Original source: 16,847 vertices and 30,288 faces. Voxel resolution 256; source longest extent 0.004 units, cell size 0.000015625. The replay uses the isolated Unity 6000.2.6f2 project, the original source capture/root orientation and the same camera before/after. No source textures are needed for the geometric error measurement.

The final connectivity pass accepted 76 flips in four sweeps. Every position is bit-identical, the number of faces is unchanged, topology remains valid, and two independent executions produce identical geometry bytes. The subsequent native unwrap passes a complete whole-atlas overlap scan: zero overlap pairs, invalid faces and degenerate UV faces (45 charts).

| Area | RMS before, cells | RMS after, cells | Maximum before/after, cells |
| --- | ---: | ---: | ---: |
| Whole mesh, 1,598 faces | 0.380221706 | 0.365756475 | 2.369341962 / 2.369341962 |
| Chin/front patch, 108 faces | 0.450339868 | 0.430049892 | 1.516832896 / 1.516832896 |

These measurements use the existing four edge-midpoint/centroid probes per face, independently of the denser local acceptance samples. The chin patch is selected in the display-normalized source space: `0.1 < y < 0.4`, `z > 0.05`, `abs(x) < 0.3`. Its sampled RMS decreases by about 4.5%. This is a local improvement to decimated triangle placement, not automatic artistic retopology or a guarantee that every chin edge matches a hand-authored mesh.

Artifacts are ignored local outputs under `_results~/chin-triangulation/verified/`:

- `input.bin`, `result.bin`, `repeat.bin`, `source.bin`, `metrics.txt`, `report.txt`, `display.json`.
- `before-180.png`, `after-180.png`, `before-215.png`, `after-215.png`: identical cameras; removed edges are red and added edges green.

The discarded broader variant also spent triangle quality for fit on a nearly flat box face and made its UV repair much slower. The final source-bend condition prevents that change; it does not alter the source-error or overlap-repair acceptance gates.

## Automated verification

Unity 6000.2.6f2 EditMode: 95/95 tests passed, no skipped cases, in 45.7 seconds (`verified-tests.xml`). This covers surface refinement, manifold topology, normals and chart merging. Added regressions cover equal-quality diagonals on a curved source at unit and millimetre scales, exact source-fit preservation, and planar fit noise; fold, cancellation and invalid-topology tests also exercise the connectivity-only pass. The diagonal-box end-to-end simplify/unwrap regression passes with zero extra source-aligned flips.

License-free C# compilation passes with and without `LIGHTMAP_UV_TOOL_FBX_EXPORTER`. Tool-dependency and undeclared-identifier checks report no findings.

To regenerate the live result after updating, rerun **Simplify → Unwrap → Bake**. Reusing the previous UVs or normal map with the changed triangles is invalid.
