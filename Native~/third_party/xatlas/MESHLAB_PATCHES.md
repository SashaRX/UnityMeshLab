# Reviewed Mesh Lab extension

The pinned xatlas implementation has a local extension: `PackOptions.preserveChartShape`
(default false). Native Remesh unwrap and its managed repair pack opt into it.
The existing `xatlasPackCharts` export retains its shape policy for ordinary UV
Transfer; `xatlasPackChartsPreserveShape` is used by Remesh and Reverse UV packing.

Upstream ceil-rounds each chart's width and height and stretches UV coordinates
independently to fill that pixel rectangle. Subpixel chart dimensions can cause
large anisotropy and destroy consistent area density even after parameterization.
The extension retains the uniformly scaled UV coordinates and uses the rounded
rectangle only for raster allocation and placement. Chart images and padding are
generated from the retained shape, before packing; no post-pack UV correction
can introduce untested overlaps.

This adds no field to the exported C ABI or serialized managed settings. Other
xatlas users retain the upstream behavior unless they explicitly opt into it.
Keep this patch when updating the pinned vendor snapshot; native CI and the UV
shape regressions must pass before publishing Plugins.

## Equivalent bit-image operations

`bit_image_overlap.h` implements the same `BitImage::canBlit` predicate with
one source-word traversal, an aligned fast path, and separate low/high fragments
for unaligned destinations. Empty source words skip destination reads. The first
fragment rejects common collisions before loop setup, with MSVC forced inlining
matching the vendor's hot accessors. It keeps
the original clipped read and storage-tail semantics, including dirty tail bits
after resizing. It does not alter chart rasterization, candidate order, random
state, rotations, padding, scoring or tie breaks; it affects both packing APIs.

Padding dilation uses the same eight-neighbour kernel, combining three rows of
64-bit words and carrying horizontal neighbours across word boundaries. Source
tail bits are masked before propagation, and every output word is overwritten.
The last storage word is cleared outside the image width just as in the original
pixel loop. The number of dilation passes and every padded pixel stay unchanged.

`bit_image_test.cpp` compares the predicate with the original traversal on all
64 alignments, word boundaries, sparse/empty masks, dirty tails and clipped rows.
Repeated dilation passes compare every storage bit with a pixel reference,
including zero dimensions, single rows and masks at both sides of word edges.
`packing_replay.cpp` compares the complete native output bit for bit with private
captures from the managed benchmark, including refused canvas trials. Keep both
the shape extension and this equivalent overlap traversal when updating xatlas.

## Optional oneTBB row search

`MESHLAB_TBB_PACKING=ON` enables `parallel_chart_search.h` for large brute-force
windows. Rows read the same immutable atlas in an eight-thread oneTBB arena.
The first eight rows and small windows remain scalar. Results are reduced in
the original row/rotation order, preserving the first-inside early return and
stable same-metric/distance ties. Atlas writes and random-search state remain
serial. Atomic earliest-inside pruning only skips rows after an already found
fit. It does not select the winner according to worker completion order.
Already-running late rows stop at the next candidate after an earlier inside
fit appears. A shared feasible metric prunes only strictly higher metrics;
equal metrics remain eligible for stable tie/first-inside reduction. The first
eight row results use stack storage, and the parallel table is allocated only
if that scalar prefix fails.

`parallel_packing_test.cpp` compares 607 synthetic searches with the scalar
reference, including forced canvas growth, block alignment, rotation priority
and first-inside versus closest-position ties. The normal build leaves this
experiment disabled and has no oneTBB runtime dependency. Private ON builds use
pinned 2021.12.0 sources and must replay every real packing request bit for bit.

## Optional ISPC masks

`MESHLAB_ISPC_PACKING=ON` compiles `Native~/src/ispc_masks.ispc` with SSE2/AVX2
runtime dispatch for large integer mask comparisons and dilation. The scalar
immediate-collision prefix and small masks remain unchanged. No float, scoring,
candidate-order or shape decisions are delegated to SIMD. Expanded reference
tests and all 74 model requests match exactly, but measured native packing does
not improve and Cafe overlap regresses. Keep the experiment OFF in normal
builds; it requires `MESHLAB_COPY_PLUGINS=OFF`. Results and other library trials
are documented in `Documentation~/INTEL_LIBRARY_BENCHMARKS.md`.
