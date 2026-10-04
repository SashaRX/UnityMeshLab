# Seam placement research and UV render gallery

These tools measure actual UV cuts on **identical closed manifold geometry**.
They do not change Unity's production unwrap, settings, native plugins or assets.
Private mesh captures/checkpoints/third-party checkouts must remain in ignored
scratch directories. Do not commit them. See `Documentation~/UV_SEAM_PLACEMENT.md`
for the measured results and limitations.

Python: NumPy, SciPy, scikit-learn, Matplotlib. Tests:

```powershell
$env:PYTHONDONTWRITEBYTECODE='1'
python -m unittest discover -s 'Tools~/SeamPlacementBenchmark' -v
python 'Tools~/SeamPlacementBenchmark/compare.py' --v1 V1_CAPTURE_DIR --v2 V2_CAPTURE_DIR --labels edges.json --out RESULTS
```

Capture format: little-endian int32 `vertexCount,indexCount,chartCount`, then
float32 XYZ, float32 UV, int32 triangle indices, int32 per-vertex chart IDs.
`compare.py` rejects different exact positions/connectivity and compares actual
UV continuity, including duplicate render vertices. `edges.json` is the evaluated
reference-label artifact from the source-signal study, not an algorithm input.

Geometry-only proposals:

```powershell
python 'Tools~/SeamPlacementBenchmark/relocate.py' --reference REFERENCE.bin --initial AUTO.bin --out RESULTS
python 'Tools~/SeamPlacementBenchmark/thickness.py' --reference REFERENCE.bin --out RESULTS
python 'Tools~/SeamPlacementBenchmark/planes.py' --reference REFERENCE.bin --out RESULTS
python 'Tools~/SeamPlacementBenchmark/creases.py' --reference REFERENCE.bin --out RESULTS
```

These save edge masks (`*.npz`), exact per-face labels (`*-labels.npy`) and settings.
They are **boundary proposals**, not finished UVs. Full region-constrained xatlas:

```powershell
cmake -S 'Tools~/SeamPlacementBenchmark' -B BUILD_DIR -G 'Visual Studio 17 2022' -A x64
cmake --build BUILD_DIR --config Release --parallel 4
python 'Tools~/SeamPlacementBenchmark/regions.py' --reference REFERENCE.bin --probe BUILD_DIR/Release/seam_region_probe.exe --labels REGION_LABELS.npy --out REGION_RESULTS
python 'Tools~/SeamPlacementBenchmark/collect.py' --reference REFERENCE.bin --prototype PROPOSALS.npz --directory REGION_RESULTS --out ALL.npz
python 'Tools~/SeamPlacementBenchmark/compare.py' --v1 V1_CAPTURE_DIR --v2 V2_CAPTURE_DIR --labels edges.json --prototype ALL.npz --out SCORES
python 'Tools~/SeamPlacementBenchmark/render.py' --reference REFERENCE.bin --capture artist=REFERENCE.bin --directory REGION_RESULTS --scores SCORES/scores.csv --out RENDERS
```

The executable compiles the existing vendored xatlas into a **standalone binary**;
it never copies anything into Plugins. `faceMaterialData` protects region
boundaries; xatlas can add cuts within a region. All runs include a no-region
control and three recorded chart profiles. Geometry is restored through `xref`.
The `pack` mode accepts existing UVs and face-chart uint32 labels; it only packs.

`render.py` writes one actual UV PNG per capture, overlap annotations, metrics/
hashes, overview sheets, a clickable README and filterable `index.html`. Open the
HTML in a browser. `--reuse` checks capture hashes and reuses existing individual
PNGs/quality CSV; regenerated overview sheets then omit overlap highlights.
Full scans include adjacent triangles inside the same chart. `--extra NAME=FILE`
on `compare.py` adds independent unwrap captures. `--plot NAME...` selects 3D views.

## OptCuts (external, optional)

