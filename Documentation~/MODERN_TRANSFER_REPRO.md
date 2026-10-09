# Modern furniture transfer corpus

## Inputs and isolation

The corpus uses `Modern_DressingTable_A.fbx`, `Modern_Kitchen_A.fbx` and
`speaker-retro.fbx`, including their importer `.meta` files. The FBX files are
not committed. `prepare-transfer-benchmark.ps1` copies them into a new temporary
Unity project and records SHA256 hashes in `source-inputs.json`. It never opens
or modifies the source project. Meshes with `_COL` and LOD0-only decorations do
not form transfer pairs. The three FBX files contain eight LOD0 → LOD1 pairs.

```powershell
$package = (Get-Location).Path
$project = Join-Path $env:TEMP 'UnityMeshLab-modern-transfer-new'
$fbx = @('C:/path/to/Modern_DressingTable_A.fbx',
         'C:/path/to/Modern_Kitchen_A.fbx',
         'C:/path/to/speaker-retro.fbx')
& "$package/Tools~/prepare-transfer-benchmark.ps1" -PackagePath $package -ProjectPath $project -FbxPaths $fbx
& "$package/Tools~/run-transfer-benchmark.ps1" `
    -UnityEditor 'C:/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe' `
    -ProjectPath $project `
    -ConfigPath "$package/Tools~/modern-furniture-benchmark.example.json"
```

The example compares welding on/off, with pre-optimization and adaptive symmetry
preparation, across six transfer methods. It also includes the seven analytic
controls. Each real pair gets its own source atlas. The explicit 1:1 source
texture metric is a controlled comparison: material/texture dependencies are
not copied, and this does not reproduce the original scene's material metrics
or UI auto-tune. Use **Capture next run** in the actual project when those
settings matter; pass its `manifest.json` through `captures` to replay exact
prepared meshes. Captured output is a comparison baseline, not correspondence
ground truth. See [TRANSFER_CAPTURE.md](TRANSFER_CAPTURE.md).

## Measurements on 2026-10-09

Unity 6000.2.6f2 / DX11. Grouped transfer, welding on, two measured repetitions,
zero warmups, eight paired parts. Before = `a0c3bbd`; after = tangent-handedness
guard. Both runs were deterministic and left the prepared inputs unchanged.
All positive-area overlap scans completed. Anisotropy is weighted by 3D area;
1 is isotropic. Degenerate triangles remain defects even when their area is
too small to materially affect the average.

| Target part | Degenerate faces before → after | Overlap pairs before → after | Anisotropy before → after |
|---|---:|---:|---:|
| DressingTable Frame LOD1 | 4 → 3 | 22 → 12 | 3.87 → 3.66 |
| DressingTable Handle LOD1 | 0 → 0 | 2 → 2 | 1.13 → 1.13 |
| Kitchen Backsplash LOD1 | 0 → 2 | 2 → 0 | 1.11 → 1.03 |
| Kitchen Countertop LOD1 | 14 → 15 | 2 → 1 | 1.05 → 1.05 |
| Kitchen Handle LOD1 | 24 → 0 | 12 → 0 | 512.56 → 1.05 |
| Kitchen Main LOD1 | 4 → 4 | 12 → 12 | 1.03 → 1.03 |
| Speaker 1 LOD1 | 0 → 0 | 0 → 0 | 1.35 → 1.35 |
| Speaker 2 LOD1 | 0 → 0 | 0 → 0 | 1.05 → 1.05 |

This fixes the destructive tangent seam weld and the Kitchen Handle collapse.
It does **not** establish that either furniture model has a valid final atlas.
The Frame, Main, Countertop and Backsplash still need correspondence/source
parameterization work. The original `Quality: accepted`/100% coverage summary
does not certify absence of collapsed or overlapping triangles.

The six-method frozen-input comparison completed 96 rows, with two repetitions
per row, unchanged inputs and deterministic outputs. Global nearest UV/3D
methods removed some collapses but created thousands of overlaps on the Frame
and Countertop. They cannot replace chart-aware transfer on this corpus.
Repeating all 96 frozen rows after the Sonar helper extraction and source-hint
invalidation preserved every quality/reference counter; timing is compared
separately. The affected EditMode subset passes 353/353 tests without skips.

### Rejected dedup experiment

Countertop target shell 17 initially matched source 11 with mean squared surface
distance ≈0.0000043, then dedup reassigned it to source 17 at ≈0.028. The latter
has one face; projection collapses 11 target faces. The current absolute
catastrophic-distance threshold is scale dependent. A mesh-relative guard
reduced degenerates 15 → 4 but increased overlap area from ≈9.4e-9 to ≈0.0010.
A constrained geometry candidate did not resolve that tradeoff. This experiment
was rejected and **is not present in production code**. A future repair must
validate both the correspondence and overlaps between shared-source fragments.

### Preview and pipeline ownership

Two full pipeline runs with active Checker reproduced missing render meshes on
the second run: Speaker 2 missing, DressingTable 2 missing, Kitchen 4 missing
before the next canvas collection. Pipeline mutations now suspend overlays
before replacing/destroying meshes, using the existing nested preview scope.
The selected display mode and framing survive completion/cancellation/errors.

Auto-tune snapshots now own source working meshes, packed source atlases and
dimensions, target transfers, validation reports and symmetry flags together.
Replacing an attempt, selecting a winner, cancellation and exceptions release
the appropriate temporary meshes. Repainting cannot bind a discarded attempt.

Cross-LOD source indices belong to the concrete source mesh, not just its entry
and group name. Replacing that mesh, restoring the winning attempt, refreshing
the context or receiving its in-place `VertexChannels.Changed` event invalidates
the corresponding hints and source transforms. A two-chart fixture with nearby
surfaces and swapped chart indices reproduced a wrong atlas selection in all
three mutation paths before the correction.

An isolated async full-pipeline probe on the fixed code exercised preview
collection on editor updates throughout two runs per model. No renderer had a
missing mesh. Speaker completed in approximately 0.45–0.68 seconds per run;
its reported hang was **not reproduced** with the controlled settings. These
times are local observations, not throughput targets. Actual scene/captured
settings are still needed to diagnose a hang outside this setup.
