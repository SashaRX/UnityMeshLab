# Transfer performance replay

The opt-in paired model test records a `performance.samples` list in each
mode of `comparison.json`. Timings use `Stopwatch` wall time, including awaited
worker execution. Nested stages overlap and must not be summed. `count` records
the number of constructions/queries for counters, or one completed timed scope.
`Reverse total` covers correspondence and final packing; `Reverse workflow total`
also includes detached input cleanup and final layout validation. Export and disk
I/O are outside these timers.

## Private model corpus

Use a closed, isolated Unity test project with a `file:` reference to this package
and `com.sasharx.unitymeshlab` in the manifest's `testables`. Existing private
fixture paths are:

- `Assets/JunctionFixtures/Cafe_Table_A_01.fbx`
- `Assets/TransferBenchmarkInputs/Modern_Kitchen_A.fbx`
- `Assets/KamazFixtures/kamaz_Off.fbx`

Set `MESHLAB_PAIRED_TRANSFER_OUTPUT` to a fresh output directory and run the
EditMode filter
`SashaRX.UnityMeshLab.Tests.PreviewLodSwitchTests.FrozenForwardReverseComparison.*32`.
Exactly three cases must pass; a zero-test or skipped run is not a benchmark.
Each case runs ordinary Transfer, then both reverse overlap policies on detached
copies of the same forward result. Inputs and original FBX mesh streams are
checked for changes. Layouts, quality reports and ancestry are exported alongside
the timing samples. Results at 10 tex/m are available with a `.*10` suffix.

`Tools~/run-transfer-performance.ps1` accepts `UnityEditor`, `ProjectPath`,
`OutputRoot`, `Repetitions` (default three) and `Density` (10 or 32). It uses a
fresh subdirectory per run and requires all three cases to actually pass. Use
`-DisableFootprintCache` for the control, and another fresh output root for the
cached variant. The runner restores its environment variables and refuses an
already open project. It never overwrites the source FBXs.

Use `-DisableFootprintSearchIndex` to retain the original fallback occupancy
traversal while keeping native footprint caching enabled. Compare this control
with a fresh default run to isolate the placement search optimization.

Compare multiple runs on the same editor, machine, fixture import settings,
texel density, padding and atlas-fill settings. Separate the ordinary pipeline
and detached forward preparation from the reverse solve; the benchmark
intentionally measures both preparations to verify their equality.

## First optimization: immutable packing footprints

The final pack prepares proxy footprints for whole cross-LOD families repeatedly
when trying densities, placement rotations and precision retries. Cache these
immutable footprints by source array identity and geometry policy within one
final pack. Keep all density trials, native placements, Float32 stabilization and
contact/metric audits. Cache entries expire after that pack; source mesh/UV
changes cannot reuse them in a later operation. Retention is bounded to one
million source/proxy corners, including source arrays held by the keys.

The diagnostic `cachePackingFootprints` option is internal. Runtime use defaults
to caching; tests also execute the uncached path and compare serialized reports
and complete output mesh streams. GPU projection is a separate future experiment.
Existing GPU BVH can supply candidate queries, but donor ambiguity, chart/layer
ownership and packing audits still require equivalent semantics.

Run `TransferPerformanceTests`, `ReverseUvFrozenCascadeTests`,
`ReverseUvFamilyPackingTests`, `ReverseUvFootprintPackingTests`,
`ReverseUvSeamTests`, `ReverseUvTransferTests` and `UvJunctionCutsTests` for
regression verification. Run both FBX define variants of
`Tools~/compile_check.py` as well.

Local private artifacts for the 2026-10-13 experiment are under
`_results~/transfer-performance-20261013/`; they are intentionally not shipped.

## Measured first pass

32 tex/m, 2px padding, Fill atlas enabled, same isolated Unity 6000.2.6f2 project.
These times cover reverse correspondence and final packing; ordinary Transfer
preparation and export are separate. Two uncached controls and one cached run
were completed. The table uses the second control with matching logging settings;
these are individual measurements rather than a statistical speed guarantee.

| Model | Policy | Uncached (s) | Cached (s) |
|---|---|---:|---:|
| Cafe_Table_A_01 | no overlap | 1.98 | 1.45 |
| Cafe_Table_A_01 | overlap | 15.46 | 15.11 |
| Modern_Kitchen_A | no overlap | 14.49 | 6.63 |
| Modern_Kitchen_A | overlap | 21.50 | 18.66 |
| Kamaz Typhoon (`kamaz_Off`) | no overlap | 38.28 | 17.21 |
| Kamaz Typhoon (`kamaz_Off`) | overlap | 100.46 | 86.08 |

Both controls and the cached variant produce identical SHA256 hashes for all
96 exported layout/report files. Geometry, UVs, atlas dimensions, ancestry,
packing family IDs and input snapshots are unchanged. The graphics-enabled
regression run passes 258 cases, has zero failures and skips 17 optional private
fixtures. The final focused follow-up passes all 24 cases, including the new
invalid-input timing guard.
Both FBX reference compilation variants and the identifier guard pass.

The largest remaining overlap cost is the fallback triangle-footprint placement
search: the Kamaz row retains 129,267,798 tightening and 837,606,125 repacking
work units and 48,906 validations. Caching retains that search exactly. Profile
its candidate generation and exact validation before the next optimization;
GPU donor queries alone do not address this cost.

## Fallback search profiling

`Footprint candidate search` accumulates complete placement searches, including
their audits. `Footprint placement audits` measures corner reconstruction,
canvas bounds and the existing exact validation callback within those searches.
Subtract the audit time to approximate occupancy lookup/search cost. Raster mask
construction, tightening and complete packing orders have separate wall times;
these nested samples must not be summed together.

