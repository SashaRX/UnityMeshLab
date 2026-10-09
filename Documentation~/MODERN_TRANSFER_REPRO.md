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

### Compressed FBX channel save: metal_beam

The Bakery messages about checking `Assets/__MeshLabTemp/corner_tags_*.fbx`
announce a UV overlap scan; they do not report an overlap. The reported save
failure came from decoding the temporary FBX corner tags instead.

The original `metal_beam.fbx` uses Low Mesh Compression. Its corner-zero tag
imports as approximately `-0.000000894`, which the previous sign check rejected
before rounding. High Compression also produces an approximately `0.00101`
fractional error at corner 131. The decoder now checks the rounded corner ID
after a bounded `0.002` precision check. Negative IDs, larger fractional errors,
non-finite numbers and inconsistent mesh ordinals are still refused. Exact
position/attribute pairing and atomic-write refusal remain unchanged; compression
is kept on the temporary import so its positions match the working mesh.

An isolated copy of the actual model successfully wrote UV1 (Unity UV2) at Off,
Low, Medium and High Compression. All 132 corner values equal the edited values;
original UV0 values, control points, polygons and the input file/metadata are
unchanged. The actual E: project model and its importer were not modified.
Original FBX SHA-256:
`95E9AF9F18BAE78536CFCED21EB363857F14802CDFFC6DE7D26C45813107AF26`.

A separate quad/n-gon fixture with UV seams and full-precision vertex colours
reproduced the old refusal at Low and Medium Compression. The fixed integration
test covers all four compression settings and verifies written corner values,
source bytes/metadata, polygons, control points, UV0, colours and FBX format.
Related FBX EditMode tests: **86 passed, 0 failed, 0 skipped** on Unity 6000.2.6f2.
Both reference C# compile variants also passed. Evidence is local under
`_results~/fbx-compression-20261009/` (`fbx-final.xml`, `metal-write-final.log`).
This case establishes safe export of the tested meshes, not a fix for the
remaining transfer overlaps or chart collapse described above.

### Shelf_C: shared source and rotation-invariant diagnostics

The reported Shelf_C case was tested on the current `f3cbb88` baseline. The
actual E: project and an isolated copy preserve every triangle corner's tangent
handedness through `UvEdgeWeld`: LOD0 587 → 551 vertices and LOD1 449 → 425,
with zero changed corner signs and no zero tangent.w values. The supplied TBN
warnings were not reproduced on this import. The original FBX and `.meta`
remain unchanged. FBX SHA-256:
`9CE4B1F0CC33509C73BEB0C52D0248797ABCAA984B87522E55EF18913E243BDD`.

Shared-source dedup and its post-dedup diagnostic previously treated UV0 AABB
intersections as triangle overlap. Both now use positive-area triangle tests.
Shared edges and disjoint triangle interiors may use one source independently;
a scan exceeding 200000 comparisons per shell pair retains the conservative
conflict but reports an incomplete check rather than a witnessed duplicate.
An integration fixture verifies the retained source assignments, affine UV2 and
absence of output overlaps. Actual stacked UV0 remains a conflict.

The former `shell #50` sliver warning compared UV2 bounds with a rotation-dependent
world AABB. Its actual per-triangle anisotropy is approximately 1.37:1 in the
square-metric forward capture; the reported 12.7:1 versus 1.4:1 was not a stretch
measurement. `CollapseDiag` now uses per-triangle UV/3D anisotropy with a threshold
of 5, then uses UV bounds/fill to describe the warning. Rotation alone cannot
turn a valid thin chart into a collapse. Truly collapsed and stretched charts
still report, and the diagnostic never edits UV2.

Controlled preparation uses the Shelf_C group only (excluding decorations
without a matching LOD), UV0 welding, vertex-preserving pre-optimization, ARAP
and density normalization. It compares forward/reverse, 1024×1024/1024×2048
texture metrics and off/legacy/adaptive symmetry. Actual materials and textures
are not copied. All six methods across the 12 prepared cases, with two measured
repetitions and no warmup, produce **72 frozen rows**. Every input hash, output
UV2 hash and quality counter matches the baseline; all rows are deterministic
and preserve their inputs. One false shared-source conflict disappears, while
one confirmed UV0 shared-source ambiguity remains. This is a classification
correction, not an improvement of the measured Shelf output UV2.

Grouped transfer quality is the same across the three symmetry settings:

