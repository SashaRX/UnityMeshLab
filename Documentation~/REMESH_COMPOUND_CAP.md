# PileOfBricks: compound Cap experiment

This records the pre-connection baseline. The subsequent
[Cap branch reconnection](REMESH_CAP_BRANCH.md) removes the remaining input fan
without new intersections; original source crossings and voxel defects remain.

Measured 2026-10-09 on the same private source capture and native af94e5860826
as [the intersection audit](REMESH_CAP_INTERSECTIONS.md). The implementation
is an offline experiment in `Tools~/RemeshCapBenchmark/compound.py`. Production
Cap/Remesh, native libraries, project settings and source assets are unchanged.

## Projection is part of the problem

The main 195-vertex contour has two proper self-crossings in XZ projection:
edges (1422,1319) and (1303,1327) cross (1298,1603). Slots refer to the exact
lexicographic geometric weld. These are projected crossings, not proof that
the original 3D edges intersect. An XZ constrained triangulation inserts two
constraint-intersection vertices even with zero permitted Steiner points.
The benchmark rejects this because it would split the original boundary.

Of 169 explicitly tested projection shears, three avoid these proper crossings:
`(0.1,-1)`, `(-3,0.03)`, `(-3,0.1)`. They are experiment choices, not a universal
projection policy. The published tool subsequently checks the selected projection
with exact predicates for crossings, collinear overlaps and nonadjacent contacts.

Six oppositely wound contours become interior boundaries (#0, #3, #5, #6, #7,
#8). Contour #0 shares one vertex with the main rim. The three exterior domains
are #1, #2 and #4. Original points/faces remain unchanged. No existing source
face is deleted and no source edge is split or moved.

## Compound surface and refinement

Boundary-only constrained Delaunay at shear `(0.1,-1)` creates 259 Cap triangles
in three patches. This removes all Cap/Cap intersections and nonadjacent contacts,
but introduces three duplicate source faces, four non-manifold edges and eleven
source/Cap crossing/overlap pairs. Thus constrained 2D triangulation alone is
insufficient. The other two tested shears produce 24 and 28 such pairs.

Greedy internal diagonal flips improve some defects but stall: the first shear
still has two duplicate faces, two non-manifold edges, six crossing/overlap pairs
and four segment contacts. The other shears also fail. These were diagnostic
alternatives; the published experiment does not use that flip heuristic.

Interior refinement uses the initial face centroids and internal-edge midpoints,
without touching source constraints. The interior is lifted to capture Y
`-0.0032885705`, 0.0001 below the original minimum. This explicit floor changes
the closure shape. It is not a bake-preserving or automatically safe default.
The first 520 interior points remove duplicate/non-manifold edges but leave
four source/Cap crossings, all on one triangle below a source protrusion. One
additional local centroid refinement removes those crossings.

The final candidate adds **521 interior vertices and 1,301 Cap triangles**.
The exact audit finds **zero new crossing/overlap/contact pairs**. The original
four source/source crossings remain. Every original face retains its original
positions and ordering; the published CLI reproduces the Unity-probed candidate
with byte-equivalent position/index arrays.

## A clean geometric Cap is still not an accepted solid

Actual Unity 6000.2.6f2 managed `RemeshTopology.Inspect` finds:

- Boundary, duplicate faces, degenerate faces, non-manifold edges and inconsistent
  edges: all zero.
- **One disconnected geometric vertex fan**, at original slot 1228, position
  `(0.019783529, 0.0000097866, -0.73250568)`. This is the inner contour #0/main-rim
  branch. Original source had two disconnected fans; the other branch resolves.

The benchmark therefore reports `capGeometryAccepted=true` but `capAccepted=false`.
A shared endpoint may be an allowed geometric contact while its face link is
still disconnected. Merely duplicating that vertex index does not repair this
geometric topology: the managed check welds coincident positions independently
of UV/normal splits. No epsilon displacement or guard relaxation is applied.

## Actual native and managed results

Duplicate counts below are excess faces, not incidences. All rows use resolution
128. Raw and Trim counts match: Trim keeps all final-candidate voxel faces.

| Source / stage | Solve | Faces | Duplicate faces | Non-manifold edges | Disconnected fans |
| --- | --- | ---: | ---: | ---: | ---: |
| Original open source / raw | off | 65,328 | 1,985 | 5,680 | 5,188 |
| Independent constrained Cap / raw | off | 46,222 | 210 | 383 | 278 |
| Compound refined Cap / raw and Trim | off | 46,276 | 20 | 47 | 76 |
| Compound refined Cap / raw and Trim | on | 46,276 | 21 | 48 | 77 |
| Compound refined Cap / conservative fin removal | off | 46,236 | 0 | 12 | 31 |
| Compound refined Cap / conservative fin removal | on | 46,234 | 0 | 12 | 31 |

The final source and all listed final voxel outputs have zero boundary,
degenerate and inconsistent edges. Fin removal is measured on a private copy;
it still fails the manifold contract and is not a replacement for that guard.
The remaining output cannot be attributed solely to native generation: the input
still has a branch defect and pre-existing crossings.

## Verification, reproduction and next step

- **22 Python tests** cover the exact classifier, boundary coverage/orientation,
  self-crossed projection, collinear overlap, T junctions, a compound tube with
  an interior hole under either winding, original face preservation, attribute
  splits and a closed bow-tie vertex rejected despite valid edge incidence.
- Exact candidate audit: 49,371 broad-phase pairs. Geometric acceptance passes;
  topology acceptance fails. Both predicates remain independent in the report.
- Actual managed source/voxel/Trim/fin checks run in the existing isolated Unity
  diagnostic project, not the live E: scene. Private ignored evidence lives in
  `_results~/pile-remesh-20261009/compound-refined-final-0/` and
  `compound-published/`, including captured geometry, JSON and managed logs.
- Dependency and invocation details are in the [benchmark README](../Tools~/RemeshCapBenchmark/README.md).
  Triangle is an external offline dependency, not an added Unity/native ABI.

Next isolate the remaining contour #0 branch, preserving its actual 3D fan
ordering. Establish whether a local surface connection can close it without
intersecting source faces or changing the authored source. Reject the candidate
if those conditions cannot be met. Only then compare native residuals on a
certified source; reduce an offending voxel neighbourhood to a smaller fixture
before changing native topology handling. The four original crossings also
need an explicit source-repair decision rather than silently being ignored.

Triangulator reference: [Triangle PSLG/holes API](https://rufat.be/triangle/API.html).
The library may split constraint segments, so the experiment verifies exact
preservation of its input projected vertices instead of assuming flags suffice.
