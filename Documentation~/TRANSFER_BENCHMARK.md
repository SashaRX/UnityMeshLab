# Transfer Modes Benchmark — Protocol & Metrics

> See `EXPERIMENTS.md` for the experiments log (what we tried and why).
> This doc is about **how to measure** each run reproducibly.

## Purpose

As the UV2 transfer pipeline accumulates alternative modes (SymSplit
`LegacyFixed` vs `Adaptive`, `RepackPerMesh`, `splitTargets in SymmetryStep`,
topology iteration caps, free-space relocator, etc.) we need a repeatable way
to:

1. Run the same model through several mode combinations.
2. Collect machine-readable metrics (CSV + JSON).
3. Inspect remaining defects visually, filtered by category.

## Pipeline state (2026-05)

Repack stage (single mesh OR multi-mesh joint atlas):

1. **Extract shells** + per-face shell IDs (`UvShellExtractor`).
2. **Perturb UV0** of overlap-grouped shells (`PerturbOverlapShellsUv0`) so
   xatlas's chart-dedup heuristic gives every tile-instance a distinct atlas
   slot. Strength is adaptive from atlas resolution + padding by default,
   manual override via `RepackOptions.perturbStrength`.
3. **Pre-pack normalisation** (`TexelDensityNormalizer.Normalize`, two passes):
   - *Pass 1 — aspect.* Per-shell PCA on UV0 vertices and on 3D vertices
     projected onto the shell's mean tangent plane. Apply non-uniform scale
     along the UV principal axes so σ1_UV/σ2_UV matches σ1_3D/σ2_3D, capped
     by `maxShellAspect` (default 2). Area-preserving: scale_a1 × scale_a2 = 1.
   - *Pass 2 — density.* Re-measure UV area on the now aspect-correct shells,
     compute area-weighted (or median) target density, apply uniform per-shell
     scale, then a global shrink to `targetUvCoverage` (default 0.75) leaves
     packer slack so xatlas doesn't overflow the requested atlas resolution.
4. **xatlas pack** with `bruteForce + rotateCharts` on by default.
5. **UV2 write-back** + orphan-vertex fix.
6. **Atlas utilization log line** for visibility.

Transfer stage (per target LOD): `GroupedShellTransfer.Transfer` — unchanged
from earlier docs. Matches target shells to source by UV0 bbox + world
centroid + world normal, then applies similarity transform / interpolation /
strip-parameterization.

### Removed modes

- **`mergeOverlappingTiles`** (and its helpers: `IsEquivalentShellForTileMerge`,
  `BuildNonDuplicate*`, post-process `FixOverlappingUv2Shells`,
  `FixNearDuplicateUv2Shells`, `RelocateToFreeSpace`, `RescaleUv2ToUnit`).
  This mode collapsed tile-instance shells into one xatlas chart and copied
  UV2 from the rep to every duplicate. Wrong for lightmap UV2 — every plank
  needs its own lightmap region for correct baked lighting. The fix passes
  were band-aids for false overlaps the merge path produced; with merge gone
  they were unreachable. Deleted entirely.

### Open / next