| Direction / texture metric | Degenerate faces | Overlap pairs | Worst anisotropy | Area-weighted anisotropy |
|---|---:|---:|---:|---:|
| LOD0 → LOD1 / square | 0 | 0 | 4.974 | 2.215 |
| LOD0 → LOD1 / 1:2 | 0 | 0 | 3.197 | 1.631 |
| LOD1 → LOD0 / square | 0 | 22 | 8.880 | 2.220 |
| LOD1 → LOD0 / 1:2 | 1 | 8 | 4.222 | 1.630 |

The related EditMode subset passes **101/101**, without skips, on Unity
6000.2.6f2 / DX11. Both reference C# variants and the identifier check pass.
Two isolated async Full Pipeline runs with Checker keep all render meshes alive
through editor updates and complete in approximately 2.6–2.9 seconds each.
They do not emit TBN warnings, but still report a genuinely stretched target
triangle at 5.2:1. These UI auto-tune runs are separate from the frozen comparison.
Reverse quality and the real remaining stretch need further correspondence or
source-parameterization work; neither is hidden by the new diagnostics.

Local evidence is under `_results~/shelf-transfer-20261009/`: `affected.xml`,
`full-pipeline.log`, `baseline-config.json`, `fixed-frozen-config.json`, the
completed `baseline-reports/transfer_compare_20261009_015612_237_0085d91c/` and
`fixed-frozen/transfer_compare_20261009_021210_307_24d37217/`. Replay uses the
baseline `manifest.json` via the benchmark's `captures` array. The source FBX,
copies and diagnostic outputs are not committed.

### TrainCarriage: pack budget and instance source selection

The supplied stack traces identify the old `59d634d` package. Current baseline
`ca2daf0` still reproduces its pack refusal on an isolated copy of the actual
TrainCarriage FBX/importer. The body has 1428 source UV0 shells: requested 4096
with internal oversample 4 estimates 383B operations; 2048 with factor 4 estimates
95B. Both exceed the existing 20B safety budget. Factor 1 at requested 2048 packs
in approximately 0.4 seconds on this machine. These are local observations, not
timing guarantees. The budget and native source/binaries remain unchanged.

Optional internal oversampling now adapts to the highest factor within that
budget, in both single and shared packing. Requested resolution and proportional
pixel padding are retained; the selected factor is logged. Even factor 1 at 4096
exceeds the body budget, so that request still refuses explicitly. A budget
refusal is distinct from cancellation. If any included source fails required
Repack, Full Pipeline stops before Transfer and auto-tune rather than treating a
partial atlas as success. Successful source outputs remain available; the run
does not produce a successful capture/benchmark or a misleading Complete message.

The imported seat instances use names such as `ElectricWagon_Seat_LOD0.001` and
`ElectricWagon_Seat_LOD1.001`. Their group keys previously retained the LOD token,
which made transfer fall back to the first source renderer rather than the seat.
Group keys now remove LOD/collision suffixes while preserving numeric instance
suffixes, so `.001` and `.002` match their own source and stay distinct. Original
node names, FBX data and scene transforms remain unchanged. Capture from the
LODGroup includes these instances without depending on the imported-asset
benchmark's strict terminal LOD-name parser.

The original body weld preserves every triangle corner's tangent handedness:
LOD0 18158 → 17180 vertices, LOD1 14828 → 13891, LOD2 6948 → 6315; zero changed
signs and no zero tangent.w values. TBN warnings are not reproduced on the current
import. Original FBX SHA-256:
`6C631D85D48C8C7FE2537C0BD5DCBC6D45646E30FD8112E7B57A4DC6F8DCBA4D`.

Five pack/pipeline fixtures reproduced failures before the corrections. Three
numeric-key cases and two actual source-atlas transfer fixtures also failed
before the naming fix. The affected subset passes **273/273 EditMode tests**,
with zero failures/skips, on Unity 6000.2.6f2 / DX11. It includes single/shared
native packing, positive-area output scans, UV0/UV2 preservation, session release,
int.MaxValue oversample, partial/all failed pipeline paths, naming and previews.
Both reference C# variants and the identifier/dependency checks pass.

Two isolated asynchronous Full Pipeline runs at manual resolution 2048, per-mesh
packing and active Checker complete on LOD1 and LOD2 with zero missing render
meshes. The pipeline's rejected-shell total falls from 85 to 2 after matching the
seat instances correctly. Its narrow overlaps=5 summary does **not** certify a
valid atlas. All three seat variants on LOD1 now have zero degenerate faces and
zero overlap pairs, with area-weighted anisotropy approximately 1.074. LOD2 seats
still have two degenerate faces and 20–22 pairs each.