The extra timing records are aggregated per fallback invocation. No timer or
sample is allocated for each occupancy span. Private profiling artifacts are in
`_results~/transfer-performance-20261013/footprint-profile/`.

## Second optimization: compiled occupancy spans

Compile each immutable raster mask into an array of nonempty row spans, keeping
their original row/span order. Each placement search then scans that array
instead of traversing every row and repeatedly constructing list enumerators.
Per-row lookup cursors reuse the occupied-interval lower bound as x advances.
A new canvas row or a backwards query resets the lower bound. No cursor survives
one search; no compiled mask survives its fallback invocation.

The internal `indexFootprintSearch` switch defaults to enabled. Its disabled path
retains the reference row traversal and binary search for regression/control
runs. The optimization visits the same occupied spans, chooses the same next x,
spends the same work budget and invokes every placement audit in the same order.
It does not change placement phases, retention, rotations, scale, padding,
precision repair or atlas audits.

Regression coverage compares complete candidate traces, refusals, retained
families, search/validation counters and final coordinates with and without
rotation. Ring rows exercise backwards queries across multiple spans; 260-family
cases exercise the bottom-left policy for larger orders. Cancellation tests
verify that profiling scopes close on failure in both implementations.

## Measured fallback search continuation

The same 32 tex/m, 2px padding, Fill atlas corpus uses native footprint caching
in both variants. One compiled-search run and two reference controls were
completed; the table uses the second control on the final source with
`-DisableFootprintSearchIndex`. Timings are individual measurements, not a
statistical guarantee. Ordinary Transfer preparation and export are excluded.

| Model | Policy | Reference reverse (s) | Compiled reverse (s) | Reference search (s) | Compiled search (s) |
|---|---|---:|---:|---:|---:|
| Cafe_Table_A_01 | no overlap | 1.79 | 1.70 | 0 | 0 |
| Cafe_Table_A_01 | overlap | 17.31 | 10.24 | 11.14 | 4.25 |
| Modern_Kitchen_A | no overlap | 7.95 | 8.11 | 0 | 0 |
| Modern_Kitchen_A | overlap | 21.92 | 13.50 | 16.80 | 8.39 |
| Kamaz Typhoon (`kamaz_Off`) | no overlap | 20.93 | 20.23 | 0 | 0 |
| Kamaz Typhoon (`kamaz_Off`) | overlap | 102.76 | 65.46 | 57.83 | 21.71 |

The occupancy search is 2.0–2.7 times faster; the complete reverse overlap solve
is 1.6–1.7 times faster in this control comparison. Exclusive mode does not use
the modified fallback path and has only run-to-run timing variation.

All 96 exported geometry/UV/ancestry report files match the original cached
baseline byte for byte in the profiling control, cursor-only prototype,
compiled search and final reference control. Input hashes/frames, every search
and placement-audit count, work budgets, retained families and completed orders
also match. The cursor-only prototype did not consistently speed up the smaller
models; compilation of nonempty spans supplies the main improvement there.

The compiled graphics-enabled regression run passes 265 cases, has zero failures
and skips 17 optional private fixtures. The final reference control executes and
passes all three model cases. Local artifacts and exact comparisons are in
`_results~/transfer-performance-20261013/search-summary.json` and
`search-summary.md`. All 46 final focused tests pass on the final source,
including the guard that avoids allocating lookup arrays for masks larger than
the canvas. Both FBX reference compilation variants, the identifier guard and
Editor/test metadata checks pass.

The remaining Kamaz overlap cost includes 24.5s of native chart placement and
21.7s of fallback candidate search. Measured donor projection remains a small
slice of the reverse solve. This optimization uses CPU occupancy indexing;
a GPU query backend remains separate work.

## Third optimization: native bit-image operations

Xatlas retains its rasterizer, chart ordering, random state, placement candidates,
scoring, rotations and tie breaks. Only two integer mask operations change:

- Overlap lookup visits source words instead of repeatedly locating partial
  source/destination fragments. Aligned offsets have a separate path; zero source
  words skip destination reads. Clipped reads and unmasked storage-tail semantics
  match the original predicate. The first fragment can reject a collision before
  row-loop setup, retaining the cheap path for small charts. MSVC inlines that
  predicate as it does the existing xatlas word-buffer accessors.
- Each padding pass combines three rows of 64-bit words with horizontal shifts
  and word-boundary carries. It computes the same eight-neighbour dilation.
  Source tails are masked before propagation and every output word is written;
  pixels outside the image width remain clear. The padding radius is unchanged.

The native differential test compares 1,721,292 overlap queries with the original
traversal and 4,800 repeated dilation passes with a pixel reference, including
sparse/empty masks, every bit alignment, clipped bounds, dirty storage tails,
single rows and zero dimensions. All four native regression tests pass.

### Replaying real packing requests

`run-transfer-performance.ps1 -CaptureNativePacking` records every native trial
on the three-model corpus, including the candidates later rejected by managed
canvas/precision audits. Each private `.xatlaspack` contains the exact input
buffers/options and expected atlas dimensions, chart IDs, references and Float32
UV bits. The current corpus contains 74 requests across both overlap policies.

`meshlab-packing-replay` is built when `MESHLAB_BUILD_TESTS=ON`. It compares all
native output bytes with those captures and reports placement time separately
from chart discovery, process startup, file I/O and managed UV audits.
`Tools~/compare-native-packing.py` repeats reference/optimized executables in
alternating order, checks the same six corpus cases and unchanged capture hashes,
and reports medians for each policy.

