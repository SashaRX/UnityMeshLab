# Local Cap: topology, geometry and Remesh integration

Research date: 2026-10-09. Repository baseline: `ba16d67`, PR #225,
`codex/remesh-and-bake`. This document proposes experiments; it does not add a
production Cap, a dependency, or a native change. Conclusions about our models
below come from the recorded captures, not from running the cited libraries.

The subsequent [architecture and implementation plan](REMESH_CAP_PLAN.md) maps
these ideas onto the actual code and records the known-box structure probe.

## Decision

Use a per-opening pipeline: **extract the actual source boundary, classify its
surface domain, construct a constrained local patch, and validate the assembled
mesh**. A triangulator is one part of this pipeline. Filling every boundary
cycle independently, or projecting everything onto a world-space floor, is not
a general repair algorithm.

Keep three operations explicit:

- **Cap-only:** preserve all original positions, faces and attributes; add faces
  and optionally interior vertices.
- **Local repair:** replace a bounded source collar when its connectivity or
  geometry prevents Cap-only closure. Preserve its outer interface and report
  every changed original face and displacement.
- **Reconstruction:** replace a larger surface when local constraints cannot
  be satisfied. This has a different shape-preservation contract.

An open boundary does not say whether the missing surface should be restored.
A cup mouth, a deliberately open sheet, and an accidental missing face can have
similar boundary graphs. Automatic closure needs a stated intent, a size policy,
or a selected opening. A thick-walled vessel can have a closed manifold surface
while its cavity remains open to the air: its inner and outer walls meet at the
rim. A handle tunnel is likewise not necessarily a boundary hole.

## What published algorithms establish

