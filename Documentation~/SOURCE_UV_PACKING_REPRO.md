# Source UV packing precision — 2026-10-09

Continuation of PR #225. Baseline: `24266da`. Native fix: `48a5e29`;
rebuilt plugins: `3309372`. Unity 6000.2.6f2, Windows DX11, isolated project
with original FBX and import metadata copied from the E: project.

## Causal result

`xatlas::AddUvMesh` rejects UV triangles whose float area is at most
`FLT_EPSILON`, independently of edge scale. Texel-density normalization
produces valid faces below this threshold. Their vertices remain uncharted;
managed orphan recovery then snaps them onto nearby packed vertices and
collapses or folds the source atlas before transfer begins.

The bridge recomputes the UV face mask after a successful AddUvMesh and before
ComputeCharts. It rejects non-finite coordinates and compares double area
against `4 * DBL_EPSILON * max(edgeLengthSquared)`. Uniform UV units, density
options and exported ABI are unchanged. The pinned upstream snapshot is
unmodified; its implementation is compiled in the bridge translation unit so
the local adapter can access the face mask. Updating that snapshot must also
review this adapter.

## Source stage probe

Each mesh follows import → optional UV weld → adaptive symmetry split → ARAP
→ density normalization → native packing → assignment → orphan recovery.
No changes are saved to original models or scenes.

| Source / weld | Baseline UV degenerates / overlaps | Fixed | Fixed mean / worst axis stretch |
|---|---:|---:|---:|
| TrainСarriage_Base_LOD0 / off | 43 / 17822 | 8 / 0 | 1.089 / 10.125 |
| TrainСarriage_Base_LOD0 / on | 42 / 13346 | 8 / 4 | 1.072 / 13.262 |
| Modern_DressingTable_A_Frame_LOD0 / off | 11 / 295 | 0 / 10 | 1.059 / 41.694 |
| Modern_DressingTable_A_Frame_LOD0 / on | 11 / 194 | 0 / 7 | 1.079 / 41.771 |

Train has eight degenerate imported 3D faces. Its density-normalized input
also contained 248/280 valid faces rejected by the absolute native cutoff
(weld off/on). Frame had zero degenerate UV faces after ARAP, but the native
cutoff left 50/51 orphan vertices. These input failures are reproduced
before any target LOD or BVH transfer runs.

Remaining Frame overlaps and Train weld-on overlaps are already present after
ARAP. Keeping small triangles cannot unfold those charts.

## Prepared end-to-end corpus

44 asset configurations produce 80 source/target pairs; seven analytic
controls bring the total to 87. Each runs grouped and grouped-no-hints twice:
**174 rows**, complete, zero row errors, deterministic transfer outputs and
unchanged frozen inputs. Covers Modern furniture, Shelf, Tire, Park_Bench_A,
Bench_Metal_A and Train, including source LOD1 → target LOD0.

**32 rows changed; 142 retained their source and transfer hashes.** Positions,
indices, normals, tangents and UV0 remain byte-identical in all changed inputs.
Shelf, Tire, Park and Bench transfer results are unchanged by the native fix.

| Target / configuration | Degenerates before → after | Overlaps before → after | Mean stretch before → after |
|---|---:|---:|---:|
| DressingTable Frame LOD1 / weld off | 0 → 0 | 111 → 0 | 1.033 → 1.021 |
| DressingTable Frame LOD1 / weld on | 3 → 1 | 12 → 0 | 3.659 → 1.033 |
| Train LOD0→LOD1 / weld off | 127 → 119 | 15245 → 63 | 5.368 → 6.258 |
| Train LOD0→LOD2 / weld off | 179 → 188 | 5321 → 435 | 2927.320 → 1465.677 |
| Train LOD1→LOD0 / weld off | 321 → 319 | 3988 → 132 | 405.432 → 314.196 |
| Train LOD1→LOD0 / weld on | 175 → 171 | 3851 → 140 | 16.545 → 31.588 |

The native input defect is fixed; **the whole transfer is not regression-free**.
Comparison of seven source/target metrics flags 26 rows with at least one
worsened metric. Kitchen has tiny geometry and float line/sliver tradeoffs;
Train still has substantial target collapse and stretch. A source atlas
improvement is insufficient evidence that every target triangle improved.

For example, Kitchen Countertop weld-on target face 3 was an exact UV line in
the baseline and becomes a floating sliver (~1.56e9 axis stretch). The old
global mean excluded that collapsed face; the new mean includes its finite
ratio, changing 1.048 to 56.406. This is not a successful repair. Separately,
Train weld-off LOD2 face 3071 changes from a similarity transform (stretch
1.001) to legacy geometry fallback (8223.687). Its geometric area is 0.0533,
so this is not merely an insignificant imported micro-triangle.

## Rejected alternatives

1. Uniformly scaling all native UVs by a power of two restores tiny faces but
   changes automatic texels-per-unit because packing floors its area estimate
   at one. Explicit density compensation also changes maximum-resolution
   behavior. The 174-row trial had 32 strict metric regressions; it is excluded.
2. Choosing an existing transform using local chart quality reduced some
   collapse counts, but Train weld-off LOD2 overlaps increased from 435 to 438
   compared with native-only. Tire numeric metrics also worsened. The complete
   174-row trial had 44 strict regressions relative to the original baseline.
   It is excluded; GroupedShellTransfer is unchanged by this source fix.

## Validation and local artifacts

- Native build run `37910357333`: Windows, Linux and universal macOS all pass
  remesh and uv-input CTests. Plugins are published by CI, never edited locally.
- Unity EditMode: **154 passed, 0 failed, 3 ignored**. Ignored Cabinet cases
  require a corpus absent from the isolated project. The three new tests check
  automatic density, explicit density and a shared atlas with mixed UV scales.
- Both FBX define reference builds, identifier/dependency checks and CI metadata
  gates pass. CI Unity execution remains license-gated; local results above
  are actual Unity Test Runner results.
- Ignored artifacts: `_results~/source-preparation-20261009/`. Stage snapshots
  in `stage-baseline/` and `native-stage/`; prepared reports in
  `prepared-baseline/transfer_compare_20261009_085358_199_2ca93f2a/` and
  `native-final/transfer_compare_20261009_092306_460_f1c29752/`.
  `native-final-audit.json` retains all metric comparisons and mutation checks;
  `native-face-audit.json` identifies newly stretched and recovered faces.
  `native-tests.xml` records the test run. Artifact paths are local and ignored,
  so clone users must regenerate them from their own model corpus.

## Next boundary

Target candidate selection must compare complete chart quality and certify
cross-chart footprints before replacing interpolation with geometry or
transform. A lower collapse count alone is insufficient, and line→sliver
must retain a collapse penalty. ARAP folds require their own source repair.
Reverse transfer additionally lacks target detail correspondence; this fix
does not establish a joint atlas or new one-to-many matching algorithm.
