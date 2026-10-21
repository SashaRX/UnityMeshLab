# Furniture transfer and packing corpus

Run `FurnitureCorpusTests.ComparePrivateFurnitureCorpus` in an isolated Unity
project that references the local package. It imports byte copies, compares all
matched `Name_LOD0` → `Name_LOD{N}` subassets in each FBX, and deletes the imports.
Original fixture bytes and prepared source/target snapshots must remain identical.
Missing LOD pairs are explicit exclusions; meshes in different FBX files are not
matched. `.fbx` extension matching is case-insensitive. The corpus limit is 256 files.

```powershell
$env:MESHLAB_FURNITURE_CORPUS = 'D:/private-fixtures/Furniture'
$env:MESHLAB_CORPUS_OUTPUT = 'D:/private-results/furniture-fresh.jsonl'
# Run the EditMode test above in the isolated project, or pass its name to Unity's -testFilter.
python Tools~/FurnitureCorpus/report.py D:/private-results/furniture-fresh.jsonl --out D:/private-results/furniture-report
python -m unittest discover -s Tools~/FurnitureCorpus -v
```

Use a fresh output filename outside the test project's Assets/Packages. Existing
reports are never overwritten by the test. Each row is flushed; a terminal marker,
matching file/pair/error counts and all nine measurements per pair are required for
the summary's `complete` flag. Errors and incomplete intersection scans remain
visible. A completed audit is **not** a clean-UV certification.

The portable report and analysis checks also run in `meta-check.yml` without private meshes,
Unity licensing or SLIM dependencies. The private corpus audit remains local.
An additional optional solver test rejects a closed low-stretch patch before the
probe threshold; it runs with the SLIM environment and is explicitly skipped without it.

The fixed experiment uses 512 resolution, square texture metric, symmetry off,
UV0 edge welding and triangle pre-optimization. It calls imported-asset preparation
and ordinary grouped transfer, **not** UI auto-tune, the complete Forward/Reverse
pipeline, multi-part shared-atlas packing, or an FBX export round trip.

For the same prepared inputs it compares:

- Default packing and grouped transfer.
- Packing without source reparameterization, then grouped transfer.
- Requested internal oversampling 8 instead of 4, then grouped transfer. Existing
  pack budgets still apply; the requested factor is not guaranteed to be achieved.
- On the default source only: UV0-nearest, surface-nearest and normal-filtered
  surface-nearest comparison implementations.

Every transfer uses the same final unit-square clamp. `rawOutOfBounds` records the
number of vertices outside before that clamp. The source scan uses packed UV2.
`geometryDegenerateFaces` counts exact zero-area stored 3D triangles independently
of `uvDegenerateFaces`. Both can apply to the same face. The existing quality
report's `degenerateFaces` remains its combined counter. A nearly singular
Jacobian computation can miss an exactly zero stored UV area; comparison therefore
also includes both direct degeneracy counters instead of trusting only that metric.

`uvTriangleAreaSum` is a sum with multiplicity, **not occupied atlas area** when
triangles overlap. `atlasSide` is the normalized pack raster size. Timings are
single samples, not a performance ranking; default source preparation time is
unavailable (`-1`), while alternative pack times exclude measurement. Geometry,
texture-space correctness and cross-LOD correspondence have no independent truth
labels here. A map can look undistorted while sampling the wrong source region.

The summary compares each model/LOD separately across invalid/degenerate faces,
overlap count/area, bounds and worst/mean anisotropy. It reports better, worse,
same, incomplete, invalid or tradeoff. Float comparisons use relative 1e-6
tolerance; integer defects must agree exactly. These comparisons are diagnostic,
not the production candidate acceptance rule. Source rows repeat for each target;
deduplicate `(model, source)` before claiming a count of unique source meshes.

## Oversample analysis

On the full corpus, `oversample-8` improves 137 packed sources against `default`
(17 worse, 101 tradeoff), yet its grouped transfer splits 65 better / 61 worse /
129 tradeoff. Oversampling changes two things at once. The pack reports its
internal atlas (requested resolution × reached oversample), and grouped transfer
takes that size as the side of its texel tolerances (1.25 px out-of-bounds margin,
2.5 px bounds tolerance, the planar-recovery band). When the packs reach oversample 8
and 4, these are half as wide in UV space at 8 as at the default 4, although the
lightmap is 512 px in both; the reached factor is reported below.

To separate the pack from the transfer side, record with
`MESHLAB_CORPUS_OVERSAMPLE_ANALYSIS=1`. The `default` and `oversample-8` profiles
then add a `grouped-at-resolution` row: the same source transferred against the
requested resolution. Its `atlasSide` is that resolution. Grouped rows are also
traced: `sourceAtlasRecoveryAllowed` is the source-atlas recovery gate and
`cleanChartRecoveries` counts the target shells clean-chart recovery ran on (among
the first 10000, which the trace keeps). The gate also lets merged and overlap-group
candidate selection recover; that path is not counted. Traced times include the
trace. A capture with these rows has eleven measurements per pair, and `report.py`
then counts it complete only when every pair has both control rows; compare it only
with captures recorded the same way.

```powershell
$env:MESHLAB_CORPUS_OVERSAMPLE_ANALYSIS = '1'
# Run the corpus test as above, then:
python Tools~/FurnitureCorpus/report.py D:/private-results/oversample.jsonl --out D:/private-results/oversample-report
python Tools~/FurnitureCorpus/oversample.py D:/private-results/oversample.jsonl --out D:/private-results/oversample-report
```

`oversample.md` contains:
- verdicts for the packed source, the transfer at the internal side, the transfer at
  the requested resolution, and the side change alone at oversample 4 and at 8;
- per-metric better/worse/same counts and the most common tradeoff patterns;
- a table of how each pair's packed-source verdict carries through each transfer;
- a table of the recovery gate (closed/open at oversample 4 -> 8) and the clean-chart
  recovery count against each transfer verdict;
- the oversample each pack reached (`atlasSide / resolution` per source mesh), which
  shows where the pack budget lowered the requested factor.

It names no models and can be posted as a summary.

## Optional SLIM control

Reuse the separately installed libigl/NumPy/SciPy/Matplotlib environment described
in `Tools~/SeamPlacementBenchmark/README.md`. No dependency is added to Unity.

```powershell
python Tools~/FurnitureCorpus/slim_probe.py private-capture.json --out fresh-slim-directory
```

Input JSON contains `positions` (`{x,y,z}`), `uv` (`{x,y}`), and flat `triangles`.
The existing `DiskSlim` control solves indexed edge-connected patches with worst
intrinsic stretch above 4 or singular UVs. Geometrically degenerate patches and
non-disk topology are reported separately. Healthy charts are recorded but not
solved. Per-patch buffers and an input SHA-256 are retained for inspection.

The solver starts from a convex-boundary embedding and evaluates checkpoints with
full triangle-intersection tests. The probe then separately checks Float32 rounding.
Its `validFloat32` comes from the existing diagnostic predicate and includes its
positive-area threshold. It does not certify final packing or subsequent transfer.
Splitting point-only junctions into independent patches can introduce additional
UV discontinuities. Do not apply these buffers directly to a production atlas.

Measured corpus findings and algorithm references are in
`Documentation~/FURNITURE_TRANSFER_RESEARCH.md`. Private geometry and generated
scoreboards stay outside the tracked package.
