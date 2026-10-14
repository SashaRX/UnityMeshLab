# Intel library experiments — 2026-10-14

Later compatible adapter and exact bake checks:
[EMBREE_COMPATIBILITY.md](EMBREE_COMPATIBILITY.md). The later prototype retains
managed primitive arithmetic and exact results, with selective gains on large
queries; it remains outside the production pipeline.

Source base: `97d8828f`, branch `codex/transfer-performance`, with the existing
combined native/Burst/shared-snapshot performance work. These are local
experiments; package `Plugins/` and the user's Unity project are unchanged.

## Decision

- **ISPC mask backend:** exact, but no consistent native packing improvement.
  Keep `MESHLAB_ISPC_PACKING` OFF. It is an experiment, not a new default.
- **Embree:** promising for large CPU query batches, but a direct replacement
  changes seam/vertex hits and donor-face selection. Do not enable it in Transfer
  or the baker until candidate selection and boundary behavior are compatible.
- **IPP:** the tested byte-mask adapter is exact but much slower after conversion.
  The separate packed-bit adapter fails exact comparison and remains unqualified.
- **oneMKL:** inspected, not benchmarked or integrated. The current packing hot
  path does integer occupancy/candidate work, not linear-system solves. The
  managed ARAP solver does have CSR conjugate-gradient work; isolate and profile
  that stage before considering MKL. This is not evidence that MKL cannot improve
  ARAP or native automatic parameterization.

## ISPC: actual model packing and complete UV output

Pinned ISPC 1.26.0 Windows compiler, SSE2/AVX2 runtime dispatch. Kernels use only
integer mask operations. Immediate collisions and small masks remain scalar;
wide comparisons retain the clipped-word and dirty-tail semantics. Padding
retains the exact eight-neighbour dilation and clears invalid storage bits.
Both arms have the previously tested oneTBB backend enabled, plus the same
native footprint search; this isolates ISPC from the earlier oneTBB gain.

Native chart-placement medians, three alternating pairs, seconds:

| Model | Policy | oneTBB control | oneTBB + ISPC |
|---|---|---:|---:|
| Cafe_Table_A_01 | exclusive | 0.351 | 0.348 |
| Cafe_Table_A_01 | overlap | 0.413 | 0.450 |
| Modern_Kitchen_A | exclusive | 1.464 | 1.476 |
| Modern_Kitchen_A | overlap | 0.674 | 0.681 |
| Kamaz (`kamaz_Off`) | exclusive | 4.122 | 4.163 |
| Kamaz (`kamaz_Off`) | overlap | 12.453 | 12.515 |

All 74 captured requests in each of six executions match their original native
UV Float32 bits, chart IDs, references and atlas sizes. Input hashes also match.
The roughly 9% Cafe overlap regression rejects this configuration as an upgrade.
The other changes are small and do not establish a benefit.

The ISPC build passes six CTest suites: 1,736,652 reference mask comparisons,
6,240 reference dilation passes, the footprint-search oracle and the existing
topology/UV/parallel-placement tests. A separate complete Unity run executes
ordinary Transfer and both Reverse policies on all three models: 288 passed,
zero failed, 17 optional private cases skipped. All 120 stable model exports
match the immutable baseline by exact numeric bits. Geometry, ancestry, input
frames/hashes, logical work, retained/refused orders and atlas dimensions match.
Burst remains active on the same qualifying work as the control.

The single complete ISPC run is a correctness gate, not a new median performance
comparison. Some subsequent harness preparation overlapped that run, so its
wall-clock samples are not used to qualify speed. The three native pairs above
were measured sequentially without concurrent benchmark jobs.

Evidence under `_results~/intel-libraries-20261014/`:
`ispc-native/summary.json`, `ispc-build/Testing/Temporary/LastTest.log`,
`ispc-full/run-01/{tests.xml,unity.log,models/}`, `ispc-full-summary.json`.
Tested ISPC plugin SHA256:
`B45D52D548694F0317D024AF58D50030F3628109D5EE80AFF061A6BAE2CCD094`.

## Embree: query speed and compatibility

