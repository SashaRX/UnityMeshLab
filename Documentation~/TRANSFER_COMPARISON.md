# Autonomous UV transfer benchmark

This benchmark compares transfer methods on identical, frozen source UV0/UV2 and
target geometry/UV0. It does not change the operator's scene, meshes, camera,
preview materials or tool settings. Imported-asset preparation uses owned mesh
copies. Output is outside `Assets/` and `Packages/`.

## Run from Mesh Lab / Unity MCP

The diagnostics section contains **Run synthetic transfer benchmark** and
**Compare transfer methods on last capture**. Equivalent menu paths:

- `Tools/Mesh Lab/Diagnostics/Benchmark Transfer - Synthetic Corpus`
- `Tools/Mesh Lab/Diagnostics/Benchmark Transfer - Last Capture`
- `Tools/Mesh Lab/Diagnostics/Benchmark Transfer - Config File`

MCP can invoke the first two with `execute_menu_item`; poll editor state and the
`[TransferBenchmark]` console message to find the completed report. The config-file
menu opens a file picker; use the batch entry point for unattended config runs.

For the exact problematic scene run, enable **Capture next Full Pipeline / Transfer
run** and run the pipeline once. Preserve the complete capture directory. A current
state snapshot has no historical transfer pairs and cannot be used as a corpus.
See [capture and replay](TRANSFER_CAPTURE.md).

## Unattended execution

Use a separate Unity project with a `file:` UPM reference to this checkout. Close
that project before starting; the script refuses a project with a Unity lock file.
Prepare a new project automatically (existing directories are refused):

```powershell
& ./Tools~/prepare-transfer-benchmark.ps1 `
  -PackagePath (Get-Location).Path `
  -ProjectPath 'C:/Temp/MeshLabBench' `
  -UnityVersion '6000.2.6f2' `
  -FbxPaths 'E:/Models/Example.fbx'
```

`FbxPaths` is optional. FBX files and their importer `.meta` settings are copied;
original files remain untouched. `source-inputs.json` records their original paths
and SHA-256 hashes. Refer to the copied assets under `Assets/TransferBenchmarkInputs`
in `assetCases`. Textures/material dependencies are not copied; declare their
effective aspect explicitly. First launch resolves the package's UPM dependencies.

Copy `Tools~/transfer-benchmark.example.json` and add capture manifests to `captures`.
Relative paths resolve from the config file. The script launches a hidden editor,
limits execution time and terminates only the process it started on timeout.

```powershell
& ./Tools~/run-transfer-benchmark.ps1 `
  -UnityEditor 'C:/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe' `
  -ProjectPath 'C:/Temp/MeshLabBench' `
  -ConfigPath 'C:/Temp/transfer-benchmark.json' `
  -TimeoutSeconds 1800
```

The underlying entry point is
`-batchmode -executeMethod SashaRX.UnityMeshLab.TransferBenchmarkCommands.RunBatch
-meshLabTransferBench <config.json>`. **Do not pass `-quit`**: the async entry point
exits after saving. Without a config it runs the synthetic corpus. Exit 0 means
all requested comparisons completed with deterministic UV2/mapping and unchanged
inputs. Exit 2 means invalid inputs, cancellation, a failed method or a
nondeterministic result. Poor UV quality remains visible in the report; intentionally
bad baselines are comparisons, so quality alone does not fail execution.

## Imported FBX preparation matrix

`assetCases` can prepare inputs without an open scene or tool window. FBX meshes
must follow the package's `Name_LOD{N}` naming convention. The imported asset is
read only. Each source/target pair is copied, optionally split with coordinated
source symmetry parameters, packed once, then frozen before timed methods run.

```json
{
  "asset": "Assets/Bench/Grandma_Cabinet_B_Destructed.fbx",
  "label": "cabinet-adaptive-aspect-on",
  "sourceLod": 0,
  "groups": ["Grandma_Cabinet_B_Doors_B", "Grandma_Cabinet_B_Table"],
  "symmetry": "adaptive",
  "textureWidth": 1024,
  "textureHeight": 2048,
  "correctSourceAspect": true,
  "resolution": 512,
  "normalizeDensity": true,
  "arap": true
}
```