Acquire [official OptCuts](https://github.com/liminchen/OptCuts), tested commit
`cd2302671af7954f263b0ea93d8419aa943d54be`, separately. For Windows/CMake 4,
`optcuts-windows.patch` contains build/platform fixes only. Apply it in that
checkout with `git apply --unidiff-zero --ignore-space-change optcuts-windows.patch`, configure with `'-DCMAKE_POLICY_VERSION_MINIMUM=3.5'`,
build Release, and add the build's `stb_image/Release` to the child PATH. The
generated export-header patch is Windows-specific; do not apply it on Linux.

```powershell
python 'Tools~/SeamPlacementBenchmark/export_obj.py' REFERENCE.bin reference.obj
# Run from the directory containing reference.obj, using a relative input path.
OptCuts_bin.exe 100 reference.obj 0.999 1 0 4.5 1 1 raw
python 'Tools~/SeamPlacementBenchmark/import_obj.py' output/reference_Tutte_0.999_1_OptCuts_raw/finalResult_mesh_normalizedUV.obj REFERENCE.bin OptCuts.bin
python 'Tools~/SeamPlacementBenchmark/piecewise_optcuts.py' --reference REFERENCE.bin --labels REGION_LABELS.npy --optcuts OptCuts_bin.exe --dll-directory STB_DLL_DIR --packer seam_region_probe.exe --out FRESH_OUTPUT
```

The converter requires preserved ordered source corners with only positive
uniform scale/translation. Piecewise runs split disconnected boundary fans and
abort on any failed region. Output is density-normalized and packed. Use a fresh
output directory to prevent accidentally reading stale solver results.

## Full PartUV (external, optional research)

Acquire [official PartUV](https://github.com/EricWang12/PartUV) and the official
PartField checkpoint separately. Respect their licenses; bundled NVIDIA modules
have non-commercial research/education limitations. They are not redistributed.
Tested PartUV source `46bad07092a9396f00d7e04ea90369ded7de3d18`, Linux wheel 0.1.2.

Use an isolated Windows environment with Torch 2.5.1/cu118, compatible official
torch-scatter, and PartUV's Python dependencies for frozen feature inference.
Checkpoint restricted loading stays enabled; the permitted globals are tensors,
OrderedDict, CfgNode and set. No training or fine tuning is performed.

```powershell
python 'Tools~/SeamPlacementBenchmark/partfield_probe.py' --partuv PARTUV_SOURCE --checkpoint MODEL.ckpt --reference REFERENCE.bin --out RESULTS
python 'Tools~/SeamPlacementBenchmark/partuv_tree.py' --partuv PARTUV_SOURCE --features RESULTS/partfield-features.npy --reference REFERENCE.bin --out PARTUV_INPUT
```

The first script's fixed-count Ward masks are a probe, **not PartUV results**.
The second calls the author's complete hierarchy builder. Install `partuv==0.1.2`
and NumPy into an isolated Linux environment, then run the full native pipeline:

```bash
python Tools~/SeamPlacementBenchmark/partuv_run.py --input PARTUV_INPUT --config CONFIG.yaml --threshold 1.25 --out PARTUV_OUTPUT
```

For the GTX 980 Ti / WSL setup, `unwrap.pamo: false` is required: default PAMO
reported CUDA 801 errors. ABF, 5 iterations, remaining configuration unchanged;
thresholds 1.1/1.25/1.5 were tested. The Linux script imports the unmodified native
core directly to avoid reloading the already-completed Torch preprocessing.
Native combined-component count is replaced by actual UV-connected chart count;
all source faces are checked and density is normalized before packing:

```powershell
seam_region_probe.exe PARTUV_OUTPUT/unpacked.bin PARTUV_OUTPUT/charts.dat PartUV.bin pack 1 0.5
```

The packing stage is vendored xatlas, 512/padding 3, not the paid UVPackMaster
used in some author examples. Render and score `PartUV.bin` like any other capture.
