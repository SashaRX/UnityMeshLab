# Solid voxel cleanup validation — 1.1.16

The 512-resolution report had 145 boundary edges after fitting and 58 after retrying without fitting. The solid preflight correctly stopped before Trim/Simplify. Disabling fitting did not remove the underlying cleanup defect.

## Cause and correction

`meshLabVoxelRemesh` used the Simplify cleanup floor (`extent² * FLT_EPSILON`) on the freshly generated voxel surface. Valid, positive-area sub-cell triangles were deleted. As the grid gets denser, more triangles fall below this model-wide threshold.

Voxel cleanup now uses an area floor of zero: it retains finite positive-area triangles, removes exact collapses, compacts vertices and leaves the managed closed-surface/topology guard in place. Simplify/Unwrap cleanup retains its existing threshold. No position solving, voxel resolution, serialized settings or native ABI changes were made.

## Reproduction and checks

The stored source is the earlier captured bust, not a new capture from the supplied log. It reproduces the unfitted count exactly; fitted counts differ from the user's latest run.

| Replay | Old faces | Old boundary edges | Fixed faces | Fixed boundary edges |
|---|---:|---:|---:|---:|
| Bust, 512, no fitting | 851,968 | 58 | 851,990 | 0 |
| Bust, 512, fitting | 851,942 | 132 | 851,990 | 0 |
| Rotated closed box, 48, fitting | 6,995 | 3 | 6,996 | 0 |

- Independent Python connectivity checks: no non-manifold or inconsistent edges in the fixed outputs. Exact position sets are unchanged; all old faces remain and only 22/48 previously filtered bust faces are retained. Bust 256 results remain unchanged and closed.
- Native regression exercises a rotated box at 48 and 512, fitting on/off, scales 1 and 0.001. Requires finite positive-area faces and two oppositely oriented incidences per edge. The 48/fitted case pins retention below the old floor. The test fails against the old package DLL and passes against the fixed isolated build.
- Full native CTest on Windows: 1/1 suite passed (16.72 seconds), including the new cases and existing remesh/simplify/unwrap, budget, shell and high-resolution cases.
- Actual Unity 6000.2.6f2 managed solid guard accepts both 851,990-face fixed outputs without retry or geometry replacement. This includes duplicate/degenerate faces, non-manifold edges, winding and disconnected vertex-fan checks.
- Local build used an isolated copy of the exact native sources; no checked-in plugin binary was edited. Platform publication is through `build-native.yml`.

Evidence: `_results~/voxel-closed-fix/{baseline,fixed,compare}.json`, `managed-guard-d3d11.log`, and `_results~/voxel-resolution/build-windows/Testing/Temporary/LastTest.log`.
A full Simplify/Unwrap/Bake replay of the latest live source is outside this targeted raw-voxel check.