Pinned Embree 4.3.3 CPU Windows distribution. A private batched ABI performs
ordinary two-sided rays, facing-filtered rays, target-relative ray selection,
nearest point and normal-filtered nearest point. Face-normal and either-side
rules are applied explicitly; every seventeenth face is marked either-side as
a controlled filter fixture. Nearest primitives use the same Ericson branches
as `TriangleBvh`; Embree provides traversal and its native ray intersection.

Inputs are the fixtures' actual LOD0 geometry in root-local space. Query points
come from coarser LOD face centres and vertices, displaced by controlled normal
offsets: 748 Cafe, 3,250 Kitchen and 25,694 Kamaz probes per mode. The analytic
fixture has parallel surface layers and 1,024 probes. These are query tests on
real geometry, not actual bake bands or complete Reverse output benchmarks.
No model mesh is assigned, overwritten or saved by this driver.

Each mode is checked serially and with eight workers in both arms; each has a
warmup and three alternating measured repetitions. Native timings include
managed/native marshalling and creation/join of workers. Managed timings include
`Parallel.For` scheduling. Scene build is measured separately. Small Cafe and
analytic batches show no consistent eight-worker benefit; dispatch costs matter.

Audited eight-worker medians, milliseconds:

| Model | Operation | TriangleBvh | Embree | Speedup |
|---|---|---:|---:|---:|
| Kitchen | ray | 2.89 | 0.80 | 3.64x |
| Kitchen | facing ray | 3.02 | 0.79 | 3.81x |
| Kitchen | target-relative ray | 3.76 | 0.89 | 4.23x |
| Kitchen | nearest point | 2.92 | 1.04 | 2.81x |
| Kitchen | normal-filtered nearest | 4.96 | 1.26 | 3.94x |
| Kamaz | ray | 33.99 | 3.78 | 9.00x |
| Kamaz | facing ray | 33.67 | 3.48 | 9.68x |
| Kamaz | target-relative ray | 41.28 | 3.78 | 10.91x |
| Kamaz | nearest point | 33.96 | 5.79 | 5.87x |
| Kamaz | normal-filtered nearest | 33.54 | 5.40 | 6.21x |

Compatibility findings are unchanged across both worker counts and the two
corrected runs:

- Ray hit/miss differences: Cafe 136 ordinary / 142 filtered, Kitchen 33 / 44,
  Kamaz 329 / 368. Every such difference is a vertex probe; face-centre probes
  have zero hit/miss differences in this fixture set. This does not prove all
  centre hits select the same face or UV.
- Nearest queries have zero hit/miss differences after correcting the private
  adapter's initial radius initialization, but select different tied/nearby
  faces. On Kamaz, normal-filtered nearest has 2,807 face-identity differences
  and 515 interpolated authored UV0 differences greater than 1e-5 over 25,694
  probes. The maximum UV difference is 1.03369, despite a maximum squared-distance
  difference of only 8.55e-8. A near-identical distance is not donor equivalence.
- Exact result-bit comparisons also fail. The analytic fixture has matching hit
  and face selections, yet floating-point barycentric results are not bit equal.
- The 1e-5 thresholds categorize experimental differences only. No production
  tolerance or result guard was changed. UV0 here is a seam sensitivity check;
  it does not claim to measure a final lightmap atlas.

The next Embree experiment should use it to enumerate geometric candidates,
retain our primitive tests/filters, and explicitly preserve tie selection or
route ambiguous boundary queries through the existing resolver. Then test real
bake bands and all UV outputs. Directly swapping `TriangleBvh` is not qualified.
Query-only speedups cannot be applied to the whole Reverse timer: the earlier
Kamaz overlap profile spends 30.6/33.2s in packing/audits and 1.45s in projection.

The first `embree-queries/` report used positional initialization with a zero
point-query radius and is invalid for nearest comparison. It is retained for
diagnosis and excluded from the results above. Corrected reports:
`embree-queries-final/queries.json`, `embree-queries-audited/queries.json` (includes
query/result examples), and `embree-audited-summary.json`.
Final private adapter SHA256:
`85B8A83CDC5F6DE2854CEF794D727C88618DDB2F1DDA121FA9A9D68C07B79CA5`.

## IPP: padding masks

