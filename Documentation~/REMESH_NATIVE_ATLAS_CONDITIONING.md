# Native remesh atlas conditioning

The previous unit-sized xatlas input still rejected valid positive-area slivers
from voxelization and collapse. Its absolute `FLT_EPSILON` face-area threshold
marked those faces ignored, and their unassigned atlas vertices failed the
strict output mapping check. Deleting those triangles opens otherwise closed
surface meshes and is not a repair.

The atlas input now receives a uniform expansion based on the smallest positive
triangle area, bounded to 65536. Chart area, boundary length and texel density
are converted through the same atlas unit. Physical output positions continue
to come from the original corners through `xref`; no source face is removed.
Zero-area input and domains beyond the conditioning budget still fail explicitly.
Conditioning alone leaves the original surface geometry and wire layout untouched.

Local MSVC headless tests, 2026-10-09: `remesh` and `uv-input` pass. The thin
closed tetrahedron regression now verifies all four faces, exact physical
corners, chart assignment and finite channels at scales 1 and 0.001.
The next fitted-mesh validation exposed two additional distortions: per-axis
ceil stretching within a chart and per-axis normalization of a rectangular
atlas into square UV units. Remesh now opts into shape-preserving raster bounds
and normalizes both coordinates by the larger atlas dimension. Its repair pack
uses the additional `xatlasPackChartsPreserveShape` export. Legacy UV Transfer
uses the existing export and retains its packing behavior.

The vendor extension is documented in
`Native~/third_party/xatlas/MESHLAB_PATCHES.md`; the exported C ABI changes only
by an additional function. Existing functions and the managed wire layout are
unchanged. Unity fitted-mesh quality and padding are separate validation gates.

`MESHLAB_COPY_PLUGINS=OFF` builds isolated diagnostic libraries without touching
package Plugins. Published binaries are rebuilt by `build-native.yml`.

Final Unity 6000.2.6f2 regression run with CI-built plugins `2858a74`: 468 passed,
0 failed, 2 platform/shader-specific skips across Remesh and chart quality/repair.
The diagonal fitted box previously failed the mapping check; after conditioning
alone its worst stretch after repair was 106.075 against a 94.4533 limit. With
shape-preserving packing and square normalization it passes in 0.64 s: mean
stretch 1.00003, worst 1.23503, zero overlap pairs and degenerate UV faces,
chart density CV 1.17e-6. The acceptance thresholds were not weakened.

## Solid voxel collapsed patches

The frozen Garbage_Chute_Long capture from 2026-10-10 first failed preparation
on Cap revision 8. Revision 9 closes all five rims at the captured `1e-5` tolerance
without moving donor corners: 380 welded copies, 66 added faces, nine local patches,
zero refused loops and zero remaining boundary edges. Contacts with other connected
elements remain excluded; contacts within the element being closed remain audited.

Its next native voxel stage exposed edge-connected patches of exact coincident,
oppositely wound triangle pairs. Greedy pair removal could strand neighbouring
pairs or refuse a shared edge with more than four incidences. Solid voxel preflight
now audits each connected patch atomically. A patch needs an attachment to retained
surface edges, balanced winding and zero or two retained incidences on every edge.
The candidate is accepted only when the entire result has valid closed topology
and every component has nonzero enclosed volume. Otherwise the existing native
retry/refusal remains. Ordinary Simplify retains its narrower pair-removal policy.
Source preparation, donor geometry and native binaries are unchanged.

Unity 6000.2.6f2 replay at resolution 64, captured collapse error 0.02 and a 512
atlas with three-pixel padding and chart merging:

| Solve | Removed coincident faces | Voxel faces | Simplified faces | Charts | Mean / worst UV stretch |
| --- | ---: | ---: | ---: | ---: | --- |
| Off | 148 | 5132 | 50 | 4 | 1.01647 / 1.22626 |
| On | 154 | 5126 | 48 | 5 | 1.03114 / 1.42938 |

Both replays preserve all original donor arrays, retain every face through Trim
with the production two-cell reach, and produce zero atlas overlaps, zero
degenerate UV faces and valid closed geometry through Simplify. This validates
geometry and UV, not texture Bake quality. The selected eight-class regression
battery passes 204 checks with zero failures and 11 unavailable private cases
skipped. Analytic controls cover face ordering, split vertex copies, shared fin
edges, true openings, isolated sheets, vertex-only contact, same-winding/triple
copies, zero volume and cancellation. Evidence is in the ignored
`_results~/cap-chute-long-20261010/` directory.
