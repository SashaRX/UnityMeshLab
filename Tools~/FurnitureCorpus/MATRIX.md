# Transfer variant matrix on the private corpus

The existing corpus audit (`README.md`) measures one fixed per-pair setup. The
matrix audit runs every production-relevant variant on the same corpus. It covers
ordinary grouped transfer, the complete Forward pipeline and Reverse, and records
failures, defects and stage timings. `rank.py` then ranks error classes, defect
classes, variants and slow stages. Results are diagnostic. They do not certify
correspondence, and they never change production defaults by themselves.

Private geometry, captures and generated reports stay outside the repository.

## Running

```powershell
$env:MESHLAB_FURNITURE_CORPUS = 'D:/private-fixtures/Furniture'
$env:MESHLAB_CORPUS_MATRIX_OUTPUT = 'D:/private-results/matrix-<commit>-<date>.jsonl'
# Optional: limit a pilot run to the first N corpus files, and repeat timings.
$env:MESHLAB_CORPUS_MATRIX_LIMIT = '5'
$env:MESHLAB_CORPUS_MATRIX_REPETITIONS = '1'
# Run FurnitureCorpusMatrixTests in a closed, isolated Unity project (-testFilter).
python Tools~/FurnitureCorpus/rank.py D:/private-results/matrix-*.jsonl --out D:/private-results/matrix-ranking
```

The output file must be new and outside `Assets`/`Packages`. Each row is flushed.
A terminal `complete` row marks a finished capture. Without it, `rank.py` still
writes the report but exits with code 2.

## Variants

Each level has a `default` variant equal to production defaults. Every other
variant changes **one** factor from that default. Factorial runs come later and
only for the winning factors.

| Level | Variant | Meaning |
|---|---|---|
| `grouped` (per LOD pair, `TransferBenchmarkAssets.Prepare`) | `default` | Same as the existing audit: 512, symmetry off, weld + pre-optimize, ARAP on, grouped transfer |
| | `symmetry=legacy`, `symmetry=adaptive` | `Case.symmetry` |
| | `method=grouped-no-hints`, `method=uv0-nearest`, `method=surface-nearest`, `method=surface-normal`, `method=shell-similarity` | `TransferBenchmarkMethods` on the default source |
| | `arap=off`, `oversample=8` | Source repack profiles of the existing audit |
| `forward` (whole FBX, Full Pipeline through the workflow, as `PreviewLodSwitchTests.FrozenForwardReverseComparison` sets it up) | `default` | UI defaults: LegacyFixed symmetry, Auto 10 tex/m, shared atlas, junction cuts off, ARAP and density on, aspect correction on, 2 px padding, 0.25-texel boundary tolerance |
| | `symmetry=adaptive`, `symmetry=skip` | Symmetry mode, or skip the step |
| | `cuts=on` | `CutNarrowUvJunctions` |
| | `arap=off` | `ReparameterizeStretchedShells` |
| | `density=32` | `LightmapDensity` 32 tex/m |
| | `atlas=per-mesh` | `RepackPerMesh` |
| `reverse` (whole FBX, the Reverse button path with ordinary-transfer preparation) | `default` | Exclusive (frozen cascade), fill on, reach 0.05 |
| | `overlap=on` | Allow projected detail overlap (prepared-chain algorithm) |
| | `fill=off` | Fill final atlas off |
| | `density=32` | 32 tex/m |

## Row schema (version 2)

Every row is one JSON object per line (`JsonUtility` compatible) and has
`schema: 2`, `level`, `model` (corpus-relative FBX path), `variant`, `status` and
`inputHash` (SHA-256 of the FBX bytes).

- **`measured`**: one target mesh (or source mesh with `method: "source"`).
  - Fields: `source`, `target`, `lod`, `method`, `profile`, `quality`.
  - `quality` is `TransferUvQuality.Measure(mesh, uv, Vector2.one, Matrix4x4.identity)` with `faceAnisotropy` emptied.
  - Counters: `uvDegenerateFaces` (exact zero stored UV area), `geometryDegenerateFaces` (exact zero 3D area), `rawOutOfBounds` (before the final clamp, or -1 when the pipeline already clamped), `misses` and `fallbackVertices` (grouped methods; otherwise 0), `atlasSide`.
  - Optional fields use -1 when unknown:
    - forward only: `shellsRejected`, `shellsPoor`, `shellsDegraded` and `seamTrialAccepted` (0/1);
    - reverse only: `alignedArea`, `inheritedArea`.
- **`run`**: one variant execution of one model (or pair, for `grouped`).
  - `milliseconds` is wall time.
  - `stages`: `[{stage, lod, milliseconds, count}]` from `TransferPerformanceProfile` when available.
  - Also `atlasSide`, `fill` (LOD0 bbox fill fraction, -1 if unknown) and `repetition` (0-based).
- **`error`**: an exception escaped the variant.
  - Fields: `errorType` (exception type name), `error` (message plus first stack frame), `stage` if known.
  - The run continues with the next variant.
- **`refused`**: the pipeline declined without an exception (for example `ExecFullPipelineImpl` returned false, or Reverse reported a refusal). Message as for `error`.
- **`excluded`**: no matched LOD0-to-LOD pair, as in the existing audit.
- **`complete`**: terminal row with `fileCount`, `pairCount`, `errorCount`.

Original FBX bytes must remain unchanged. Each file is imported as an owned byte
copy and deleted afterwards, as in `FurnitureCorpusTests`.

## Ranking output

`rank.py` writes `ranking.json` (complete) and `ranking.md`:

- **Failures:** run errors and refusals grouped by level, type and digit-free message, ranked by status, then affected models.
- **Defects:** classes of the `default` variant, ranked by severity, then affected models.
  - Order: error, refused, incomplete scan, invalid UV, collapsed UV, overlap, extreme stretch (>10000 or non-finite), stretch >4, unmatched vertices, out of bounds.
  - Collapsed UVs use the direct stored-area counter, never 3D zero-area faces.
- **Variants:** per level, each variant against `default` on identical model/target keys, using `report.compare`.
  - Counts: better, worse, tradeoff and same; runs that fail where `default` succeeds (`failed`) or the reverse (`recovered`).
  - Also: total and median heuristic defect score, median time, median time ratio against `default`.
- **Best variant per model:** lowest heuristic defect score among successful runs; ties go to the faster run.
- **Slow stages:** per-model median time of each `default` stage, summed over models.

The defect score weights (`rank.WEIGHTS`) are a heuristic used for ordering
only. Dominance counts (`better`/`worse`/`tradeoff`) remain the evidence, and a
tradeoff is never reported as an improvement.
