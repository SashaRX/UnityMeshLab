# Compatible Embree query adapter — 2026-10-14

Continuation of [INTEL_LIBRARY_BENCHMARKS.md](INTEL_LIBRARY_BENCHMARKS.md), on
`codex/transfer-performance`, source base `97d8828f`. This is a private prototype
and benchmark; no production query backend, package native binary, FBX or scene
was replaced during these measurements. The subsequent production-path trial
is documented in [REVERSE_QUERY_BACKEND.md](REVERSE_QUERY_BACKEND.md); it retains
UVs but does not establish a full-workflow speed gain.

## Behavior retained

Direct Embree primitive results did not retain seam hits and donor UVs. The first
native port also failed the strict comparison even with the same BVH and
`/fp:strict`: small numerical differences sometimes change face selection.
That failed result is preserved in `native-1/` and is not an accepted backend.

The compatible path therefore uses Embree only for discovery and proposals:

1. A raw Embree proposal is re-evaluated with the existing managed primitive
   functions and eligibility rules. A valid proposal supplies a conservative
   search region, not the winning face or UV.
2. A separate Embree user-primitive tree contains bounds for every source face,
   including zero-area/collinear faces. Discovery returns candidate IDs. A
   conservative bounds/normal prefilter keeps borderline eligibility for C#.
3. The managed adapter calls `TriangleBvh.Watertight` and
   `TriangleBvh.ClosestPointOnTriangle` on candidates. Original dynamic leaf
   bounds, face order, child-order tie rules and target-relative ranking remain.
4. Candidate leaves are ranked at their lowest common ancestor using the
   original query-dependent child order. Visiting just those leaves avoids
   repeating empty ancestor paths; enclosing parent bounds cannot reject a
   passing leaf in the finite tested domain.
5. Capacity exhaustion and unsupported reaches fall back to the original BVH
   for the entire query. No partial candidate list is used as a valid answer.

Margins apply only to discovery. They never modify primitive tests, UVs,
normal thresholds, reach in the exact resolver, or final winner comparisons.
The adapter remains experimental; the test corpus is not proof of universal
equivalence for every malformed mesh or floating-point extreme.

## Final paired timings

Three separate Unity processes, each with warmup and three alternating paired
repetitions per query mode. The table gives medians over nine measurements per
arm, eight workers. Candidate time includes both native calls, marshalling,
proposal re-evaluation, sorting and managed exact resolution. Scene/tree setup
is recorded separately. These are query timings, not complete transfer or bake
speedups.

| Model | Operation | Original (ms) | Compatible adapter (ms) | Speedup |
|---|---|---:|---:|---:|
| Cafe_Table_A_01 | ray | 0.459 | 1.710 | 0.27x |
| Cafe_Table_A_01 | facing ray | 0.461 | 1.565 | 0.29x |
| Cafe_Table_A_01 | target-relative ray | 0.517 | 1.707 | 0.30x |
| Cafe_Table_A_01 | nearest | 0.403 | 1.536 | 0.26x |
| Cafe_Table_A_01 | normal-filtered nearest | 0.453 | 1.649 | 0.27x |
| Modern_Kitchen_A | ray | 3.202 | 14.072 | 0.23x |
| Modern_Kitchen_A | facing ray | 3.018 | 12.477 | 0.24x |
| Modern_Kitchen_A | target-relative ray | 3.824 | 11.253 | 0.34x |
| Modern_Kitchen_A | nearest | 3.179 | 3.565 | 0.89x |
| Modern_Kitchen_A | normal-filtered nearest | 5.368 | 4.396 | 1.22x |
| Kamaz (`kamaz_Off`) | ray | 35.689 | 39.821 | 0.90x |
| Kamaz (`kamaz_Off`) | facing ray | 35.172 | 26.257 | 1.34x |
| Kamaz (`kamaz_Off`) | target-relative ray | 43.725 | 40.208 | 1.09x |
| Kamaz (`kamaz_Off`) | nearest | 34.888 | 22.642 | 1.54x |
| Kamaz (`kamaz_Off`) | normal-filtered nearest | 37.490 | 23.190 | 1.62x |

Cafe has only 748 queries per mode, Kitchen 3,250, Kamaz 25,694. An unconditional
rollout would regress small workloads and Kitchen rays. The qualified gain is
in large nearest/filtered query batches; production integration should retain
the existing resolver for workloads that do not benefit. Selection is not yet
implemented in the package. The earlier direct Embree 6..10x ray numbers cannot
be advertised for this compatible path.

