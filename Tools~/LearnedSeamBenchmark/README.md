# Local learned-seam data preparation

This research tool prepares numerical inputs and labels and includes an optional
local edge-label trainer. It does not change Unity or generate a valid UV unwrap.
Preparation needs only NumPy; training additionally needs PyTorch. The canonical
capture reader is shared with SeamPlacementBenchmark.

Create a JSON **list** of entries; relative capture paths resolve against the manifest:

```json
[
  {"asset_id":"bust","family_id":"bust-original-A","capture":"v1/reference.bin"},
  {"asset_id":"bust","family_id":"bust-original-A","capture":"v2/reference.bin"}
]
```

Optional `split` is `train`, `val`, or `test`. All UV/LOD/augmentation variants of
one asset need the same asset ID; related assets need the same family ID. Exact
position/connectivity duplicates are grouped even under different names. Splits
propagate through these groups; conflicting explicit splits fail before output.
Two UV versions of one model count as **one independent model**. A changed
triangulation is not automatically identified as the same asset: supply IDs.
`family_id` identifies a shared asset/template lineage, not a broad category such
as `organic`: using one category ID would put every organic object in one split.
Use a new or empty output directory; existing inputs/results are never overwritten.

```powershell
$env:PYTHONDONTWRITEBYTECODE='1'
& '_results~/seam-methods/venv/Scripts/python.exe' -m unittest discover -s 'Tools~/LearnedSeamBenchmark' -v
& '_results~/seam-methods/venv/Scripts/python.exe' 'Tools~/LearnedSeamBenchmark/prepare.py' --manifest LOCAL_MANIFEST.json --out '_results~/learned-seams/prepared'
```

Input: little-endian int32 `vertexCount,indexCount,chartCount`, float32 XYZ,
float32 UV, int32 triangle indices and per-vertex chart IDs. Position duplicates
are welded **exactly**, without epsilon. UV cuts require endpoint-coordinate
discontinuity above `1e-7`; render-vertex/normal splits and chart IDs are ignored.
Coincident disconnected geometry is unsupported and can fail fan checks. A cut
with exactly coincident UV coordinates cannot be recovered from this format;
future raw-FBX ingestion must retain explicit UV topology. Existing artist UV
overlaps are not certified or repaired by preparation.

Each NPZ contains geometry, edges, adjacent faces, four edge-neighbor slots
(`-1` for missing), geometry-only features, internal seam labels, learned mask,
and separate forced open-boundary mask. SG is not inferred from seams/normals.
Duplicate/degenerate faces, nonmanifold edges/vertex fans and inconsistent winding
are rejected. `report.json` records capture/geometry/output hashes, grouping,
counts and split assignments. Store manifests, captures, NPZs and reports under
ignored `_results~`; they contain private geometry. No weights are downloaded.

## Untrained capacity probe

Optional `model_probe.py` uses locally installed PyTorch. Four width-64 residual
message blocks aggregate self plus masked mean/max of four neighboring edges;
the binary-logit head also receives global mean/max context. Tests check edge
reordering equivariance, neighbor-slot invariance and finite gradients with missing
neighbors. Without PyTorch these two tests skip; preparation still works.

```powershell
& '_results~/seam-methods/venv/Scripts/python.exe' 'Tools~/LearnedSeamBenchmark/model_probe.py' --input '_results~/learned-seams/prepared-bust-variants-reviewed/capture-0000.npz' --out '_results~/learned-seams/untrained-gpu-probe.json'
```

The batch-1 FP32 probe performs warmup and one measured forward/backward pass with
random weights. There is no optimizer or parameter update, download, UV prediction,
saved logits, checkpoint, training or quality evaluation. JSON records versions,
device, parameter count, SHA256 and PyTorch peak allocated/reserved GPU tensors.
CUDA context and other applications are outside these memory counters. Use a fresh
output JSON path. CPU fallback reports GPU memory as unavailable, not zero.

## Ephemeral step timing

`step_timing.py` separately measures real masked BCE + AdamW steps, including
parameter updates that exist only for the lifetime of this process. It uses one
prepared graph and its real UV labels. This is a hardware microbenchmark, not a
train-quality run. Boundary edges are excluded from the loss and invalid masks
fail. No checkpoints, logits or UVs are saved; existing JSON outputs are preserved.

```powershell
& '_results~/seam-methods/venv/Scripts/python.exe' 'Tools~/LearnedSeamBenchmark/step_timing.py' --input '_results~/learned-seams/prepared-bust-variants-reviewed/capture-0000.npz' --out '_results~/learned-seams/adamw-step-timing.json' --warmup 10 --steps 50
```