- Aspect normalisation is approximate for curved surfaces (cylinder unrolled
  parameterization isn't AABB-extent-shaped). Acceptable for typical
  lightmap meshes (planks, walls, panels).
- Cross-LOD aspect consistency: pass 1 currently re-derives per-mesh; for
  multi-LOD groups it may be worth deriving on LOD0 then propagating.
- Iterative shrink-to-fit (auto-tune `targetUvCoverage` to land at exactly
  the requested resolution) — proposed, not implemented.

## Tooling

| Piece | Location | What it does |
| --- | --- | --- |
| `UvtLog.Category` | `Editor/UvtLog.cs` | Per-subsystem log filter. Toggle in *Pipeline Settings → Log filters*. |
| `BenchmarkRecorder` | `Editor/BenchmarkRecorder.cs` | Collects per-mesh metrics during `ExecFullPipeline` / `ExecTransferAll`; writes CSV + JSON into `<projectRoot>/BenchmarkReports/` on session end. |
| `SymmetrySplitShells.LastFallbackCount` / `LastTotalSplitCount` | `Editor/SymmetrySplitShells.cs` | Counters read by the recorder. |
| `GroupedShellTransfer.LastTopologyIterations` / `LastTopologyFixed` / `LastTopologyCapHit` | `Editor/GroupedShellTransfer.cs` | Counters for the Laplacian topology pass. |
| `UvCanvasView.ValidationFilterMask` | `Editor/Framework/UvCanvasView.cs` | Restricts the validation fill/overlay to selected `TriIssue` bits. |
| `TestSuiteAsset` | `Editor/Settings/TestSuiteAsset.cs` | ScriptableObject registry of benchmark cases (FBX + LOD path + expected ranges). Create via `Assets → Create → Lightmap UV Tool → Test Suite`. |

## Metrics (one CSV row per mesh × LOD)

Session-level (same across rows of one run):

- `timestamp`, `runLabel`, `lodGroup`, `symSplitMode`, `repackPerMesh`, `splitTargets`
- `atlasRes`, `shellPad`, `borderPad`, `sourceLod`
- `pipelineMs`, `repackMs`, `transferMs`, `validateMs` — accumulated stage timers.

Pre/post repack overlap pair counts used to ride here as scalar fields, but
they were sentinel-only (never populated) and were removed. Pre-pack overlap
counts are still visible in the verbose `[xatlas] Pre-repack mesh N: K shells, G overlap groups, P pairs` log line per mesh.

Per-row (snapshot of `TransferResult` / `ValidationReport` / static counters):

- `shellsMatched`, `shellsUnmatched`, `shellsTransform`, `shellsInterpolation`,
  `shellsMerged`, `shellsRejected`, `shellsOverlapFixed`
- `dedupConflicts`, `fragmentsMerged`, `consistencyCorrected`
- `verticesTransferred`, `verticesTotal`
- `uv2DuplicatePairs`, `compositeBrokenCount`, `severeMismatchCount`
  — see *Visual-defect counters* below
- `invertedCount`, `stretchedCount`, `zeroAreaCount`, `oobCount`, `cleanCount`
- `overlapShellPairs`, `overlapTriangleCount`, `overlapSameSrcPairs`
- `texelDensityBadCount`, `texelDensityMedian`
- `symSplitFallbackCount`, `symSplitTotalCount`
- `topologyIterations`, `topologyFixed`, `topologyCapHit`

### Visual-defect counters

The original `defectScore = stretched + zeroArea + oob` flagged geometric
defects, but missed the failure modes where the algorithm produces
"technically valid" UV2 that bakes wrong:

| Metric | What it catches |
| --- | --- |
| `uv2DuplicatePairs` | Pairs of target shells whose quantised UV2 fingerprint hash matches. Non-zero = two distinct 3D instances bake onto the same atlas region (silent lightmap bleeding between symmetric copies). Rejected/Unmatched shells excluded (they legitimately share the empty hash). |
| `shellsOverlapFixed` (== force3D overlap count) | Force3D-fallback shells whose UV2 AABB overlaps a non-fallback shell's UV2 AABB. Already warned via `[GroupedTransfer] UV2 overlap: ... bleeding likely`; surfaced here as a counter. |
| `compositeBrokenCount` | Target shells whose Phase 3 composite UV2 spilled out of the matched source UV2 region (`compArea > 2× srcArea`) and were forced back to single-source fallback. Signals a Phase 2 matching miss. |
| `severeMismatchCount` | Target shells whose chosen source is &gt;10% of mesh diagonal away in 3D. Almost always a wrong-source assignment by Phase 2 — e.g. wedge swapped with a sibling. |

These four feed into the sweep score (`BenchmarkSweep.Score`) with weights
`-50 / -30 / -10 / -20` respectively, so refactors that drag any of them
upward lose against the previous winner.

JSON output mirrors the CSV but nests `records[]` inside a run envelope.

## Protocol

1. **Prepare a suite.**
   `Assets → Create → Lightmap UV Tool → Test Suite`. Add one `TestCase` per
   model; set a short `label` (becomes `runLabel` in CSV), point `fbxAsset`
   at the FBX, and list your expected ranges in `expectations` (informational;
   not enforced automatically).

2. **Pick a mode combination.** In `LightmapTransferTool`:
   - `SymSplit thresholds` = `LegacyFixed` or `Adaptive`
   - `Per-mesh repack` on/off
   - `SymSplit target LODs (advanced)` on/off

3. **Run the pipeline.** Click *Run Full Pipeline*. `BenchmarkRecorder` wraps
   the call, writes `<projectRoot>/BenchmarkReports/{ts}_{lodGroup}_FullPipeline_{mode}.{csv,json}`
   when the run finishes.

4. **Inspect visually.** Open *Transfer tab → Validation Overlay*. Toggle
   `Inverted`, `Stretched`, `ZeroArea`, `OutOfBounds`, `Overlap`,
   `TexelDensity` to isolate a category on the UV canvas. `None` selected =
   every triangle drawn (original behavior).

5. **Compare.** Switch the mode combination, hit *Reset Pipeline State* →
   *Run Full Pipeline* again. Each run produces a separate CSV — diff with
   a spreadsheet / pandas.

### Provenance manifest + auto-archive

Every sweep run writes a `manifest.json` into `sweep_<ts>/` alongside the
`summary.csv` / `winner.json` / `index.html`, and copies the whole
directory into `BenchmarkReports/Archive/<sweepDirName>.zip` so old runs
stay organised even after many new sweeps. The source directory is left
in place; the zip is non-destructive.

`manifest.json` records:

- `package.{name, version, gitSha, gitBranch, gitDirty}` — UPM
  PackageInfo + `git rev-parse` (best-effort; blank when the package was
  installed via Library/PackageCache without `.git`)
- `unity.{version, platform}` — `Application.unityVersion` /
  `Application.platform`
- `host.{user, machine, os, processor}` — `Environment.*` +
  `SystemInfo.processorType`
- `sweep.{cellCount, caseCount, sweepLabel, matrix, scoringWeights}`
  — a literal mirror of the `SweepMatrix` that drove the run plus a
  snapshot of the `BenchmarkSweep.Score` weights at the time

Use case: when comparing a sweep run today against one from six months
ago, the manifest tells you whether the algorithm constants, package
version, Unity version, or scoring weights changed — so a metric delta
isn't silently caused by something unrelated to the actual change.

Cross-time tracking: point an external sync target (Google Drive,
Dropbox, OneDrive) at `BenchmarkReports/Archive/` and every sweep auto-
mirrors. The zip filename includes the sweep timestamp so chronological
sort is free.

### Multi-case sweep (all `TestSuiteAsset.cases[]` in one click)

For cross-model regression coverage — running the full sweep matrix on
every case in the suite without manually switching FBXes — use **Run
Multi-Case (N × M)**. Located in *Setup → Parameter Sweep*, right of the
single-model **Run Sweep** button. `N` is the number of `cases`, `M` is
the cell count.

For each case the runner:

1. Loads the case's `fbxAsset` via `AssetDatabase.LoadAssetAtPath` and
   instantiates it into the scene with `PrefabUtility.InstantiatePrefab`,
   marking the root `HideFlags.DontSave` (no scene-dirty leakage).
2. Resolves the LODGroup: `lodGroupPath` first (if set), otherwise
   `GetComponentInChildren<LODGroup>`. Skips the case with a warning if
   none found.
3. Calls `ctx.Refresh(lg) + OnRefresh()` to wire it into the tool.
4. Runs `ExecSweep(sweep)` against a per-case subdirectory
   `BenchmarkReports/sweep_<ts>_<caseLabel>/` so each model gets its own
   `summary.csv` + `winner.json` + `index.html`.
5. Destroys the spawned root in a `finally` block and continues to the
   next case. Cancel via the progress strip stops between cases.

When the loop ends the operator's original `ctx.LodGroup` wiring is
restored.

Cross-model analysis (pandas):

```python
import pandas as pd, glob, re
rows = []
for csv in glob.glob('BenchmarkReports/sweep_*_*/*.csv'):
    df = pd.read_csv(csv)
    df['model'] = re.search(r'sweep_\d+_\d+_\d+_(.+?)/', csv).group(1)
    rows.append(df)
all = pd.concat(rows)
all.groupby(['model','atlasRes','shellPad'])[
    ['uv2DuplicatePairs','severeMismatchCount','shellsOverlapFixed']
].sum()
```

### Parameter sweep (cartesian product of 7 axes)

For automated sweeps, fill `TestSuiteAsset.sweep`. Cells = product of all
array lengths.

```
atlasResolutions              = [256, 512, 2048]      # ctx.AtlasResolution
shellPaddingPxVariants        = [2, 4, 8, 32]         # ctx.ShellPaddingPx
borderPaddingPxVariants       = [0]                    # ctx.BorderPaddingPx
arapIterationsVariants        = [0, 50]                # 0 = ARAP off; >0 = on with N iters
stretchThresholdVariants      = [1.5]                  # Sander L² gate (only relevant when arap > 0)
internalOversampleVariants    = [4]                    # xatlas internal pack resolution multiplier
symSplitThresholdModeVariants = [LegacyFixed]          # LegacyFixed | Adaptive
resetBetweenRuns              = true
```

Per-cell label encodes every axis so the recovery regex can reconstruct
CellConfigs from filenames: `sweep_res{R}_pad{S}_bdr{B}_arap{A}_stretch{T}_os{O}_sym{legacy|adaptive}`.

In *LightmapTransferTool → Setup tab*, assign the asset to the **Sweep suite**
field; the neighbouring **Run Sweep (N)** button iterates the cartesian
product (N = product of array lengths). Each cell:

1. Sets `ctx.AtlasResolution` / `ShellPaddingPx` / `BorderPaddingPx`.
2. Calls `ResetWorkingCopies()` (no sidecar delete, no FBX reimport — just
   restores `originalMesh = fbxMesh` and clears pipeline flags).
3. Runs `ExecFullPipeline("sweep_res{R}_pad{S}_bdr{B}")` — each cell's CSV
   + JSON carry the cell identifier in the filename and as the `runLabel`
   column. BenchmarkRecorder additionally dumps one PNG per recorded mesh
   into a sibling `{fileBase}_png/` folder, showing the result UV2
   (repacked mesh on source LOD, transferred mesh on target LODs) with
   per-shell coloring — so visual diffs between cells are immediate.

Original atlas/padding values are restored when the sweep finishes or is
cancelled. A progress bar with **Cancel** is shown during the sweep.

Concatenate the output for analysis:

```
pandas.concat([pd.read_csv(f) for f in glob('BenchmarkReports/*_sweep_*.csv')])
```

### FBX baseline metrics (run once before a sweep)

Before running a sweep, export the source-FBX characterization so the sweep
numbers can be interpreted against each model's baseline.

Menus:
- `Mesh Lab → Export FBX Metrics (Selected Assets)` — select one or more
  `.fbx` assets in the Project window, then run. Scans every LODGroup /
  Renderer inside each FBX.
- `Mesh Lab → Export FBX Metrics (Scene LODGroup)` — select any GameObject
  under a LODGroup in the Hierarchy, then run. Scans that LODGroup only.

Output goes to `<projectRoot>/BenchmarkReports/FbxMetrics_{ts}/`:

- `FbxMetrics_{ts}.csv` — one row per mesh × LOD with vertex/triangle count,
  bounds size, avg edge length, shell count, UV0 coverage, AABB overlap
  pairs, OOB verts, estimated mirror pairs, UV2 stats (if present), etc.
- `png/<model>_<lodGroup>_LOD{N}_<renderer>_uv0.png` — UV0 snapshot with
  per-shell coloring + wire + 0–1 bounding box, range `[-0.1, 1.1]` so OOB
  verts are visible.
- `png/<model>_<lodGroup>_LOD{N}_<renderer>_uv2.png` — same for UV2 when
  present.

Share both the FBX metrics CSV and the sweep CSVs when asking for analysis;
joining on `(model, lodGroup, rendererName, lodIndex)` gives context for
each sweep cell (e.g. `postRepackOverlaps=0` on a model with
`uv0AabbOverlapPairs=120` is a much stronger signal than on a model with 2).

### Log filters

When a run is noisy (e.g. Adaptive threshold messages spam the console), open
*Pipeline Settings → Log filters* and uncheck the offending `UvtLog.Category`.
Verbosity (`Level`) still controls global threshold; the mask is an additional
silencer persisted per user in EditorPrefs
(`LightmapUvTool_LogCategoryMask`).

| Category | Typical messages |
| --- | --- |
| `General` | Default bucket for legacy `UvtLog.Info(msg)` calls. |
| `SymSplit` | Symmetry split detection + fallback matches. |
| `Repack` | Atlas repack via xatlas. |
| `Match` | Shell matching / similarity transform. |
| `Dedup` | Source-shell dedup passes. |
| `Overlap` | Post-transfer overlap detection & relocation. |
| `Topology` | Laplacian displaced-vertex pass. |
| `Validation` | `TransferValidator` summaries. |
| `Export` | FBX / sidecar export. |
| `Benchmark` | `BenchmarkRecorder` output paths. |

## Test matrix (fill in per-run)

| Model | SymSplit | RepackPerMesh | SplitTargets | Date | Result file | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| Playground | LegacyFixed | off | off | | | baseline |
| Playground | Adaptive    | off | off | | | compare fallbackCount, stretched, inverted |
| Playground | LegacyFixed | on  | off | | | compare topologyCapHit |
| WateringCan | LegacyFixed | off | off | | | simple symmetric case |
| Carousel    | LegacyFixed | off | off | | | rotational symmetry (N-fold) |

## Go / Stop criteria (suggested thresholds)

These are rules of thumb — adjust per case in the `TestSuiteAsset` expectations
list.

- **Inverted faces:** 0. Any non-zero = STOP.
- **Overlap shell pairs (diff-src):** 0 on source LOD. Up to 2 tolerable on
  target LODs.
- **Shells rejected:** 0. STOP if non-zero.
- **SymSplit fallbackCount:** <= 1 across all target LODs. Higher = shell
  descriptor hashing is unreliable on this model; investigate.
- **Topology cap hit:** false. If true, either increase
  `kMaxTopologyIterations` or accept residual displacement.
- **Coverage** (`verticesTransferred / verticesTotal`): >= 0.99.
- **uv2DuplicatePairs:** 0. Non-zero = silent lightmap bleeding; STOP.
- **shellsOverlapFixed (force3D overlap):** 0 on source LOD. Up to 1
  tolerable on target LODs (only on heavily-decimated geometry).
- **compositeBrokenCount:** 0 on source LOD. Up to ~5% of target shell
  count tolerable; higher = Phase 2 matching is misassigning to source
  shells that don't cover the target UV0 region.
- **severeMismatchCount:** 0 on source LOD. Non-zero on target LODs is a
  strong signal that a wedge / sibling got swapped — investigate the
  specific shells (`shellMatchDistSqr` column).

## Known models

- **Playground** — stress test with many separate groups, used across most
  experiments. Highly sensitive to fragment-merge behavior.
- **WateringCan** — simple mirror symmetry; canonical SymSplit binary case.
- **Carousel** — N-fold rotational symmetry; exercises `ApplyNFoldSplit`.

See `EXPERIMENTS.md` for the history of failed approaches on each of these.
