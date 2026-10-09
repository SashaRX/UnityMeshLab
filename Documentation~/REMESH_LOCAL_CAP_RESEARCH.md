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