For the final Unity check, `-NativePackingReplayExecutable <optimized-executable>`
also returns the optimized native output to the managed packing pipeline. The
child verifies it against the live reference result before returning it. Family
transforms, precision phases and every final UV audit then use the optimized
coordinates. This verification run executes both packers and includes process
and capture overhead, so its complete reverse time is not a performance result.

These controls are explicit private benchmark options. Production runs retain
the normal P/Invoke path and perform no capture/replay I/O. Local native builds
use `MESHLAB_COPY_PLUGINS=OFF`; package DLL/SO/dylib publication remains the
responsibility of `build-native.yml`. No package binary is overwritten locally.

### Native placement measurements

Windows/MSVC Release, the same frozen 32 tex/m, 2px padding, Fill atlas corpus.
Both variants retain the previous managed footprint cache and indexed occupancy
search. The table shows the median of three runs, summing every native placement
trial for each case; chart discovery, managed projection/search/audits and export
are excluded. Each repetition replays all 74 exact native requests and compares
every output bit. Reference/optimized run order alternates.

| Model | Policy | Original native (s) | Optimized native (s) | Speedup |
|---|---|---:|---:|---:|
| Cafe_Table_A_01 | no overlap | 1.448 | 1.030 | 1.41x |
| Cafe_Table_A_01 | overlap | 6.559 | 2.214 | 2.96x |
| Modern_Kitchen_A | no overlap | 2.790 | 2.536 | 1.10x |
| Modern_Kitchen_A | overlap | 1.632 | 1.171 | 1.39x |
| Kamaz Typhoon (`kamaz_Off`) | no overlap | 7.661 | 6.505 | 1.18x |
| Kamaz Typhoon (`kamaz_Off`) | overlap | 32.922 | 20.387 | 1.61x |

The overlap-only word traversal prototype added small overhead to the short
exclusive charts. A cheap rejection of the original first fragment and MSVC
inlining remove that observed regression in the final measurements. The final
version includes exactly equivalent padding dilation. All native output bytes
match in all three repetitions. AddressSanitizer also passes the complete
overlap/dilation differential tests without memory errors.

Local results: `_results~/transfer-performance-20261013/native-final-timings/`.
The final graphics-enabled Unity run uses the optimized replay coordinates in
the complete managed solve: 265 tests pass, zero fail, and 17 optional private
fixtures are skipped. All three required model cases execute and pass, including
both overlap policies. All 120 exported geometry/UV/ancestry/quality files match
the cached baseline byte for byte in the profiling, capture and optimized replay
runs. Input hashes/frames, native call counts, fallback candidate/audit counts,
work budgets, retained families and completed packing orders also match.

The source and both FBX reference compilation variants pass, the identifier guard
reports zero findings, and Editor/test metadata checks pass. The complete local
verification report is `_results~/transfer-performance-20261013/native-summary.json`.
No change has been committed, pushed or installed into the user's Unity project;
the native package binaries still require publication through native CI.

## Fourth optimization: bounded collision-row index

Repeated placement queries on one immutable occupancy window reuse the earliest
colliding mask span and its earliest occupied interval for each integer x. The
index stores the original next-x jump and the number of logical span checks.
It keeps half-open interval semantics, span priority and all candidate/audit
ordering. No geometry, UV phase, rotation or padding changes.

Preparation starts only after eight queries and 2,048 logical checks on that
row. It indexes at most 4,096 mask spans and spends at most 4,096 preparation
effort units. A union/find successor table skips positions already labelled by
an earlier span or interval. A partially indexed final span is retained for
known hits; unassigned x positions resume at the fully completed prefix.
Preparation does not spend logical search work. Cached hits/misses charge
exactly the reference checks, stopping immediately at the first over-budget
span. The existing 128-million work and audit limits remain unchanged.

Three integer buffers are bounded by one candidate row (at most an 8,192-cell
canvas), reused across y within one Find and discarded when Find returns. The
cache never survives an occupancy change or another mask/rotation search.
`cacheCollisionRows` defaults to enabled; the benchmark's
`-DisableCollisionRowCache` keeps the previous compiled-span/cursor traversal
for controls. `Footprint row index construction` measures preparation inside
the existing complete candidate-search timing; these nested samples overlap.

Differential tests compare early/late conflict priority, overlapping forbidden
intervals, partial construction at several effort limits, domain changes and
cache reuse, cancellation, and exact logical work at/above the original cap.
Complete candidate traces, refusals, retained families, validation counts and
coordinates still match the original traversal with and without chart rotation.

### Collision index measurements and limits

One full indexed run and one disabled-cache control were executed on the final
source at 32 tex/m, 2px padding, Fill atlas. Both use the same package native DLL;
the preceding native optimization's timings remain a separate comparison.
Ordinary Transfer preparation and export are excluded from the reverse solve.
These are individual measurements, not a statistical speed guarantee.

| Model | Policy | Control reverse (s) | Indexed reverse (s) | Control search (s) | Indexed search (s) |
|---|---|---:|---:|---:|---:|
| Cafe_Table_A_01 | no overlap | 1.54 | 1.49 | 0 | 0 |
| Cafe_Table_A_01 | overlap | 9.28 | 9.23 | 3.82 | 3.78 |
| Modern_Kitchen_A | no overlap | 6.70 | 6.72 | 0 | 0 |
| Modern_Kitchen_A | overlap | 12.33 | 12.12 | 7.90 | 7.75 |
| Kamaz Typhoon (`kamaz_Off`) | no overlap | 17.52 | 17.40 | 0 | 0 |
| Kamaz Typhoon (`kamaz_Off`) | overlap | 58.61 | 57.35 | 20.86 | 20.11 |

