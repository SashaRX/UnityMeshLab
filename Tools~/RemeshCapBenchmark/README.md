# Remesh Cap intersection benchmark

Offline diagnostics for a proposed source Cap. These tools do not change Unity
meshes, settings, native binaries, or the production Remesh pipeline. Private
model captures and generated reports belong in ignored scratch directories.

[Local Cap research](../../Documentation~/REMESH_LOCAL_CAP_RESEARCH.md) compares
boundary detection, planar/3D filling, compound patches and stitching, and defines
the proposed acceptance contract and next offline experiment. It is a design
proposal, not a production Cap implementation.

[Architecture and milestone plan](../../Documentation~/REMESH_CAP_PLAN.md) maps
the current code and defines boundary domains, partial-plane hypotheses and
missing feature reconstruction. Its reproducible known-box structure probe is:

```powershell
python 'Tools~/RemeshCapBenchmark/box_structure_probe.py' --output '_results~/cap-structure/box.json'
```

The probe checks known reference closures and whole-boundary plane fits. It does
not automatically infer missing faces or handle singular production boundaries.

P1 now provides [source-fan-aware boundary extraction](../../Documentation~/REMESH_BOUNDARY_EXTRACTION.md),
including selected-opening coverage and explicit singular-junction diagnostics:

```powershell
python 'Tools~/RemeshCapBenchmark/boundaries.py' --source SOURCE.bin --output PRIVATE_BOUNDARY_REPORT.json
```

The report preserves original corner references. Extraction success does not
certify a solid; singular cycles require further domain/feature analysis before
triangulation. The existing compound experiment's frozen simple cycles remain
separate comparison inputs.

Failed production Remesh/Simplify attempts now save version 2 geometry captures
in `%TEMP%/meshlab-uvmerge/failures/`, independently of Verbose logging. Decode
one before selecting Cap or native experiments:

```powershell
python 'Tools~/RemeshCapBenchmark/failure_capture.py' --capture FAILURE.bin --output PRIVATE_OUTPUT
```

This exports exact source/raw/input mesh arrays, stage settings and independent
topology summaries. A missing raw mesh remains explicit; it is not fabricated
from the later input. Details and limitations:
`Documentation~/REMESH_FAILURE_CAPTURE.md`.

Python dependencies: NumPy 2.1.3 and Triangle 20250106 for the compound experiment
and its tests; Matplotlib only for the optional plot. Triangle remains an external
offline dependency; no library is vendored or linked into Unity.

```powershell
python -m pip install numpy==2.1.3 triangle==20250106
python -m unittest discover -s 'Tools~/RemeshCapBenchmark' -v
python 'Tools~/RemeshCapBenchmark/analyze.py' --source SOURCE.bin --capped CAPPED.bin --voxel VOXEL.npz --resolution 128 --output REPORT.json
python 'Tools~/RemeshCapBenchmark/render.py' --report REPORT.json --output REPORT.png
```

The little-endian mesh capture consists of uint32 vertex/index counts, float32
XYZ vertices, then uint32 triangle indices. The capped capture must retain the
original triangles, in order and with unchanged positions, as its face prefix.
Voxel NPZ files contain `positions` (Nx3) and `indices` (Mx3). Repeat `--voxel`
for several variants at the same explicitly supplied resolution. Voxel meshes
must already be indexed by exact position; UV/normal vertex splits are not
welded by the voxel sampling step.

`intersections.py` uses an AABB tree for candidate pairs, then integer plane
predicates and rational intersection/clipping on exact captured float values.
There is no model-relative epsilon or approximate coplanarity decision. A real
small gap stays a gap. Degenerate faces are diagnosed separately and excluded
from triangle-pair tests; the report records their count.

Classification distinguishes transverse `crossing`, positive-area
`coplanar_overlap`, and nonadjacent `point_contact` / `segment_contact`. A shared
vertex or edge is allowed only when the whole intersection lies on that shared
feature. Adjacent faces overlapping beyond their common edge are still reported.
Counts are triangle pairs, not independent geometric intersection regions.