The body is already invalid **before transfer**: the captured prepared source UV2
has 32 degenerate faces, 13964 positive-area overlap pairs and area-weighted
anisotropy 15.094. A probe finds zero shared UV0-chart vertices and zero native
UV2 assignment conflicts, so this case does not establish a point-contact/native
corner-assignment fault. Source parameterization needs further investigation.
The corrected grouped targets retain the following defects:

| Body target | Degenerate faces | Overlap pairs | Area-weighted anisotropy |
|---|---:|---:|---:|
| LOD1 | 124 | 11830 | 8.508 |
| LOD2 | 19 | 4955 | 145592.152 |

The captured final inputs include the body and all three seat variants at both
target LODs. A filtered manifest retains those eight pairs and the original
checksummed mesh/detail files. Replaying all six methods, two measured
repetitions and no warmup gives **48 rows**: deterministic outputs, unchanged
inputs and matching grouped references. None gives a clean body. `shell-similarity`
reduces LOD1 average anisotropy to 2.060 but increases overlap pairs to 19110;
global nearest alternatives are worse. Five nearest-method rows exhaust their
overlap scan limits, so their counts are lower bounds. Grouped scans complete.
Reference agreement is reproducibility, not independent correspondence truth.

Local evidence: `_results~/train-transfer-20261009/` contains `affected.xml`,
`baseline-pipeline.xml`, `baseline-naming.xml`, `baseline-pack.log`,
`full-pipeline-fixed.log`, `captured-quality.json`, `source-probe.log`,
`frozen-config.json` and
`frozen-comparison/transfer_compare_20261009_025305_688_235b6a15/`.
The complete 32-pair capture is in the isolated project's
`BenchmarkReports/transfer_20261009_024949_746_e986b018/`; `train-subset.json`
selects the eight comparison pairs. Source FBX/copies and generated reports are
not committed. Authored material/texture dependencies were not copied; these are
controlled runs, not an exact replay of the supplied historical scene settings.
## Park_Bench_A: internal Mesh LODs and Packed quality limits

Baseline: `2c40343`, PR #225. Two original FBX/importer pairs from the E: project
were copied byte-for-byte into the owned Unity 6000.2.6f2 / DX11 project:

| Variant | Original asset folder | LOD0 / LOD1 / LOD2 faces | Albedo size |
|---|---|---:|---:|
| Packed | `TestLevel_Packed` | 3832 / 1498 / 230 | 4096×4096 |
| Separate | `TestLevel_Separate/Park_Bench` | 9110 / 2306 / 1498 | 2048×2048 |

The actual `Park_Bench_A.prefab` references Packed GUID
`666fbd7714e8b9145ae40e890e5d113a`. The variants are distinct inputs; a clean
Separate result does not certify the user's Packed model. Their original SHA-256
values are `CCCDA3A61BDE738CB37E9B6B4573E692D90AF4FE2A42C14D4F6DFB19ECD82501`
(Packed) and `86017128553EF770C6A104932E70FF63D74E0D300E91286D21DC5B77374F1E6C`
(Separate).

Separate exposed a mesh-copy bug before transfer: `MeshData.GetSubMesh` includes
the backing index ranges of internal Mesh LODs, but `MeshData.GetIndices` writes
only the active range. Allocating with descriptor.indexCount copied uninitialized
tail entries into working meshes, emitted SetIndices errors and crashed native
UV packing. For its three render meshes the untouched sentinel tails contain
9231, 2553 and 1533 indices respectively. Allocating with `Mesh.GetIndexCount`
fixes both ordinary and raw-UV copies. Two unreadable UInt16/UInt32 fixtures fail
before this change and pass afterward; no native binaries or importer settings
are edited.

The affected subset passes **125/125 EditMode tests**, without failures/skips;
both reference C# variants and identifier/dependency guards pass. Two asynchronous
Full Pipeline runs per variant at manual 512, per-mesh packing and active Checker
cover LOD1/2 and repeated repack/transfer. Missing renderer meshes remain zero
through Editor updates and preview mode changes. These probes also capture the
actual pipeline inputs independently of the controlled preparation below.