## Exact query verification

Each final process checks nine fixtures, five modes, serial and eight-worker
execution: 318,680 result comparisons. All three processes retain every bit of
face ID, distance, barycentric weights and returned point: **956,040 checks**.
There are zero hit/miss, face, UV or parameter differences. A further complete
verification process also passes the same 318,680 cases.

The three real model geometries are complemented by parallel/coincident layers,
different UVs on overlapping faces, zero-area and collinear triangles, scales
0.0001/1/1000, a 100,000-unit offset, zero/negative/infinite reaches and
nonpositive/out-of-segment preferred positions. A 1,024-coincident-triangle
fixture exceeds the 256-ID query capacity and verifies whole-query fallback.

The strict gate requires all result bits to match, including misses. It does
not merely accept differences below the experimental 1e-5 diagnostic threshold.

## Real bake bands and final maps

`BakerQueryBench` uses actual ordinary-transfer LOD0 geometry and coarser model
UV layouts from the immutable paired benchmark exports. It runs the package's
real `RemeshBaker.Prepare`, `BuildRequests`, `ResolveCpu`, `EvaluateBand` and
`Finish`. Every band first obtains the original resolver's answers, verifies
the adapter's corresponding ray/fallback-nearest answers bit for bit, then feeds
the adapter's answers into the same evaluator. A separate complete original bake
supplies the final maps/counter reference.

Controlled CPU gradient textures make UV sampling observable. This is a
Materials-mode geometry/query integration test, not an authored-material,
Beauty, source-AO, GPU, native-remesh-output or production-performance benchmark.
Reflection is used only by the private driver; its costs are excluded from the
query performance table above.

| Model | Texture size | Bands | Ray queries | Nearest fallbacks | Covered texels | Distinct colors |
|---|---:|---:|---:|---:|---:|---:|
| Cafe_Table_A_01 | 64 | 1 | 5,925 | 101 | 1,783 | 2,425 |
| Cafe_Table_A_01 | 128 | 2 | 16,417 | 259 | 5,309 | 6,941 |
| Modern_Kitchen_A | 64 | 1 | 9,740 | 114 | 3,241 | 3,629 |
| Modern_Kitchen_A | 128 | 2 | 27,163 | 339 | 9,411 | 11,601 |
| Kamaz | 64 | 1 | 12,455 | 257 | 3,528 | 3,884 |
| Kamaz | 128 | 3 | 34,893 | 808 | 11,971 | 13,573 |

All six bakes have zero fully missed covered texels and preserve every base
color/normal/metal/AO byte, emission float bit and diagnostic counter. The 128²
cases explicitly reuse query-band buffers. The 64² checks also passed in two
earlier final processes. Empty/monochrome map results cannot qualify the fixtures.

## Artifacts and reproduction

Source: `Tools~/IntelLibraryBench/embree_query_bench.cpp`, `legacy_bvh.h` (failed
native arithmetic control), `ManagedCandidateBvh.cs`, `EmbreeQueryBench.cs`,
`BakerQueryBench.cs`, `run-embree.ps1`, `summarize-compatible.py`.

Under `_results~/embree-compat-20261014/`:

- `native-1/`: rejected native port, strict gate fails.
- `candidates-1/`, `candidates-tight-1/`, `candidates-leaves-1/`: exact earlier
  search variants; broad/extra ancestor work costs too much.
- `candidates-filtered-1/`: first selective speed gain, exact outputs.
- `verified-{1,2,3}/queries.json`, `verified-summary.json`: final paired samples.
- `multi-band/queries.json`: all six final bake checks, plus the strict corpus.
- `verified-build/Release/`: final private library and pinned Embree runtimes.

The final private library SHA256 is
`5B8E9F13BFFD838A636A418572FA5FF79EE66B264CF82389F719E593FDBA3755`.
It uses the preceding pinned Embree 4.3.3 CPU distribution. Configure the hidden
tool CMake project with `EMBREE_ROOT`, then call `run-embree.ps1 -Backend candidates`
on a closed isolated Unity project containing the three fixtures. Add
`-BakeLayoutRoot <immutable paired models folder>` to verify the six bakes.
The runner checks every expected fixture/mode and removes its own three scripts.
Use `summarize-compatible.py` on the three final reports for the timing table.

No production DLL was changed; no full ordinary/Reverse transfer acceleration
or changed final UV atlas is claimed for this private adapter. A future typed
batch integration still needs workload selection, buffer reuse/lifecycle and
native runtime/license/platform packaging through the normal native CI process.