Source-source findings are pre-existing. Source-cap and cap-cap findings are
added by the proposed Cap. `capGeometryAccepted` is false for any degenerate Cap
face or new improper intersection/contact. It is a geometric gate only; full Cap acceptance also
needs separate topology, component/volume, and shape checks. It does not repair
or silently remove any offending face.

`topology` also records closed edges, duplicates, winding, degenerate faces and
disconnected geometric vertex fans, independently of UV/normal vertex splits.
`capAccepted` requires both the geometric gate and closed manifold topology.
`solidCandidateAccepted` additionally requires zero reported intersections or
nonadjacent contacts anywhere, including the original source. Neither flag is
a full solid/bake-quality certificate: component orientation/volume, shape
deviation and the actual voxel output still need verification.

Each report preserves zero-based face IDs, cap component IDs, and 3D intersection
witnesses. Cap components join over shared edges, not over a lone contact vertex.
Voxel defects are sampled once per duplicate geometric face group and once per
non-manifold edge (centroid / midpoint). The report records nearest source and
cap faces and distances to intersection/contact geometry at 0.5, 1, and 2 cells.
Distances and their associations use float64; intersection classification is
exact. Spatial proximity is not a causal attribution, and a far centroid does
not prove that the entire offending face is far from an intersection.

Measured PileOfBricks findings and the next experiment are recorded in
`Documentation~/REMESH_CAP_INTERSECTIONS.md`.

## Controlled compound Cap experiment

```powershell
python 'Tools~/RemeshCapBenchmark/compound.py' --source SOURCE.bin --boundaries BOUNDARIES.json --shear 0.1 -1 --floor-margin 0.0001 --output CANDIDATE.bin
```

Boundary JSON is an array of directed cycles covering every original boundary
edge once. Its integer vertex slots refer to `np.unique(sourcePositions, axis=0)`
(lexicographic geometric weld), not raw imported vertex indices. The tool rejects
missing/reversed edges, crossed projected constraints, overlaps and T junctions.
Shared branch vertices remain explicit and are checked for disconnected fans.

Projection is `(X + shearX * Y, Z + shearZ * Y)`. Projection and the floor margin
are explicit capture-unit parameters, not automatic production defaults. The
largest projected contour determines winding; oppositely wound contours are
treated as interior boundaries. This assumes one layer of holes in equally
oriented domains; arbitrary nested volumes and sheets are outside its scope.

The experiment constrains boundary edges, adds only interior vertices, and lifts
them to `min(sourceY) - floorMargin`. It then performs up to `--max-rounds` local
refinement rounds at remaining source/Cap crossings. This changes the closure
surface, so shape and bake behavior must be measured before production use.
It never moves/splits an original source edge or face and checks the face prefix.
It writes a diagnostic candidate and JSON even when rejected; exit code **2**
means `solidCandidateAccepted=false`, including pre-existing source defects
even when `capAccepted=true`. Do not apply a rejected candidate to a Unity asset.

Optional `--bridge-branches` attempts to reconnect exactly two disconnected
geometric vertex fans through Cap triangles. For oriented faces `(v,a,b)` and
`(v,c,d)`, it tries `(v,a,d)`, `(v,c,b)`, `(a,b,d)`, `(c,d,b)`. All source faces
and positions remain unchanged. Each candidate must improve fan topology,
preserve closed oriented edges and pass exact 3D patch/unchanged and patch/patch
intersection tests. The search uses stable face order and a 256-pair budget;
it does not rank candidates for shape quality. Refusals remain in the JSON.
Whole-mesh geometry and topology are audited again after a successful change.
This operation is specific to a closed edge manifold with a two-fan branch,
not an arbitrary non-manifold repair.

Measured results, remaining branch/voxel failures and limitations:
`Documentation~/REMESH_COMPOUND_CAP.md`.
The branch follow-up and remaining source defects are recorded in
`Documentation~/REMESH_CAP_BRANCH.md`.