Controlled imported preparation uses weld on, vertex pre-optimization off,
manual 512 and square texture metric, with symmetry off/adaptive and ARAP off/on:
eight settings pairs / sixteen target cases. Separate's eight target cases have
zero degenerate faces and zero positive-area overlaps. Packed remains invalid:

| Packed with ARAP | Degenerate faces | Overlap pairs | Area-weighted anisotropy |
|---|---:|---:|---:|
| Prepared source | 0 | 0 | 1.098 |
| Grouped LOD1 | 0 | 4 | 1.104 |
| Grouped LOD2 | 5 | 52 | 3238.508 |

ARAP repairs the source's two collapsed triangles, but target correspondence and
projection still fail. Frozen replay of four Packed inputs with all six methods
is complete (24 rows, two repeats); none yields a clean LOD2. Global UV0/surface
nearest methods introduce thousands of intersections. Old bbox-based CollapseDiag
messages are not the quality measurement: independent triangle anisotropy and
positive-area overlap scans establish these remaining defects.

Three matching experiments were rejected before publication. A nearest-face
normal gate fixes an analytic curved-fragment source override, but changes Packed
LOD2 from 5 degenerates / 52 pairs to 4 / 55 and increases summed pair area from
0.005352 to 0.025476. Adding UV0 coverage does not prevent the regression.
Restricting merged rescore by source surface distance produces 9 degenerates /
45 pairs, still with area 0.025293. Those source/test changes were removed; no
known-worse matching variant is included in this patch.

Local evidence lives in `_results~/park-bench-20261009/`: `project-before.json`,
the sentinel and actual-count mesh probes, baseline/final EditMode XML, prepared
and frozen benchmark configs/reports, `full-pipeline.log`, and rejected patches.
Final preparation is `prepared-publish/transfer_compare_20261009_034610_326_3b71afba`;
all eight Packed UV2/mapping hashes match baseline. Final frozen replay is
`frozen-publish/transfer_compare_20261009_034732_618_e1bf775e`: 40 Park/Modern/Shelf/
Train pairs × six methods = **240 rows**, two repeats, deterministic outputs,
unchanged inputs, no row errors, and all 40 grouped references matching. All
24 Park outputs/mappings match baseline. Five Train nearest-method overlap scans
are incomplete; their counts are lower bounds. Every Park scan is complete.
The owned project's `BenchmarkReports/transfer_20261009_034547_988_a47f1f2d` and
`transfer_20261009_034549_921_0b8b001b` contain the full-pipeline Packed/Separate
captures. Frozen results are regression baselines, not independent correspondence
truth. Exact failing UI material/auto-tune settings remain outside the controlled
preparation; the working scene, original FBX and importer metadata are preserved.

## Tire_C: tessellation-dependent normal override

Baseline `ebe5fcd`, PR #225. The actual Tire_C prefab references the Packed FBX,
GUID `f23fd07c97f149d4bac9b04b087dfd44`; the Separate FBX has GUID
`db234b86ec39c1a47b90d71ae761789d`. Both contain 816/560/144 faces at LOD0/1/2.
Packed uses the square Courtyard atlas; Separate's Tire albedo is 2048×2048.
The original FBX/importer pairs were copied without editing into the owned
Unity 6000.2.6f2 / DX11 project. Preparation uses manual 512, square metric,
weld on and pre-optimization off, with symmetry off/adaptive and ARAP off/on:
16 target cases. Their source UV2 atlases have zero degenerates and overlaps.

The chart matcher counts each triangle's normalized face normal equally.
Uneven subdivision therefore changes its direction even when the represented
surface stays the same. Packed LOD1's outer chart matches the correct source at
squared sampled distance approximately 5.5e-13, but an opposed average-normal
override replaces it with the inner chart at 0.00552. Its source/target
unweighted Z sums are -13.86/+16.62; geometric area-weighted sums are
-0.3767/-0.3586. This is tessellation bias, not a genuinely reversed surface.

Globally replacing every average normal is rejected: it degrades Kitchen and
Train in the comparison corpus. The final correction retains existing scoring
and merged rescore, protecting only an almost exact sampled surface match
(distance squared <= model diagonal squared × 1e-10) with agreeing area-weighted
normals (dot >= 0.3). Closed/cancelled charts have no invented weighted normal.
The opposite side of a thin sheet still fails the orientation check. Four
analytic source/target subdivision fixtures first select a remote wall; the
final code passes them and **129/129 related EditMode tests**, zero skips.
Both reference C# variants and identifier/dependency guards pass.

