# Sweep analysis — LegacyFixed, 2026-04-19

**Source data:** `_results/BenchmarkReports/` on branch `results/legacyfixed-2026-04-19`.
60 sweep cells × (`Playground`, `Gazebo`, `Carousel`, `Wooden_Box_Long`) ×
(`res ∈ {256, 512, 2048}`, `pad ∈ {2, 4, 6, 8, 32}`, `bdr=0`), one run.
Mode: `LegacyFixed`, `RepackPerMesh=on`, `splitTargets=on`.

## Health check — pipeline itself

| Metric | Value | Verdict |
| --- | --- | --- |
| `shellsRejected` | **0** across every cell | clean |
| `overlapShellPairs` (diff-src) | **0** across every cell | clean |
| `coverage` (`verticesTransferred / verticesTotal`) | **1.00** across every cell | clean |
| `overlapTriangleCount` | 0 | clean |

Pipeline is **solid** — every vertex is transferred, no shells rejected, no
cross-source overlap on any LOD. All variance lives in validation metrics
below.

## Aggregated defect trends

`defectScore = stretchedCount + zeroAreaCount + oobCount` — summed across
target LODs of all four models per cell.

### defectScore pivot (lower = better)

| pad \ res | 256 | 512 | 2048 |
| --- | --- | --- | --- |
| **2** | 1225 | 1240 | 1225 |
| **4** | 1233 | 1171 | 1253 |
| **6** | 1092 | 1235 | 1216 |
| **8** | 1092 | 1218 | 1255 |
| **32** | **864** | **989** | 1209 |

Big `pad=32` wins on 256/512 for quality; on 2048 the effect flattens.

### avg repackMs pivot

| pad \ res | 256 | 512 | 2048 |
| --- | --- | --- | --- |
| 2 | 40 | 58 | 123 |
| 4 | 43 | 51 | 150 |
| 6 | 45 | 57 | 180 |
| 8 | 51 | 64 | 216 |
| **32** | **312** | **408** | **1157** |

`pad=32` is 8–10× slower than `pad=2` at the same resolution. The
quality win is only worth that cost on 256/512.

### avg texelDensityMedian pivot (lower = tighter UV2)

| pad \ res | 256 | 512 | 2048 |
| --- | --- | --- | --- |
| 2 | 106 | 94 | **72** |
| 4 | 161 | 114 | 82 |
| 6 | 216 | 152 | 99 |
| 8 | 283 | 161 | 89 |
| 32 | 1228 | 616 | 180 |

Rough rule: doubling `res` ≈ halves texel density; doubling `pad` ≈
doubles it. `res=2048, pad=2` is the tightest UV2.

## Per-model highlights

**Gazebo** — cleanest. Best cell `res=256, pad=2`, defectScore=15 across 2
target LODs. 25 mirror pairs detected in UV0, SymSplit handled them
fine.

**Wooden_Box_Long** — also clean, defectScore 14–20. `uv0AabbOverlapPairs=1631`
on a 991-vert mesh is very high (dense tiling in UV0), but the pipeline
still produces no diff-src overlaps. Good regression test for SymSplit.

**Playground** — largest model (10k verts × 3 LODs), defectScore 83–88 at
best. stretched+zeroArea are tiny (~85), but **`invertedCount` is
5000–8000**. Per per-LOD drill: LOD1 has 3000–5000 inverted, LOD2 has
1500–2500, LOD3 has 300–400. Per `TransferValidator` docstring, winding
flip is expected (UV0 vs UV2 independent), but at this scale it's worth
double-checking visually (UV2 PNG dumps are in the sweep `_png/`
folders).

**Carousel** — hardest. defectScore 741–965 (of ~3000 triangles =
**25–32% defective**). Breakdown per LOD at `res=512, pad=2`:

| LOD | inverted | stretched | zeroArea | topologyFixed | capHit |
| --- | --- | --- | --- | --- | --- |
| 1 | 876 | 305 | 366 | 6 | 0 |
| 2 | 421 | 194 | 199 | 12 | 1 |
| 3 | 179 | 28 | 10 | **34** | 1 |

LOD3 has the fewest triangles but the **most aggressive topology
enforcement** (34 Laplacian fixes), and the cap is hit on LOD2/LOD3 for
most cells. Cause is the N-fold rotational SymSplit — after splitting a
rotational pattern into N charts, the fragments don't always land on
clean UV0 boundaries, so `EnforceShellTopologyOnUv2` does a lot of work
on heavily simplified LODs.

## Topology cap signal

`topologyCapHit` summed across 60 cells:

| pad \ res | 256 | 512 | 2048 |
| --- | --- | --- | --- |
| 2 | 4 | 3 | 3 |
| 4 | 2 | 3 | 3 |
| 6 | 2 | 4 | 3 |
| 8 | 3 | 4 | 2 |
| 32 | 3 | 3 | 3 |

Cap hit rate ~5–10% of cells, consistent across pad. Mostly on Carousel
and Wooden_Box_Long. Worth one follow-up experiment: raise
`kMaxTopologyIterations` from 5 → 8, rerun Carousel sweep, see if
stretched/zeroArea drop.

## Recommendations

Immediate defaults (for `MeshLabProjectSettings`):
- **`atlasResolution = 512`**: best balance. `2048` gives ~60ms extra
  repack for modest quality gain; `256` is fine for tiny assets only.
- **`shellPaddingPx = 4`**: near-minimum repack cost (~50ms), defectScore
  mid-pack. `pad=32` wins on 256/512 defect count but pays 8× repack
  cost — only worth it in a final bake.
- **`borderPaddingPx = 0`** kept as default (not swept in this run).

Follow-ups worth running:
1. **Adaptive vs LegacyFixed** — exactly the same sweep with
   `SymSplit thresholds = Adaptive`. Expected signal on
   `symSplitFallbackCount` (currently all zero in this LegacyFixed
   sweep) and Carousel stretched/zeroArea.
2. **Carousel isolated** — same sweep + topology cap=8, see if Carousel
   defects drop materially.
3. **Investigate invertedCount** — inspect a handful of Playground
   `_png/` UV2 dumps visually. If the flipped triangles look correct
   (i.e. UV2 just has mirrored winding relative to UV0), confirm we
   can safely ignore this metric; if not, we have a real bug.
4. **Fill the res=1024 gap** — add 1024 to the sweep matrix so the
   `res × pad` surface is fully sampled.
5. **borderPad sweep** — `[0, 2, 4]` once defaults above are confirmed.

## Reproducing the analysis

Raw combined data: `_results/BenchmarkReports/` (CSV + JSON + PNG per
cell, plus `FbxMetrics_*/` baselines). One-liner:

```python
import pandas as pd, glob
sweep = pd.concat(pd.read_csv(f) for f in
    glob.glob('_results/BenchmarkReports/*_sweep_*.csv'))
```
