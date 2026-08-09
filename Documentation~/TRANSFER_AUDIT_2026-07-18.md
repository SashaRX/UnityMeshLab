# Transfer Pipeline Audit — 2026-07-18

> Multi-agent audit of BOTH transfer generations. 4 of 9 subsystem
> reviewers completed (classic core, classic repack, SymSplit,
> hierarchical-early) before the run hit the monthly spend limit; the
> adversarial **verification pass never ran**, so every finding below is
> reviewer-proposed, not confirmed — EXCEPT the ones marked ✅ VERIFIED,
> which were spot-checked directly against the code afterwards.
>
> Not audited (finders never ran): orchestration
> (`LightmapTransferTool.cs` full flow), native contract (`XatlasNative.cs`
> call sites), support math (`ArapParameterization`/`CoverageSplitSolver`/
> BVH numerical stability), docs-drift, and `HierarchicalRepack.cs`
> lines 2300-3750 (Stage E1/E2/**E3 just written**/F). Those remain open.

## Cross-cutting themes (present in BOTH pipelines)

These are the systemic classes — the "common problems" — not one-off bugs.

### T1. Silent zero-UV2 fallbacks reported as success ⚠️ the biggest theme
A failed/aborted/unplaceable shell keeps its allocated `uv2 = (0,0)`
(the atlas origin) and the calling code's only failure signal is
`uv2 == null`. A non-null but all-zero array passes as a valid result,
so the whole shell/LOD bakes into a single texel with no warning.
- `GroupedShellTransfer.cs:931` (+1039/1219/1425/1937) — **✅ VERIFIED**:
  `result.uv2` allocated at :897, every cancel checkpoint does
  `return result` with the zeroed array; caller checks only `uv2 == null`.
  Cancel mid-transfer silently ships a single-texel LOD.
- `GroupedShellTransfer.cs:1361` — unmatched/rejected shells keep
  `(0,0)`; `shellsUnmatched` never assigned (benchmark always reports 0).
- `GroupedShellTransfer.cs:4748` — legacy public `Transfer` overload
  returns all-zero result with no error.
- `XatlasRepack.cs:1723` — fully-orphaned shells collapse to `(0,0)`;
  the orphan branch counts no-op snaps as "fixed".
- `HierarchicalRepack.cs` BuildCascadedUv2 — same shape; the
  2026-07-18 commit at least now emits a Warn + counts `unplacedFaces`
  instead of dropping faces, but still parks them at `(0,0)`.

### T2. The same "deepest / proxy LOD" and "which renderer" computed 4-5 ways
- `HierarchicalRepack.cs` **✅ VERIFIED**: five `deepest`-pickers with
  **three different validity criteria** — `:569` uses the prebuilt
  `meshes[]` (first *valid* renderer, may be `rs[1]`), `:1649` and
  `:1921` test only `rs[0] == null`, `:2034` and `:2136` test
  `rs[0] + MeshFilter?.sharedMesh`. On a LODGroup whose `rs[0]` is null
  but `rs[1]` valid, `Build()`'s `meshDiag`/seed reference and Stage C/D's
  processed LOD disagree — the cascade seeds from a different LOD than the
  one that set the reference scale.
- Every stage independently re-picks `renderers[0]` and re-warns on
  multi-renderer LODs — multi-mesh-per-LOD is unsupported but the choice
  is made inconsistently across stages.
- Classic side has the analogue: source-mesh selection by `meshGroupKey`
  in the orchestrator vs. raw shell indices inside the transfer.

### T3. Absolute epsilons / thresholds that don't scale with mesh size
EXPERIMENTS.md limitation #3 ("пороги не масштабируются") is alive in
many concrete places:
- `GroupedShellTransfer.cs:367` — 3D match gate `max(10% diag, 0.03)`
  mixes an absolute world-unit floor with squared-vs-linear distances;
  breaks on meshes far from ~1m scale.
