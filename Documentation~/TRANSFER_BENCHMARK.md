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
- `preRepackOverlaps`, `postRepackOverlaps` — UV shell AABB overlap counts
  (pre = UV0, post = UV2); set externally via
  `BenchmarkRecorder.Current.SetPre/PostRepackOverlaps`.
- `pipelineMs`, `repackMs`, `transferMs`, `validateMs` — accumulated stage timers.

Per-row (snapshot of `TransferResult` / `ValidationReport` / static counters):

- `shellsMatched`, `shellsUnmatched`, `shellsTransform`, `shellsInterpolation`,
  `shellsMerged`, `shellsRejected`, `shellsOverlapFixed`
- `dedupConflicts`, `fragmentsMerged`, `consistencyCorrected`
- `verticesTransferred`, `verticesTotal`
- `invertedCount`, `stretchedCount`, `zeroAreaCount`, `oobCount`, `cleanCount`
- `overlapShellPairs`, `overlapTriangleCount`, `overlapSameSrcPairs`
- `texelDensityBadCount`, `texelDensityMedian`
- `symSplitFallbackCount`, `symSplitTotalCount`
- `topologyIterations`, `topologyFixed`, `topologyCapHit`

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

### Parameter sweep (atlasRes × shellPad × borderPad)

For automated sweeps across repack parameters, fill `TestSuiteAsset.sweep`:

```
atlasResolutions      = [256, 512, 2048]
shellPaddingPxVariants = [2, 4, 8, 32]
borderPaddingPxVariants = [0]
resetBetweenRuns      = true
```

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

## Known models

- **Playground** — stress test with many separate groups, used across most
  experiments. Highly sensitive to fragment-merge behavior.
- **WateringCan** — simple mirror symmetry; canonical SymSplit binary case.
- **Carousel** — N-fold rotational symmetry; exercises `ApplyNFoldSplit`.

See `EXPERIMENTS.md` for the history of failed approaches on each of these.