Pinned Intel NuGet IPP 2021.12.1.13, `ippcv`/`ippcore`. The isolated harness compares
eight-neighbour dilation with the existing 64-bit word implementation. Thirty-three
mask shapes cover widths 7..2048, heights 3/32/257, sparse/empty/dense patterns and
dirty storage tails. Setup/work buffers are cached, and each measurement uses 200
calls with compiler barriers, three alternating pairs. All 396 byte-mask checks
match every output storage bit.

The byte path unpacks bit words, performs `ippiDilateBorder_8u_C1R`, then repacks
them. Conversion is included. It is 11..91x slower over these synthetic shapes:

| Mask | Word dilation (ms) | IPP byte path including conversion (ms) | IPP kernel only (ms) |
|---|---:|---:|---:|
| 129x257 | 0.0009585 | 0.056303 | 0.0054815 |
| 512x257 | 0.002291 | 0.204526 | 0.0060155 |
| 1025x257 | 0.005896 | 0.423383 | 0.010094 |
| 2048x257 | 0.010214 | 0.828719 | 0.016036 |

These are synthetic mask measurements, not model transfer timings. They reject
this adapter for packing; they do not reject IPP operations on already-byte-form
textures elsewhere in the baker.

The packed `1u` path is separately unqualified: exact comparison still fails at
63x3 with a zero halo, masked source tails and IPP-aligned state allocation.
It fails strictly; no result guard is disabled. The cause is unresolved and is
not attributed to an IPP defect. Its timings are not used as an acceleration
result. The independent `--8u-only` benchmark does not run that representation.
Evidence: `ipp-8u-verified.csv`, `ipp-verified-summary.json`; strict 1u diagnostics
are reproducible with the default harness command.

## Reproduction and dependencies

Sources: `Native~/src/ispc_masks.ispc`, opt-in native CMake option,
`Tools~/IntelLibraryBench/{CMakeLists.txt,EmbreeQueryBench.cs,embree_query_bench.cpp,
ipp_mask_bench.cpp,run-embree.ps1}`. Experimental builds require
`MESHLAB_COPY_PLUGINS=OFF`. Embree and IPP binaries remain only in the ignored
results folder; nothing is installed into system directories or the package.

Official distributions and observed archive SHA256:

- [ISPC v1.26.0](https://github.com/ispc/ispc/releases/tag/v1.26.0):
  `CFFE9904D32260994FA264F8FCA60EAC7BE9F8995C122BACF456A1C66AC72987`.
- [Embree v4.3.3 CPU](https://github.com/RenderKit/embree/releases/tag/v4.3.3):
  `D4C07F88DF9F009DD84E4E9B9DCEC32AD7D96F927BD88DE00B721B0923D481A9`.
- [Intel IPP devel](https://www.nuget.org/packages/intelipp.devel.win-x64/2021.12.1.13):
  `95D1AD38760D239EB4F6C7BCED0AE40A19FC554CF756F797581C6E08D241886A`.
- [Intel IPP redist](https://www.nuget.org/packages/intelipp.redist.win-x64/2021.12.1.13):
  `3F91797E3FB36420A551E3315882174A187D61B1D7678289B243AC53030FDA39`.

Configure native with `-DMESHLAB_ISPC_PACKING=ON`, the pinned compiler path in
`MESHLAB_ISPC_EXECUTABLE`, and the same pinned meshoptimizer/oneTBB sources as the
preceding control. Run CTest, `Tools~/compare-native-packing.py` and
`Tools~/run-transfer-performance.ps1 -CombinedNative -RequireBurstRaster`, then
`Tools~/compare-transfer-performance.py` against the unchanged baseline.

Configure `Tools~/IntelLibraryBench` with `EMBREE_ROOT` pointing to the extracted
CPU distribution. Optional `IPP_DEVEL_ROOT` and `IPP_REDIST_ROOT` point to their
NuGet extraction roots. The Embree runner requires a closed isolated Unity
project with the three fixtures and removes only its own temporary script.
Run `meshlab-ipp-mask-bench <output.csv>` for strict 1u+8u checking, or
`meshlab-ipp-mask-bench <output.csv> --8u-only` for the byte-representation trial.

No commit, push, package update or native-binary publication was performed.
