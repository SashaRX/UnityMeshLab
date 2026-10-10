# Real-hole filling comparison, 2026-10-10

The experiment compares actual upstream libraries with production Cap revision
11 on nine private captured source meshes. It adds a repeatable offline/Unity
benchmark, not a new production dependency or a change to the default closure
mode. No original FBX, asset, scene mesh or donor face was edited.

## Algorithms and checks

13 strategies were run on the same inputs:

- Our local planar Caps at absolute tolerances `0.01` and `0.1`, Surface Caps,
  and the existing Automatic mode.
- PyMeshLab `2025.7.post1`: close holes with its self-intersection option, with
  refinement disabled or enabled at the documented 3% bounding-box edge length.
- MeshLib `3.1.4.297`: universal, minimum triangle angle and minimum area metrics;
  also `fillHoleNicely` with curvature smoothing, target edge length twice the
  median source boundary-edge length, and at most 1000 splits per hole.
- A centroid fan as a simple negative control.
- Two disk-only sequential strategies: our planar Caps at `0.01` followed by
  Surface Caps or MeshLib minimum-area filling on the remaining boundaries.
  Both preserve every previously accepted planar patch.

The upstream methods are described in [PyMeshLab's close-holes documentation](https://pymeshlab.readthedocs.io/en/latest/filter_list.html#meshing-close-holes),
[MeshLib's filling metrics](https://meshlib.io/documentation/Cpp/group__FillHoleGroup.html)
and [its fill-hole example](https://meshlib.io/documentation/ExampleMeshFillHole.html).
CGAL's [triangulate/refine/fair API](https://doc.cgal.org/latest/PMP_Mesh_repair/index.html)
was reviewed as another design reference; **CGAL was not executed in this experiment**.

Each library receives a fresh exact-position weld, with source geometry retained
for the preservation audit. Results are converted to the float32 contract used
by our captured support. Original oriented face multiplicity must be preserved;
changed, removed or split donor faces are rejected. Only face reordering and new
Cap faces/interior vertices are allowed. Reconstructed candidates retain the
original source triangle prefix.

Independent Python checks cover geometric duplicate/degenerate faces, edge
manifoldness, winding, disconnected vertex fans and exact new-face contacts.
Contacts are scoped to the affected connected element; other elements do not
block a Cap. Existing source/source contacts are not re-certified. The contact
budget is 200,000 exact tests per candidate; no candidate exhausted it. Closed
topology is not a certificate of intended shape or absence of pre-existing
source defects.

Audited candidates then pass through real Unity/native Voxelize → Trim →
Simplify → Unwrap, with all topology guards enabled and Solve off/on. Settings:
resolution 64, maximum error `0.02`, prune small parts, normal crease 133,
normal smoothing 3, area/corner weighting, 512 texture, padding 3, chart merge.
The trim distance is twice source bounding-box diagonal / resolution. Nonzero
closed component volumes, UV overlap, degeneracy, bounds, winding and conformal
stretch are checked. Shape comparisons use 15 barycentric samples per Cap
triangle in both directions; they are sampled differences, not Hausdorff bounds.

## Outcome

| Strategy | Closed without new improper contacts | Remesh/UV at 64, Solve off/on |
|---|---:|---:|
| Planar `0.01` | 8/9 | 12/16 |
| Planar `0.1` | 6/9 | 8/12 |
| Surface Caps | 8/9 | 12/16 |
| Existing Automatic | 8/9 | 12/16 |
| MeshLab basic | 7/9 | 10/14 |
| MeshLab refine at 3% | 5/9 | 6/10 |
| MeshLib universal | 8/9 | 12/16 |
| MeshLib minimum angle | 7/9 | 12/14 |
| MeshLib minimum area | 9/9 | 14/18 |
| MeshLib refine/smooth | 3/9 | 4/6 |
| Centroid fan | 7/9 | 10/14 |
| Planes → Surface | 9/9 | 14/18 |
| Planes → minimum area | 9/9 | 14/18 |

Library failures are outcomes of these specific configurations, not claims that
other settings or every use of the library fail. Refinement can intentionally
split donor boundary faces; that behavior violates this benchmark's immutable
donor contract and is reported rather than silently accepted.

### Curved column

`Univer_Column_C` contains ten holes, each with 13–24 boundary edges. Planar
`0.01` adds 44 faces and leaves 152 open edges; `0.1` adds 106 and leaves 82.
Surface Caps adds 180 faces and closes all ten with no new improper contacts.
MeshLib minimum area also closes all ten. Universal and minimum-angle filling
close topology but introduce 5 and 6 improper contact pairs respectively; the
centroid fan introduces 36. MeshLab basic leaves 14 open edges.

Both sequential methods close the column with 182 faces, retaining the first
planar stage's patches and added vertices. This difference from the pure surface
method is intentional and independently audited.

Surface Caps at 64 with Solve on produces 88 triangles, mean/max conformal
stretch `1.01886 / 1.22499`. Pure minimum area produces 94 triangles and
`1.02181 / 1.46474`. With Solve off, minimum area has lower worst stretch
(`1.46688` vs `1.66253`). There is no universal quality winner across settings.

### Stairs and porch: shape matters

`FairStall_Steps` and `Univer_SmallPorch_1` have a connected rim spanning two
missing planes. Many fillers produce closed manifolds with no new contacts and
valid downstream UV while choosing a different closure surface.

Relative to our local-plane patch, the maximum sampled difference on the stairs
is `0.56279` for pure Surface Caps and `0.66671` for minimum area, about 18.2%
and 21.5% of the source span. They cut across the intended two-plane corner.
MeshLab basic and MeshLib universal remain within `0.0006822` on this stair
capture, but universal/minimum-area differ by `0.69679` on the porch. Therefore
a simple global preference for smoothness, area or triangle quality is unsafe.

The sequential strategies match the planar patch on both controls to numerical
noise because no fallback runs on an already closed hole. This is the main
reason to preserve a supported planar hypothesis before trying a surface disk.

### Larger tolerance is not monotonic

Both `Garbage_Chute` captures close with planar `0.01`, adding 66 faces across
five contours. At `0.1`, the short variant leaves 10 open edges; the long variant
leaves 24. The larger tolerance changes the selected plane hypotheses, not just
their numerical acceptance. These are regression controls against further
blanket tolerance increases.

`Bush_19` closes with every strategy in this experiment. Its earlier contact
refusal is no longer reproduced with current connected-element contact scoping.
`SandbagRoundedcorner` closes with planar Caps; pure Surface Caps exhausts the
4096-candidate search budget and publishes no partial patch. MeshLab basic leaves
five open edges and its refined configuration adds a new improper contact.

### Failures after closure

At resolution 64, every admitted strategy fails on `Park_Bench_A` and
`BulletinBoard` inside native solid voxel remesh, before Trim/Simplify. This is
40 failed native outcomes among the original 152 attempts, despite different
independently audited Cap triangulations. Changing the Cap objective alone does
not resolve these captures. This localizes the failing stage; it does not prove
that pre-existing source intersections or thin-volume ambiguity are irrelevant.

Additional replay of two methods per column/bench/board and three per sandbag at
32, 128 and 256 produced 54 outcomes: 34 passes and 20 voxel failures.

- The column and sandbag pass at all three resolutions and both Solve settings.
- The bench passes at 32 but fails at 64, 128 and 256. At 128 the output has
  107 duplicate faces / 179 non-manifold edges; at 256 it still has 2 / 4.
- The board fails at all four tested resolutions with hundreds of duplicates
  and non-manifold edges. Replacing its Cap triangulation does not remove them.

Passing at 32 is a diagnostic result, not a demonstrated shape-preserving fix.
The voxel stage needs a separate input/output investigation. No native guard
was weakened and no malformed voxel result reached simplification.

Including the two sequential strategies gives 117 filling outcomes and 188
resolution-64 native outcomes (140 pass, 48 voxel failures). With the 54
resolution probes, the total is 242 native outcomes: 174 pass and 68 fail at
voxel validation. These failure counts are deliberately retained in the report.

## Verification

All 122 independent Python benchmark tests pass, including seven new tests for
donor preservation, winding, component-scoped contacts and incomplete audits.
Both C# compile variants pass. The two opt-in Unity benchmark methods completed
all five export/replay invocations; donor preservation assertions passed. This
is distinct from accepting every algorithm output. Geometry captures and native
results were evaluated locally in Unity 6000.2.6f2; licence-gated GitHub EditMode
execution is not used as evidence for these outcomes.

## Decision

The strongest tested disk strategy is: preserve supported local planes first,
then try a bounded 3D fill only on the remaining contours, with the same atomic
topology/contact guards. Minimum-area dynamic programming is useful as an
alternative candidate for genuinely curved rims, not as the global default.
Our current bounded Surface search remains preferable to importing another
production native dependency before the shape and failure cases are resolved.

This experiment covers disk holes. A torus band or paired opening still needs
Bridge classification; neither sequential strategy has been certified for that
topology. Original patches and per-hole refusal metadata must survive any future
production fallback. The benchmark does not yet implement that UI/provenance
integration.

Next production work should separately address per-hole method selection and
the bench/board voxel failure. Mixing a Cap replacement with a weakened native
validity check would hide the distinction exposed by these tests.

## Reproduction and private evidence

See `Tools~/RemeshCapBenchmark/README.md` for the manifest-driven procedure.
Tracked code: `compare_fill.py`, `report_fill.py`, `test_compare_fill.py` and
`Tests/Editor/RemeshCapComparisonTests.cs`. Optional dependencies are pinned in
`compare-requirements.txt`; they are not Unity package dependencies.

Private artifacts are under `_results~/cap-compare-20261010/`:

- `manifest.json`: exact source paths; source binary SHA-256 values are in
  `output/comparison.json`.
- `output/production.json` and `output/comparison.json`: all per-method outcomes,
  timings, preservation, exact contact samples and topology counts.
- `output/native.json`: baseline resolution-64 native replay; `hybrid-native/`
  and `resolution/` retain their separate native reports.
- `output/shape.json`, `output/report.md`, and PNG comparisons for the stairs,
  column and sandbag.
- Unity logs and NUnit XML for export, native and resolution replays.

Private source geometry is excluded from Git. A green benchmark NUnit result
means the replay completed and donor preservation assertions passed, not that
all experimental algorithms were accepted.
