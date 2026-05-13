# Transfer-to-LOD Quality — Debug Plan (next session)

> Companion to `EXPERIMENTS.md` (history of what was tried) and
> `TRANSFER_BENCHMARK.md` (how to measure). This file is the
> next-session checklist for diagnosing and fixing the
> "трансфер на лоды не очень хороший" issue.

## Why this is the next focus

After the PR that introduced the async pipeline, Pipeline UI, and Debug
toggles (`claude/refactor-uv-progress-jaKkx`), the user reports that
UV2 transfer onto target LODs produces noticeably worse results than
the source LOD pack. The pipeline itself is fast and responsive — the
shells just don't transfer cleanly.

Everything in this plan is read-only investigation first, then small
focused fixes one hypothesis at a time, each one verifiable through
`TRANSFER_BENCHMARK.md`.

## Inputs we already have

* `GroupedShellTransfer.TransferResult` (`Editor/GroupedShellTransfer.cs:78–119`)
  carries per-target diagnostic state — `shellsMatched`,
  `shellsUnmatched`, `shellsTransform`, `shellsInterpolation`,
  `shellsMerged`, `shellsRejected`, `shellsOverlapFixed`,
  `dedupConflicts`, `fragmentsMerged`, `consistencyCorrected`,
  `targetShellStatus[]` (Accepted/Degraded/Poor/Rejected/Unmatched),
  `targetShellIssues[]`, `topologyFixed`, `topologyCapHit`.
* `TransferValidator.TriIssue` flags (`Editor/TransferValidator.cs:14`):
  Inverted, Stretched, ZeroArea, OutOfBounds, Overlap, TexelDensity.
* `BenchmarkRecorder` already records all of the above per-mesh.
* Quality Report in the Transfer tab visualises them per-LOD.
* Validation Overlay in the Transfer tab can mask specific TriIssue
  bits onto the canvas.
* `UvShell` cross-LOD identity is established by
  `UvToolContext.ExtractGroupKey` (`Editor/Framework/UvToolContext.cs:258`)
  — strips trailing `LOD\d+` so meshes with matching base names are
  linked.

## Hypotheses, ranked by suspected impact

### H1. LOD simplification splits / merges UV0 shells

**Symptom**: target shell count differs from source; `fragmentsMerged > 0`
or many `Unmatched` shells.

**Cause**: meshoptimizer's seam-aware simplify is conservative around
edges but still can collapse a thin shell into 1–2 triangles or split
a long shell into fragments. The Phase 1a `MergeFragmentShells`
(`Editor/GroupedShellTransfer.cs:854`) handles fragments where target
bbox sits fully inside one source shell, but not the inverse case
(source split → multiple target fragments that each sit inside their
own piece of the source).

**Diagnostic**: log per-LOD source/target shell counts side-by-side;
look at `targetShellStatus[]` distribution for the worst LOD.

**Fix candidates**:
* Loosen the fragment-merge bbox-containment test to also fire when a
  target shell straddles a source-shell boundary with > N% overlap.
* Run the source-LOD weld pass (`Uv0Analyzer.WeldUv0`) before extract
  on target LODs too — currently each LOD welds independently and can
  produce slightly different topologies.

### H2. Shell matching by 3D centroid is ambiguous on LODs

**Symptom**: `dedupConflicts > 0` or `targetShellMethod[] == 2 (merged)`
on shells that should map 1:1.

**Cause**: simplification moves vertex positions, so 3D centroids
drift. Phase 2a (`Editor/GroupedShellTransfer.cs:1192`) picks the
nearest source shell by 3D centroid distance; when two shells are
close in 3D (e.g., the two halves of a symmetric prop after SymSplit),
the assignment can flip on LOD2/LOD3.

**Diagnostic**: print `targetShellMatchDistSqr[]` distribution;
visualise via Validation Overlay → Overlap mask.

**Fix candidates**:
* Add UV0-centroid distance as a tiebreaker when 3D centroid distances
  are within ε of each other.
* Use the cross-LOD `CrossLodMatchHint` more aggressively: today it's
  accumulated only across LODs in `LightmapTransferTool.cs:1614`; check
  it isn't being cleared inside Phase 2a's rescore step.

### H3. Per-vertex interpolation extrapolates near shell borders

**Symptom**: `TriIssue.OutOfBounds` triangles concentrated near shell
edges; `topologyFixed > 0`.

**Cause**: Phase 3 interpolation (clamped barycentric, currently the
default per `EXPERIMENTS.md` line 40) projects each target vertex into
the source-UV0 hull. A target vertex slightly outside the source hull
(common after LOD simplification rounds corners) gets clamped to the
nearest edge, dragging the projected UV2 onto the chart border. This
clamp pulls into adjacent shells' padding band.

**Diagnostic**: per-target-shell, count how many vertices required
clamp-to-edge; correlate with target-shell-status Degraded/Poor.

**Fix candidates**:
* Extend the projected UV2 by half a texel inward whenever the source
  triangle was border-adjacent — small bias to keep interpolated UVs
  off the chart edge.