| Source | Relevant result | Limit for our use |
| --- | --- | --- |
| [CGAL 6.2.1 hole filling](https://doc.cgal.org/latest/PMP_Mesh_repair/group__PMP__hole__filling__grp.html) | Near-planar boundaries can use a fitted plane and constrained 2D Delaunay; fallback searches 3D Delaunay facets and optionally a wider cubic search. Preconditions differ by method. | Successful triangulation is not a solid certificate. Use explicit budgets rather than allowing unbounded expensive fallback. |
| [CGAL repair manual / Liepa-based pipeline](https://doc.cgal.org/latest/PMP_Mesh_repair/index.html) | Triangulate, refine, then fair. Patch selection prioritizes the worst dihedral angle, with area as a tiebreaker. Complex boundaries can still produce self-intersections. The manual also distinguishes combinatorial vertex splitting from geometric manifoldness. | We need independent intersection and geometric vertex-link checks. Duplicating coincident vertex indices cannot satisfy our position-welded contract. |
| [PMP `fill_hole`](https://www.pmp-library.org/group__algorithms.html) | Angle/area triangulation followed by remeshing and curvature fairing; intended for simple holes with manifold boundary vertices. | Not a general solution to branching or intersecting boundaries. |
| [Zou, Ju, Carr, 2013](https://www.cs.wustl.edu/~taoju/research/triangulate_final.pdf) | Extends dynamic programming to multiple non-planar polygons, including holes with islands; optimizes additive triangle/adjacency costs and enforces manifold triangulation. Its definition permits geometric self-intersection. | Useful for compound 3D domains, but geometric acceptance remains separate. Optimal means optimal for the specified cost/candidate set, not recovered original shape. |
| [Yip, Stahl, Schellewald](https://arxiv.org/abs/2311.12466), [reference implementation](https://github.com/Mauhing/hole-detection-on-triangle-mesh) | Boundary detection handles singular vertices in edge-manifold triangle meshes. | Detecting a boundary is different from constructing an embedded, manifold filling. Do not extend the premise to edges with more than two incident faces. |
| [MeshLib fill/stitch API](https://meshlib.io/documentation/Cpp/group__FillHoleGroup.html), [parameters](https://meshlib.io/documentation/Cpp/structMR_1_1FillHoleParams.html) | Separates filling from bridging two holes; exposes fill plans, metrics and a new-face mask. Parameters include stopping before bad triangulation. | Some paths fall back to a centroid fan; other options can create a degenerate band. Neither is suitable as an unchecked fallback under our strict guards. |

These are algorithm references and potential offline comparators, not an approved
Unity dependency selection. Any library integration needs a separate build,
distribution and license review. The 2013 author-hosted paper was available to
the search index; direct full-file retrieval timed out during this research.

## Evidence from our benchmark

The [independent contour experiment](REMESH_CAP_INTERSECTIONS.md) produced a
topologically clean candidate that nevertheless added **46 crossing/overlap
pairs and 20 nonadjacent contact pairs**. These are face pairs, not counts of
independent defects. The large projected boundary contained other contours;
independent full lids occupied the same surface domain.

The [compound experiment](REMESH_COMPOUND_CAP.md) eliminated new geometric
intersections using interior refinement, but retained one disconnected vertex
fan. It also used an explicit world-space floor: an experimental shape choice,
not local continuation of the missing surface.

The [branch reconnection](REMESH_CAP_BRANCH.md) removed that fan without moving
source vertices or creating new intersections. Yet **four original source
crossings remained**. At resolution 128, raw native output still had **20
duplicate faces / 47 non-manifold edges** with Solve off, and **21 / 48** with
Solve on. Thus the Cap was accepted locally, but the source was not certified
as a solid. Those measurements do not establish that Cap causes or fixes the
remaining native defects.

## Proposed extraction and classification

1. Preserve the source arrays and a face-provenance map. Build a geometry-only
   connectivity view with the same exact position equality as `RemeshTopology`.
   Attribute seams are not holes. Do not merge nearby sheets by a global epsilon.
2. Enumerate directed boundary halfedges from edge incidence. Record the
   adjacent source triangle, its opposite vertex, component and face-fan identity
   at each endpoint. In an ordinary manifold boundary, traverse through the
   incident face fan to the next boundary edge. Do not choose the next edge
   merely by vertex ID, proximity or projected turning angle.
3. Preserve separate corner/fan occurrences at singular geometric vertices for
   diagnosis and traversal. This bookkeeping is not a repair: acceptance must
   still inspect the final geometry after position welding. The frozen cycles
   in our branch case joined endpoints across the wrong source fans.
4. Analyze a small source collar around each opening: local edge lengths,
   boundary diameter, fitted-plane residual, face orientation, feature edges,
   nearby sheets and existing intersections. Edge-non-manifold and inconsistent
   winding configurations require explicit repair or refusal.
5. Group loops only when they belong to the same intended missing surface.
   Projected containment or opposite winding alone cannot establish that two
   3D boundaries form one domain. Once a planar compound domain is established,
   build its containment hierarchy, including islands and deeper nesting.

## Construction choices

| Opening | Proposed local construction | Acceptance concern |
| --- | --- | --- |
| Simple, nearly planar rim | Fit a local plane; verify its projected polygon is simple; constrained Delaunay inside that domain. Keep original 3D boundary coordinates. | A small plane residual does not rule out projected crossings, folds or collisions. |
| One rim crossing several planar faces | Segment contiguous boundary chains by plane support; reconstruct compatible plane-intersection feature edges; triangulate each resulting planar patch separately. | A single plane or smooth patch can remove the original corner; plane fitting alone does not establish missing-edge connectivity. |
| Warped rim | Boundary-aware 3D triangulation using angle/area costs; adjacent source triangles inform continuity. Refine inside the patch if needed. | Individually admissible triangles may collide with each other; complete candidate checks and bounded alternative search are required. |
| Narrow crack or two compatible borders | Stitch the appropriate sides, preserving ordering and orientation. Identical duplicate seams can be joined without inventing a lid. | Similar distance alone is insufficient to identify matching surfaces. Boundary resampling requires coordinated source-edge edits. |
| Outer rim with interior boundaries | One compound patch retaining those boundaries; planar constrained triangulation when justified, multiple-3D-polygon method otherwise. | Filling each rim independently overlaps islands; domain membership and vertex links must remain valid in 3D. |
| Touching or pinched boundary | Diagnose source fans; test a bounded Cap connection, or explicitly retriangulate a local source collar. | Index duplication or epsilon displacement must not disguise disconnected geometric fans. |
| Intentional opening | Preserve it, or create a separately selected Remesh proxy closure. | The shape and bake behavior of a lid cannot be inferred uniquely from the rim. |

Refinement should target local source sampling and Remesh cell scale without
introducing needless tiny triangles. Triangle shape quality helps downstream
decimation, but does not prove the missing surface's shape is correct. Fair only
new interior vertices with a fixed border initially; preserve sharp features.
Reject or shorten any smoothing step that introduces intersection, inversion,
or violates the chosen local shape prior.

There is no original triangle surface inside a genuine hole. Therefore distance
to the original mesh cannot certify the restored interior shape: it can instead
pull a valid lid towards the rim. Use a declared plane/continuation/user hint,
report patch excursion against that prior, and expose ambiguous cases.

## Whole-contour and partial-contour plane detection

The user's adjacent-missing-box-faces example requires a piecewise-planar
hypothesis. One closed six-edge boundary contains two consecutive three-edge
chains in different planes. Their missing common edge must be reconstructed
before independently triangulating the two faces. A single arbitrary
triangulation followed by fairing need not preserve that corner.

Proposed bounded classifier, after source-fan-aware boundary extraction:

1. Fit a plane to the entire cyclic contour. Account for boundary arc length so
   subdivision density does not dominate the fit. Require non-collinear support
   and check maximum endpoint distance, not only average/RMS residual. Collinear
   chains do not determine a unique plane. For straight mesh edges, endpoint
   distances suffice to bound distance along the segment.
2. If the whole contour passes the geometric tolerance and simple-projection
   checks, retain the single-plane candidate. Report the tolerance and residual;
   do not force original boundary positions onto the fitted plane.
3. Otherwise generate plane candidates from contiguous non-collinear chains and
   grow their support along the cyclic boundary, including across its start/end
   seam. Refit and verify the entire proposed chain at each extension; a series
   of locally small turns must not accumulate into an accepted curved chain.
4. Compare bounded alternative segmentations. Favor adequate support and fewer
   planes subject to residual limits; merge adjacent compatible candidates.
   Individual edges or arbitrary three-point fits are insufficient evidence of
   a separate face. Inspect source feature directions and collar geometry, but
   do not require adjacent source-face normals to equal the missing-face normal:
   a box's surviving side faces meet the missing face at a sharp angle.
5. Construct intersection-line candidates for compatible neighboring planes.
   An infinite line is not yet a valid missing edge: verify its finite endpoints,
   correspondence to chain junctions, face closure, winding and consistency with
   the surrounding mesh. Reject ill-conditioned near-parallel intersections or
   ambiguous connectivity. Do not infer right angles for arbitrary models.
6. Triangulate the resulting domains independently with shared reconstructed
   edges as fixed constraints. Preserve those features during refinement and
   fairing; apply the complete topology/intersection contract to their union.
   Failed piecewise-planar candidates may enter the bounded 3D-patch search or
   be refused with the reason recorded.

Plane-fit tolerance is a geometric classification parameter, not a welding
tolerance. Report it in mesh units and relative to local edge lengths, opening
diameter and native cell size. Account for coordinate precision and nearby-sheet
separation; do not use a voxel-scale threshold that silently merges thin layers.
Numerical equality predicates and permitted shape approximation stay separate.

Required analytic case: a box with two adjacent faces removed. The expected
candidate restores two planar quads, four triangles and their shared sharp
edge, without moving or adding boundary vertices. Test rotations, scales,
uneven edge subdivisions and contour start offsets. A box with opposite faces
removed instead has two separate planar boundary loops. Include a three-face
opening and a curved rim as controls for over-segmentation and ambiguity.

This is a proposed classifier and fixture contract, not a measured implementation
result. Geometry alone does not uniquely determine every missing surface.

## Acceptance contract

Apply all checks to the complete assembled candidate, including its final
float32 representation used by Unity/native code:

- Every preserved source face retains its positions, indices and attributes
  under Cap-only operation. Each selected boundary edge gains exactly one
  oppositely directed incident patch face; unselected boundaries are unchanged.
- Interior patch edges have two opposite incidences. No duplicate, degenerate,
  non-finite or inconsistently wound faces are introduced.
- Each geometric vertex has one connected manifold face link: a path on an
  open boundary, a cycle in the interior. Merely checking edge incidence misses
  bow-tie vertices.
- Verify the intended patch topology and boundary coverage. A connected disk
  has Euler characteristic 1; an annulus has 0. More generally, a genus-zero
  patch with b boundary components has characteristic 2-b. These values do not
  replace connectivity/link checks. Joining source components requires an
  explicitly intended topology change.
- Query an AABB/BVH against **all** source faces and previously accepted Caps,
  not only the collar. Check patch self-intersections as well. Shared features
  permit only their expected contact; adjacent faces may overlap beyond them.
- Audit original intersections separately from new ones. A local Cap can pass
  while global solid readiness fails. Repairing the original crossing is a
  separate source-edit operation.
- Re-run topology and geometry checks after smoothing and float conversion.
  Maintain separate budgets for shape error and numerical robustness; do not
  substitute an arbitrary weld tolerance for exact incidence.

Use transactional candidates: failed attempts leave the source unchanged, with
a specific reason and captured offending faces. Bound candidate search,
refinement, memory and execution time, and honor cancellation.

## Integration into our stages

The current `RemeshPipeline` passes `node.source` both into voxelization and into
Trim; `RemeshSurfaceRefine` builds its fit BVH from that source. Material/bake
projection also uses `node.source`. Production currently has no Cap stage.

For a future proxy-only Cap, preserve two references:

- `bakeSource`: original faces, UV/material/tangent data and donor policy.
- `remeshSupport`: original geometry plus accepted Cap, with synthetic-face
  provenance. Voxelization, Trim and geometric fitting must have an explicit
  policy for using this closure.

Adding Cap solely at voxelization while keeping later Trim/fit tied to the open
source is an integration risk; it has not been reproduced as a Cap regression
yet. Synthetic faces must not silently become UV/material projection donors.
The new surface needs an explicit bake policy: legitimate neighboring sampling,
a designated cap material, or an unresolved projection diagnostic.

Keep native output guards. Clean input is a prerequisite for interpreting a
native failure, not a guarantee that voxelization/Solve preserves manifoldness.

## First implementation experiment

Start offline with **source-fan-aware boundary extraction and simple planar local
Caps**, reusing the existing exact intersection and topology auditors. Keep the
boundary fixed, refuse ambiguous domains, and record each failure class. Next
add contiguous partial-plane detection and the adjacent-missing-box-faces case
above. Leave 3D compound filling and local source-collar edits as subsequent
experiments.

Analytic fixtures should include a concave planar rim, a warped rim, annular and
nested domains, a narrow crack, touching loops, a bow-tie vertex, thin nearby
sheets, reversed winding, preexisting intersections and collisions between two
Caps. Repeat representative fixtures under rotation, translation, scale and
float32 conversion. Include an intentional cup mouth and a closed thick rim.

For each candidate record source preservation, changed boundary coverage,
connected components, vertex links, new/existing intersections, minimum area,
triangle quality, patch excursion and deterministic refusal reason. Use known
analytic surfaces to measure reconstruction error; private models lack ground
truth for a missing face.

Then replay the private failures and native resolutions 64/128/256 with Solve
off/on, checking raw output, Trim and Simplify independently. Comparators should
use the same source, opening selection and budgets. A refusal is an explicit
outcome, not a reason to lower topology guards. No production automatic Cap
default should follow from this research alone.

## MeshLab Close Holes code audit — 2026-10-10

This follow-up inspects upstream source against our `df08572` baseline. It does
not run MeshLab on private captures or change our production Cap defaults.
MeshLab `main` was pinned to
[`71e7b0b`](https://github.com/cnr-isti-vclab/meshlab/tree/71e7b0b53cd98f520f84cb17b31bac6f1ef947cc);
its `src/vcglib` gitlink points to
[`c94ef4e`](https://github.com/cnr-isti-vclab/vcglib/tree/c94ef4e12e9ea3ae986d9af91005be8328d13719).
The normal filter and the legacy interactive editor are different paths.

### Normal Close Holes filter

[Filter dispatch](https://github.com/cnr-isti-vclab/meshlab/blob/71e7b0b53cd98f520f84cb17b31bac6f1ef947cc/src/meshlabplugins/filter_meshing/meshfilter.cpp#L1483-L1514)
requires edge manifoldness, then calls VCGlib ear filling. There is no automatic
vertex weld in this dispatch. The
[options](https://github.com/cnr-isti-vclab/meshlab/blob/71e7b0b53cd98f520f84cb17b31bac6f1ef947cc/src/meshlabplugins/filter_meshing/meshfilter.cpp#L514-L522)
include a boundary-edge-count limit (default 30), selected-boundary faces,
selection of new faces, intersection prevention (default on), and optional
patch refinement (default off). Edge count is not a physical hole-size measure.

[VCGlib ears](https://github.com/cnr-isti-vclab/vcglib/blob/c94ef4e12e9ea3ae986d9af91005be8328d13719/vcg/complex/algorithms/hole.h#L285-L603)
operate directly on 3D rim vertices. A priority queue prefers convex ears, good
triangle shape and smaller dihedral changes against adjacent faces. Accepted
triangles update neighbouring candidates. Intersection checks cover the rim's
incident face fans plus accepted triangles, rather than the whole connected
element. If the queue stalls, accepted ears remain. The returned hole count is
incremented before filling, so it counts attempts, not audited complete closures.

The same header has a separate
[dynamic-programming triangulation](https://github.com/cnr-isti-vclab/vcglib/blob/c94ef4e12e9ea3ae986d9af91005be8328d13719/vcg/complex/algorithms/hole.h#L655-L901)
using angle and area costs. It is not invoked by this filter; it should not be
described as the normal MeshLab Close Holes algorithm. Neither path explicitly
reconstructs missing planar feature edges or chooses Cap versus Bridge.

### Refinement is a separate construction stage

[Refine Filled Hole](https://github.com/cnr-isti-vclab/meshlab/blob/71e7b0b53cd98f520f84cb17b31bac6f1ef947cc/src/meshlabplugins/filter_meshing/meshfilter.cpp#L1516-L1549)
enables selected-face isotropic remeshing: split, collapse, flip and smooth.
It runs three cycles of coarse (3L), fine (L/3), and target (L) sampling, with
5/3/2 iterations respectively. Projection and source-distance checks are off;
the default L comes from 3% of the whole mesh bounding-box diagonal.

The
[smoothing selection](https://github.com/cnr-isti-vclab/vcglib/blob/c94ef4e12e9ea3ae986d9af91005be8328d13719/vcg/complex/algorithms/isotropic_remeshing.h#L1320-L1356)
keeps the selected patch interface fixed for Laplacian relaxation. However,
[`cleanFlag` defaults to true](https://github.com/cnr-isti-vclab/vcglib/blob/c94ef4e12e9ea3ae986d9af91005be8328d13719/vcg/complex/algorithms/isotropic_remeshing.h#L88-L97),
and the initial
[cleanup](https://github.com/cnr-isti-vclab/vcglib/blob/c94ef4e12e9ea3ae986d9af91005be8328d13719/vcg/complex/algorithms/isotropic_remeshing.h#L251-L295)
removes duplicate faces/unreferenced vertices and compacts the whole mesh.
This is not our original-face-prefix preservation contract. Refinement ideas
are reusable; the whole mutation path is not a drop-in Cap-only operation.

### Legacy Bridge editor

Under `unsupported/plugins_unsupported/edit_hole`,
[`FgtBridge`](https://github.com/cnr-isti-vclab/meshlab/blob/71e7b0b53cd98f520f84cb17b31bac6f1ef947cc/unsupported/plugins_unsupported/edit_hole/fgtBridge.h#L100-L134)
joins two boundary edges with two triangles. It can merge two loops into one
remaining loop, or split one loop into two. This is a seed connection followed
by filling, not an entire resampled strip generated in one operation.

[Automatic multi-bridging](https://github.com/cnr-isti-vclab/meshlab/blob/71e7b0b53cd98f520f84cb17b31bac6f1ef947cc/unsupported/plugins_unsupported/edit_hole/fgtBridge.h#L478-L580)
searches selected loop pairs and updates the hole list after each connection.
The
[two diagonal choices](https://github.com/cnr-isti-vclab/meshlab/blob/71e7b0b53cd98f520f84cb17b31bac6f1ef947cc/unsupported/plugins_unsupported/edit_hole/fgtBridge.h#L685-L754)
are scored by triangle quality after mesh-contact tests. The
[contact query](https://github.com/cnr-isti-vclab/meshlab/blob/71e7b0b53cd98f520f84cb17b31bac6f1ef947cc/unsupported/plugins_unsupported/edit_hole/fgtHole.h#L502-L521)
uses a spatial grid and face bounding boxes. This legacy code is useful as a
construction reference, but does not establish that two selected openings
belong to one missing surface. It also has no equivalent of our element-owner
exception for unrelated source geometry.

### Consequences for our next experiments

Our current planar path already has exact projected ear tests and constrained
Delaunay diagonal improvement (`RemeshPlanarCap.Triangulate`). Replacing it with
greedy 3D ears would not establish an improvement. The useful additions are:

1. A bounded 3D candidate for genuinely warped, simple rims that have no accepted
   planar-feature hypothesis. Preserve fixed rim positions and source winding;
   use shape quality and donor-collar normals for ranking, not for acceptance.
2. A seed-Bridge comparator after domain pairing has been accepted: insert a
   small connection, re-extract the remaining boundary, then fill it. Keep the
   complete-strip method as a separate candidate. Do not pair holes solely by
   distance or triangle quality.
3. Optional patch-only refinement, with a length policy based on the local rim
   and voxel scale, fixed original/feature vertices, and persistent synthetic
   face provenance. Audit after every geometry-changing stage.

Every candidate must retain our atomic acceptance per opening/domain, complete
position-welded topology checks, contact checks against the affected connected
element and other candidate faces, cancellation and work budgets. Contacts with
other elements remain non-blocking. Count actual remaining boundaries; a partial
ear fill must not be reported as a closed hole. A refused opening must not undo
independent accepted openings.

| Comparison case | Required result |
| --- | --- |
| Concave planar rim | Fixed boundary; no overlap; compare triangle quality with current Delaunay output. |
| Known warped smooth rim | Compare construction error and worst dihedral; prohibit collision and inversion. |
| Box missing two/three adjacent faces | Retain expected planes and shared feature edges; smooth filling is not equivalent. |
| Torus with a removed band | Compare complete-strip and seed-Bridge closure; preserve the intended tunnel. |
| Two unrelated open components | No automatic pairing; contacts with the other element cannot refuse a valid local Cap. |
| Valid and obstructed rims together | Keep the valid closure and preview the refused rim with its reason. |

Record preservation, synthetic-face masks, residual boundaries, vertex links,
new/preexisting contacts, triangle quality, patch excursion, work and refusal
reasons. Only then replay private captures and native 64/128/256 with Solve
off/on; local closure success alone is not native Remesh or bake validation.

## Near-planar source rims: relaxed tolerance (2026-10-10)

The private `Univer_SmallPorch_1` capture has 96 source vertices and 60 triangles.
Exact position welding leaves 35 vertices and one eight-edge boundary. Its
underside is planar, but the midpoint on the other missing face is offset by
`0.0009227395` source-local units (about 0.02% of that arc's span). The exact box
fixture did not exercise this authored bow: the old `1e-5` relative allowance
rejected the real staircase's second patch.

Cap revision 10 changes the relative plane-fit allowance to 0.1% of local
support span and the default minimum tolerance to `0.01` source-local units,
as requested. The effective bound is the larger of the two. Saved settings
with the former shipped `0.00001` default migrate to `0.01`; a missing loop
selection migrates to `all`. Other explicitly saved values, including zero,
remain intact.

This changes plane classification and the 2D triangulation projection only.
Source positions, indices, donors and FBX files are preserved. Continuous arcs,
unique decomposition, boundary conservation, vertex links, exact contacts
within the affected element, cancellation and search budgets remain checked.
Prepared snapshots include the Cap revision, so old refused preparations are
invalidated after the package update.

Regression coverage adds a bowed, subdivided box with two adjacent missing
faces, rotation, translation, scale and reversed winding. The private porch
replay checks both the new minimum and the relative allowance with a saved
`1e-5` minimum, then runs native Remesh at 64, Trim, Simplify and Unwrap with
Solve off/on. Both closures add six faces in two patches, leave no open edges,
and preserve every original source corner. Explicit `0.01` intersection tests
still refuse obstructed closures. `FairStall_Steps` also passes Cap and native
Remesh with the new default; `Garbage_Chute_Long` additionally passes Trim,
Simplify and Unwrap. These replays do not constitute a material-bake verification.

Local validation: 337 EditMode tests passed, no failures; 18 tests requiring
other private inputs or external prerequisites were ignored. Both C# compile
variants (FBX exporter defined/undefined) passed. The independent Python Cap
geometry benchmark passed all 115 tests.