Empty `groups` selects all matched pairs. Unknown groups or ambiguous sources fail
explicitly. `symmetry` is `off`, `legacy` or `adaptive`. Texture size is an explicit
input, not inferred from a missing imported material; include material tiling in
the declared effective aspect. Use separate cases for source aspect ON/OFF and
symmetry modes. To isolate aspect correction, set `normalizeDensity=false` and
`arap=false` for both cases. These preparation cases do not reproduce UI auto-tune,
weld or material/submesh resolution; an exact captured pipeline run covers those.

## Methods and controls

| Method | What it compares |
|---|---|
| `grouped` | Current production transfer with captured cross-LOD hints |
| `grouped-no-hints` | Same algorithm, both hint lists removed |
| `uv0-nearest` | Global UV0 triangle BVH, barycentric source UV2 interpolation |
| `surface-nearest` | Global 3D triangle BVH, barycentric source UV2 interpolation |
| `surface-normal` | 3D nearest with normal dot >= 0.25; counts unfiltered fallbacks |
| `shell-similarity` | Nearest source shell centroid; least-residual direct/mirrored similarity |

The last four are benchmark baselines, not new production options. They deliberately
expose UV0 ambiguity, thin-surface errors and limits of a similarity transform.
All methods receive the same worker coordinate system; no hidden world alignment
or vertex welding is performed. Timings include BVH construction and transfer,
exclude source preparation, warmup, quality scans, tracing and file IO.

Seven analytic cases cover corrected and deliberately uncorrected 1024x2048 UVs,
mirrored stacked UV0, close opposite surfaces, changed LOD triangulation, nonlinear
UV2 and a connected folded source chart. The analytic source atlas is fixed: these
cases isolate transfer, while imported cases exercise the native source packer.
The two negative controls are labelled; correspondence truth and quality are
separate checks. A transfer cannot repair a distorted source atlas by preserving
its coordinates exactly.

## Reports and decision criteria

Each run creates `BenchmarkReports/transfer_compare_<UTC>_<id>/`:

- `index.html`: comparison table and linked UV triangle SVGs (up to 20,000 faces
  per view; the shown/total count is printed). Full metrics still cover all faces.
- `comparison.json` and `comparison.csv`: scalar metrics, timing samples, source
  and target hashes, errors, deterministic/input-preservation flags and provenance.
- `details/<SHA256>.json`: full UV2/reference, per-face quality, production matching
  trace and shell assignments. Metadata stays compact independently of mesh size.
- `manifest.json`, `meshes/` and `details/`: portable frozen input corpus. Add this
  manifest to another config's `captures` to repeat without the original FBX/project.
  When reused, its reference is treated as a recorded baseline; original analytic
  truth/control labels remain available in the originating `comparison.json`.

Every measured repeat must have the same raw UV2 bit hash and shell mapping hash.
An additional untimed tracing run must agree. The final quality/reference metrics
use the common captured clamp policy, with the number of clamped vertices reported;
the repeat UV hash is before clamp. Full mesh snapshots are compared after each
method, and public production/symmetry counters are restored. Reports are updated
after each method so a partial run keeps completed rows.

Read anisotropy against 3D geometry (1 is isotropic), stretched faces (>1.25),
degenerate/invalid faces, positive-area triangle overlap including same-shell
overlap, out-of-bounds vertices, misses and reference error in atlas texels.
Source quality uses local geometry; target quality uses the captured target world
matrix. Unknown atlas dimensions use a 1x1 domain for reference error. Overlap
scans have a two-million comparison budget; incomplete results are lower bounds,
never proof of zero overlaps. Pair counts and summed pair areas are not unique
overlap area. Do not rank methods using anisotropy alone: a collapsed result can
have no valid triangles contributing to its mean.

Recorded outputs are **not independent ground truth**. For real LODs, use source
quality to locate pre-transfer defects, inspect matching traces/UV views, and keep
the exact pipeline capture for material, symmetry and auto-tune analysis. The
benchmark reports individual metrics and does not choose an overall winner.

Limits: 128 cases, 6 unique methods, 2..7 measured repeats, 0..2 warmups, config
1 MiB, report/capture metadata 64 MiB and each verified mesh/detail payload 512 MiB.