Exclusive mode does not invoke this fallback. The improvement on the measured
overlap corpus is modest. Kamaz builds 51,596 local row indexes in 1.95s, already
included in its candidate-search time; placement audits take 1.26s. Occupancy
lookup remains the larger cost, so this continuation does not establish that
the reverse search bottleneck has been eliminated.

All 120 exports have exactly equal binary numeric values in the short-prefix
prototype, final indexed run and disabled-cache control. The two indexed runs
also match the previous cached baseline JSON byte for byte. Sixteen control
JSON files differ only in their last printed decimal digit: parsing yields the
same Float64 bits, and all geometry/UV Float32 bits match. The comparison uses
no tolerance. Input hashes/frames, native call counts, candidate/audit counts,
work budgets, refused orders, retained families and completed orders also match.

The final graphics-enabled regression run passes 275 tests, fails zero, and
skips 17 optional private fixtures; all three required model cases pass both
overlap policies. The disabled-cache control passes all three model cases.
Both FBX reference compilation variants, identifier guard, runner syntax and
Editor/test metadata checks pass. Local verification is in
`_results~/transfer-performance-20261013/collision-summary.json`.

## Fifth continuation: native candidate traversal — 2026-10-14

`meshLabFootprintSearch*` owns an immutable integer snapshot per Find call,
independent of xatlas's global atlas. The snapshot contains the original ordered
mask spans and merged occupancy intervals. Native code advances across occupied
positions until the next free candidate; the existing managed code still builds
Float32 UVs and performs the same gutter, metric and contact audits, in the same
phase order. Rotations, density trials, extent scoring and packing orders are
unchanged. The internal managed control remains selectable.

The native traversal preserves logical span work exactly, including the first
over-budget increment. Calls yield every 65,536 checked spans, resuming inside
long queries without publishing a partial candidate. Cancellation is checked
before and after each chunk. The managed owner destroys its native snapshot on
success, refusal, cancellation and exceptions. The snapshot owns its copied
buffers; no managed pointer outlives the creation call. Version 1 is optional:
older native plugins keep the managed path. No static native context is retained.

The graphics-enabled regression run passes 280 tests, fails zero and skips 17
optional private fixtures. All three required models execute ordinary Transfer
and both reverse policies. Four new complete trace cases compare every audit
candidate, phase retry, work counter and packing decision with the original
managed traversal. Native ABI differential tests cover 6,000 randomized searches,
near/exhausted work budgets, empty masks, multi-span rows, chunk resumption and
invalid lengths; all five native CTest suites pass. The differential ABI harness
also passes AddressSanitizer. Original source meshes are unchanged.

All 120 geometry/UV/ancestry/quality exports match the immutable cached baseline
by exact binary numeric values, without tolerance. JSON's final printed decimal
digits may differ. Input frames and hashes, atlas sizes, candidate/audit counts,
logical work, retention decisions and refused/completed orders also match.

Private verification loads the newly built library through a test-owned Windows
handle, without replacing package Plugins. Only the candidate-search API comes
from that private library; native chart packing still uses the installed control
plugin. Thus whole-solve timings here measure this search continuation alone.
Publishing package binaries still requires `build-native.yml`.

Reproduce after configuring the closed private corpus project:

```powershell
# Both arms: ordinary Transfer and reverse at 32 tex/m, 2 px padding, Fill atlas.
& 'Tools~/run-transfer-performance.ps1' -UnityEditor $editor -ProjectPath $project `
    -OutputRoot $control -Repetitions 3 -DisableNativeFootprintSearch
& 'Tools~/run-transfer-performance.ps1' -UnityEditor $editor -ProjectPath $project `
    -OutputRoot $candidate -Repetitions 3 -NativeFootprintLibrary $privateDll
# Compare every complete models directory; the verifier requires all three models.
python 'Tools~/compare-transfer-performance.py' --baseline $baselineModels `
    --control $controlModels1 $controlModels2 $controlModels3 `
    --candidate $candidateModels1 $candidateModels2 $candidateModels3 --output $summary
```

Artifacts: `_results~/transfer-performance-20261013/native-search-models/` and
`native-search-initial-summary.json`; repeated timing results are recorded
separately so regression-suite timings are not treated as repeated measurements.

Final medians (three controls and three candidate runs, including the initial
graphics regression corpus run; Windows/MSVC, 32 tex/m, 2 px padding, Fill atlas):

| Model | Policy | Reverse control (s) | Reverse native search (s) | Search control (s) | Search native (s) |
|---|---|---:|---:|---:|---:|
| Cafe_Table_A_01 | no overlap | 1.47 | 1.49 | 0 | 0 |
| Cafe_Table_A_01 | overlap | 9.17 | 6.60 | 3.74 | 1.15 |
| Modern_Kitchen_A | no overlap | 6.75 | 6.70 | 0 | 0 |
| Modern_Kitchen_A | overlap | 12.01 | 8.32 | 7.66 | 3.87 |
| Kamaz Typhoon (`kamaz_Off`) | no overlap | 17.66 | 17.96 | 0 | 0 |
| Kamaz Typhoon (`kamaz_Off`) | overlap | 57.55 | 46.50 | 20.15 | 7.99 |

Each solve measurement includes native snapshot construction, ownership and
marshalling, all managed placement audits and the rest of the reverse solve.
Forward preparation and export are excluded as in the earlier tables. The
exclusive path does not use this fallback; its small timing variation is not a
speed improvement. On Kamaz overlap the candidate-search median falls by 60%,
and whole reverse solve falls by 19%. Snapshot preparation still costs about
3s and remains an opportunity for a later continuation.

