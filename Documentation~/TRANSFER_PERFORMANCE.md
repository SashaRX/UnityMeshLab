# Transfer performance replay

## 2026-10-15: local integration of the forward acceleration stack

At the checked main `1945a383` (1.2.10), PR247's Reverse packing work and
PR252's reduced per-shell copying are present. The later forward stack was
merged into intermediate branches: PR257 targets
`ccr-73114d2a-p5-forward-stage-profile`, and PR260 targets
`ccr-73114d2a-p7-projection-indexes`. Its merge `0b01c314` is not an ancestor
of this main. A merged PR in that stack does not establish main integration.

This unpublished continuation ports the managed shared-prefix, donor-seam and
chart indexes, original contact cache and exact parent-coverage index from that
stack. It retains the current main lifecycle and the local correspondence,
continuity and published-position precision fixes. Mutable target arrays and
faces are copied for each retry; the shared preparation lives for one operation.
Indexes retain candidate order and the existing logical work budgets.

### Audit of the earlier PR stack

GitHub PR metadata and commit ancestry were rechecked against main `1945a383`.
The status below distinguishes a merge into main from a merge into another
branch of the stack.

| PR | Work | State at this continuation |
|---|---|---|
| [247](https://github.com/SashaRX/UnityMeshLab/pull/247) | Footprint caches, native search, shared occupancy and optional query/Burst backends | Already in main |
| [252](https://github.com/SashaRX/UnityMeshLab/pull/252) | Avoid whole-mesh copies in per-shell Forward work | Already in main |
| [253](https://github.com/SashaRX/UnityMeshLab/pull/253) | Reuse final-packing face/index lists, defer precision snapshots, avoid repeated orientation/rescue work | Still in the stack; not included in this port |
| [254](https://github.com/SashaRX/UnityMeshLab/pull/254) | Opt-in concurrent family and per-LOD atlas validation | Still in the stack; not included in this port |
| [255](https://github.com/SashaRX/UnityMeshLab/pull/255) | Burst direct calls on async workers | Ported here, including actual-execution tests |
| [256](https://github.com/SashaRX/UnityMeshLab/pull/256) | Ordinary Forward stage profiling | Ported here |
| [257](https://github.com/SashaRX/UnityMeshLab/pull/257) | Share the Forward seam projection prefix | Ported here with the current quality/precision fixes |
| [258](https://github.com/SashaRX/UnityMeshLab/pull/258) | Donor-seam and chart-bounds indexes | Ported here |
| [259](https://github.com/SashaRX/UnityMeshLab/pull/259) | Native Embree build flags and missing-batch error reporting | Already in main; this does not publish the optional Intel runtime |
| [260](https://github.com/SashaRX/UnityMeshLab/pull/260) | Cache original seam contacts and index parent coverage | Ported here |

The remaining PR253/254 implementations and their associated concurrency tests
are not claimed as part of the current result. The presence of an option field
alone is not evidence that its concurrent implementation runs. Their prior
experiments are retained in `EXPERIMENTS.md` and the stack commits.

Intel experiments also remain available, with their distinct outcomes:

| Backend | Retained result | Production decision |
|---|---|---|
| oneTBB | Exact output; useful Cafe gains, no substantial complete-workflow Kamaz gain | Optional native build, OFF |
| ISPC | Exact mask backend; no consistent packing gain | OFF |
| Embree | Later compatible adapter passes exact comparisons and speeds some large query batches, but complete Reverse does not improve | Runtime remains experimental/OFF |
| IPP | Byte-mask conversion path is slower; packed-bit prototype fails exact comparison | Not adopted |
| oneMKL | Applicability inspected; not benchmarked or integrated | No speedup claim |

The later compatible Embree work supersedes the first incompatible prototype
as correctness evidence; it does not establish a whole-workflow performance
gain. See `EMBREE_COMPATIBILITY.md`, `REVERSE_QUERY_BACKEND.md` and
`INTEL_LIBRARY_BENCHMARKS.md`. The old private native builds, model fixtures and
benchmark artifacts remain separate from the current default-plugin runs.

### Controlled comparison

Three alternating control/candidate pairs use Unity 6000.2.6f2, the same copied
Kamaz_Typhoon and CementWall FBX files **and their importer metadata**. Both arms
use Legacy Fixed symmetry, pre-optimization, narrow-junction cuts, 32 tex/m,
2 px shell padding, zero border and exclusive Reverse with reach 0.05 and Fill
atlas. The control is main plus the same local quality fixes, frozen before
this performance integration; it is not an untouched main benchmark.

Headless CPU timings exclude editor startup, result export and asset saving.
Diagnostic transfer capture is enabled in both arms and its in-pipeline work is
included. No viewport/GPU timing or live-project save/import performance is
established. The installed project and its assets were not updated.

| Model / stage | Control median (s) | Integrated median (s) | Reduction |
|---|---:|---:|---:|
| Kamaz / ordinary full pipeline | 35.037 | 21.059 | 39.9% |
| Kamaz / detached preparation for Reverse | 30.581 | 16.496 | 46.1% |
| Kamaz / Reverse solve alone | 24.504 | 24.033 | within timing variation |
| Kamaz / preparation plus Reverse | 55.085 | 40.397 | 26.7% |
| CementWall / ordinary full pipeline | 1.427 | 1.027 | 28.1% |
| CementWall / detached preparation for Reverse | 1.026 | 0.667 | 35.0% |
| CementWall / Reverse solve alone | 2.101 | 2.097 | within timing variation |
| CementWall / preparation plus Reverse | 3.137 | 2.774 | 11.6% |

The last stage for each model is the median of each run's two disjoint phases.
Nested profile stages are not added. All six model pairs retain exact input
hashes/frames and all 150 stable exported layout, quality and ancestry JSON files
byte for byte (37 Kamaz + 13 CementWall per pair). Timestamped diagnostic file
names and timing samples are not stable result files.

These measurements use the package's existing native binaries. Intel oneTBB,
ISPC, Embree and IPP experimental backends are OFF. Burst is installed, but
neither of these exclusive-mode runs records executed Burst raster spans; this
gain is shared preparation and spatial indexing, not measured Burst execution.
The earlier optional Intel results below and in `INTEL_LIBRARY_BENCHMARKS.md`
remain separate experiments.

The same integration audit found PR255 (`10032dc4`, worker Burst direct calls)
merged into `ccr-73114d2a-p3-parallel-validation`, also outside this main's
ancestry. Its implementation is now restored locally. The optional Burst
assembly permits unsafe code for pinned kernel buffers; its dependency/version
gates and Editor-only platform stay unchanged. The editor thread schedules jobs;
workers call the same strict-float kernel in independent thread-pool chunks.
The test scope changes global compilation options only temporarily, with
EditorPrefs callbacks disabled and the original options restored afterward.

The real Kamaz asynchronous overlap run records 288111 qualifying triangle
visits and 288111 executed Burst triangle visits (repeated masks, not distinct
model triangles). The recorded raster work, including span assembly, is 0.261 s;
Reverse solve is 33.820 s and the complete Reverse workflow 33.860 s, excluding
the separate 16.857 s preparation. All publication/Viewer audits pass. This is
one execution proof, not a median or a measured whole-workflow Burst speedup.
The three-pair table above measures exclusive mode, which does not call this
raster path. Evidence: `async-burst.xml` and `async-burst/` in the same directory.

The matching asynchronous scalar control passes too. All 37 stable Kamaz result
files, input hashes and frames are byte-identical. Scalar mask work is 1.037 s
versus 0.714 s total mask work with Burst (0.261 s of that is recorded bulk
raster/span assembly). Complete solve times are 34.584 s / 33.820 s. A single
pair establishes output equivalence and executed Burst, with a modest observed
whole-solve difference; it is not a statistically resolved speedup estimate.
Evidence: `async-burst-summary.json` and `async-scalar.xml`.

Final focused Unity regression suite: 387 passed, 0 failed, 16 optional private
cases skipped, with Burst required. It includes worker/job/scalar spans,
asynchronous Reverse under both overlap policies, shared/independent seam passes,
indexed/full-scan coverage, asynchronous contact audit equivalence and waiting
for cancelled workers before releasing data. Both FBX compile-check variants,
identifier and dependency guards pass. No native source or binary changed.

An integrated Kamaz sample attributes 12.19 s to the donor-seam trial, including
8.39 s of projection and 2.72 s of contact audits. The ordinary shell-transfer
stage itself is 2.99 s. These nested figures explain the remaining bottleneck;
they must not be summed. Interactive original/candidate contact audits now use
the existing cancellable worker helper over captured managed arrays. Workers
exit before their operation's owned meshes are disposed. This scheduling change
is separate from the synchronous timings above.

Quality limits and output defects are unchanged by this acceleration. Kamaz
ordinary LOD2 still reports 59 collapsed faces and 134 unexpected overlap pairs;
exclusive Reverse publishes zero collapsed faces or unexpected overlaps and no finer cuts.
The preceding whole-island correspondence work gives CementWall 1079 inherited
faces / 1522 px rather than 388 / 2142 px. Those are quality improvements from
the control's shared fixes, not effects of the performance port.

Private evidence: `_results~/transfer-speed-audit/summary.json`, six run XML/log
sets, `control-provenance.json`, `performance-source.diff` and the frozen control
package. The owned project's manifest and lock are restored byte for byte.

### Final-packing continuation: exact work reuse from PR253

The next local continuation ports PR253's three managed-source changes on top of
`846d08f5`. Per-LOD face/index lists and surface group keys are prepared once per
pack; aligned native placement ranks discrete turns only to describe a shape
refusal; phase (0,0) is not validated twice after it fails. Float32 repair clones
coordinates and builds shared-corner aliases only when the first face actually
needs repair. Validated faces before that repair still participate in its aliases.
No density, placement, repair order, tolerance or work budget changes. The older
PR254 concurrent implementation is deliberately not imported: its five-pair
experiment found no gain and +6.5% median Kamaz exclusive time. Its existing option
field is not evidence of an integrated concurrent path.

Three new alternating pairs compare against the preceding committed integration
`846d08f5`, not main and not the earlier pre-integration control. Both arms add
the same opt-in timing scopes. Inputs/import metadata/settings match the earlier
32 tex/m exclusive test. All 150 stable output files and input hashes/frames are
identical. Native placement counts remain 22 per Kamaz solve and 12 per CementWall
solve; Kamaz whole-atlas audits remain 13. Family validation drops 61706 -> 61643
only by removing repeated failed phase (0,0).

| Model / stage | Control median (s) | Trims median (s) | Control range / trims range (s) |
|---|---:|---:|---|
| Kamaz / Reverse solve | 28.275 | 26.137 | 24.482-39.209 / 22.525-28.086 |
| Kamaz / preparation + Reverse | 45.890 | 43.956 | 41.182-62.855 / 39.516-51.368 |
| CementWall / Reverse solve | 2.131 | 2.229 | 2.122-4.754 / 2.109-2.247 |
| CementWall / preparation + Reverse | 2.800 | 2.916 | 2.797-6.824 / 2.784-2.974 |

These full-workflow numbers are preliminary under external workstation load.
WinDirStat and the user's open Unity were active; no user application was stopped.
Even unchanged Forward work varies substantially between runs. The Kamaz solve
median is 7.6% lower in this series; preparation plus solve is 4.2% lower. This
is not a clean estimate of a small end-to-end gain. CementWall has no measured
whole-solve gain (its native placement, untouched by the patch, is slightly slower).
Do not combine these medians with the earlier table as one simultaneous baseline.

The directly affected Kamaz stage is more consistent:

| Stage | Control median (s) | Trims median (s) |
|---|---:|---:|
| Native placement | 8.986 | 8.825 |
| Native envelope audit | 5.601 | 5.711 |
| Family transforms and validation | 5.316 | 2.933 |
| Family validation (nested) | 1.617 | 1.143 |
| Float32 stabilization (nested) | 0.459 | 0.057 |
| Whole-atlas audits | 0.869 | 0.778 |

These intervals are nested and must not be added. Native placement and envelope
audits now dominate; family-level validation is a small part of the solve. Simply
parallelizing that small phase is not supported by the old PR254 evidence. In
exclusive mode these native packing/audit operations do not call the optional
Burst raster kernel. The previously verified async Burst path remains integrated;
Intel runtime experiments remain OFF and all nine plugin files match the control.

The next profiling target is the actual native request sequence: repeated density,
axis/orthogonal and precision trials, and its exact envelope broad phase. Any
new cache or broad phase must preserve real Float32 contacts, cancellation and
budget/refusal ordering; reducing audited output quality is not a speedup.
Ordinary Forward still spends most of its transfer work on donor-seam projection
and contact audits, with the previously documented unresolved Kamaz faces unchanged.

A separate diagnostic capture confirms 22 native requests: density 1/2/3/4/6/8
uses 2/2/6/4/4/4 calls, including orientation and precision retries. The first
request has 98122 proxy triangles for 2762 families; the published three-LOD
chain has 31842 triangles. Of those proxy triangles, 66736 have altitude at most
0.01001 in the base packing coordinates. They are packing masks/guards, not
new exported faces. Leaner equivalent proxies or an exact envelope broad phase
are hypotheses to test; these counts do not justify discarding protective geometry.
All 37 stable capture outputs and input hashes/frames match the timed candidate.
Its extra capture I/O is excluded from the six-run timing table.

Validation: 426 passed / 0 failed / 16 optional private fixture skips with Burst
required; both compile-check FBX variants and identifier/dependency guards pass.
The healthy-face-before-repair regression covers deferred alias construction.
Evidence: `_results~/transfer-packing-audit/`, `protocol.json`, `summary.json`,
source hashes, the frozen control and instrumentation diff, six model-run XML/log
sets. Manifest and lock were restored byte for byte. All changes are local.

### Native chart and input reuse: final controlled comparison

One resident xatlas context now lives for a final exclusive frozen-family pack.
Its existing global lease prevents other xatlas callers from replacing prepared
charts. Changed UV Float32 bits, indices or face materials rebuild the atlas;
identical operation-owned immutable footprints reuse both charts and their input
buffers. Preparation failures are not retained. Cancellation/error disposal
releases native ownership. This uses the existing ABI and package plugins.

Kamaz has 6 hits and 16 chart preparations instead of 22 preparations. CementWall
has 11 hits and one preparation instead of 12. PackCharts still executes every
trial (22 / 12); changed precision masks cannot reuse unrelated prepared charts.
Multiple resident native contexts are not supported by the current singleton
bridge. No density candidate, packing attempt or quality audit was removed.

The final comparison freezes the preceding quality/shared-prefix/async-Burst/
PR253 work as its control. It includes no new Intel runtime. Three alternating
control/candidate pairs run sequentially in headless Unity 6000.2.6f2 using copied
FBX/import metadata and the preceding 32 tex/m settings. The experimental GPU API
is not called. Timing excludes startup/import, final export and saving assets.

| Model / stage | Control median (s) | Reuse median (s) | Control range (s) | Reuse range (s) |
|---|---:|---:|---:|---:|
| Kamaz ordinary Forward | 20.742 | 21.076 | 20.681–21.551 | 21.018–21.379 |
| Kamaz detached preparation | 16.229 | 16.210 | 16.136–16.487 | 16.124–16.495 |
| Kamaz exclusive Reverse | 21.688 | 21.172 | 21.253–22.141 | 20.948–21.377 |
| Kamaz preparation + Reverse | 37.917 | 37.296 | 37.389–38.629 | 37.158–37.873 |
| CementWall ordinary Forward | 1.027 | 1.023 | 1.017–1.077 | 1.013–1.032 |
| CementWall detached preparation | 0.655 | 0.653 | 0.650–0.660 | 0.650–0.659 |
| CementWall exclusive Reverse | 2.067 | 2.030 | 2.058–2.105 | 2.028–2.039 |
| CementWall preparation + Reverse | 2.717 | 2.689 | 2.713–2.765 | 2.678–2.692 |

The observed Reverse medians improve by 2.38% / 1.78%; including preparation,
1.64% / 1.03%. This is a small measured difference with overlapping Kamaz ranges,
not evidence of a substantial workflow acceleration. Ordinary Forward uses the
same algorithm; its noisy timings do not establish a gain or regression. All
150 stable export files match byte for byte, including
UV coordinates, reports, geometry, atlas dimensions and ancestry; input hashes
and frames match. All six run XML files execute both required models successfully.

The native input+chart preparation scope falls from 1.423 to 1.078 s on Kamaz and
17.70 to 1.50 ms on CementWall. This scope includes C# input-buffer construction
in both arms, unlike the older narrow discovery timer. Its control-only timer
movement is saved in `control-instrumentation.diff` of the earlier comparison.
Native placement remains 7.810 / 7.714 s on Kamaz and 1.532 / 1.534 s on
CementWall; exact envelope audits remain 4.945 / 4.863 s and 0.0437 / 0.0425 s.
Nested stage times must not be added.

Earlier artifacts remain distinct. Charts-only reuse in
`_results~/native-chart-reuse-audit/` retained exact output without a resolved
whole-solve gain. `_results~/native-chart-buffer-reuse-audit/` was intentionally
stopped after two complete exact pairs: a GPU refactor had dropped cached CPU
triangle reads and regressed the CPU loop. That loop was restored before these
final measurements. Its `superseded.json` excludes those timings from conclusions.

Publication-only evidence: `_results~/native-chart-publication-check/summary.json`,
`provenance.json`, source hashes, frozen packages, six run XML/log sets and the
additional broad regression XML: 433 passed, zero failed, 16 optional skips with
Burst required. This snapshot excludes the experimental GPU code and restores
the original CPU audit implementation. Its frozen commit is `6ff840aa`; the
later S3973 correction only adds an outer foreach block. The earlier combined
cache/GPU comparison in `native-chart-reuse-final` retains its own measurements,
source hashes and test outcomes. All nine plugin files match the control, and
manifest/lock are restored exactly. Ordinary Kamaz's previously documented
quality defects remain unchanged; reuse is not a fix for those defects. The
live project was not updated by these benchmarks.

### GPU pair-selection experiment: exact output, slower wall time

An explicit experimental `MeasureGpuAsync` snapshots managed input, constructs
the same CPU grid, and asks a compute shader for a conservative superset of AABB
pairs. Outward Float32 bounds retain touching and uncertain pairs. GPU records
unique grid ownership and each candidate's original comparison ordinal; readback
sorting preserves work budgets and deterministic sample order. The CPU still
executes the original double SAT/clipping/area predicates and conflict limit.
Unsupported hardware, overflow or errors use the complete CPU path. Cancellation
waits for submitted readbacks before releasing buffers. Normal Transfer and its
publication gates stay on CPU; the experiment has no automatic registration.

Three DX11 rounds on NVIDIA GeForce GTX 980 Ti replay 22 actual Kamaz native proxy
envelopes and three CementWall published LOD layouts. All 75 reports match CPU
exactly, with no fallback; the CementWall inputs are layouts, not native proxies.
Median sums per round:

| Input set | CPU (ms) | Hybrid including setup/readback/sort/exact CPU (ms) |
|---|---:|---:|
| Kamaz, 22 envelopes | 4891.95 | 7590.49 |
| CementWall, 3 layouts | 2.143 | 18.965 |

Kamaz's 33.1 million charged comparisons still produce 18.8 million conservative
GPU candidates. Its exact CPU phase alone remains about 4.60 s, plus about
0.77 s preparation and 2.26 s selection/upload/readback/sorting. Those are separate
component medians; their sum need not equal the median total. Selection is an
end-to-end wall timer, not GPU kernel time. The hybrid audit is 55% slower and
does not qualify as a production accelerator on this hardware. CementWall is too
small to amortize dispatch/readback. No whole-Reverse GPU speedup is claimed.

Analytic tests cover edge contacts, one-ULP differences, slivers, non-finite and
subnormal coordinates, wide coordinates, finite work budgets, same-chart filters,
overflow fallback and cancellation/resource reuse. Native-cache tests cover exact
keys, immutable buffer identity/order, failed preparation, lease exclusion and
disposal. The broad run passed 453 cases with one NaN assertion failure and 16
optional skips; unchanged NaN coordinates compare unequal as ordinary Vector2s.
After changing that immutability assertion to compare Float32 bits, its targeted
rerun and the native-cache tests passed all six cases. The combined distinct
results are 454 passed, zero unresolved failures and 16 optional skips, not a
claim that the first broad run was entirely green. Both FBX compile variants and
identifier/dependency guards pass. See `_results~/gpu-envelope-audit/verification.json`,
`measurements.json`, `summary.json` and run XML/logs. Keep this prototype separate
from the native chart-reuse experiment when preparing future PRs.

CUDA is a possible separate follow-up backend. It has not been implemented or measured. Any trial must compare the same exact CPU reports, retain every quality gate, and include transfer/readback overhead. Transfer correctness takes priority over further parallelization experiments.

## Replay harness

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

PR review added a main-thread scheduling guard. Unity's job scheduling is only
supported from the editor main thread; async final packing runs on a Task worker
and retains scalar rasterization before any NativeArray allocation or job API.
The timing tables below were measured by the synchronous benchmark path and do
not establish a Burst gain for async editor actions. Regression coverage compares
complete synchronous/async Reverse outputs with both overlap policies and checks
background refusal, cancellation and subsequent main-thread Burst execution.

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