Correcting correspondence alone leaves 198 overlap pairs in normal-filtered UV0
interpolation. For a protected match, the existing transform wins an equal
face-issue tie only if its bounded positive-area overlap scan certifies it and
interpolation fails the same check. A bounded scan that cannot complete does
not certify the transform. Other matches retain the previous selection rule.

| Controlled grouped target | Before: degenerates / overlaps | After | Weighted anisotropy after |
|---|---:|---:|---:|
| Packed LOD1, all four settings | 87 / 8727 | 0 / 0 | 1.283 |
| Packed LOD2 | 0 / 14 | 0 / 14 | 2.485 |
| Separate LOD1 | 0 / 0 | 0 / 0 | 1.288 |
| Separate LOD2 | 0 / 14 | 0 / 14 | 2.430 |

Final frozen comparison covers 60 captured pairs (Park/Modern/Shelf/Train/Tire,
including four Tire Full Pipeline pairs) and seven analytic controls, six methods,
two repeats: **402 rows**, no row errors, deterministic outputs and unchanged
inputs. All Park/Modern/Shelf outputs/mappings match baseline. Train grouped
LOD1 improves from 124 degenerates / 11830 overlaps to 66 / 11797; grouped LOD2
is unchanged. The no-hints Train LOD2 baseline changes from 129 / 4319 to
120 / 4327; its summed pair area increases slightly, from 0.009691 to 0.009693.
Five Train nearest scans remain incomplete (lower bounds). All Tire scans
complete; five positive analytic grouped controls retain independent truth.

After reviewing the accepted dedup reassignment's protection flag, all 67 pairs
were repeated with grouped and grouped-no-hints: **134 rows**, zero errors,
deterministic outputs and unchanged inputs. Every UV2/mapping hash and quality
counter matches the corresponding row of the six-method comparison.

Two asynchronous Full Pipeline runs per variant with Checker at LOD1 and LOD2
finish without missing renderer meshes through updates or preview mode changes.
Their captures confirm clean LOD1 and the remaining fourteen LOD2 overlap pairs.
The specific reported `src3` claimed by `t3/t4` warning was not reproduced in
these controlled/full-pipeline settings. The diagnostic and its genuine UV0
ambiguity constraints remain active; zero LOD1 overlaps is not a guarantee for
unknown UI settings or an independently transformed scene instance.

Local evidence: `_results~/tire-transfer-20261009/project-before.json`,
`normal-baseline.xml`, `final-tests.xml`, normal probes and rejected global
normal patch; native preparation `prepared-baseline/transfer_compare_20261009_035641_963_47453fe7`;
final replay `frozen-publish/transfer_compare_20261009_041606_890_81347b6d`;
final grouped revalidation `frozen-final/transfer_compare_20261009_042720_366_01baf460`
and `final-revalidation.json`; `full-final.log` and `full-final-summary.json`.
Final full-pipeline captures in the owned project's BenchmarkReports are
`transfer_20261009_042932_982_f0127e7c` (Packed) and
`transfer_20261009_042933_717_3d1b742e` (Separate). Originals and importer settings
are preserved; captured outputs are regression baselines, not correspondence truth.

## Bench_Metal_A: shared boundaries and cross-LOD hints

Baseline `927eff1`, PR #225. The actual prefab references FBX GUID
`7896c81e71a3ef74f804c0e590cb339e`, with 1668/1268/272 faces at LOD0/1/2.
The original FBX and importer metadata were copied into the isolated Unity
6000.2.6f2 / DX11 project. Controlled preparation uses manual 512, a square
source metric, weld on and pre-optimization off: symmetry off/legacy/adaptive
times ARAP off/on, twelve target cases. Every prepared source UV2 has zero
degenerate faces and zero positive-area overlap pairs.

Vertex-only chart matching cannot distinguish a filled surface from a coplanar
frame sharing its boundary. Three large LOD1 charts matched the wrong frames
at squared vertex distance approximately 1e-12, while all their UV2 triangle
area lay outside the selected source's triangles. Source bounds hid this hole.
The matcher and dedup retries now sample at most 32 target face centroids per
candidate and use the maximum of vertex and interior distances before the
existing normal penalty. This retains the bounded candidate set and merged
rescore rules. Captures record `faceInteriorDistanceSquared` separately from
vertex distance; the new optional field does not rename earlier diagnostics.