All six runs retain the exact 120 exported binary numeric values, input hashes
and frames, atlas dimensions, native pack counts, placement counts, logical
work and complete packing refusal/retention/order decisions. Five repeated
benchmark runs pass their three required cases each; the initial regression
run passes 280 and skips 17 optional private fixtures. The rebuilt library also
replays all 74 real xatlas requests byte for byte. No package Plugins were
modified. Full evidence: `native-search-summary.json`, `native-search-control/`,
`native-search-repeated/` and `native-search-native-replay.csv` under the same
private results root.

## Sixth continuation: combined native build, shared rows and Burst — 2026-10-14

The private benchmark now binds the complete `XatlasNative` API to one rebuilt
library using scoped test delegates. Ordinary Repack and all reverse packing
trials use the same implementation as occupancy search. The default public
method signatures remain unchanged; normal calls still use the installed
plugin. A native session must be idle to change bindings, and nested scopes
restore their previous table before the private library is unloaded. Package
Plugins are not modified by local verification.

Footprint search ABI 2 preserves the legacy create/advance/destroy exports and
adds immutable occupancy snapshots. One snapshot serves all rotations of one
family. Each search still owns its mask spans, cursors and chunk-resumption
state. The native snapshot uses shared ownership so an active search can outlive
the snapshot owner. Packing constructs a new owner after rows change; it never
reuses old occupancy for the next placed/removed family. Cancellation and early
refusals release every context and owner. Plugins without the new exports retain
the equivalent older path.

The optional `SashaRX.UnityMeshLab.Burst.Editor` assembly references core Editor,
Burst >= 1.8 and Collections >= 2.1; core Editor has no mandatory dependency on
either package. Its parallel job computes the same double-precision scan-line
intersections in FloatMode.Strict, then the managed code merges output spans in
the original triangle/row order. Local point transforms, rounding, dilation
radius and final UV placement are unchanged. Masks below 32 triangles and
temporary buffers above two million spans retain scalar rasterization.

Unity's job scheduling is only supported from the editor main thread, and async
final packing runs on a Task worker. PR review first kept that worker scalar; the
eighth continuation instead runs the same kernel there through a Burst direct
call, never reaching the job API off the main thread. The timing tables below
were measured by the synchronous benchmark path and do not establish a Burst gain
for async editor actions. Regression coverage compares complete synchronous/async
Reverse outputs with both overlap policies, requires every qualifying async mask
to run compiled, and checks worker cancellation, direct-call/job span equality
and subsequent main-thread Burst execution.

The job writes a per-triangle BurstDiscard witness: managed execution is never
counted as accelerated. The benchmark enables compilation only inside its test
scope, suppressing the editor-option persistence callback while changing runtime
flags. User preference values and missing keys are checked before/after the
scope. Production respects the user's Burst setting. Small Cafe Table masks do
not qualify; Kitchen and Kamaz must record genuinely compiled triangle work.

Verification uses Unity 6000.2.6f2 with Burst 1.8.28 and Collections 2.5.7, read
from the existing project's package cache. The isolated project was also checked
without Burst/Collections; its core tests compile and pass with the optional
assembly excluded. The older cached Burst 1.8.12 could not initialize its JIT
compiler and is not used as evidence of Burst performance.

The complete graphics regression run passes 288 tests, fails zero and skips 17
optional private fixtures. All three models execute ordinary Transfer and both
reverse policies. Native differential tests compare legacy/shared searches on
6,000 randomized snapshots, including disposal before search completion; all
five CTest suites and AddressSanitizer pass. Burst spans match the scalar
reference on 48 randomized clipping/roundoff cases with real execution witnesses.
Both FBX compilation variants, identifier guard, metadata and runner syntax pass.

The initial complete run retains all 120 baseline exports by exact binary
numeric values, without tolerance. Inputs, atlas sizes, ancestry, work budgets,
placement counts and packing decisions match. Initial reverse solve times:
Cafe exclusive 1.10s / overlap 2.92s; Kitchen 6.38s / 6.57s; Kamaz 16.33s / 33.75s.
These are single-run observations; repeated timing arms are recorded separately.

Reproduction adds runner flags `-CombinedNative -NativeFootprintLibrary $dll`
and `-RequireBurstRaster`. The control uses the same library for legacy search
only, with `-DisableOccupancySnapshotReuse -DisableBurstRaster`. The separate
ablation uses combined native/shared snapshots with `-DisableBurstRaster`.
Artifacts under `_results~/transfer-performance-20261013/`:
`combined-shared-burst-final/`, `combined-initial-summary.json`,
`combined-control/`, `combined-no-burst/`, and `combined-repeated/`.

Final medians, three fresh controls versus three combined runs (the complete
graphics run and two repetitions), on an Intel Core i9-14900K:

| Model | Policy | Control (s) | Combined native + shared rows + Burst (s) | Time reduction |
|---|---|---:|---:|---:|
| Cafe_Table_A_01 | no overlap | 1.43 | 1.10 | 23% |
| Cafe_Table_A_01 | overlap | 6.47 | 2.92 | 55% |
| Modern_Kitchen_A | no overlap | 6.46 | 6.39 | 1% |
| Modern_Kitchen_A | overlap | 7.85 | 6.57 | 16% |
| Kamaz Typhoon (`kamaz_Off`) | no overlap | 17.17 | 16.24 | 5% |
| Kamaz Typhoon (`kamaz_Off`) | overlap | 44.83 | 33.92 | 24% |

