# Planar Cap in the Unity Remesh pipeline

Managed integration is opt-in. In Remesh & Bake,
enable **Close holes before remesh**, choose **Closure method**, and enter comma-separated **Closure loop numbers**
(`0` for the single SandbagRoundedcorner floor opening). Numbers start at zero,
ordered by the lowest welded vertex in each oriented boundary cycle. They belong
to the current filtered capture; inspect the log again after changing the source.
Loop selection is explicit. **Caps** retains the original disk intent; **Bridge**
requires exactly two rims; **Automatic** chooses a Bridge only for a unique mutual
collar partner. Other supported contours receive local Caps. Ambiguous partners,
unsupported geometry and exhausted search budgets leave the affected contours open.

Enable **Local compound caps** to also allow a selected nonplanar cycle with one
supported decomposition into continuous planar patches. The option is off in
existing settings. Automatic mode enables local compound analysis implicitly.

## Revision 9: sequential planar arcs and explicit tolerances

When neither a two-plane split nor an audited three-plane corner succeeds, local
Cap tries a bounded disk decomposition into continuous planar arcs. This also
handles parallel planes without constructing an artificial common apex. Every
patch needs at least four supported vertices; arbitrary triangular fans remain
unsupported. Source positions and the configured **Cap plane tolerance** are
preserved. The changed boundary is re-extracted and checked after every patch.

The fallback searches rims of at most 16 edges, with 2048 contour states, 32768
plane fits, two million support samples and at most 128 minimum-patch plans per
state. All minimum-patch plans undergo the existing topology and exact contact
audits. Different surviving triangulated surfaces or exhausted budgets refuse the
whole contour; more complex plans are not tried after minimum plans fail. Other
independent contours still continue, and the solid/native guards are unchanged.

The frozen `FairStall_Steps` input has 203 donor vertices, 110 faces and one
12-edge rim. At its saved tolerance `1e-5`, no supported complete planar closure
exists. At explicitly selected tolerance `0.002` in capture coordinates, the
existing two-plane method adds 10 faces in two patches without moving vertices.
Native Remesh at resolution 64 passes closed topology with Solve off and on
(11210 triangles each). This tolerance is specific to the captured model; the
tool does not automatically widen it for other objects.

The `Garbage_Chute` capture benefits from the new arc fallback at its original
`1e-5` tolerance. With production element-scoped contacts, all five contours close:
66 added faces in nine patches, zero open edges, valid closed volumes. The legacy
strict contact oracle adds 52 faces and still refuses the two affected contours.
Closure support acceptance does not certify downstream native union geometry.

## Visual preparation and multiple holes

Use **Prepare / inspect Cap & Bridge** before **Remesh**. The separate **Cap / Bridge**
3D stage shows original faces grey, individual closure patches orange/purple and
the original rims cyan. Refused contours are red; select their **Hole rim** entry
to read the refusal reason. The summary counts refused contours, and the log and
v3 geometry captures retain their original loop numbers and reasons.
Use **Hole rim** to highlight all contours or one named node/loop at a time; these
are the same loop numbers used by **Closure loop numbers**.
Preparation does not run the native plugin and permits unselected openings to
remain. Solid Remesh checks completeness afterward; a
refusal preserves this preview. Source FBX, imported meshes and scene renderers
are not modified. The viewport camera is not reframed when changing stages.

Closures run sequentially: one selected contour or one explicit/mutually matched
Bridge pair at a time. Each candidate is audited against original faces and
previous accepted patches. Local two-plane closure re-extracts the changed rim
after its first patch. Completion reports the original loop number and selected
loops completed (a Bridge completes two). Initial loop numbers are local to each
filtered node when Keep hierarchy is enabled.

Production preparation accepts each contour independently. A refused contour does
not undo previous closures or prevent later independent contours from being tried.
A two/three-plane Cap or Bridge remains atomic: if any part fails, its entire
candidate is discarded, including generated vertices, patches and contact counts.
Cancellation and invalid source topology still stop the preparation. Invalid or
repeated selection entries are skipped with a warning and the available loop
range; valid explicitly listed Caps continue. If no valid IDs remain, preparation
shows the original contours without adding closures. Invalid Bridge selections
never infer a replacement pair. Loop numbers are contour IDs, not a hole count.
Offline callers keep strict whole-operation refusal by default;
`continueOnRefusal` opts into the same partial result used in the editor.
To inspect a subset, enter its loop numbers and prepare it separately. Caps supports more than 16 loops; Automatic is
limited to 16 selected loops to bound partner search. Other limits are 512 edges
per rim, 4096 added triangles per preparation, 200000 source vertices, 1200000
source indices and 2000000 contact trials. These are refusal limits, not silent
truncation. Analytic controls close 1, 16 and 32 independent box openings and verify
separate patches, per-loop completion order and unchanged donor geometry. Mixed
controls cover failures before/after successful caps, compound rollback, failed
Bridge pairs and oversized rims beside small valid openings. The frozen Bush_19
contact with face 26 remains refused and inspectable without changing its donor.

