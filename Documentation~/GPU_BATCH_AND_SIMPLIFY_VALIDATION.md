# GPU batching and source refinement performance — 2026-10-06

The comparison uses Unity 6000.2.6f2, Windows DX11 and the same GTX 980 Ti as
the preceding GPU pipeline validation. It runs in the isolated URP replay
project; no meshes or settings in the user's editor are modified.

## GPU band comparison

The existing textured bust replay retains identical geometry, UVs, normals,
tangents, source materials and settings for all cases: 1997 target triangles,
1024 maps, 9 samples per texel, atlas padding 3, dilation radius 64, fitted cage,
source normal maps, vertex colors and alpha. Source AO is disabled here and
covered separately by the streaming tests.

Three interleaved runs of each query limit:

| Query limit | Bake times (ms) | Mean (ms) | Query bands | Projection dispatches |
| --- | --- | --- | --- | --- |
| 65536 | 9225.89, 9015.93, 9070.48 | 9104.10 | 157 | 221 |
| 131072 | 8820.07, 8873.18, 8870.32 | 8854.52 | 77 | 121 |
| 262144 | 5161.97, 5211.83, 6569.59 | 5647.80 | 38 | 70 |

256k lowers mean bake time by 38.0% (1.61x) in this comparison. This is not a
comparison with the user's latest 1978-triangle bake. All nine `maps.bin`
files are byte-identical: SHA256
`0c2b02791532828087d224e2713d050f9ded951e4a5da1c1b58d5326bc7b0265`.
The files contain all four Color32 maps, float emission and float vertex colors.

Dispatch-to-readback callback latency sums to about 0.39–0.45 seconds per bake,
including CPU result copies and Editor callback scheduling. It does not measure
GPU kernel execution. Reducing band count also amortizes CPU evaluation and
Editor task handoffs; increasing GPU utilization alone is not the identified
bottleneck in this replay.

256k needs 28 MiB of reusable GPU query buffers instead of 7 MiB. The two CPU
query bands also grow. Devices reporting at most 1 GiB or unknown GPU memory
retain the previous 64k default. Legacy ray/nearest async methods retain 64k.

## Simplify comparison on the latest user capture

Input: `remesh_20261006_000926_494_672d82.bin`, SHA256
`f2f7211c7a802a3726c15aa55b27ebf840f48975686d60a64f0c78b54298869d`.
The retained source has 30288 triangles and the trimmed voxel mesh 210626.
Settings are read unchanged from the capture. Two repeats per implementation:

| Implementation | Times (ms) | Mean (ms) |
| --- | --- | --- |
| Original queries, timing instrumentation only | 100420.7840, 105996.6585 | 103208.7213 |
| Parallel distance probes | 66975.3303, 72918.8231 | 69947.0767 |
| Parallel probes plus exact-position nearest reuse | 61043.7533, 61166.9149 | 61105.3341 |

The final change reduces mean Simplify time by 40.8% (1.69x). All six serialized
outputs — positions, indices and native collapse error — are byte-identical,
SHA256 `456d1e1e60e744be645da1360e5f20703f25564eb48c2c428c12f67fbae8d3bb`.
The output remains 1978 triangles. Rejected candidates and accepted motion
scales retain the same decisions.

Before optimization, pre-refinement takes 84–90 seconds; each native collapse
takes about 0.2–0.3 seconds. In the optimized dense fitting pass, 337164 vertex
nearest queries execute and 1227021 exact-position answers are reused. Queries
for projected seed positions remain independent. Initial dense edge-flip
passes still take about 10.4 seconds; backtracks repeat that dependent work.
These triangulation decisions mutate neighbouring patches in order and were
not parallelized, because that would change the algorithm's decisions.

Distance queries use bounded windows of 8192 faces, with every original probe
retained. Reduction still visits each face and probe in original order. The
source BVH is immutable during parallel reads. The vertex nearest cache is
local to fitting, uses exact Vector3.Equals, and is invalidated by any position
change. The source, vertex normals and reach remain fixed for these queries.

## Validation artifacts

Scratch artifacts live under `_results~/gpu-batch/`: `bench/`,
`simplify-before/`, `simplify-after/`, `simplify-final/` and `tests/`.
`SimplifyPerfReplay.cs` replays the captured input; `GpuBatchReplay.cs`
compares band limits. These replay drivers are outside the distributed package.

The final unmodified default batch policy ran three more bakes in 5921.2430,
5217.2156 and 5259.5595 ms (mean 5466.0060 ms). Every map file retains the same
SHA256 as the nine comparison runs; the serialized target remains identical.
Readback callback latency is 447–450 ms; CPU-worker completion-to-resume spans
are 426–820 ms and can overlap GPU work. Continuation delays are workload and
Editor scheduling dependent, not certified GPU timings.

- 237 EditMode tests pass on DX11, zero failed or skipped, covering bake,
  projection, source normal scales, surface padding, health, source refinement
  and saved window preferences.
- The async-query, dense streaming, GPU source AO and cancellation/drain tests
  each pass on Windows DX12, OpenGL Core and Vulkan (4 per backend, zero failed
  or skipped). The dense fixtures use 256px maps to cross multiple enlarged
  query bands rather than accidentally exercising only one band.
- The distance-reduction oracle checks exact double results against the original
  serial algorithm across the 8192-face window, both probe modes and two scales,
  including cancellation. Repeated complete captured Simplify outputs are exact.
- Licence-free C# compilation succeeds with and without the FBX define.
  The tool dependency guard reports 9 declarations and zero violations.

Windows graphics API results do not establish physical Android runtime or
performance. No shader/native implementation changed in this patch.