Cross-LOD hints previously bypassed geometric matching. On frozen Full Pipeline
inputs, LOD2 with hints had 30 degenerates / 111 overlaps; disabling hints gave
0 / 6. Rejecting hints on 3D distance alone improved Bench but increased Train
LOD2 degenerates from 19 to 47. Adding a relative UV0 comparison still left 25.
Both variants were rejected. The final guard requires a geometric alternative
with sampled UV0 interiors within squared distance 1e-8, a hint outside that
UV0 tolerance, and more than twice the geometric interior distance. A feature
whose geometry moved during decimation can therefore retain its surviving UV0
hint. Hint candidates record `uv0InteriorDistanceSquared` and the trace names
the rejection reason. These samples are evidence, not full triangle coverage.

| Bench grouped target | Degenerate faces before → after | Positive-area overlap pairs before → after |
|---|---:|---:|
| Controlled LOD1, all six settings | 8 → 2 | 418 → 7 |
| Controlled LOD2, all six settings | 0 → 0 | 119 → 6 |
| Frozen Full Pipeline LOD1, after interior correction | 1 → 1 | 8 → 8 |
| Frozen Full Pipeline LOD2, hint guard | 30 → 0 | 111 → 13 |

The remaining six controlled LOD2 intersections are folds within two charts.
The final production hint path has thirteen pairs, summed area 2.7451e-6 in
normalized UV space; disabling hints reduces pairs but loses feature evidence.
LOD1 still has a degenerate face in Full Pipeline. `POST-DEDUP DUPLICATE`
messages remain active and were reproduced in the full probe: they report
source claims, not a count of intersecting UV2 triangle pairs. This correction
does not certify Bench as a clean lightmap atlas.

Three analytic shared-boundary fixtures at scales 0.001/1/1000 failed before
the correction. The same three fixtures with a misleading previous-LOD hint
and a displaced-feature fixture cover rejection and preservation respectively.
The final affected suite passes **136/136 EditMode tests**, no skips; both
reference C# define variants and identifier/dependency checks pass.

Two asynchronous Full Pipeline runs with Checker at LOD1/2 completed in
712/595 ms in the owned project. The probe checked 9839 Editor updates and
Off/Checker transitions, with zero missing renderer meshes. A repeated
TransferAll capture confirms LOD1 1/8 and LOD2 0/13. Other workflow defaults
were retained, with target symmetry splitting enabled and manual atlas 512;
this is not the original scene's exact material/auto-tune capture.

Existing corpus changes include Countertop degenerates 14→3 without weld and
15→4 with weld, and clean Shelf reverse cases. Train LOD1 trades four fewer
degenerates for three more overlap pairs: 66/11797 → 62/11800, pair area
0.0030578235 → 0.0030581745 (about +0.0115%). Its source was already invalid
with 32 degenerates / 13964 pairs. Train grouped LOD2 retains 19 degenerates
and improves 4955→4953 pairs. Source defects, local stretch and remaining
target folds still require work.

Final frozen comparison: 74 captured pairs and seven analytic controls across
six methods, two repetitions, **486 rows**, zero row errors, deterministic
outputs and unchanged inputs. Five Train nearest-method overlap scans remain
incomplete lower bounds; every Bench scan completes. All five positive analytic
grouped controls match independent truth. Both negative controls still fail it.
Compared with the interior-only run, the final hint guard changes only Train
grouped LOD2 and Bench Full Pipeline LOD2; source/target hashes are identical.
Timing includes the asynchronous benchmark/Editor schedule: final Full Bench
LOD1/2 medians are 149/74 ms, Train grouped LOD2 approximately 1102 ms versus
902 ms before hint validation. Added queries have a cost; this is a quality fix.

Local evidence under `_results~/bench-metal-20261009/`: `project-before.json`,
`interior-baseline.xml`, `final-tests.xml`, `compile-publish/`,
`prepared-baseline/transfer_compare_20261009_043926_595_119364b8`,
`corpus-interior-all/`, rejected `corpus-hint-guard/` and `corpus-hint-uv0/`,
accepted `corpus-hint-contained/`, and `full-publish.log` / `full-publish-summary.json`.
Final comparison and counters are in `frozen-publish/`, `final-summary.json`
and `final-revalidation.json`.
The final owned-project capture is
`BenchmarkReports/transfer_20261009_052514_533_96307819/manifest.json`.
FBX, importer metadata, prefab and authored material are preserved.