## Revision 8: contact audits follow connected geometric elements

Production preparation uses edge-connected face components after position weld,
not renderer or material identity. Original faces and already accepted synthetic
faces on a different element are excluded from the closure contact audit and its
pair budget, even inside one imported Mesh. Same-element faces and every new face
of the current candidate remain protected. A Bridge protects both participating
elements; subsequent candidates use the accepted topology, so a Bridge joining
two elements also joins their future contact scope.

The source and prepared face-element maps and excluded-pair count are stored in
v3 captures. Excluded pairs are not reported as proven intersections. The
Garbage_Chute capture has contour 4 on element 472 while face 467 belongs to
element 400: the unrelated face no longer refuses that Cap. Unsupported local
plane decompositions still remain open with their reasons. Native output topology
checks and the requirement for closed solid support are unchanged.

Offline array callers retain the strict/renderer-scoped legacy oracle by default;
`elementScopedContacts: true` reproduces the editor policy independently of
`continueOnRefusal`. No source vertices, material assignments or UV channels are
modified by element classification.

## Legacy renderer-scoped contact policy (revision 5)

The legacy policy uses original face ownership from the capture in
Cap/Bridge preparation. Ownership identifies a captured renderer instance, so
two instances of the same shared Mesh asset are distinct donors. Filtering keeps
that provenance. A closure derives its owner set from the original rim faces;
a Bridge includes owners of both rims.

An exact improper contact with an original face from another known donor is
reported as a warning and does not stop preparation. Contact counts and the first
added/source face pair are recorded with source face owners in capture metadata.
These contacts do not affect donor attributes or the synthetic-face mask. The
Bridge and compound searches report contacts from their accepted candidate only.

In that legacy policy, contacts with the closing donor, unknown ownership and any other newly generated
patch remain strict. Earlier accepted patches never become original donors during
incremental closure. Existing preflight, winding, fan, boundary, budget and native
output checks remain in place. Offline array callers without ownership keep strict
contact auditing; old private captures do not fabricate missing mesh identities.

The analytic external-mesh controls cover planar, two-plane, three-plane and
Bridge preparation. A full native replay of the closed box plus an intersecting
tetrahedron at resolution 64, Solve off, still produces nine open edges. The Cap
support is valid and closed; the separate native output guard refuses that result.
`NativeGuardStillRefusesMeasuredOpenUnionOfIntersectingClosedDonors` records this
remaining limitation rather than claiming the whole Remesh succeeds.

## Revision 3: Bridge, three planes and projected masks

- `RemeshBridge` enumerates cyclic seams and two best monotone zippers per seam.
  Unequal rims keep every source border vertex and edge. Each candidate must be a
  single annulus, preserve remaining boundaries, and pass exact new/source and
  new/new contact checks. Limits: 64 edges per rim, 100,000 DP states and the
  shared two-million pair-trial audit budget. Budget exhaustion rejects even a
  provisional winner. The selected strip is scored geometry, not a guarantee of
  authored shape.
- `RemeshCompoundCap` partitions one rim into three continuous planar arcs and
  intersects their planes in double precision around a local origin. It adds one
  corner in geometry-only support. Each patch is audited against freshly changed
  boundaries and all existing faces. Different surviving corners are ambiguous.
  The search is bounded to 64 rim edges, 32,768 fits and two million samples.
  Arbitrary four-or-more-plane feature graphs remain unsupported.
- Automatic mode permits at most 16 selected rims. At least 80% of each rim's
  edge co-normals must point toward its unique partner with dot > 0.6. Opposite
  box holes grow away from one another and use Caps; a cut torus uses Bridge.
  This evidence selects a candidate family; the independent audits still apply.
- Support patch IDs project to the remesh, then to the fitted simplified mesh
  with normal-filtered interior samples. This is a geometric mask estimate,
  not exact native triangle ancestry. Unanimous samples identify closure or
  original surface; mixed/uncertain faces remain a separate orange mask.
