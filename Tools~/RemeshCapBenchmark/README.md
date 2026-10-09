# Remesh Cap intersection benchmark

Offline diagnostics for a proposed source Cap. These tools do not change Unity
meshes, settings, native binaries, or the production Remesh pipeline. Private
model captures and generated reports belong in ignored scratch directories.

Python dependencies: NumPy; Matplotlib only for the optional plot.

```powershell
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
