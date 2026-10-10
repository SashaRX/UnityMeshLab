# Reviewed meshoptimizer remesher extension

Upstream: https://github.com/zeux/meshoptimizer
Snapshot: `9e1f07b159d3cb777f1c67ed31fc11fd117986f4` (v1.3).
Original file: `src/remesher.cpp`. License: MIT, reproduced in `LICENSE.md`.

`Native~/CMakeLists.txt` excludes only upstream `remesher.cpp` and compiles this
reviewed copy against the headers and remaining sources from that exact snapshot.
No downloaded dependency tree or checked-in plugin binary is modified.

Local changes:

- Occupancy midpoint positions undo the rasterizer's one-cell padding: padded
  index `g` has source-space center `g - 0.5`. Component center/bounds checks
  cover three axes, resolutions 32/64 and scales 0.001/1/1000.

- Grid access and row offsets are templated. Grids up to 256 retain upstream's
  byte storage and behavior; 257 through 1024 use unsigned 16-bit offsets.
- Interior sentinels, row comparisons and flood-fill temporary values use the
  grid's actual type. The zero row is bounded to 1024 entries.
- An explicit release-build guard rejects resolutions outside 4 through 1024.
  The packed voxel coordinates still use upstream's ten bits per axis.
- Internal option `1 << 29` extracts the 0.5 isosurface of the filled binary
  occupancy grid using six conforming tetrahedra per cell. This is the editor's
  solid recovery path only, after strict rejection of ordinary voxel output.
  It uses occupancy midpoints, without quadrics/source fitting or shell mode.
  Ordinary corner-based extraction and its byte/16-bit grid paths are unchanged.
  Count and emission use the same cases; the native five-million-face budget
  still applies before the output corner buffer is allocated.

The grid is still dense: the 16-bit grid alone uses 256 MiB at 512 and 2 GiB at
1024. Occupied-voxel quadrics and output/weld buffers add to this; the bridge's
five-million-triangle budget is preserved. Raising the cap above 1024 requires a
new coordinate representation and a memory strategy, not another UI adjustment.

Update this copy together with the pinned upstream commit. Native regression tests
exercise long occupied rows, coordinate extents, shell/solid modes and both APIs'
rejection of 1025. Build and publish platform binaries with `build-native.yml`.
