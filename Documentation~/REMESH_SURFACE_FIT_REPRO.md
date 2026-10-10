# Remesh surface fitting and UV strategy comparison

## Frozen baseline and bounded correction

Baseline `fc62cc8`, Unity 6000.2.6f2 / DX11, CI-published native DLL unchanged.
These measurements use voxel revision 1; the coordinate correction in voxel
revision 2 requires fresh downstream acceptance results. See
[solid recovery position correction](REMESH_SOLID_RECOVERY.md#voxel-revision-2-source-space-position).
Inputs are prepared, closed Park_Bench_A and Garbage_Chute captures from
[solid recovery](REMESH_SOLID_RECOVERY.md). Settings: resolution 64, collapse
error 0.02, small-part pruning, 512 atlas, padding 3, chart merging on,
133-degree crease, three normal-smoothing iterations, area-and-corner weighting.
Source geometry and FBX files are not modified.

Park's pre-fit collapse was discarded because it produced 3,774 triangles,
above the existing 3,240.6 budget relative to the ordinary 2,946 result. The
post-collapse pass moved 1,003 vertices, but final coarse fitting made no moves:
eligibility required every incident edge to be at least two cells long. Thin
cross-section edges excluded long approximation patches on rods.

Eligibility now uses the longest edge; the shortest edge still limits motion.
One-ring orientation, area and shape gates remain, followed by complete
topology/boundary/component and bidirectional sampled-distance checks. On Park,
893 attempted moves improved mean distance but worsened the worst reverse probe.
Half, quarter and eighth motion are retried from the original snapshot. Quarter
motion accepts 636 moved vertices, with maximum displacement 0.109 cells. The
existing 5%/0.005-cell RMS and 0.025-cell maximum-error bounds are unchanged.
Accepted full fits remain available; Simplify keys include a fitting revision.

The first accepted geometry produced 83 islands, but worst UV stretch rose from
2.851 to 5.386. Broad merging used the original atlas's worst stretch 5.027 as
its bound, even though narrow merging had already reduced it to 3.121. Broad
trials and final selection now use narrow-result quality bounds. Packing,
density, overlap and winding gates remain; Unwrap keys include a merge revision.

## Measured result and tradeoff

Distances are world-space area-weighted probes, not certified Hausdorff bounds
or intersection checks. Target samples include centroids and edge midpoints;
reverse samples are source face centroids.

| Park at 64, Solve On | Baseline | Corrected |
|---|---:|---:|
| Triangles | 2,946 | 2,946 |
| Target-to-source RMS | 0.0415130 | 0.0405652 |
| Target-to-source maximum | 0.101031 | 0.0999288 |
| Source-to-target RMS | 0.0142163 | 0.0140378 |
| Source-to-target maximum | 0.0392666 | 0.0398669 |
| UV islands | 112 | 113 |
| Mean / worst stretch | 1.03762 / 2.85105 | 1.03929 / 3.12150 |
| Overlap / degenerate / outside | 0 / 0 / 0 | 0 / 0 / 0 |

This is a small surface-fit improvement with a bounded reverse-maximum/UV
tradeoff, **not reduced atlas fragmentation or restored thin-part silhouettes**.
Park Solve Off is unchanged (355 islands). Garbage_Chute is unchanged with
Solve Off/On (68 triangles, 4/5 islands), including distance/UV metrics.

The additional resolution-128 research run is not a quality acceptance success.
Solve Off produces 7,218 triangles but native Unwrap refuses invalid/unmapped
vertices before managed merging. Solve On reaches 7,192 triangles and a clean
overlap scan, target/source RMS 0.0182138/0.00650474, but worst stretch 14.2755
across 259 islands. A clean overlap scan alone does not certify quality. No
resolution, atlas-size or default fitting setting is increased automatically.

## Reproduction

Set `MESH_LAB_CAP_COMPARISON_MANIFEST` to an absolute JSON path:

```json
{
  "output": "C:/absolute/output",
  "measureSurface": true,
  "candidates": [
    { "caseName": "Park_Bench_A", "method": "ours_auto",
      "path": "C:/absolute/prepared-support.bin", "resolution": 64 }
  ]
}
```

Run `RemeshCapComparisonTests.ReplayAuditedNativeCandidates` to record successes
and refusals as data, or `VerifyAuditedNativeCandidates` to require all requested
Solve Off/On rows to pass. `measureSurface` adds both distance directions and
exports voxel/simplified geometry without changing production processing.

For byte-level repeat checks, use final simplified meshes as candidate paths,
set `unwrapRepeats` to 2–10 and run `VerifyRepeatedUnwraps`. Every repeat requires
complete clean atlas scans and exact 3D corner preservation. SHA256 includes
positions, indices, UVs, chart IDs, normals and tangents. Results are written to
`unwrap-repeats.txt`; determinism failures fail the test. Private inputs are opt-in.

Public controls cover short cross-section edges, reduced motion around a source
protrusion at scales 0.001/1/1000, unchanged boundaries/input buffers, opposite
sheets, cancellation, invalid topology and UV strategy quality/packing.

## Avoiding provably losing packing retries

The counts in this subsection are the historical voxel-1 CafeChair replay.

The saved CafeChair_12 settings use brute-force packing. A narrow result has 754
charts; the broad 588-chart candidate fails its stricter post-pack stretch bound
(9.62185 versus 9.3071). The old retry then halves the merge budget to 1079 from
a 2747-chart baseline. Even perfect joins leave at least 1668 charts, so this
candidate cannot beat the 754-chart result regardless of stretch or packing.
The unpruned replay was stopped while pursuing further losing retries; it is
not counted as a successful full-stage test.

The halving/first-success path now stops before generating or packing such a
candidate. Every subsequent budget is smaller, so none can win either. Equal
chart counts remain eligible because the small-chart count can improve. The
interactive refined search stays unchanged: its budgets can increase again.
Existing accepted checkpoints, quality bounds and final strategy choice remain
unchanged; there is no timing-based cutoff or automatic packing-setting change.
The public policy control includes the measured CafeChair counts, equal-count
small-chart improvement and the initial unlimited-budget boundary.

## Roundoff face normals after corrected voxel recovery

Voxel revision 2 exposes three almost-collinear CafeChair triangles after
Simplify. All three native charting variants give those faces zero UV area.
The complete atlas scan reports three degenerate faces and thirteen overlapping
pairs; preserving the certification guard correctly rejects that output.

Surface-fit revision 2 only changes final fitted regularization. When exactly
one side of a diagonal has triangle quality at most `32 * FLT_EPSILON`, use the
healthy adjoining face's normal for the existing flip proposal. Source feature
protection, new-face area/orientation, topology, component and bidirectional
surface-distance checks still apply. Vertices and triangle count stay fixed.
Six scale/protected-source controls and thirty existing surface-fit controls
pass. A protected source edge still prevents the flip; genuine folds retain
their prior behavior. Simplify and UV cache keys include the surface revision.

On CafeChair this enables four additional audited flips and removes all three
zero-area UV faces. Existing seam-cut repair then removes twelve overlapping
pairs, yielding a complete clean 30,128-face / 3,641-island atlas before optional
chart merging. Mean/worst stretch is 1.03076/12,219.9: finite certification is
not acceptable unwrap quality for these thin triangles. Full saved-setting
chart merging remains a separate expensive step; this intermediate atlas is
not a successful final pipeline assertion. The private replay now saves exact
Simplify geometry before Unwrap, and refusal messages include scan counters.