These are complete reverse solve times, including snapshot creation and exact
placement audits; forward preparation and export remain outside this timer.
The control already includes the previous cached footprints, compiled spans
and native candidate search. The new combined arm also uses the accelerated
xatlas build for every native pack. Kitchen exclusive is effectively unchanged
within timing variation; this is not a uniform speedup of every operation.

Every one of the six runs matches all 120 stable baseline exports by exact
binary numeric values. Minor JSON last-digit formatting differences decode to
the same Float32 layout values or Float64 report values. Original mesh streams,
input hashes/frames, atlas dimensions, pack/search/audit counts, logical work,
retained orders and packing refusals are unchanged. All five additional
three-model runs pass without skips or failures. The combined library replays
all 74 recorded xatlas requests byte for byte. Evidence: `combined-summary.json`
and `combined-native-replay.csv`.

For Kamaz overlap, 6,579 immutable row snapshots serve 26,316 searches across
four rotations. Median snapshot construction plus search-context setup falls
from 3.30s to 0.83s. A new snapshot is still constructed after every occupancy
mutation. Burst executes 326,588 triangle rasterizations for Kamaz and 57,151
for Kitchen; Cafe has no qualifying masks. The single run with Burst disabled
retains identical UVs and measures Kamaz overlap at 37.58s, versus the combined
median of 33.92s. Cafe and Kitchen show no isolated Burst gain in that run.
This ablation confirms execution and applicability, but one run does not
establish a stable Burst-only performance estimate.

The native binary tested here is private. Package `Plugins/` are unchanged;
shipping the new native exports requires the normal `build-native.yml` CI
rebuild. No package version or mandatory Burst dependency was added.

## Seventh continuation: Intel oneTBB chart search — 2026-10-14

This continuation tests the proposed oneTBB library on the remaining native
placement work. It is an optional `MESHLAB_TBB_PACKING=ON` build, with sources
pinned to commit `9afd759b72c0c233cd5ea3c3c06b0894c9da9c54` (2021.12.0).
Normal builds default to OFF and do not gain a mandatory Intel runtime.