- **Simplified → Cap / Bridge faces** displays the mask. Shift-click selects a
  connected region. **Max region area (m²)** selects pure closure regions by total
  world-space area; native/UV vertex seams do not split a region. Mixed regions
  require explicit selection and inspection.
- **Remove selected closure faces** edits only working optimized geometry and
  invalidates Normals & UV and Bake. **Restore removed faces** restores the exact
  optimized baseline until the next Simplify/Remesh run. Original scene meshes,
  captured attributes and material/lightmap donors are never edited.
- Baking continues projecting rays to the original/full donor. No synthetic
  material fill or hole closing is added during projection; misses remain explicit.

Solid voxel preflight now removes only safe coincident opposite-winding fin pairs
and accepts the result only after complete closed topology and volume checks.
Sandbag at 256 succeeds for Solve off/on in the frozen replay; native raw output
still contains the fin pair. This is a guarded cleanup, not a general native
non-manifold repair. Native UV conditioning and shape-preserving packing are
documented [separately](REMESH_NATIVE_ATLAS_CONDITIONING.md).

Validation on 2026-10-09: 468 EditMode cases passed, zero failures, two
platform/shader skips; all 115 independent offline Cap/Bridge tests passed.
Native CI CTests passed on Windows, Linux and macOS. New fixtures cover all eight
three-plane box corners, transformed/reversed winding, explicit/automatic torus
closure, opposite box disks, unequal Bridge rims, obstacle refusal, projected
mask classes, world-area regions and removal/restoration invalidation. Both
FBX reference-build variants and static dependency/identifier gates pass.

## Local two-plane closure (revision 2)

Every boundary sample is tested against its support plane. Noncollinear evidence
must exceed a relative cross-product conditioning threshold of 1e-12; maximum
plane residual is bounded by the rim bounding-box diagonal times 1e-5. Only rim
turns of at least 20 degrees may be arc endpoints, avoiding subdivision-generated
fake junctions. Each arc must contain at least two edges, and its plane must
differ from its neighbor by at least 20 degrees. The analyzer enumerates all
endpoint pairs within 32,768 fits and two million support samples. Exhaustion
refuses the run; a first found hypothesis is never accepted from an incomplete
search. This is geometric evidence under explicit disk intent, not a certificate
of the missing surface's intended shape.

A unique two-plane decomposition closes the first ordered arc with the existing
constrained triangulator, using a new endpoint chord. Its exact boundary edge set
must equal the old set minus that arc plus the chord. All new/source and new/new
contacts and vertex fans are checked before accepting the patch internally.
The algorithm then extracts the actual updated contours, finds the directed new
chord, and requires the remaining contour to be wholly planar. The second patch
is audited against the original surface and the first patch. Contact-pair budgets
are cumulative across all local patches. Failure returns no prepared support and
never modifies the donor. Three-plane reconstruction, feature intersections,
automatic Bridge choice and freeform hole filling are outside this option.

Unity 6000.2.6f2: the first focused run passes **52/52** Cap cases, comprising the
existing 23 and 29 local controls. All 12 adjacent pairs of missing box faces close
with four triangles on the two known reference planes. Rotation, translation,
scale, reversed winding, uneven rim subdivision, seam-split vertices, unselected
components, obstacles on either patch, ambiguous support and cancellation/budgets
are exercised. Native Voxelize -> Trim -> Simplify -> Unwrap passes with Solve off
and on: 10,092 voxel faces -> 12 simplified faces -> six charts. The optional real
Sandbag replay confirms identical prepared geometry in disk and local modes.

The separate offline Bridge benchmark also passes equal and unequal torus rims,
and refuses disk-shaped box openings and an occupied torus gap. It tests explicit
annular intent and genus preservation; it is not a production Unity Bridge stage.
Eight known-reference sequential Cap replays (two orders for two faces, six for
three faces) also pass. Three-face replays use supplied reference patches; they
do not imply automatic three-plane generation. Current local evidence is under
`_results~/local-cap-20261009/` (ignored).

The broader `Remesh` EditMode filter runs **396 cases: 393 passed, one failed,
two skipped**. The failure is the pre-existing DiagonalBox native unwrap described
below. Skips require the Android DXT5nm harness and both Standard/URP shaders.
The newly discovered request-budget fixture had retained a six-argument reflection
call after Cap added a seventh support parameter; supplying null support restores
its existing per-pixel/per-stratum assertions. Both reference C# build variants
pass, as do **115/115** offline cases, identifier and tool-dependency checks.
After clarifying the exact-predicate comparison contract and conditional blocks
reported by Sonar, **111/111** Cap and request-padding EditMode cases pass again;
both reference build variants also pass. The predicate arithmetic is unchanged.

