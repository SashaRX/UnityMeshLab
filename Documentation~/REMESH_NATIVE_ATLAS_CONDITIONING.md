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
