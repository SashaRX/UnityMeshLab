# Source signals versus authored UV/smoothing boundaries

Developer experiment for one source mesh and two edited low-poly references.
These tools measure a relationship; they do not install learned weights in Unwrap,
modify the source FBX, or change EditorPrefs. Mesh captures stay in the scratch
project. Python needs NumPy, SciPy, scikit-learn and Matplotlib.

1. Use an isolated Unity project referencing the package. Import the high-poly FBX
   there as a readable mesh. Run `UvReferenceBenchmark` for both low-poly references
   (see `../UvReferenceBenchmark.cs`); keep separate capture directories
   and keep the first reference FBX for exact smoothing masks.
2. Copy `SourceSignalsCapture.cs` and `SourceSignalsProject.cs` into that scratch
   project's `Assets/Editor`. Set `MESHLAB_SIGNAL_SOURCE_ASSET` to the high-poly
   asset path, e.g. `Assets/Bust.fbx`. Run
   `-batchmode -executeMethod SourceSignalsCapture.Run`. It captures the full source
   through `RemeshSource.Capture(... geometryOnly:true)`, writes `source-signals/source.bin`
   and the normalization frame `source-space.json` (Vector4: center x/y/z, diagonal w).
3. Prepare fields and queries using the saved input and output of the actual simplify:

```text
python Tools~/SourceSignalsBenchmark/prepare_features.py SCRATCH_PROJECT V1_CAPTURE_DIR
python Tools~/SourceSignalsBenchmark/prepare_edges.py SCRATCH_PROJECT V1_CAPTURE_DIR V2_CAPTURE_DIR V1_FBX
python Tools~/SourceSignalsBenchmark/prepare_simplify.py SCRATCH_PROJECT V1_CAPTURE_DIR SIMPLIFY_INPUT_BIN CURRENT_UNWRAP_BIN
```

`SIMPLIFY_INPUT_BIN` is two int32 counts, float32 XYZ positions and int32 indices
(the `XatlasDefaultsBenchmark` input format). `CURRENT_UNWRAP_BIN` is the full
position/UV/index/chart capture format used by `UvMergeRelaxBenchmark`.

The reference/simplify must depict the same object in the source orientation. An
axis permutation/sign search reports its nearest-vertex fit for the reference;
inspect that fit before trusting the projection. The actual simplify uses the
source root's capture frame. It must already be in that frame, not arbitrarily
centered/transformed world coordinates. This is an experiment harness, not general
object registration. Smoothing masks use nearest corresponding triangle centroids
after axis alignment; inspect the printed residual. The two references must have
identical source positions/connectivity for shared edge labels.

4. Run `-batchmode -executeMethod SourceSignalsProject.Run`. It builds the production
   fitted cage with distance 0.02 and smoothing 2, then uses the bake's ray plus bounded
   nearest fallback. Source winding controls the facing filter. Each manifold edge
   is sampled at t=0.2/0.5/0.8, on both sides 5% toward the face centroid. Barycentric
   interpolation transfers source fields at the resulting source hits. The actual
   `SourceAoBaker` computes geometric occlusion at radii 0.01/0.05/0.2 with 128 sphere
   directions, cosine weighting, linear falloff, bias 0.0001, no ground plane,
   intensity 1 and source normal-map use disabled.
5. Analyze without exporting user geometry:

```text
python Tools~/SourceSignalsBenchmark/analyze.py SCRATCH_PROJECT/source-signals
python Tools~/SourceSignalsBenchmark/validate.py SCRATCH_PROJECT/source-signals
python Tools~/SourceSignalsBenchmark/plot.py SCRATCH_PROJECT/source-signals REPORT_DIRECTORY
```

`analyze.py` checks that each edge has six finite projected samples. It compares
fixed Random Forest families with five GroupKFold folds over 5×5×5 spatial cells,
including OOF predictions and length-weighted scores. `projected_source` includes
source/target normal disagreement and projection distance; it is not a feature set
independent of simplify. `validate.py` runs 500 paired spatial-block bootstrap
resamples and, separately, the UV-seam task with all authored hard edges removed.
The latter is essential: a method predicting smoothing boundaries can appear to
predict UV seams simply because many UV seams coincide with those boundaries.

Fields are cotangent mean-curvature estimates with barycentric vertex area,
area-weighted signed local normal height and normal variation at radii 0.01/0.03/0.07.
Units are normalized to the source bounds diagonal. Positive signed H is convex;
positive local normal height is cavity-like. These are mesh estimators, not texture
curvature/cavity maps. AO is recorded as `1 - visibility`. Source fields are measured
on position-welded high-poly geometry; UV/normal duplicates do not add geometric mass.

Summary CSVs and figures can be published. `source.bin`, references, edge-feature
tables and projection captures contain user geometry-derived data and stay local.
Repeat the projection to compare its float buffers bit-for-bit; avoid running two
Unity processes against the same scratch project. Scores are conditional on one
asset, two artist layouts and these samples/hyperparameters; they do not establish
new production defaults or artistic UV equivalence.
