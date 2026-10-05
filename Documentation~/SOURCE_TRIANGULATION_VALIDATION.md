# Source-aligned triangulation after Simplify

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