The updated E-project package also passes the live probe: revision 2 adds four
triangles in two local patches to the synthetic adjacent-face box, with two fresh
plane checks. The real Sandbag FBX completes Remesh -> Simplify -> Unwrap -> GPU
Bake with the local option enabled: 188 welded copies, 45 Cap faces, 6,012 voxel
faces, 952 simplified faces and 37 charts. Its source file, meta, captured donor
channels and scene dirty state remain unchanged. Bake retains the measured
21,604 complete misses (19,235 Cap-associated) and 2,917 partial misses. No new
surface donor is invented; all temporary pipeline objects are disposed.

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
skips it as well; Hull is a solid operation and can use it. Multiple disks can use
**All boundaries** or explicit loop numbers; torus band gaps needing Bridge and unsupported compound
holes are not inferred or silently sealed.

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

### Small plane deviations and complete selection (revision 4)

New settings select `all` boundaries by default when Cap is enabled. Existing
serialized numeric selections remain unchanged. The UI exposes **All boundaries**
and **Cap plane tolerance**. The latter defaults to `0.00001` in source-local units
and sets a minimum for plane fitting in planar/local/three-plane preparation.
Boundary positions remain unchanged, relative plane tolerance still applies,
and exact topology/intersection checks remain mandatory. The tolerance is part of
the Remesh cache key. Open-support errors report the remaining boundary count;
zero-volume errors are reported separately.

Frozen Park_Bench_A and BulletinBoard failures from 2026-10-09 exposed both an
overly strict plane fit and selection `0` leaving other holes open. The fixed fit
accepts the captured micrometre deviations, but full closure **still refuses**
because some proposed Cap faces intersect original geometry. Park has 10 rims:
3, 4, 6, 8 pass individually; 0, 1, 2, 5, 7, 9 fail contact checks. Bulletin has
4 rims: 0, 2, 3 pass; 1 fails. All ear-clipping start rotations without Delaunay
refinement were also tested and did not resolve those contacts. This experiment
was not shipped. No intersection guard was relaxed.

The private regression `UserFrozenCapFailuresPreserveDonorsAndRefuseIntersectingClosure`
uses `MESH_LAB_CAP_FAILURE_SOURCES` with semicolon-separated decoded Park and
Bulletin `source.bin` paths. It verifies whole-operation refusal, donor preservation
and successful non-intersecting subsets, rather than claiming solid Remesh works.
The MilitaryGabion capture named in the supplied log was already pruned from the
bounded failure cache and was not replayed.
The combined local Unity battery (including reverse UV/preview/export regressions)
passes 309 checks with no failures and seven unavailable private fixtures skipped.
The four Park/Bulletin full-refusal and partial-closure checks were executed.

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

## Actual Unity/project verification

Unity 6000.2.6f2: **23/23 new Cap EditMode cases pass**, including seam-split
geometry preservation, concave constraints, obstacle/adjacent contacts, donor
channels, missed synthetic projections, v3 capture and private native trials.
The broader selected Remesh battery passes **211/212**; the sole failure is the
pre-existing DiagonalBox UV failure reproduced on the untouched baseline above.
Both reference C# build variants compile; **115/115** offline cases pass, including
backward-compatible v2 and separate v3 prepared-slot decoding.

The real SandbagRoundedcorner FBX in the E project completed all four stages with
Cap at 64, Solve on, 512 atlas, nine bake samples and GPU projection. Result:
6,012 voxel faces, 952 simplified faces, 37 charts; original FBX and `.meta` hashes,
captured geometry/UV0/UV2/normals/tangents/colors/material IDs all remained unchanged.
The probe disposed every preview/result object and did not save meshes or scenes.

Bake is **not artifact-free**: 152,196 covered texels, 21,604 complete misses and
2,917 partial misses. Nearest-support association reports 26,622 Cap-covered texels,
19,235 Cap misses and 775 Cap partial misses. The new floor has no original textured
surface across most of its area. Geometry preparation therefore resolves the
original solid failure, while a deliberate synthetic-surface texturing policy
remains the next work item. These misses are visible, not hidden by invented UVs.