Only the brute-force row search is parallelized. Each row reads immutable atlas
bits in a local eight-thread task arena. Small windows and the first eight rows
stay scalar. After joining, results are reduced in original rotation/row order:
the first fitting placement wholly inside the existing canvas wins even when a
later fit is closer to the origin. Outside-canvas placements retain the original
metric, distance and stable tie rules. Atomic earliest-inside pruning discards
only rows after an already found fit. Chart commits and the random-search RNG
remain serial and unchanged. The arena does not change Unity/Burst concurrency
or a process-wide oneTBB setting. See [oneTBB parallel_for](https://uxlfoundation.github.io/oneTBB/main/tbb_userguide/parallel_for_os.html).

Six native suites pass, including 607 scalar/oneTBB placement comparisons.
Three alternating replay pairs execute all 74 captured requests; output UV
Float32 bits, references, chart IDs and atlas dimensions are identical in every
pair. Each candidate replay records 6,150 actual oneTBB batches; controls record
zero. Both FBX compilation variants, identifier and metadata checks pass.

Initial prototype native placement medians, three repetitions per arm:

| Model | Policy | Combined native control (s) | With oneTBB (s) |
|---|---|---:|---:|
| Cafe_Table_A_01 | no overlap | 0.789 | 0.557 |
| Cafe_Table_A_01 | overlap | 1.639 | 0.759 |
| Modern_Kitchen_A | no overlap | 1.630 | 1.520 |
| Modern_Kitchen_A | overlap | 0.740 | 0.714 |
| Kamaz Typhoon (`kamaz_Off`) | no overlap | 4.309 | 4.323 |
| Kamaz Typhoon (`kamaz_Off`) | overlap | 13.263 | 13.038 |

These are native replay times, not the entire reverse solve. Cafe benefits
substantially; Kitchen changes little and Kamaz has no substantial native gain.
Random placement and the managed footprint-search/audit stages are outside
this new parallel path. Evidence is in
`_results~/transfer-performance-20261014/tbb-native-timings/summary.json`.

The private Windows build keeps `tbb12.dll` beside `xatlas-unity.dll`; the test
loader uses the explicitly supplied absolute path and altered dependency search
for that load only. It does not modify process DLL directories or replace the
package's Plugins. Publishing an ON variant would additionally require
platform runtime/metadata/license integration in native CI. The current normal
build remains OFF. Burst and shared occupancy snapshots stay enabled in both
complete Unity benchmark arms so this comparison isolates oneTBB.

The first full three-control/three-candidate prototype comparison retains all
120 exported files' exact numeric bits, input hashes, atlas sizes and packing
decisions, but Kamaz overlap regresses from 34.45s to 36.39s (about 6%). The
native-only replay does not expose that complete-solve regression. The revised
search abandons in-flight rows after an earlier inside fit appears, prunes only
metrics strictly greater than an already feasible metric, and defers the row
table allocation until the scalar prefix fails. Equal metrics still run so
first-inside and stable ties are preserved. Final measurements follow separately.

One earlier full prototype trial fails in the ordinary Kamaz pipeline's
`TransferUvQuality.WorldFrame` with a NullReferenceException, before reverse UV.
Its cause is unconfirmed. The subsequent complete control and prototype suites
each pass 288 tests with zero failures and 17 optional skips, and all four model
repetitions pass. The incomplete failed trial is retained in `tbb-candidate/`
and is excluded from performance comparisons. No quality guard was removed.

The export verifier compares leaves by their original Float32/Float64 bits,
including signed zero, instead of building two additional canonical report
buffers of roughly 25MB each. Array order, field sets and value types remain
strict. Small controls cover adjacent Float32 values, Float64 changes, signed
zero, array lengths and field/type changes. An independent Node comparison also
confirms all 120 first-control exports; no numeric tolerance was introduced.

Revised native placement medians, three alternating pairs after in-flight
pruning and deferred allocation. All 74 requests still replay bit for bit;
all six CTest suites pass on this final native source:

| Model | Policy | Combined native control (s) | Revised oneTBB (s) |
|---|---|---:|---:|
| Cafe_Table_A_01 | no overlap | 0.786 | 0.360 |
| Cafe_Table_A_01 | overlap | 1.632 | 0.436 |
| Modern_Kitchen_A | no overlap | 1.648 | 1.488 |
| Modern_Kitchen_A | overlap | 0.736 | 0.690 |
| Kamaz Typhoon (`kamaz_Off`) | no overlap | 4.251 | 4.143 |
| Kamaz Typhoon (`kamaz_Off`) | overlap | 12.857 | 12.884 |

Evidence: `tbb-native-final-timings/summary.json`. The tested revised Windows
library SHA256 is
`5011675D2621ADB8C633B296C42A57160403641110E6EC9E8868A89BE4BD55DA`.
This remains a native-only measurement; complete solve times follow separately.

Final complete-solve medians, three fresh alternating control/candidate pairs,
with the revised native source, Burst, cached footprints and shared occupancy
snapshots enabled in both arms (32 tex/m, 2 px padding, Fill atlas):

| Model | Policy | Combined control (s) | Revised oneTBB (s) |
|---|---|---:|---:|
| Cafe_Table_A_01 | no overlap | 1.165 | 0.746 |
| Cafe_Table_A_01 | overlap | 2.875 | 1.698 |
| Modern_Kitchen_A | no overlap | 6.368 | 6.160 |
| Modern_Kitchen_A | overlap | 6.282 | 6.224 |
| Kamaz Typhoon (`kamaz_Off`) | no overlap | 16.370 | 16.351 |
| Kamaz Typhoon (`kamaz_Off`) | overlap | 34.173 | 33.736 |

The complete reverse timer includes correspondence, rasterization, snapshots,
native placement, fallback search and all final audits; forward preparation and
export remain excluded. Cafe gains 36% without overlap and 41% with overlap.
Kitchen gains about 3% / 1%; Kamaz is effectively unchanged within timing
variation. The initial 6% Kamaz overlap regression does not recur after pruning
and deferred allocation. In complete-solve samples its native placement median
falls from 13.174s to 12.769s, while fallback search stays 4.546s / 4.562s.

Each of the six final runs retains all 120 stable exports by exact binary
numeric values, with unchanged input hashes/frames, geometry, atlas dimensions,
ancestry, pack/search/audit counts, logical work and retained/refused packing
orders. Both full suites pass 288 tests, fail zero and skip 17 optional private
cases. Four other final repetitions pass all three required model cases with
no skips. Every case runs ordinary Transfer and both reverse overlap policies.
An independent Node leaf-bit comparison confirms all six complete export sets.
The Python verifier also passes in the bundled isolated runtime (`-I -S`);
the system Python runtime produced inconsistent comparison exceptions earlier.
No result guard or precision tolerance was relaxed to obtain a pass.

Real Burst rasterization remains 57,151 triangles for Kitchen and 326,588 for
Kamaz in each overlap run and both arms. Cafe retains scalar small-mask handling.
Final evidence: `tbb-final-control/`, `tbb-final-candidate/`,
`tbb-final-summary.json` and `tbb-native-final-timings/` under
`_results~/transfer-performance-20261014/`.

Conclusion for this optional backend: oneTBB benefits the brute-force-heavy
Cafe workload, with a smaller benefit on Kitchen; it is not a significant Kamaz
optimization. The existing random placement and footprint-search/audit paths
remain the better targets for that workload. Keep the backend experimental and
OFF in normal builds. `MESHLAB_TBB_PACKING=ON` requires
`MESHLAB_COPY_PLUGINS=OFF`; publishing a dependency-bearing native variant is
explicitly blocked until runtime/metadata/license integration is implemented.
Package native binaries and the original project are unchanged.

Further local ISPC, Embree and IPP trials and the oneMKL applicability assessment:
[INTEL_LIBRARY_BENCHMARKS.md](INTEL_LIBRARY_BENCHMARKS.md). ISPC and IPP do not
qualify as packing upgrades; Embree query speed is promising but seam/vertex
compatibility fails, so no query backend was replaced.

## Eighth continuation: concurrent packing audits — 2026-10-14

Each native family trial validates every family's initial placement through
the managed metric, precision and contact audits. These validations read only
their own family, so they run concurrently and are consumed in family order;
transform/shape recovery, rescue phases, refusals and exceptions keep the
sequential order. Atlas audits scan LOD levels concurrently and merge in level
order. It was measured default on, with `-DisableParallelValidation` as the
control. After those measurements it defaults to off; opt in with
`-ParallelValidation` / `MESHLAB_PARALLEL_VALIDATION=1`. New timing sample:
`Native family parallel validation` (nested inside `Native family packing trial`).

Measured candidate: `3552a3c5` (forward per-shell trims `ab575bc`, reverse
packing trims `b58d9e3`, concurrent audits `cf74766`, worker Burst raster
`3552a3c`). Intel Core i9-14900K, Unity 6000.2.6f2, Burst 1.8.28, Collections
2.5.7, 32 tex/m, 2 px padding, Fill atlas, same isolated project. Every arm uses
`-CombinedNative` with one private library (SHA256 `15C512B4…51783`; the
installed plugin lacks the footprint-search exports) and `-RequireBurstRaster`.
Package Plugins are unchanged. Arms alternate per round, one repetition each:
(a) package at `a5c5deb`, three rounds; (b) head with
`-DisableParallelValidation` and (c) head default, five rounds each. Medians:

| Model | Policy | Reverse a (s) | Reverse b (s) | Reverse c (s) | a → c |
|---|---|---:|---:|---:|---:|
| Cafe_Table_A_01 | no overlap | 1.09 | 1.13 | 1.15 | +5% |
| Cafe_Table_A_01 | overlap | 2.96 | 2.88 | 2.88 | −3% |
| Modern_Kitchen_A | no overlap | 6.50 | 6.04 | 6.06 | −7% |
| Modern_Kitchen_A | overlap | 6.59 | 5.90 | 5.87 | −11% |
| Kamaz Typhoon (`kamaz_Off`) | no overlap | 16.46 | 15.39 | 16.39 | 0% |
| Kamaz Typhoon (`kamaz_Off`) | overlap | 34.66 | 32.22 | 32.56 | −6% |

`Native family packing trial` medians b / c: Kitchen 5.34 / 5.32 s and
1.55 / 1.53 s; Kamaz 12.74 / 13.41 s and 15.84 / 16.23 s. The concurrent
`Native family parallel validation` phase takes at most 0.62 s (Kamaz exclusive).

The a → b gain is the exact reverse trims; concurrent validation (b → c) gives no
measurable gain. On Kamaz exclusive it is slower and noisier: b spans
15.17–15.52 s, c 15.26–17.40 s (median +6.5%). Its cause is unconfirmed. Cafe
exclusive differences are within run-to-run spread. `parallelFamilyValidation`
therefore now defaults to off. One check pair on the opt-in source keeps all
120 exports identical: zero concurrent-phase samples by default, 68 with
`-ParallelValidation`. Its single Kamaz exclusive result (15.29 s sequential,
14.82 s concurrent) confirms that the difference is run-to-run variation.

Ordinary Transfer is timed as `Forward pipeline` (export excluded). Medians
a / b / c: Cafe 0.53 / 0.46 / 0.47 s, Kitchen 4.40 / 3.86 / 3.84 s, Kamaz
30.39 / 28.09 / 27.78 s — about 13% faster on Kitchen and 9% on Kamaz.

All 14 runs pass the three required cases. Every run's 120 exported files are
byte-identical (SHA256) to arm a round 1. Input hashes/frames, atlas sizes,
pack/search/audit counts, logical work and refused/retained orders match. They
also keep exact binary numeric values against `tbb-final-control`, with eight
JSON last-digit formatting differences. Burst runs 57,151 (Kitchen) and 326,588
(Kamaz) compiled overlap triangles in every arm. No check or tolerance was relaxed.

Candidate EditMode regression with Burst required: 385 tests, 348 pass, 0 fail,
37 optional private skips. Groups (pass/skip): `TransferPerformanceTests` 27/10
(37/0 with the private native-search library), ReverseUv fixtures 224/16,
`UvJunctionCutsTests` 31/11, `TransferQualityRegressionTests` 66/0. The Burst
worker direct-call assertions and both parallel-validation identity cases
pass. The async hub UI path was not timed in this run.

Reproduce (one round; the driver alternates arms and switches the project's
`file:` package reference between an `a5c5deb` and a head worktree):

```powershell
& 'Tools~/run-transfer-performance.ps1' -UnityEditor $editor -ProjectPath $project `
    -OutputRoot $arm -Repetitions 1 -Density 32 -CombinedNative `
    -NativeFootprintLibrary $privateDll -RequireBurstRaster [-ParallelValidation]
python -I 'Tools~/compare-transfer-performance.py' --baseline $a1Models `
    --control $aModels1 $aModels2 $aModels3 --candidate $cModels1 $cModels2 $cModels3 --output $summary
```

Artifacts: `_results~/transfer-performance-20261014-eighth/` (`arm-a|b|c/`,
`summary-a-vs-c.json`, `summary-b-vs-c-5pairs.json`, `summary-historical*.json`,
`stage-medians.json`, `regression-candidate*/`, `optin-check/`).

### Async Reverse: Burst direct call on workers

Merging `codex/transfer-performance` (PR #247) brings the CI-published Plugins
(`7607b172`), which export the footprint-search and snapshot API, and its
async-packing tests. The async tests now require the direct call instead of the
earlier scalar guard. On the merged source, a run with only the installed plugin
(no private library) and a combined-library run with the regression groups both
keep all 120 exports byte-identical to arm a. The installed plugin performs the
same 26,316 Kamaz overlap snapshot searches and 326,588 Burst triangles. The
regression run passes 363 tests, fails 0 and skips 27 optional private fixtures.
Its timings are single observations, partly measured alongside the regression
suite, and are not a performance comparison. Artifacts: `merge-check/`.

`-AsyncReverse` (`MESHLAB_PAIRED_ASYNC_REVERSE=1`) runs the reverse modes through
the editor's asynchronous path, with final packing on workers, as the hub does.
A Burst ablation on both paths keeps all 120 exports byte-identical in 12 runs.
Burst shortens the Kamaz footprint raster stage from 1.28 s to 1.04 s on the
sync path and from 1.25 s to 0.93 s on the async path, where it runs as a
direct call. That stage is under 4% of the overlap solve, so the whole Reverse
total shows no change beyond run-to-run spread. See EXPERIMENTS.md ("Burst
raster on the synchronous and asynchronous paths — measured").