- `GroupedShellTransfer.cs:4150` — `CountShellIssues` uses absolute
  `1e-10` area epsilon and is blind to fold-overs → folded/collapsed
  candidates score 0 issues and are **Accepted**.
- `HierarchicalRepack.cs:776` — canonical-vertex weld uses absolute
  `1e-5` floor with no neighbour-cell probe → shell partitions become
  position- and scale-dependent (non-deterministic across placements).
- `HierarchicalRepack.cs:1802` — Stage-2 sample UV-bbox cap `0.35`
  (absolute) silently drops all samples on legitimately large charts.
- `SymmetrySplitShells.cs:861` — "adaptive" `posFar` degrades to a fixed
  `0.125` floor because `DetectFoldCount`/`HasUv0Overlap` are called with
  `mesh = null`, inconsistent with the binary stage.

### T4. Brute-force O(N²)/O(N³) with existing BVHs left unused
- `GroupedShellTransfer.cs:481` — **FindBestSourceShell** re-ranks & sorts
  ALL source shells every call, no cache (documented O(N³), still true).
- `GroupedShellTransfer.cs:2059` — Phase-3 face-voting pre-pass is
  O(targetFaces × Σ group source faces) with per-pair centroid+cross,
  despite `shellBvh3D` already existing. Freezes on Carousel-class groups.
- `SymmetrySplitShells.cs:766` — degenerate/constant-UV0 shell collapses
  the spatial hash to one bucket → O(F²) hang + bogus binary split.
- `SymmetrySplitShells.cs:1345` — `SplitWithParams` copies
  `mesh.vertices`/`normals` per prescribed param → O(P·S·V).
- `HierarchicalRepack.cs:1195` — Stage-3 projection is O(samples × faces)
  brute force with a 3-samples-per-face floor; freezes on 100k-face LODs.

### T5. Stale counts / struct-copy hazards after post-passes
- `GroupedShellTransfer.cs:3366` — post-transfer 20% AABB safety net
  re-projects merged-shell vertices per-vertex, contradicting the
  composite path's 2×/30%/50% allowances, with **no re-count** of
  `targetShellIssues` (classification at :3487 uses stale counts) and no
  composite-contract re-check. Yanks legitimately voted vertices back.
- `HierarchicalRepack.cs` E3 (just written) — `StageEMetrics` is a
  struct; mutations after `r.stageEMetrics[li] = m` are lost unless
  written back. **Needs review** (hier-late finder never ran).

## Classic-pipeline-specific

- **CRITICAL (proposed)** `SymmetrySplitShells.cs:385` — **✅ VERIFIED
  present**: the descriptor-distance *fallback* still hard-filters
  `p.sourceGroupId != 0 && descriptor.groupId != p.sourceGroupId`. The
  fallback exists to be lenient when the exact-signature match fails, but
  the groupId gate makes it drop prescribed splits whenever a target LOD's
  shell carries a different groupId than the source. Same gate on the
  primary path (:365) — so a groupId mismatch across LODs loses the split
  on both paths. Severity depends on how often groupId diverges per LOD.
- `GroupedShellTransfer.cs:387` — cross-LOD hints store only a raw
  `sourceShellIndex`, validated only against `srcShellCount`. With 2+ mesh
  entries per LOD, `accumulatedMatchHints` is cleared per target and
  `accumulatedOverlapHints` grows across all targets/LODs → target B gets
  target A's hints as indices into the wrong source mesh; hint-matched
  shells win dedup priority and evict correct claimants.
- `GroupedShellTransfer.cs:1987` — fragment-merged per-face-voting branch
  is gated on `srcShellOverlapMembers[chosenSrc] != null`, but
  `MergeFragmentShells` only merges sources with **zero** UV0-bbox overlap
  → the gate is always false for fragment-merged shells → the composite
  voting is dead code → near-tiling fragments get identical (duplicate)
  UV2. (The exact duplicate-lightmap defect the comments claim to prevent.)
