# Local learned-seam data preparation

This prototype prepares numerical inputs and labels; it does not train a model,
change Unity, choose automatic splits, or evaluate generalization. Only NumPy is
required. The canonical capture reader is shared with SeamPlacementBenchmark.

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
