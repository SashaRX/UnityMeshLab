# Progressive reverse UV transfer (experimental)

Setup has a separate **Run Reverse UV** action. It starts at the highest included
LOD number and advances toward LOD0. The existing forward solver is unchanged.

## Contract

- **Prepare coarsest LOD from geometry** builds one atlas across its included
  renderers in world space. Fragmented UV0 and rectangular source textures do not
  control this parameterization. Connected charts are retained where valid;
  collapsed, overlapping or excessively stretched faces get intrinsic triangle
  charts. Manual mode uniformly fits the requested resolution. Auto density mode
  preserves the requested texels per world unit and chooses a square size.
- Disable preparation to use an existing coarsest repack/UV2. Its combined atlas
  must be finite, nonoverlapping and have triangle anisotropy at most 4.
- Each next LOD projects onto the immediately preceding LOD's chart BVHs. Normal
  consistency, reach, affine interior probes, orientation and exact UV footprint
  coverage determine inheritance. A donor hole between probes is not considered
  covered. Equal-distance different charts are ambiguous.
- Shared positions in one chart receive identical UV2 values when interpolation
  differs by less than 0.001 pixel. UV0 seams, materials and other vertex attributes
  survive; only UV2 conflicts split vertices.
- New or rejected faces receive a geometry unwrap. Local failures get separate
  intrinsic triangle charts. New blocks append to the occupied canvas; inherited
  coordinates remain fixed in pixels. One final square normalization applies to
  **every LOD**, including the seed.
- Before seed preparation and projection, all inputs are cloned and exactly
  zero-area source-local triangles are removed from those detached copies.
  Double area arithmetic has no epsilon: nonzero thin faces remain. Nonfinite
  positions and meshes with no remaining faces are refused. Material slots
  (including empty slots), vertex streams, skin weights and blend shapes survive.
  Reverse UV does not change FBX files or importer settings; importer precision
  preparation remains a separate forward-pipeline operation.
- Outputs are staged before replacing the visible chain. Failed validation,
  cancellation or failed audit writes publish no new chain. Temporary cleaned
  inputs and prepared seed meshes are destroyed on success, failure or cancellation.

## Intentional overlap

**Allow projected detail overlap** permits details to reuse parent light.
Shared-edge folds are still rejected. Previous layers take precedence over
projection distance, so a closer detail cannot reorder its parent layer.

Reports retain root chart, previous LOD/node/representative face, current layer and
every positive-area under/over face relationship. Each node's face decisions, chain
keys and overlap relationships are saved in the optional `reverseTransferJson`
sidecar field. Existing sidecars remain compatible. Legacy pipeline mutations and
sidecar replacement clear stale reverse metadata. The sidecar/export path retains
the result's UV2 when the working mesh already has UV2.

**Ordered lightmap baking is not connected yet.** Use unique UVs for current
production lightmaps. The overlap option evaluates light reuse and its ancestry;
it does not change Bakery or the bake server's write order.

## Diagnostics and benchmark

Successful runs write JSON under `BenchmarkReports/ReverseUV/` or the benchmark
output override. Reports contain face decisions, per-mesh quality, complete per-LOD
overlap classification, density extrema, atlas size and local fallback counts.
Incomplete bounded overlap scans are refused.
Each node also records `sourceFaces` (output face to pre-cleanup input face) and
`removedSourceFaces`. Parent/overlay face indices continue to use output order;
resolve the parent's `sourceFaces` map to find its pre-cleanup face. These refer
to the input working mesh, whose face order may differ from the imported FBX.

**Tools → Mesh Lab → Diagnostics → Reverse UV — benchmark last capture** replays
the last frozen transfer capture in both overlap modes. A forward capture's target
becomes the coarse seed; its source becomes the fine target. The seed is prepared
from geometry. Trials use clones and do not modify scene or FBX assets. The summary
records refused trials as failures, separately from successful transfers.
Failures include the processing stage; degenerate geometry errors identify the
offending triangle. Final audits also reject excessive normalized UV distortion,
even when no overlaps are found.

Projection and seed preparation use one translated world frame near the coarsest
input. Matrix products and translation subtraction are computed in double before
rounding positions for the float BVH. This preserves world-unit reach, scale,
relative renderer positions and reflection winding while avoiding precision loss
from a model's world position. Output vertex positions stay source-local.
The low-level projection solver still rejects source-local zero-area triangles
before transformation; the workflow and benchmark clean detached inputs first.
Rounding cannot turn a collinear source face into an apparently valid triangle.
Errors distinguish source defects from projection-frame collapse and include vertex IDs.

The Kamaz_Typhoon capture on 2026-10-09 contains a valid 0.24-micrometre-wide
LOD1 face that collapses at a roughly 100-metre world position, and a separate
exactly collinear source face. Cleanup removes the latter on its working copy.
The earlier reported working face 6768 cannot be equated
with imported FBX face order after weld/processing. Exact captured triangles are
regression fixtures; this does not certify the whole Kamaz chain as successful.
Set `MESHLAB_REVERSE_KAMAZ` to the frozen probe directory and run
`ReverseUvTransferTests.FrozenKamazFullLodChainAfterCleanup` with
`MESHLAB_REVERSE_OUTPUT`. It exercises all five renderer meshes in LOD2 → LOD1 →
LOD0, three scene transforms and the imported identity frame, in both overlap
modes. `kamaz-trials.json` reports refusals rather than disguising them as accepted
transfers. The 2026-10-10 run removes one LOD1 face in each trial and prepares the
seed, but all eight trials still refuse projection: one scene frame collapses a
different thin face, and the other frames fail the strict UV stretch gate.

For isolated EditMode runs, set `MESHLAB_REVERSE_MANIFESTS` to semicolon-separated
manifest paths, `MESHLAB_REVERSE_OUTPUT` to the output directory, and run
`ReverseUvTransferTests.FrozenCapturesProduceIndependentReverseBenchReports`.
Without inputs this optional corpus test is skipped. Analytic tests cover three
levels, fragmented UV0, append placement, optional overlap, layer persistence,
seams, donor holes, ambiguity, transforms, cancellation, resource limits, raw
Float16/scalar UVs, materials, blend shapes, variable bone weights, source
immutability, sidecar persistence and repeatability. Workflow tests check checker
refresh, camera preservation and rollback after failure.
Set `MESHLAB_REVERSE_BASELINES` to corresponding semicolon-separated prior summary
paths to require every previously accepted pair and overlap policy to remain
accepted. Accepted audits are checked for invalid/degenerate/out-of-bounds UVs,
distortion, complete overlap scans, unexpected overlaps and finite density.
Exact Kitchen triangles also cover corner-order independence of the double
metric and refusal of float placement collapse without source geometry changes.

## Current limits

This is a conservative face projection prototype. A triangle crossing incompatible
donor chart boundaries becomes new UV; it is not split along the seam. Intrinsic
rescue can create many islands. Expanded blocks are not globally compacted. New
blocks use median seed density; density extrema are measured rather than assumed
equal. Unity's connected unwrap uses its internal margin; pixel padding governs
block borders and intrinsic rescue shelves, not every connected chart edge.

Preparation supports 50000 combined seed faces, each transfer input supports
250000 faces, and the UI limits the atlas to 8192 pixels. Projection, footprint and
overlap checks have comparison budgets. Very thin triangles can lose a valid UV
metric at float precision after placement and are refused. Native unwrap runs on
the editor thread; projection runs on a worker with cancellation support.

Next: split detailed triangles at donor chart boundaries, then place unmatched
regions into free atlas space without moving inherited placements.
