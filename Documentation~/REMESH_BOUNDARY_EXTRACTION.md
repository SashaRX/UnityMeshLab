# Cap P1: source-fan-aware boundary extraction

2026-10-09, continuing the [architecture plan](REMESH_CAP_PLAN.md).
Implemented in `Tools~/RemeshCapBenchmark/boundaries.py`. This is an offline
analysis stage: it adds no Cap triangles and changes no Unity/native code, model
assets or original position/index arrays.

## Contract and traversal

The input is finite float32/float64 positions and integer triangle indices.
The analysis view welds exactly coincident positions independently of attribute
seams. It retains each original face/corner and vertex-index occurrence; nearby
sheets are never joined through a tolerance.

Halfedge identity is `face*3+corner`, directed with the source triangle winding.
Interior edges must have exactly two opposing incidents. Duplicate/degenerate
faces, non-manifold edges and inconsistent winding produce a refusal with
witnesses, rather than partially usable loops. Singular **vertices** are instead
retained as separate source face fans for diagnosis.

For a boundary halfedge `h=(a,b)`, begin at `next(h)` in its source triangle.
If that edge is interior, cross its twin and take `next(twin)`; repeat around
the incident fan at `b` until reaching another boundary edge. Verify matching
geometric endpoint and source-fan identity. The resulting successor map must
be a permutation of all directed boundary occurrences. Decompose it into closed
cycles, visiting each occurrence once.

This pairs boundaries by source surface connectivity, not by vertex IDs,
projected angle, spatial proximity or an arbitrary geometric graph walk.
A cycle may visit the same geometric position through different fans. Such a
cycle is not automatically a simple polygon; `requiresJunctionResolution=true`
records the singular positions it touches. Closed singular sources also retain
their fan diagnostics even when they have no boundary.

The extractor does not test source self-intersections, component volume or
missing-face shape. `extractionAccepted=true` means directed boundary extraction
passed, not that the source or a future Cap is an accepted solid.

## Snapshot identity, selection and coverage

Each report has a SHA-256 identity over source array shapes, dtypes and values,
normalized to little-endian C-order bytes. Each loop identity incorporates that
snapshot and its directed halfedge sequence, starting at the minimum halfedge.
Repeated analysis of the same snapshot is deterministic, independent of array
memory layout. Changing source geometry, vertex/face indexing or orientation
creates a different snapshot; selections must not be reused across it.

`select_domains` supports all loops, a subset, or an explicitly empty selection.
It returns selected and unselected halfedge sets and junction diagnostics.
`validate_coverage` requires every selected directed occurrence exactly once,
preserves the unselected complement, and rejects duplicates, missing/extraneous
occurrences and stale snapshot/loop IDs. These are source-boundary checks;
opposing Cap-face orientation remains a later candidate audit.

The halfedge budget is checked before snapshot hashing and geometric traversal.
Early budget/cancellation refusal has a null snapshot hash. The budget bounds
triangle-corner count and fan-rotation work, not the byte size of an arbitrary
input vertex buffer. Cancellation checks occur between processing blocks and
cycle steps; NumPy bulk operations themselves are not interruptible. Refusal
never publishes a partial loop list.

## Private PileOfBricks replay

The exact existing `_results~/pile-remesh-20261009/source.bin` capture has
4,709 source faces and 255 boundary edges. Automatic extraction produces
**7 cycles**, with lengths **203, 10, 19, 6, 3, 10, 4**. Two geometric positions
have disconnected source fans; both occur on the 203-edge cycle.

At welded slot 1228, the new source-following turns are:

- `1327 -> 1228 -> 1207`, source fan 1399.
- `1133 -> 1228 -> 1298`, source fan 262.

The previous frozen nine-cycle decomposition instead paired
`1133 -> 1228 -> 1207` and `1327 -> 1228 -> 1298`, as recorded in
[the branch experiment](REMESH_CAP_BRANCH.md). Those cross-fan turns explain
why vertex-only contour decomposition was insufficient as a source boundary
contract. The different cycle count is expected when these junctions are paired
through the actual source fans.

This replay is not a new triangulation/Remesh result. The large automatic cycle
requires explicit junction/domain analysis and cannot be passed directly into
`compound.py`, whose current input contract requires simple cycles. The four
previously measured original crossings have not been repaired or re-audited
by this extractor. The old compound and branch experiments remain comparison
baselines; their manually chosen contours are not silently replaced.

Private report: `_results~/cap-structure-20261009/pile-boundaries.json`.
No source capture or private geometry report is committed.

## Reproduction and tests

```powershell
python 'Tools~/RemeshCapBenchmark/boundaries.py' --source SOURCE.bin --output PRIVATE_BOUNDARY_REPORT.json
# Repeat with snapshot-bound loop IDs from that report to select openings:
python 'Tools~/RemeshCapBenchmark/boundaries.py' --source SOURCE.bin --select LOOP_ID --output PRIVATE_SELECTION_REPORT.json
python -m unittest discover -s 'Tools~/RemeshCapBenchmark' -v
```

CLI exit 0 means extraction succeeded, including diagnosed singular vertices;
exit 2 means extraction was refused. Invalid input/selection raises an explicit
error. Output is JSON analysis only. The box structure probe now uses this
extractor rather than its previous ordinary-vertex loop walker.

**52/52 offline tests pass** with NumPy 2.1.3 and the existing pinned Triangle
20250106 dependency for compound tests. Fifteen new boundary test methods cover
box openings, interior fan rotation, touching source fans, connected pinched
rims, closed singular sources, attribute splits without mutation, tiny separated
sheets, winding/corner/face ordering, deterministic identities, rigid transforms
and scale, selected/unselected coverage, stale selections, invalid source
geometry, budgets and cancellation.

These are Python benchmark tests, not Unity EditMode tests, attribute-channel
preservation validation or production Cap certification. The implemented
[P2 follow-up](REMESH_CAP_PLANES.md) analyzes whole contours and contiguous
partial-plane hypotheses, retaining the fan occurrences and junction flags
rather than flattening them into simple loops.
