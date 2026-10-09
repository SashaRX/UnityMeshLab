# Planar Cap in the Unity Remesh pipeline

The first managed integration is an opt-in **disk** operation. In Remesh & Bake,
enable **Cap planar holes (disks)** and enter comma-separated **Cap loop numbers**
(`0` for the single SandbagRoundedcorner floor opening). Numbers start at zero,
ordered by the lowest welded vertex in each oriented boundary cycle. They belong
to the current filtered capture; inspect the log again after changing the source.
This prototype has no interactive rim selector and does not infer closure intent.

## Geometry and donors

Preparation runs after small-part filtering. It uses the existing
`MeshGeometry.WeldPositions` exact position primitive before extracting contours,
and compacts the geometry-only support. The UV-aware Transfer weld intentionally
retains attribute seams and is not a physical boundary weld. There is no distance
threshold, averaging, or modification of the captured donor arrays. Original
positions, UV0/UV2, normals, tangents, colors and material/lightmap references stay
in `node.source`; `node.support` holds the welded geometry and synthetic face range.

A selected contour must be continuous, single-fan, simple in projection and wholly
planar (maximum residual <= longest chord * 1e-5; non-collinear support required).
Constrained ear clipping retains every boundary edge/vertex. Exact Lawson incircle
flips improve interior diagonals without changing those constraints. New triangles
are wound opposite the source rim. Preparation is transactional: no candidate is
returned until topology/coverage and new/source plus new/new contact audits pass.

Contact decisions use rational predicates over the exact binary32 coordinates,
with conservative AABB rejection. Shared edges/vertices allow only contact on that
simplex; improper adjacent overlaps also fail. Limits: 200,000 source vertices,
400,000 source faces, 512 edges per rim, 4,096 added faces, two million contact-pair
trials and two million ear-search trials. Cancellation is checked throughout.
These local gates do not audit all old/old intersections or certify intended shape.

The solid pipeline additionally requires closed oriented nonzero-volume components.
Partial selection may be analyzed by the managed generator, but cannot feed this
solid Remesh integration. BoundingBox skips preparation. LOD0 Two-sided shell
skips it as well; Hull is a solid operation and can use it. Multiple disks require
explicit loop numbers; torus band gaps needing Bridge and nonplanar compound holes
are not inferred or silently sealed.

Voxelize, Trim and source-fitted Simplify all use prepared support. Bake and cage
queries continue to use the original donor. Missing Cap texture projections follow
the existing policy: magenta for LOD0 misses; proxy empty texels use existing alpha
zero/neighbor fill. No synthetic UV/material donor is invented. Separate counters
report Cap-associated covered, missed and partially projected texels on CPU and
GPU evaluation. This association uses nearest support face at target centroids,
not exact provenance after voxelization/decimation.

Cap enablement, loop selection and algorithm revision invalidate Remesh and its
downstream stages. Managed support lives with its node; clear/dispose drops it.
No source scene objects or source mesh assets are edited by preparation.

## Diagnostics

Accepted preparation writes original and prepared geometry separately to
`%TEMP%/meshlab-uvmerge/cap/`. Failures during preparation, Voxelize and Simplify
retain original donors; when support is available they use capture **version 3**
with slots `source`, `prepared`, `raw`, `input`. Version 2 retains its original
three-slot meaning. The decoder accepts both versions. Settings carry explicit
disk intent/selection and metadata records `capRevision`. Private captures remain
outside the repository.

## Measured integration limits

The frozen SandbagRoundedcorner source has 685 attribute vertices, 945 faces and
one 47-edge planar bottom rim. Managed preparation welds 188 vertex copies and adds
45 triangles. Native remesh, Trim, Simplify and Unwrap succeed at **64 and 128**, both
Solve off/on, with the captured 512 atlas and chart merging enabled.

At **256**, both attempts still produce one duplicate face, two non-manifold edges
and three disconnected vertex fans. The native guard rejects them before Trim.
This is a measured unresolved native limitation, not a successful high-resolution
Cap result. An optional private regression explicitly verifies that refusal.

The earlier `DiagonalBoxKeepsCollapseBudgetAndUnwrapsWithoutOverlaps` test fails
with invalid/unmapped native UV vertices both here and on untouched commit
`31e9d11` in a separate Unity project. This pre-existing failure is retained and
reported; neither the test nor the native guard is suppressed.

Run optional real-source replay by setting `MESH_LAB_CAP_SOURCE` to decoded
`source.bin`, then running `RemeshPlanarCapTests` in the actual Unity EditMode runner.
Without that variable the six private cases are skipped; public analytic controls
still run. Local evidence is under `_results~/cap-integration-20261009/` (ignored).