* Switch border vertices to similarity-transform fallback when
  interp's clamp count is high (already exists per-shell in Phase 3,
  but threshold may be too lax).

### H4. Topology enforcer clamps too aggressively on LOD targets

**Symptom**: `topologyCapHit == true` or `topologyIterations` saturates
at the cap (`kMaxTopologyIterations`).

**Cause**: `EnforceShellTopologyOnUv2`
(`Editor/GroupedShellTransfer.cs:3621`) runs a Laplacian smooth pass
that re-anchors displaced verts. On LOD meshes with fewer vertices,
displacement detection ε may be set against the source mesh's vertex
spacing, not the target's — so target vertices read as "displaced"
when they're just sparser.

**Diagnostic**: report `topologyIterations` / `topologyFixed` per
target LOD; compare to source LOD.

**Fix candidates**:
* Scale the displacement ε by target mesh vertex density (use
  `targetMesh.vertexCount / total3DArea` as a proxy for average
  spacing).

### H5. ARAP / density normalisation on the source skews UV0 vs target UV0

**Symptom**: source UV0 and target UV0 used to be identical (modulo LOD
weld), but Repack's pre-pack ARAP modifies the in-memory source UV0;
target LODs still hold the raw artist UV0.

**Cause**: Repack pre-pack passes in `XatlasRepack.RepackMultiCore`
(`Editor/XatlasRepack.cs`) mutate `uvFlat` for ARAP and texel-density
normalisation. The result is written back to `e.repackedMesh.uv` (UV1
slot — fine). But Transfer uses `srcMesh.uv` (UV0) for matching, and
`srcMesh` IS `repackedMesh` per LOD source — so the target sees
post-ARAP UV0 while it still has artist UV0. The interp shell mapping
then has subtly different UV0 layouts.

**Diagnostic**: log mean L2 distance between source UV0 and target UV0
for each matched shell pair on a perfect identity test (LOD0 mapped
onto itself).

**Fix candidates**:
* Make Transfer read source UV0 from `srcEntry.originalMesh` instead
  of `srcEntry.repackedMesh ?? srcEntry.originalMesh` (currently
  `LightmapTransferTool.cs:1607` chooses the repacked mesh).
* OR: stop mutating UV0 in pre-pack and instead operate on a UV0 copy
  fed only to xatlas (UV2 result is what matters; UV0 should stay
  untouched).

## Diagnostic harness (build before fixing anything)

1. **Identity sanity test**: pick a LOD0 mesh, duplicate it as
   `Foo_LOD1`, run pipeline. Expect 100% match, 0 issues. Any
   deviation isolates a bug in the pipeline itself (not the LOD
   simplifier).

2. **Per-LOD ratio sweep**: sweep `generateLodRatios` ∈ {0.9, 0.75,
   0.5, 0.25, 0.1}; record `shellsMatched %`, `oobCount`,
   `topologyFixed`, `shellsRejected` for each ratio. Plot — find the
   ratio at which quality cliffs.

3. **Per-hypothesis log gate**: add a debug-only `bool LogTransferDiag`
   to Pipeline Settings → Log filters that, when on, prints:
   - source shell count, target shell count, fragments merged
   - dedup conflicts, target shell status histogram
   - mean clamp count per target shell
   - source/target UV0 L2 distance distribution

4. **Capture a known-bad case**: pick the worst LOD3 transfer the user
   has, save the FBX + sidecar as a regression test in
   `Tests/Editor/Assets~/` (gitignored bin) plus an expected-metrics
   range entry in `TestSuiteAsset`.

## Execution order for the next session

1. (read-only, 30 min) Run the identity sanity test. Log all `TransferResult`
   counters. If identity is NOT clean → fix that bug first; everything
   downstream is suspect.
2. (read-only, 30 min) Run the per-LOD ratio sweep on the user's
   carousel mesh and write findings into a new EXPERIMENTS.md entry.
3. Pick the strongest hypothesis from the data. Implement ONE fix
   (smallest one that addresses the dominant failure mode).
4. Re-run sweep, compare. If better — commit, document in
   EXPERIMENTS.md, move on. If worse / same — revert immediately, mark
   the hypothesis as ruled-out in EXPERIMENTS.md, pick the next.
5. Iterate.

## Guardrails

* **One experiment per PR** (per `AGENTS.md`). Each PR cites the
  EXPERIMENTS.md entry it implements and the baseline numbers it
  improves.
* **No async changes** in this work — the async pipeline is settled.
  If a fix requires touching `TransferCore`'s control flow, run the
  identity sanity test before and after on both sync and async paths.
* **Sweep before merging** — `TRANSFER_BENCHMARK.md` baseline run on
  the carousel suite, no regression on `oobCount` /
  `shellsRejected` / `coverage` vs the pre-PR baseline.
* **Roll back fast** — if a hypothesis-fix produces no measurable
  improvement, revert immediately and update EXPERIMENTS.md (Что НЕ
  работает section). Trying to "fix the fix" is how we got the
  5-rejected-PRs hole that EXPERIMENTS.md warns about.