FP32, batch 1; synchronized CUDA events and wall time are reported separately.
Memory includes AdamW states and gradients, excluding CUDA context/other apps.
Preprocessing, topology decoding, solvers, validation, rendering and checkpoint IO
are excluded. `N_train * epochs * median_step_time` estimates compute only for
similarly sized graphs/features with this model and batch size; a larger dataset,
richer source features or a different model requires new timing.

## Explicit raw-file archive

`archive.py` is a separate, SDK-neutral collector. It copies only the files or
directories explicitly listed in a JSON manifest, byte-for-byte, without opening
FBX/MAX models or finding texture/XRef/modifier-stack dependencies. Any existing
high-poly source plus handmade final low-poly pair is sufficient; intermediate
remesh/simplify stages are optional if they already exist. Include native scenes
and reference directories when available to preserve data for future ingestion.

This **collection manifest is different from `prepare.py`'s capture manifest**:

```json
[
  {
    "asset_id": "bust",
    "family_id": "bust-original-A",
    "files": [
      {"role": "source", "path": "source/high.fbx"},
      {"role": "artist", "path": "artist/low.fbx"},
      {"role": "native", "path": "scene/bust.max"},
      {"role": "textures", "path": "textures"}
    ]
  }
]
```

Only list optional files/directories that actually exist. Input paths resolve
against the manifest; `--out` resolves against the current working directory.
Asset/family/role IDs use 1-128 ASCII letters, digits, dots, underscores or hyphens
(starting with a letter or digit); Windows reserved names and trailing dots are
rejected. Declare each asset once; multiple assets can share one family ID.

```powershell
python 'Tools~/LearnedSeamBenchmark/archive.py' --manifest COLLECTION.json --out '_results~/learned-seams/raw-archive-001'
```

The output must not exist, even as an empty directory. Files are placed at
`assets/<asset_id>/<role>/<source_filename>`. Directory roots retain their name
and complete relative structure, including empty directories and hidden files;
for example `textures/nested/normal.exr` becomes
`assets/bust/textures/textures/nested/normal.exr`. This collector needs only the
Python standard library. It never deletes, moves or rewrites inputs/results.

All explicitly listed inputs are preflighted before output creation. Missing
inputs, symlinks/junctions/reparse points (including ancestors), output inside an
input, unsafe output names and Windows case-insensitive path collisions fail.
Every copied file is verified by input/output SHA256. Interrupted copying or an
input changing after preflight can leave a partial **new** archive without a
success report; the helper does not remove it. Use a different fresh output.

`collection-manifest.json` retains the original manifest bytes.
`archive-report.json` records manifest/input/output hashes, source-to-archived
path mapping, roles, asset/family grouping and byte counts. It explicitly reports
`dependency_discovery_performed: false`, `completeness_verified: false`, and
`fields_extracted: false`. A copied FBX/MAX file may preserve UV, SG, normals and
material information, but the helper has not extracted or certified those fields,
the model pairing, or all external dependencies. It performs no automatic disk
scan and archives no real models during its tests. Curvature/AO/source projection
and other derived numerical fields remain separate, recomputable processing.

## First local training cycle

The geometry-method comparison is closed; its captures, quality scans and renders
remain the baseline in [the final report](../../Documentation~/UV_GEOMETRY_EXPERIMENT_FINAL.md).
`train.py` is a small FP32 edge-label baseline, not a text LLM or an automatic
UV system. It uses only the two verified geometry features already prepared.
SG, source projection features, topology repair and seam-chain/patch decoding
are separate next steps; neither predicted labels nor a low BCE certify UVs.

### Dataset required

Declare `split` for every independent family in the preparation manifest before
training. Train, val and test must all contain independent data. All UV/LOD and
synthetic variants of an asset stay together; aliases of the exact same geometry
are grouped even when their names differ. Related templates share `family_id`.
The trainer rebuilds grouping from asset/family/geometry and verifies NPZ hashes,
geometry, winding, feature values, graph adjacency, masks and reported counts.
It refuses unassigned splits, duplicate NPZ records, paths outside the prepared
directory and cross-split leakage before creating a new run directory.

The current two bust UV variants are one unassigned model, with 88 conflicting
edge labels. **They cannot be used as an independent train/val/test experiment.**
Select a preferred authored variant per asset for the initial style dataset;
multiple references without a style condition may have conflicting targets.
Additional variants do not become additional independent models. Satisfying the
technical minimum of three split groups does not establish statistical quality;
reserve independent test families before tuning methods or data augmentation.

### Windows environment for the RTX 3070 Ti laptop

