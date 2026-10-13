# PileOfBricks Cap: topology versus geometric intersections

Measured 2026-10-09 on the private PileOfBricks capture from
`E:/fps-project-M2-4328`, Unity 6000.2.6f2, native package af94e5860826.
This is an offline diagnostic experiment. No production Cap or native Remesh
change is published by this experiment. Source/scene assets stay untouched.

## Baseline

The original source has 4,709 triangles and 255 boundary edges. Its voxel output
at resolution 128, Solve off, reproduces the reported failure exactly: 1,985
duplicate faces and 5,680 non-manifold edges. Solve does not resolve it.

An initial Cap of nine independent contours adds 237 triangles but creates two
duplicate source faces and three non-manifold edges. Forbidding occupied source
diagonals and existing face keys removes those defects. The constrained Cap has
zero boundary, duplicate, degenerate, non-manifold/winding edges, and disconnected
vertex fans, as verified by the actual managed RemeshTopology in isolated Unity.
Nevertheless, its voxel output still has 210 duplicates / 383 non-manifold edges
with Solve off, or 215 / 388 with Solve on. Trim keeps all 46,222 raw voxel faces.

## Geometric audit

`Tools~/RemeshCapBenchmark` checks candidate pairs through an AABB tree and exact
integer/rational predicates on the captured binary coordinates. It distinguishes
permitted adjacency, crossings, positive-area coplanar overlap, and nonadjacent
contacts. Adjacent triangles are not blindly excluded: they may overlap beyond
their shared edge. Existing source intersections are counted independently of
new Cap intersections. The audit tested 45,040 candidate pairs in about 3.4 s.

| Face origins | Crossing pairs | Coplanar overlap pairs | Nonadjacent point contacts | Nonadjacent segment contacts |
| --- | ---: | ---: | ---: | ---: |
| Original / original | 4 | 0 | 0 | 0 |
| Original / Cap | 32 | 0 | 10 | 7 |
| Cap / Cap | 11 | 3 | 0 | 3 |

Thus the proposed Cap adds 46 crossing/overlap pairs and 20 nonadjacent contact
pairs. These are triangle pairs, not counts of independent collision regions.
The geometric gate rejects the Cap despite its valid topology.

Every new intersection/contact involves the largest Cap component (#1), with
193 triangles and a 195-vertex boundary. In XZ projection, contours #3, #5, #6,
#7 and #8 lie fully inside that outer contour. Contour #0 touches it at one
boundary vertex and has three vertices inside. Contour #2 is outside but shares
one boundary point; #4 is outside. The contained contours have opposite projected
winding from the large outer contour. This is evidence for investigating a
compound patch with interior boundaries, rather than nine independent full
patches. The contours are not perfectly planar, so XZ containment alone is not
a sufficient 3D repair rule.

The 32 original/Cap crossings all involve component #1. Cap/Cap findings are:
#1/#3: 10 crossings; #0/#1: one crossing; #1/#5: three overlaps and three segment
contacts. The original has a separate crossing segment about 0.10 units long
plus three shorter crossings; none was introduced by Cap.

## Association with voxel defects

Cell size is about 0.01587 capture units (the native grid scale, not 1/128 in UV).
Duplicate face groups are sampled once at their centroid, not once per duplicate
incidence. Non-manifold edges are sampled at their midpoint.

| Voxel variant | Duplicate groups | Groups within 2 cells of new crossing/overlap | Non-manifold edges | Midpoints within 2 cells of new crossing/overlap |
| --- | ---: | ---: | ---: | ---: |
| 128 / Solve off | 210 | 16 | 383 | 35 |
| 128 / Solve on | 215 | 17 | 388 | 36 |

With Solve off, 104 duplicate group centroids / 184 edge midpoints have Cap as
their nearest captured face; 106 / 199 have the original source as nearest.
With Solve on those counts are 141 / 258 and 74 / 130 respectively. Spatial
proximity and the nearest face are evidence for investigation, not causality.
Most defect samples are outside the two-cell neighbourhood of measured crossings.
None of these samples is within two cells of the four original source crossings.
Correcting Cap intersections alone has not yet been shown to fix voxel topology.

## Next controlled experiment

The following experiment was subsequently implemented and measured in
[Compound Cap results](REMESH_COMPOUND_CAP.md). It eliminates new geometric
intersections but leaves a disconnected vertex fan and invalid voxel topology;
it is not accepted as a production repair.

Construct a single domain for the main outer contour with its interior
boundaries, retaining the existing source islands. Handle the touching boundary
explicitly instead of treating it as an ordinary disjoint hole. Reject occupied
diagonals and any new cap/source or cap/cap intersections. Because the main rim
spans roughly 0.054 units in height, verify the patch in 3D after triangulation;
use refinement only inside the patch if its geometry needs it. Preserve the
original positions/faces and bake channels as the reference.

Rerun the geometric gate and both voxel variants on precisely the same source.
Keep the strict solid topology guard. If a geometrically valid Cap still produces
folded/contact voxel output, isolate that failure into a smaller voxel fixture
before changing native surface generation. No arbitrary bad-face deletion or
global epsilon weld is justified by this evidence.

## Verification and evidence

- 16 Python tests: exact crossing symmetry/winding, scale/translation, tiny
  nonzero gaps, permitted adjacency versus actual overlap, nested independent
  caps, closed cube Cap, AABB candidates versus brute bounds, nearest sample
  distances, degenerate cap rejection and duplicate group counting. These run
  as a dedicated Unity Package Checks job. They are offline benchmark tests,
  not Unity EditMode tests or proof of production Cap correctness.
- Managed topology/Trim was verified earlier on the same constrained Cap with
  the actual Unity package in an isolated project.
- Private ignored evidence: `_results~/pile-remesh-20261009/intersections.json`,
  `managed-constrained.txt`, `source.bin`, `capped-constrained.bin`, native voxel
  NPZ files and `intersections.png`. No model capture is committed.

Reference: CGAL separates combinatorial manifold requirements from geometric
self-intersection checks in its Polygon Mesh Processing documentation:
https://doc.cgal.org/latest/Polygon_mesh_processing/index.html
The benchmark uses its own exact classifier, not CGAL or a new native dependency.
