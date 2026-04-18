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