Use a dedicated environment in a checkout of this branch. The locally tested
baseline is Python 3.12 / NumPy 2.1.3 / PyTorch 2.5.1+cu118. These commands reuse
that environment; they are not a claim that this is the latest PyTorch release.
CUDA wheel variants are documented by [PyTorch](https://pytorch.org/get-started/previous-versions/#v251).
The trainer itself has no FlashAttention, FBX SDK or third-party model dependency.

```powershell
py -3.12 -m venv '_results~/training-venv'
& '_results~/training-venv/Scripts/python.exe' -m pip install numpy==2.1.3
& '_results~/training-venv/Scripts/python.exe' -m pip install torch==2.5.1 --index-url https://download.pytorch.org/whl/cu118
$env:PYTHONDONTWRITEBYTECODE='1'
& '_results~/training-venv/Scripts/python.exe' -c 'import torch; print(torch.__version__); print(torch.cuda.is_available()); print(torch.cuda.get_device_name(0))'
& '_results~/training-venv/Scripts/python.exe' -m unittest discover -s 'Tools~/LearnedSeamBenchmark' -v
```

The Python trainer needs neither Unity nor WSL for this baseline. It installs
nothing automatically and performs no cloud upload. Keep captures, prepared
data and weights under ignored `_results~`; the laptop must receive these private
files separately because a Git pull does not include them. A raw FBX/MAX archive
is preserved data, not yet a numerical training dataset: per-corner FBX/SG
ingestion and author high→low correspondence have not been implemented here.

### Run, pause and resume

After preparing a reviewed capture manifest with explicit independent splits:

```powershell
& '_results~/training-venv/Scripts/python.exe' 'Tools~/LearnedSeamBenchmark/prepare.py' --manifest '_results~/learned-seams/TRAINING_MANIFEST.json' --out '_results~/learned-seams/training-data-001'
& '_results~/training-venv/Scripts/python.exe' 'Tools~/LearnedSeamBenchmark/train.py' --prepared '_results~/learned-seams/training-data-001' --out '_results~/learned-seams/run-001' --epochs 200 --device cuda:0
```

The output directory must not exist. There is one AdamW update per independent
model per epoch; references have equal gradient contribution within that model.
FP32, width 64, four residual blocks, constant LR 0.001, weight decay 0.01 and
seed 73 are starting settings, not optimized training defaults. Normalization
uses train data only. Boundaries never enter the learned loss or seam metrics.

`--max-epochs 20` limits one invocation to 20 epochs while retaining the same
total `--epochs 200`. Resume with the exact original configuration and dataset:

```powershell
& '_results~/training-venv/Scripts/python.exe' 'Tools~/LearnedSeamBenchmark/train.py' --prepared '_results~/learned-seams/training-data-001' --out '_results~/learned-seams/run-001' --epochs 200 --device cuda:0 --max-epochs 20
& '_results~/training-venv/Scripts/python.exe' 'Tools~/LearnedSeamBenchmark/train.py' --prepared '_results~/learned-seams/training-data-001' --out '_results~/learned-seams/run-001' --epochs 200 --device cuda:0 --resume
```

Use the limited first invocation instead of the full invocation above; do not
run both against the same existing directory. The last completed epoch is the
resume boundary; an interrupted partial epoch is replayed. Checkpoints preserve
model, optimizer, Python/NumPy/Torch RNG, history, normalization and data/config
hashes. Source/runtime/config/data changes are refused rather than silently
continuing a different experiment. Exact reproduction is tested in the same
runtime/device; it is not promised across different GPUs or library versions.

### Results and limits

`best.pt` is chosen by validation macro F1; the threshold is selected on val,
with ties preferring proximity to 0.5. The untouched test split is evaluated
only after the configured training end using the selected best checkpoint.
Reloaded validation probabilities must reproduce the saved SHA before test.
`last.pt`, `best.pt`, `metrics.json` and `provenance.json` are saved locally;
checkpoint/JSON writes replace files atomically. A completed test run cannot
be extended to tune against the same test set.

If the final JSON was lost after the finalized checkpoint was written, `--resume`
restores that JSON without evaluating test again. The first checkpoint appears
after the first completed epoch. CLI progress prints epoch/total, train BCE,
validation macro F1, threshold and whether the best checkpoint improved.
Final `last.pt` is a finalized result containing the best model and end-of-training
optimizer state; it is not a checkpoint for extending an already evaluated run.

Precision/recall/F1 use 3D internal-edge lengths normalized within each reference,
with equal reference contribution per independent model. Reports include per-model,
macro and normalized micro scores; zero denominators return zero. Plain edge
accuracy is deliberately omitted because most edges are not seams. Independent
thresholding still does not guarantee continuous cuts, disk charts or no overlaps.
Checkpoint tests on synthetic fixtures validate training/resume and leakage
guards; they do not measure learned artistic UV quality. Time/VRAM on the
RTX 3070 Ti laptop have not been measured by runs on the current GTX 980 Ti host.
