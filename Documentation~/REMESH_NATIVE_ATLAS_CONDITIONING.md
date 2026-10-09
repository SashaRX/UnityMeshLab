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
The native ABI and pinned xatlas source are unchanged.

Local MSVC headless tests, 2026-10-09: `remesh` and `uv-input` pass. The thin
closed tetrahedron regression now verifies all four faces, exact physical
corners, chart assignment and finite channels at scales 1 and 0.001.
Unity fitted-mesh UV quality remains a separate validation gate.

`MESHLAB_COPY_PLUGINS=OFF` builds isolated diagnostic libraries without touching
package Plugins. Published binaries are rebuilt by `build-native.yml`.