- `XatlasRepack.cs:1495` — **✅ VERIFIED store**: `atlasWidth/Height` are
  set from `xatlasGetAtlasWidth/Height`; with `internalOversample = 4`
  (default per the 2026-05-13 experiment) these are the oversampled dims.
  If transfer tolerances scale as `pixels / min(atlasW,atlasH)`, the
  margins shrink by the oversample factor. **Confirm** the tolerance
  consumer to rate severity.
- `XatlasRepack.cs:782` — heuristic cost budget can refuse to pack
  realistic assets at default oversample and report it as "cancelled".
- `LightmapTransferTool.cs:2652` — `ctx.HasRepack = true` even when every
  mesh failed to repack; failed entries keep a stale `repackedMesh`.
- `TexelDensityNormalizer.cs:126` — coverage budget makes the density
  target absolute; the `[0.1, 10]` scale clamp destroys density uniformity
  on tiled or miniature UV0.
- `XatlasRepack.cs:543` — `PostPackDensityCorrection` is default-ON in the
  tool despite an "experimental, default false" contract; computes the
  shrink pivot over orphan+conflict verts → translates shells into
  neighbours.
- `SymmetrySplitShells.cs:641` — `ApplyBinarySplit`/`ApplyNFoldSplit`
  rebuild via `mesh.Clear()` and silently destroy blend shapes and
  >4-bone skin weights.
- `SymmetrySplitShells.cs:798` — binary split fires on a **single** vote,
  no vote-count/ratio gate (asymmetric with the N-fold gate).
- `UvShellExtractor.cs:319` — `BboxOverlapRatio` returns 0 for degenerate
  (zero-area/width) shell bboxes → stacked degenerate shells never join
  overlap groups.
- `XatlasRepack.cs:1517` — **✅ VERIFIED**: `chartCount = outVertCount`
  (mislabels vertex count as chart count).

## Hierarchical-cascade-specific

- `HierarchicalRepack.cs:2167` — a null/meshless middle LOD makes
  `CascadeGroupShells` `continue` past that transition, **breaking the
  cascade chain**: LODs above the gap can't match through it, so their
  domains disconnect from the deepest LOD's groups (the exact symptom —
  all-zero vs. disconnected-domains — is **unverified**; the chain break
  is real by construction).
- `HierarchicalRepack.cs:2157` — Stage D vote has **no normal/orientation
  gate** and its distance threshold scales with the whole-mesh diagonal →
  thin double-sided geometry (both faces within `overlayDistNorm×diag`)
  gets cross-side vote contamination. Front/back of a thin panel merge
  into one lighting domain.
- `HierarchicalRepack.cs:2206` — the min-3-samples-per-face floor makes
  the Stage D vote **face-count-weighted, not area-weighted**; a shell
  that is many tiny faces outvotes a shell that is few large faces.
- `HierarchicalRepack.cs:1626` — a UV0-less mesh produces the Auto proxy
  variant but the default `ProxyMode.Clean` never auto-selects it, so
  Stage 2/3 silently no-op (empty cascade, no error).
- `HierarchicalRepack.cs:1003` — `MergeAdjacentShells` adjacency misses
  shell pairs sharing an edge used by 3+ shells (non-manifold edges).

## Recommended triage order

1. **T1 silent-zero-UV2** — turn every `(0,0)` fallback into an explicit
   `success=false`/warn so nothing corrupt is ever applied. One shared
   result flag across both pipelines. Highest safety-per-effort.
2. **SymmetrySplitShells.cs:385/365 groupId gate** — confirm how often
   groupId diverges per-LOD; if ever, the fallback must not hard-filter.
3. **T2 deepest-picker unification** — one `PickDeepest(lods)` helper,
   one renderer-selection helper, called everywhere.
4. Run the **missing 4 finders + verification pass** when budget allows —
   orchestration error-recovery/leaks, native create/destroy pairing,
   PCA/NaN stability, and the freshly-written Stage E3 (never compiled).
